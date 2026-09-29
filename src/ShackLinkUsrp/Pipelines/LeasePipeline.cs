// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Radio;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Pipelines;

/// <summary>
/// One lease: NCO to baseband, decimate by D, sc16 blocks of at most 16,384 samples and 20 ms
/// (section 9.3), on its own thread reading its own ring position.
/// <para>A BLOCK IS CONTIGUOUS IN TIME AND IN CENTRE. Anything that breaks the stream - a lap by
/// the radio, the retune boundary of a radio move, a lease retune - first flushes the partial
/// block, and the change is stated on the NEXT block: its first_sample jumps by exactly the output
/// samples missed (dropped, discontinuity) and its header carries the centre its samples were
/// taken at, captured when the block started - never read at emit time.</para>
/// </summary>
internal sealed class LeasePipeline : ILeasePipeline, IRadioListener
{
    private const int ChunkMax = 8192;

    private readonly IRadioContext _ctx;
    private readonly Action<LeasePipeline> _removed;
    private readonly object _pendingLock = new();
    private Thread? _thread;
    private volatile bool _stop;
    private long _center;
    private long _lo;
    private bool _changePending;
    private bool _radioMoved;
    private long _skipTo = -1;

    public long CenterHz => Interlocked.Read(ref _center);
    public int Decimation { get; }
    public double RateHz { get; }

    /// <summary>Blocks posted and output samples produced - telemetry.</summary>
    public long BlocksPosted;
    public long SamplesProduced;
    public long WideSamplesMissed;

    public LeasePipeline(IRadioContext ctx, long centerHz, int decimation, Action<LeasePipeline> removed)
    {
        _ctx = ctx;
        _removed = removed;
        _center = centerHz;
        _lo = ctx.LoHz;
        Decimation = decimation;
        RateHz = ctx.WideRateHz / decimation;
    }

    public void Start(uint streamId, IFrameSink sink)
    {
        _thread = new Thread(() => Run(streamId, sink)) { IsBackground = true, Name = $"Lease-{streamId}" };
        _thread.Priority = ThreadPriority.AboveNormal;
        _thread.Start();
    }

    public long Retune(long centerHz)
    {
        lock (_pendingLock)
        {
            Interlocked.Exchange(ref _center, centerHz);
            _changePending = true;
        }
        return centerHz;
    }

    public void OnRadioRetuned(long loHz, long position)
    {
        lock (_pendingLock)
        {
            _lo = loHz;
            _changePending = true;
            _radioMoved = true;
            _skipTo = position;
        }
    }

    // The server ends every lease BEFORE a rate change (section 9.4), so a lease never re-plans.
    public void OnRateChanged(double wideRateHz, long position) { }

    public void Dispose()
    {
        _stop = true;
        _ctx.Ring.WakeAll();
        if (_thread != null && Thread.CurrentThread != _thread) _thread.Join(2000);
        _removed(this);
    }

