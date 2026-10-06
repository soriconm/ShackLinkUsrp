// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Host;
using ShackLinkUsrp.Pipelines;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Radio;

/// <summary>
/// What every radio backend shares: the wide rings, the state, the pipelines and the sweep workers
/// built on them, telemetry and the log. A concrete backend supplies the RADIO: how it opens, how it
/// applies a setting, and the thread that fills the rings. The protocol server (MIT library) has
/// already checked ownership, readiness and every range against <see cref="Caps"/> before a setter
/// here runs; what is left to refuse is what only the radio knows. Both radios TRANSMIT
/// (<see cref="ITxBackend"/>, docs/usrp_b210_support.md section 9.17 P4-b): the transmitter's state,
/// the one open transmission and the rules around it are here, the hardware is the subclass's.
/// <para><b>Two receive sides</b> (section 9.23, <see cref="IRxSidesBackend"/>): each side has its own
/// <see cref="SideContext"/> - a ring, listeners, a settle boundary - its own centre, gain, AGC and antenna.
/// The two rings share ONE position: when both stream they are written in lockstep, and a side switched on
/// starts at the position the other has reached, so radio position n is one instant on both. The rate, the
/// clock and the RF LO are the radio's; with two sides streaming the RF LO is PLACED here
/// (<see cref="SideRules.PlaceRfLo"/>) and the clock is chosen here (<see cref="SideRules.DualClockHz"/>).</para>
/// </summary>
internal abstract class RadioBackendBase : IRadioBackend, IRxSidesBackend, ITxBackend
{
    /// <summary>Side B's front end; side A's lives in <see cref="_state"/>'s top-level fields, which are
    /// side A's on the wire too.</summary>
    private sealed class SideFront
    {
        public long CenterHz;
        public double GainDb = 30;
        public bool Agc;
        public string Antenna = "";
    }

    private readonly object _stateLock = new();
    private readonly ManualResetEventSlim _settled = new(false);
    private readonly RadioState _state = new();
    private readonly SideFront _b;
    private readonly SideContext[] _sides;
    private bool _streamA = true, _streamB;
    private long _rfLoHz;
    private RadioCaps _caps = null!;      // set through Caps, which stamps it
    private long _overflows;
    private volatile bool _disposed;

    protected RadioBackendBase(SidecarLog log, RadioInfo info, RadioCaps caps, int ringSamples, long initialLoHz, double initialRateHz)
    {
        LogBook = log;
        Info = info;
        Caps = caps;
        _sides = new[] { new SideContext(this, RxSides.A, ringSamples), new SideContext(this, RxSides.B, ringSamples) };
        _state.LoHz = initialLoHz;
        _state.WideRateHz = initialRateHz;
        _state.GainDb = 30;
        _state.Readiness = RadioStates.Opening;
        _b = new SideFront { CenterHz = initialLoHz };
        _rfLoHz = initialLoHz;
    }

    protected SidecarLog LogBook { get; }
    public RadioInfo Info { get; protected set; }

    /// <summary>What the radio can do. Every caps a backend states carries THIS sidecar's FFT ceiling,
    /// stamped here - the number a client plans with (<c>max_fft_size</c>, CRTU200's A3) is the number
    /// <see cref="PanoramaGeometry.Compute"/> refuses a geometry at, by construction. A backend states its
    /// sides in <see cref="RadioCaps.RxSides"/> (null = side A alone) and the rates two sides stream at in
    /// <see cref="RadioCaps.DualRatesHz"/>.</summary>
    public RadioCaps Caps
    {
        get => _caps;
        protected set
        {
            value.MaxFftSize = MaxFftSize;
            _caps = value;
        }
    }

    /// <summary>Side A's ring - the one every single-side path writes.</summary>
    public WideRing Ring => _sides[0].Ring;
    public int MaxFftSize { get; } = 1 << 20;

    public event Action? StateChanged;
    public event Action<string>? Faulted;

    public void Log(string line) => LogBook.Write(line);

    // ------------------------------------------------------------ sides

    private static int IndexOf(string side) => side == RxSides.B ? 1 : 0;

    /// <summary>A side's pipeline context.</summary>
    internal SideContext Side(string side) => _sides[IndexOf(side)];

    /// <summary>A side's ring, for the thread that fills it.</summary>
    protected WideRing RingOf(string side) => _sides[IndexOf(side)].Ring;

    /// <summary>A side's ring, for a gate reading its position.</summary>
    internal WideRing RingOfSide(string side) => RingOf(side);

    /// <summary>The radio's position: the samples every streaming ring has been written with. The rings are in
    /// lockstep, so this is the position of whichever side streams.</summary>
    protected long RadioPosition => Math.Max(_sides[0].Ring.Written, _sides[1].Ring.Written);

    /// <summary>Put every ring in <paramref name="sides"/> at the radio's position, so a side switched on starts
    /// where the other stands. Only while nothing writes the rings (the UHD backend between streams; the
    /// synthetic generator on its own thread, at the change's exact position).</summary>
    protected void AlignRings(IEnumerable<string> sides, long? position = null)
    {
        long pos = position ?? RadioPosition;
        foreach (var s in sides) RingOf(s).AlignTo(pos);
    }

    /// <summary>The sides this radio has (its caps; side A alone when they say nothing).</summary>
    protected string[] SupportedSides => SideRules.OfferedSides(Caps);

