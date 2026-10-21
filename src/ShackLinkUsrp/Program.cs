// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// The sidecar executable. The wire it speaks is specified in ShackLink's
// docs/usrp_b210_support.md section 9 and implemented by the MIT library ShackLinkUsrp.Protocol;
// this program supplies the radio (UHD or synthetic) and the DSP behind it.

using System.Globalization;
using System.Net;
using ShackLinkUsrp.Client;
using ShackLinkUsrp.Host;
using ShackLinkUsrp.Radio;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Uhd;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp;

internal static class Program
{
    public const string Version = "ShackLinkUsrp 0.1.0";

    private static int Main(string[] args)
    {
        SidecarOptions opt;
        try
        {
            opt = SidecarOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(SidecarOptions.Usage);
            return 2;
        }
        if (opt.Help)
        {
            Console.WriteLine(SidecarOptions.Usage);
            return 0;
        }
        if (opt.List) return RunList(opt);
        return RunServer(opt);
    }

    private static int RunServer(SidecarOptions opt)
    {
        // The rendezvous key is the address grammar's (one owner): sim-<name>, iiod-<hw_serial>, w1700k-<name> or the B2xx serial.
        string key = opt.Address().RendezvousKey!;
        if (opt.StateDirectory != null) Rendezvous.RootOverride = opt.StateDirectory;
        using var log = new SidecarLog(Rendezvous.LogPathFor(key), console: opt.Console);
        log.Write($"{Version} (GPL-3.0-or-later) pid {Environment.ProcessId}: {string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}");
        // Before any thread of the stream starts (ShackLink docs/usrp_b210_support.md section 9.33).
        string throttling = PowerThrottling.OptOut();
        if (throttling.Length > 0) log.Write(throttling);

        RadioBackendBase backend = opt.W1700k
            // A W1700K (C:\Dev\SDRW1700K docs/05 option 2): its export received here, its controller upstream's.
            ? new W1700kBackend(log, new W1700kBackendOptions
            {
                Name = opt.W1700kName,
                Bind = opt.W1700kBind,
                ServeCommand = opt.W1700kServe,
                RatesHz = opt.W1700kRatesHz,
                WideRateHz = opt.RateHz,
                LoHz = opt.LoHz,
                SettleMs = opt.W1700kSettleMs,
                Flatten = opt.W1700kFlatten,
                RemoveDc = opt.W1700kRemoveDc,
            })
            : opt.Sim
            ? new SimBackend(log, opt.SimName, opt.RateHz, opt.LoHz) { FaultAfterMs = opt.SimFaultAfterMs, SettleMs = opt.SimSettleMs, TxCouplingDb = opt.SimTxCouplingDb, TxCouplingDbB = opt.SimTxCouplingDbB, SideBGainDb = opt.SimSideBGainDb, SideBPhaseDeg = opt.SimSideBPhaseDeg }
            : opt.Iiod != null
            // A Pluto-class board: no UHD is loaded for it (ShackLink docs/plutosdr_support.md section 9.1 B2).
            ? new PlutoBackend(log, new PlutoBackendOptions
            {
                Serial = opt.Address().Serial,
                AddressHint = opt.Iiod == SidecarLocator.IiodBrowse || opt.IiodUsb ? null : opt.Iiod,
                Usb = opt.IiodUsb,
                WideRateHz = opt.RateHz,
                LoHz = opt.LoHz,
            })
            : new UhdBackend(log, new UhdBackendOptions
            {
                Serial = opt.Serial!,
                UhdDirectory = opt.UhdDirectory,
                ImagesDirectory = opt.ImagesDirectory,
                FpgaPath = opt.FpgaPath,
                ExtraDeviceArgs = opt.DeviceArgs,
                AllowKnownCodecFault = opt.AllowKnownCodecFault,
                OtwFormat = opt.OtwFormat,
                WideRateHz = opt.RateHz,
                LoHz = opt.LoHz,
                DcOffsetHz = opt.DcOffsetHz,
            });
        if (opt.DualRatesHz.Length > 0)
            log.Write("--dual-rates is ignored: every native rate the two-side clock admits is offered now (ShackLink docs/usrp_b210_support.md 9.27)");

        var server = new UsrpServer(backend, new UsrpServerOptions
        {
            BindAddress = opt.Listen,
            Port = opt.Port,
            LingerSeconds = opt.LingerSeconds,
            FirstClientTimeoutSeconds = opt.FirstClientTimeoutSeconds,
            Persist = opt.Persist,
            SidecarVersion = Version,
            TxMaxMs = opt.TxMaxMs,
            TxStarveMs = opt.TxStarveMs,
            Log = log.Write,
        });

        var exit = new ManualResetEventSlim();
        string reason = "";
        void Quit(string why)
        {
            if (reason.Length == 0) reason = why;
            exit.Set();
        }
        server.IdleExpired += Quit;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Quit("Ctrl+C");
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Quit("process exit");

        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            log.Write($"cannot listen on {opt.Listen}:{opt.Port}: {ex.Message}");
            backend.Dispose();
            return 3;
        }

