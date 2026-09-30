// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Radio;

/// <summary>
/// One open transmission - what both radios share (ShackLink docs/usrp_b210_support.md section 9.17
/// P4-a, P4-b). Its SOURCE is an IQ queue the <c>tx</c> stream fills, or a generator; its RADIO side (a
/// UHD sender thread, or the synthetic radio's own clock) takes samples from the source, reports what
/// happened through the Note* calls, and calls <see cref="Finish"/> exactly once, when it has let go of
/// the transmitter. <see cref="Stop"/> asks for a drain or an abort and waits for that - it never
/// declares an acknowledgement the radio did not give. A <c>step</c> source's steps are announced through
/// <see cref="NoteStep"/>, which the radio side calls at the instant it knows (section 9.18 P4b2).
/// </summary>
internal abstract class RadioTransmission : ITxTransmission
{
    /// <summary>How long an abort waits for the radio to let go before it says it did not.</summary>
    public const int AbortWaitMs = 2000;

    /// <summary>Steps kept for a <see cref="Step"/> handler that has not subscribed yet: the server subscribes
    /// the moment <c>StartTx</c> returns, and step 0 may already have happened by then.</summary>
    private const int MaxUnheardSteps = 1024;

    private readonly RadioBackendBase _backend;
    private readonly ManualResetEventSlim _finished = new(false);
    private readonly object _gate = new();
    private volatile bool _drain;
    private volatile bool _abort;
    private int _stopping;
    private int _endedRaised;
    private int _finishing;
    private long _drainDeadlineTs;
    private long _samples;
    private long _underflows;
    private long _keyedTs;
    private long _finishedTs;
    private bool _acked;
    private string _finishText = "";
    private long _keyedTicks;

    private readonly object _eventGate = new();
    private Action<JsonObject>? _stepHandlers;
    private List<JsonObject>? _unheardSteps = new();
    private Action<StreamEnd>? _endedHandlers;
    private StreamEnd? _unheardEnd;

    protected RadioTransmission(RadioBackendBase backend, TxStartRequest request, TxGrant grant, TxStepPlan? plan, TxSignal? signal)
    {
        _backend = backend;
        Request = request;
        Grant = grant;
        switch (request.Source)
        {
            case TxSourceKinds.Iq:
                Queue = new TxSampleQueue(grant.QueueSamples);
                break;
            case TxSourceKinds.Tone:
                Generator = new ToneGenerator(request.OffsetHz, grant.RateHz, request.LevelDbfs);
                break;
            case TxSourceKinds.Mod:
                Generator = ModulatedSource(request, grant, signal);
                break;
            case TxSourceKinds.Step:
                Plan = plan ?? throw new ArgumentNullException(nameof(plan), "a stepped source needs its plan");
                Generator = new SteppedCarrier(plan, request.LevelDbfs);
                // The plan's LO, never the read-back: whether a later step moves the synthesizer is the
                // plan's question, and a synthesizer that lands a hertz off must not make every step "rf".
                StepLoHz = plan.LoHz[0];
                break;
            default:
                throw new RadioRefusal(ErrorCodes.Unsupported,
                    $"this sidecar does not build a \"{request.Source}\" source; it builds {string.Join(", ", RadioBackendBase.BuiltSources)}");
        }
        // Baseband 0 Hz: an IQ transmission's centre; a tone or a modulated carrier sits offset_hz from it; a
        // baseband sweep's steps sit (f - centre) from the parked centre; a tuned sweep's carrier IS baseband 0 Hz.
        CenterHz = request.Source == TxSourceKinds.Iq ? grant.CenterHz
                 : Plan != null ? (Plan.Baseband ? grant.CenterHz - (Plan.FrequencyHz[0] - Plan.CenterHz) : grant.CenterHz)
                 : grant.CenterHz - request.OffsetHz;
    }