    /// <summary>The sides streaming now, in wire order.</summary>
    public string[] StreamingSides
    {
        get
        {
            lock (_stateLock)
                return _streamA && _streamB ? RxSides.All : _streamB ? new[] { RxSides.B } : RxSides.OnlyA;
        }
    }

    /// <summary>The centre <paramref name="side"/>'s products sit on.</summary>
    public long CenterOf(string side)
    {
        lock (_stateLock) return side == RxSides.B ? _b.CenterHz : _state.LoHz;
    }

    /// <summary>The gain a row of <paramref name="side"/> states: NaN while AGC decides.</summary>
    public float RowGainOf(string side)
    {
        lock (_stateLock)
            return side == RxSides.B ? (_b.Agc ? float.NaN : (float)_b.GainDb) : (_state.Agc ? float.NaN : (float)_state.GainDb);
    }

    /// <summary>Where the RF LO is, as the radio read it back.</summary>
    protected long RfLoHz
    {
        get { lock (_stateLock) return _rfLoHz; }
    }

    /// <summary>The AD9361 clock in force, as the radio states it (UHD's read-back; the synthetic radio's model).</summary>
    protected abstract double MasterClockHz { get; }

    /// <summary>The clock the radio runs with the given sides at <paramref name="wideRateHz"/>: with two, the one
    /// section 9.23.2 item 9 chooses; with one, UHD's own automatic rule - chosen HERE either way, because once a
    /// clock has been set explicitly UHD never chooses one again (S4).</summary>
    protected static double ClockFor(int sides, double wideRateHz) =>
        sides > 1 ? SideRules.DualClockHz(wideRateHz) : TxRatePlan.ModelTickHz(wideRateHz);

    /// <summary>The clock THIS radio runs with <paramref name="sides"/> at <paramref name="wideRateHz"/>; 0 = two sides cannot run
    /// there. The B2xx's rule (<see cref="ClockFor"/>) unless a radio says otherwise: one with no per-side DDC runs at its wide rate
    /// whatever its sides (ShackLink docs/plutosdr_support.md 9.32 D21).</summary>
    protected virtual double ClockForSides(int sides, double wideRateHz) => ClockFor(sides, wideRateHz);

    /// <summary>
    /// True for a radio whose two sides share ONE centre (ShackLink docs/plutosdr_support.md 9.32 D21): no per-side DDC, so the RF
    /// LO IS the centre and two centres are served only when equal. The B2xx's placement - a DDC's window inside 0.9 of a wider
    /// clock (<see cref="SideRules.PlaceRfLo"/>) - is never asked of such a radio: with the clock at the wide rate it places no LO
    /// at all, not even for two equal centres.
    /// </summary>
    protected virtual bool SidesShareOneCenter => false;

    // ------------------------------------------------------------ the pipelines' view

    public long LoHz
    {
        get { lock (_stateLock) return _state.LoHz; }
    }

    public double WideRateHz
    {
        get { lock (_stateLock) return _state.WideRateHz; }
    }

    public long Overflows => Interlocked.Read(ref _overflows);

    /// <summary>Side A's settle boundary (the single-side view the TX paths and the gates read).</summary>
    public long SettledFrom => _sides[0].SettledFrom;

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
                Log($"radio ready: {Info.Product} {Info.Serial} at {LoHz} Hz, {WideRateHz:0} S/s" +
                    (SupportedSides.Length > 1 ? $"; receive sides {RxSides.Words(SupportedSides)}, streaming {RxSides.Words(StreamingSides)}" : ""));
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

    /// <summary>
    /// Put the RF LO at <paramref name="rfLoHz"/> and each streaming side named in <paramref name="centers"/> on
    /// its centre (the FPGA's DDC shifting the difference), and return what the radio READ BACK: the RF LO and
    /// every centre. One side: the classic tune, the LO at its centre plus <c>dc_offset_hz</c>. Two sides: ONE
    /// tune on channel 0 and channel 1's DDC set with the RF policy NONE, then both read back (section 9.23.2
    /// item 8) - a tune on channel 1 would move the LO under channel 0.
    /// </summary>
    protected abstract (long RfLoHz, IReadOnlyDictionary<string, long> Centers) ApplyTuning(long rfLoHz, IReadOnlyDictionary<string, long> centers);

    /// <summary>Re-make the receive stream for exactly <paramref name="sides"/> at the rate in force: the clock set
    /// to <paramref name="clockHz"/>, the rings of any side switched on aligned (<see cref="AlignRings"/>) BEFORE
    /// the stream writes them, and the radio tuned as <see cref="ApplyTuning"/> would. Returns the tuning read
    /// back and the clock the radio states afterwards.</summary>
    protected abstract (long RfLoHz, IReadOnlyDictionary<string, long> Centers, double ClockHz) ApplyRxSides(
        IReadOnlyList<string> sides, long rfLoHz, IReadOnlyDictionary<string, long> centers, double clockHz);

    protected abstract double ApplyRate(double hz);
    protected abstract double ApplyGain(string side, double db);
    protected abstract void ApplyAgc(string side, bool on);
    protected abstract string ApplyAntenna(string side, string name);
    /// <summary>The analog bandwidth: ONE filter for both chains (S3) - a backend writes it to both and reads
    /// back the chip's one answer.</summary>
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
    protected virtual long ChangeBoundary() => RadioPosition + SettleSamples;

