// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ShackLinkIio.Ad936x;
using ShackLinkIio.Protocol;
using ShackLinkUsrp.Client;
using ShackLinkUsrp.Host;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Radio;

/// <summary>How the Pluto backend reaches its board (the sidecar's command line: <c>--iiod</c>, <c>--serial</c>).</summary>
internal sealed record PlutoBackendOptions
{
    /// <summary>The board's <c>hw_serial</c>, lower-case: the IDENTITY (ShackLink docs/plutosdr_support.md section 9.1 B6).</summary>
    public required string Serial { get; init; }

    /// <summary><c>host[:port]</c> where the board was last reached - a HINT, tried first; null = none, browse at once.</summary>
    public string? AddressHint { get; init; }

    /// <summary>The rate the board is OPENED at - the sidecar's choice, never the board's own (section 9.9 item 2).</summary>
    public double WideRateHz { get; init; } = 10e6;

    public long LoHz { get; init; } = 100_000_000;

    /// <summary>The <c>_iio._tcp</c> browse a stale hint falls back to; null = the library's own (one browse). The gate's seam.</summary>
    public Func<IReadOnlyList<(string Host, int Port)>>? Browse { get; init; }

    public int ConnectTimeoutMs { get; init; } = 3000;
    public int BrowseTimeoutMs { get; init; } = 2000;

    /// <summary>The foreign-writer watch's full check, in ms (section 9.1 B11: once a second).</summary>
    public int WatchIntervalMs { get; init; } = 1000;

    /// <summary>Kernel buffers (<c>SET BUFFERS_COUNT</c>): 4, the configuration P1 measured clean for ten minutes at 10 MS/s.</summary>
    public int BuffersCount { get; init; } = 4;

    /// <summary>Samples per kernel buffer; 0 = about 33 ms of the rate, a power of two, at most 2^20 (P1's IiodProbe rule).</summary>
    public int BufferSamples { get; init; }
}

/// <summary>
/// A Pluto-class board (an AD936x behind iiod) as a radio of this sidecar (ShackLink docs/plutosdr_support.md
/// section 9, P2): ONE receive side, the wide stream pumped from <see cref="PlutoRxStream"/> into the ring. It loads
/// no UHD. Every value it states is the board's read-back.
/// <list type="bullet">
/// <item><b>The board is found by its serial</b> (B6): the stored address first, then one <c>_iio._tcp</c> browse
/// matched on the serial; a board stating another serial, or none, is never adopted and never written.</item>
/// <item><b>SafeIdle is the FIRST write</b> (B10) - the board transmits from power-on - and is written again at
/// close and on every fault path except one: a board another program changed is that program's (B11).</item>
/// <item><b>A FIR another program loaded is found and switched off, out loud</b> (section 9.8; the owner's choice,
/// 2026-09-30): this backend's rates assume none.</item>
/// <item><b>Levels have one owner</b> (section 9.2 item 8): the 12-bit samples are scaled by 16 into sc16 full
/// scale before the ring, so no client carries a Pluto correction.</item>
/// <item><b>The loss oracle is a polled register</b> (section 9.2 item 5), read after every buffer; a board without
/// debugfs states <c>overflow_oracle: false</c> instead of a counter of 0 that would be a lie.</item>
/// <item><b>A third program is watched for</b> (B11): a value this sidecar did not write faults the radio, and no
/// retry may fight it.</item>
/// <item><b>The radio slot's wide lease</b> (B4): <c>lease_decim_min = 1</c>.</item>
/// </list>
/// Transmit is P4: this backend states no transmitter.
/// </summary>
internal sealed class PlutoBackend : RadioBackendBase
{
    /// <summary>The union of the AD936x's RX gain ranges across its bands (section 9.2 item 7): what the caps
    /// state. The board states only its CURRENT band's (-3..71 at 2.4 GHz on the owner's board), and a gain is
    /// put on that band's range and grid when it is set - the reply is what was applied.</summary>
    public const double GainUnionMinDb = -10, GainUnionMaxDb = 73;

    /// <summary>The words every foreign-writer fault starts with: what ShackLink's restart reads as PERMANENT
    /// (B11; its <c>UsrpKind.PermanentPhrases</c>, P3). Change it there in the same commit.</summary>
    public const string ForeignWriterPhrase = "another program changed this board";