        // Published BEFORE the radio opens: a client may join at once and read "opening" while a
        // bitstream loads, instead of guessing from silence (section 9.5).
        var record = new RendezvousRecord
        {
            Key = key,
            Serial = backend.Info.Serial,
            Pid = Environment.ProcessId,
            Port = server.Port,
            Token = opt.SpawnToken ?? "",
            StartedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Exe = Environment.ProcessPath ?? "",
        };
        bool published = false;
        if (opt.Listen.Equals(IPAddress.Loopback) || opt.Listen.Equals(IPAddress.Any))
        {
            try
            {
                Rendezvous.Write(record);
                published = true;
            }
            catch (Exception ex)
            {
                log.Write("rendezvous file not written: " + ex.Message);
            }
        }
        log.Write($"listening on {opt.Listen}:{server.Port} for radio {key}" + (published ? $" (rendezvous {Rendezvous.PathFor(key)})" : ""));
        if (opt.Console) Console.WriteLine($"SHACKLINKUSRP LISTENING {server.Port}");

        backend.Start();
        exit.Wait();

        log.Write("stopping: " + reason);
        server.Stop(reason);
        if (published) Rendezvous.Delete(key, Environment.ProcessId);
        server.Dispose();
        backend.Dispose();
        log.Write("stopped");
        return 0;
    }

    /// <summary>Radios present, without opening any (W7): UHD's find, and whether a sidecar already
    /// serves each (its rendezvous file). A B2xx another process holds may be missing from UHD's answer.
    /// The find loads FX3 firmware into every cold board it can claim - which is how a catalogue that asks
    /// for this gets a serial for a board fresh from power-up (ShackLink docs/usrp_b210_support.md 9.26).
    /// With <c>--json &lt;file&gt;</c> the answer is written there, in the library's spelling, whatever
    /// happened; a spawner hands a child no pipe (SidecarSpawner, D6).</summary>
    private static int RunList(SidecarOptions opt)
    {
        Action<string>? log = opt.Console ? Console.Error.WriteLine : null;
        try
        {
            UhdLoader.Load(new UhdLoadOptions { UhdDirectory = opt.UhdDirectory, ImagesDirectory = opt.ImagesDirectory }, log);
        }
        catch (Exception ex)
        {
            return FinishList(opt, new DiscoveryResult { Error = $"UHD could not be loaded: {ex.Message}" }, 1);
        }
        IReadOnlyList<string> found;
        try
        {
            using (UhdDeviceOpsLock.Acquire(UhdDeviceOpsLock.DefaultWait, log))
                found = UhdUsrp.Find("type=b200");
        }
        catch (Exception ex)
        {
            return FinishList(opt, new DiscoveryResult { Error = $"UHD's find failed: {ex.Message}", UhdVersion = UhdLoader.Version }, 1);
        }
        var result = ListAnswer.From(found,
            serial => Rendezvous.TryRead(serial, out var r) && Rendezvous.IsProcessAlive(r.Pid) ? r.Pid : 0,
            UhdLoader.Version);
        return FinishList(opt, result, 0);
    }

    private static int FinishList(SidecarOptions opt, DiscoveryResult result, int exitCode)
    {
        if (opt.JsonPath == null)
        {
            foreach (var line in ListAnswer.HumanLines(result)) Console.WriteLine(line);
            return exitCode;
        }
        try
        {
            // Written whole then renamed, so a reader never sees half an answer.
            string tmp = opt.JsonPath + ".tmp";
            File.WriteAllText(tmp, SidecarDiscovery.Compose(result));
            File.Move(tmp, opt.JsonPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"the list could not be written to {opt.JsonPath}: {ex.Message}");
            return 3;
        }
        return exitCode;
    }
}

