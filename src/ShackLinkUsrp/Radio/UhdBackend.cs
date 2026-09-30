// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using ShackLinkUsrp.Host;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Uhd;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Radio;

/// <summary>How the UHD backend opens its radio (the sidecar's command line).</summary>
internal sealed record UhdBackendOptions
{
    public required string Serial { get; init; }
    public string? UhdDirectory { get; init; }
    public string? ImagesDirectory { get; init; }
    public string? FpgaPath { get; init; }
    public string? ExtraDeviceArgs { get; init; }
    public bool AllowKnownCodecFault { get; init; }
    /// <summary>What crosses USB: "sc16" or "sc8" (docs/usrp_b210_support.md section 9.8).</summary>
    public string OtwFormat { get; init; } = "sc16";
    public double WideRateHz { get; init; } = 10e6;
    public long LoHz { get; init; } = 100_000_000;
    /// <summary>RF LO offset from the centre, compensated in the FPGA DDC, so the DC spike is off-centre.</summary>
    public long DcOffsetHz { get; init; }
}

/// <summary>
/// A B2xx through UHD's C API. The open runs on a background thread (a cold board loads firmware and
/// a bitstream, ~16 s); the recv thread fills the wide ring and never blocks on a consumer. UHD's
/// own answers are the truth: every rate and frequency is read back, and the capabilities are the
/// ranges UHD reports once the radio is open (clients re-read them through wait_ready).
/// </summary>
internal sealed class UhdBackend : RadioBackendBase
{
    private readonly UhdBackendOptions _opt;
    private readonly object _radioLock = new();
    private UhdUsrp? _usrp;
    private UhdRxStreamer? _streamer;
    private Thread? _recvThread;
    private volatile bool _stopRecv;
    private long _dcOffsetHz;
    private long _recvCalls, _recvSamples, _timeouts, _otherErrors, _lateFirstMs;
    private double _masterClock;
    // The transmitter (section 9.17 P4-b): UHD's ranges read at open, the caps rebuilt for every wide
    // rate, and why there is none when there is none.
    private TxCaps? _txCaps;
    private string _txWhyNot = "the radio is not open";
    private UhdMetaRange? _txFreqRange, _txGainRange;
    private string[] _txAntennas = Array.Empty<string>();
    private int _txQuantum;
    private long _txAcks, _txUnderflowEvents, _txSeqErrors, _txTimeErrors;

    public UhdBackend(SidecarLog log, UhdBackendOptions opt)
        : base(log,
               InfoFor(opt, opt.Serial, "B2xx", ""),
               InitialCaps(opt),
               ringSamples: 1 << 24,
               initialLoHz: opt.LoHz,
               initialRateHz: opt.WideRateHz)
    {
        _opt = opt;
        _dcOffsetHz = opt.DcOffsetHz;
    }

    /// <summary>Rates this sidecar offers on this OS and USB format (section 9.1 D7): sc16 is clean to
    /// 10 MS/s on Windows, sc8 to 20; nothing above is offered on Windows until it is measured clean.</summary>
    private static double[] OfferedRates(string otw)
    {
        bool windows = OperatingSystem.IsWindows();
        if (!windows) return new[] { 2e6, 5e6, 10e6, 20e6, 30.72e6, 40e6, 56e6 };
        return otw == "sc8" ? new[] { 2e6, 5e6, 10e6, 20e6 } : new[] { 2e6, 5e6, 10e6 };
    }

    private static RadioCaps InitialCaps(UhdBackendOptions opt) => new()
    {
        // Preliminary until the radio opens; replaced by UHD's own ranges then.
        FreqMinHz = 50_000_000,
        FreqMaxHz = 6_000_000_000,
        GainMinDb = 0,
        GainMaxDb = 76,
        GainStepDb = 1,
        WideRatesHz = OfferedRates(opt.OtwFormat),
        LeaseDecimMin = 2,
        LeaseDecimMax = 4096,
        MaxLeases = 8,
        MaxPanoramas = 4,
        MaxPanoramaBins = 1 << 18,
        Antennas = new[] { "TX/RX", "RX2" },
        Agc = true,
        BandwidthMinHz = 200e3,
        BandwidthMaxHz = 56e6,
    };

