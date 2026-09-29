// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Host;
using ShackLinkUsrp.Pipelines;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Radio;

/// <summary>
/// What every radio backend shares: the wide ring, the state, the pipelines and the sweep workers
/// built on it, telemetry and the log. A concrete backend supplies the RADIO: how it opens, how it
/// applies a setting, and the thread that fills the ring. The protocol server (MIT library) has
/// already checked ownership, readiness and every range against <see cref="Caps"/> before a setter
/// here runs; what is left to refuse is what only the radio knows.
/// </summary>
internal abstract class RadioBackendBase : IRadioBackend, IRadioContext
{
    private readonly object _stateLock = new();
    private readonly object _listenersLock = new();
    private readonly List<IRadioListener> _listeners = new();
    private readonly Dictionary<int, SweepWorker> _sweeps = new();
    private readonly ManualResetEventSlim _settled = new(false);
    private readonly RadioState _state = new();
    private long _overflows;
    private volatile bool _disposed;

    protected RadioBackendBase(SidecarLog log, RadioInfo info, RadioCaps caps, int ringSamples, long initialLoHz, double initialRateHz)
    {
        LogBook = log;
        Info = info;
        Caps = caps;
        Ring = new WideRing(ringSamples);
        _state.LoHz = initialLoHz;
        _state.WideRateHz = initialRateHz;
        _state.GainDb = 30;
        _state.Readiness = RadioStates.Opening;
    }

    protected SidecarLog LogBook { get; }
    public RadioInfo Info { get; protected set; }
    public RadioCaps Caps { get; protected set; }
    public WideRing Ring { get; }
    public int MaxFftSize { get; protected set; } = 1 << 20;

    public event Action? StateChanged;
    public event Action<string>? Faulted;

    public void Log(string line) => LogBook.Write(line);

    // ------------------------------------------------------------ IRadioContext

    public long LoHz
    {
        get { lock (_stateLock) return _state.LoHz; }
    }

    public double WideRateHz
    {
        get { lock (_stateLock) return _state.WideRateHz; }
    }

    public long Overflows => Interlocked.Read(ref _overflows);

    public float RowGainDb
    {
        get { lock (_stateLock) return _state.Agc ? float.NaN : (float)_state.GainDb; }
    }

    protected void AddOverflows(long n) => Interlocked.Add(ref _overflows, n);

    // ------------------------------------------------------------ lifecycle

    /// <summary>Open the radio on a background thread: clients may connect and read "opening"
    /// while a bitstream loads. Success = ready; failure = a fault in words, kept until disposal.</summary>
    public void Start()
    {
        var t = new Thread(() =>
        {
            try
            {
                OpenRadio();
                lock (_stateLock)
                {
                    _state.Readiness = RadioStates.Ready;
                    _state.FaultText = "";
                }
                Log($"radio ready: {Info.Product} {Info.Serial} at {LoHz} Hz, {WideRateHz:0} S/s");
                _settled.Set();
                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Fault($"the radio could not be opened: {ex.Message}");
            }
        })
        { IsBackground = true, Name = "RadioOpen" };
        t.Start();
    }

    /// <summary>Declare the radio dead (at open or later). Streams end with this text.</summary>
    protected void Fault(string text)
    {
        lock (_stateLock)
        {
            if (_state.Readiness == RadioStates.Fault) return;
            _state.Readiness = RadioStates.Fault;
            _state.FaultText = text;
        }
        Log("FAULT: " + text);
        _settled.Set();
        Faulted?.Invoke(text);
    }

    protected abstract void OpenRadio();
    protected abstract void CloseRadio();

    /// <summary>Apply to the radio and return what it actually did (the readback is the truth).</summary>
    protected abstract long ApplyLo(long hz);
    protected abstract double ApplyRate(double hz);
    protected abstract double ApplyGain(double db);
    protected abstract void ApplyAgc(bool on);
    protected abstract string ApplyAntenna(string name);
    protected abstract double ApplyBandwidth(double hz);
    protected abstract JsonArray RadioSettings();
    protected abstract string ApplySetting(string key, string value);
    protected abstract JsonObject RadioTelemetry();

    /// <summary>Samples a pipeline must skip after a retune because they were already in flight
    /// at the old frequency (USB transport buffers). 0 for a source that retunes at a block edge.</summary>
    protected virtual long RetuneSettleSamples => 0;

    // ------------------------------------------------------------ IRadioBackend

    public RadioState GetState()
    {
        lock (_stateLock) return _state.Clone();
    }

    public bool WaitReady(int timeoutMs)
    {
        _settled.Wait(timeoutMs);
        lock (_stateLock) return _state.Readiness == RadioStates.Ready;
    }

    public void SetLo(long hz)
    {
        long eff = ApplyLo(hz);
        lock (_stateLock) _state.LoHz = eff;
        long boundary = Ring.Written + RetuneSettleSamples;
        foreach (var l in Listeners()) l.OnRadioRetuned(eff, boundary);
        Log($"LO {eff} Hz" + (eff != hz ? $" (asked {hz})" : ""));
    }