    /// <summary>The first ring position at the tuning or side change JUST applied: asked ONCE, right after
    /// <see cref="ApplyTuning"/> (a retune) or <see cref="ApplyRxSides"/> returns, and given to every announce of that change -
    /// never asked for a gain, AGC, antenna, bandwidth, setting or rate change, so a backend's answer for a retune cannot reach
    /// one. Default <see cref="ChangeBoundary"/>. A backend that throws away what was in flight at a retune knows where its
    /// fresh samples begin, and answers that (ShackLink docs/plutosdr_support.md 9.33: the Pluto's re-opened buffer).</summary>
    protected virtual long AppliedBoundary() => ChangeBoundary();

    /// <summary>
    /// EVERY radio change ends here (CRTU200's A2), on the sides it touched. The boundary is remembered per
    /// side, so a pipeline created later starts no earlier (F1), and every pipeline alive on that side now is
    /// told it - never earlier than a boundary already announced, or a rate's shorter settle could undo a
    /// retune's longer one. The remembering and the listener snapshot are ONE critical section with a
    /// pipeline's registration: a pipeline is either in the snapshot, and told, or registered after it, and
    /// reads the new <see cref="SideContext.SettledFrom"/> when it starts. Called AFTER the state is written, so
    /// a pipeline that applies the change reads the settings its next frame is taken at (the gain its rows state).
    /// </summary>
    private void Announce(IEnumerable<SideContext> sides, Action<IRadioListener, long> tell, long? at = null)
    {
        long boundary = at ?? ChangeBoundary();
        foreach (var side in sides)
        {
            List<IRadioListener> all;
            long b;
            lock (side.ListenersLock)
            {
                if (boundary > side.SettledFromValue) Volatile.Write(ref side.SettledFromValue, boundary);
                b = side.SettledFromValue;
                all = side.Listeners.ToList();
            }
            foreach (var l in all) tell(l, b);
        }
    }

    private void Announce(Action<IRadioListener, long> tell, long? at = null) => Announce(_sides, tell, at);

    /// <summary>A change to the receive chain that leaves the LO and the rate alone (A2 F3) - on one side, or
    /// (null) on the radio. A burst's start and end are one too (section 9.17 P4-a, receive interplay): on a
    /// B2xx the ATR moves the receiver's port and the transmitter's leakage changes what it hears, so no row may
    /// integrate across a key-up or a key-down. <paramref name="at"/>: the exact ring position, when the radio
    /// knows it; null = the backend's own boundary for a change made now.</summary>
    private void AnnounceFrontEnd(long? at = null, string? side = null) =>
        Announce(side == null ? _sides : new[] { Side(side) }, (l, p) => l.OnFrontEndChanged(p), at);

    /// <summary>For a subclass whose radio changed the receive chain as a side effect (a TX streamer
    /// powering the AD9361's TX chain moves it through ALERT and back).</summary>
    protected void AnnounceFrontEndChange() => AnnounceFrontEnd();

    // ------------------------------------------------------------ IRadioBackend

    public RadioState GetState()
    {
        lock (_stateLock)
        {
            var s = _state.Clone();
            if (SupportedSides.Length < 2 && !_streamB) return s;   // a one-sided radio says what it always said
            s.RxSides = _streamA && _streamB ? RxSides.All : _streamB ? new[] { RxSides.B } : RxSides.OnlyA;
            s.Rx = new List<RxSideState>
            {
                new() { Side = RxSides.A, Streaming = _streamA, CenterHz = _state.LoHz, Agc = _state.Agc, GainDb = _state.GainDb, Antenna = _state.Antenna ?? "" },
                new() { Side = RxSides.B, Streaming = _streamB, CenterHz = _b.CenterHz, Agc = _b.Agc, GainDb = _b.GainDb, Antenna = _b.Antenna ?? "" },
            };
            s.RfLoHz = _rfLoHz;
            double clock = MasterClockHz;
            if (clock > 0) s.MasterClockHz = clock;
            if (_streamA && _streamB && clock > 0) s.MaxApartHz = SideRules.MaxApartHz(clock, _state.WideRateHz);
            return s;
        }
    }

    public bool WaitReady(int timeoutMs)
    {
        _settled.Wait(timeoutMs);
        lock (_stateLock) return _state.Readiness == RadioStates.Ready;
    }

    /// <summary>The streaming sides' centres, with <paramref name="side"/> moved to <paramref name="hz"/> when named.</summary>
    private Dictionary<string, long> StreamingCenters(string? side = null, long hz = 0)
    {
        var d = new Dictionary<string, long>();
        foreach (var s in StreamingSides) d[s] = s == side ? hz : CenterOf(s);
        return d;
    }

    /// <summary>Where the RF LO goes for these streaming centres: one side, its centre plus dc_offset_hz (the
    /// classic tune); two, placed by <see cref="SideRules.PlaceRfLo"/> - which keeps the LO where it is while
    /// both windows fit.</summary>
    private long PlaceFor(IReadOnlyDictionary<string, long> centers, double clockHz, out bool moved)
    {
        long dc;
        long current;
        double rate;
        lock (_stateLock)
        {
            dc = _state.DcOffsetHz;
            current = _rfLoHz;
            rate = _state.WideRateHz;
        }
        if (centers.Count < 2)
        {
            long rf = centers.Values.First() + dc;
            moved = rf != current;
            return rf;
        }
        if (SidesShareOneCenter)
        {
            if (centers.Values.Distinct().Count() > 1)
                throw new RadioRefusal(ErrorCodes.OutOfRange,
                    $"this radio's two sides share ONE centre (no per-side DDC): {RxSides.Words(centers.Keys)} cannot be at " +
                    $"{string.Join(" and ", centers.Values)} Hz - move the radio (set_lo with no side), which moves both");
            long one = centers.Values.First() + dc;
            moved = one != current;
            return one;
        }
        if (!SideRules.PlaceRfLo(current, centers.Values.ToList(), rate, clockHz, dc, out long placed, out moved))
            throw new RadioRefusal(ErrorCodes.OutOfRange,
                $"no RF LO serves {RxSides.Words(centers.Keys)} at {string.Join(" and ", centers.Values)} Hz: they are more than " +
                $"{SideRules.MaxApartHz(clockHz, rate)} Hz apart at a {clockHz:0} Hz clock");
        return placed;
    }