    /// <summary>A <c>mod</c> source's generator, built from the signal the rules described (section 9.21): the wave's
    /// components are the library's, so what goes out is what the grant says and what a client predicts.</summary>
    private static IIqGenerator ModulatedSource(TxStartRequest request, TxGrant grant, TxSignal? signal)
    {
        var wave = signal?.Waveform ?? throw new RadioRefusal(ErrorCodes.Unsupported,
            $"a {request.Modulation} carrier needs its modulating wave described; this one was not");
        var components = wave.Components.Select(c => new ModComponent(c.FrequencyHz, c.Amplitude, c.PhaseRad)).ToArray();
        switch (request.Modulation)
        {
            case TxWords.Am:
                return ModGenerator.Am(request.OffsetHz, grant.RateHz, request.LevelDbfs, components, request.AmDepthPercent / 100.0);
            case TxWords.Fm:
                return ModGenerator.Fm(request.OffsetHz, grant.RateHz, request.LevelDbfs, components, request.FmDeviationHz);
            case TxWords.Pm:
                // Degrees on the wire, radians here (section 9.21 Q3); the peak frequency deviation is the library's.
                return ModGenerator.Pm(request.OffsetHz, grant.RateHz, request.LevelDbfs, components, TxRules.PmBetaRad(request),
                                       signal!.PeakFrequencyDeviationHz);
            case TxWords.Dsb:
                return ModGenerator.Dsb(request.OffsetHz, grant.RateHz, request.LevelDbfs, components);
            case TxWords.Ssb:
                return ModGenerator.Ssb(request.OffsetHz, grant.RateHz, request.LevelDbfs, components, wave.AnalyticPeak,
                                        upper: request.Sideband == TxWords.Usb);
            default:
                throw new RadioRefusal(ErrorCodes.Unsupported,
                    $"this sidecar does not modulate \"{request.Modulation}\"; it builds {TxWords.Am}, {TxWords.Fm}, {TxWords.Pm}, {TxWords.Dsb} and {TxWords.Ssb}");
        }
    }

    public TxStartRequest Request { get; }
    public TxGrant Grant { get; }

    /// <summary>An IQ transmission's samples; null for a generated one.</summary>
    protected TxSampleQueue? Queue { get; }

    /// <summary>A generated source's samples (tone, mod, step); null for an IQ one.</summary>
    protected IIqGenerator? Generator { get; }

    /// <summary>A stepped source's plan; null otherwise.</summary>
    protected TxStepPlan? Plan { get; }

    /// <summary>The next step the radio side has not announced yet (0 = the key-up's own).</summary>
    protected long NextStep { get; private set; }

    /// <summary>The LO the current step stands at, as the plan placed it.</summary>
    protected long StepLoHz { get; private set; }

    /// <summary>The backend, for a radio side that moves the carrier itself (a tuned step on UHD).</summary>
    protected RadioBackendBase Backend => _backend;

    /// <summary><see cref="SidecarClock"/> at the key-up; 0 before it.</summary>
    protected long KeyedTicks => Interlocked.Read(ref _keyedTicks);

    /// <summary>Where baseband 0 Hz leaves: the TX centre (a tone sits <c>offset_hz</c> from it). A park moves it.</summary>
    public long CenterHz
    {
        get => Volatile.Read(ref _centerHz);
        private set => Volatile.Write(ref _centerHz, value);
    }
    private long _centerHz;

    public bool IsFinished => _finished.IsSet;
    public bool IsKeyed => Interlocked.Read(ref _keyedTs) != 0;
    protected bool DrainRequested => _drain;
    protected bool AbortRequested => _abort;
    protected long DrainDeadlineTs => Interlocked.Read(ref _drainDeadlineTs);
    public long Samples => Interlocked.Read(ref _samples);
    public long Underflows => Interlocked.Read(ref _underflows);

    /// <summary>The radio ended it on its own. A handler that subscribes after it happened is still told -
    /// the server subscribes only once <c>StartTx</c> has returned.</summary>
    public event Action<StreamEnd>? Ended
    {
        add
        {
            if (value == null) return;
            StreamEnd? held;
            lock (_eventGate)
            {
                _endedHandlers += value;
                held = _unheardEnd;
                _unheardEnd = null;
            }
            if (held != null) ThreadPool.QueueUserWorkItem(_ => value(held));
        }
        remove
        {
            lock (_eventGate) _endedHandlers -= value;
        }
    }

    /// <summary>A stepped source's step: <c>{index, center_hz, retune, time_ticks, timing}</c>, in order. The
    /// steps raised before the first handler subscribed are handed to it, in order, as it subscribes.</summary>
    public event Action<JsonObject>? Step
    {
        add
        {
            if (value == null) return;
            lock (_eventGate)
            {
                _stepHandlers += value;
                var held = _unheardSteps;
                _unheardSteps = null;
                if (held != null)
                    foreach (var b in held) value(b);
            }
        }
        remove
        {
            lock (_eventGate) _stepHandlers -= value;
        }
    }