    /// <summary>Who the radio is, as every hello and wait_ready states it - before the open and after it.
    /// <c>known_codec_fault</c> is this sidecar's command line (CRTU200's A3): a client that joined a
    /// sidecar somebody else started has no other way to learn that its levels are untrustworthy.</summary>
    private static RadioInfo InfoFor(UhdBackendOptions opt, string serial, string product, string name) => new()
    {
        Serial = serial,
        Product = product,
        Name = name,
        Source = RadioSources.Uhd,
        KnownCodecFault = opt.AllowKnownCodecFault,
    };

    // The samples UHD already had in flight when a setter returned were taken at the old settings - a
    // retune's frequency, a gain, an antenna alike.
    protected override long SettleSamples => (long)(WideRateHz * 0.005);

    protected override void OpenRadio()
    {
        UhdLoader.Load(new UhdLoadOptions
        {
            UhdDirectory = _opt.UhdDirectory,
            AllowKnownCodecFault = _opt.AllowKnownCodecFault,
            ImagesDirectory = _opt.ImagesDirectory,
        }, Log);
        if (_opt.AllowKnownCodecFault)
            Log("KNOWN-FAULT OPT-IN: the CODEC loopback test is bypassed; this board's receive samples are NOT trustworthy for measurement.");

        // The board is chosen HERE, by exact string, from an UNFILTERED find: UHD matches serials as
        // hexadecimal numbers and cannot be asked for one that is not (UhdDeviceSelection, 9.20).
        var found = UhdUsrp.Find("type=b200");
        var plan = UhdDeviceSelection.Plan(_opt.Serial, found);
        if (plan.Refused) throw new InvalidOperationException(plan.Refusal);
        if (plan.Note.Length > 0) Log(plan.Note);

        var args = plan.Args;
        if (!string.IsNullOrWhiteSpace(_opt.FpgaPath)) args += $",fpga={_opt.FpgaPath}";
        if (!string.IsNullOrWhiteSpace(_opt.ExtraDeviceArgs)) args += "," + _opt.ExtraDeviceArgs;
        Log($"opening {args} (a cold board loads firmware and the bitstream first)");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var usrp = UhdUsrp.Open(args);
        Log($"opened in {sw.Elapsed.TotalSeconds:0.0} s");

        var info = usrp.GetRxInfo();
        // Is it the board that was asked for? Checked whenever the board states a serial; REQUIRED when it was
        // opened without one (another B2xx plugged in between the find and the open is the case this closes).
        var wrongBoard = UhdDeviceSelection.OpenedSerialRefusal(_opt.Serial, info.MboardSerial, plan.MustConfirmSerial);
        if (wrongBoard.Length > 0)
        {
            try { usrp.Dispose(); } catch { }
            throw new InvalidOperationException(wrongBoard);
        }
        Info = InfoFor(_opt,
                       string.IsNullOrEmpty(info.MboardSerial) ? _opt.Serial : info.MboardSerial,
                       string.IsNullOrEmpty(info.MboardId) ? "B2xx" : info.MboardId,
                       info.MboardName);
        var freq = usrp.GetRxFrequencyRange();
        var gain = usrp.GetRxGainRange();
        var bw = usrp.GetRxBandwidthRange();
        var ants = usrp.GetRxAntennas();
        Caps = new RadioCaps
        {
            FreqMinHz = (long)Math.Ceiling(freq.Start),
            FreqMaxHz = (long)Math.Floor(freq.Stop),
            GainMinDb = gain.Start,
            GainMaxDb = gain.Stop,
            GainStepDb = gain.Step,
            WideRatesHz = OfferedRates(_opt.OtwFormat),
            LeaseDecimMin = 2,
            LeaseDecimMax = 4096,
            MaxLeases = 8,
            MaxPanoramas = 4,
            MaxPanoramaBins = 1 << 18,
            Antennas = ants.ToArray(),
            Agc = true,
            BandwidthMinHz = bw.Start,
            BandwidthMaxHz = bw.Stop,
        };

        lock (_radioLock) _usrp = usrp;
        double rate = usrp.SetRxRate(_opt.WideRateHz);
        var tune = usrp.TuneRx(_opt.LoHz, _dcOffsetHz);
        double g = usrp.SetRxGain(30);
        string ant = ants.Contains("RX2") ? usrp.SetRxAntenna("RX2") : usrp.GetRxAntenna();
        double bandwidth = usrp.SetRxBandwidth(Math.Min(rate, bw.Stop));
        _masterClock = usrp.GetMasterClockRate();
        UpdateState(s =>
        {
            s.WideRateHz = rate;
            s.LoHz = (long)Math.Round(tune.ReadbackHz);
            s.GainDb = g;
            s.Antenna = ant;
            s.BandwidthHz = bandwidth;
            s.DcOffsetHz = _dcOffsetHz;
        }, announce: false);
        Log($"rate {rate:0} S/s (master clock {_masterClock:0}), LO {tune.ReadbackHz:0} Hz, gain {g} dB, {ant}, bandwidth {bandwidth:0} Hz, otw {_opt.OtwFormat}");
        OpenTransmitter(usrp, rate);
        StartStreaming();
    }