    /// <summary>...and the one a second reader of its samples starts with.</summary>
    public const string ForeignReaderPhrase = "another program is reading this board";

    /// <summary>The LO's lock time as settle: DOCUMENTED, not measured (section 9.2 item 9; P0 measures it).</summary>
    private const double LoLockSeconds = 0.001;

    private readonly PlutoBackendOptions _opt;
    private readonly object _boardLock = new();
    private PlutoRadio? _radio;
    private PlutoRxStream? _stream;
    private Thread? _pump;
    private volatile bool _stopPump;
    private volatile bool _foreign;
    private volatile bool _closed;
    private int _samplesPerBuffer;
    private double _adcClockHz = double.NaN;
    private string _firFound = "";
    // What this sidecar last WROTE and read back - the foreign-writer watch compares the board to these.
    private long _expRxLo;
    private double _expRate, _expCoreRate;
    private long _buffers, _samples, _flagged, _watches, _quickWatches;
    private double _temperatureC = double.NaN;

    public PlutoBackend(SidecarLog log, PlutoBackendOptions opt)
        : base(log, InfoFor(opt.Serial, "Pluto-class board (iiod)", ""), InitialCaps(), ringSamples: 1 << 24,
               initialLoHz: opt.LoHz, initialRateHz: opt.WideRateHz)
    {
        _opt = opt;
    }

    private static RadioInfo InfoFor(string serial, string product, string name) => new()
    {
        Serial = serial,
        Product = product,
        Name = name,
        Source = RadioSources.Iiod,
        KnownCodecFault = false,
    };

    /// <summary>B9: the library's native AD936x list at or above the no-FIR floor, inside what the board states
    /// (a HARDWARE statement, never a measurement - the overflows say what is lost).</summary>
    internal static double[] OfferedRates(double boardMinHz, double boardMaxHz) =>
        B2xxRates.Native.Where(r => r >= Ad936xNames.MinRateNoFirHz && r >= boardMinHz && r <= boardMaxHz).ToArray();

    private static RadioCaps InitialCaps() => new()
    {
        // Preliminary until the board is open; replaced by its own statements then (clients re-read through wait_ready).
        FreqMinHz = 70_000_000,
        FreqMaxHz = 6_000_000_000,
        GainMinDb = GainUnionMinDb,
        GainMaxDb = GainUnionMaxDb,
        GainStepDb = 1,
        WideRatesHz = OfferedRates(Ad936xNames.MinRateNoFirHz, B2xxRates.MaxOneSideHz),
        LeaseDecimMin = 1,
        LeaseDecimMax = 4096,
        MaxLeases = 8,
        MaxPanoramas = 4,
        MaxPanoramaBins = 1 << 18,
        Antennas = new[] { "A_BALANCED" },
        Agc = true,
        BandwidthMinHz = 200e3,
        BandwidthMaxHz = 56e6,
        RxSides = RxSides.OnlyA,
        // Not known until the board is open: NOT STATED, never a guess.
        OverflowOracle = null,
    };

    private PlutoRadio Radio
    {
        get
        {
            lock (_boardLock) return _radio ?? throw new RadioRefusal(ErrorCodes.NotReady, "the board is not open");
        }
    }

    // ------------------------------------------------------------ open