    /// <summary>Write what the radio read back after a tuning.</summary>
    private void TakeTuning(long rf, IReadOnlyDictionary<string, long> centers)
    {
        lock (_stateLock)
        {
            _rfLoHz = rf;
            foreach (var kv in centers)
                if (kv.Key == RxSides.B) _b.CenterHz = kv.Value;
                else _state.LoHz = kv.Value;
        }
    }

    /// <summary>The radio as a whole (what an older client means by set_lo): side A to <paramref name="hz"/> and
    /// every other streaming side by the same amount, the RF LO moved with them.</summary>
    public void SetLo(long hz)
    {
        long delta = hz - CenterOf(RxSides.A);
        var before = StreamingCenters();
        var centers = before.ToDictionary(kv => kv.Key, kv => kv.Key == RxSides.A ? hz : kv.Value + delta);
        long rf = centers.Count > 1 ? RfLoHz + delta : PlaceFor(centers, MasterClockHz, out _);
        var eff = ApplyTuning(rf, centers);
        long boundary = AppliedBoundary();
        TakeTuning(eff.RfLoHz, eff.Centers);
        // Side A's centre is the radio's LO on the wire even when A does not stream: it moves too.
        if (!before.ContainsKey(RxSides.A)) lock (_stateLock) _state.LoHz = hz;
        foreach (var kv in eff.Centers)
            Announce(new[] { Side(kv.Key) }, (l, at) => l.OnRadioRetuned(kv.Value, at), boundary);
        Log($"LO {CenterOf(RxSides.A)} Hz" + (CenterOf(RxSides.A) != hz ? $" (asked {hz})" : "") +
            (eff.Centers.Count > 1 ? $"; {RxSides.Words(RxSides.B)} at {CenterOf(RxSides.B)} Hz, RF LO {eff.RfLoHz} Hz" : ""));
    }

    public void SetRate(double hz)
    {
        double eff = ApplyRate(hz);
        lock (_stateLock) _state.WideRateHz = eff;
        Announce((l, at) => l.OnRateChanged(eff, at));
        Log($"wide rate {eff:0} S/s" + (Math.Abs(eff - hz) > 0.5 ? $" (asked {hz:0})" : ""));
        ReapplyBandwidth(eff);
        // A new rate moves the clock, and with two sides the reach with it: centres that no longer fit are
        // brought together, said out loud (the server ended every lease before a rate change).
        if (StreamingSides.Length > 1)
        {
            double clock = MasterClockHz;
            long apart = SideRules.MaxApartHz(clock, eff);
            if (Math.Abs(CenterOf(RxSides.B) - CenterOf(RxSides.A)) > apart)
            {
                Log($"at {eff:0} S/s the two sides can be at most {apart} Hz apart: {RxSides.Words(RxSides.B)} moves to {RxSides.Words(RxSides.A)}'s centre");
                SetCenter(RxSides.B, CenterOf(RxSides.A));
            }
        }
    }

    // A2 F3: these, and a setting, told no pipeline, so the next row integrated FFTs from both sides of the
    // change and stated the new gain. Each announces now, on the side it changed, as a retune does.

    public void SetGain(double db) => SetGain(RxSides.A, db);

    public void SetAgc(bool on) => SetAgc(RxSides.A, on);

    public void SetAntenna(string name) => SetAntenna(RxSides.A, name);

    public void SetGain(string side, double db)
    {
        double eff = ApplyGain(side, db);
        lock (_stateLock)
        {
            if (side == RxSides.B) { _b.GainDb = eff; _b.Agc = false; }
            else { _state.GainDb = eff; _state.Agc = false; }
        }
        AnnounceFrontEnd(side: side);
    }

    public void SetAgc(string side, bool on)
    {
        ApplyAgc(side, on);
        lock (_stateLock)
        {
            if (side == RxSides.B) _b.Agc = on;
            else _state.Agc = on;
        }
        AnnounceFrontEnd(side: side);
    }

    public void SetAntenna(string side, string name)
    {
        string eff = ApplyAntenna(side, name);
        lock (_stateLock)
        {
            if (side == RxSides.B) _b.Antenna = eff;
            else _state.Antenna = eff;
        }
        AnnounceFrontEnd(side: side);
    }

    /// <summary>ONE filter for both chains (S3): every side is told. A bandwidth a client SETS is kept across
    /// rate changes (<see cref="ReapplyBandwidth"/>); until one is set, it follows the wide rate.</summary>
    public void SetBandwidth(double hz)
    {
        double eff = ApplyBandwidth(hz);
        lock (_stateLock)
        {
            _state.BandwidthHz = eff;
            _bandwidthSetHz = hz;
        }
        AnnounceFrontEnd();
    }

