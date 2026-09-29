// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Radio;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Pipelines;

/// <summary>
/// The FFT loop shared by a push panorama and a pull (sweep) accumulator: reads non-overlapping
/// frames off the ring, integrates them, and follows the radio through retunes and rate changes.
/// Integration is honest about cost: a worker that cannot keep up is LAPPED by the ring, counts the
/// wide samples it missed into the next row's <c>dropped</c>, and integrates fewer FFTs (the row's
/// <c>ffts</c> says how many) - it never slows the radio. "sample" integration computes only the
/// last frame before each row, which is what makes it the cheap mode.
/// <para>The accumulator and the geometry it was built for are ONE pair, guarded by
/// <see cref="AccLock"/>: a pull take on another thread reads them together or not at all.</para>
/// </summary>
internal abstract class SpectrumWorker : IRadioListener, IDisposable
{
    protected readonly IRadioContext Ctx;
    protected readonly object AccLock = new();
    private readonly object _pendingLock = new();
    private Thread? _thread;
    protected volatile bool Stopping;
    private PanoramaRequest _request;
    private PanoramaGeometry _latest;
    private bool _regeometry;
    private long _skipTo = -1;

    /// <summary>Guarded by <see cref="AccLock"/>.</summary>
    protected SpectrumAccumulator Acc;
    /// <summary>The geometry <see cref="Acc"/> was built for; guarded by <see cref="AccLock"/>.</summary>
    protected PanoramaGeometry AccGeometry;

    protected SpectrumWorker(IRadioContext ctx, PanoramaRequest request)
    {
        Ctx = ctx;
        _request = request;
        _latest = PanoramaGeometry.Compute(request, ctx.LoHz, ctx.WideRateHz, ctx.MaxFftSize);
        AccGeometry = _latest;
        Acc = new SpectrumAccumulator(_latest.FftSize, _latest.Window, _latest.Integration);
    }

    /// <summary>The geometry the worker is producing (or about to).</summary>
    public PanoramaGeometry Geometry
    {
        get { lock (_pendingLock) return _latest; }
    }

    public PanoramaRequest Request
    {
        get { lock (_pendingLock) return _request; }
    }

