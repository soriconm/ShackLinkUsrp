// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ShackLinkUsrp.Client;
using ShackLinkUsrp.Host;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Radio;

internal sealed class W1700kBackendOptions
{
    /// <summary>The radio's name in this sidecar's serial (<c>w1700k-&lt;name&gt;</c>) and rendezvous key.</summary>
    public string Name = "unit";

    /// <summary>Where the unit's export arrives: its FIXED destination (upstream's <c>export.dst_ip</c> and <c>port</c>).</summary>
    public IPEndPoint Bind = ShackLinkUsrp.Client.W1700kDefaults.BindEndPoint();

    /// <summary>The command line of upstream's controller; null = NONE: receive the stream another program drives, change nothing.</summary>
    public string? ServeCommand = W1700kServe.DefaultCommand;

    /// <summary>A gate's controller in place of <see cref="ServeCommand"/>'s process.</summary>
    public Func<W1700kServe>? ServeFactory;

    /// <summary>The wide rates offered: by default every rate the unit has (<see cref="ShackLinkUsrp.Client.W1700kDefaults.UnitRatesHz"/>;
    /// owner, 2026-10-21: no station's link decides for another's), and the operator picks. The export does not throttle, so a rate
    /// the link cannot carry is lost wholesale - 40 MS/s needs 2.5 GbE, 80 5 GbE, 320 10 GbE - and a launch profile may offer fewer.
    /// The ring is sized for the fastest offered (<see cref="RingSamplesFor"/>).</summary>
    public double[] RatesHz = ShackLinkUsrp.Client.W1700kDefaults.UnitRatesHz();

    /// <summary>The rate it opens at: the lowest the unit has, unless the caller names another offered one.</summary>
    public double WideRateHz = ShackLinkUsrp.Client.W1700kDefaults.UnitRatesHz()[0];
    public long LoHz = 2_442_000_000;

    /// <summary>Samples skipped after a change, in milliseconds of the wide rate: what is still in flight between the unit's
    /// DMA ring and this socket when the controller's reply arrives. STATED, NOT MEASURED (a retune itself is about 1 ms).</summary>
    public int SettleMs = 20;

    /// <summary>Correct the decimation's roll-off (<see cref="W1700kCondition"/>); a setting too.</summary>
    public bool Flatten = true;

    /// <summary>Take the LO's leak out of the centre; a setting too.</summary>
    public bool RemoveDc = true;

    /// <summary>A stream this sidecar started that delivers nothing this long is a fault, in words.</summary>
    public int SilenceFaultMs = 3000;

    /// <summary>Threads beside the conditioning worker that share a batch's slices (<see cref="WorkTeam"/>): by default one per eight
    /// logical processors, 1 to 3. They sleep while the worker keeps up alone.</summary>
    public int ConditionHelpers = WorkTeam.HelpersFor(Environment.ProcessorCount);

    /// <summary>How far ahead of a hole a datagram may arrive before the hole is given up (<see cref="W1700kReorder"/>); a power of two.</summary>
    public int ReorderWindow = W1700kReorder.DefaultWindow;

    public int ReceiveBufferBytes = 256 << 20;
    public int AttachTimeoutMs = 60_000;
    /// <summary>The unit's configure (a band switch, a rate change: stop, RATE, start) and start. A CLIENT waits at least this
    /// for set_lo, set_antenna and set_rate on this radio (ShackLink docs/w1700k_support.md rule 9), so it is the protocol's
    /// number, never a second one: a longer wait here would answer after the client had given up and broken its connection.</summary>
    public int LoadTimeoutMs = ProtocolConstants.W1700kConfigureMs;
    public int CommandTimeoutMs = 30_000;

    /// <summary>The command line of the unit's TRANSMIT service (C:\Dev\SDRW1700K <c>tools\w1700k-tx-serve.py</c>; ShackLink
    /// docs/w1700k_support.md "Transmit"); null = NONE, the default: this radio states no transmitter.</summary>
    public string? TxServeCommand;

    /// <summary>A gate's transmit service in place of <see cref="TxServeCommand"/>'s process.</summary>
    public Func<W1700kTxServe>? TxServeFactory;

    /// <summary>The launch's transmit-service value as the operator gave it (ShackLink docs/w1700k_support.md "The built-in transmit
    /// service", D1-D4): "" = BUILT IN, "none" = off, else a command - resolved at open by
    /// <see cref="ShackLinkUsrp.Client.W1700kDefaults.ResolveTxServe"/> into the service started, or into <c>caps.tx_absent</c>.
    /// Null = nothing is resolved here: <see cref="TxServeCommand"/> and <see cref="TxServeFactory"/> stand as given (a gate's).</summary>
    public string? TxServeSetting;

    /// <summary>The station's config (<c>--w1700k-config</c>), "" = none: the transmit service reads the router the controller reads.</summary>
    public string ConfigFile = "";

    /// <summary>The folder whose sibling <c>W1700K\</c> holds the bundled controller: this program's own.</summary>
    public string HelperDirectory = AppContext.BaseDirectory;

    /// <summary>A gate's reader of the bundled controller's <c>--version</c> in place of running it: its path in, what it printed
    /// out (null = the file is not there).</summary>
    public Func<string, string?>? ReadControllerVersion;

    /// <summary>The bundled controller's <c>--version</c> (one JSON line, no unit contact: ~110 ms measured) is given this long.</summary>
    public int VersionTimeoutMs = 5000;

    /// <summary>A transmit-service request that touches nothing slow (hello, caps, start).</summary>
    public int TxRequestTimeoutMs = 10_000;
}

/// <summary>
/// A Gemtek W1700K running h0m3us3r/w1700k-sdr (C:\Dev\SDRW1700K docs/01, docs/05 option 2): the MT7996's three receive front ends
/// as one radio, its IQ arriving as the unit's UDP export. RECEIVE ONLY - upstream's firmware keeps the transmit path disabled -
/// and ONE side. This backend's own work is the export (<see cref="W1700kExport"/>) into the wide ring; the radio is driven by
/// upstream's controller, <c>w1700k serve</c> (<see cref="W1700kServe"/>), so its firmware load, mailbox and RF programs stay
/// upstream's. Rules that are this radio's:
/// <list type="bullet">
/// <item><b>The band follows the frequency.</b> Band 0 (2.4 GHz) and bands 1 and 2 (5 and 6 GHz, overlapping) are separate front
/// ends; a tune the band in force cannot lock moves to the first band that can (band 1 before band 2), and between bands that
/// both reach a frequency the ANTENNA picks. A frequency no band locks (2.75-4.64 GHz) is refused in words naming the spans.</item>
/// <item><b>The gain is relative dB, never dBm</b> (upstream has no level calibration): LNA index 0-4 and LPF index 1-9 placed on
/// upstream's measured steps (LNA 1 to 4: +6.8, +6.1, +5.8, +5.6 dB; LPF about 2 dB a step - include/hw/mt7996/phy.h), 0 dB = LNA 0
/// LPF 1. LPF 0 does not follow the slope and is not on the axis.</item>
/// <item><b>Lost completions are seen</b> (caps.overflow_oracle): <c>seq</c> counts every completion, and a gap is counted as 2048
/// samples each (an event in the gap carried none: an upper bound). <b>The datagrams are put back in order first</b>
/// (<see cref="W1700kReorder"/>): an adapter may deliver them out of order, and a hole is lost only once the window has passed it.</item>
/// <item><b>One controller at a time</b> (the unit keeps one state for every controller): a unit found already streaming is refused,
/// naming both remedies, and <c>--w1700k-serve none</c> receives a stream as it is and changes nothing.</item>
/// </list>
/// </summary>
internal sealed class W1700kBackend : RadioBackendBase, W1700kReorder.ISink, ITxHandoverBackend
{
    /// <summary>TUNE's reach per band (upstream <c>host/w1700k/radio.py</c> LO_RANGE): a band's span until the unit has measured its
    /// own lock edges (<c>rf.lo_range_hz</c>, stated for the band in force).</summary>
    internal static readonly (long Min, long Max)[] TuneReach =
    {
        (2_070_000_000, 2_750_000_000),
        (4_640_000_000, 8_000_000_000),
        (4_640_000_000, 8_000_000_000),
    };

    /// <summary>The antennas: one per band, each its own front end.</summary>
    internal static readonly string[] BandNames = { "band 0 (2.4 GHz)", "band 1 (5 GHz)", "band 2 (6 GHz)" };

    /// <summary>LNA index to relative dB: upstream's measured steps, cumulative from LNA 0.</summary>
    internal static readonly double[] LnaDb = { 0, 6.8, 12.9, 18.7, 24.3 };

    internal const double LpfStepDb = 2;
    internal const double GainMaxDb = 24.3 + 8 * LpfStepDb;     // LNA 4, LPF 9

    /// <summary>The analog filters, MHz (upstream <c>rf.FILTER_CODE</c>).</summary>
    internal static readonly int[] FiltersMhz = { 20, 40, 80, 160, 320 };

    private readonly W1700kBackendOptions _opt;
    private readonly bool _controlled;
    private readonly (long Min, long Max)[] _span = ((long, long)[])TuneReach.Clone();
    private readonly object _unitLock = new();
    private W1700kServe? _serve;
    private Socket? _socket;
    private Thread? _rx;
    private Thread? _watch;
    private volatile bool _stopRx;
    private volatile bool _closing;
    private volatile bool _accept;
    private volatile bool _resync;
    private volatile bool _streamOurs;
    /// <summary>The unit has said, in a state event, that our stream runs: only then can it END on its own (OnUnitState).</summary>
    private volatile bool _streamSeenRunning;
    private int _band;
    private readonly W1700kCondition _cond = new();
    private double _condRateHz;
    private bool _silenceSaid;