/// <summary>The command line. Every option a spawner passes is here (the MIT library's
/// SidecarLocator.Arguments composes the same spellings).</summary>
internal sealed class SidecarOptions
{
    public const string Usage =
        "ShackLinkUsrp --serial <serial> | --serial <hw_serial> --iiod <host[:port]|mdns|usb:<hw_serial>> | --sim [--sim-name <name>] [--sim-fault-after <ms>] [--sim-settle-ms <ms>] [--sim-tx-coupling <dB>] | --w1700k [--w1700k-...] | --list [--json <file>]\n" +
        "  --w1700k                    a Gemtek W1700K running h0m3us3r/w1700k-sdr: its UDP export received here, receive only (--rate default 40e6,\n" +
        "                              --lo default 2442e6). Serve with --port and --persist, and join it from ShackLink as usrp://127.0.0.1:<port>\n" +
        "  --w1700k-name <name>        the radio's name in its serial (w1700k-<name>) and its log (default unit)\n" +
        "  --w1700k-bind <ip:port>     where the export arrives (default 10.77.0.10:7700, the unit's fixed destination)\n" +
        "  --w1700k-serve <cmd|none>   upstream's controller, spoken to as JSON lines (default: the bundled w1700k.exe beside this program, serve;\n" +
        "                              a WSL checkout is wsl.exe -d Ubuntu-24.04 -- bash -lc \"cd ~/w1700k-sdr && exec host/bin/w1700k serve\");\n" +
        "                              none = receive the stream another program drives and change nothing on the unit\n" +
        "  --w1700k-config <file>      a station's own config for the controller, given to it as --config \"<file>\" before serve (default none:\n" +
        "                              the controller's own %USERPROFILE%\\.config\\w1700k\\config.json; a second router uses a file of its own)\n" +
        "  --w1700k-rates <S/s,...>    the wide rates offered (default every rate the unit has: 40e6,80e6,160e6,320e6; the operator picks one, and a\n" +
        "                              rate above what the link carries is lost, since the export never throttles - 40 needs 2.5 GbE, 80 5 GbE, 320 10 GbE)\n" +
        "  --w1700k-settle-ms <ms>     samples skipped after a change, as milliseconds in flight from the unit (default 20, stated, not measured)\n" +
        "  --w1700k-flatten on|off     correct the decimation's roll-off at the band's edges, in the samples (default on; the setting flatten)\n" +
        "  --w1700k-dc on|off          take the LO's leak out of the centre (default on; the setting dc_removal)\n" +
        "  --iiod <host[:port]|mdns>   a Pluto-class board behind iiod, by its hw_serial: tried at this address first (a HINT), then\n" +
        "                              re-found by an _iio._tcp browse matched on the serial; mdns = no address known, browse at once.\n" +
        "  --iiod usb:<hw_serial>      ...or over the board's OWN USB pipes through WinUSB: no address, no browse; the serial is --serial's\n" +
        "                              Loads no UHD, and takes none of UHD's options\n" +
        "  --list                      UHD's find: every B2xx it can claim, loading firmware into a cold one\n" +
        "  --json <file>               (with --list) write the answer there as one JSON object, whatever happened\n" +
        "  --sim-fault-after <ms>      (tests) the synthetic radio faults this long after it starts streaming\n" +
        "  --sim-settle-ms <ms>        (tests) a synthetic radio change takes effect this long after it is asked for, like in-flight USB buffers\n" +
        "  --sim-tx-coupling <dB>      how the synthetic radio hears its own transmission: L dBFS at a TX gain of G is heard at L + G + this (default -80)\n" +
        "  --sim-side-b-gain-db <dB>   what the synthetic radio's RX B hears the scene at, relative to RX A (default -6)\n" +
        "  --sim-side-b-phase-deg <d>  ...and at which phase from RX A (default 30)\n" +
        "  --sim-tx-coupling-b <dB>    how the synthetic radio's RX B hears its own 2R2T transmission, as --sim-tx-coupling (default -90)\n" +
        "  --tx-max-ms <ms>            the longest any transmission may run, whatever a client asks (default 3600000)\n" +
        "  --tx-starve-ms <ms>         a keyed IQ transmission that receives no block this long is ended (default 500)\n" +
        "  --uhd <dir>                 load uhd.dll from <dir> (default: C:\\Program Files\\UHD\\bin)\n" +
        "  --images <dir>              UHD_IMAGES_DIR (default: the Ettus install's images)\n" +
        "  --fpga <path>               FPGA image for a clone board\n" +
        "  --args <k=v,...>            extra UHD device arguments\n" +
        "  --allow-known-codec-fault   a patched UHD may open a board whose CODEC loopback fails; its samples are NOT trustworthy\n" +
        "  --otw sc16|sc8              the USB sample format: sc8 halves the USB load at 8-bit resolution (every rate is offered in both)\n" +
        "  --rate <S/s>                initial wide rate (default 10e6)\n" +
        "  --lo <Hz>                   initial LO (default 100e6)\n" +
        "  --dc-offset <Hz>            RF LO offset compensated in the FPGA DDC (default 0)\n" +
        "  --dual-rates <S/s,...>      (obsolete, ignored) every native rate the two-side clock admits is offered\n" +
        "  --port <n>                  listening port (default 0 = ephemeral, published in the rendezvous file)\n" +
        "  --listen <ip>               bind address (default 127.0.0.1)\n" +
        "  --linger <s>                exit this long after the last client leaves (default 10)\n" +
        "  --first-client-timeout <s>  exit if no client connects within this long (default 30)\n" +
        "  --persist                   never exit for want of clients (a server started by hand)\n" +
        "  --spawn-token <t>           echoed into the rendezvous file for the spawner\n" +
        "  --console                   also log to the console\n" +
        "  --state-dir <dir>           rendezvous and log root (default %LocalAppData%\\ShackLinkUsrp; tests redirect it)";