    // ------------------------------------------------------------ the transmitter (section 9.17 P4-b)

    /// <summary>
    /// The TX chain, set up once as the radio opens and BEFORE the receiver streams. An admitted rate is
    /// set first, because creating a TX streamer re-applies whatever TX rate UHD holds (get_tx_stream's
    /// rate update) and so marks it as set; then one streamer is made and freed, which proves the chain
    /// can be driven and states UHD's own quantum - while it exists the TX chain is powered, and with no
    /// receiver yet there is nothing for that to disturb. Nothing is sent. A failure here leaves the
    /// radio receive-only and says why; it never fails the open.
    /// </summary>
    private void OpenTransmitter(UhdUsrp usrp, double wideRate)
    {
        try
        {
            if (usrp.TxChannelCount < 1)
            {
                NoTransmitter("UHD reports no TX channel on this radio");
                return;
            }
            double[] admitted = AdmittedTxRates(usrp, wideRate);
            if (admitted.Length == 0)
            {
                NoTransmitter($"no TX rate from {TxRatePlan.MinTxRateHz:0} to {TxRatePlan.MaxTxRateHz:0} S/s divides {wideRate:0} S/s exactly at a {_masterClock:0} Hz clock");
                return;
            }
            double txRate = usrp.SetTxRate(admitted[0]);
            if (txRate != admitted[0])
            {
                NoTransmitter($"the DUC read back {txRate} S/s for the admitted {admitted[0]} S/s");
                return;
            }
            int quantum;
            using (var probe = usrp.CreateTxStreamer()) quantum = probe.MaxSamplesPerPacket;
            double clock = usrp.GetMasterClockRate();
            if (clock != _masterClock)
                Log($"UNEXPECTED: the master clock moved from {_masterClock:0} to {clock:0} Hz while the transmitter was set up");
            _masterClock = clock;
            var tune = usrp.TuneTx(usrp.GetTxFrequency(), UhdTxTunePolicy.LoOnTarget);
            double gain = usrp.GetTxGain();
            string ant = usrp.GetTxAntenna();
            _txFreqRange = usrp.GetTxFrequencyRange();
            _txGainRange = usrp.GetTxGainRange();
            _txAntennas = usrp.GetTxAntennas().ToArray();
            _txQuantum = quantum;
            RebuildTxCaps(usrp, wideRate);
            InitTxState(s =>
            {
                s.CenterHz = (long)Math.Round(tune.ReadbackHz);
                s.LoHz = (long)Math.Round(tune.ActualRfHz);
                s.LoOffsetHz = 0;
                s.RateHz = txRate;
                s.GainDb = gain;
                s.Antenna = ant;
            }, txRate);
            Log($"transmitter: {_txFreqRange}, gain {_txGainRange}, {ant}, quantum {quantum} samples, TX rates " +
                $"{string.Join("/", (_txCaps?.RatesHz ?? Array.Empty<double>()).Select(r => (r / 1e3).ToString("0.###", CultureInfo.InvariantCulture)))} kS/s " +
                $"at a {clock:0} Hz clock; at {tune.ReadbackHz:0} Hz, {gain} dB, {txRate:0} S/s - nothing is sent until a client asks");
        }
        catch (Exception ex)
        {
            NoTransmitter($"the TX chain could not be set up: {ex.Message}");
        }
    }

