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
            ? new SimBackend(log, opt.SimName, opt.RateHz, opt.LoHz) { FaultAfterMs = opt.SimFaultAfterMs }
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

        var server = new UsrpServer(backend, new UsrpServerOptions
        {
            BindAddress = opt.Listen,
            Port = opt.Port,
            LingerSeconds = opt.LingerSeconds,
            FirstClientTimeoutSeconds = opt.FirstClientTimeoutSeconds,
            Persist = opt.Persist,
            SidecarVersion = Version,
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
    /// serves each (its rendezvous file). A B2xx another process holds may be missing from UHD's answer.</summary>
    private static int RunList(SidecarOptions opt)
    {
        try
        {
            UhdLoader.Load(new UhdLoadOptions { UhdDirectory = opt.UhdDirectory, ImagesDirectory = opt.ImagesDirectory },
                           opt.Console ? Console.Error.WriteLine : null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"error: UHD could not be loaded: {ex.Message}");
            return 1;
        }
        var found = UhdUsrp.Find("type=b200");
        var serials = new List<string>();
        foreach (var dev in found)
        {
            var kv = dev.Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
                        .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);
            kv.TryGetValue("serial", out var serial);
            kv.TryGetValue("product", out var product);
            kv.TryGetValue("name", out var name);
            serial ??= "";
            serials.Add(serial);
            string busy = "free";
            if (serial.Length > 0 && Rendezvous.TryRead(serial, out var r) && Rendezvous.IsProcessAlive(r.Pid))
                busy = $"served by pid {r.Pid} on port {r.Port}";
            Console.WriteLine($"usrp:{serial}  product={product}  name={name}  {busy}");
        }
        foreach (var dup in serials.GroupBy(s => s).Where(g => g.Count() > 1))
            Console.WriteLine($"warning: {dup.Count()} devices answer serial {dup.Key}; clones ship one EEPROM serial - re-serialise one with b2xx_fx3_utils");
        if (found.Count == 0) Console.WriteLine("no B2xx found");
        return 0;
    }
}

/// <summary>The command line. Every option a spawner passes is here (the MIT library's
/// SidecarLocator.Arguments composes the same spellings).</summary>
internal sealed class SidecarOptions
{
    public const string Usage =
        "ShackLinkUsrp --serial <serial> | --sim [--sim-name <name>] [--sim-fault-after <ms>] | --list\n" +
        "  --sim-fault-after <ms>      (tests) the synthetic radio faults this long after it starts streaming\n" +
        "  --uhd <dir>                 load uhd.dll from <dir> (default: C:\\Program Files\\UHD\\bin)\n" +
        "  --images <dir>              UHD_IMAGES_DIR (default: the Ettus install's images)\n" +
        "  --fpga <path>               FPGA image for a clone board\n" +
        "  --args <k=v,...>            extra UHD device arguments\n" +
        "  --allow-known-codec-fault   a patched UHD may open a board whose CODEC loopback fails; its samples are NOT trustworthy\n" +
        "  --otw sc16|sc8              the USB sample format (sc8 is clean to 20 MS/s on Windows, sc16 to 10)\n" +
        "  --rate <S/s>                initial wide rate (default 10e6)\n" +
        "  --lo <Hz>                   initial LO (default 100e6)\n" +
        "  --dc-offset <Hz>            RF LO offset compensated in the FPGA DDC (default 0)\n" +
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
    public bool List;
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
                case "--list": o.List = true; break;
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
        if (!o.Help && !o.List)
        {
            if (o.Sim == (o.Serial != null)) throw new ArgumentException("give exactly one of --serial <serial> or --sim");
            if (o.SimFaultAfterMs > 0 && !o.Sim)
                throw new ArgumentException("--sim-fault-after is for the synthetic radio (--sim); a real radio faults on its own");
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