    private void Run(uint streamId, IFrameSink sink)
    {
        try
        {
            RunCore(streamId, sink);
        }
        catch (Exception ex)
        {
            _ctx.Log($"lease {streamId}: stopped by {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void RunCore(uint streamId, IFrameSink sink)
    {
        var ring = _ctx.Ring;
        double fs = _ctx.WideRateHz;
        var nco = new Nco();
        var fir = new DecimatingFir(Decimation);
        int chunk = Math.Max(Decimation, ChunkMax / Decimation * Decimation);
        chunk = Math.Min(chunk, ring.Capacity / 4);
        var raw = new short[chunk * 2];
        var re = new float[chunk];
        var im = new float[chunk];
        var outRe = new float[chunk / Decimation + 2];
        var outIm = new float[chunk / Decimation + 2];
        int maxBlock = (int)Math.Clamp(Math.Floor(RateHz * ProtocolConstants.MaxIqBlockSeconds), 1, ProtocolConstants.MaxIqBlockSamples);
        var block = new short[maxBlock * 2];

        int fill = 0;
        ulong outIndex = 0;              // this lease's own output-sample count
        ulong blockFirst = 0;
        long blockCenter = 0;
        IqFlags blockFlags = IqFlags.None;
        uint blockDropped = 0;
        IqFlags nextFlags = IqFlags.None;
        long droppedWide = 0;            // wide samples missed, not yet charged to a block
        long lastOverflows = _ctx.Overflows;
        long pos = ring.Written;
        long ncoCenter = long.MinValue, ncoLo = long.MinValue;
        bool ok = true;

        bool Flush()
        {
            long ov = _ctx.Overflows;
            ok = Emit(streamId, sink, block, fill, blockFirst, blockCenter, blockFlags, blockDropped,
                      (uint)Math.Clamp(ov - lastOverflows, 0, uint.MaxValue));
            lastOverflows = ov;
            fill = 0;
            return ok;
        }

        while (!_stop)
        {
            bool changed = false;
            long skip = 0;
            IqFlags changeFlags = IqFlags.None;
            lock (_pendingLock)
            {
                if (_changePending)
                {
                    _changePending = false;
                    changed = true;
                    changeFlags = IqFlags.Retuned | (_radioMoved ? IqFlags.RadioReconfigured : IqFlags.None);
                    _radioMoved = false;
                    if (_skipTo > pos) skip = _skipTo - pos;
                    _skipTo = -1;
                }
                if (CenterHz != ncoCenter || _lo != ncoLo)
                {
                    // Samples already in the partial block were mixed with the OLD shift; they are
                    // flushed below under the centre captured when their block began.
                    nco.Set(CenterHz - _lo, fs);
                    ncoCenter = CenterHz;
                    ncoLo = _lo;
                }
            }
            if (changed)
            {
                if (fill > 0 && !Flush()) return;
                nextFlags |= changeFlags;
                // Samples before a radio move's boundary were taken at the old LO: they are
                // missing from THIS lease's point of view, and are charged as such.
                if (skip > 0)
                {
                    droppedWide += skip;
                    pos += skip;
                }
            }

            if (!ring.WaitFor(pos + chunk, 100)) continue;
            if (!ring.TryRead(pos, chunk, raw))
            {
                if (fill > 0 && !Flush()) return;
                long resume = ring.Written - chunk;
                if (resume > pos)
                {
                    droppedWide += resume - pos;
                    Interlocked.Add(ref WideSamplesMissed, resume - pos);
                    pos = resume;
                }
                continue;
            }
            pos += chunk;

            nco.Mix(raw, chunk, re, im);
            int produced = fir.Process(re, im, chunk, outRe, outIm, 0);
            for (int i = 0; i < produced; i++)
            {
                if (fill == 0)
                {
                    blockFlags = nextFlags;
                    nextFlags = IqFlags.None;
                    blockDropped = 0;
                    if (droppedWide >= Decimation)
                    {
                        long missed = droppedWide / Decimation;
                        blockDropped = (uint)Math.Min(missed, uint.MaxValue);
                        outIndex += (ulong)missed;
                        droppedWide %= Decimation;
                        blockFlags |= IqFlags.Discontinuity;
                    }
                    blockFirst = outIndex;
                    blockCenter = ncoCenter;
                }
                block[2 * fill] = ToSc16(outRe[i]);
                block[2 * fill + 1] = ToSc16(outIm[i]);
                fill++;
                outIndex++;
                if (fill == maxBlock && !Flush()) return;
            }
            Interlocked.Add(ref SamplesProduced, produced);
        }
    }

    private static short ToSc16(float v)
    {
        float s = v * 32767f;
        if (s >= 32767f) return 32767;
        if (s <= -32768f) return -32768;
        return (short)MathF.Round(s);
    }

    private bool Emit(uint streamId, IFrameSink sink, short[] block, int count, ulong first, long center,
                      IqFlags flags, uint dropped, uint overflows)
    {
        var frame = FrameIO.NewFrame(FrameType.IqBlock, streamId, ProtocolConstants.IqHeaderSize + count * 4);
        new IqHeader
        {
            FirstSample = first,
            CenterHz = center,
            RateHz = RateHz,
            Count = count,
            Format = IqFormat.Sc16,
            Flags = flags,
            Dropped = dropped,
            Overflows = overflows,
            TimeTicks = SidecarClock.Ticks,
        }.Write(frame, ProtocolConstants.FrameHeaderSize);
        // The wire is little-endian and so is every host this runs on (x64 Windows, x64/arm64 Linux).
        Buffer.BlockCopy(block, 0, frame, ProtocolConstants.FrameHeaderSize + ProtocolConstants.IqHeaderSize, count * 4);
        Interlocked.Increment(ref BlocksPosted);
        return sink.Post(frame);
    }
}