    private void NoTransmitter(string why)
    {
        Volatile.Write(ref _txCaps, null);
        _txWhyNot = why;
        Log("this radio will not transmit: " + why);
    }

    /// <summary>UHD's DUC host rates at the clock it reads back, through the rules both radios share.</summary>
    private double[] AdmittedTxRates(UhdUsrp usrp, double wideRate) =>
        TxRatePlan.Admitted(usrp.GetTxRates().Ranges.Select(r => r.Start), usrp.GetMasterClockRate(), wideRate);

    private void RebuildTxCaps(UhdUsrp usrp, double wideRate)
    {
        if (_txFreqRange == null || _txGainRange == null) return;
        double[] rates = AdmittedTxRates(usrp, wideRate);
        Volatile.Write(ref _txCaps, rates.Length == 0 ? null : StateGeneratedSources(new TxCaps
        {
            FreqMinHz = (long)Math.Ceiling(_txFreqRange.Start),
            FreqMaxHz = (long)Math.Floor(_txFreqRange.Stop),
            GainMinDb = _txGainRange.Start,
            GainMaxDb = _txGainRange.Stop,
            GainStepDb = _txGainRange.Step,
            RatesHz = rates,
            Antennas = _txAntennas,
            MaxMs = 0,               // the server's ceiling
            QuantumSamples = _txQuantum,
            MaxLoOffsetHz = TxRatePlan.MaxLoOffsetHz(usrp.GetMasterClockRate(), rates),
        }));
        if (rates.Length == 0) _txWhyNot = $"no admitted TX rate divides the wide rate {wideRate:0} S/s";
    }

    protected override TxCaps? RadioTxCaps() => Volatile.Read(ref _txCaps);

    protected override double ApplyTxRate(double hz)
    {
        var u = Usrp;
        double before = u.GetMasterClockRate();
        double eff = u.SetTxRate(hz);
        double after = u.GetMasterClockRate();
        if (after != before)
        {
            // Impossible by the admission rule (an admitted rate divides the wide rate); said out loud,
            // because the receiver's samples around this instant were taken at a clock nobody chose.
            Log($"UNEXPECTED: the master clock moved from {before:0} to {after:0} Hz when the TX rate was set to {hz:0} S/s");
            _masterClock = after;
        }
        return eff;
    }

    protected override double ApplyTxGain(double db) => Usrp.SetTxGain(db);

    protected override (long CenterHz, long LoHz) ApplyTxTune(long centerHz, long loOffsetHz)
    {
        var r = Usrp.TuneTx(centerHz, loOffsetHz == 0 ? UhdTxTunePolicy.LoOnTarget : UhdTxTunePolicy.LoOffset, loOffsetHz);
        return ((long)Math.Round(r.ReadbackHz), (long)Math.Round(r.ActualRfHz));
    }

    protected override (long CenterHz, long LoHz, string Retune) ApplyTxPark(long centerHz, long loOffsetHz, RadioTransmission open)
    {
        var u = Usrp;
        long lo = GetTxState().LoHz;
        if (Math.Abs(centerHz - lo) <= TxRatePlan.DspReachHz(_masterClock, open.Grant.RateHz))
        {
            var r = u.TuneTx(centerHz, UhdTxTunePolicy.KeepLo);
            return ((long)Math.Round(r.ReadbackHz), (long)Math.Round(r.ActualRfHz), TxWords.RetuneDsp);
        }
        var t = u.TuneTx(centerHz, loOffsetHz == 0 ? UhdTxTunePolicy.LoOnTarget : UhdTxTunePolicy.LoOffset, loOffsetHz);
        return ((long)Math.Round(t.ReadbackHz), (long)Math.Round(t.ActualRfHz), TxWords.RetuneRf);
    }