    private void RaiseEnded(StreamEnd end)
    {
        Action<StreamEnd>? h;
        lock (_eventGate)
        {
            h = _endedHandlers;
            if (h == null)
            {
                _unheardEnd = end;
                return;
            }
        }
        ThreadPool.QueueUserWorkItem(_ => h(end));
    }

    /// <summary>Under the gate, so the steps reach every handler in the order they happened, and a handler
    /// that subscribes late is handed the earlier ones first.</summary>
    private void RaiseStep(JsonObject body)
    {
        lock (_eventGate)
        {
            if (_stepHandlers == null)
            {
                if (_unheardSteps != null && _unheardSteps.Count < MaxUnheardSteps) _unheardSteps.Add(body);
                return;
            }
            _stepHandlers(body);
        }
    }

    /// <summary>A park of a generated carrier - a tone or a modulated one; the server refuses it for a stepped
    /// source, which moves its own: the carrier moves to <paramref name="centerHz"/> + its offset.</summary>
    public void Park(long centerHz)
    {
        CenterHz = centerHz;
        Grant.CenterHz = centerHz + Request.OffsetHz;
    }

    // ------------------------------------------------------------------ the radio side

    /// <summary>The first sample went out. <paramref name="position"/>: the ring position from which the
    /// receiver hears it, when the radio knows it exactly (the synthetic one); null = now.</summary>
    protected void NoteKeyed(long? position, long? ticks = null)
    {
        if (Interlocked.CompareExchange(ref _keyedTs, Stopwatch.GetTimestamp(), 0) != 0) return;
        Interlocked.Exchange(ref _keyedTicks, ticks ?? SidecarClock.Ticks);
        _backend.OnTransmissionEdge(this, keyed: true, position);
    }

    /// <summary>
    /// The radio side reached step <see cref="NextStep"/>: the carrier stands at its point's frequency from
    /// <paramref name="timeTicks"/>, and <paramref name="timing"/> says how exactly that is known. A baseband
    /// step's carrier is the parked centre plus its offset; a tuned step's is what the write read back
    /// (<paramref name="tunedCarrierHz"/>, <paramref name="tunedLoHz"/>; the plan's values when the radio
    /// moves exactly). Every client is told, in order, and the state follows.
    /// </summary>
    protected void NoteStep(long timeTicks, string timing, long? tunedCarrierHz = null, long? tunedLoHz = null)
    {
        var plan = Plan!;
        long k = NextStep;
        int index = plan.IndexOf(k);
        long planLo = plan.LoHz[index];
        string retune = k == 0 ? TxWords.RetuneDsp : TxStepPlan.RetuneFor(planLo, StepLoHz);
        long carrier, lo;
        if (plan.Baseband)
        {
            carrier = CenterHz + (plan.FrequencyHz[index] - plan.CenterHz);
            lo = Grant.LoHz;
        }
        else
        {
            carrier = tunedCarrierHz ?? plan.FrequencyHz[index];
            lo = tunedLoHz ?? planLo;
            CenterHz = carrier;
        }
        StepLoHz = planLo;
        NextStep = k + 1;
        Grant.CenterHz = carrier;
        _backend.OnTransmissionStep(carrier, lo);
        RaiseStep(new JsonObject()
            .Set("index", index)
            .Set("center_hz", carrier)
            .Set("retune", retune)
            .Set("time_ticks", timeTicks)
            .Set("timing", timing));
    }

    /// <summary>A step's instant counted from the key-up: its first sample at the TX rate after step 0's.</summary>
    protected long TicksOfStep(long k) =>
        KeyedTicks + (long)Math.Round(Plan!.StartSample(k) * 10_000_000.0 / Grant.RateHz);

    protected void NoteSamples(long n)
    {
        if (n > 0) Interlocked.Add(ref _samples, n);
    }

    /// <summary>A radio that knows exactly how many samples went out (a sweep that ran to its end on the
    /// synthetic radio) says so, rather than what its interpolator had taken ahead of the end.</summary>
    protected void NoteSamplesSent(long exact) => Interlocked.Exchange(ref _samples, exact);

    /// <summary>The radio ran short mid-burst: counted, and told to the server as it happens.</summary>
    protected void NoteUnderflow()
    {
        Interlocked.Increment(ref _underflows);
        _backend.RaiseTxUnderflow(1);
    }