    /// <summary>The analog bandwidth a client set; NaN = none, and it follows the wide rate.</summary>
    private double _bandwidthSetHz = double.NaN;

    /// <summary>
    /// After a rate change, put the analog filter where it should be and say so (ShackLink
    /// docs/usrp_b210_support.md 9.27). UHD re-programs the AD9361's filter from the clock on every
    /// master-clock change (<c>ad9361_device_t::set_clock_rate</c> calls <c>set_bw_filter</c> with its own
    /// clock-derived width), so without this a set bandwidth was silently lost and the state kept reporting
    /// it. A set value is re-applied; otherwise the filter follows the new rate.
    /// </summary>
    private void ReapplyBandwidth(double rateHz)
    {
        double set;
        lock (_stateLock) set = _bandwidthSetHz;
        bool follows = double.IsNaN(set);
        double want = follows ? BandwidthForRate(rateHz, Caps) : set;
        double eff;
        try
        {
            eff = ApplyBandwidth(want);
        }
        catch (Exception ex)
        {
            Log($"the analog bandwidth could not be re-applied after the rate change: {ex.Message}");
            return;
        }
        lock (_stateLock) _state.BandwidthHz = eff;
        AnnounceFrontEnd();
        Log(follows
            ? $"analog bandwidth {eff:0} Hz, following the wide rate"
            : $"analog bandwidth kept at {eff:0} Hz, as it was set" +
              (eff < rateHz * 0.99 ? $" - narrower than the {rateHz:0} S/s stream, so its edges are filtered" : ""));
    }