    protected override double TxTickHz => _masterClock;

    /// <summary>A stepped source's carrier: rf MANUAL at the LO and dsp AUTO when the synthesizer moves, rf NONE
    /// (the DUC's CORDIC alone) otherwise - the park's two shapes. Untimed: the write lands when it arrives
    /// (section 9.18 P4b2), so the caller states an upper bound.</summary>
    protected override (long CenterHz, long LoHz) ApplyTxStepTune(long carrierHz, long loHz, bool moveLo)
    {
        var r = moveLo ? Usrp.TuneTx(carrierHz, UhdTxTunePolicy.LoOffset, loHz - carrierHz)
                       : Usrp.TuneTx(carrierHz, UhdTxTunePolicy.KeepLo);
        return ((long)Math.Round(r.ReadbackHz), (long)Math.Round(r.ActualRfHz));
    }

    protected override RadioTransmission OpenTransmission(TxStartRequest request, TxGrant grant, TxStepPlan? plan, TxSignal? signal)
    {
        var u = Usrp;
        // Its own streamer, for its life (item 7). Making it powers the TX chain, which takes the AD9361
        // through ALERT and back under the receiver: a front-end change before a sample is sent.
        var streamer = u.CreateTxStreamer();
        AnnounceFrontEndChange();
        if (GetState().Antenna == "TX/RX")
            Log("the receiver is on TX/RX: for every burst from TRX1 the board hears RX2 instead (b200_impl.cpp update_atrs, " +
                "the full-duplex state) - its samples during the burst come from another port (section 9.17 item 3)");
        try
        {
            return new UhdTransmission(this, request, grant, plan, signal, streamer, NoteTxAsync);
        }
        catch
        {
            streamer.Dispose();
            throw;
        }
    }

    private void NoteTxAsync(UhdAsyncEventCode code)
    {
        if ((code & UhdAsyncEventCode.BurstAck) != 0) Interlocked.Increment(ref _txAcks);
        if ((code & (UhdAsyncEventCode.Underflow | UhdAsyncEventCode.UnderflowInPacket)) != 0) Interlocked.Increment(ref _txUnderflowEvents);
        if ((code & (UhdAsyncEventCode.SeqError | UhdAsyncEventCode.SeqErrorInBurst)) != 0)
        {
            // Packets lost between the host and the radio: the burst on the air is spliced.
            if (Interlocked.Increment(ref _txSeqErrors) == 1) Log("TX: packets were lost between the host and the radio (SEQ_ERROR) - the burst on the air is spliced");
        }
        if ((code & UhdAsyncEventCode.TimeError) != 0) Interlocked.Increment(ref _txTimeErrors);
    }

    private void StartStreaming()
    {
        UhdUsrp usrp;
        lock (_radioLock) usrp = _usrp ?? throw new InvalidOperationException("the radio is not open");
        _streamer = usrp.CreateRxStreamer(_opt.OtwFormat);
        _stopRecv = false;
        _recvThread = new Thread(RecvLoop) { IsBackground = true, Name = "UhdRecv", Priority = ThreadPriority.Highest };
        _recvThread.Start();
    }

    private void StopStreaming()
    {
        _stopRecv = true;
        _recvThread?.Join(3000);
        _recvThread = null;
        try { _streamer?.Dispose(); } catch (Exception ex) { Log("streamer close: " + ex.Message); }
        _streamer = null;
    }