    /// <summary>The radio failed under the transmission. The server is told once, on another thread -
    /// never from inside <see cref="Stop"/>, which already knows - and the radio side goes on to let go
    /// and <see cref="Finish"/>.</summary>
    protected void EndedByRadio(string reason, string text)
    {
        _abort = true;
        Queue?.Abort();
        if (Volatile.Read(ref _stopping) != 0) return;
        if (Interlocked.Exchange(ref _endedRaised, 1) != 0) return;
        RaiseEnded(new StreamEnd { Reason = reason, Text = text });
    }

    /// <summary>A source that ran to its end - a stepped sweep without <c>repeat</c> - once the radio side has
    /// <see cref="Finish"/>ed it: the server is told it ended <c>drained</c>, in words.</summary>
    protected void EndedBySource(string text)
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        if (Interlocked.Exchange(ref _endedRaised, 1) != 0) return;
        RaiseEnded(new StreamEnd { Reason = EndReasons.Drained, Text = text });
    }

    /// <summary>The words a completed sweep ends with.</summary>
    protected string SweepCompletedText() =>
        $"the stepped sweep completed: {Plan!.StepCount} steps of {Plan.DwellMs} ms";

    /// <summary>The radio has let go of the transmitter: once. <paramref name="acked"/> is the RADIO's
    /// word that the last sample left it - nothing else may set it.</summary>
    protected void Finish(bool acked, string text, long? position)
    {
        if (Interlocked.Exchange(ref _finishing, 1) != 0) return;
        lock (_gate)
        {
            _acked = acked;
            _finishText = text ?? "";
        }
        Interlocked.Exchange(ref _finishedTs, Stopwatch.GetTimestamp());
        try
        {
            _backend.OnTransmissionEdge(this, keyed: false, position);
        }
        finally
        {
            _finished.Set();
        }
    }

    /// <summary>A drain was asked for: the radio sends what is queued, then the end-of-burst, and waits
    /// for its acknowledgement until <see cref="DrainDeadlineTs"/>.</summary>
    protected virtual void OnDrainRequested() { }

    /// <summary>An abort was asked for: end the burst where it stands.</summary>
    protected virtual void OnAbortRequested() { }

    // ------------------------------------------------------------------ ITxTransmission

    public bool Write(IqHeader header, byte[] samples)
    {
        if (Queue == null || _drain || _abort) return false;
        return Queue.Write(samples, header.Count);
    }

    public TxStopResult Stop(bool drain, int timeoutMs)
    {
        Interlocked.Exchange(ref _stopping, 1);
        var sw = Stopwatch.StartNew();
        string stopText = "";
        if (drain && !_abort && !_finished.IsSet)
        {
            Interlocked.Exchange(ref _drainDeadlineTs, Stopwatch.GetTimestamp() + (long)(Math.Max(0, timeoutMs) / 1000.0 * Stopwatch.Frequency));
            _drain = true;
            Queue?.Close();
            OnDrainRequested();
            if (!_finished.Wait(Math.Max(0, timeoutMs)))
                stopText = $"the radio did not finish the drain within {timeoutMs} ms, so the burst was ended where it stood";
        }
        if (!_finished.IsSet)
        {
            _abort = true;
            Queue?.Abort();
            OnAbortRequested();
            if (!_finished.Wait(AbortWaitMs))
                stopText = Join(stopText, $"the radio had not let go of the transmitter {AbortWaitMs} ms after it was told to stop");
        }
        var res = new TxStopResult { WasOpen = true, Drain = drain };
        lock (_gate)
        {
            res.Acked = _acked;
            res.Text = Join(stopText, _finishText);
        }
        res.Samples = Samples;
        res.Underflows = Underflows;
        long keyed = Interlocked.Read(ref _keyedTs);
        long ended = Interlocked.Read(ref _finishedTs);
        if (keyed != 0) res.OnMs = (long)(((ended != 0 ? ended : Stopwatch.GetTimestamp()) - keyed) * 1000.0 / Stopwatch.Frequency);
        return res;
    }

    private static string Join(string a, string b) =>
        string.IsNullOrEmpty(a) ? b ?? "" : string.IsNullOrEmpty(b) ? a : a + "; " + b;

    public void Dispose()
    {
        if (_finished.IsSet) return;
        try { Stop(false, 0); } catch { }
    }
}
