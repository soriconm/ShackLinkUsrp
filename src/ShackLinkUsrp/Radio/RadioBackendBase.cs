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
    private RadioCaps _caps = null!;      // set through Caps, which stamps it
    private long _settledFrom;              // written under _listenersLock, read without it
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

    /// <summary>What the radio can do. Every caps a backend states carries THIS sidecar's FFT ceiling,
    /// stamped here - the number a client plans with (<c>max_fft_size</c>, CRTU200's A3) is the number
    /// <see cref="PanoramaGeometry.Compute"/> refuses a geometry at, by construction.</summary>
    public RadioCaps Caps
    {
        get => _caps;
        protected set
        {
            value.MaxFftSize = MaxFftSize;
            _caps = value;
        }
    }

    public WideRing Ring { get; }
    public int MaxFftSize { get; } = 1 << 20;

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

    /// <summary>Read WITHOUT the listener lock: a sweep worker being replaced is disposed - its thread
    /// joined - under that lock, and an idle worker reads this every 100 ms, so a locked read made every
    /// replacement of an idle accumulator wait out the 2 s join. The write stays under the lock.</summary>
    public long SettledFrom => Volatile.Read(ref _settledFrom);

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

    /// <summary>Samples a pipeline must skip after ANY radio change - a retune, a rate, a gain, AGC,
    /// antenna, bandwidth or setting - because they were already in flight at the old settings (USB
    /// transport buffers). 0 for a source that changes at a sample it knows.</summary>
    protected virtual long SettleSamples => 0;

    /// <summary>The first ring position taken at the settings just applied, asked right after the
    /// Apply that changed them. A real radio answers "now plus what was in flight"; a source that
    /// knows exactly where its change took effect (the synthetic radio) answers that position.</summary>
    protected virtual long ChangeBoundary() => Ring.Written + SettleSamples;

    /// <summary>
    /// EVERY radio change ends here (CRTU200's A2). The boundary is remembered, so a pipeline created
    /// later starts no earlier (F1), and every pipeline alive now is told it - never earlier than a
    /// boundary already announced, or a rate's shorter settle could undo a retune's longer one. The
    /// remembering and the listener snapshot are ONE critical section with a pipeline's registration:
    /// a pipeline is either in the snapshot, and told, or registered after it, and reads the new
    /// <see cref="SettledFrom"/> when it starts. Called AFTER the state is written, so a pipeline that
    /// applies the change reads the settings its next frame is taken at (the gain its rows state).
    /// </summary>
    private void Announce(Action<IRadioListener, long> tell)
    {
        long boundary = ChangeBoundary();
        List<IRadioListener> all;
        lock (_listenersLock)
        {
            if (boundary > _settledFrom) Volatile.Write(ref _settledFrom, boundary);
            boundary = _settledFrom;
            all = _listeners.ToList();
        }
        foreach (var l in all) tell(l, boundary);
    }

    /// <summary>A change to the receive chain that leaves the LO and the rate alone (A2 F3).</summary>
    private void AnnounceFrontEnd() => Announce((l, at) => l.OnFrontEndChanged(at));

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
        Announce((l, at) => l.OnRadioRetuned(eff, at));
        Log($"LO {eff} Hz" + (eff != hz ? $" (asked {hz})" : ""));
    }

    public void SetRate(double hz)
    {
        double eff = ApplyRate(hz);
        lock (_stateLock) _state.WideRateHz = eff;
        Announce((l, at) => l.OnRateChanged(eff, at));
        Log($"wide rate {eff:0} S/s" + (Math.Abs(eff - hz) > 0.5 ? $" (asked {hz:0})" : ""));
    }

    // A2 F3: these four, and a setting, told no pipeline, so the next row integrated FFTs from both
    // sides of the change and stated the new gain. Each announces now, as a retune does.

    public void SetGain(double db)
    {
        double eff = ApplyGain(db);
        lock (_stateLock)
        {
            _state.GainDb = eff;
            _state.Agc = false;
        }
        AnnounceFrontEnd();
    }

    public void SetAgc(bool on)
    {
        ApplyAgc(on);
        lock (_stateLock) _state.Agc = on;
        AnnounceFrontEnd();
    }

    public void SetAntenna(string name)
    {
        string eff = ApplyAntenna(name);
        lock (_stateLock) _state.Antenna = eff;
        AnnounceFrontEnd();
    }

    public void SetBandwidth(double hz)
    {
        double eff = ApplyBandwidth(hz);
        lock (_stateLock) _state.BandwidthHz = eff;
        AnnounceFrontEnd();
    }

    public JsonArray GetSettings() => RadioSettings();

    /// <summary>A named setting (UHD's clock source, DC and IQ correction) changes what the radio
    /// delivers as surely as a gain does, so it is announced too - after it applied; a refused one
    /// changed nothing. (dc_offset_hz retunes through <see cref="SetLo"/> and is announced there as well.)</summary>
    public string SetSetting(string key, string value)
    {
        string eff = ApplySetting(key, value);
        AnnounceFrontEnd();
        return eff;
    }

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