    protected override void OpenRadio()
    {
        var radio = FindBoard();
        lock (_boardLock) _radio = radio;
        var id = radio.Identity;
        Log($"board: {id}");

        // B10: the FIRST write. Open wrote nothing; this quiets a transmitter the board may have had on since power-up.
        var idle = radio.SafeIdle();
        Log(idle.WasQuiet
            ? "SafeIdle: the board was already quiet (TX LO powered down, TX attenuation 89.75 dB, every DDS scale 0), read back"
            : "SafeIdle: this board was NOT quiet when the sidecar opened it - " + string.Join("; ", idle.Found) + " - and reads back quiet now");

        // Section 9.8: a FIR another program loaded is FOUND, said, and switched off - this backend's rates assume none.
        var st = radio.ReadState();
        if (st.RxFirEnabled == true || st.TxFirEnabled == true)
        {
            // The driver will not drop a FIR while the rate needs one; such a rate moves first, inside the FIR's range.
            if (st.SampleRateHz < Ad936xNames.MinRateNoFirHz)
            {
                double safe = Math.Min(Math.Max(_opt.WideRateHz, Ad936xNames.MinRateNoFirHz), st.SampleRateRange.Max);
                Log($"the board runs at {st.SampleRateHz:0} S/s, which needs its FIR: moving to {safe:0} S/s before switching the FIR off");
                radio.SetSampleRate(safe);
            }
            var fir = radio.DisableFir();
            _firFound = fir.Found;
            Log($"FIR: {fir.Found}; switched off, read back off on both chains, and the board states rates {fir.RangeAfter} again");
        }

        var caps = radio.ReadCapabilities();
        bool oracle = radio.HasLossRegisters;
        double rate, coreRate;
        long lo;
        double gain, bandwidth;
        string antenna;
        lock (_boardLock)
        {
            rate = radio.SetSampleRate(_opt.WideRateHz);
            coreRate = radio.ReadRxCoreRateHz();
            if (Math.Abs(coreRate - rate) > 0.5)
                throw new InvalidOperationException($"the RX DMA delivers {coreRate:0} S/s where the AD936x runs at {rate:0} S/s: its /8 decimator could not be bypassed");
            lo = radio.SetRxLo(_opt.LoHz);
            gain = radio.SetRxGain(30);
            bandwidth = radio.SetRxBandwidth(Math.Clamp(BandwidthForRate(rate, null), caps.RxBandwidthRange.Min, caps.RxBandwidthRange.Max));
            antenna = radio.ReadState().RxPort;
            _expRate = rate;
            _expCoreRate = coreRate;
            _expRxLo = lo;
        }
        var after = radio.ReadState();
        _adcClockHz = after.AdcClockHz;
        _temperatureC = after.TemperatureC;

        Info = InfoFor(id.Serial, id.Model.Length > 0 ? id.Model : "Pluto-class board (iiod)", "");
        Caps = new RadioCaps
        {
            FreqMinHz = (long)Math.Ceiling(caps.RxLoRange.Min),
            FreqMaxHz = (long)Math.Floor(caps.RxLoRange.Max),
            GainMinDb = GainUnionMinDb,
            GainMaxDb = GainUnionMaxDb,
            GainStepDb = caps.RxGainRange.Step > 0 ? caps.RxGainRange.Step : 1,
            WideRatesHz = OfferedRates(caps.SampleRateRange.Min, caps.SampleRateRange.Max),
            LeaseDecimMin = 1,
            LeaseDecimMax = 4096,
            MaxLeases = 8,
            MaxPanoramas = 4,
            MaxPanoramaBins = 1 << 18,
            Antennas = caps.RxPorts,
            Agc = caps.GainControlModes.Contains(Ad936xNames.AgcSlowAttack) && caps.GainControlModes.Contains(Ad936xNames.AgcManual),
            BandwidthMinHz = caps.RxBandwidthRange.Min,
            BandwidthMaxHz = caps.RxBandwidthRange.Max,
            RxSides = RxSides.OnlyA,
            OverflowOracle = oracle,
        };
        UpdateRfLo(lo);
        UpdateState(s =>
        {
            s.WideRateHz = rate;
            s.LoHz = lo;
            s.GainDb = gain;
            s.Agc = false;
            s.Antenna = antenna;
            s.BandwidthHz = bandwidth;
            s.DcOffsetHz = 0;
        }, announce: false);

        // The flag is sticky (section 9.8: found set from earlier use): cleared now, so the first one counted is ours.
        bool? was = radio.ReadAndClearRxOverflow();
        Log($"{rate:0} S/s (ADC clock {(double.IsNaN(_adcClockHz) ? "not stated" : _adcClockHz.ToString("0", CultureInfo.InvariantCulture) + " Hz")}), " +
            $"LO {lo} Hz, gain {gain} dB (manual), {antenna}, bandwidth {bandwidth:0} Hz; rates offered " +
            string.Join("/", Caps.WideRatesHz.Select(r => (r / 1e6).ToString("0.###", CultureInfo.InvariantCulture))) + " MS/s");
        Log(oracle
            ? $"loss oracle: the RX core's status register, read after every buffer{(was == true ? " (its overflow flag was already set, from before this sidecar; cleared)" : "")}"
            : "loss oracle: NONE - this board has no debugfs, so lost samples cannot be seen and the overflow counter says nothing (caps.overflow_oracle false)");
        StartStreaming();
    }