    // The receive thread's counters (it is their only writer).
    private long _datagrams, _bytes, _raw, _packed, _events, _malformed, _unknown, _beforeStream;
    private long _seqGaps, _seqMissing, _seqLate, _runs;
    private long _lastSeq = -1;
    private bool _unknownSaid;
    private readonly W1700kReorder _reorder;
    private long _lastDatagramTs;
    // The watch's own clock: from its start and from the end of every request to the unit (whose work may stop the stream a while).
    private long _watchFromTs;
    private int _unitInFlight;

    public W1700kBackend(SidecarLog log, W1700kBackendOptions opt)
        : base(log, InfoFor(opt.Name), MakeCaps(opt), RingSamplesFor(opt.RatesHz), opt.LoHz, opt.WideRateHz)
    {
        _opt = opt;
        _controlled = opt.ServeCommand != null || opt.ServeFactory != null;
        _txCommand = opt.TxServeCommand;
        _band = BandFor(opt.LoHz, -1, TuneReach) ?? 0;
        _condRateHz = opt.WideRateHz;
        _cond.Flatten = opt.Flatten;
        _cond.RemoveDc = opt.RemoveDc;
        _reorder = new W1700kReorder(this, opt.ReorderWindow);
    }

    /// <summary>The radio's serial: its address's rendezvous key, the grammar's ONE spelling (<c>usrp:w1700k:&lt;name&gt;</c>), so the
    /// serial a client's IsSameRadio compares is the key it found the sidecar by.</summary>
    internal static string KeyFor(string name) => ShackLinkUsrp.Client.UsrpAddress.W1700k(name).RendezvousKey;

    private static RadioInfo InfoFor(string name) => new()
    {
        Serial = KeyFor(name),
        Product = "W1700K (MT7996)",
        Name = name,
        Source = RadioSources.W1700k,
        KnownCodecFault = false,
    };

    private static RadioCaps MakeCaps(W1700kBackendOptions opt) => new()
    {
        FreqMinHz = TuneReach.Min(r => r.Min),
        FreqMaxHz = TuneReach.Max(r => r.Max),
        GainMinDb = 0,
        GainMaxDb = GainMaxDb,
        GainStepDb = 1,
        WideRatesHz = (double[])opt.RatesHz.Clone(),
        LeaseDecimMin = 2,
        LeaseDecimMax = 4096,
        MaxLeases = 16,
        MaxPanoramas = 4,
        MaxPanoramaBins = 1 << 18,
        Antennas = (string[])BandNames.Clone(),
        Agc = true,
        BandwidthMinHz = FiltersMhz[0] * 1e6,
        BandwidthMaxHz = FiltersMhz[^1] * 1e6,
        RxSides = RxSides.OnlyA,
        OverflowOracle = true,
        Link = $"the unit's UDP export to {opt.Bind}",
    };

    /// <summary>About 0.8 s of the fastest offered rate, a power of two, at most 2^27 samples (512 MiB).</summary>
    private static int RingSamplesFor(double[] rates)
    {
        double want = 0.8 * (rates.Length == 0 ? 40e6 : rates.Max());
        int n = 1 << 22;
        while (n < want && n < (1 << 27)) n <<= 1;
        return n;
    }

    // ------------------------------------------------------------ the bands

    /// <summary>The band that tunes <paramref name="hz"/>: <paramref name="current"/> when it can, else the first that can; null
    /// when none can.</summary>
    internal static int? BandFor(long hz, int current, (long Min, long Max)[] spans)
    {
        if (current >= 0 && current < spans.Length && hz >= spans[current].Min && hz <= spans[current].Max) return current;
        for (int b = 0; b < spans.Length; b++)
            if (hz >= spans[b].Min && hz <= spans[b].Max) return b;
        return null;
    }

    private string SpansWords()
    {
        lock (_unitLock)
            return string.Join("; ", Enumerable.Range(0, _span.Length).Select(b =>
                $"band {b} {Mhz(_span[b].Min, "0.0")}-{Mhz(_span[b].Max, "0.0")} MHz"));
    }