    /// <summary>The bandwidth that follows a wide rate: the rate itself, inside the radio's analog range.</summary>
    internal static double BandwidthForRate(double rateHz, RadioCaps? caps)
    {
        double bw = rateHz;
        if (caps != null && caps.BandwidthMaxHz > 0) bw = Math.Min(bw, caps.BandwidthMaxHz);
        if (caps != null && caps.BandwidthMinHz > 0) bw = Math.Max(bw, caps.BandwidthMinHz);
        return bw;
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

    /// <summary>For a subclass: side B's read-backs at open (its gain, antenna, AGC).</summary>
    protected void UpdateSideB(double? gainDb = null, bool? agc = null, string? antenna = null, long? centerHz = null)
    {
        lock (_stateLock)
        {
            if (gainDb != null) _b.GainDb = gainDb.Value;
            if (agc != null) _b.Agc = agc.Value;
            if (antenna != null) _b.Antenna = antenna;
            if (centerHz != null) _b.CenterHz = centerHz.Value;
        }
    }

    /// <summary>For a subclass: where the RF LO landed at open.</summary>
    protected void UpdateRfLo(long hz)
    {
        lock (_stateLock) _rfLoHz = hz;
    }

    // ------------------------------------------------------------ IRxSidesBackend (section 9.23)

    /// <summary>
    /// Stream exactly <paramref name="sides"/>. The server has checked the owners, the rate and the streams a
    /// side switched off takes with it (ended already). A side switched on starts at its last centre when the
    /// other side can reach it, else at the other's; the RF LO is placed, the clock chosen, the streamer made
    /// again - and the side that stays on is told it was reconfigured (a gap, a new clock, never a lost lease).
    /// </summary>
    public void SetRxSides(IReadOnlyList<string> sides)
    {
        var want = RxSides.Canonical(sides) ?? RxSides.OnlyA;
        foreach (var s in want)
            if (!RxSides.Contains(SupportedSides, s))
                throw new RadioRefusal(ErrorCodes.Unsupported, $"this radio has no {RxSides.Words(s)}");
        var before = StreamingSides;
        if (before.SequenceEqual(want)) return;
        double rate = WideRateHz;
        double clock = ClockForSides(want.Length, rate);
        if (clock <= 0)
            throw new RadioRefusal(ErrorCodes.OutOfRange, $"two sides cannot stream at {rate:0} S/s: no clock under 30.72 MHz serves it with an even decimation");
        var centers = new Dictionary<string, long>();
        foreach (var s in want) centers[s] = CenterOf(s);
        if (want.Length > 1 && Math.Abs(centers[RxSides.B] - centers[RxSides.A]) > SideRules.MaxApartHz(clock, rate))
        {
            // The side switched on cannot be where it was left: it starts where the side already streaming is.
            string on = RxSides.Contains(before, RxSides.A) ? RxSides.B : RxSides.A;
            centers[on] = centers[RxSides.Other(on)];
            Log($"{RxSides.Words(on)} starts at {centers[on]} Hz: its last centre is out of reach of {RxSides.Words(RxSides.Other(on))}");
        }
        long rf;
        lock (_stateLock)
        {
            long dc = _state.DcOffsetHz;
            if (want.Length < 2 || SidesShareOneCenter) rf = centers.Values.First() + dc;
            else if (!SideRules.PlaceRfLo(_rfLoHz, centers.Values.ToList(), rate, clock, dc, out rf, out _))
                rf = centers.Values.Min() + (centers.Values.Max() - centers.Values.Min()) / 2;
        }
        var eff = ApplyRxSides(want, rf, centers, clock);
        long boundary = AppliedBoundary();
        lock (_stateLock)
        {
            _streamA = RxSides.Contains(want, RxSides.A);
            _streamB = RxSides.Contains(want, RxSides.B);
        }
        TakeTuning(eff.RfLoHz, eff.Centers);
        var staying = before.Where(want.Contains).Select(Side).ToList();
        var switchedOn = want.Where(s => !before.Contains(s)).Select(Side).ToList();
        var switchedOff = before.Where(s => !want.Contains(s)).Select(Side).ToList();
        Announce(staying, (l, at) => l.OnRadioReconfigured(at), boundary);
        // A side switched on has no pipeline yet; its settle boundary is the switch, so the first one starts after it.
        Announce(switchedOn, (l, at) => l.OnRadioReconfigured(at), boundary);
        foreach (var side in switchedOff) DropSweeps(side);
        Log($"streaming {RxSides.Words(want)} (was {RxSides.Words(before)}), clock {eff.ClockHz:0} Hz, RF LO {eff.RfLoHz} Hz" +
            string.Concat(eff.Centers.Select(kv => $", {RxSides.Words(kv.Key)} at {kv.Value} Hz")));
    }

    /// <summary>
    /// Move ONE side's centre (section 9.23 Q2). One side streaming: the classic tune. Two: the other side's
    /// centre stays, the RF LO stays where both windows still fit and is placed anew where they do not - and
    /// then the other side is told it was reconfigured under an unmoved centre.
    /// </summary>
    public void SetCenter(string side, long hz)
    {
        var centers = StreamingCenters(side, hz);
        if (!centers.ContainsKey(side))
            throw new RadioRefusal(ErrorCodes.NotReady, $"{RxSides.Words(side)} is not streaming");
        long rf = PlaceFor(centers, MasterClockHz, out bool moved);
        var eff = ApplyTuning(rf, centers);
        long boundary = AppliedBoundary();
        TakeTuning(eff.RfLoHz, eff.Centers);
        Announce(new[] { Side(side) }, (l, at) => l.OnRadioRetuned(eff.Centers[side], at), boundary);
        if (moved)
            Announce(eff.Centers.Keys.Where(s => s != side).Select(Side), (l, at) => l.OnRadioReconfigured(at), boundary);
        Log($"{RxSides.Words(side)} at {eff.Centers[side]} Hz" + (eff.Centers[side] != hz ? $" (asked {hz})" : "") +
            (centers.Count > 1 ? (moved ? $"; the RF LO placed at {eff.RfLoHz} Hz" : $"; the RF LO stays at {eff.RfLoHz} Hz") : ""));
    }

    public ILeasePipeline CreateLease(long centerHz, int decimation) => CreateLease(RxSides.A, centerHz, decimation);

    public ILeasePipeline CreateLease(string side, long centerHz, int decimation) =>
        RegisterLease(new LeasePipeline(Side(side), centerHz, decimation, UnlistenLease));

    public ILeasePipeline CreateDualLease(long centerHz, int decimation) =>
        RegisterLease(new LeasePipeline(new IRadioContext[] { Side(RxSides.A), Side(RxSides.B) }, centerHz, decimation, UnlistenLease));

    private LeasePipeline RegisterLease(LeasePipeline p)
    {
        for (int i = 0; i < p.Sides.Length; i++)
        {
            var ctx = Side(p.Sides[i]);
            lock (ctx.ListenersLock) ctx.Listeners.Add(p.SideListeners[i]);
        }
        return p;
    }

    private void UnlistenLease(LeasePipeline p)
    {
        for (int i = 0; i < p.Sides.Length; i++)
        {
            var ctx = Side(p.Sides[i]);
            lock (ctx.ListenersLock) ctx.Listeners.Remove(p.SideListeners[i]);
        }
    }

    public IPanoramaPipeline CreatePanorama(PanoramaRequest request)
    {
        var ctx = Side(request.Side ?? RxSides.A);
        var p = new PanoramaPipeline(ctx, request, pp => Unlisten(ctx, pp));
        lock (ctx.ListenersLock) ctx.Listeners.Add(p);
        return p;
    }

    public SweepResult Sweep(int clientId, PanoramaRequest request, int timeoutMs)
    {
        var ctx = Side(request.Side ?? RxSides.A);
        SweepWorker w;
        lock (ctx.ListenersLock)
        {
            if (ctx.Sweeps.TryGetValue(clientId, out var existing) && !SameGeometry(existing.Request, request))
            {
                ctx.Sweeps.Remove(clientId);
                ctx.Listeners.Remove(existing);
                existing.Dispose();
                existing = null;
            }
            if (existing == null)
            {
                existing = new SweepWorker(ctx, request);
                ctx.Sweeps[clientId] = existing;
                ctx.Listeners.Add(existing);
            }
            w = existing;
        }
        return w.Take(timeoutMs);
    }

    private static bool SameGeometry(PanoramaRequest a, PanoramaRequest b) =>
        a.Bins == b.Bins && a.Integration == b.Integration && a.Window == b.Window && a.CenterHz == b.CenterHz && a.SpanHz == b.SpanHz;

    public void ForgetClient(int clientId)
    {
        foreach (var ctx in _sides)
        {
            SweepWorker? w = null;
            lock (ctx.ListenersLock)
            {
                if (ctx.Sweeps.Remove(clientId, out w)) ctx.Listeners.Remove(w);
            }
            w?.Dispose();
        }
    }

    /// <summary>A side switched off: its clients' sweep accumulators go with it (the server ended its streams).</summary>
    private void DropSweeps(SideContext ctx)
    {
        List<SweepWorker> workers;
        lock (ctx.ListenersLock)
        {
            workers = ctx.Sweeps.Values.ToList();
            foreach (var w in workers) ctx.Listeners.Remove(w);
            ctx.Sweeps.Clear();
        }
        foreach (var w in workers) w.Dispose();
    }

    private static void Unlisten(SideContext ctx, IRadioListener l)
    {
        lock (ctx.ListenersLock) ctx.Listeners.Remove(l);
    }

    private List<IRadioListener> Listeners() => _sides.SelectMany(s => s.Snapshot()).ToList();

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
        caps.Modulations = new[] { TxWords.Am, TxWords.Fm, TxWords.Pm, TxWords.Dsb, TxWords.Ssb, TxWords.Pulse };
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

    /// <summary>
    /// A radio whose transmitter cannot be SET while idle - a Pluto, whose TX LO is powered down and whose attenuation is at
    /// its maximum until the key-up (ShackLink docs/plutosdr_support.md section 9.15 Q1) - HOLDS set_tx's centre and gain in
    /// <c>state.tx.held</c> and writes them at the key-up. The fields beside it stay the TRUTH IN FORCE, so a client that
    /// reads only them sees a read-back that disagrees with what it asked, and does not key: fail-closed.
    /// </summary>
    protected virtual bool HoldsTxUntilKeyed => false;

    /// <summary>A transmission is open and its first sample has gone out.</summary>
    protected bool TxKeyedNow => OpenTx is { IsFinished: false, IsKeyed: true };

    /// <summary>A holding radio that is not keyed: set_tx's centre and gain go to <c>held</c>, never to the state in force.</summary>
    private bool HoldingNow => HoldsTxUntilKeyed && !TxKeyedNow;

    /// <summary>For a subclass: the transmitter's state in force changed by the radio itself (a key-up's read-backs, an end).</summary>
    protected void UpdateTxState(Action<TxState> set)
    {
        lock (_txLock) set(_txState);
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
            bool holding = HoldingNow;
            double eff = ApplyTxGain(s.GainDb.Value);
            lock (_txLock)
            {
                // A holding radio records what the next key-up writes; keyed, what it took is in force as well.
                if (_txState.Held != null) _txState.Held.GainDb = eff;
                if (!holding) _txState.GainDb = eff;
            }
            Log($"TX gain {eff} dB" + (eff != s.GainDb.Value ? $" (asked {s.GainDb.Value})" : "") +
                (holding ? " - HELD: written at the key-up" : ""));
        }
        if (s.CenterHz != null || s.LoOffsetHz != null)
        {
            TxState st = GetTxState();
            long center = s.CenterHz ?? st.NextCenterHz;
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
                    // ...and on a holding radio the next transmission keys there too, as a B2xx's would.
                    if (_txState.Held != null) _txState.Held.CenterHz = park.CenterHz;
                }
                Log($"TX park to {park.CenterHz} Hz by the {(park.Retune == TxWords.RetuneDsp ? "DUC" : "synthesizer")}, LO {park.LoHz} Hz");
            }
            else
            {
                bool holding = HoldingNow;
                var tune = ApplyTxTune(center, offset);
                lock (_txLock)
                {
                    if (_txState.Held != null) _txState.Held.CenterHz = tune.CenterHz;
                    if (!holding)
                    {
                        _txState.CenterHz = tune.CenterHz;
                        _txState.LoHz = tune.LoHz;
                    }
                    _txState.LoOffsetHz = offset;
                }
                Log(holding
                    ? $"TX centre {tune.CenterHz} Hz HELD: the TX LO is written at the key-up, never while receiving (hazard 6)"
                    : $"TX centre {tune.CenterHz} Hz, LO {tune.LoHz} Hz" + (tune.CenterHz != center ? $" (asked {center})" : ""));
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
        // A holding radio keys at what was HELD (section 9.15 Q1): a gain never set is refused - a transmission keys at a
        // stated level - and the centre and LO are the held centre (the LO sits on the carrier: no DUC to offset it).
        if (st.Held != null && double.IsNaN(st.Held.GainDb))
            throw new RadioRefusal(ErrorCodes.BadRequest, "the TX gain was never set (set_tx gain_db): a transmission keys at a stated level");
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
        long center = r.IsGenerated ? st.NextCenterHz + r.OffsetHz : st.NextCenterHz;
        long lo = st.Held != null ? st.NextCenterHz : st.LoHz;
        if (plan != null)
        {
            // The LO parks where the sweep needs it and the first step's carrier stands there before anything
            // is keyed (P4-a': a stepped sweep's LO sits where its steps need it; P4-3 is the tone's and the
            // inlet's rule). What the client had set is put back when the sweep ends. A holding radio only RECORDS
            // the parked LO here: its key-up writes the grant's.
            lock (_txLock) _txTuneToRestore = (st.NextCenterHz, st.LoOffsetHz);
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
            GainDb = st.NextGainDb,
            LevelDbfs = r.IsGenerated ? r.LevelDbfs : double.NaN,
            QueueSamples = r.IsGenerated ? 0 : TxRatePlan.QueueSamplesFor(rate),
            MaxBlockSamples = ProtocolConstants.MaxIqBlockSamples,
            StartHz = plan != null ? r.StartHz : 0,
            StopHz = plan != null ? r.StopHz : 0,
            Points = plan != null ? r.Points : 0,
            RxPortMoves = RxPortMovesForBurst(),
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
        Log($"transmission opened: {r.Source} at {grant.CenterHz} Hz, {rate:0} S/s, gain {st.NextGainDb} dB" +
            (st.Held != null ? " (held: the LO stays down until the first buffer)" : "") +
            (r.IsGenerated ? $", carrier {r.LevelDbfs} dBFS" : $", queue {grant.QueueSamples} samples") +
            (plan == null ? "" : plan.Baseband
                ? $"; a baseband sweep of {plan.Points} points around {grant.LoHz} Hz, stepped at exact samples"
                : $"; a tuned sweep of {plan.Points} points in {plan.Windows} LO window(s) (DUC reach {plan.ReachHz:0} Hz), the LO parked at {grant.LoHz} Hz"));
        return t;
    }

    internal void RaiseTxUnderflow(long n) => TxUnderflow?.Invoke(n);

    /// <summary>
    /// The receive sides that hear their RX2 port instead of TX/RX for every burst (section 9.23.3 <c>rx_port_moves</c>): the
    /// B2xx's full-duplex ATR state takes a side's TX/RX port for its transmitter and gives its receiver RX2 (section 9.17 item
    /// 3). RX A whenever it streams on TX/RX - its transmitter is the one keyed. RX B only with BOTH sides streaming, when
    /// the transmission is 2R2T and TX B exists (silenced, but its ATR runs); a lone RX B shares no chain with TX A.
    /// </summary>
    private string[] RxPortMovesForBurst()
    {
        var moves = new List<string>();
        lock (_stateLock)
        {
            if (_streamA && string.Equals(_state.Antenna, "TX/RX", StringComparison.OrdinalIgnoreCase)) moves.Add(RxSides.A);
            if (_streamA && _streamB && string.Equals(_b.Antenna, "TX/RX", StringComparison.OrdinalIgnoreCase)) moves.Add(RxSides.B);
        }
        return moves.ToArray();
    }

    /// <summary>A stepped source's carrier moved: <c>state.tx</c> follows it - once it is in force. A holding radio's sweep
    /// parked before its key-up is only a plan: its LO is still powered down.</summary>
    internal void OnTransmissionStep(long centerHz, long loHz)
    {
        if (HoldingNow) return;
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
            bool holding = HoldingNow;
            var tune = ApplyTxTune(restore.Value.CenterHz, restore.Value.LoOffsetHz);
            lock (_txLock)
            {
                if (_txState.Held != null) _txState.Held.CenterHz = tune.CenterHz;
                if (!holding)
                {
                    _txState.CenterHz = tune.CenterHz;
                    _txState.LoHz = tune.LoHz;
                }
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
        foreach (var ctx in _sides)
        {
            foreach (var l in ctx.Snapshot())
            {
                switch (l)
                {
                    case PanoramaPipeline pp:
                        pipelines.Add(new JsonObject().Set("kind", "panorama").Set("side", ctx.Side).Set("fft_size", pp.Geometry.FftSize)
                            .Set("bins", pp.Geometry.Bins).Set("rows", Interlocked.Read(ref pp.RowsPosted)));
                        break;
                    case SweepWorker sw:
                        pipelines.Add(new JsonObject().Set("kind", "sweep").Set("side", ctx.Side).Set("fft_size", sw.Geometry.FftSize));
                        break;
                }
            }
        }
        // A lease listens on every side it reads; it is listed once, from its first side's listener.
        foreach (var lp in _sides.SelectMany(s => s.Snapshot()).Select(LeaseOf).Where(p => p != null).Distinct())
            pipelines.Add(new JsonObject().Set("kind", "lease").Set("sides", lp!.Sides).Set("center_hz", lp.CenterHz).Set("rate_hz", lp.RateHz)
                .Set("blocks", Interlocked.Read(ref lp.BlocksPosted)).Set("samples", Interlocked.Read(ref lp.SamplesProduced))
                .Set("wide_missed", Interlocked.Read(ref lp.WideSamplesMissed)));
        return new JsonObject()
            .Set("ring_written", Ring.Written)
            .Set("ring_capacity", Ring.Capacity)
            .Set("ring_written_b", RingOf(RxSides.B).Written)
            .Set("rx_sides", StreamingSides)
            .Set("rf_lo_hz", RfLoHz)
            .Set("master_clock_hz", MasterClockHz)
            .Set("overflows", Overflows)
            .Set("pipelines", pipelines)
            .Set("tx", TxTelemetry())
            .Set("source", RadioTelemetry());
    }

    private static LeasePipeline? LeaseOf(IRadioListener l) => l is ILeaseListener ll ? ll.Lease : null;

    public string[] GetLog(int lines) => LogBook.Tail(lines);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // The transmitter first: nothing radiates past a closing radio.
        var tx = OpenTx;
        if (tx != null && !tx.IsFinished)
            try { tx.Stop(false, 0); } catch (Exception ex) { Log("transmission stop at close: " + ex.Message); }
        var all = new List<IRadioListener>();
        foreach (var ctx in _sides)
        {
            lock (ctx.ListenersLock)
            {
                all.AddRange(ctx.Listeners);
                ctx.Listeners.Clear();
                ctx.Sweeps.Clear();
            }
        }
        foreach (var d in all.Select(l => (object?)LeaseOf(l) ?? l).Distinct().OfType<IDisposable>())
            try { d.Dispose(); } catch { }
        try { CloseRadio(); } catch (Exception ex) { Log("close: " + ex.Message); }
    }
}
