// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Radio;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Pipelines;

/// <summary>A listener that belongs to a lease (a lease listens on every side it reads).</summary>
internal interface ILeaseListener
{
    LeasePipeline Lease { get; }
}

/// <summary>
/// One lease: NCO to baseband, decimate by D, sc16 blocks of at most 16,384 samples and 20 ms
/// (section 9.3), on its own thread reading its own ring position.
/// <para>A BLOCK IS CONTIGUOUS IN TIME AND IN CENTRE. Anything that breaks the stream - a lap by
/// the radio, the retune boundary of a radio move, a lease retune - first flushes the partial
/// block, and the change is stated on the NEXT block: its first_sample jumps by exactly the output
/// samples missed (dropped, discontinuity) and its header carries the centre its samples were
/// taken at, captured when the block started - never read at emit time.</para>
/// <para><b>A DUAL lease</b> (ShackLink docs/usrp_b210_support.md section 9.23 Q4) is this pipeline over BOTH
/// sides: the two rings are written in lockstep, so one read position is one instant on both; each side
/// has its own NCO (the lease centre against THAT side's centre) and its own decimator, and the blocks
/// carry both as two-channel sc16 (<see cref="IqFormat.Sc16Dual"/>) of at most 8,192 instants. A lap or a
/// change on either side flushes and skips BOTH, so the halves of an instant never come from two moments.</para>
/// <para><b>Its origin</b> (Q4) is the radio position of sample 0, fixed when the server asks
/// (<see cref="ChooseOrigin"/>, before <see cref="Start"/>): output sample k was taken at radio position
/// <c>origin + k x D</c>, through every drop (a drop advances first_sample by exactly the samples missed).
/// And its NCO's phase is a function of the radio position alone (anchored before every chunk), so two leases
/// at one centre agree in PHASE at one position too - the price is that a retune's phase is not continuous.</para>
/// </summary>
internal sealed class LeasePipeline : ILeasePipeline, ILeaseOrigin
{
    private const int ChunkMax = 8192;

    private readonly IRadioContext[] _ctx;
    private readonly Action<LeasePipeline> _removed;
    private readonly object _pendingLock = new();
    private readonly long[] _lo;
    private readonly SideListener[] _listeners;
    private Thread? _thread;
    private volatile bool _stop;
    private long _center;
    private bool _changePending;
    private bool _radioMoved;
    private bool _reconfigured;
    private long _skipTo = -1;
    private long _origin = -1;

    public long CenterHz => Interlocked.Read(ref _center);
    public int Decimation { get; }
    public double RateHz { get; }

    /// <summary>The sides it reads, in wire order: one, or both for a dual lease.</summary>
    public string[] Sides { get; }
    public bool IsDual => _ctx.Length > 1;

    /// <summary>One listener per side, for the backend to register on that side and remove with the lease.</summary>
    public IReadOnlyList<IRadioListener> SideListeners => _listeners;

    /// <summary>Blocks posted and output samples produced - telemetry.</summary>
    public long BlocksPosted;
    public long SamplesProduced;
    public long WideSamplesMissed;

    public LeasePipeline(IRadioContext ctx, long centerHz, int decimation, Action<LeasePipeline> removed)
        : this(new[] { ctx }, centerHz, decimation, removed) { }

    public LeasePipeline(IRadioContext[] sides, long centerHz, int decimation, Action<LeasePipeline> removed)
    {
        if (sides == null || sides.Length < 1 || sides.Length > 2) throw new ArgumentException("a lease reads one side or both", nameof(sides));
        _ctx = sides;
        _removed = removed;
        _center = centerHz;
        _lo = sides.Select(c => c.LoHz).ToArray();
        _listeners = Enumerable.Range(0, sides.Length).Select(i => new SideListener(this, i)).ToArray();
        Sides = sides.Select(c => c.Side).ToArray();
        Decimation = decimation;
        RateHz = sides[0].WideRateHz / decimation;
    }

    /// <summary>Where this lease's samples start: fixed on the first call (the server's, before Start), never
    /// earlier than the last change any of its sides announced (A2 F1) - the rings are in lockstep, so the
    /// latest of their positions is one position for all of them.</summary>
    public long ChooseOrigin()
    {
        lock (_pendingLock)
        {
            if (_origin < 0)
                _origin = _ctx.Max(c => Math.Max(c.Ring.Written, c.SettledFrom));
            return _origin;
        }
    }