    /// <summary>
    /// The board this sidecar is for, by its SERIAL (B6, section 9.9 item 1): the stored address first; when nothing
    /// answers there, or a board with another serial does, one <c>_iio._tcp</c> browse, every answer opened read-only
    /// and matched on the serial. Nothing is written to a board that is not this one.
    /// </summary>
    private PlutoRadio FindBoard()
    {
        var tried = new List<string>();
        string? hintHost = null;
        int hintPort = 0;
        if (!string.IsNullOrWhiteSpace(_opt.AddressHint))
        {
            if (!PlutoDiscovery.TryParseHostPort(_opt.AddressHint, out hintHost, out hintPort))
                throw new InvalidOperationException($"\"{_opt.AddressHint}\" is not host[:port]");
            var r = TryOpen(hintHost, hintPort, out string why);
            if (r != null) return r;
            tried.Add($"at the stored address {_opt.AddressHint}: {why}");
            Log($"the stored address {_opt.AddressHint} did not reach board {_opt.Serial} ({why}); re-finding it by its serial with an _iio._tcp browse");
        }
        else Log($"no stored address for board {_opt.Serial}: finding it with an _iio._tcp browse");

        var found = (_opt.Browse ?? DefaultBrowse)();
        foreach (var (host, port) in found)
        {
            if (hintHost != null && string.Equals(host, hintHost, StringComparison.OrdinalIgnoreCase) && port == hintPort) continue;
            var r = TryOpen(host, port, out string why);
            if (r != null)
            {
                Log($"found board {_opt.Serial} at {host}:{port} by an _iio._tcp browse" +
                    (hintHost == null ? "" : $" - the stored address {_opt.AddressHint} is stale; the launch profile can be updated to it"));
                return r;
            }
            tried.Add($"{host}:{port}: {why}");
        }
        string browse = found.Count == 0
            ? "the _iio._tcp browse got no answer (is the board on this machine's network? On a network Windows calls Public, Windows Firewall " +
              "drops the board's answer to a program that has no inbound UDP rule)"
            : $"the _iio._tcp browse found {found.Count} board(s) and none states it";
        throw new InvalidOperationException($"no board stating hw_serial {_opt.Serial} answered: " +
                                            (tried.Count == 0 ? "" : string.Join("; ", tried) + "; ") + browse);
    }

    private IReadOnlyList<(string Host, int Port)> DefaultBrowse() =>
        PlutoDiscovery.MdnsCandidates(_opt.BrowseTimeoutMs).Select(c => (c.Host, c.Port)).ToList();

    /// <summary>One read-only open (VERSION, PRINT, the AD936x shape, the serial); null with the reason when it is not ours.</summary>
    private PlutoRadio? TryOpen(string host, int port, out string why)
    {
        why = "";
        try
        {
            return PlutoRadio.Open(host, port, _opt.Serial, _opt.ConnectTimeoutMs);
        }
        catch (PlutoRefusalException ex)
        {
            why = ex.Message;
        }
        catch (IiodException ex)
        {
            why = ex.Message;
        }
        return null;
    }

    // ------------------------------------------------------------ the stream

    /// <summary>About 33 ms of the rate, a power of two, at most 2^20 samples (P1's measured configuration).</summary>
    private int BufferSamplesFor(double rateHz) =>
        _opt.BufferSamples > 0 ? _opt.BufferSamples : (int)Math.Min(1 << 20, 1L << (int)Math.Ceiling(Math.Log2(Math.Max(1024, rateHz * 0.033))));

    private void StartStreaming()
    {
        var radio = Radio;
        double rate = WideRateHz;
        var s = PlutoRxStream.Open(radio, BufferSamplesFor(rate), _opt.BuffersCount, rate);
        Volatile.Write(ref _samplesPerBuffer, s.SamplesPerBuffer);
        _stream = s;
        _stopPump = false;
        _pump = new Thread(() => Pump(radio, s)) { IsBackground = true, Name = "PlutoRx", Priority = ThreadPriority.Highest };
        _pump.Start();
        Log($"streaming RX1: {s.SamplesPerBuffer} samples ({1000.0 * s.SamplesPerBuffer / rate:0.0} ms) x {s.BuffersCount} kernel buffers, mask {s.Mask}");
    }

