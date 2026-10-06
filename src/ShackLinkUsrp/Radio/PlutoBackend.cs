// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ShackLinkIio.Ad936x;
using ShackLinkIio.Protocol;
using ShackLinkIio.Usb;
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

    /// <summary>Over the board's OWN USB pipes (<c>--iiod usb:&lt;serial&gt;</c>, ShackLink docs/plutosdr_support.md section 9.26 D14):
    /// no address and no browse; a board that cannot be opened there is refused in the words of the one that opens it.</summary>
    public bool Usb { get; init; }

    /// <summary>The USB route for a serial; null = the board's own, through WinUSB (<see cref="IiodUsbRoute.ForSerial"/>). The gate's
    /// seam: the fake board's USB face.</summary>
    public Func<string, IiodRoute>? OpenUsbRoute { get; init; }

    /// <summary>Which route a network address is (section 9.26 D11); null = the library's classifier (<see cref="PlutoRoutes"/>).
    /// The gate's seam: a fake board on 127.0.0.1 stands for any of them.</summary>
    public Func<string, PlutoRoute>? ClassifyAddress { get; init; }

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
/// section 9, P2): RX A - and RX B when the board states a second chain (9.32: RX2A, its own gain, AGC and input, ONE centre with
/// RX A) - the wide stream pumped from ONE <see cref="PlutoRxStream"/> into each side's ring. It loads
/// no UHD. Every value it states is the board's read-back.
/// <list type="bullet">
/// <item><b>The board is found by its serial</b> (B6): the stored address first, then one <c>_iio._tcp</c> browse
/// matched on the serial; a board stating another serial, or none, is never adopted and never written.</item>
/// <item><b>SafeIdle is the FIRST write</b> (B10) - the board transmits from power-on - and is written again at
/// close and on every fault path except one: a board another program changed is that program's (B11).</item>
/// <item><b>A FIR another program loaded is found and switched off, out loud</b> (section 9.8; the owner's choice,
/// 2026-09-30): this backend's rates assume none.</item>
/// <item><b>An RX LO another program powered down is found and powered up, out loud</b> (ShackLink
/// docs/plutosdr_support.md 9.29): SoapyPlutoSDR leaves it down whenever its receive stream closes, and a receiver with its LO
/// down hears only a DC spike. Once streaming, its going down is another program's write (B11).</item>
/// <item><b>Levels have one owner</b> (section 9.2 item 8): the 12-bit samples are scaled by 16 into sc16 full
/// scale before the ring, so no client carries a Pluto correction.</item>
/// <item><b>The loss oracle is a polled register</b> (section 9.2 item 5), read after every buffer; a board without
/// debugfs states <c>overflow_oracle: false</c> instead of a counter of 0 that would be a lie.</item>
/// <item><b>A third program is watched for</b> (B11): a value this sidecar did not write faults the radio, and no
/// retry may fight it.</item>
/// <item><b>The radio slot's wide lease</b> (B4): <c>lease_decim_min = 1</c>.</item>
/// <item><b>The transmitter</b> (section 9.15, P4): set_tx's centre and gain are HELD and written at the key-up - the first
/// whole buffer - read back before the LO is powered; every transmission is streamed IQ (B5, never the DDS) through the TX
/// DMA on a connection of its own, and every end is SafeIdle (B10). Its timebase is the host's (section 9.2 item 2).</item>
/// <item><b>The TX DMA runs at the WIDE rate, always</b> (section 9.21, the owner's Q4): the owner's board's DMA consumes about 1.5 x the
/// wide rate at the wide/8 it lists and reads back (section 9.20), so a source a client sends at wide/8 is interpolated x8 by the sender
/// (<c>TxUpsampler</c>).</item>
/// <item><b>Every rate the board states is offered, receive and transmit, over every route</b> (ShackLink docs/plutosdr_support.md
/// 9.32 Q21, the owner's rule): a link that cannot carry a rate says so through what already counts it - the overflow oracle, the
/// leases' breaks, the TX underflows and a starved transmission's end - never through a ceiling measured on one board on one PC. What
/// was measured is one line of the open's log (<see cref="MeasuredWords"/>).</item>
/// </list>
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
    // How the board is reached (section 9.26 D11), known once FindBoard has found it.
    private volatile int _route = (int)PlutoRoute.Ethernet;
    // ShackLink docs/plutosdr_support.md 9.32: the receive chains the board states (2 booted 2R2T), and the sides the ONE stream
    // carries now - RX A is the AD936x's chain 1 (RX1A), RX B its chain 2 (RX2A).
    private int _rxChains = 1;
    private volatile string[] _streamSides = RxSides.OnlyA;

    private static int ChainOf(string side) => side == RxSides.B ? Ad936xInputs.Rx2 : Ad936xInputs.Rx1;

    private static string SideOfChain(int chain) => chain == Ad936xInputs.Rx2 ? RxSides.B : RxSides.A;

    /// <summary>9.32 D21: no per-side DDC in the stock HDL - one RX LO, so both sides sit on ONE centre, and it is the LO.</summary>
    protected override bool SidesShareOneCenter => true;

    /// <summary>The AD936x's data interface runs at the wide rate whatever the sides (the clock IS the rate: no DDC to decimate
    /// from a wider one). Never 0: a 2R2T board states its rates with both chains running.</summary>
    protected override double ClockForSides(int sides, double wideRateHz) => wideRateHz;

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
        Antennas = new[] { "RX1A" },
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

        // Section 9.29: the RX LO is FOUND powered down when another program left it so (SoapyPlutoSDR does, whenever its
        // receive stream closes), and a receiver with its LO down hears only a DC spike. Powered up, read back, said.
        if (radio.PowerUpRxLo())
            Log("RX LO: found powered DOWN - another program left it so (SoapyPlutoSDR powers it down when its receive stream closes) - " +
                "powered up, read back");

        var caps = radio.ReadCapabilities();
        bool oracle = radio.HasLossRegisters;
        var offered = OfferedRates(caps.SampleRateRange.Min, caps.SampleRateRange.Max);
        // 9.32 Q19/Q20: the board's own statement of its chains, and each side's inputs named as the AD936x names them.
        _rxChains = caps.RxChains;
        var inputsA = Ad936xInputs.Offered(Ad936xInputs.Rx1, caps.RxPorts);
        var inputsB = _rxChains > 1 ? Ad936xInputs.Offered(Ad936xInputs.Rx2, caps.RxPorts) : Array.Empty<string>();
        // Section 9.32 Q21: opened at the rate it was asked for, whatever the route - what this link loses there is counted, never guessed.
        double openAt = _opt.WideRateHz;
        double rate, coreRate;
        long lo;
        double gain, bandwidth, gainB = double.NaN;
        bool agcB = false;
        string antenna, port;
        lock (_boardLock)
        {
            rate = radio.SetSampleRate(openAt);
            coreRate = radio.ReadRxCoreRateHz();
            if (Math.Abs(coreRate - rate) > 0.5)
                throw new InvalidOperationException($"the RX DMA delivers {coreRate:0} S/s where the AD936x runs at {rate:0} S/s: its /8 decimator could not be bypassed");
            lo = radio.SetRxLo(_opt.LoHz);
            gain = radio.SetRxGain(30);
            bandwidth = radio.SetRxBandwidth(Math.Clamp(BandwidthForRate(rate, null), caps.RxBandwidthRange.Min, caps.RxBandwidthRange.Max));
            // READ, never written: the R3 bench requires the open never write the port (9.30 F30).
            port = radio.ReadRxPort();
            antenna = Ad936xInputs.Name(Ad936xInputs.Rx1, port) ?? port;
            if (_rxChains > 1)
            {
                // RX B as the board has it (9.32 D22) - read, nothing written: its slot sets what it wants when it opens.
                gainB = radio.ReadRxGainDb(Ad936xInputs.Rx2);
                agcB = radio.ReadGainControlMode(Ad936xInputs.Rx2) != Ad936xNames.AgcManual;
            }
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
            WideRatesHz = offered,
            LeaseDecimMin = 1,
            LeaseDecimMax = 4096,
            MaxLeases = 8,
            MaxPanoramas = 4,
            MaxPanoramaBins = 1 << 18,
            Antennas = inputsA,
            SideAntennas = _rxChains > 1 ? new Dictionary<string, string[]> { [RxSides.A] = inputsA, [RxSides.B] = inputsB } : null,
            Agc = caps.GainControlModes.Contains(Ad936xNames.AgcSlowAttack) && caps.GainControlModes.Contains(Ad936xNames.AgcManual),
            BandwidthMinHz = caps.RxBandwidthRange.Min,
            BandwidthMaxHz = caps.RxBandwidthRange.Max,
            // 9.32 Q19: RX B is offered exactly when the board states a second chain; every rate it states, with two as with one (Q21).
            RxSides = _rxChains > 1 ? RxSides.All : RxSides.OnlyA,
            DualRatesHz = _rxChains > 1 ? offered : null,
            OverflowOracle = oracle,
            // Which route this is (section 9.26 D15). No max_wide_rate_hz: a ceiling would be one board's measurement (9.32 Q21).
            Link = PlutoRoutes.Words(Route),
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
        if (_rxChains > 1)
            UpdateSideB(gainDb: gainB, agc: agcB, antenna: Ad936xInputs.Name(Ad936xInputs.Rx2, port) ?? port, centerHz: lo);

        // The transmitter's statements, read APART (section 9.15): a board whose TX side cannot be read still receives, and
        // says it does not transmit.
        try
        {
            OpenTransmitter(radio, radio.ReadTxCapabilities());
        }
        catch (Exception ex) when (ex is IiodErrorException || ex is PlutoRefusalException)
        {
            Volatile.Write(ref _txCaps, null);
            _txWhyNot = "the board's transmitter could not be read: " + ex.Message;
            Log("this board will not transmit: " + _txWhyNot);
        }

        // The flag is sticky (section 9.8: found set from earlier use): cleared now, so the first one counted is ours.
        bool? was = radio.ReadAndClearRxOverflow();
        Log($"{rate:0} S/s (ADC clock {(double.IsNaN(_adcClockHz) ? "not stated" : _adcClockHz.ToString("0", CultureInfo.InvariantCulture) + " Hz")}), " +
            $"LO {lo} Hz, gain {gain} dB (manual), {antenna}, bandwidth {bandwidth:0} Hz; rates offered " +
            string.Join("/", Caps.WideRatesHz.Select(r => (r / 1e6).ToString("0.###", CultureInfo.InvariantCulture))) + " MS/s");
        Log($"route: {PlutoRoutes.Words(Route)} ({radio.Route.Name}) - every rate the board states is offered; {MeasuredWords(Route)}");
        Log(_rxChains > 1
            ? $"receive: two chains (the board was booted 2R2T) - RX A is RX1 ({string.Join("/", inputsA)}), RX B is RX2 ({string.Join("/", inputsB)}, " +
              $"{(agcB ? "its AGC on" : $"{gainB} dB manual")}); ONE centre for both (no per-side DDC), and the input letter is one setting for both"
            : $"receive: one chain (the board was booted 1R1T) - RX A is RX1 ({string.Join("/", inputsA)}); no RX B");
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
        if (_opt.Usb) return FindBoardOverUsb();
        var tried = new List<string>();
        string? hintHost = null;
        int hintPort = 0;
        if (!string.IsNullOrWhiteSpace(_opt.AddressHint))
        {
            if (!PlutoDiscovery.TryParseHostPort(_opt.AddressHint, out hintHost, out hintPort))
                throw new InvalidOperationException($"\"{_opt.AddressHint}\" is not host[:port]");
            var r = TryOpen(hintHost, hintPort, out string why);
            if (r != null) return Reached(r);
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
                return Reached(r);
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

    /// <summary>A board reached over the network: which route that is (section 9.26 D11) - its Ethernet port, or its own USB network
    /// (RNDIS), which the ADAPTER tells and the address does not.</summary>
    private PlutoRadio Reached(PlutoRadio r)
    {
        var classify = _opt.ClassifyAddress ?? PlutoRoutes.ClassifyAddress;
        PlutoRoute route;
        try { route = classify(r.Host); }
        catch { route = PlutoRoute.Ethernet; }
        _route = (int)(route == PlutoRoute.UsbNetwork ? PlutoRoute.UsbNetwork : PlutoRoute.Ethernet);
        return r;
    }

    /// <summary>
    /// Section 9.26 D14: the board over its OWN USB pipes, by its serial - no address, no browse. A board that is not on USB, whose IIO
    /// interface has no usable driver, or that another program holds, is refused in the words of the one that opens it (WinUsbLocator,
    /// the device: "held over USB by another program" is the foreign rule's, D6, and ShackLink never retries it).
    /// </summary>
    private PlutoRadio FindBoardOverUsb()
    {
        IiodRoute route = (_opt.OpenUsbRoute ?? IiodUsbRoute.ForSerial)(_opt.Serial);
        try
        {
            var r = PlutoRadio.Open(route, _opt.Serial, _opt.ConnectTimeoutMs);
            _route = (int)PlutoRoute.UsbPipes;
            Log($"found board {_opt.Serial} over its own USB pipes ({route.Name}): no address, no browse, no firewall rule");
            return r;
        }
        catch (Exception ex) when (ex is IiodException || ex is PlutoRefusalException || ex is IOException)
        {
            throw new InvalidOperationException($"board {_opt.Serial} over its own USB pipes ({route.Name}): {ex.Message}");
        }
    }

    /// <summary>How the board is reached (section 9.26 D11).</summary>
    internal PlutoRoute Route => (PlutoRoute)_route;

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

    /// <summary>ONE stream for the sides streaming now (9.32 D23): RX1, RX2 or both on one DMA - one instant on both.</summary>
    private void StartStreaming()
    {
        var radio = Radio;
        double rate = WideRateHz;
        var sides = _streamSides;
        var s = PlutoRxStream.Open(radio, BufferSamplesFor(rate), _opt.BuffersCount, rate, sides.Select(ChainOf));
        Volatile.Write(ref _samplesPerBuffer, s.SamplesPerBuffer);
        _stream = s;
        _stopPump = false;
        _pump = new Thread(() => Pump(radio, s)) { IsBackground = true, Name = "PlutoRx", Priority = ThreadPriority.Highest };
        _pump.Start();
        Log($"streaming {string.Join(" and ", s.Chains.Select(Ad936xInputs.ChainWords))} ({RxSides.Words(sides)}): {s.SamplesPerBuffer} samples " +
            $"({1000.0 * s.SamplesPerBuffer / rate:0.0} ms) x {s.BuffersCount} kernel buffers, mask {s.Mask}");
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
        // Each chain the stream carries into ITS side's ring: RX1 -> RX A, RX2 -> RX B (9.32 D23).
        var rings = s.Chains.Select(c => RingOf(SideOfChain(c))).ToArray();
        var buf = new byte[s.BytesPerBuffer];     // once, reused: never a per-buffer allocation
        var samples = MemoryMarshal.Cast<byte, short>(buf.AsSpan(0, s.BytesPerBuffer));
        // Two chains: an instant is RX1's I/Q then RX2's, split here into one array a side, allocated once with the buffer.
        short[]? one = rings.Length > 1 ? new short[2 * s.SamplesPerBuffer] : null;
        short[]? two = rings.Length > 1 ? new short[2 * s.SamplesPerBuffer] : null;
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
                if (one == null || two == null) rings[0].Write(samples);
                else
                {
                    for (int k = 0, j = 0; k + 3 < samples.Length; k += 4, j += 2)
                    {
                        one[j] = samples[k];
                        one[j + 1] = samples[k + 1];
                        two[j] = samples[k + 2];
                        two[j + 1] = samples[k + 3];
                    }
                    // Both rings take the same instants, in lockstep: radio position n is one instant on both sides.
                    rings[0].Write(one);
                    rings[1].Write(two);
                }
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
            if (radio.ReadRxLoPoweredDown())
                return $"the RX LO reads powered DOWN, and this sidecar receives on it ({Ad936xNames.Phy} OUTPUT {Ad936xNames.RxLo} {Ad936xNames.Powerdown} = 1)";
            var q = radio.ReadQuietState();
            if (_txExpect is { } keyed)
            {
                // KEYED (section 9.15 item 10): the transmitter as this sidecar wrote it, and nothing else on.
                var off = TxNotAsKeyed(q, keyed.LoHz, keyed.AttenuationDb);
                if (off.Count > 0) return "while this sidecar transmits, " + string.Join("; ", off);
            }
            else
            {
                var quiet = q.NotSafeIdle();
                if (quiet.Count > 0) return "while this sidecar holds it idle, " + string.Join("; ", quiet);
            }
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

    /// <summary>What is not as THIS sidecar keyed it (section 9.15 item 10): the TX LO powered at the LO it wrote, TX1 at the
    /// attenuation it wrote, every other TX path at the maximum, every DDS scale 0. Empty = as keyed.</summary>
    private static List<string> TxNotAsKeyed(PlutoState q, long loHz, double attenuationDb)
    {
        var r = new List<string>();
        if (q.TxLoPoweredDown)
            r.Add($"the TX LO reads powered DOWN, and this sidecar keyed it ({Ad936xNames.Phy} OUTPUT {Ad936xNames.TxLo} {Ad936xNames.Powerdown} = 1)");
        else if (q.TxLoHz != loHz)
            r.Add($"the TX LO reads {PlutoValues.Mhz(q.TxLoHz)} MHz, and this sidecar keyed it at {PlutoValues.Mhz(loHz)} MHz " +
                  $"({Ad936xNames.Phy} OUTPUT {Ad936xNames.TxLo} {Ad936xNames.Frequency})");
        for (int i = 0; i < q.TxAttenuationDb.Length; i++)
        {
            double want = i == 0 ? attenuationDb : Ad936xNames.MaxTxAttenuationDb;
            if (Math.Abs(q.TxAttenuationDb[i] - want) > Ad936xNames.TxAttenuationStepDb / 2)
                r.Add($"TX{i + 1} is attenuated {PlutoValues.Num(q.TxAttenuationDb[i])} dB, and this sidecar set {PlutoValues.Num(want)} dB " +
                      $"({Ad936xNames.Phy} OUTPUT voltage{i} {Ad936xNames.HardwareGain})");
        }
        foreach (var t in q.Tones)
            if (t.Scale != 0)
                r.Add($"the DDS tone {t.Label} is at scale {PlutoValues.Num(t.Scale)}, {PlutoValues.Mhz(t.FrequencyHz)} MHz from the TX LO " +
                      $"({Ad936xNames.TxCore} OUTPUT {t.Channel} {Ad936xNames.Scale})");
        return r;
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

    /// <summary>SafeIdle on the control connection, or on a re-opened one when that one broke; what happened, in the log. Under the
    /// board's lock, as <see cref="TxEndSafeIdle"/> is: a broken control connection sends the transmission's end AND the receive pump's
    /// fault here at once, and on USB two re-opens at once cannot both have pipe 0 (section 9.26 D17) - the second would be refused
    /// and say SafeIdle was never written, when the first had written it.</summary>
    private void TrySafeIdle(string when)
    {
        try
        {
            SafeIdleReport report;
            lock (_boardLock)
            {
                var r = _radio;
                if (r == null) return;
                if (!r.Control.IsBroken) report = r.SafeIdle();
                else
                {
                    // Section 9.26 D17: the control connection again - over USB its OWN pipe, never a fourth (a keyed radio holds all three).
                    using var fresh = r.ReopenControl(2000);
                    report = fresh.SafeIdle();
                }
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

    /// <summary>The centre every streaming side sits on (9.32 D21): ONE, and it is the RX LO. A side this board does not have, or
    /// two centres that differ, are refused in words before anything is written.</summary>
    private long OneCenter(IReadOnlyDictionary<string, long> centers)
    {
        if (centers.Count == 0) throw new RadioRefusal(ErrorCodes.BadRequest, "no side to tune");
        if (centers.ContainsKey(RxSides.B) && _rxChains < 2)
            throw new RadioRefusal(ErrorCodes.Unsupported, "this board states one receive chain (it was booted 1R1T): it has no RX B");
        long want = centers.Values.First();
        if (centers.Values.Any(c => c != want))
            throw new RadioRefusal(ErrorCodes.OutOfRange,
                $"this board's two sides share ONE centre - one RX LO, no per-side DDC: {RxSides.Words(centers.Keys)} cannot be at " +
                $"{string.Join(" and ", centers.Values)} Hz; move the radio (set_lo with no side), which moves both");
        return want;
    }

    protected override (long RfLoHz, IReadOnlyDictionary<string, long> Centers) ApplyTuning(long rfLoHz, IReadOnlyDictionary<string, long> centers)
    {
        long want = OneCenter(centers);
        long got = Board(r =>
        {
            long lo = r.SetRxLo(want);
            _expRxLo = lo;
            return lo;
        });
        ReadGainAfterMove();
        return (got, centers.Keys.ToDictionary(k => k, _ => got));
    }

    /// <summary>The AD936x's gain table changes with the band (section 9.2 item 7): after a move, a manual gain the new
    /// band cannot hold reads back as what it now is, and the state says so - each streaming side's, on its own chain.</summary>
    private void ReadGainAfterMove()
    {
        bool changed = false;
        foreach (var side in _streamSides)
        {
            float before = RowGainOf(side);
            if (float.IsNaN(before)) continue;      // its AGC decides
            int chain = ChainOf(side);
            double now = Board(r => r.ReadRxGainDb(chain));
            if (Math.Abs(now - before) < 1e-3) continue;
            if (side == RxSides.B) UpdateSideB(gainDb: now);
            else UpdateState(s => s.GainDb = now, announce: false);
            changed = true;
            Log($"{RxSides.Words(side)}'s gain reads {now} dB after the move (it was {before} dB): the new band's range is {Board(r => r.ReadRxGainRange(chain))}");
        }
        if (changed) UpdateState(_ => { }, announce: true);
    }

    /// <summary>
    /// 9.32 D23: stream exactly <paramref name="sides"/> - the ONE stream made again with the chains they are (mask 03, 0c or 0f).
    /// Refused before anything stops when the board has no RX B or the centres differ; a side switched on starts where the other
    /// stands (its ring aligned while nothing writes), and the base tells the side that stays it was reconfigured.
    /// </summary>
    protected override (long RfLoHz, IReadOnlyDictionary<string, long> Centers, double ClockHz) ApplyRxSides(
        IReadOnlyList<string> sides, long rfLoHz, IReadOnlyDictionary<string, long> centers, double clockHz)
    {
        var want = RxSides.Canonical(sides) ?? RxSides.OnlyA;
        if (RxSides.Contains(want, RxSides.B) && _rxChains < 2)
            throw new RadioRefusal(ErrorCodes.Unsupported, "this board states one receive chain (it was booted 1R1T): it has no RX B");
        OneCenter(centers);
        StopStreaming();
        var before = _streamSides;
        _streamSides = want;
        (long RfLoHz, IReadOnlyDictionary<string, long> Centers) t;
        try
        {
            // Nothing writes the rings now: a side switched on starts at the position the other has reached.
            AlignRings(want);
            t = ApplyTuning(rfLoHz, centers);
        }
        catch
        {
            _streamSides = before;
            RestartAfter("the side change was refused");
            throw;
        }
        RestartAfter("the side change");
        return (t.RfLoHz, t.Centers, WideRateHz);
    }

    /// <summary>The stream made again after a reconfiguration; a stream that cannot be made again ends the radio, said.</summary>
    private void RestartAfter(string what)
    {
        if (_closed || GetState().Readiness != RadioStates.Ready) return;
        try
        {
            StartStreaming();
        }
        catch (Exception ex)
        {
            RadioDied($"the stream could not be made again after {what}: {ex.Message}");
        }
    }

    protected override double ApplyRate(double hz)
    {
        // ONE clock chain: the write moves RX and TX together (section 9.2 item 1). The server refuses set_rate under a
        // transmission (TX_ACTIVE); this is the backend's own net, because here the wide rate IS the TX DMA's parent clock.
        if (OpenTx is { IsFinished: false })
            throw new RadioRefusal(ErrorCodes.TxActive, "the wide rate cannot change while a transmission is open: it is the transmitter's clock too");
        // The stream is made again around it, so no buffer at the old rate is written after the pipelines were told the new one.
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
                    // The TX DMA's list follows the wide rate (section 9.2 item 1): re-read, the DMA put at the NEW wide rate
                    // (section 9.21), the configured source rate kept when it is still offered, else the lowest - and said, as the
                    // UHD backend says its move.
                    if (_txLoRange.Max > 0)
                    {
                        double was = GetTxState().RateHz;
                        double txRate = RefreshTxCaps(Radio, was);
                        if (txRate > 0) InitTxState(s => s.RateHz = txRate, txRate);
                        if (Math.Abs(txRate - was) >= 0.5)
                            Log($"TX rate moved from {was:0} to {txRate:0} S/s: at {now:0} S/s the source rates are {string.Join("/", (Volatile.Read(ref _txCaps)?.RatesHz ?? Array.Empty<double>()).Select(r => r.ToString("0", CultureInfo.InvariantCulture)))} S/s, the TX DMA at the wide rate");
                    }
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

    /// <summary>A side's gain, on ITS chain (9.32 D22): RX A is the AD936x's RX1, RX B its RX2.</summary>
    protected override double ApplyGain(string side, double db) => Board(r => r.SetRxGain(db, ChainOf(side)));

    protected override void ApplyAgc(string side, bool on)
    {
        int chain = ChainOf(side);
        Board(r => r.SetGainControlMode(on ? Ad936xNames.AgcSlowAttack : Ad936xNames.AgcManual, chain));
        if (on) return;
        // Off, the gain the AGC left is the manual gain now in force: its read-back.
        double g = Board(r => r.ReadRxGainDb(chain));
        if (side == RxSides.B) UpdateSideB(gainDb: g);
        else UpdateState(s => s.GainDb = g, announce: false);
    }

    /// <summary>
    /// A side's input (9.32 Q20): RX1A..RX1C on RX A, RX2A..RX2C on RX B - the old spelling (a balanced port's own name) taken on the
    /// side it arrives on. Another side's input is refused in words naming that side. The LETTER is one setting for both chains
    /// (F34): written only when it changes (a board whose device tree locks it refuses in the library's words, 9.30), and then the
    /// other side is moved to the same letter, told, and said.
    /// </summary>
    protected override string ApplyAntenna(string side, string name)
    {
        int chain = ChainOf(side);
        string offered = string.Join(", ", SideRules.AntennasFor(Caps, side));
        if (!Ad936xInputs.TryParse(name, chain, out int asked, out string port))
            throw new RadioRefusal(ErrorCodes.OutOfRange, $"\"{name}\" is no input of this board: {RxSides.Words(side)} offers {offered}");
        if (asked != chain)
            throw new RadioRefusal(ErrorCodes.OutOfRange,
                $"{name.Trim().ToUpperInvariant()} is {RxSides.Words(SideOfChain(asked))}'s input (the AD936x's {Ad936xInputs.ChainWords(asked)}): " +
                $"{RxSides.Words(side)} offers {offered}");
        string before = Board(r => r.ReadRxPort());
        string now = before == port ? before : Board(r => r.SetRxPort(port));
        string mine = Ad936xInputs.Name(chain, now) ?? now;
        if (now != before && _rxChains > 1)
        {
            string other = RxSides.Other(side);
            string theirs = Ad936xInputs.Name(ChainOf(other), now) ?? now;
            if (other == RxSides.B) UpdateSideB(antenna: theirs);
            else UpdateState(s => s.Antenna = theirs, announce: false);
            AnnounceFrontEndChange();
            Log($"{RxSides.Words(side)} on {mine}: the AD936x selects A, B or C for both chains at once, so {RxSides.Words(other)} is on {theirs} now");
        }
        return mine;
    }

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
            .Add(new JsonObject().Set("key", "iiod").Set("type", "string").Set("value", r.Route.Name).Set("read_only", true)
                .Set("text", $"where this board was reached, over {PlutoRoutes.Words(Route)} - a handle, not its identity"))
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
            .Set("address", r == null ? "" : r.Route.Name)
            .Set("route", PlutoRoutes.Token(Route))
            .Set("rx_chains", _rxChains)
            .Set("rx_mask", _stream?.Mask ?? "")
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
            .Set("tx_available", Volatile.Read(ref _txCaps) != null)
            .Set("tx_why_not", _txWhyNot)
            .Set("tx_key_ups", Interlocked.Read(ref _txKeyUps))
            .Set("tx_key_ups_refused", Interlocked.Read(ref _txRefusedKeyUps))
            .Set("tx_dma_rate_hz", _txDmaRateHz)
            .Set("tx_interp_clipped", Volatile.Read(ref _lastTx)?.InterpolatorClipped ?? 0)
            .Set("tx_keyed_expect", _txExpect is { } e ? $"LO {e.LoHz} Hz, {e.AttenuationDb} dB" : "idle (SafeIdle)");
    }

    // ------------------------------------------------------------ the transmitter (ShackLink docs/plutosdr_support.md 9.15)

    /// <summary>The TX DMA's kernel buffers: four, as RX (section 9.15 item 5).</summary>
    private int TxBuffersCount => _opt.BuffersCount;

    /// <summary>How long a busy TX DMA is waited for before it is another program's: the time iiod may take to process the
    /// close of THIS sidecar's previous writer (an abort a moment before).</summary>
    private const int TxBusyGraceMs = 500;

    private TxCaps? _txCaps;
    // The rate the TX DMA was set to and read back at - the wide rate (section 9.21); 0 = it transmits nothing.
    private double _txDmaRateHz;
    private string _txWhyNot = "the board is not open";
    private PlutoRange _txLoRange, _txBandwidthRange;
    private string[] _txPorts = Array.Empty<string>();
    // What the transmitter's watch expects while KEYED (section 9.15 item 10): null = idle, and the watch expects SafeIdle.
    private (long LoHz, double AttenuationDb)? _txExpect;
    private long _txKeyUps, _txRefusedKeyUps;

    /// <summary>Section 9.15 item 3 / Q1: a Pluto cannot apply set_tx's centre and gain while idle - its TX LO is powered down
    /// and its attenuation at the maximum - so it HOLDS them (state.tx.held) and writes them at the key-up.</summary>
    protected override bool HoldsTxUntilKeyed => true;

    /// <summary>Called from <see cref="OpenRadio"/> once the board's rate is set: the transmitter's statements, the TX DMA put at
    /// its LOWEST listed rate (FPGA-only: nothing at RF), and the state in force - the LO powered down, the gain 0 (the maximum
    /// attenuation), nothing held yet.</summary>
    private void OpenTransmitter(PlutoRadio radio, PlutoTxCapabilities caps)
    {
        _txLoRange = caps.TxLoRange;
        _txBandwidthRange = caps.TxBandwidthRange;
        _txPorts = caps.TxPorts;
        double txRate = RefreshTxCaps(radio, keepRateHz: 0);
        InitTxState(s =>
        {
            s.CenterHz = 0;
            s.LoHz = 0;
            s.LoOffsetHz = 0;
            s.RateHz = txRate;
            s.GainDb = 0;
            s.Antenna = _txPorts.Length > 0 ? radio.ReadState().TxPort.Trim() : "";
            s.Held = new TxHeld();
        }, txRate);
        if (_txCaps != null)
            Log($"transmitter: TX LO {PlutoValues.Mhz(_txLoRange.Min)}-{PlutoValues.Mhz(_txLoRange.Max)} MHz, attenuation 0-89.75 dB in 0.25 dB, " +
                $"source rates {string.Join("/", _txCaps.RatesHz.Select(r => (r / 1e6).ToString("0.###", CultureInfo.InvariantCulture)))} MS/s (set to {txRate / 1e6:0.###}), " +
                $"the TX DMA at the wide rate {_txDmaRateHz / 1e6:0.###} MS/s ALWAYS - a wide/8 source is interpolated x8 here (section 9.21) - " +
                "keying at any wide rate the board states; timebase host; set_tx's centre and gain are HELD and " +
                "written at the key-up - nothing is sent until a client asks");
    }

    /// <summary>
    /// What was MEASURED over a route, on the owner's board and PC (ShackLink docs/plutosdr_support.md 9.12, 9.22, 9.25) - words for the
    /// open's log and nothing else. Never a ceiling (9.32 Q21, the owner's rule): another board, another PC or another link carries
    /// something else, so every rate the board states is offered and what a link loses is counted where it is lost.
    /// </summary>
    internal static string MeasuredWords(PlutoRoute route) => route switch
    {
        PlutoRoute.UsbPipes => "measured on the owner's board over its USB pipes: receive clean to 7 MS/s, transmit and receive together to 4 (9.25)",
        PlutoRoute.UsbNetwork => "measured on the owner's board over its USB network: receive clean to 5 MS/s, transmit and receive together to 2.5 (9.25)",
        _ => "measured on the owner's board over Ethernet: receive clean to 10 MS/s, transmit and receive together to 2.5 (9.12, 9.22)",
    } + " - the overflow and underflow counts say what THIS link loses";

    /// <summary>The source rates a transmission may run at: the wide rate itself, and wide/8, which THIS sidecar interpolates x8
    /// (section 9.21) - the TX DMA only ever runs at the wide rate. Its samples per source sample; 0 for any other.</summary>
    internal static int InterpolationFor(double sourceRateHz, double wideRateHz)
    {
        if (!(sourceRateHz > 0) || !(wideRateHz > 0)) return 0;
        double f = wideRateHz / sourceRateHz;
        int k = (int)Math.Round(f);
        return (k == 1 || k == 8) && Math.Abs(f - k) < 1e-9 * k ? k : 0;
    }

    /// <summary>
    /// The transmitter as the board lists it NOW (its DMA's rates follow the wide rate), and the TX DMA put at the WIDE rate - always,
    /// the one setting the owner's board's DMA runs right at (section 9.20: at the wide/8 it lists and reads back it consumes about
    /// 1.5 x the wide rate). The source rates offered are the DMA's own list, each of which this sidecar reaches exactly (x1 or x8,
    /// <see cref="InterpolationFor"/>); <c>quantum_samples</c> stays in SOURCE samples. Returns the source rate in force -
    /// <paramref name="keepRateHz"/> while it is still offered, else the lowest; 0 and no caps when the board lists none, or its DMA will
    /// not run at the wide rate (it then transmits nothing, said).
    /// </summary>
    private double RefreshTxCaps(PlutoRadio radio, double keepRateHz)
    {
        double[] listed, rates;
        double wide, dma;
        lock (_boardLock)
        {
            listed = radio.ReadTxCoreRatesHz();
            wide = radio.ReadSampleRateHz();
            rates = listed.Where(r => InterpolationFor(r, wide) > 0).OrderBy(r => r).ToArray();
            string? why = listed.Length == 0 ? "the board's TX DMA states no sample rate"
                        : _txLoRange.Max <= 0 ? "the board states no TX LO range"
                        : !rates.Any(r => InterpolationFor(r, wide) == 1) ? $"the board's TX DMA does not list the wide rate {PlutoValues.Num(wide)} S/s (it lists {string.Join(", ", listed.Select(PlutoValues.Num))})"
                        : null;
            if (why == null)
            {
                // Section 9.21 (Q4): the DMA at the wide rate, read back - never at the wide/8 a client may send at.
                dma = radio.SetTxCoreRate(wide);
                if (Math.Abs(dma - wide) >= 0.5) why = $"the board's TX DMA reads back {PlutoValues.Num(dma)} S/s for the wide rate {PlutoValues.Num(wide)} S/s";
            }
            else dma = 0;
            if (why != null)
            {
                Volatile.Write(ref _txCaps, null);
                _txDmaRateHz = 0;
                _txWhyNot = why;
                Log("this board will not transmit: " + _txWhyNot);
                return 0;
            }
            _txDmaRateHz = dma;
        }
        double want = rates.Any(r => Math.Abs(r - keepRateHz) < 0.5) ? keepRateHz : rates[0];
        Volatile.Write(ref _txCaps, StateGeneratedSources(new TxCaps
        {
            FreqMinHz = (long)Math.Ceiling(_txLoRange.Min),
            FreqMaxHz = (long)Math.Floor(_txLoRange.Max),
            // The wire's TX gain is 89.75 + hardwaregain (section 9.2 item 4): 0 = the maximum attenuation, 89.75 = none.
            GainMinDb = 0,
            GainMaxDb = Ad936xNames.MaxTxAttenuationDb,
            GainStepDb = Ad936xNames.TxAttenuationStepDb,
            RatesHz = rates,
            Antennas = _txPorts,
            MaxMs = 0,               // the server's ceiling
            // One TX DMA buffer, in SOURCE samples at the lowest rate (the DMA's buffer holds that times its interpolation).
            QuantumSamples = TxBufferSamplesFor(dma) / InterpolationFor(rates[0], wide),
            // No DUC and no CORDIC (section 9.15 item 8): the LO sits on the carrier and no offset is offered.
            MaxLoOffsetHz = 0,
            // 9.32 D24: TX1A keys from its own DMA whether RX B streams or not - the receive stream is apart from it.
            DualRx = _rxChains > 1,
            Timebase = TxTimebases.Host,
            // No tx.max_wide_rate_hz: a key-up at any wide rate the board states (9.32 Q21); a starved over ends, said.
        }));
        _txWhyNot = "";
        return want;
    }

    /// <summary>About 25 ms of the TX DMA's rate, a power of two, at least 4,096 samples and at most 2^20 (section 9.15 item 5). Asked at
    /// the WIDE rate, the DMA's: a power of two of at least 4,096 holds a whole number of wide/8 source samples.</summary>
    internal static int TxBufferSamplesFor(double rateHz) =>
        (int)Math.Min(1 << 20, Math.Max(4096, 1L << (int)Math.Round(Math.Log2(Math.Max(4096, rateHz * 0.025)))));

    protected override TxCaps? RadioTxCaps() => Volatile.Read(ref _txCaps);

    /// <summary>The SOURCE's rate - what a client sends at, or a generator runs at - refused unless offered. Nothing is written: the TX
    /// DMA runs at the wide rate whatever the source's (section 9.21), and a wide/8 source is interpolated x8 by the sender.</summary>
    protected override double ApplyTxRate(double hz)
    {
        if (OpenTx is { IsFinished: false })
            throw new RadioRefusal(ErrorCodes.TxActive, "the TX rate cannot change while a transmission is open");
        var caps = Volatile.Read(ref _txCaps) ?? throw new RadioRefusal(ErrorCodes.Unsupported, "this board does not transmit: " + _txWhyNot);
        var hit = caps.RatesHz.Where(r => Math.Abs(r - hz) < 0.5).ToArray();
        if (hit.Length == 0)
            throw new RadioRefusal(ErrorCodes.OutOfRange,
                $"{hz:0} S/s is not a TX rate at the rate in force; this board takes {string.Join(", ", caps.RatesHz.Select(r => r.ToString("0", CultureInfo.InvariantCulture)))} S/s");
        return hit[0];
    }

    /// <summary>Idle: the gain put on the attenuation's grid and HELD (item 3). Keyed: the attenuation written and read back.</summary>
    protected override double ApplyTxGain(double db)
    {
        double att = PlutoRadio.TxAttenuationOnGrid(Ad936xNames.MaxTxAttenuationDb - db);
        if (!TxKeyedNow) return Ad936xNames.MaxTxAttenuationDb - att;
        double got = Board(r =>
        {
            double a = r.SetTxAttenuation(att);
            if (_txExpect is { } e) _txExpect = (e.LoHz, a);
            return a;
        });
        return Ad936xNames.MaxTxAttenuationDb - got;
    }

    /// <summary>Idle only (the server fixes an IQ transmission's centre for its life; a generated one PARKS): HELD, on the
    /// carrier, never written - a TX LO write may radiate while the LO is powered down (hazard 6).</summary>
    protected override (long CenterHz, long LoHz) ApplyTxTune(long centerHz, long loOffsetHz)
    {
        if (loOffsetHz != 0) throw new RadioRefusal(ErrorCodes.Unsupported, "this board places its TX LO on the carrier and offers no LO offset");
        if (centerHz < _txLoRange.Min || centerHz > _txLoRange.Max)
            throw new RadioRefusal(ErrorCodes.OutOfRange,
                $"{centerHz} Hz is outside this board's TX LO range {_txLoRange.Min:0}-{_txLoRange.Max:0} Hz");
        return (centerHz, centerHz);
    }

    /// <summary>A keyed generated carrier moved: there is no CORDIC, so it is the synthesizer (rf), read back, and the
    /// step's instant is when the write returned.</summary>
    protected override (long CenterHz, long LoHz, string Retune) ApplyTxPark(long centerHz, long loOffsetHz, RadioTransmission open)
    {
        if (!open.IsKeyed)
            throw new RadioRefusal(ErrorCodes.TxActive, "the transmitter is keying up; move it once it is keyed");
        long lo = Board(r =>
        {
            long l = r.RetuneTxLo(centerHz);
            if (_txExpect is { } e) _txExpect = (l, e.AttenuationDb);
            return l;
        });
        return (lo, lo, TxWords.RetuneRf);
    }

    /// <summary>No DUC: its reach is ZERO. Stated as the lowest TX rate, which makes <c>TxRatePlan.DspReachHz</c> 0, so a
    /// tuned sweep puts its LO on every carrier (one window per point).</summary>
    protected override double TxTickHz => Volatile.Read(ref _txCaps)?.RatesHz.FirstOrDefault() ?? 0;

    /// <summary>Before the key-up (tx_start parks a sweep's first LO): recorded, never written - the key-up writes the grant's
    /// LO. Keyed: the synthesizer moved, read back.</summary>
    protected override (long CenterHz, long LoHz) ApplyTxStepTune(long carrierHz, long loHz, bool moveLo)
    {
        if (!TxKeyedNow || !moveLo) return (carrierHz, loHz);
        long got = Board(r =>
        {
            long l = r.RetuneTxLo(loHz);
            if (_txExpect is { } e) _txExpect = (l, e.AttenuationDb);
            return l;
        });
        return (carrierHz + (got - loHz), got);
    }

    protected override RadioTransmission OpenTransmission(TxStartRequest request, TxGrant grant, TxStepPlan? plan, TxSignal? signal)
    {
        var caps = Volatile.Read(ref _txCaps) ?? throw new RadioRefusal(ErrorCodes.Unsupported, "this board does not transmit: " + _txWhyNot);
        if (!caps.RatesHz.Any(r => Math.Abs(r - grant.RateHz) < 0.5))
            throw new RadioRefusal(ErrorCodes.OutOfRange, $"this board does not transmit at {grant.RateHz:0} S/s at the rate in force");
        if (_foreign)
            throw new RadioRefusal(ErrorCodes.RadioFault, "another program changed this board; this sidecar transmits nothing on it");
        double wide = WideRateHz;
        int factor = InterpolationFor(grant.RateHz, wide);
        if (factor == 0)
            throw new RadioRefusal(ErrorCodes.OutOfRange, $"{grant.RateHz:0} S/s is neither the wide rate {wide:0} S/s nor an eighth of it");
        var radio = Radio;
        // Q4 (section 9.21): the TX DMA at the WIDE rate, read back, whatever the source's rate - another program may have moved it.
        double dma = Board(r => r.ReadTxCoreRateHz());
        if (Math.Abs(dma - wide) >= 0.5) dma = Board(r => r.SetTxCoreRate(wide));
        if (Math.Abs(dma - wide) >= 0.5)
            throw new RadioRefusal(ErrorCodes.RadioFault, $"the board's TX DMA reads back {dma:0} S/s for the wide rate {wide:0} S/s; nothing was keyed");
        PlutoTxStream? stream = null;
        var busySince = Stopwatch.StartNew();
        while (stream == null)
        {
            try
            {
                stream = PlutoTxStream.Open(radio, TxBufferSamplesFor(dma), TxBuffersCount, dma);
            }
            catch (IiodErrorException ex) when (ex.Status == -16)
            {
                // iiod releases a writer's buffer when it processes that writer's close, which an abort a moment ago may not
                // have reached yet: a short wait is no fight with anybody. A DMA still held after it is another program's.
                if (busySince.ElapsedMilliseconds < TxBusyGraceMs)
                {
                    Thread.Sleep(50);
                    continue;
                }
                throw new RadioRefusal(ErrorCodes.TxActive,
                    $"another program holds this board's transmitter (its TX DMA is open: -EBUSY, still after {TxBusyGraceMs} ms) - " +
                    "one writer per device; nothing was keyed");
            }
            catch (Exception ex) when (ex is IiodException || ex is PlutoRefusalException)
            {
                throw new RadioRefusal(ErrorCodes.RadioFault, "the board's TX DMA could not be opened: " + ex.Message);
            }
        }
        Log($"TX DMA open: {stream.SamplesPerBuffer} samples ({1000.0 * stream.SamplesPerBuffer / dma:0.0} ms) x {stream.BuffersCount} " +
            $"kernel buffers at the wide rate {dma:0} S/s, non-cyclic; the source at {grant.RateHz:0} S/s" +
            (factor > 1 ? $", interpolated x{factor} here" : "") + "; the LO stays down until the first buffer");
        var t = new PlutoTransmission(this, request, grant, plan, signal, stream, dma, factor);
        Volatile.Write(ref _lastTx, t);
        return t;
    }

    private PlutoTransmission? _lastTx;

    /// <summary>
    /// THE KEY-UP (section 9.15 item 4), at the transmission's first whole buffer: the HELD gain (as an attenuation) and the
    /// grant's LO written, read back and then the LO powered - <see cref="PlutoRadio.KeyTx"/>, whose every refusal leaves the
    /// board quiet. The watch expects the transmitter's state from here; <c>state.tx</c> carries the read-backs in force.
    /// </summary>
    internal void KeyUp(PlutoTransmission t)
    {
        var st = GetTxState();
        double gain = st.NextGainDb;
        if (double.IsNaN(gain))
            throw new RadioRefusal(ErrorCodes.BadRequest, "the TX gain was never set (set_tx gain_db): a transmission keys at a stated level");
        double att = PlutoRadio.TxAttenuationOnGrid(Ad936xNames.MaxTxAttenuationDb - gain);
        double bw = TxBandwidthFor(WideRateHz);
        TxKeyReport rep;
        try
        {
            rep = Board(r =>
            {
                var x = r.KeyTx(t.Grant.LoHz, att, bw);
                _txExpect = (x.LoHz, x.AttenuationDb);
                return x;
            });
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _txRefusedKeyUps);
            Log("KEY-UP REFUSED - nothing was powered, and the library left the board quiet: " + ex.Message);
            throw;
        }
        Interlocked.Increment(ref _txKeyUps);
        long landed = rep.LoHz - t.Grant.LoHz;
        UpdateTxState(s =>
        {
            s.CenterHz = t.Grant.CenterHz + landed;
            s.LoHz = rep.LoHz;
            s.GainDb = Ad936xNames.MaxTxAttenuationDb - rep.AttenuationDb;
        });
        Log($"KEY-UP: TX LO {PlutoValues.Mhz(rep.LoHz)} MHz ({landed} Hz from {PlutoValues.Mhz(t.Grant.LoHz)} - the synthesizer's landing), " +
            $"attenuation {rep.AttenuationDb} dB (gain {Ad936xNames.MaxTxAttenuationDb - rep.AttenuationDb}), bandwidth {rep.BandwidthHz:0} Hz, " +
            "each read back; the LO powered");
    }

    /// <summary>The TX analog bandwidth: the RX rule (the wide rate itself), inside the board's TX range.</summary>
    private double TxBandwidthFor(double wideRateHz)
    {
        double bw = wideRateHz;
        if (_txBandwidthRange.Max > 0) bw = Math.Min(bw, _txBandwidthRange.Max);
        if (_txBandwidthRange.Min > 0) bw = Math.Max(bw, _txBandwidthRange.Min);
        return bw;
    }

    internal bool? ReadAndClearTxUnderflow()
    {
        PlutoRadio? r;
        lock (_boardLock) r = _radio;
        return r?.ReadAndClearTxUnderflow();
    }

    /// <summary>
    /// The end of EVERY transmission (section 9.15 item 7; B10): SafeIdle, read back - on the control connection, or on it re-opened
    /// when it broke (over USB the same pipe, never a fourth: section 9.26 D17) - and the state in force idle again. "" when the board reads back quiet; otherwise the words,
    /// LOUDLY in the log. A board another program changed is LEFT as that program set it (B11).
    /// </summary>
    internal string TxEndSafeIdle(bool keyed)
    {
        UpdateTxState(s =>
        {
            s.CenterHz = 0;
            s.LoHz = 0;
            s.GainDb = 0;
        });
        if (_foreign)
        {
            lock (_boardLock) _txExpect = null;
            Log("transmission ended: no SafeIdle - another program changed this board, and it is left as that program set it");
            return "";
        }
        try
        {
            // ONE hold of the board for both: the watch may never see "idle expected" while the board is still keyed - it
            // would read our own transmission as another program's (section 9.15 item 10).
            lock (_boardLock)
            {
                _txExpect = null;
                var r = _radio;
                if (r == null) return "the board was already closed";
                if (!r.Control.IsBroken) r.SafeIdle();
                else
                {
                    // Section 9.26 D17: on USB the transmitter still holds pipes 1 and 2, so a fresh connection would be a fourth.
                    using var fresh = r.ReopenControl(2000);
                    fresh.SafeIdle();
                }
            }
            Log(keyed ? "transmission ended: SafeIdle written, the board reads back quiet (TX LO powered down, 89.75 dB)"
                      : "transmission ended before any key-up: SafeIdle written all the same, and read back quiet");
            return "";
        }
        catch (Exception ex)
        {
            string text = $"SafeIdle did NOT take after the transmission: {ex.Message} - the board may still be transmitting";
            Log("LOUD: " + text);
            return text;
        }
    }
}