    protected void StartThread(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>A new geometry, applied before the next frame; returns what it will be.</summary>
    public PanoramaGeometry Reconfigure(PanoramaRequest request)
    {
        var g = PanoramaGeometry.Compute(request, Ctx.LoHz, Ctx.WideRateHz, Ctx.MaxFftSize);
        lock (_pendingLock)
        {
            _request = request;
            _latest = g;
            _regeometry = true;
        }
        return g;
    }

    public void OnRadioRetuned(long loHz, long position) => Resync(position);

    public void OnRateChanged(double wideRateHz, long position) => Resync(position);

    private void Resync(long position)
    {
        lock (_pendingLock)
        {
            try
            {
                _latest = PanoramaGeometry.Compute(_request, Ctx.LoHz, Ctx.WideRateHz, Ctx.MaxFftSize);
            }
            catch (RadioRefusal ex)
            {
                // The request no longer fits the radio (a smaller wide rate): keep the old FFT and
                // say so; a Reconfigure can fix it.
                Ctx.Log("panorama geometry kept after a radio change: " + ex.Message);
            }
            _regeometry = true;
            _skipTo = position;
        }
    }

    /// <summary>Called on the worker thread after each frame is integrated. Return true when the
    /// <paramref name="droppedWide"/> count has been charged to a row (the worker then zeroes it).</summary>
    protected abstract bool OnFrameIntegrated(long position, long droppedWide);

    /// <summary>A push worker in "sample" mode jumps to the last frame before this position.</summary>
    protected virtual long NextRowPosition(long position) => long.MaxValue;

    /// <summary>True = do not compute now (a pull worker nobody has read for a while).</summary>
    protected virtual bool Paused => false;

    private void Run()
    {
        try
        {
            RunCore();
        }
        catch (Exception ex)
        {
            Ctx.Log($"{Thread.CurrentThread.Name}: stopped by {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void RunCore()
    {
        var ring = Ctx.Ring;
        PanoramaGeometry g;
        lock (AccLock) g = AccGeometry;
        var raw = new short[g.FftSize * 2];
        long pos = ring.Written;
        long droppedWide = 0;

        while (!Stopping)
        {
            if (Paused)
            {
                Thread.Sleep(100);
                pos = ring.Written;
                droppedWide = 0;
                lock (AccLock) Acc.Reset();
                continue;
            }

            PanoramaGeometry? changed = null;
            lock (_pendingLock)
            {
                if (_regeometry)
                {
                    _regeometry = false;
                    changed = _latest;
                    if (_skipTo > pos) pos = _skipTo;
                    _skipTo = -1;
                }
            }
            if (changed != null)
            {
                lock (AccLock)
                {
                    if (Acc.FftSize != changed.FftSize || Acc.Window.Shape != changed.Window || Acc.Integration != changed.Integration)
                        Acc = new SpectrumAccumulator(changed.FftSize, changed.Window, changed.Integration);
                    else
                        Acc.Reset();
                    AccGeometry = changed;
                }
                g = changed;
                if (raw.Length != g.FftSize * 2) raw = new short[g.FftSize * 2];
                droppedWide = 0;
            }

            if (g.Integration == Integration.Sample)
            {
                // Only the last frame before the row matters: jump there instead of computing.
                long due = NextRowPosition(pos);
                if (due != long.MaxValue && pos < due - g.FftSize) pos = due - g.FftSize;
            }

            if (!ring.WaitFor(pos + g.FftSize, 100)) continue;
            if (!ring.TryRead(pos, g.FftSize, raw))
            {
                long resume = ring.Written - g.FftSize;
                if (resume > pos)
                {
                    droppedWide += resume - pos;
                    pos = resume;
                }
                continue;
            }
            pos += g.FftSize;
            lock (AccLock) Acc.AddFrame(raw);
            if (OnFrameIntegrated(pos, droppedWide)) droppedWide = 0;
        }
    }

    public virtual void Dispose()
    {
        Stopping = true;
        Ctx.Ring.WakeAll();
        lock (AccLock) Monitor.PulseAll(AccLock);
        if (_thread != null && Thread.CurrentThread != _thread) _thread.Join(2000);
    }

    protected RowHeader MakeHeader(ulong seq, PanoramaGeometry g, uint ffts, uint overflows, long droppedWide) => new()
    {
        Seq = seq,
        CenterHz = g.CenterHz,
        SpanHz = g.SpanHz,
        Bins = g.Bins,
        FftSize = g.FftSize,
        Window = (WindowKind)(int)g.Window,
        Integration = (IntegrationKind)(int)g.Integration,
        Ffts = ffts,
        EnbwBins = (float)g.EnbwBins,
        GainDb = Ctx.RowGainDb,
        Overflows = overflows,
        Dropped = (uint)Math.Clamp(droppedWide, 0, uint.MaxValue),
        TimeTicks = SidecarClock.Ticks,
    };
}

/// <summary>A push panorama (open_panorama): one row every 1/fps seconds of SIGNAL - counted in
/// samples, not wall time, so rows stay aligned to the data when the host stutters.</summary>
internal sealed class PanoramaPipeline : SpectrumWorker, IPanoramaPipeline
{
    private readonly Action<PanoramaPipeline> _removed;
    private IFrameSink? _sink;
    private uint _streamId;
    private ulong _seq;
    private long _nextRowAt = -1;
    private long _lastOverflows;
    private short[] _row = Array.Empty<short>();
    public long RowsPosted;

    public PanoramaPipeline(IRadioContext ctx, PanoramaRequest request, Action<PanoramaPipeline> removed) : base(ctx, request)
    {
        _removed = removed;
    }

    PanoramaGrant IPanoramaPipeline.Geometry => Geometry.ToGrant();

    public void Start(uint streamId, IFrameSink sink)
    {
        _streamId = streamId;
        _sink = sink;
        _lastOverflows = Ctx.Overflows;
        StartThread($"Panorama-{streamId}");
    }

    PanoramaGrant IPanoramaPipeline.Reconfigure(PanoramaRequest request) => Reconfigure(request).ToGrant();

    private long RowInterval(PanoramaGeometry g) => Math.Max(g.FftSize, (long)(Ctx.WideRateHz / Math.Max(0.1, g.Fps)));

    protected override long NextRowPosition(long position)
    {
        if (_nextRowAt < 0)
        {
            PanoramaGeometry g;
            lock (AccLock) g = AccGeometry;
            _nextRowAt = position + RowInterval(g);
        }
        return _nextRowAt;
    }

    protected override bool OnFrameIntegrated(long position, long droppedWide)
    {
        byte[] frame;
        lock (AccLock)
        {
            var g = AccGeometry;
            long interval = RowInterval(g);
            if (_nextRowAt < 0) _nextRowAt = position + interval;
            if (position < _nextRowAt) return false;
            _nextRowAt += interval;
            // Far behind (a lap, a stall): the next row is one interval from now, never a burst.
            if (_nextRowAt <= position) _nextRowAt = position + interval;
            if (_row.Length != g.Bins) _row = new short[g.Bins];
            uint ffts = (uint)Acc.FramesIntegrated;
            Acc.TakeRow(g.CropStart, g.Bins, _row);
            long ov = Ctx.Overflows;
            var header = MakeHeader(_seq++, g, ffts, (uint)Math.Clamp(ov - _lastOverflows, 0, uint.MaxValue), droppedWide);
            _lastOverflows = ov;
            frame = RowHeader.ComposeFrame(_streamId, header, _row);
        }
        Interlocked.Increment(ref RowsPosted);
        if (_sink != null && !_sink.Post(frame)) Stopping = true;
        return true;
    }

    public override void Dispose()
    {
        base.Dispose();
        _removed(this);
    }
}

/// <summary>A pull accumulator for one client's <c>sweep</c> (W1): integrates continuously and hands
/// over everything since the previous take. It stops computing after 30 s without a take, so a
/// client that stopped sweeping does not cost a core forever; the next take restarts it.</summary>
internal sealed class SweepWorker : SpectrumWorker
{
    private const int IdleStopMs = 30_000;
    private long _dropped;
    private ulong _seq;
    private long _lastOverflows;
    private long _lastTakeTicks = Environment.TickCount64;
    private short[] _row = Array.Empty<short>();

    public SweepWorker(IRadioContext ctx, PanoramaRequest request) : base(ctx, request)
    {
        _lastOverflows = ctx.Overflows;
        StartThread("Sweep");
    }

    protected override bool Paused => Environment.TickCount64 - Volatile.Read(ref _lastTakeTicks) > IdleStopMs;

    protected override bool OnFrameIntegrated(long position, long droppedWide)
    {
        lock (AccLock)
        {
            _dropped += droppedWide;
            Monitor.PulseAll(AccLock);
        }
        return true;
    }

    /// <summary>Everything integrated since the previous take, as one row. Waits for at least one
    /// frame (the first take after a new geometry costs one FFT interval).</summary>
    public SweepResult Take(int timeoutMs)
    {
        Volatile.Write(ref _lastTakeTicks, Environment.TickCount64);
        long deadline = Environment.TickCount64 + timeoutMs;
        lock (AccLock)
        {
            while (Acc.FramesIntegrated == 0 && !Stopping)
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0)
                    throw new RadioRefusal(ErrorCodes.NotReady, $"no FFT frame was integrated within {timeoutMs} ms - is the radio streaming?");
                Monitor.Wait(AccLock, (int)Math.Min(left, 250));
            }
            var g = AccGeometry;
            if (_row.Length != g.Bins) _row = new short[g.Bins];
            uint ffts = (uint)Acc.FramesIntegrated;
            Acc.TakeRow(g.CropStart, g.Bins, _row);
            long ov = Ctx.Overflows;
            var header = MakeHeader(++_seq, g, ffts, (uint)Math.Clamp(ov - _lastOverflows, 0, uint.MaxValue), _dropped);
            _lastOverflows = ov;
            _dropped = 0;
            return new SweepResult { Header = header, Bins = (short[])_row.Clone() };
        }
    }
}