    private void RecvLoop()
    {
        var s = _streamer!;
        UhdUsrp.TrySetThreadPriority();
        var buf = new short[Math.Max(s.MaxSamplesPerPacket, 1) * 8 * 2];
        int silentMs = 0;
        var started = System.Diagnostics.Stopwatch.StartNew();
        bool first = true;
        try
        {
            s.StartContinuous();
            while (!_stopRecv)
            {
                int got = s.Recv(buf, first ? 3.0 : 0.25, out var md);
                Interlocked.Increment(ref _recvCalls);
                if (got > 0)
                {
                    if (first)
                    {
                        Interlocked.Exchange(ref _lateFirstMs, started.ElapsedMilliseconds);
                        first = false;
                    }
                    Ring.Write(buf.AsSpan(0, got * 2));
                    Interlocked.Add(ref _recvSamples, got);
                    silentMs = 0;
                }
                switch (md.ErrorCode)
                {
                    case UhdRxErrorCode.None:
                        break;
                    case UhdRxErrorCode.Overflow:
                        // UHD restarts a continuous stream by itself after an overflow; the samples the
                        // FPGA dropped are gone, and every header after this one says how many overflows.
                        AddOverflows(1);
                        break;
                    case UhdRxErrorCode.Timeout:
                        Interlocked.Increment(ref _timeouts);
                        silentMs += first ? 3000 : 250;
                        if (silentMs >= 3000)
                        {
                            Fault($"the radio delivered no samples for {silentMs / 1000.0:0.#} s (recv timeouts) - unplugged, or the USB link failed");
                            return;
                        }
                        break;
                    default:
                        Interlocked.Increment(ref _otherErrors);
                        Log($"recv: {md.ErrorCode} ({s.LastMetadataText()})");
                        break;
                }
            }
        }
        catch (UhdException ex)
        {
            if (!_stopRecv) Fault("the radio stopped streaming: " + ex.Message);
        }
        catch (Exception ex)
        {
            if (!_stopRecv) Fault($"the receive thread failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    protected override void CloseRadio()
    {
        StopStreaming();
        UhdUsrp? u;
        lock (_radioLock)
        {
            u = _usrp;
            _usrp = null;
        }
        u?.Dispose();
    }

    private UhdUsrp Usrp
    {
        get
        {
            lock (_radioLock) return _usrp ?? throw new RadioRefusal(ErrorCodes.NotReady, "the radio is not open");
        }
    }

    protected override long ApplyLo(long hz)
    {
        var r = Usrp.TuneRx(hz, Interlocked.Read(ref _dcOffsetHz));
        return (long)Math.Round(r.ReadbackHz);
    }

    protected override double ApplyRate(double hz)
    {
        // A rate change re-plans UHD's master clock; stop the stream around it so no packet at the
        // old rate is written after the pipelines were told the new one.
        StopStreaming();
        var u = Usrp;
        // The TX rate UHD holds joins the LCM its automatic clock is chosen from (section 9.17 item 4):
        // one that does not divide the NEW wide rate is moved first, to the lowest rate that wide rate
        // admits at the clock it would get alone - so the receiver gets the clock it would have had with
        // no transmitter. (No transmission is open: the server refuses set_rate under one, TX_ACTIVE.)
        double txRate = GetTxState().RateHz;
        if (Volatile.Read(ref _txCaps) != null && txRate > 0 && !TxRules.DividesWideRate(txRate, hz))
        {
            double tick = TxRatePlan.ModelTickHz(hz);
            double[] plan = TxRatePlan.Admitted(TxRatePlan.DucHostRates(tick), tick, hz);
            if (plan.Length > 0)
            {
                double moved = u.SetTxRate(plan[0]);
                InitTxState(s => s.RateHz = moved, moved);
                Log($"TX rate moved from {txRate:0} to {moved:0} S/s: {txRate:0} does not divide the new wide rate {hz:0} S/s");
            }
        }
        double eff = u.SetRxRate(hz);
        _masterClock = u.GetMasterClockRate();
        if (_txFreqRange != null) RebuildTxCaps(u, eff);
        StartStreaming();
        return eff;
    }

    protected override double ApplyGain(double db) => Usrp.SetRxGain(db);

    protected override void ApplyAgc(bool on) => Usrp.SetRxAgc(on);

    protected override string ApplyAntenna(string name) => Usrp.SetRxAntenna(name);

    protected override double ApplyBandwidth(double hz) => Usrp.SetRxBandwidth(hz);

    protected override JsonArray RadioSettings()
    {
        var u = Usrp;
        var sources = u.GetClockSources();
        return new JsonArray()
            .Add(new JsonObject().Set("key", "clock_source").Set("type", "enum").Set("value", u.GetClockSource())
                .Set("options", sources.ToArray()))
            .Add(new JsonObject().Set("key", "dc_offset_hz").Set("type", "int").Set("value", Interlocked.Read(ref _dcOffsetHz)
                .ToString(CultureInfo.InvariantCulture)).Set("text", "RF LO offset from the centre, compensated in the FPGA DDC"))
            .Add(new JsonObject().Set("key", "dc_correction").Set("type", "bool").Set("value", "auto"))
            .Add(new JsonObject().Set("key", "iq_balance").Set("type", "bool").Set("value", "auto"))
            .Add(new JsonObject().Set("key", "otw_format").Set("type", "enum").Set("value", _opt.OtwFormat).Set("read_only", true));
    }

    protected override string ApplySetting(string key, string value)
    {
        var u = Usrp;
        switch (key)
        {
            case "clock_source":
                if (!u.GetClockSources().Contains(value))
                    throw new RadioRefusal(ErrorCodes.OutOfRange, $"clock source \"{value}\" is not one of {string.Join(", ", u.GetClockSources())}");
                u.SetClockSource(value);
                return u.GetClockSource();
            case "dc_offset_hz":
                if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long off) ||
                    Math.Abs(off) > WideRateHz / 4)
                    throw new RadioRefusal(ErrorCodes.OutOfRange, $"dc_offset_hz must be an integer within +/-{WideRateHz / 4:0} Hz");
                Interlocked.Exchange(ref _dcOffsetHz, off);
                SetLo(LoHz); // retune through the one path that tells the pipelines
                UpdateState(s => s.DcOffsetHz = off, announce: false);
                return off.ToString(CultureInfo.InvariantCulture);
            case "dc_correction":
                u.SetRxDcOffsetEnabled(ParseBool(key, value));
                return value;
            case "iq_balance":
                u.SetRxIqBalanceEnabled(ParseBool(key, value));
                return value;
            case "otw_format":
                throw new RadioRefusal(ErrorCodes.Unsupported, "otw_format is chosen when the sidecar starts (--otw sc16|sc8)");
            default:
                throw new RadioRefusal(ErrorCodes.NotFound, $"no setting \"{key}\"");
        }
    }

    private static bool ParseBool(string key, string value) => value switch
    {
        "true" or "on" or "1" => true,
        "false" or "off" or "0" => false,
        _ => throw new RadioRefusal(ErrorCodes.BadRequest, $"{key} is true or false, not \"{value}\""),
    };

    protected override JsonObject RadioTelemetry() => new JsonObject()
        .Set("kind", "uhd")
        .Set("uhd_version", UhdLoader.Version)
        .Set("uhd_path", UhdLoader.LoadedPath)
        .Set("otw_format", _opt.OtwFormat)
        .Set("master_clock_hz", _masterClock)
        .Set("recv_calls", Interlocked.Read(ref _recvCalls))
        .Set("recv_samples", Interlocked.Read(ref _recvSamples))
        .Set("timeouts", Interlocked.Read(ref _timeouts))
        .Set("other_errors", Interlocked.Read(ref _otherErrors))
        .Set("first_samples_ms", Interlocked.Read(ref _lateFirstMs))
        .Set("known_fault_opt_in", _opt.AllowKnownCodecFault)
        .Set("tx_available", Volatile.Read(ref _txCaps) != null)
        .Set("tx_why_not", Volatile.Read(ref _txCaps) != null ? "" : _txWhyNot)
        .Set("tx_quantum_samples", _txQuantum)
        .Set("tx_burst_acks", Interlocked.Read(ref _txAcks))
        .Set("tx_underflow_events", Interlocked.Read(ref _txUnderflowEvents))
        .Set("tx_seq_errors", Interlocked.Read(ref _txSeqErrors))
        .Set("tx_time_errors", Interlocked.Read(ref _txTimeErrors));
}