    public string? Serial;
    /// <summary><c>--iiod</c>: a Pluto-class board's address hint, <see cref="SidecarLocator.IiodBrowse"/>, or
    /// <c>usb:&lt;hw_serial&gt;</c> (<see cref="IiodUsb"/>); null = not one.</summary>
    public string? Iiod;

    /// <summary><c>--iiod usb:&lt;hw_serial&gt;</c>: over the board's own USB pipes (ShackLink docs/plutosdr_support.md 9.26 D14). Tested
    /// BEFORE host[:port], the longer-prefix rule.</summary>
    public bool IiodUsb => Iiod != null && Iiod.StartsWith(SidecarLocator.IiodUsbPrefix, StringComparison.OrdinalIgnoreCase);
    public bool Sim;
    public string SimName = UsrpAddress.DefaultSimName;
    public int SimFaultAfterMs;
    public int SimSettleMs;
    public double SimTxCouplingDb = SimBackend.DefaultTxCouplingDb;
    public double SimTxCouplingDbB = SimBackend.DefaultTxCouplingDbB;
    public double SimSideBGainDb = SimBackend.DefaultSideBGainDb;
    public double SimSideBPhaseDeg = SimBackend.DefaultSideBPhaseDeg;
    public bool SimTxCouplingSet;
    public long TxMaxMs = ProtocolConstants.DefaultTxMaxMs;
    public int TxStarveMs = ProtocolConstants.DefaultTxStarveMs;
    public bool List;
    /// <summary><c>--json &lt;file&gt;</c>: where <c>--list</c> writes its answer (ShackLink docs/usrp_b210_support.md 9.26 D2).</summary>
    public string? JsonPath;
    public bool Help;
    public string? UhdDirectory;
    public string? ImagesDirectory;
    public string? FpgaPath;
    public string? DeviceArgs;
    public bool AllowKnownCodecFault;
    public string OtwFormat = "sc16";
    public bool OtwSet;
    public double RateHz = 10e6;
    public long LoHz = 100_000_000;
    public long DcOffsetHz;
    public double[] DualRatesHz = Array.Empty<double>();
    public int Port;
    public IPAddress Listen = IPAddress.Loopback;
    public int LingerSeconds = ProtocolConstants.DefaultLingerSeconds;
    public int FirstClientTimeoutSeconds = ProtocolConstants.DefaultFirstClientTimeoutSeconds;
    public bool Persist;
    public string? SpawnToken;
    public bool Console;
    public string? StateDirectory;
    public bool RateSet, LoSet;
    public bool W1700k;
    public string W1700kName = "unit";
    public IPEndPoint W1700kBind = ShackLinkUsrp.Client.W1700kDefaults.BindEndPoint();
    public bool W1700kBindSet;
    /// <summary>Null = none: no controller.</summary>
    public string? W1700kServe = ShackLinkUsrp.Radio.W1700kServe.DefaultCommand;
    public bool W1700kServeSet;
    /// <summary>A station's own config file for the controller ("" = the controller's default folder), put before its subcommand.</summary>
    public string W1700kConfig = "";
    public double[] W1700kRatesHz = ShackLinkUsrp.Client.W1700kDefaults.UnitRatesHz();
    public bool W1700kRatesSet;
    public int W1700kSettleMs = 20;
    public bool W1700kSettleSet;
    public bool W1700kFlatten = true, W1700kRemoveDc = true, W1700kConditionSet;