    public void SetRate(double hz)
    {
        double eff = ApplyRate(hz);
        lock (_stateLock) _state.WideRateHz = eff;
        long boundary = Ring.Written + RetuneSettleSamples;
        foreach (var l in Listeners()) l.OnRateChanged(eff, boundary);
        Log($"wide rate {eff:0} S/s" + (Math.Abs(eff - hz) > 0.5 ? $" (asked {hz:0})" : ""));
    }

    public void SetGain(double db)
    {
        double eff = ApplyGain(db);
        lock (_stateLock)
        {
            _state.GainDb = eff;
            _state.Agc = false;
        }
    }

    public void SetAgc(bool on)
    {
        ApplyAgc(on);
        lock (_stateLock) _state.Agc = on;
    }

    public void SetAntenna(string name)
    {
        string eff = ApplyAntenna(name);
        lock (_stateLock) _state.Antenna = eff;
    }

    public void SetBandwidth(double hz)
    {
        double eff = ApplyBandwidth(hz);
        lock (_stateLock) _state.BandwidthHz = eff;
    }

    public JsonArray GetSettings() => RadioSettings();

    public string SetSetting(string key, string value) => ApplySetting(key, value);

    /// <summary>For a subclass that learns a state value from the radio itself (open, AGC).</summary>
    protected void UpdateState(Action<RadioState> change, bool announce)
    {
        lock (_stateLock) change(_state);
        if (announce) StateChanged?.Invoke();
    }

    public ILeasePipeline CreateLease(long centerHz, int decimation)
    {
        var p = new LeasePipeline(this, centerHz, decimation, Unlisten);
        Listen(p);
        return p;
    }

    public IPanoramaPipeline CreatePanorama(PanoramaRequest request)
    {
        var p = new PanoramaPipeline(this, request, Unlisten);
        Listen(p);
        return p;
    }

    public SweepResult Sweep(int clientId, PanoramaRequest request, int timeoutMs)
    {
        SweepWorker w;
        lock (_listenersLock)
        {
            if (_sweeps.TryGetValue(clientId, out var existing) && !SameGeometry(existing.Request, request))
            {
                _sweeps.Remove(clientId);
                _listeners.Remove(existing);
                existing.Dispose();
                existing = null;
            }
            if (existing == null)
            {
                existing = new SweepWorker(this, request);
                _sweeps[clientId] = existing;
                _listeners.Add(existing);
            }
            w = existing;
        }
        return w.Take(timeoutMs);
    }

    private static bool SameGeometry(PanoramaRequest a, PanoramaRequest b) =>
        a.Bins == b.Bins && a.Integration == b.Integration && a.Window == b.Window && a.CenterHz == b.CenterHz && a.SpanHz == b.SpanHz;

    public void ForgetClient(int clientId)
    {
        SweepWorker? w = null;
        lock (_listenersLock)
        {
            if (_sweeps.Remove(clientId, out w)) _listeners.Remove(w);
        }
        w?.Dispose();
    }

    private void Listen(IRadioListener l)
    {
        lock (_listenersLock) _listeners.Add(l);
    }

    private void Unlisten(object l)
    {
        lock (_listenersLock) _listeners.Remove((IRadioListener)l);
    }

    private List<IRadioListener> Listeners()
    {
        lock (_listenersLock) return _listeners.ToList();
    }

    public JsonObject GetTelemetry()
    {
        var pipelines = new JsonArray();
        foreach (var l in Listeners())
        {
            switch (l)
            {
                case LeasePipeline lp:
                    pipelines.Add(new JsonObject().Set("kind", "lease").Set("center_hz", lp.CenterHz).Set("rate_hz", lp.RateHz)
                        .Set("blocks", Interlocked.Read(ref lp.BlocksPosted)).Set("samples", Interlocked.Read(ref lp.SamplesProduced))
                        .Set("wide_missed", Interlocked.Read(ref lp.WideSamplesMissed)));
                    break;
                case PanoramaPipeline pp:
                    pipelines.Add(new JsonObject().Set("kind", "panorama").Set("fft_size", pp.Geometry.FftSize)
                        .Set("bins", pp.Geometry.Bins).Set("rows", Interlocked.Read(ref pp.RowsPosted)));
                    break;
                case SweepWorker sw:
                    pipelines.Add(new JsonObject().Set("kind", "sweep").Set("fft_size", sw.Geometry.FftSize));
                    break;
            }
        }
        return new JsonObject()
            .Set("ring_written", Ring.Written)
            .Set("ring_capacity", Ring.Capacity)
            .Set("overflows", Overflows)
            .Set("pipelines", pipelines)
            .Set("source", RadioTelemetry());
    }

    public string[] GetLog(int lines) => LogBook.Tail(lines);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<IRadioListener> all;
        lock (_listenersLock)
        {
            all = _listeners.ToList();
            _listeners.Clear();
            _sweeps.Clear();
        }
        foreach (var l in all)
            if (l is IDisposable d)
                try { d.Dispose(); } catch { }
        try { CloseRadio(); } catch (Exception ex) { Log("close: " + ex.Message); }
    }
}
