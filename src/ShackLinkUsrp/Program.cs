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
        string key = opt.Sim ? "sim-" + opt.SimName : opt.Serial!;
        if (opt.StateDirectory != null) Rendezvous.RootOverride = opt.StateDirectory;
        using var log = new SidecarLog(Rendezvous.LogPathFor(key), console: opt.Console);
        log.Write($"{Version} (GPL-3.0-or-later) pid {Environment.ProcessId}: {string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}");

        RadioBackendBase backend = opt.Sim
            ? new SimBackend(log, opt.SimName, opt.RateHz, opt.LoHz) { FaultAfterMs = opt.SimFaultAfterMs, SettleMs = opt.SimSettleMs, TxCouplingDb = opt.SimTxCouplingDb, TxCouplingDbB = opt.SimTxCouplingDbB, SideBGainDb = opt.SimSideBGainDb, SideBPhaseDeg = opt.SimSideBPhaseDeg }
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
        "ShackLinkUsrp --serial <serial> | --sim [--sim-name <name>] [--sim-fault-after <ms>] [--sim-settle-ms <ms>] [--sim-tx-coupling <dB>] | --list [--json <file>]\n" +
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
                    if (o.OtwFormat != "sc16" && o.OtwFormat != "sc8") throw new ArgumentException("--otw is sc16 or sc8");
                    break;
                case "--rate": o.RateHz = Double(a, Next()); break;
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
                case "--lo": o.LoHz = (long)Double(a, Next()); break;
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
        if (!o.Help && !o.List)
        {
            if (o.Sim == (o.Serial != null)) throw new ArgumentException("give exactly one of --serial <serial> or --sim");
            if (o.SimFaultAfterMs > 0 && !o.Sim)
                throw new ArgumentException("--sim-fault-after is for the synthetic radio (--sim); a real radio faults on its own");
            if (o.SimSettleMs > 0 && !o.Sim)
                throw new ArgumentException("--sim-settle-ms is for the synthetic radio (--sim); a real radio's settle is its own");
            if (o.SimTxCouplingSet && !o.Sim)
                throw new ArgumentException("--sim-tx-coupling and --sim-tx-coupling-b are for the synthetic radio (--sim); a real radio's coupling is its own");
            try
            {
                if (o.Sim) UsrpAddress.Sim(o.SimName);
                else UsrpAddress.Local(o.Serial!);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException(ex.Message);
            }
        }
        return o;
    }

    private static double Double(string opt, string v) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0
            ? d : throw new ArgumentException($"{opt} needs a positive number, not \"{v}\"");

    private static int Int(string opt, string v) =>
        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0
            ? n : throw new ArgumentException($"{opt} needs a non-negative integer, not \"{v}\"");
}