    public static SidecarOptions Parse(string[] args)
    {
        var o = new SidecarOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"{a} needs a value");
                return args[++i];
            }
            switch (a)
            {
                case "--serial": o.Serial = Next(); break;
                case "--iiod": o.Iiod = Next().Trim(); break;
                case "--sim": o.Sim = true; break;
                case "--sim-name": o.SimName = Next(); break;
                case "--sim-fault-after": o.SimFaultAfterMs = Int(a, Next()); break;
                case "--sim-settle-ms": o.SimSettleMs = Int(a, Next()); break;
                case "--sim-tx-coupling":
                    {
                        string v = Next();
                        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out o.SimTxCouplingDb) ||
                            double.IsNaN(o.SimTxCouplingDb) || double.IsInfinity(o.SimTxCouplingDb))
                            throw new ArgumentException($"--sim-tx-coupling needs a number of decibels, not \"{v}\"");
                        o.SimTxCouplingSet = true;
                        break;
                    }
                case "--sim-tx-coupling-b":
                    {
                        string v = Next();
                        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out o.SimTxCouplingDbB) ||
                            double.IsNaN(o.SimTxCouplingDbB) || double.IsInfinity(o.SimTxCouplingDbB))
                            throw new ArgumentException($"--sim-tx-coupling-b needs a number of decibels, not \"{v}\"");
                        o.SimTxCouplingSet = true;
                        break;
                    }
                case "--sim-side-b-gain-db":
                    {
                        string v = Next();
                        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out o.SimSideBGainDb) ||
                            double.IsNaN(o.SimSideBGainDb) || double.IsInfinity(o.SimSideBGainDb))
                            throw new ArgumentException($"--sim-side-b-gain-db needs a number of decibels, not \"{v}\"");
                        break;
                    }
                case "--sim-side-b-phase-deg":
                    {
                        string v = Next();
                        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out o.SimSideBPhaseDeg) ||
                            double.IsNaN(o.SimSideBPhaseDeg) || double.IsInfinity(o.SimSideBPhaseDeg))
                            throw new ArgumentException($"--sim-side-b-phase-deg needs a number of degrees, not \"{v}\"");
                        break;
                    }
                case "--tx-max-ms":
                    o.TxMaxMs = Int(a, Next());
                    if (o.TxMaxMs < 1) throw new ArgumentException("--tx-max-ms must be at least 1: every transmission has a ceiling");
                    break;
                case "--tx-starve-ms":
                    o.TxStarveMs = Int(a, Next());
                    if (o.TxStarveMs < 1) throw new ArgumentException("--tx-starve-ms must be at least 1: a keyed radio fed nothing is ended");
                    break;
                case "--list": o.List = true; break;
                case "--json": o.JsonPath = Next(); break;
                case "--help": case "-h": case "/?": o.Help = true; break;
                case "--uhd": o.UhdDirectory = Next(); break;
                case "--images": o.ImagesDirectory = Next(); break;
                case "--fpga": o.FpgaPath = Next(); break;
                case "--args": o.DeviceArgs = Next(); break;
                case "--allow-known-codec-fault": o.AllowKnownCodecFault = true; break;
                case "--otw":
                    o.OtwFormat = Next();
                    o.OtwSet = true;
                    if (o.OtwFormat != "sc16" && o.OtwFormat != "sc8") throw new ArgumentException("--otw is sc16 or sc8");
                    break;
                case "--rate": o.RateHz = Double(a, Next()); o.RateSet = true; break;
                case "--dual-rates":
                    {
                        string v = Next();
                        var rates = new List<double>();
                        foreach (var part in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) || !(r > 0))
                                throw new ArgumentException($"--dual-rates needs a comma-separated list of rates in S/s, not \"{v}\"");
                            rates.Add(r);
                        }
                        o.DualRatesHz = rates.ToArray();
                        break;
                    }
                case "--lo": o.LoHz = (long)Double(a, Next()); o.LoSet = true; break;
                case "--w1700k": o.W1700k = true; break;
                case "--w1700k-name": o.W1700kName = Next(); break;
                case "--w1700k-bind":
                    {
                        string v = Next();
                        if (!IPEndPoint.TryParse(v, out var ep) || ep.Port == 0)
                            throw new ArgumentException($"--w1700k-bind needs ip:port, not \"{v}\"");
                        o.W1700kBind = ep;
                        o.W1700kBindSet = true;
                        break;
                    }
                case "--w1700k-serve":
                    {
                        string v = Next().Trim();
                        if (v.Length == 0) throw new ArgumentException("--w1700k-serve needs a command line, or none");
                        o.W1700kServe = string.Equals(v, "none", StringComparison.OrdinalIgnoreCase) ? null : v;
                        o.W1700kServeSet = true;
                        break;
                    }
                case "--w1700k-config":
                    {
                        string v = Next().Trim();
                        if (v.Length == 0) throw new ArgumentException("--w1700k-config needs a file");
                        o.W1700kConfig = v;
                        break;
                    }
                case "--w1700k-rates":
                    {
                        string v = Next();
                        var rates = new List<double>();
                        foreach (var part in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) || !W1700kRates.Contains(r))
                                throw new ArgumentException($"--w1700k-rates takes the unit's rates ({string.Join(", ", W1700kRates.Select(x => x.ToString("0", CultureInfo.InvariantCulture)))}), comma-separated - not \"{v}\"");
                            rates.Add(r);
                        }
                        if (rates.Count == 0) throw new ArgumentException("--w1700k-rates needs at least one rate");
                        o.W1700kRatesHz = rates.Distinct().OrderBy(r => r).ToArray();
                        o.W1700kRatesSet = true;
                        break;
                    }
                case "--w1700k-settle-ms": o.W1700kSettleMs = Int(a, Next()); o.W1700kSettleSet = true; break;
                case "--w1700k-flatten": o.W1700kFlatten = OnOff(a, Next()); o.W1700kConditionSet = true; break;
                case "--w1700k-dc": o.W1700kRemoveDc = OnOff(a, Next()); o.W1700kConditionSet = true; break;
                case "--dc-offset":
                    {
                        string v = Next();
                        if (!long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out o.DcOffsetHz))
                            throw new ArgumentException($"--dc-offset needs an integer number of hertz, not \"{v}\"");
                        break;
                    }
                case "--port": o.Port = Int(a, Next()); break;
                case "--listen":
                    if (!IPAddress.TryParse(Next(), out var ip)) throw new ArgumentException("--listen needs an IP address");
                    o.Listen = ip;
                    break;
                case "--linger": o.LingerSeconds = Int(a, Next()); break;
                case "--first-client-timeout": o.FirstClientTimeoutSeconds = Int(a, Next()); break;
                case "--persist": o.Persist = true; break;
                case "--spawn-token": o.SpawnToken = Next(); break;
                case "--console": o.Console = true; break;
                case "--state-dir": o.StateDirectory = Next(); break;
                default: throw new ArgumentException($"unknown option {a}");
            }
        }
        if (o.JsonPath != null && !o.List) throw new ArgumentException("--json <file> goes with --list");
        bool w1700kOption = o.W1700kBindSet || o.W1700kServeSet || o.W1700kRatesSet || o.W1700kSettleSet || o.W1700kConditionSet || o.W1700kName != "unit" ||
                           o.W1700kConfig.Length > 0;
        if (w1700kOption && !o.W1700k) throw new ArgumentException("the --w1700k-... options go with --w1700k");
        if (o.W1700k && !o.Help && !o.List)
        {
            if (o.Sim || o.Serial != null || o.Iiod != null)
                throw new ArgumentException("--w1700k is its own radio: not with --sim, --serial or --iiod");
            var uhdOnly = new List<string>();
            if (o.UhdDirectory != null) uhdOnly.Add("--uhd");
            if (o.ImagesDirectory != null) uhdOnly.Add("--images");
            if (o.FpgaPath != null) uhdOnly.Add("--fpga");
            if (o.DeviceArgs != null) uhdOnly.Add("--args");
            if (o.AllowKnownCodecFault) uhdOnly.Add("--allow-known-codec-fault");
            if (o.OtwSet) uhdOnly.Add("--otw");
            if (o.DcOffsetHz != 0) uhdOnly.Add("--dc-offset");
            if (uhdOnly.Count > 0)
                throw new ArgumentException($"{string.Join(", ", uhdOnly)} {(uhdOnly.Count == 1 ? "is a" : "are")} UHD option{(uhdOnly.Count == 1 ? "" : "s")}; a W1700K (--w1700k) loads no UHD");
            if (o.W1700kConfig.Length > 0)
            {
                // The ONE placement of --config is the protocol library's; refused in words where it has no place.
                string why = ShackLinkUsrp.Client.W1700kDefaults.WithConfig(o.W1700kServe ?? ShackLinkUsrp.Client.W1700kDefaults.NoController,
                                                                            o.W1700kConfig, out string composed);
                if (why.Length > 0) throw new ArgumentException("--w1700k-config: " + why);
                o.W1700kServe = composed;
            }
            if (!o.RateSet) o.RateHz = o.W1700kRatesHz[0];
            if (!o.LoSet) o.LoHz = 2_442_000_000;
            if (!o.W1700kRatesHz.Contains(o.RateHz))
                throw new ArgumentException($"--rate {o.RateHz.ToString("0", CultureInfo.InvariantCulture)} is not one of --w1700k-rates ({string.Join(", ", o.W1700kRatesHz.Select(r => r.ToString("0", CultureInfo.InvariantCulture)))})");
            try
            {
                o.Address();
            }
            catch (FormatException ex)
            {
                throw new ArgumentException("--w1700k-name: " + ex.Message);
            }
            return o;
        }
        if (!o.Help && !o.List)
        {
            if (o.Sim == (o.Serial != null)) throw new ArgumentException("give exactly one of --serial <serial> or --sim");
            if (o.SimFaultAfterMs > 0 && !o.Sim)
                throw new ArgumentException("--sim-fault-after is for the synthetic radio (--sim); a real radio faults on its own");
            if (o.SimSettleMs > 0 && !o.Sim)
                throw new ArgumentException("--sim-settle-ms is for the synthetic radio (--sim); a real radio's settle is its own");
            if (o.SimTxCouplingSet && !o.Sim)
                throw new ArgumentException("--sim-tx-coupling and --sim-tx-coupling-b are for the synthetic radio (--sim); a real radio's coupling is its own");
            if (o.Iiod != null)
            {
                if (o.Sim) throw new ArgumentException("--iiod is a real board; the synthetic radio is --sim");
                if (o.IiodUsb)
                {
                    // The serial is the ADDRESS's, never a second spelling of it: a usb: form naming another board is refused.
                    string usbSerial = o.Iiod.Substring(SidecarLocator.IiodUsbPrefix.Length).Trim();
                    if (o.Serial == null || !string.Equals(usbSerial, o.Serial.Trim(), StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException($"--iiod {o.Iiod} names a board other than --serial {o.Serial}: the USB form is usb:<the same hw_serial>");
                }
                else if (o.Iiod != SidecarLocator.IiodBrowse && !PlutoDiscovery.TryParseHostPort(o.Iiod, out _, out _))
                    throw new ArgumentException($"--iiod needs host[:port] (an IPv6 host as [addr]:port), {SidecarLocator.IiodBrowse} or {SidecarLocator.IiodUsbPrefix}<hw_serial>, not \"{o.Iiod}\"");
                // UHD's options mean nothing to an iiod radio: refused, never silently ignored.
                var uhdOnly = new List<string>();
                if (o.UhdDirectory != null) uhdOnly.Add("--uhd");
                if (o.ImagesDirectory != null) uhdOnly.Add("--images");
                if (o.FpgaPath != null) uhdOnly.Add("--fpga");
                if (o.DeviceArgs != null) uhdOnly.Add("--args");
                if (o.AllowKnownCodecFault) uhdOnly.Add("--allow-known-codec-fault");
                if (o.OtwSet) uhdOnly.Add("--otw");
                if (o.DcOffsetHz != 0) uhdOnly.Add("--dc-offset");
                if (uhdOnly.Count > 0)
                    throw new ArgumentException($"{string.Join(", ", uhdOnly)} {(uhdOnly.Count == 1 ? "is a" : "are")} UHD option{(uhdOnly.Count == 1 ? "" : "s")}; an iiod board (--iiod) loads no UHD");
            }
            try
            {
                o.Address();
            }
            catch (FormatException ex)
            {
                throw new ArgumentException(ex.Message);
            }
        }
        return o;
    }

    /// <summary>The radio this command line names, in the address grammar - the ONE spelling of its rendezvous key.</summary>
    public UsrpAddress Address() =>
        W1700k ? UsrpAddress.W1700k(W1700kName) : Sim ? UsrpAddress.Sim(SimName) : Iiod != null ? UsrpAddress.Iiod(Serial!) : UsrpAddress.Local(Serial!);

    /// <summary>The unit's sample rates (upstream <c>host/w1700k/radio.py</c> SAMPLE_RATES); band 2's 640/2 is not offered.</summary>
    public static readonly double[] W1700kRates = ShackLinkUsrp.Client.W1700kDefaults.UnitRatesHz();

    private static bool OnOff(string opt, string v) => v.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "1" => true,
        "off" or "false" or "0" => false,
        _ => throw new ArgumentException($"{opt} is on or off, not \"{v}\""),
    };

    private static double Double(string opt, string v) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0
            ? d : throw new ArgumentException($"{opt} needs a positive number, not \"{v}\"");

    private static int Int(string opt, string v) =>
        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0
            ? n : throw new ArgumentException($"{opt} needs a non-negative integer, not \"{v}\"");
}