    /// <summary>The band named by an antenna: its <see cref="BandNames"/> entry, "band N" or "N".</summary>
    internal static int? ParseBand(string name)
    {
        string s = (name ?? "").Trim();
        for (int b = 0; b < BandNames.Length; b++)
            if (string.Equals(s, BandNames[b], StringComparison.OrdinalIgnoreCase)) return b;
        if (s.StartsWith("band", StringComparison.OrdinalIgnoreCase)) s = s.Substring(4).Trim();
        if (s.Length > 0 && int.TryParse(s.Split(' ')[0], NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 0 && n < BandNames.Length)
            return n;
        return null;
    }

    // ------------------------------------------------------------ the gain

    /// <summary>
    /// A relative gain in dB to (LNA, LPF): the HIGHEST LNA that reaches it within half an LPF step (the front end's gain first, for
    /// the noise figure), the LPF making up the rest on its 2 dB steps. Monotone: a higher gain asked is never a lower one realised -
    /// "the highest LNA at or under it" was not, since the LPF's rounding carried one LNA's top past the next one's floor.
    /// </summary>
    internal static (int Lna, int Lpf) GainFor(double db)
    {
        db = Math.Clamp(db, 0, GainMaxDb);
        for (int k = LnaDb.Length - 1; k >= 0; k--)
        {
            int lpf = (int)Math.Clamp(1 + Math.Round((db - LnaDb[k]) / LpfStepDb, MidpointRounding.AwayFromZero), 1, 9);
            if (Math.Abs(LnaDb[k] + (lpf - 1) * LpfStepDb - db) <= LpfStepDb / 2 + 1e-9) return (k, lpf);
        }
        return (0, 1);      // unreachable: the five LNA ladders cover 0..GainMaxDb with no gap wider than an LPF step
    }

    private static string Mhz(double hz, string format = "0.000") => (hz / 1e6).ToString(format, CultureInfo.InvariantCulture);

    /// <summary>(LNA, LPF) on the relative-dB axis. LPF 0 is off the slope: reported a step under LPF 1, said where it is read.</summary>
    internal static double DbFor(int lna, int lpf) =>
        Math.Round(LnaDb[Math.Clamp(lna, 0, LnaDb.Length - 1)] + (Math.Clamp(lpf, 0, 9) - 1) * LpfStepDb, 1);

    /// <summary>The narrowest filter that passes <paramref name="hz"/>, the widest when none does.</summary>
    internal static int FilterFor(double hz)
    {
        foreach (int mhz in FiltersMhz)
            if (mhz * 1e6 >= hz - 0.5) return mhz;
        return FiltersMhz[^1];
    }

    // ------------------------------------------------------------ open and close

    protected override void OpenRadio()
    {
        // A refusal at open (a start at a rate the link cannot carry, a router another program drives, a host key not trusted) is
        // said the way every refusal is (W1700kServeException.Said), so the host can tell one that will not fix itself.
        try
        {
            OpenRadioCore();
        }
        catch (W1700kServeException ex)
        {
            throw new InvalidOperationException(ex.Said, ex);
        }
    }

    private void OpenRadioCore()
    {
        // Which transmit service, or why none, is settled BEFORE the controller starts: its --version is the one question asked of
        // the bundled controller, and it touches nothing on the unit (ShackLink docs/w1700k_support.md D4).
        ResolveTransmitter();
        OpenSocket();
        _worker = new Thread(ConditionLoop) { IsBackground = true, Name = "W1700kCondition", Priority = ThreadPriority.AboveNormal };
        _worker.Start();
        _rx = new Thread(ReceiveLoop) { IsBackground = true, Name = "W1700kRx", Priority = ThreadPriority.Highest };
        _rx.Start();
        if (!_controlled)
        {
            _accept = true;
            // The gain and filter are the driving program's, and nothing here can read them. A gain number would be invented (the
            // base's 30 dB is no measurement), so the state says AGC - upstream's default after a load, and a row's "the radio decides" -
            // and the bandwidth 0, not stated.
            UpdateState(s => { s.Antenna = BandNames[_band]; s.Agc = true; s.BandwidthHz = 0; }, announce: false);
            Log($"no controller (--w1700k-serve none): receiving the export on {_opt.Bind} as another program drives it, labelled " +
                $"{LoHz} Hz at {WideRateHz:0} S/s from the command line; this sidecar changes nothing on the unit, and cannot read its " +
                "gain or filter (stated as AGC and not stated)");
            StartWatch();
            return;
        }

        var serve = _opt.ServeFactory != null ? _opt.ServeFactory() : W1700kServe.Spawn(_opt.ServeCommand!, Log);
        lock (_unitLock) _serve = serve;
        serve.StateChanged += OnUnitState;
        serve.Exited += why => { if (!_closing) Fault($"w1700k serve ended under the radio: {why}"); };
        serve.Start();
        Log(_opt.ServeFactory != null ? "controller: a gate's" : $"controller: {_opt.ServeCommand}");
        var st = serve.WaitAttached(_opt.AttachTimeoutMs);
        if (st.GetObject("stream")?.GetBool("running") == true)
            throw new InvalidOperationException(
                "the unit is already streaming - another controller (upstream's viewer, the CLI, another sidecar) drives it. Stop it there " +
                "(w1700k stop), or start this sidecar with --w1700k-serve none to receive that stream as it is");
        bool loaded = st.GetObject("radio")?.GetBool("loaded") == true;
        LearnSpan(st);
        int band = BandFor(_opt.LoHz, -1, _span) ?? throw new InvalidOperationException(
            $"no band of this unit tunes {Mhz(_opt.LoHz)} MHz ({SpansWords()})");
        Log(loaded ? $"the unit's radio is loaded; capturing band {band}" : $"the unit's radio is not loaded: loading it (about 3 s), band {band}");
        st = serve.Request(Req("configure").Set("band", band).Set("sample_rate", (long)_opt.WideRateHz).Set("lo_hz", _opt.LoHz),
                           loaded ? _opt.CommandTimeoutMs : _opt.LoadTimeoutMs);
        TakeUnitState(st, announce: false);
        // The stream is ours from here: what arrives after the start is its samples, from its first sequence number.
        _resync = true;
        _accept = true;
        _streamOurs = true;
        st = serve.Request(Req("start"), _opt.LoadTimeoutMs);
        TakeUnitState(st, announce: false);
        StartWatch();
        LearnTransmitter();
    }

    private static JsonObject Req(string op) => new JsonObject().Set("op", op);

    private void OpenSocket()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            // A second receiver of the export must fail at its bind, never share the datagrams with this one.
            ExclusiveAddressUse = true,
            ReceiveTimeout = 500,
        };
        try
        {
            s.ReceiveBufferSize = _opt.ReceiveBufferBytes;
            s.Bind(_opt.Bind);
        }
        catch (SocketException ex)
        {
            s.Dispose();
            throw new InvalidOperationException(ex.SocketErrorCode switch
            {
                SocketError.AddressAlreadyInUse or SocketError.AccessDenied =>
                    $"{_opt.Bind} is held by another program - the export has ONE receiver (upstream's w1700k-stream, a Python receiver, another sidecar)",
                SocketError.AddressNotAvailable =>
                    $"no interface of this machine holds {_opt.Bind.Address}: the cable to the unit's wan port is unplugged, or the address is not set",
                _ => $"cannot bind {_opt.Bind}: {ex.Message}",
            });
        }
        lock (_unitLock) _socket = s;
        Log($"receiving the export on {_opt.Bind}, socket buffer {s.ReceiveBufferSize >> 20} MiB" +
            " (Windows Firewall drops it unless this program has an inbound UDP rule for that port)");
    }

    protected override void CloseRadio()
    {
        _closing = true;
        // A radio closed while taken: the service's input closes, and it unkeys and closes its station. The unit is left needing
        // a load, which the next open's controller gives it (a configure of a radio that does not receive loads it).
        W1700kTxServe? tx;
        lock (_handoverLock)
        {
            tx = _txServe;
            _txServe = null;
        }
        if (tx != null)
        {
            tx.Dispose();
            Log("the transmit service closed with the radio: the unit's reception needs a load (w1700k load) before it is used again");
        }
        W1700kServe? serve;
        lock (_unitLock) serve = _serve;
        if (serve != null)
        {
            if (_streamOurs)
            {
                try
                {
                    serve.Request(Req("stop"), _opt.CommandTimeoutMs);
                    Log("the unit's stream stopped");
                }
                catch (Exception ex)
                {
                    Log($"the unit's stream could not be stopped: {ex.Message} - it runs on until `w1700k stop`");
                }
            }
            serve.Dispose();
        }
        _stopRx = true;
        Socket? s;
        lock (_unitLock)
        {
            s = _socket;
            _socket = null;
        }
        s?.Dispose();
        if (_rx != null && Thread.CurrentThread != _rx) _rx.Join(2000);
        if (_worker != null && Thread.CurrentThread != _worker) _worker.Join(2000);
        if (_watch != null && Thread.CurrentThread != _watch) _watch.Join(2000);
    }

    // ------------------------------------------------------------ the unit's state

    private void LearnSpan(JsonObject st)
    {
        int band = (int)(st.GetObject("radio")?.GetLong("band", -1) ?? -1);
        if (band < 0 || band >= _span.Length) return;
        if (st.GetObject("rf")?.GetArray("lo_range_hz") is { Count: 2 } r && r[0] is JsonNumber lo && r[1] is JsonNumber hi &&
            lo.TryAsLong(out long min) && hi.TryAsLong(out long max) && min < max)
            lock (_unitLock) _span[band] = (min, max);
    }

    /// <summary>What the unit's controller states, taken into this radio's state: the band, the LO it realised, the rate, the
    /// gain (AGC, or LNA and LPF on the relative-dB axis) and the filter. <paramref name="announce"/>: tell the clients.</summary>
    private void TakeUnitState(JsonObject st, bool announce)
    {
        LearnSpan(st);
        var radio = st.GetObject("radio");
        var rf = st.GetObject("rf");
        var stream = st.GetObject("stream");
        int band = (int)(radio?.GetLong("band", _band) ?? _band);
        lock (_unitLock) _band = band;
        long lo = stream?.GetLong("center_hz", 0) is long c && c > 0 ? c : rf?.GetLong("lo_hz", LoHz) ?? LoHz;
        double rate = radio?.GetDouble("sample_rate", WideRateHz) ?? WideRateHz;
        bool agc = rf?.GetBool("agc", true) ?? true;
        long lna = -1, lpf = -1;
        bool hasLna = rf != null && rf.TryGetLong("lna", out lna);
        bool hasLpf = rf != null && rf.TryGetLong("lpf", out lpf);
        long filter = rf?.GetLong("filter_mhz", 0) ?? 0;
        UpdateRfLo(lo);
        UpdateState(s =>
        {
            s.LoHz = lo;
            s.WideRateHz = rate;
            Volatile.Write(ref _condRateHz, rate);
            s.Antenna = band >= 0 && band < BandNames.Length ? BandNames[band] : $"band {band}";
            s.Agc = agc;
            if (!agc && hasLna && hasLpf) s.GainDb = DbFor((int)lna, (int)lpf);
            if (filter > 0) s.BandwidthHz = filter * 1e6;
            s.DcOffsetHz = 0;
        }, announce);
        if (!agc && hasLpf && lpf == 0)
            Log($"the unit's gain is LNA {lna} LPF 0: LPF 0 is off upstream's 2 dB slope, so {DbFor((int)lna, 0)} dB is a placeholder, not a step");
    }

    /// <summary>Every <c>state</c> event: a stream that ended on its own (an overrun, a killed pump) ends this radio, in the unit's
    /// words - samples will not come back without a start, and a client must not wait on silence. Only a stream SEEN running can
    /// end: a start the unit refuses emits its state (error set, not running) BEFORE its reply, and the reply carries the reason
    /// the open reports (W1700kServeException.Said); read here, the first-wins fault would say "stream ended" and lose it.</summary>
    private void OnUnitState(JsonObject st)
    {
        if (_closing || !_streamOurs) return;
        var stream = st.GetObject("stream");
        if (stream == null) return;
        if (stream.GetBool("running"))
        {
            _streamSeenRunning = true;
            return;
        }
        if (!_streamSeenRunning) return;
        if (st.GetString("busy") != null) return;     // a configure stops and restarts the stream itself
        string error = st.GetString("error") ?? "";
        _streamOurs = false;
        Fault("the unit's stream ended" + (error.Length > 0 ? ": " + error : " (no reason stated)"));
    }

    /// <summary>
    /// A fault of THIS side (silence, the socket) while the unit streams for us: the radio is dead to its clients, so the unit stops now
    /// rather than at the sidecar's exit - a persistent sidecar holding a faulted radio would otherwise leave a 1.3 Gbit/s stream running
    /// at nothing for as long as it lives. Said either way; a stop that fails is the unit's `w1700k stop` to make.
    /// </summary>
    private void StopOurStream(string after)
    {
        if (!_controlled || !_streamOurs) return;
        _streamOurs = false;
        W1700kServe? serve;
        lock (_unitLock) serve = _serve;
        if (serve == null) return;
        try
        {
            serve.Request(Req("stop"), _opt.CommandTimeoutMs);
            Log($"the unit's stream stopped after {after}");
        }
        catch (Exception ex)
        {
            Log($"the unit's stream could not be stopped after {after}: {ex.Message} - it runs on until `w1700k stop`");
        }
    }

    private JsonObject Unit(JsonObject request, int? timeoutMs = null)
    {
        if (_taken)
            throw new RadioRefusal(ErrorCodes.NotReady,
                "the radio's transmitter is taken, so its receiver is stopped: release the transmitter (release_tx) to tune or set the receiver");
        W1700kServe serve;
        lock (_unitLock)
            serve = _serve ?? throw new RadioRefusal(ErrorCodes.NotReady, "the unit's controller is not running");
        // While the unit works (a configure may stop and restart the stream), its silence is not a fault: the watch starts again after.
        Interlocked.Increment(ref _unitInFlight);
        try
        {
            return serve.Request(request, timeoutMs ?? _opt.CommandTimeoutMs);
        }
        catch (W1700kServeException ex)
        {
            throw new RadioRefusal(ErrorCodes.OutOfRange, ex.Said);
        }
        catch (Exception ex) when (ex is TimeoutException || ex is IOException)
        {
            throw new RadioRefusal(ErrorCodes.RadioFault, ex.Message);
        }
        finally
        {
            Volatile.Write(ref _watchFromTs, Stopwatch.GetTimestamp());
            Interlocked.Decrement(ref _unitInFlight);
        }
    }

    private void RequireControl(string what)
    {
        if (!_controlled)
            throw new RadioRefusal(ErrorCodes.Unsupported,
                $"{what}: this sidecar was started with --w1700k-serve none - it receives the stream another program drives and changes nothing on the unit");
    }

    // ------------------------------------------------------------ the receive thread and the conditioning worker

    /// <summary>
    /// The receive thread's hand-over to the conditioning worker: a fixed ring of slice buffers, one writer and one reader. The receive
    /// thread only receives, counts and copies - it never waits on the DSP - so the socket is drained at the link's pace whatever the
    /// flatten costs; a slice the worker has no room for is DROPPED and counted as lost samples (it is lost: the ring says so).
    /// </summary>
    private sealed class SliceQueue
    {
        public const byte Raw = 1, Packed = 2, Reset = 3;
        private readonly byte[][] _data;
        private readonly byte[] _kind;
        private readonly int _mask;
        private readonly SemaphoreSlim _items = new(0);
        private long _head, _tail;
        private long _taken;      // the worker's: slots handed to it and not yet released (between _tail and here)

        public SliceQueue(int slots)
        {
            _data = new byte[slots][];
            _kind = new byte[slots];
            _mask = slots - 1;
            for (int i = 0; i < slots; i++) _data[i] = new byte[W1700kExport.RawSliceBytes];
        }

        public int Capacity => _data.Length;
        public int Depth => (int)(Volatile.Read(ref _head) - Volatile.Read(ref _tail));

        /// <summary>The receive thread's: false when the worker is a whole queue behind.</summary>
        public bool TryPut(byte kind, ReadOnlySpan<byte> slice)
        {
            long h = _head;
            if (h - Volatile.Read(ref _tail) >= _data.Length) return false;
            int i = (int)(h & _mask);
            _kind[i] = kind;
            slice.CopyTo(_data[i]);
            Volatile.Write(ref _head, h + 1);
            _items.Release();
            return true;
        }

        /// <summary>The worker's: the next slot, left in place (the writer cannot reuse it) until <see cref="Done"/> releases it. Several
        /// may be taken before any is released (a batch).</summary>
        public bool TryTake(int timeoutMs, out byte kind, out byte[] data)
        {
            kind = 0;
            data = Array.Empty<byte>();
            if (!_items.Wait(timeoutMs)) return false;
            int i = (int)(_taken & _mask);
            _taken++;
            kind = _kind[i];
            data = _data[i];
            return true;
        }

        /// <summary>The oldest <paramref name="n"/> slots taken are free for the writer again.</summary>
        public void Done(int n = 1) => Volatile.Write(ref _tail, _tail + n);
    }

    private readonly SliceQueue _queue = new(4096);       // 32 MiB: 0.2 s at 40 MS/s
    private Thread? _worker;
    private volatile bool _resetWanted;
    private long _queueDropped, _queueMaxDepth;
    private long _batches, _batchMax;           // the worker's: batches conditioned, and the largest

    private void ReceiveLoop()
    {
        Socket? socket;
        lock (_unitLock) socket = _socket;
        if (socket == null) return;
        var buf = new byte[65536];
        // The conditioner restarts where the stream did: a marker in the queue, in order with the slices - or, when even that has no
        // room, a flag the worker takes before its next slice. What the old run still held goes in first, its holes given up.
        void Fresh()
        {
            _reorder.Flush();
            _reorder.Reset();
            if (!_queue.TryPut(SliceQueue.Reset, ReadOnlySpan<byte>.Empty)) _resetWanted = true;
        }
        while (!_stopRx)
        {
            int n;
            try
            {
                n = socket.Receive(buf);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                // Half a second with nothing: a hole that is still open is not going to fill, and what waits behind it is released.
                _reorder.Flush();
                continue;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.MessageSize)
            {
                Interlocked.Increment(ref _malformed);
                continue;
            }
            catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException)
            {
                if (!_stopRx)
                {
                    Fault($"the export's socket failed: {ex.Message}");
                    StopOurStream("the socket's failure");
                }
                return;
            }
            long now = Stopwatch.GetTimestamp();
            // A second of silence is a new run (upstream's rule): the stream stopped and started, and its numbers start again.
            bool silent = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastDatagramTs), now).TotalSeconds >= 1;
            Volatile.Write(ref _lastDatagramTs, now);
            _datagrams++;
            _bytes += n;
            var d = buf.AsSpan(0, n);
            var kind = W1700kExport.Classify(d, out ulong seq, out int sliceBytes);
            if (!_accept)
            {
                _beforeStream++;
                continue;
            }
            if (kind == W1700kExport.Kind.Malformed)
            {
                _malformed++;
                continue;
            }
            if (_resync || silent)
            {
                _resync = false;
                Fresh();
            }
            if (_reorder.Next is ulong e && (seq > e && seq - e > (1UL << 39) || seq < e && e - seq > (1UL << 20)))
            {
                _runs++;
                Fresh();
                Log($"the export's sequence jumped from {e} to {seq}: a new run");
            }
            // Late or repeated: its place in the ring has been written past, and its gap was already counted.
            if (_reorder.Accept(kind, seq, sliceBytes, d.Slice(W1700kExport.HeaderBytes, sliceBytes)) == W1700kReorder.Placed.Late) _seqLate++;
        }
    }

    /// <summary>The next completion in order (<see cref="W1700kReorder"/>), on the receive thread.</summary>
    void W1700kReorder.ISink.Deliver(W1700kExport.Kind kind, ulong seq, int sliceBytes, ReadOnlySpan<byte> slice)
    {
        Volatile.Write(ref _lastSeq, (long)seq);
        switch (kind)
        {
            case W1700kExport.Kind.Event:
                _events++;
                break;
            case W1700kExport.Kind.Raw:
            case W1700kExport.Kind.Packed:
                if (kind == W1700kExport.Kind.Raw) _raw++;
                else _packed++;
                if (!_queue.TryPut(kind == W1700kExport.Kind.Raw ? SliceQueue.Raw : SliceQueue.Packed, slice))
                {
                    _queueDropped++;
                    AddOverflows(W1700kExport.SamplesPerSlice);
                }
                int depth = _queue.Depth;
                if (depth > _queueMaxDepth) _queueMaxDepth = depth;
                break;
            default:
                _unknown++;
                AddOverflows(W1700kExport.SamplesPerSlice);
                if (!_unknownSaid)
                {
                    _unknownSaid = true;
                    Log($"a slice of {sliceBytes} bytes is neither raw ({W1700kExport.RawSliceBytes}) nor packed ({W1700kExport.PackedSliceBytes}): counted lost");
                }
                break;
        }
    }

    /// <summary>Completions that never arrived, given up where they belong in the order.</summary>
    void W1700kReorder.ISink.Missing(ulong firstSeq, ulong count)
    {
        _seqGaps++;
        _seqMissing += (long)count;
        AddOverflows((long)count * W1700kExport.SamplesPerSlice);
    }

    /// <summary>The batch the worker has taken: its raw slots (not yet released) and their kinds - read only by its shared part, so
    /// ONE set serves, refilled once that is done (the batch in hand keeps its slices in the <see cref="W1700kCondition.Overlap"/>).</summary>
    private sealed class TakenBatch
    {
        public readonly byte[] Kinds = new byte[W1700kCondition.MaxBatch];
        public readonly byte[][] Raws = new byte[W1700kCondition.MaxBatch][];
        public readonly Action<int, short[]> Fill;
        public int Count, Taken;
        public bool ResetAfter;

        public TakenBatch()
        {
            Fill = (j, dst) =>
            {
                if (Kinds[j] == SliceQueue.Raw) W1700kExport.ConvertRaw(Raws[j], dst);
                else W1700kExport.ConvertPacked(Raws[j], dst);
            };
        }
    }

    /// <summary>What has piled up, as one batch into <paramref name="s"/>: never across a restart marker (it ends the batch, and
    /// <see cref="TakenBatch.ResetAfter"/> says so). False = nothing arrived within <paramref name="waitMs"/>.</summary>
    private bool TakeBatch(TakenBatch s, int waitMs)
    {
        s.Count = 0;
        s.Taken = 0;
        s.ResetAfter = false;
        if (!_queue.TryTake(waitMs, out byte kind, out byte[] data)) return false;
        s.Taken = 1;
        if (kind == SliceQueue.Reset)
        {
            s.ResetAfter = true;
            return true;
        }
        s.Kinds[0] = kind;
        s.Raws[0] = data;
        s.Count = 1;
        while (s.Count < W1700kCondition.MaxBatch && _queue.TryTake(0, out kind, out data))
        {
            s.Taken++;
            if (kind == SliceQueue.Reset)
            {
                s.ResetAfter = true;
                break;
            }
            s.Kinds[s.Count] = kind;
            s.Raws[s.Count++] = data;
        }
        return true;
    }

    /// <summary>The worker: the slices to sc16, flattened and their DC taken out, into the ring - the ONLY writer of the ring, and the
    /// only thread that drives the conditioner, at the rate the unit last stated (picked up between batches, never under one).
    /// <para>It takes what has piled up as ONE batch (up to <see cref="W1700kCondition.MaxBatch"/>, never across a restart marker) and
    /// a <see cref="WorkTeam"/> shares each slice's own work: one thread could not flatten 160 MS/s (12.2 us a slice against 12.8
    /// between slices, measured Release, 2026-10-27). Each batch's in-order part and ring write run beside the NEXT one's shared part
    /// (<see cref="W1700kCondition.Overlap"/>). While it keeps up a batch is one slice and the team sleeps.</para></summary>
    private void ConditionLoop()
    {
        var ring = RingOf(RxSides.A);
        Action<short[][], int> write = (iqs, n) =>
        {
            for (int j = 0; j < n; j++) ring.Write(iqs[j]);
        };
        var s = new TakenBatch();
        using var team = new WorkTeam(_opt.ConditionHelpers, "W1700kCondition+", ThreadPriority.AboveNormal);
        // Each batch's shared part on the team BESIDE the batch before it ending here (W1700kCondition.Overlap, the one owner of that order).
        var overlap = new W1700kCondition.Overlap(_cond, team);
        while (!_stopRx)
        {
            if (!TakeBatch(s, overlap.Holding ? 0 : 200))
            {
                overlap.Flush(write);                       // nothing has piled up: finish the one in hand, then wait
                continue;
            }
            try
            {
                if (_resetWanted)
                {
                    _resetWanted = false;
                    _cond.Reset();
                }
                if (s.Count > 0)
                {
                    double rate = Volatile.Read(ref _condRateHz);
                    if (rate != _cond.RateHz) _cond.RateHz = rate;
                    overlap.Next(s.Count, s.Fill, write);
                    if (s.Count > _batchMax) _batchMax = s.Count;
                    _batches++;
                }
                else overlap.Flush(write);
            }
            finally
            {
                _queue.Done(s.Taken);                       // its raw slots are read
            }
            if (s.ResetAfter) _cond.Reset();
        }
    }

    /// <summary>A stream this sidecar started that goes silent is a fault in words; without a controller, silence is said once.</summary>
    private void StartWatch()
    {
        Volatile.Write(ref _watchFromTs, Stopwatch.GetTimestamp());
        _watch = new Thread(() =>
        {
            while (!_stopRx && !_closing)
            {
                Thread.Sleep(250);
                if (_taken)
                {
                    // The silence is the transmitter's: the receive stream was stopped on purpose.
                    Volatile.Write(ref _watchFromTs, Stopwatch.GetTimestamp());
                    continue;
                }
                if (Volatile.Read(ref _unitInFlight) > 0) continue;
                double ms = Stopwatch.GetElapsedTime(Math.Max(Volatile.Read(ref _lastDatagramTs), Volatile.Read(ref _watchFromTs))).TotalMilliseconds;
                if (ms < _opt.SilenceFaultMs)
                {
                    _silenceSaid = false;
                    continue;
                }
                string words = $"no datagram from the unit on {_opt.Bind} for {(ms / 1000).ToString("0.0", CultureInfo.InvariantCulture)} s - the cable to its wan port, this program's " +
                               "inbound firewall rule for that UDP port, the jumbo MTU on this NIC, or the unit itself";
                if (_controlled && _streamOurs)
                {
                    Fault(words);
                    StopOurStream("the silence");
                    return;
                }
                if (!_silenceSaid)
                {
                    _silenceSaid = true;
                    Log(words);
                }
            }
        })
        { IsBackground = true, Name = "W1700kWatch" };
        _watch.Start();
    }

    // ------------------------------------------------------------ the radio's settings

    protected override double MasterClockHz => WideRateHz;

    /// <summary>What is in flight when the controller's reply arrives (<see cref="W1700kBackendOptions.SettleMs"/>): STATED.</summary>
    protected override long SettleSamples => (long)(WideRateHz * _opt.SettleMs / 1000.0);

    protected override (long RfLoHz, IReadOnlyDictionary<string, long> Centers) ApplyTuning(long rfLoHz, IReadOnlyDictionary<string, long> centers)
    {
        long want = centers.TryGetValue(RxSides.A, out long a) ? a : rfLoHz;
        if (!_controlled)
        {
            if (want == LoHz) return (want, centers);
            RequireControl("a retune");
        }
        int current;
        (long Min, long Max)[] spans;
        lock (_unitLock)
        {
            current = _band;
            spans = ((long, long)[])_span.Clone();
        }
        int band = BandFor(want, current, spans) ?? throw new RadioRefusal(ErrorCodes.OutOfRange,
            $"no band of this unit tunes {Mhz(want)} MHz: {SpansWords()}");
        JsonObject st = band == current ? Unit(Req("tune").Set("hz", want)) : SwitchBand(band, want);
        TakeUnitState(st, announce: band != current);
        if (band != current) Log($"band {current} to band {band} for {Mhz(want)} MHz: another front end, with its own gain and filter");
        long got = LoHz;
        return (got, centers.Keys.ToDictionary(k => k, _ => got));
    }

    protected override (long RfLoHz, IReadOnlyDictionary<string, long> Centers, double ClockHz) ApplyRxSides(
        IReadOnlyList<string> sides, long rfLoHz, IReadOnlyDictionary<string, long> centers, double clockHz)
    {
        if (sides.Contains(RxSides.B))
            throw new RadioRefusal(ErrorCodes.Unsupported, "this radio streams one band at a time: it has no RX B");
        var t = ApplyTuning(rfLoHz, centers);
        return (t.RfLoHz, t.Centers, WideRateHz);
    }

    protected override double ApplyRate(double hz)
    {
        if (!_controlled)
        {
            if (Math.Abs(hz - WideRateHz) < 0.5) return hz;
            RequireControl("a rate change");
        }
        int band;
        lock (_unitLock) band = _band;
        // The unit stops the stream, runs RATE and starts it again: the new stream numbers its completions afresh.
        _resync = true;
        var st = Unit(Req("configure").Set("band", band).Set("sample_rate", (long)Math.Round(hz)), _opt.LoadTimeoutMs);
        _resync = true;
        TakeUnitState(st, announce: false);
        return WideRateHz;
    }

    protected override double ApplyGain(string side, double db)
    {
        RequireControl("a gain");
        var (lna, lpf) = GainFor(db);
        Unit(Req("gain").Set("agc", false).Set("lna", lna).Set("lpf", lpf));
        return DbFor(lna, lpf);
    }

    protected override void ApplyAgc(string side, bool on)
    {
        RequireControl(on ? "the AGC" : "a manual gain");
        if (on)
        {
            Unit(Req("gain").Set("agc", true));
            return;
        }
        // Off: the gain in the state, which the AGC does not report - the last one set, or the default the clients read.
        var (lna, lpf) = GainFor(GetState().GainDb);
        Unit(Req("gain").Set("agc", false).Set("lna", lna).Set("lpf", lpf));
        UpdateState(s => s.GainDb = DbFor(lna, lpf), announce: false);
    }

    /// <summary>The band (front end) at the frequency in force: bands 1 and 2 overlap, band 0 reaches no frequency they do.</summary>
    protected override string ApplyAntenna(string side, string name)
    {
        int band = ParseBand(name) ?? throw new RadioRefusal(ErrorCodes.OutOfRange,
            $"\"{name}\" is not an input of this radio: {string.Join(", ", BandNames)}");
        int current;
        (long Min, long Max) span;
        lock (_unitLock)
        {
            current = _band;
            span = _span[band];
        }
        if (band == current) return BandNames[band];
        RequireControl("a band switch");
        long lo = LoHz;
        if (lo < span.Min || lo > span.Max)
            throw new RadioRefusal(ErrorCodes.OutOfRange,
                $"{BandNames[band]} does not lock {Mhz(lo)} MHz (it tunes {Mhz(span.Min, "0.0")}-{Mhz(span.Max, "0.0")} MHz): " +
                "tune into its span and the radio moves to it itself");
        TakeUnitState(SwitchBand(band, lo), announce: true);
        return BandNames[band];
    }

    /// <summary>
    /// Another front end, at <paramref name="lo"/>. Upstream keeps a sample rate PER BAND, and every load sets each to 320 MS/s, so the
    /// link's rate goes with every switch: a configure without it restarts the stream at the band's own rate, more than the link
    /// carries. When the band's rate is already the link's, the capture moves mid-stream (one register write); otherwise the unit
    /// stops, runs RATE and starts again, and its completions are numbered afresh.
    /// </summary>
    private JsonObject SwitchBand(int band, long lo)
    {
        _resync = true;
        var st = Unit(Req("configure").Set("band", band).Set("sample_rate", (long)Math.Round(WideRateHz)).Set("lo_hz", lo), _opt.LoadTimeoutMs);
        _resync = true;
        return st;
    }

    protected override double ApplyBandwidth(double hz)
    {
        RequireControl("the analog filter");
        int mhz = FilterFor(hz);
        Unit(Req("filter").Set("mhz", mhz));
        return mhz * 1e6;
    }

    protected override JsonArray RadioSettings()
    {
        var st = _serve?.State;
        var rf = st?.GetObject("rf");
        int band;
        lock (_unitLock) band = _band;
        var a = new JsonArray()
            .Add(new JsonObject().Set("key", "band").Set("type", "int").Set("value", band.ToString(CultureInfo.InvariantCulture)).Set("read_only", true)
                .Set("text", "the front end in force (set it as the antenna, or by tuning: the band follows the frequency)"))
            .Add(new JsonObject().Set("key", "spans").Set("type", "string").Set("value", SpansWords()).Set("read_only", true)
                .Set("text", "the LO each band locks; a band's own edges once the unit has measured them, else TUNE's reach"))
            .Add(new JsonObject().Set("key", "gain_axis").Set("type", "string").Set("read_only", true)
                .Set("value", rf == null ? "" : rf.GetBool("agc", true) ? "AGC" : $"LNA {rf.GetLong("lna", -1)} LPF {rf.GetLong("lpf", -1)}")
                .Set("text", "the gain is RELATIVE dB on upstream's measured LNA and LPF steps - no level calibration exists"))
            .Add(new JsonObject().Set("key", "flatten").Set("type", "bool").Set("value", _cond.Flatten ? "true" : "false")
                .Set("text", "correct the decimation's roll-off at the band's edges (upstream's measured table, up to +24 dB), in the samples: " +
                             "the panorama and every sub-receiver" + (W1700kCondition.TapsFor(WideRateHz) == null ? " - this rate has no table" : "")))
            .Add(new JsonObject().Set("key", "dc_removal").Set("type", "bool").Set("value", _cond.RemoveDc ? "true" : "false")
                .Set("text", "take the LO's leak out of the centre: a DC blocker with a " +
                             W1700kCondition.DcCornerHz(WideRateHz).ToString("0", CultureInfo.InvariantCulture) +
                             " Hz corner (a sub-receiver tuned within a few hundred hertz of the LO loses that notch)"))
            .Add(new JsonObject().Set("key", "export").Set("type", "string").Set("value", _opt.Bind.ToString()).Set("read_only", true))
            .Add(new JsonObject().Set("key", "controller").Set("type", "string").Set("read_only", true)
                .Set("value", _controlled ? (_opt.ServeFactory != null ? "a gate's" : _opt.ServeCommand ?? "") : "none"));
        return a;
    }

    /// <summary>The two this sidecar does to the samples; everything else is the radio's to state.</summary>
    protected override string ApplySetting(string key, string value)
    {
        bool on = value switch
        {
            "true" or "on" or "1" => true,
            "false" or "off" or "0" => false,
            _ when key is "flatten" or "dc_removal" => throw new RadioRefusal(ErrorCodes.BadRequest, $"{key} is true or false, not \"{value}\""),
            _ => false,
        };
        switch (key)
        {
            case "flatten":
                _cond.Flatten = on;
                return on ? "true" : "false";
            case "dc_removal":
                _cond.RemoveDc = on;
                return on ? "true" : "false";
            case "band":
            case "spans":
            case "gain_axis":
            case "export":
            case "controller":
                throw new RadioRefusal(ErrorCodes.Unsupported, $"{key} is the radio's to state, not a setting");
            default:
                throw new RadioRefusal(ErrorCodes.NotFound, $"no setting \"{key}\"");
        }
    }

    protected override JsonObject RadioTelemetry()
    {
        var st = _serve?.State;
        double since = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastDatagramTs)).TotalMilliseconds;
        return new JsonObject()
            .Set("kind", "w1700k")
            .Set("export", _opt.Bind.ToString())
            .Set("controlled", _controlled)
            .Set("stream_ours", _streamOurs)
            .Set("datagrams", Interlocked.Read(ref _datagrams))
            .Set("bytes", Interlocked.Read(ref _bytes))
            .Set("raw", Interlocked.Read(ref _raw))
            .Set("packed", Interlocked.Read(ref _packed))
            .Set("events", Interlocked.Read(ref _events))
            .Set("malformed", Interlocked.Read(ref _malformed))
            .Set("unknown_size", Interlocked.Read(ref _unknown))
            .Set("before_stream", Interlocked.Read(ref _beforeStream))
            .Set("seq_gaps", Interlocked.Read(ref _seqGaps))
            .Set("seq_missing", Interlocked.Read(ref _seqMissing))
            .Set("seq_late", Interlocked.Read(ref _seqLate))
            .Set("seq_restored", Interlocked.Read(ref _reorder.Restored))
            .Set("seq_restored_max", Interlocked.Read(ref _reorder.RestoredMax))
            .Set("reorder_window", _reorder.Window)
            .Set("reorder_held", _reorder.Held)
            .Set("runs", Interlocked.Read(ref _runs))
            .Set("last_seq", Volatile.Read(ref _lastSeq))
            .Set("ms_since_datagram", Math.Round(since))
            .Set("settle_samples", SettleSamples)
            .Set("flatten", _cond.Flatten)
            .Set("flattening", _cond.Flattening)
            .Set("dc_removal", _cond.RemoveDc)
            .Set("flatten_clipped", Interlocked.Read(ref _cond.Clipped))
            .Set("queue_depth", _queue.Depth)
            .Set("queue_max_depth", Interlocked.Read(ref _queueMaxDepth))
            .Set("queue_capacity", _queue.Capacity)
            .Set("queue_dropped", Interlocked.Read(ref _queueDropped))
            .Set("condition_helpers", _opt.ConditionHelpers)
            .Set("condition_batches", Interlocked.Read(ref _batches))
            .Set("condition_batch_max", Interlocked.Read(ref _batchMax))
            .Set("unit_busy", st?.GetString("busy") ?? "")
            .Set("unit_error", st?.GetString("error") ?? "")
            .Set("unit_stream_running", st?.GetObject("stream")?.GetBool("running") ?? false)
            .Set("tx_taken", _taken)
            .Set("tx_unit_rate_hz", Volatile.Read(ref _unitTxRateHz));
    }

    // ------------------------------------------------------------ the transmitter (ShackLink docs/w1700k_support.md "Transmit")

    private readonly object _handoverLock = new();
    // What the transmitter IS never changes after the open, and the server asks on every transmit request - and through a
    // take that runs for the unit's eleven seconds. So these two are read with NO lock; the handover lock is the handover's.
    private volatile W1700kTxCapsInfo? _txInfo;
    private volatile TxCaps? _txCapsWire;
    private W1700kTxServe? _txServe;
    private W1700kTxBand? _txBand;
    private volatile bool _taken;
    /// <summary>A prepare was sent since the take: the unit's capture is disconnected, and only a load restores reception.</summary>
    private bool _txTouchedUnit;
    private double _txGainDb = double.NaN;
    private double _unitTxRateHz;

    /// <summary>A transmitter needs BOTH children: the service that keys the unit, and the controller this sidecar stops for it
    /// and starts again after it. With no service resolved this radio states none, and says why in <c>caps.tx_absent</c>.</summary>
    /// A gate's fake service stands in for the PROCESS only: once a launch value is resolved (TxServeSetting set), the resolution
    /// decides whether there is a service at all.
    private bool TxConfigured => _controlled && (_txCommand != null || (_opt.TxServeSetting == null && _opt.TxServeFactory != null));

    /// <summary>The transmit service's command line: <see cref="W1700kBackendOptions.TxServeCommand"/>, or what the open resolved.</summary>
    private string? _txCommand;
    /// <summary>What the sidecar was started with (<c>ShackLinkUsrp.Client.W1700kTxSettings</c>), for <c>caps.tx_absent.setting</c>; "" = a gate's.</summary>
    private string _txSetting = "";

    /// <summary>The radio is a transmitter now: its receiver is stopped until the transmitter is released.</summary>
    internal bool Taken => _taken;

    /// <summary>
    /// The transmit service this open starts, or why none (ShackLink docs/w1700k_support.md D4): <see cref="W1700kDefaults.ResolveTxServe"/>
    /// over the launch's value, the controller, the station's config and - only for Built in on the bundled controller - that
    /// controller's <c>--version</c>. Whatever it answers, the receiver opens as before.
    /// </summary>
    private void ResolveTransmitter()
    {
        if (_opt.TxServeSetting == null) return;
        var choice = ResolveFor(_opt);
        _txSetting = choice.Setting;
        _txCommand = choice.Command;
        if (choice.HasService)
        {
            StateTxAbsent(null);
            Log($"transmit service ({choice.Setting}): {choice.Command}");
        }
        else
        {
            StateTxAbsent(new TxAbsence { Reason = choice.Reason, Text = choice.Text, Setting = choice.Setting });
            Log("no transmitter: " + choice.Text);
        }
    }

    internal static W1700kTxChoice ResolveFor(W1700kBackendOptions opt) =>
        W1700kDefaults.ResolveTxServe(opt.TxServeSetting ?? W1700kDefaults.NoController,
            // A gate's controller stands for the bundled one of ITS helper folder.
            opt.ServeFactory != null ? W1700kDefaults.ServeCommandIn(opt.HelperDirectory) : opt.ServeCommand ?? W1700kDefaults.NoController,
            opt.ConfigFile, opt.HelperDirectory,
            exe => opt.ReadControllerVersion != null ? opt.ReadControllerVersion(exe) : W1700kServe.ReadVersion(exe, opt.VersionTimeoutMs));

    /// <summary><c>caps.tx_absent</c>: why this radio states no transmitter, or null once it states one. Read by every hello and
    /// <c>wait_ready</c> after the open (the server never sends it beside <c>caps.tx</c>).</summary>
    private void StateTxAbsent(TxAbsence? why) => Caps.TxAbsent = why;

    private W1700kTxServe NewTxServe()
    {
        var s = _opt.TxServeFactory != null ? _opt.TxServeFactory() : W1700kTxServe.Spawn(_txCommand!, Log);
        return HelloOf(s, _opt.TxRequestTimeoutMs, out _);
    }

    /// <summary>The service started and its <c>hello</c> asked: the wire checked, the service handed back (disposed on a failure).</summary>
    private static W1700kTxServe HelloOf(W1700kTxServe s, int timeoutMs, out JsonObject hello)
    {
        try
        {
            s.Start();
            hello = s.Request(Req("hello"), timeoutMs);
            int wire = (int)hello.GetLong("wire", 0);
            if (wire != W1700kTxCapsInfo.WireVersion)
                throw new InvalidDataException($"the transmit service speaks wire {wire}; this sidecar speaks wire {W1700kTxCapsInfo.WireVersion}");
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The instrument <c>--w1700k-tx-check</c> (ShackLink docs/w1700k_support.md D5, Device Setup's Transmitter line): resolved as
    /// an open resolves it, and where a service resolves, that service asked <c>hello</c> and <c>caps</c> through this sidecar's ONE
    /// client of it, its input then closed - the caps-only start, which touches nothing on the unit. No socket is bound and no
    /// controller started. One JSON object: <c>transmitter</c>, <c>setting</c>, then either <c>command</c>, <c>service</c> (the
    /// hello) and <c>tx</c> (caps.tx as the wire would carry it), or <c>tx_absent</c>.
    /// </summary>
    internal static JsonObject CheckTransmitter(W1700kBackendOptions opt, Action<string> log)
    {
        var o = new JsonObject();
        var choice = ResolveFor(opt);
        o.Set("setting", choice.Setting);
        if (!choice.HasService)
        {
            o.Set("transmitter", false);
            o.Set("tx_absent", new TxAbsence { Reason = choice.Reason, Text = choice.Text, Setting = choice.Setting }.ToJson());
            return o;
        }
        o.Set("command", choice.Command);
        W1700kTxServe? s = null;
        try
        {
            s = HelloOf(opt.TxServeFactory != null ? opt.TxServeFactory() : W1700kTxServe.Spawn(choice.Command!, log), opt.TxRequestTimeoutMs, out var hello);
            var info = W1700kTxCapsInfo.Parse(s.Request(Req("caps"), opt.TxRequestTimeoutMs));
            o.Set("transmitter", true);
            hello.Remove("id");
            hello.Remove("ok");
            o.Set("service", hello);
            o.Set("tx", TxCapsFrom(info).ToJson());
        }
        catch (Exception ex)
        {
            o.Set("transmitter", false);
            o.Set("tx_absent", new TxAbsence
            {
                Reason = W1700kTxAbsentReasons.Failed,
                Text = "the transmit service could not state a transmitter: " + (ex is W1700kTxRefusal r ? r.Said : ex.Message),
                Setting = choice.Setting,
            }.ToJson());
        }
        finally
        {
            s?.Dispose();
        }
        return o;
    }

    /// <summary>At open: ask the service what the transmitter is - its <c>caps</c>, which touch nothing on the unit - and let it
    /// go again. A service that cannot state one is no transmitter, said in the log and in <c>caps.tx_absent</c>; the receiver is
    /// unaffected.</summary>
    private void LearnTransmitter()
    {
        if (!TxConfigured) return;
        W1700kTxServe? s = null;
        try
        {
            s = NewTxServe();
            var info = W1700kTxCapsInfo.Parse(s.Request(Req("caps"), _opt.TxRequestTimeoutMs));
            var caps = TxCapsFrom(info);
            _txInfo = info;
            _txCapsWire = caps;
            StateTxAbsent(null);
            InitTxState(st =>
            {
                st.RateHz = info.InputRateHz;
                st.GainDb = double.NaN;
                st.CenterHz = 0;
                st.LoHz = 0;
                st.Taken = false;
            }, info.InputRateHz);
            Log($"transmitter: {info.BandsWords()}; {info.InputRateHz:0} S/s in, at most {info.MaxMs} ms a key-down; taking it stops this radio's " +
                $"reception (about {caps.TakeMs} ms to take, {caps.GiveMs} ms to give back). Level is dB under each band's most, never dBm");
        }
        catch (Exception ex)
        {
            string said = ex is W1700kTxRefusal r ? r.Said : ex.Message;
            Log("no transmitter: the transmit service could not state one - " + said);
            StateTxAbsent(new TxAbsence
            {
                Reason = W1700kTxAbsentReasons.Failed,
                Text = "the transmit service could not state a transmitter: " + said,
                Setting = _txSetting,
            });
        }
        finally
        {
            s?.Dispose();
        }
    }

    /// <summary><c>caps.tx</c> from the service's own statement. The gain axis is the level's, moved to start at 0 (the wire's
    /// negative gain means "unset"): gain = level - the band's lowest level. First build: ShackLink's IQ and a tone (T9).</summary>
    internal static TxCaps TxCapsFrom(W1700kTxCapsInfo info) => StateSources(new TxCaps
    {
        FreqMinHz = info.Bands.Min(b => b.CenterMinHz),
        FreqMaxHz = info.Bands.Max(b => b.CenterMaxHz),
        GainMinDb = 0,
        GainMaxDb = info.LevelSpanDb,
        GainStepDb = info.Bands.Min(b => b.LevelStepDb),
        RatesHz = new[] { info.InputRateHz },
        Antennas = Array.Empty<string>(),
        MaxMs = info.MaxMs,
        QuantumSamples = info.MaxBlockSamples,
        MaxLoOffsetHz = 0,
        Timebase = TxTimebases.Host,
        TakesRadio = true,
        // The take is the service's FIRST preparation: an adopt of the radio this sidecar's controller left loaded where the
        // service states one, else a load. The give-back is its close and this sidecar's own load: no figure but the load's.
        TakeMs = info.AdoptMs > 0 ? info.AdoptMs : info.LoadMs,
        GiveMs = info.CloseMs + info.LoadMs,
        // Each band's facts on the wire's gain axis (0 = the band's lowest level), passed through and never invented: a linear
        // limit or a settle the service does not state is not stated here (ShackLink TX4).
        Bands = info.Bands.Select(b => new TxBand
        {
            Name = b.Name,
            FreqMinHz = b.CenterMinHz,
            FreqMaxHz = b.CenterMaxHz,
            GainMaxDb = b.LevelMaxDb - b.LevelMinDb,
            GainStepDb = b.LevelStepDb,
            LinearMaxGainDb = double.IsNaN(b.LinearMaxLevelDb) ? double.NaN : Math.Round(b.LinearMaxLevelDb - b.LevelMinDb, 6),
            LoadSettleMs = b.LoadSettleMs,
        }).ToArray(),
    }, TxSourceKinds.Iq, TxSourceKinds.Tone);

    protected override TxCaps? RadioTxCaps() => _txCapsWire?.Clone();

    private static RadioRefusal NoTx() =>
        new(ErrorCodes.Unsupported, "this radio states no transmitter: its sidecar was started without a transmit service (--w1700k-tx-serve)");

    private W1700kTxCapsInfo TxInfo() => _txInfo ?? throw NoTx();

    protected override double ApplyTxRate(double hz)
    {
        var info = TxInfo();
        if (Math.Abs(hz - info.InputRateHz) >= 0.5)
            throw new RadioRefusal(ErrorCodes.OutOfRange, $"this transmitter takes {info.InputRateHz:0} S/s and nothing else");
        return info.InputRateHz;
    }

    /// <summary>The level the next key-down asks for, kept on the unit's own steps. Nothing is written to the unit here: the
    /// service realises a level at its <c>start</c>, from measured PA indices, and the grant states what it realised.</summary>
    protected override double ApplyTxGain(double db)
    {
        var info = TxInfo();
        W1700kTxBand? band;
        lock (_handoverLock) band = _txBand;
        double step = band?.LevelStepDb ?? info.Bands.Min(b => b.LevelStepDb);
        double span = band != null ? band.LevelMaxDb - band.LevelMinDb : info.LevelSpanDb;
        double eff = Math.Clamp(Math.Round(db / step) * step, 0, span);
        lock (_handoverLock) _txGainDb = eff;
        return eff;
    }

    /// <summary>
    /// THE TAKE (T4, T5), or a move of a radio already taken: the receive stream stopped, the controller EXITED (two controllers
    /// would interleave on the unit's mailbox), the transmit service started and the unit prepared at <paramref name="centerHz"/>.
    /// It runs for the unit's prepare - about eleven seconds today - and is therefore never part of a key-up. A unit that could
    /// not be prepared is given back to its receiver rather than left neither.
    /// </summary>
    protected override (long CenterHz, long LoHz) ApplyTxTune(long centerHz, long loOffsetHz)
    {
        var info = TxInfo();
        var band = info.BandAt(centerHz) ?? throw new RadioRefusal(ErrorCodes.OutOfRange,
            $"{Mhz(centerHz)} MHz is in no band this unit transmits in: {info.BandsWords()}");
        lock (_handoverLock)
        {
            if (_closing) throw new RadioRefusal(ErrorCodes.NotReady, "the radio is closing");
            bool first = !_taken;
            if (first) TakeForTransmit();
            var tx = _txServe!;
            JsonObject r;
            try
            {
                _txTouchedUnit = true;
                r = tx.Request(Req("prepare").Set("center_hz", centerHz), _opt.LoadTimeoutMs);
            }
            catch (Exception ex)
            {
                bool refused = ex is W1700kTxRefusal;
                string words = ex is W1700kTxRefusal f ? f.Said : "the unit's transmitter could not be prepared: " + ex.Message;
                // A refusal of a MOVE leaves the earlier preparation standing (the service refuses before it touches the unit) -
                // unless the service SAYS nothing is prepared any more ("prepared": false: the preparation failed after it touched
                // the unit). That, and anything that is no refusal, leaves a unit that is no transmitter: back to its receiver.
                bool unprepared = ex is W1700kTxRefusal g && g.Reply != null && g.Reply.Has("prepared") && !g.Reply.GetBool("prepared");
                if (first || !refused || unprepared)
                {
                    GiveBackLocked("the preparation failed");
                    words += "; the radio was given back to its receiver";
                }
                else
                {
                    words += "; the transmitter stays prepared where it was";
                }
                throw new RadioRefusal(refused ? ErrorCodes.OutOfRange : ErrorCodes.RadioFault, words);
            }
            long eff = r.GetLong("center_hz", centerHz);
            long lo = r.GetLong("lo_hz", eff);
            _txBand = info.BandAt(eff) ?? band;
            string path = r.GetString("path", "") ?? "";
            // How it was prepared, as the service said (ShackLink TX4: the operator is told whether the carrier is settling).
            var prepared = new TxPrepared
            {
                Path = path,
                TookMs = r.TryGetLong("took_ms", out long tookMs) && tookMs >= 0 ? tookMs : -1,
                SettleMs = r.TryGetLong("settle_ms", out long settleMs) && settleMs >= 0 ? settleMs : -1,
                SettleMeasured = r.GetBool("settle_measured"),
            };
            UpdateTxState(s => { s.Taken = true; s.Prepared = prepared; });
            Log($"the transmitter is prepared at {Mhz(eff)} MHz ({_txBand.Name}), LO {Mhz(lo)} MHz" +
                (path.Length > 0 ? $", by {path}" : "") +
                (r.TryGetLong("took_ms", out long took) ? $" in {took} ms" : "") +
                (r.TryGetLong("settle_ms", out long settle) ? $"; its carrier settles for about {settle} ms" : ""));
            return (eff, lo);
        }
    }

    /// <summary>RX off, the controller gone, the service up. Under <see cref="_handoverLock"/>.</summary>
    private void TakeForTransmit()
    {
        W1700kServe? serve;
        lock (_unitLock) serve = _serve;
        if (serve == null) throw new RadioRefusal(ErrorCodes.NotReady, "the unit's controller is not running");
        Log("taking the radio for its transmitter: reception stops until the transmitter is released");
        _taken = true;                        // the watch and the unit's state events stand down from here
        bool ours = _streamOurs;
        _streamOurs = false;
        _accept = false;
        try
        {
            if (ours) serve.Request(Req("stop"), _opt.CommandTimeoutMs);
        }
        catch (Exception ex)
        {
            _accept = true;
            _streamOurs = ours;
            _taken = false;
            throw new RadioRefusal(ErrorCodes.RadioFault,
                "the receive stream could not be stopped, so the transmitter was not taken: " + (ex is W1700kServeException se ? se.Said : ex.Message));
        }
        // NEVER an unload: the service's first preparation ADOPTS the radio as this controller leaves it - loaded, its stream
        // stopped - with one band's setup in place of a reload (about a second instead of six to twelve).
        lock (_unitLock) _serve = null;
        serve.Dispose();                      // its input closes and it EXITS
        try
        {
            var tx = NewTxServe();
            tx.Exited += why => OnTxServiceLost(tx, why);
            _txServe = tx;
            _txTouchedUnit = false;
        }
        catch (Exception ex)
        {
            string words = "the transmit service did not start, so the transmitter was not taken: " + (ex is W1700kTxRefusal r ? r.Said : ex.Message);
            GiveBackLocked("the transmit service did not start");
            throw new RadioRefusal(ErrorCodes.RadioFault, words + "; the radio was given back to its receiver");
        }
    }

    /// <summary>The TX owner let go, or its connection closed (<see cref="ITxHandoverBackend"/>): the claim was the take's lifetime.</summary>
    public void TxOwnerLeft()
    {
        lock (_handoverLock)
        {
            _txGainDb = double.NaN;           // the next owner states its own level
            if (!_taken) return;
            GiveBackLocked("its owner released it");
        }
        UpdateState(_ => { }, announce: true);
    }

    /// <summary>The service ended under a taken radio and not by this side: the unit unkeys by itself, and the radio goes back
    /// to its receiver. Never on the caller's thread - the caller is the service's own reader, or a transmission's sender - and
    /// never on the thread pool: a pool that is busy or starved would leave the radio neither receiver nor transmitter.</summary>
    internal void OnTxServiceLost(W1700kTxServe serve, string why = "")
    {
        new Thread(() =>
        {
            lock (_handoverLock)
            {
                if (_closing || !ReferenceEquals(_txServe, serve)) return;
                Log("the transmit service is gone" + (why.Length > 0 ? $" ({why})" : "") + ": the unit unkeys by itself");
                GiveBackLocked("the transmit service ended");
            }
            UpdateState(_ => { }, announce: true);
        })
        { IsBackground = true, Name = "W1700kGiveBack" }.Start();
    }

    /// <summary>The service closed (it unkeys, closes its station and exits), then the receiver restored. Under <see cref="_handoverLock"/>.</summary>
    private void GiveBackLocked(string why)
    {
        var tx = _txServe;
        _txServe = null;
        bool load = _txTouchedUnit;
        _txTouchedUnit = false;
        _txBand = null;
        tx?.Dispose();
        Log($"the transmitter is released ({why}): giving the radio back to its receiver" + (load ? " - a load, since the preparation disconnected its capture" : ""));
        RestoreReceive(load);
        // Only now: until reception is back the radio is still not a receiver, and its state must not say it is.
        UpdateTxState(s =>
        {
            s.Taken = false;
            s.Prepared = null;
            s.CenterHz = 0;
            s.LoHz = 0;
        });
        _taken = false;
    }

    /// <summary>The controller started again and the receive stream with it, at the LO, rate, gain and filter the radio had.
    /// After a preparation only a LOAD restores reception (SDRW1700K docs/08 T12), and a load puts every band at the unit's own
    /// rate, gain and filter - so each is stated again. A radio that cannot be given back is a FAULT, in words.</summary>
    private void RestoreReceive(bool load)
    {
        if (_closing) return;
        var before = GetState();
        int band;
        lock (_unitLock) band = _band;
        try
        {
            var serve = _opt.ServeFactory != null ? _opt.ServeFactory() : W1700kServe.Spawn(_opt.ServeCommand!, Log);
            lock (_unitLock) _serve = serve;
            serve.StateChanged += OnUnitState;
            serve.Exited += why => { if (!_closing) Fault($"w1700k serve ended under the radio: {why}"); };
            serve.Start();
            serve.WaitAttached(_opt.AttachTimeoutMs);
            if (load) serve.Request(Req("load").Set("band", band), _opt.LoadTimeoutMs);
            var st = serve.Request(Req("configure").Set("band", band).Set("sample_rate", (long)Math.Round(before.WideRateHz)).Set("lo_hz", before.LoHz),
                                   _opt.LoadTimeoutMs);
            if (!before.Agc)
            {
                var (lna, lpf) = GainFor(before.GainDb);
                st = serve.Request(Req("gain").Set("agc", false).Set("lna", lna).Set("lpf", lpf), _opt.CommandTimeoutMs);
            }
            if (before.BandwidthHz > 0)
                st = serve.Request(Req("filter").Set("mhz", FilterFor(before.BandwidthHz)), _opt.CommandTimeoutMs);
            TakeUnitState(st, announce: false);
            _resync = true;
            _streamSeenRunning = false;
            _accept = true;
            _streamOurs = true;
            st = serve.Request(Req("start"), _opt.LoadTimeoutMs);
            TakeUnitState(st, announce: false);
            Volatile.Write(ref _watchFromTs, Stopwatch.GetTimestamp());
            Log($"reception is back: band {band}, {Mhz(LoHz)} MHz at {WideRateHz:0} S/s");
        }
        catch (Exception ex)
        {
            Fault("the radio could not be given back to its receiver: " + (ex is W1700kServeException se ? se.Said : ex.Message));
        }
    }

    /// <summary>The unit's own sample clock as its feeder measured it over a key-down: telemetry, never arithmetic.</summary>
    internal void NoteUnitTxRate(double hz) => Volatile.Write(ref _unitTxRateHz, hz);

    protected override (long CenterHz, long LoHz, string Retune) ApplyTxPark(long centerHz, long loOffsetHz, RadioTransmission open) =>
        throw new RadioRefusal(ErrorCodes.Unsupported,
            "this transmitter cannot move a keyed carrier: a new frequency is a new preparation of the unit - end the transmission (tx_stop) first");

    protected override double TxTickHz => 0;

    protected override (long CenterHz, long LoHz) ApplyTxStepTune(long carrierHz, long loHz, bool moveLo) =>
        throw new RadioRefusal(ErrorCodes.Unsupported, "this transmitter runs no stepped sweep: every step would be a new preparation of the unit");

    /// <summary>The service's <c>start</c>, then the sender. An IQ transmission states envelope <c>any</c> - ShackLink scales and
    /// shapes its samples - so above a band's linear limit the SERVICE refuses it, in its words (T9).</summary>
    protected override RadioTransmission OpenTransmission(TxStartRequest request, TxGrant grant, TxStepPlan? plan, TxSignal? signal)
    {
        var info = TxInfo();
        W1700kTxServe tx;
        W1700kTxBand band;
        double gain;
        lock (_handoverLock)
        {
            if (!_taken || _txServe == null || _txBand == null)
                throw new RadioRefusal(ErrorCodes.NotReady,
                    "the radio's transmitter is not taken: set_tx a centre first - that stops its reception and prepares the unit, and is never part of a key-up");
            tx = _txServe;
            band = _txBand;
            gain = _txGainDb;
        }
        if (double.IsNaN(gain))
            throw new RadioRefusal(ErrorCodes.BadRequest, "the TX gain was never set (set_tx gain_db): a transmission keys at a stated level");
        if (request.IsGenerated && info.PassbandHalfWidthHz > 0 && Math.Abs((double)request.OffsetHz) > info.PassbandHalfWidthHz)
            throw new RadioRefusal(ErrorCodes.OutOfRange,
                $"a carrier {request.OffsetHz} Hz from the centre is outside this transmitter's passband of +-{info.PassbandHalfWidthHz:0} Hz: " +
                "it would be filtered away and the unit would key on nothing - prepare the unit at the frequency itself");
        double level = Math.Min(band.LevelMaxDb, band.LevelMinDb + gain);
        var start = Req("start").Set("max_ms", request.MaxMs).Set("level_db", level).Set("envelope", "any");
        JsonObject r;
        try
        {
            r = tx.Request(start, _opt.TxRequestTimeoutMs);
        }
        catch (W1700kTxRefusal ex)
        {
            throw new RadioRefusal(ErrorCodes.OutOfRange, ex.Said);
        }
        catch (Exception ex) when (ex is TimeoutException || ex is IOException)
        {
            throw new RadioRefusal(ErrorCodes.RadioFault, "the unit's transmitter did not start: " + ex.Message);
        }
        double realised = r.GetDouble("level_db", level);
        grant.GainDb = realised - band.LevelMinDb;
        UpdateTxState(s => s.GainDb = grant.GainDb);
        grant.MaxBlockSamples = info.MaxBlockSamples;
        if (!request.IsGenerated) grant.QueueSamples = Math.Max(info.MaxBlockSamples * 2, (int)Math.Ceiling(info.InputRateHz * 0.05));
        Log($"the unit's transmitter is armed at level {realised.ToString("0.#", CultureInfo.InvariantCulture)} dB under {band.Name}'s most" +
            (r.TryGetDouble("leakage_dbc", out double leak) ? $", its carrier leakage about {leak.ToString("0.#", CultureInfo.InvariantCulture)} dBc" : ""));
        return new W1700kTransmission(this, tx, info.MaxBlockSamples, request, grant, signal);
    }
}
