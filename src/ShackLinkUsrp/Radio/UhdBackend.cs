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

        var found = UhdUsrp.Find($"type=b200,serial={_opt.Serial}");
        if (found.Count == 0)
        {
            var any = UhdUsrp.Find("type=b200");
            throw new InvalidOperationException(any.Count == 0
                ? $"no B2xx is attached (UHD found none); is serial {_opt.Serial} plugged into a USB 3 port?"
                : $"no B2xx with serial {_opt.Serial} is attached; UHD sees: {string.Join(" | ", any)}");
        }
        if (found.Count > 1)
            throw new InvalidOperationException(
                $"{found.Count} devices answer serial {_opt.Serial}; clones ship one EEPROM serial - re-serialise one with b2xx_fx3_utils (hazard 22)");

        var args = $"type=b200,serial={_opt.Serial}";
        if (!string.IsNullOrWhiteSpace(_opt.FpgaPath)) args += $",fpga={_opt.FpgaPath}";
        if (!string.IsNullOrWhiteSpace(_opt.ExtraDeviceArgs)) args += "," + _opt.ExtraDeviceArgs;
        Log($"opening {args} (a cold board loads firmware and the bitstream first)");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var usrp = UhdUsrp.Open(args);
        Log($"opened in {sw.Elapsed.TotalSeconds:0.0} s: {found[0]}");

        var info = usrp.GetRxInfo();
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
        StartStreaming();
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
        double eff = Usrp.SetRxRate(hz);
        _masterClock = Usrp.GetMasterClockRate();
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
        .Set("known_fault_opt_in", _opt.AllowKnownCodecFault);
}