    private void StopStreaming()
    {
        _stopPump = true;
        // Closing the stream's connection ends a READBUF in flight (it never takes the connection's lock).
        try { _stream?.Dispose(); } catch (Exception ex) { Log("stream close: " + ex.Message); }
        if (_pump != null && Thread.CurrentThread != _pump) _pump.Join(5000);
        _pump = null;
        _stream = null;
    }

    private void Pump(PlutoRadio radio, PlutoRxStream s)
    {
        var ring = RingOf(RxSides.A);
        var buf = new byte[s.BytesPerBuffer];     // once, reused: never a per-buffer allocation
        var samples = MemoryMarshal.Cast<byte, short>(buf.AsSpan(0, s.BytesPerBuffer));
        long lastFull = Stopwatch.GetTimestamp();
        try
        {
            while (!_stopPump)
            {
                if (!s.ReadBuffer(buf, out string mask))
                {
                    ForeignFault($"{ForeignReaderPhrase}'s samples: the board's channel mask reads {mask}, and this sidecar's is {s.Mask} - every " +
                                 "join or leave of another reader re-creates the board's buffer under this stream (section 2.9)");
                    return;
                }
                if (_stopPump) return;
                // Section 9.2 item 8: 12 significant bits, LSB-aligned (le:S12/16>>0) -> sc16 full scale. Shifting the 16-bit
                // word left by 4 is x16 for a sign-extended sample, and reads the 12 bits right if the top four were not.
                for (int i = 0; i < samples.Length; i++) samples[i] = (short)(samples[i] << 4);
                ring.Write(samples);
                Interlocked.Increment(ref _buffers);
                Interlocked.Add(ref _samples, s.SamplesPerBuffer);

                bool? flagged = radio.ReadAndClearRxOverflow();
                if (flagged == true)
                {
                    AddOverflows(1);
                    Interlocked.Increment(ref _flagged);
                }
                bool full = Stopwatch.GetElapsedTime(lastFull).TotalMilliseconds >= _opt.WatchIntervalMs;
                if (full || flagged == true)
                {
                    string? foreign = Watch(radio, full);
                    if (foreign != null)
                    {
                        ForeignFault($"{ForeignWriterPhrase} under the sidecar: {foreign}");
                        return;
                    }
                    if (full) lastFull = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (Exception ex) when (ex is IiodException || ex is PlutoRefusalException || ex is IOException)
        {
            if (!_stopPump) RadioDied($"the board stopped streaming: {ex.Message}");
        }
        catch (Exception ex)
        {
            if (!_stopPump) RadioDied($"the receive pump failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// B11: did anything this sidecar did not write change? The RX LO and both rates after every loss-flagged buffer
    /// (three reads - another program's buffer re-creation is one way a buffer is lost); with <paramref name="full"/>,
    /// once a second, also SafeIdle's facts and the FIR. Null = all as written. Skipped, never blocked, while a setter
    /// holds the board: its write and read-back are not yet in the expected values.
    /// </summary>
    private string? Watch(PlutoRadio radio, bool full)
    {
        if (!Monitor.TryEnter(_boardLock)) return null;
        try
        {
            if (_stopPump) return null;
            if (full) Interlocked.Increment(ref _watches);
            else Interlocked.Increment(ref _quickWatches);
            long rxLo = radio.ReadRxLoHz();
            if (rxLo != _expRxLo)
                return $"the RX LO reads {PlutoValues.Mhz(rxLo)} MHz, and this sidecar last set {PlutoValues.Mhz(_expRxLo)} MHz ({Ad936xNames.Phy} OUTPUT {Ad936xNames.RxLo} {Ad936xNames.Frequency})";
            double rate = radio.ReadSampleRateHz();
            if (Math.Abs(rate - _expRate) > 0.5)
                return $"the sample rate reads {PlutoValues.Num(rate)} S/s, and this sidecar last set {PlutoValues.Num(_expRate)} S/s ({Ad936xNames.Phy} INPUT {Ad936xNames.Path1} {Ad936xNames.SamplingFrequency})";
            double core = radio.ReadRxCoreRateHz();
            if (Math.Abs(core - _expCoreRate) > 0.5)
                return $"the RX DMA delivers {PlutoValues.Num(core)} S/s, and this sidecar set it to {PlutoValues.Num(_expCoreRate)} S/s ({Ad936xNames.RxCore} INPUT {Ad936xNames.Path1} {Ad936xNames.SamplingFrequency})";
            if (!full) return null;
            var quiet = radio.ReadQuietState().NotSafeIdle();
            if (quiet.Count > 0) return "while this sidecar holds it idle, " + string.Join("; ", quiet);
            var (rxFir, txFir) = radio.ReadFirState();
            if (rxFir == true || txFir == true)
                return $"a FIR was loaded (RX {(rxFir == true ? "on" : "off")}, TX {(txFir == true ? "on" : "off")}), and this sidecar switched it off at open";
            _temperatureC = radio.ReadTemperatureC();
            return null;
        }
        finally
        {
            Monitor.Exit(_boardLock);
        }
    }

    /// <summary>
    /// B11: another program wrote the board. The radio faults - terminal, in words naming the value - and the board is
    /// LEFT as that program set it: SafeIdle here would unkey a program that may be transmitting on purpose (B10's own
    /// rule for the start-up sweep), and no retry may fight it. ShackLink's restart reads the phrase as permanent (P3).
    /// </summary>
    private void ForeignFault(string text)
    {
        _foreign = true;
        Log("the board is LEFT as the other program set it: no SafeIdle is written over another program's settings");
        Fault(text);
    }

    /// <summary>The board died under the stream (the link, the board, iiod): SafeIdle is attempted (B10's fault path),
    /// then the radio faults.</summary>
    private void RadioDied(string text)
    {
        TrySafeIdle("the fault");
        Fault(text);
    }

    /// <summary>SafeIdle on the control connection, or on a fresh one when that one broke; what happened, in the log.</summary>
    private void TrySafeIdle(string when)
    {
        PlutoRadio? r;
        lock (_boardLock) r = _radio;
        if (r == null) return;
        try
        {
            SafeIdleReport report;
            if (!r.Control.IsBroken) report = r.SafeIdle();
            else
            {
                using var fresh = PlutoRadio.Open(r.Host, r.Port, _opt.Serial, 2000);
                report = fresh.SafeIdle();
            }
            Log($"SafeIdle at {when}: the board reads back quiet" + (report.WasQuiet ? "" : " (it was NOT: " + string.Join("; ", report.Found) + ")"));
        }
        catch (Exception ex)
        {
            Log($"SafeIdle at {when} could not be written: {ex.Message} - if the board was transmitting, it still is, until a power cycle or ShackLink's start-up sweep");
        }
    }

    protected override void CloseRadio()
    {
        if (_closed) return;
        _closed = true;
        StopStreaming();
        if (_foreign) Log("close: no SafeIdle - another program changed this board, and it is left as that program set it");
        else TrySafeIdle("close");
        PlutoRadio? r;
        lock (_boardLock)
        {
            r = _radio;
            _radio = null;
        }
        r?.Dispose();
    }

    // ------------------------------------------------------------ the radio's settings

    /// <summary>Section 9.2 item 9: a change leaves every buffer already filled - the kernel's, and the one iiod is
    /// sending - at the old settings, plus the LO's lock. DOCUMENTED until P0 measures it.</summary>
    protected override long SettleSamples =>
        (long)(_opt.BuffersCount + 2) * Volatile.Read(ref _samplesPerBuffer) + (long)(WideRateHz * LoLockSeconds);

    /// <summary>The rate at the AD936x's data interface - a B2xx's master clock's counterpart. The ADC clock the chain
    /// runs on is in the telemetry (<c>adc_clock_hz</c>): it is read, never computed (section 9.8).</summary>
    protected override double MasterClockHz => WideRateHz;

    /// <summary>Run a write-and-read-back with the board held, so the foreign-writer watch never compares the board
    /// against a value it has not yet been told of; a board refusal becomes a refusal in words.</summary>
    private T Board<T>(Func<PlutoRadio, T> body)
    {
        var radio = Radio;
        lock (_boardLock)
        {
            try
            {
                return body(radio);
            }
            catch (PlutoRefusalException ex)
            {
                throw new RadioRefusal(ErrorCodes.OutOfRange, ex.Message);
            }
        }
    }

    protected override (long RfLoHz, IReadOnlyDictionary<string, long> Centers) ApplyTuning(long rfLoHz, IReadOnlyDictionary<string, long> centers)
    {
        if (!centers.TryGetValue(RxSides.A, out long want))
            throw new RadioRefusal(ErrorCodes.Unsupported, "this board has one receive side, RX A");
        long got = Board(r =>
        {
            long lo = r.SetRxLo(want);
            _expRxLo = lo;
            return lo;
        });
        ReadGainAfterMove();
        return (got, new Dictionary<string, long> { [RxSides.A] = got });
    }

    /// <summary>The AD936x's gain table changes with the band (section 9.2 item 7): after a move, a manual gain the new
    /// band cannot hold reads back as what it now is, and the state says so.</summary>
    private void ReadGainAfterMove()
    {
        if (GetState().Agc) return;
        double before = GetState().GainDb;
        double now = Board(r => r.ReadRxGainDb());
        if (Math.Abs(now - before) < 1e-9) return;
        UpdateState(s => s.GainDb = now, announce: true);
        Log($"the RX gain reads {now} dB after the move (it was {before} dB): the new band's range is {Board(r => r.ReadRxGainRange())}");
    }

    protected override (long RfLoHz, IReadOnlyDictionary<string, long> Centers, double ClockHz) ApplyRxSides(
        IReadOnlyList<string> sides, long rfLoHz, IReadOnlyDictionary<string, long> centers, double clockHz)
    {
        if (sides.Count != 1 || sides[0] != RxSides.A)
            throw new RadioRefusal(ErrorCodes.Unsupported, "this board streams one receive side, RX A (section 9.2 item 6)");
        var t = ApplyTuning(rfLoHz, centers);
        return (t.RfLoHz, t.Centers, MasterClockHz);
    }

    protected override double ApplyRate(double hz)
    {
        // ONE clock chain: the write moves RX and TX together (section 9.2 item 1); the stream is made again around it,
        // so no buffer at the old rate is written after the pipelines were told the new one.
        StopStreaming();
        double got;
        try
        {
            got = Board(r =>
            {
                double rate = r.SetSampleRate(hz);
                double core = r.ReadRxCoreRateHz();
                _expRate = rate;
                _expCoreRate = core;
                if (Math.Abs(core - rate) > 0.5)
                    throw new RadioRefusal(ErrorCodes.RadioFault, $"the RX DMA delivers {core:0} S/s where the AD936x runs at {rate:0} S/s");
                return rate;
            });
            _adcClockHz = Radio.ReadState().AdcClockHz;
        }
        finally
        {
            // The stream comes back at whatever rate the board now runs at - a refused write left the old one.
            if (!_closed && GetState().Readiness == RadioStates.Ready)
            {
                try
                {
                    double now = Board(r => r.ReadSampleRateHz());
                    UpdateState(s => s.WideRateHz = now, announce: false);
                    StartStreaming();
                }
                catch (Exception ex)
                {
                    RadioDied($"the stream could not be made again after the rate change: {ex.Message}");
                }
            }
        }
        return got;
    }

    protected override double ApplyGain(string side, double db) => Board(r => r.SetRxGain(db));

    protected override void ApplyAgc(string side, bool on)
    {
        Board(r => r.SetGainControlMode(on ? Ad936xNames.AgcSlowAttack : Ad936xNames.AgcManual));
        // Off, the gain the AGC left is the manual gain now in force: its read-back.
        if (!on) UpdateState(s => s.GainDb = Board(r => r.ReadRxGainDb()), announce: false);
    }

    protected override string ApplyAntenna(string side, string name) => Board(r => r.SetRxPort(name));

    protected override double ApplyBandwidth(double hz)
    {
        var caps = Caps;
        double want = caps.BandwidthMaxHz > 0 ? Math.Clamp(hz, caps.BandwidthMinHz, caps.BandwidthMaxHz) : hz;
        return Board(r => r.SetRxBandwidth(want));
    }

    protected override JsonArray RadioSettings()
    {
        var r = Radio;
        string xo, dc, iq;
        lock (_boardLock)
        {
            xo = PlutoValues.Num(r.ReadState().XoHz);
            dc = r.ReadRxDcTracking() ? "true" : "false";
            iq = r.ReadRxQuadratureTracking() ? "true" : "false";
        }
        return new JsonArray()
            .Add(new JsonObject().Set("key", "dc_correction").Set("type", "bool").Set("value", dc)
                .Set("text", "the AD936x's RF and baseband DC-offset tracking"))
            .Add(new JsonObject().Set("key", "iq_balance").Set("type", "bool").Set("value", iq)
                .Set("text", "the AD936x's quadrature (I/Q balance) tracking"))
            .Add(new JsonObject().Set("key", "xo_correction").Set("type", "int").Set("value", xo)
                .Set("text", "the reference every frequency is computed from, in Hz (40 MHz nominal; 1 ppm is 40 Hz)"))
            .Add(new JsonObject().Set("key", "iiod").Set("type", "string").Set("value", $"{r.Host}:{r.Port}").Set("read_only", true)
                .Set("text", "where this board was reached - a handle, not its identity"))
            .Add(new JsonObject().Set("key", "firmware").Set("type", "string").Set("value", r.Identity.Firmware).Set("read_only", true));
    }

    protected override string ApplySetting(string key, string value)
    {
        switch (key)
        {
            case "dc_correction": return Board(r => r.SetRxDcTracking(ParseBool(key, value))) ? "true" : "false";
            case "iq_balance": return Board(r => r.SetRxQuadratureTracking(ParseBool(key, value))) ? "true" : "false";
            case "xo_correction":
                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long hz))
                    throw new RadioRefusal(ErrorCodes.BadRequest, $"xo_correction is a whole number of hertz, not \"{value}\"");
                return Board(r => r.SetXoCorrection(hz)).ToString(CultureInfo.InvariantCulture);
            case "iiod":
            case "firmware":
                throw new RadioRefusal(ErrorCodes.Unsupported, $"{key} is the board's to state, not a setting");
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

    protected override JsonObject RadioTelemetry()
    {
        PlutoRadio? r;
        lock (_boardLock) r = _radio;
        return new JsonObject()
            .Set("kind", "iiod")
            .Set("address", r == null ? "" : $"{r.Host}:{r.Port}")
            .Set("iiod_version", r?.Identity.Iiod?.ToString() ?? "")
            .Set("firmware", r?.Identity.Firmware ?? "")
            .Set("model", r?.Identity.Model ?? "")
            .Set("buffers", Interlocked.Read(ref _buffers))
            .Set("samples", Interlocked.Read(ref _samples))
            .Set("buffers_overflow_flagged", Interlocked.Read(ref _flagged))
            .Set("overflow_oracle", Caps.OverflowOracle)
            .Set("buffer_samples", Volatile.Read(ref _samplesPerBuffer))
            .Set("buffers_count", _opt.BuffersCount)
            .Set("settle_samples", SettleSamples)
            .Set("watch_full", Interlocked.Read(ref _watches))
            .Set("watch_quick", Interlocked.Read(ref _quickWatches))
            .Set("adc_clock_hz", double.IsNaN(_adcClockHz) ? (object?)null : _adcClockHz)
            .Set("temperature_c", double.IsNaN(_temperatureC) ? (object?)null : Math.Round(_temperatureC, 1))
            .Set("fir_found", _firFound)
            .Set("foreign_writer", _foreign)
            .Set("tx_available", false)
            .Set("tx_why_not", "transmit on a Pluto-class board is P4 (ShackLink docs/plutosdr_support.md section 9.5)");
    }

    // ------------------------------------------------------------ no transmitter (P4)

    private const string NoTx = "this board does not transmit through the sidecar yet (ShackLink docs/plutosdr_support.md section 9.5, P4)";

    protected override TxCaps? RadioTxCaps() => null;
    protected override double ApplyTxRate(double hz) => throw new RadioRefusal(ErrorCodes.Unsupported, NoTx);
    protected override double ApplyTxGain(double db) => throw new RadioRefusal(ErrorCodes.Unsupported, NoTx);
    protected override (long CenterHz, long LoHz) ApplyTxTune(long centerHz, long loOffsetHz) => throw new RadioRefusal(ErrorCodes.Unsupported, NoTx);
    protected override (long CenterHz, long LoHz, string Retune) ApplyTxPark(long centerHz, long loOffsetHz, RadioTransmission open) =>
        throw new RadioRefusal(ErrorCodes.Unsupported, NoTx);
    protected override double TxTickHz => 0;
    protected override (long CenterHz, long LoHz) ApplyTxStepTune(long carrierHz, long loHz, bool moveLo) => throw new RadioRefusal(ErrorCodes.Unsupported, NoTx);
    protected override RadioTransmission OpenTransmission(TxStartRequest request, TxGrant grant, TxStepPlan? plan, TxSignal? signal) =>
        throw new RadioRefusal(ErrorCodes.Unsupported, NoTx);
}
