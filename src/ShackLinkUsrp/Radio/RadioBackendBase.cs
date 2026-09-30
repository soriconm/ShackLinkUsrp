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
/// here runs; what is left to refuse is what only the radio knows. Both radios TRANSMIT
/// (<see cref="ITxBackend"/>, docs/usrp_b210_support.md section 9.17 P4-b): the transmitter's state,
/// the one open transmission and the rules around it are here, the hardware is the subclass's.
/// </summary>
internal abstract class RadioBackendBase : IRadioBackend, ITxBackend, IRadioContext
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
    private void Announce(Action<IRadioListener, long> tell, long? at = null)
    {
        long boundary = at ?? ChangeBoundary();
        List<IRadioListener> all;
        lock (_listenersLock)
        {
            if (boundary > _settledFrom) Volatile.Write(ref _settledFrom, boundary);
            boundary = _settledFrom;
            all = _listeners.ToList();
        }
        foreach (var l in all) tell(l, boundary);
    }

    /// <summary>A change to the receive chain that leaves the LO and the rate alone (A2 F3). A burst's
    /// start and end are one too (section 9.17 P4-a, receive interplay): on a B2xx the ATR moves the
    /// receiver's port and the transmitter's leakage changes what it hears, so no row may integrate
    /// across a key-up or a key-down. <paramref name="at"/>: the exact ring position, when the radio
    /// knows it; null = the backend's own boundary for a change made now.</summary>
    private void AnnounceFrontEnd(long? at = null) => Announce((l, p) => l.OnFrontEndChanged(p), at);

    /// <summary>For a subclass whose radio changed the receive chain as a side effect (a TX streamer
    /// powering the AD9361's TX chain moves it through ALERT and back).</summary>
    protected void AnnounceFrontEndChange() => AnnounceFrontEnd();

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

    // ------------------------------------------------------------ ITxBackend (section 9.17 P4-a, P4-b)

    private readonly object _txLock = new();
    private readonly TxState _txState = new() { GainDb = double.NaN };
    private double _txConfiguredRateHz;
    private RadioTransmission? _openTx;
    private long _txBursts;
    // A stepped source moves the centre and the LO itself; what the client had set is put back when it ends.
    private (long CenterHz, long LoOffsetHz)? _txTuneToRestore;

    /// <summary>The sources a transmitter here builds (ShackLink docs/usrp_b210_support.md 9.17 P4-a', 9.18
    /// P4b2): ShackLink's IQ, and the tone, AM / FM and stepped sweep generated in the sidecar.</summary>
    public static readonly string[] BuiltSources = { TxSourceKinds.Iq, TxSourceKinds.Tone, TxSourceKinds.Mod, TxSourceKinds.Step };

    /// <summary>What <c>caps.tx</c> says about the generated sources - the ONE place both radios state them,
    /// so the synthetic radio cannot offer what the UHD one does not build, or the reverse. The modulating waves are
    /// the protocol library's (section 9.21 Q5), cut at its harmonic count.</summary>
    protected static TxCaps StateGeneratedSources(TxCaps caps)
    {
        caps.Sources = (string[])BuiltSources.Clone();
        caps.Modulations = new[] { TxWords.Am, TxWords.Fm, TxWords.Pm, TxWords.Dsb, TxWords.Ssb };
        caps.ModulatingWaveforms = (string[])TxWaveforms.All.Clone();
        caps.ModulatingHarmonics = ProtocolConstants.TxModulatingHarmonics;
        caps.MinDwellMs = ProtocolConstants.TxMinDwellMs;
        caps.MaxStepPoints = TxStepPlan.MaxPoints;
        return caps;
    }

    public event Action<long>? TxUnderflow;

    /// <summary>The clock rows and blocks carry, which stamps the server's <c>tx</c> events.</summary>
    public long ClockTicks => SidecarClock.Ticks;

    /// <summary><c>caps.tx</c> for the wide rate in force; null while the radio is not ready or states no
    /// transmitter (a UHD backend whose TX chain did not come up at open says why in its log).</summary>
    public TxCaps? TxCaps
    {
        get
        {
            lock (_stateLock)
                if (_state.Readiness != RadioStates.Ready) return null;
            return RadioTxCaps();
        }
    }

    public TxState GetTxState()
    {
        lock (_txLock) return _txState.Clone();
    }

    /// <summary>The open transmission, or null.</summary>
    protected RadioTransmission? OpenTx
    {
        get { lock (_txLock) return _openTx; }
    }

    /// <summary>For a subclass: the transmitter's read-backs at open. <paramref name="configuredRateHz"/>
    /// is the rate a generated source's own rate is put back to when it ends.</summary>
    protected void InitTxState(Action<TxState> set, double configuredRateHz)
    {
        lock (_txLock)
        {
            set(_txState);
            _txConfiguredRateHz = configuredRateHz;
        }
    }

    /// <summary>What the transmitter can do now; null = no transmitter.</summary>
    protected abstract TxCaps? RadioTxCaps();

    /// <summary>Set the TX rate and return the rate READ BACK.</summary>
    protected abstract double ApplyTxRate(double hz);

    /// <summary>Set the TX gain and return the gain READ BACK.</summary>
    protected abstract double ApplyTxGain(double db);

    /// <summary>Tune with no transmission open: the LO on the carrier, or <paramref name="loOffsetHz"/>
    /// from it (P4-3). Returns the effective centre and where the LO landed.</summary>
    protected abstract (long CenterHz, long LoHz) ApplyTxTune(long centerHz, long loOffsetHz);

    /// <summary>Move a keyed generated carrier's centre: the DUC's CORDIC inside its reach of the LO
    /// (<see cref="TxWords.RetuneDsp"/>), the synthesizer beyond it (<see cref="TxWords.RetuneRf"/>).</summary>
    protected abstract (long CenterHz, long LoHz, string Retune) ApplyTxPark(long centerHz, long loOffsetHz, RadioTransmission open);

    /// <summary>The master clock the DUC runs at - what its reach, and so a stepped sweep's LO windows, are
    /// computed from. UHD's read-back clock; the synthetic radio's model of it.</summary>
    protected abstract double TxTickHz { get; }

    /// <summary>Put a stepped source's carrier at <paramref name="carrierHz"/> with the LO at
    /// <paramref name="loHz"/>: the synthesizer to that LO when <paramref name="moveLo"/>, else the DUC alone.
    /// Returns what the radio read back.</summary>
    protected abstract (long CenterHz, long LoHz) ApplyTxStepTune(long carrierHz, long loHz, bool moveLo);

    /// <summary>For a radio side that moves a tuned step's carrier itself (UHD's sender thread).</summary>
    internal (long CenterHz, long LoHz) StepTune(long carrierHz, long loHz, bool moveLo) => ApplyTxStepTune(carrierHz, loHz, moveLo);

    /// <summary>Open the radio's side of a transmission. An IQ one radiates nothing before its first sample;
    /// a stepped one runs by <paramref name="plan"/>.</summary>
    /// <summary>Open a transmission. A generated source's <paramref name="signal"/> is <c>TxRules.Describe</c> of it
    /// against these caps - the one computation the server states on the grant, and the one its generator is built
    /// from; null for IQ.</summary>
    protected abstract RadioTransmission OpenTransmission(TxStartRequest request, TxGrant grant, TxStepPlan? plan, TxSignal? signal);

    private void RequireTransmitter()
    {
        lock (_stateLock)
            if (_state.Readiness != RadioStates.Ready)
                throw new RadioRefusal(ErrorCodes.NotReady, "the radio is not open");
        if (RadioTxCaps() == null) throw new RadioRefusal(TxRules.NoTransmitter().Code, TxRules.NoTransmitter().Text);
    }

    public TxConfigureResult ConfigureTx(TxSettings s)
    {
        RequireTransmitter();
        var res = new TxConfigureResult();
        if (s.RateHz != null)
        {
            double asked = s.RateHz.Value;
            double eff = ApplyTxRate(asked);
            lock (_txLock)
            {
                _txState.RateHz = eff;
                _txConfiguredRateHz = eff;
            }
            Log($"TX rate {eff:0} S/s" + (eff != asked ? $" (asked {asked:0})" : ""));
            // Every admitted rate is one the DUC reads back exactly (section 9.17 item 5): a mismatch is
            // the radio disagreeing with its own rules, and the state already says what it did.
            if (eff != asked)
                throw new RadioRefusal(ErrorCodes.RadioFault, $"the transmitter read back {eff:0} S/s for the admitted rate {asked:0} S/s");
        }
        if (s.GainDb != null)
        {
            double eff = ApplyTxGain(s.GainDb.Value);
            lock (_txLock) _txState.GainDb = eff;
            Log($"TX gain {eff} dB" + (eff != s.GainDb.Value ? $" (asked {s.GainDb.Value})" : ""));
        }
        if (s.CenterHz != null || s.LoOffsetHz != null)
        {
            TxState st = GetTxState();
            long center = s.CenterHz ?? st.CenterHz;
            long offset = s.LoOffsetHz ?? st.LoOffsetHz;
            var open = OpenTx;
            if (open != null && !open.IsFinished && open.Request.IsGenerated && s.CenterHz != null)
            {
                var park = ApplyTxPark(center, offset, open);
                res.TimeTicks = ClockTicks;
                res.Retune = park.Retune;
                open.Park(park.CenterHz);
                lock (_txLock)
                {
                    _txState.CenterHz = park.CenterHz;
                    _txState.LoHz = park.LoHz;
                }
                Log($"TX park to {park.CenterHz} Hz by the {(park.Retune == TxWords.RetuneDsp ? "DUC" : "synthesizer")}, LO {park.LoHz} Hz");
            }
            else
            {
                var tune = ApplyTxTune(center, offset);
                lock (_txLock)
                {
                    _txState.CenterHz = tune.CenterHz;
                    _txState.LoHz = tune.LoHz;
                    _txState.LoOffsetHz = offset;
                }
                Log($"TX centre {tune.CenterHz} Hz, LO {tune.LoHz} Hz" + (tune.CenterHz != center ? $" (asked {center})" : ""));
            }
        }
        return res;
    }

    public ITxTransmission StartTx(TxStartRequest r)
    {
        RequireTransmitter();
        var caps = RadioTxCaps()!;
        var open = OpenTx;
        if (open != null && !open.IsFinished)
            throw new RadioRefusal(ErrorCodes.TxActive, "a transmission is already open in the radio");
        if (Array.IndexOf(BuiltSources, r.Source) < 0)
            throw new RadioRefusal(ErrorCodes.Unsupported,
                $"this sidecar does not build a \"{r.Source}\" source; it builds {string.Join(", ", BuiltSources)}");
        TxState st = GetTxState();
        double rate = st.RateHz;
        TxStepPlan? plan = null;
        // What a generated source IS, from the rules it was admitted by: its generator is built from exactly this.
        TxSignal? signal = null;
        if (r.IsGenerated)
        {
            signal = TxRules.Describe(r, caps);
            if (signal == null)
                throw new RadioRefusal(ErrorCodes.Unsupported, "this signal cannot be described against the transmitter's caps, so it is not generated");
        }
        if (r.IsGenerated)
        {
            // Its samples are the sidecar's own: it runs at the LOWEST admitted rate that holds it (P4-a'). A
            // stepped sweep's plan says which: the lowest that holds the whole sweep in baseband, or the lowest
            // of all when the sweep is tuned by frequency writes.
            if (r.Source == TxSourceKinds.Step)
            {
                plan = TxStepPlan.For(r, caps, TxTickHz);
                if (plan == null) throw new RadioRefusal(ErrorCodes.OutOfRange, "no admitted TX rate can run this sweep");
                rate = plan.RateHz;
            }
            else
            {
                rate = TxRules.RateFor(r, caps);
            }
            if (rate <= 0) throw new RadioRefusal(ErrorCodes.OutOfRange, "no admitted TX rate holds this signal");
            if (rate != st.RateHz)
            {
                double eff = ApplyTxRate(rate);
                lock (_txLock) _txState.RateHz = eff;
                if (eff != rate)
                {
                    RestoreConfiguredTxRate();
                    throw new RadioRefusal(ErrorCodes.RadioFault, $"the transmitter read back {eff:0} S/s for the admitted rate {rate:0} S/s");
                }
            }
        }
        long center = r.IsGenerated ? st.CenterHz + r.OffsetHz : st.CenterHz;
        long lo = st.LoHz;
        if (plan != null)
        {
            // The LO parks where the sweep needs it and the first step's carrier stands there before anything
            // is keyed (P4-a': a stepped sweep's LO sits where its steps need it; P4-3 is the tone's and the
            // inlet's rule). What the client had set is put back when the sweep ends.
            lock (_txLock) _txTuneToRestore = (st.CenterHz, st.LoOffsetHz);
            try
            {
                var parked = plan.Baseband
                    ? ApplyTxStepTune(plan.CenterHz, plan.LoHz[0], moveLo: true)
                    : ApplyTxStepTune(plan.FrequencyHz[0], plan.LoHz[0], moveLo: true);
                center = plan.Baseband ? parked.CenterHz + (plan.FrequencyHz[0] - plan.CenterHz) : parked.CenterHz;
                lo = parked.LoHz;
                OnTransmissionStep(center, lo);
            }
            catch
            {
                RestoreConfiguredTxRate();
                RestoreConfiguredTxTune();
                throw;
            }
        }
        var grant = new TxGrant
        {
            Source = r.Source,
            CenterHz = center,
            LoHz = lo,
            RateHz = rate,
            GainDb = st.GainDb,
            LevelDbfs = r.IsGenerated ? r.LevelDbfs : double.NaN,
            QueueSamples = r.IsGenerated ? 0 : TxRatePlan.QueueSamplesFor(rate),
            MaxBlockSamples = ProtocolConstants.MaxIqBlockSamples,
            StartHz = plan != null ? r.StartHz : 0,
            StopHz = plan != null ? r.StopHz : 0,
            Points = plan != null ? r.Points : 0,
        };
        RadioTransmission t;
        try
        {
            t = OpenTransmission(r, grant, plan, signal);
        }
        catch
        {
            if (r.IsGenerated) RestoreConfiguredTxRate();
            if (plan != null) RestoreConfiguredTxTune();
            throw;
        }
        lock (_txLock) _openTx = t;
        Interlocked.Increment(ref _txBursts);
        Log($"transmission opened: {r.Source} at {grant.CenterHz} Hz, {rate:0} S/s, gain {st.GainDb} dB" +
            (r.IsGenerated ? $", carrier {r.LevelDbfs} dBFS" : $", queue {grant.QueueSamples} samples") +
            (plan == null ? "" : plan.Baseband
                ? $"; a baseband sweep of {plan.Points} points around {grant.LoHz} Hz, stepped at exact samples"
                : $"; a tuned sweep of {plan.Points} points in {plan.Windows} LO window(s) (DUC reach {plan.ReachHz:0} Hz), the LO parked at {grant.LoHz} Hz"));
        return t;
    }

    internal void RaiseTxUnderflow(long n) => TxUnderflow?.Invoke(n);

    /// <summary>A stepped source's carrier moved: <c>state.tx</c> follows it.</summary>
    internal void OnTransmissionStep(long centerHz, long loHz)
    {
        lock (_txLock)
        {
            _txState.CenterHz = centerHz;
            _txState.LoHz = loHz;
        }
    }

    /// <summary>A transmission keyed or let go. Either is a front-end change to every receive pipeline;
    /// letting go also frees the transmitter and puts back the rate a generated source moved.</summary>
    internal void OnTransmissionEdge(RadioTransmission t, bool keyed, long? position)
    {
        AnnounceFrontEnd(position);
        if (keyed)
        {
            Log($"transmission keyed: {t.Request.Source} at {t.Grant.CenterHz} Hz");
            return;
        }
        lock (_txLock)
            if (_openTx == t) _openTx = null;
        if (t.Request.IsGenerated) RestoreConfiguredTxRate();
        if (t.Request.Source == TxSourceKinds.Step) RestoreConfiguredTxTune();
        Log($"transmission let go: {t.Request.Source}, {t.Samples} samples, {t.Underflows} underflow(s)");
    }

    /// <summary>After a stepped source: the centre, LO and offset the client had set, as the rate is.</summary>
    private void RestoreConfiguredTxTune()
    {
        (long CenterHz, long LoOffsetHz)? restore;
        lock (_txLock)
        {
            restore = _txTuneToRestore;
            _txTuneToRestore = null;
        }
        if (restore == null) return;
        try
        {
            var tune = ApplyTxTune(restore.Value.CenterHz, restore.Value.LoOffsetHz);
            lock (_txLock)
            {
                _txState.CenterHz = tune.CenterHz;
                _txState.LoHz = tune.LoHz;
                _txState.LoOffsetHz = restore.Value.LoOffsetHz;
            }
        }
        catch (Exception ex)
        {
            Log($"the TX centre {restore.Value.CenterHz} Hz could not be put back after a stepped sweep: {ex.Message}");
        }
    }

    private void RestoreConfiguredTxRate()
    {
        double configured, now;
        lock (_txLock)
        {
            configured = _txConfiguredRateHz;
            now = _txState.RateHz;
        }
        if (configured <= 0 || configured == now) return;
        try
        {
            double eff = ApplyTxRate(configured);
            lock (_txLock) _txState.RateHz = eff;
        }
        catch (Exception ex)
        {
            Log($"the TX rate {configured:0} S/s could not be put back after a generated source: {ex.Message}");
        }
    }

    /// <summary>The <c>tx</c> half of the telemetry every radio shares.</summary>
    private JsonObject TxTelemetry()
    {
        var t = OpenTx;
        var o = new JsonObject().Set("bursts", Interlocked.Read(ref _txBursts)).Set("open", t != null && !t.IsFinished);
        if (t != null)
            o.Set("source", t.Request.Source).Set("keyed", t.IsKeyed).Set("samples", t.Samples).Set("underflows", t.Underflows)
             .Set("center_hz", t.Grant.CenterHz).Set("rate_hz", t.Grant.RateHz);
        return o;
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
            .Set("tx", TxTelemetry())
            .Set("source", RadioTelemetry());
    }

    public string[] GetLog(int lines) => LogBook.Tail(lines);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // The transmitter first: nothing radiates past a closing radio.
        var tx = OpenTx;
        if (tx != null && !tx.IsFinished)
            try { tx.Stop(false, 0); } catch (Exception ex) { Log("transmission stop at close: " + ex.Message); }
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