    public void Start(uint streamId, IFrameSink sink)
    {
        ChooseOrigin();
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

    /// <summary>A WIDE lease (decimation 1, one side - ShackLink docs/plutosdr_support.md section 9.6): the side's
    /// whole stream, centred on the side. It follows every move of that centre, and its samples are the ring's own.</summary>
    public bool IsWide => Decimation == 1 && _ctx.Length == 1;

    private void OnRadioRetuned(int side, long loHz, long position)
    {
        lock (_pendingLock)
        {
            if (IsWide) Interlocked.Exchange(ref _center, loHz);
            _lo[side] = loHz;
            _changePending = true;
            _radioMoved = true;
            if (position > _skipTo) _skipTo = position;
        }
    }

    private void OnRadioReconfigured(long position)
    {
        lock (_pendingLock)
        {
            _changePending = true;
            _reconfigured = true;
            if (position > _skipTo) _skipTo = position;
        }
    }

    /// <summary>One side's view of the lease: which side a change came from is the one thing a shared
    /// listener could not tell.</summary>
    private sealed class SideListener : IRadioListener, ILeaseListener
    {
        private readonly LeasePipeline _lease;
        private readonly int _side;

        public SideListener(LeasePipeline lease, int side)
        {
            _lease = lease;
            _side = side;
        }

        public LeasePipeline Lease => _lease;

        public void OnRadioRetuned(long loHz, long position) => _lease.OnRadioRetuned(_side, loHz, position);

        // The server ends every lease BEFORE a rate change (section 9.4), so a lease never re-plans.
        public void OnRateChanged(double wideRateHz, long position) { }

        // A gain, AGC, antenna, bandwidth or setting change reaches a lease as what it is: a level step in
        // a continuous stream. Nothing in a lease is integrated across it, and an IQ block states no gain,
        // so no sample can be mislabelled by it - while skipping the settle would cut a hole into the one
        // product that promises continuity (a demodulator copes with a step, not with a gap). So a lease is
        // not told (ShackLink docs/usrp_b210_support.md 9.15); the event stream's state says the gain moved.
        public void OnFrontEndChanged(long position) { }

        public void OnRadioReconfigured(long position) => _lease.OnRadioReconfigured(position);
    }

    public void Dispose()
    {
        _stop = true;
        foreach (var c in _ctx) c.Ring.WakeAll();
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
            _ctx[0].Log($"lease {streamId}: stopped by {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void RunCore(uint streamId, IFrameSink sink)
    {
        int n = _ctx.Length;
        var rings = _ctx.Select(c => c.Ring).ToArray();
        double fs = _ctx[0].WideRateHz;
        var ncos = new Nco[n];
        var firs = new DecimatingFir[n];
        for (int s = 0; s < n; s++)
        {
            ncos[s] = new Nco();
            firs[s] = new DecimatingFir(Decimation);
        }
        int chunk = Math.Max(Decimation, ChunkMax / Decimation * Decimation);
        chunk = Math.Min(chunk, rings[0].Capacity / 4);
        var raw = new short[chunk * 2];
        var re = new float[n][];
        var im = new float[n][];
        var outRe = new float[n][];
        var outIm = new float[n][];
        for (int s = 0; s < n; s++)
        {
            re[s] = new float[chunk];
            im[s] = new float[chunk];
            outRe[s] = new float[chunk / Decimation + 2];
            outIm[s] = new float[chunk / Decimation + 2];
        }
        var format = n > 1 ? IqFormat.Sc16Dual : IqFormat.Sc16;
        int maxBlock = (int)Math.Clamp(Math.Floor(RateHz * ProtocolConstants.MaxIqBlockSeconds), 1, IqHeader.MaxSamples(format));
        var block = new short[maxBlock * 2 * n];

        int fill = 0;
        ulong outIndex = 0;              // this lease's own output-sample count
        ulong blockFirst = 0;
        long blockCenter = 0;
        IqFlags blockFlags = IqFlags.None;
        uint blockDropped = 0;
        IqFlags nextFlags = IqFlags.None;
        long droppedWide = 0;            // wide samples missed, not yet charged to a block
        long lastOverflows = _ctx[0].Overflows;
        // The origin was fixed never earlier than the last change the radio announced: a lease opened inside a
        // retune's settle (ShackLink's radio slot closes, moves and reopens its own channel 0 around one) would
        // otherwise mix samples still in flight at the old LO with the new LO's shift (CRTU200's A2 F1).
        long pos = ChooseOrigin();
        long ncoCenter = long.MinValue;
        var ncoLo = Enumerable.Repeat(long.MinValue, n).ToArray();
        bool ok = true;

        bool Flush()
        {
            long ov = _ctx[0].Overflows;
            ok = Emit(streamId, sink, format, block, fill, blockFirst, blockCenter, blockFlags, blockDropped,
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
                    if (_radioMoved) changeFlags |= IqFlags.Retuned | IqFlags.RadioReconfigured;
                    else if (!_reconfigured) changeFlags |= IqFlags.Retuned;
                    // The other side switched, or the RF LO was placed elsewhere: the centre did not move, and
                    // time between the samples on either side of it may be missing.
                    if (_reconfigured) changeFlags |= IqFlags.RadioReconfigured | IqFlags.Discontinuity;
                    _radioMoved = false;
                    _reconfigured = false;
                    if (_skipTo > pos) skip = _skipTo - pos;
                    _skipTo = -1;
                }
                for (int s = 0; s < n; s++)
                {
                    if (CenterHz == ncoCenter && _lo[s] == ncoLo[s]) continue;
                    // Samples already in the partial block were mixed with the OLD shift; they are
                    // flushed below under the centre captured when their block began.
                    ncos[s].Set(CenterHz - _lo[s], fs);
                    ncoLo[s] = _lo[s];
                }
                ncoCenter = CenterHz;
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

            bool ready = true;
            foreach (var ring in rings)
                if (!ring.WaitFor(pos + chunk, 100)) { ready = false; break; }
            if (!ready) continue;

            // A wide lease on its side's centre shifts nothing and decimates by 1: its samples are the ring's own,
            // bit for bit - no NCO, no filter, no float round trip (which would cost a 32767/32768 gain and LSBs).
            bool passthrough = IsWide && ncoCenter == ncoLo[0];
            bool lapped = false;
            for (int s = 0; s < n; s++)
            {
                if (!rings[s].TryRead(pos, chunk, raw)) { lapped = true; break; }
                if (passthrough) continue;
                // The phase is the radio position's, not this lease's history (Nco.AnchorAt): every chunk.
                ncos[s].AnchorAt(pos);
                ncos[s].Mix(raw, chunk, re[s], im[s]);
            }
            if (lapped)
            {
                if (fill > 0 && !Flush()) return;
                // Every side resumes at the SAME position: the halves of an instant are never taken apart.
                long resume = rings.Min(r => r.Written) - chunk;
                if (resume > pos)
                {
                    droppedWide += resume - pos;
                    Interlocked.Add(ref WideSamplesMissed, resume - pos);
                    pos = resume;
                }
                continue;
            }
            pos += chunk;

            int produced = passthrough ? chunk : 0;
            for (int s = 0; s < n && !passthrough; s++)
            {
                int got = firs[s].Process(re[s], im[s], chunk, outRe[s], outIm[s], 0);
                if (s == 0) produced = got;
                else if (got != produced) throw new InvalidOperationException("the two sides' decimators disagree on their output count");
            }
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
                int o = fill * 2 * n;
                if (passthrough)
                {
                    block[o] = raw[2 * i];
                    block[o + 1] = raw[2 * i + 1];
                }
                else
                {
                    for (int s = 0; s < n; s++)
                    {
                        block[o + 2 * s] = ToSc16(outRe[s][i]);
                        block[o + 2 * s + 1] = ToSc16(outIm[s][i]);
                    }
                }
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

    private bool Emit(uint streamId, IFrameSink sink, IqFormat format, short[] block, int count, ulong first, long center,
                      IqFlags flags, uint dropped, uint overflows)
    {
        int bytes = count * IqHeader.BytesPerSample(format);
        var frame = FrameIO.NewFrame(FrameType.IqBlock, streamId, ProtocolConstants.IqHeaderSize + bytes);
        new IqHeader
        {
            FirstSample = first,
            CenterHz = center,
            RateHz = RateHz,
            Count = count,
            Format = format,
            Flags = flags,
            Dropped = dropped,
            Overflows = overflows,
            TimeTicks = SidecarClock.Ticks,
        }.Write(frame, ProtocolConstants.FrameHeaderSize);
        // The wire is little-endian and so is every host this runs on (x64 Windows, x64/arm64 Linux).
        Buffer.BlockCopy(block, 0, frame, ProtocolConstants.FrameHeaderSize + ProtocolConstants.IqHeaderSize, bytes);
        Interlocked.Increment(ref BlocksPosted);
        return sink.Post(frame);
    }
}
