// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// The BUILT-IN transmit service's gate (ShackLink docs/w1700k_support.md "The built-in transmit service", D1-D5), with no unit
// and NOTHING KEYED: the protocol library's one resolver over every launch value and every answer a bundled controller's
// --version can give; the wire's caps.tx_absent; the real backend and server resolving at open over the fake unit and the fake
// service; the --w1700k-tx-check instrument in process and as the real executable. The verb itself does not exist yet (the
// SDRW1700K sessions build it), so a controller that HAS it is a version line written here.

using System.Diagnostics;
using ShackLinkUsrp.Client;
using ShackLinkUsrp.Radio;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Tests;

internal static partial class Program
{
    /// <summary>The bundled controller's --version on the station that built this, 2026-11-10 (1.0.0, built 2026-10-21): verbatim,
    /// and a build WITHOUT the verb.</summary>
    private const string W1700kVersion100 =
        "{\"name\": \"w1700k\", \"version\": \"1.0.0\", \"upstream\": \"0b0413e4309f0c083702fa4eb2d1c2bca4532163\", \"windows_host\": 1, " +
        "\"patches\": \"403baa33dfa1120af39069240fbd5f64ca946a97\", \"firmware\": {\"source\": \"bundled\", \"dir\": " +
        "\"C:\\\\Dev\\\\SDRW1700K\\\\app\\\\dist\\\\W1700K\\\\firmware\", \"sha256\": \"e4bf866816c6cf402bf37cea863f1649eced5b57e9d70409edb7359f02cdd1a1\", " +
        "\"upstream\": \"0b0413e4309f0c083702fa4eb2d1c2bca4532163\"}, \"driver_abi\": 5, \"driver\": {\"sha256\": " +
        "\"381fe0547fea639dcb64c1b40ffa3b1198ba46e99017586671259b73a70b5489\"}}\r\n";

    /// <summary>A build WITH the verb, in the shape agreed with sdrw1700k-eb (2026-11-10): <c>tx_serve {wire, service, version}</c>.</summary>
    private static string W1700kVersionWithVerb(int wire = 1) =>
        "{\"name\": \"w1700k\", \"version\": \"1.1.0\", \"tx_serve\": {\"wire\": " + wire + ", \"service\": \"w1700k-tx\", \"version\": \"T25\"}}\n";

    /// <summary>A helper folder whose sibling W1700K\ is where the bundled controller would be (nothing is created there).</summary>
    private static string W1700kGateHelperDir() => Path.Combine(Path.GetTempPath(), "w1700k-builtin-gate", "UsrpSidecar");

    private static void W1700kBuiltInResolveCases()
    {
        string helper = W1700kGateHelperDir();
        string exe = W1700kDefaults.ControllerPathFor(helper);
        string builtIn = "\"" + exe + "\" tx-serve";
        int asked = 0;
        Func<string, string?> Reader(string? output) => path => { asked++; Check(path == exe, "the version is asked of the bundled controller beside the helper: " + path); return output; };
        W1700kTxChoice R(string tx, string serve, string config, Func<string, string?> read) => W1700kDefaults.ResolveTxServe(tx, serve, config, helper, read);

        // The launch value's three meanings (D1).
        Check(W1700kDefaults.TxSettingOf("") == W1700kTxSettings.BuiltIn && W1700kDefaults.TxSettingOf(null!) == W1700kTxSettings.BuiltIn &&
              W1700kDefaults.TxSettingOf(" None ") == W1700kTxSettings.Off && W1700kDefaults.TxSettingOf("python x.py") == W1700kTxSettings.Command,
              "D1: \"\" is Built in, none (any case) is Off, anything else a command");

        // Built in, on the bundled controller.
        asked = 0;
        var c = R("", "", "", Reader(W1700kVersionWithVerb()));
        Check(c.HasService && c.Command == builtIn && c.Setting == W1700kTxSettings.BuiltIn && c.Reason == "" && asked == 1,
              "Built in on the bundled controller that has the verb: its tx-serve, by full path: " + c.Command);
        c = R("", W1700kDefaults.ServeCommandIn(helper), "", Reader(W1700kVersionWithVerb()));
        Check(c.Command == builtIn, "...and the same when the bundled controller is TYPED as the controller (its exe is what decides)");
        c = R("", "", @"D:\routers\attic.json", Reader(W1700kVersionWithVerb()));
        Check(c.Command == "\"" + exe + "\" --config \"D:\\routers\\attic.json\" tx-serve",
              "D2: the station's config reaches the service, placed before tx-serve as it is before serve: " + c.Command);
        c = R("", "\"" + exe + "\" --config \"D:\\r2.json\" serve", "", Reader(W1700kVersionWithVerb()));
        Check(c.Command == "\"" + exe + "\" --config \"D:\\r2.json\" tx-serve",
              "...and a config the controller's own command names is the service's too (the same router): " + c.Command);

        // Built in, and the bundled controller cannot give one.
        asked = 0;
        c = R("", "", "", Reader(W1700kVersion100));
        Check(!c.HasService && c.Reason == W1700kTxAbsentReasons.NoVerb && c.Text.Contains("version 1.0.0") && c.Text.Contains("has no transmit service") && asked == 1,
              "X3: today's controller (1.0.0, no tx_serve) is NO TRANSMITTER in words, never a fallback: " + c.Text);
        c = R("", "", "", Reader(W1700kVersionWithVerb(2)));
        Check(c.Reason == W1700kTxAbsentReasons.Wire && c.Text.Contains("wire 2") && c.Text.Contains("wire 1"), "a service of another wire is not started: " + c.Text);
        c = R("", "", "", Reader(null));
        Check(c.Reason == W1700kTxAbsentReasons.NotInstalled && c.Text.Contains(exe), "a bundled controller that is not there: not installed, naming where: " + c.Text);
        c = R("", "", "", _ => throw new TimeoutException("it did not answer within 5000 ms"));
        Check(c.Reason == W1700kTxAbsentReasons.VersionUnread && c.Text.Contains("5000 ms"), "a --version that does not answer: unread, its own words: " + c.Text);
        c = R("", "", "", Reader("Traceback (most recent call last):\n  oops\n"));
        Check(c.Reason == W1700kTxAbsentReasons.VersionUnread && c.Text.Contains("no JSON line"), "a --version that prints no JSON: unread: " + c.Text);

        // No question asked of the controller where its answer could not matter.
        asked = 0;
        c = R("none", "", "", Reader(W1700kVersionWithVerb()));
        Check(c.Reason == W1700kTxAbsentReasons.Off && c.Setting == W1700kTxSettings.Off && asked == 0, "Off: no transmitter, and the controller is not asked: " + c.Text);
        c = R("", "none", "", Reader(W1700kVersionWithVerb()));
        Check(c.Reason == W1700kTxAbsentReasons.NoController && asked == 0, "no controller (none): nothing could take the radio or give it back: " + c.Text);
        c = R("", @"C:\Py\python.exe C:\co\host\bin\w1700k serve", "", Reader(W1700kVersionWithVerb()));
        Check(c.Reason == W1700kTxAbsentReasons.OwnController && asked == 0, "X1: a checkout controller gets no default: " + c.Text);
        c = R("", W1700kDefaults.WslServeCommand, "", Reader(W1700kVersionWithVerb()));
        Check(c.Reason == W1700kTxAbsentReasons.OwnController && asked == 0, "...nor a WSL one");
        c = R("python C:\\x\\w1700k-tx-serve.py", @"C:\Py\python.exe C:\co\host\bin\w1700k serve", "", Reader(W1700kVersionWithVerb()));
        Check(c.HasService && c.Command == "python C:\\x\\w1700k-tx-serve.py" && c.Setting == W1700kTxSettings.Command && asked == 0,
              "a command is the operator's, as typed, beside any controller, and asks the bundled one nothing");
        c = R("python C:\\x.py", "none", "", Reader(W1700kVersionWithVerb()));
        Check(c.Reason == W1700kTxAbsentReasons.NoController, "...but not with no controller");

        // A typed command and the station's config (D2).
        c = R("python C:\\x\\w1700k-tx-serve.py", "", @"D:\r2.json", Reader(null));
        Check(c.Reason == W1700kTxAbsentReasons.Config && c.Text.Contains("add --config \"D:\\r2.json\""),
              "a command that cannot be given the router's config is refused in words, naming the remedy: " + c.Text);
        c = R("python C:\\x\\w1700k-tx-serve.py --config \"D:\\r2.json\"", "", @"D:\r2.json", Reader(null));
        Check(c.HasService && c.Command == "python C:\\x\\w1700k-tx-serve.py --config \"D:\\r2.json\"", "...one that names the SAME config is taken as typed");
        c = R("python C:\\x\\w1700k-tx-serve.py --config D:\\other.json", "", @"D:\r2.json", Reader(null));
        Check(c.Reason == W1700kTxAbsentReasons.Config && c.Text.Contains("other.json") && c.Text.Contains("r2.json"), "...one that names ANOTHER is refused: " + c.Text);
        c = R("\"C:\\W\\w1700k.exe\" tx-serve", "", @"D:\r2.json", Reader(null));
        Check(c.Command == "\"C:\\W\\w1700k.exe\" --config \"D:\\r2.json\" tx-serve", "...and one ending in tx-serve has it placed: " + c.Command);

        // WithConfig keeps its old answers for serve (the verb is a parameter now, defaulting to serve).
        string why = W1700kDefaults.WithConfig("\"C:\\W\\w1700k.exe\" tx-serve", @"D:\r.json", out _);
        Check(why.Contains("does not end with its subcommand serve"), "WithConfig for serve still refuses a command that ends in tx-serve: " + why);
        why = W1700kDefaults.WithConfig("\"C:\\W\\w1700k.exe\" serve", @"D:\r.json", W1700kDefaults.TxServeVerb, out _);
        Check(why.Contains("does not end with its subcommand tx-serve"), "...and for tx-serve one that ends in serve: " + why);

        // The version line's reader.
        Check(W1700kDefaults.TryReadVersion(W1700kVersion100, out string v, out var t) && v == "1.0.0" && t == null,
              "the real 1.0.0 line reads as version 1.0.0 with no transmit service");
        Check(W1700kDefaults.TryReadVersion("a banner\r\n" + W1700kVersionWithVerb(), out v, out t) && v == "1.1.0" && t != null && t.Wire == 1 && t.Service == "w1700k-tx" && t.Version == "T25",
              "a build with the verb: its tx_serve read, the first JSON line taken past any other line");
        Check(!W1700kDefaults.TryReadVersion("", out _, out _) && !W1700kDefaults.TryReadVersion("{not json", out _, out _), "nothing, or not JSON, is not a version");
    }

    private static void W1700kBuiltInWireCases()
    {
        var plain = new RadioCaps();
        Check(!plain.ToJson().Has("tx_absent") && RadioCaps.Parse(plain.ToJson()).TxAbsent == null, "D3: caps.tx_absent is ABSENT for every other radio");
        var absent = new RadioCaps { TxAbsent = new TxAbsence { Reason = W1700kTxAbsentReasons.NoVerb, Text = "the words", Setting = W1700kTxSettings.BuiltIn } };
        var back = RadioCaps.Parse(Json.ParseObject(absent.ToJson().ToString()));
        Check(back.TxAbsent != null && back.TxAbsent.Reason == "no_verb" && back.TxAbsent.Text == "the words" && back.TxAbsent.Setting == "builtin" &&
              absent.Clone().TxAbsent!.Reason == "no_verb" && !ReferenceEquals(absent.Clone().TxAbsent, absent.TxAbsent),
              "caps.tx_absent round-trips {reason, text, setting}, and Clone copies it");
        var both = absent.Clone();
        both.Tx = new TxCaps { FreqMinHz = 1, FreqMaxHz = 2, RatesHz = new[] { 48000.0 }, MaxMs = 1000 };
        Check(!both.ToJson().Has("tx_absent"), "...and is never sent beside caps.tx");
        var bothJson = both.ToJson().Set("tx_absent", absent.TxAbsent!.ToJson());
        Check(RadioCaps.Parse(bothJson).TxAbsent == null, "...nor read beside it");
        Check(TxAbsence.Parse(new JsonObject().Set("text", "no reason")) == null && TxAbsence.Parse(null!) == null, "a tx_absent with no reason states nothing");
        Check(!new TxAbsence { Reason = "off" }.ToJson().Has("setting"), "an unsaid setting is absent, not empty");
    }

    /// <summary>The real backend and server resolving at open: what a hello says for each answer.</summary>
    private static void W1700kBuiltInOpenCases()
    {
        string helper = W1700kGateHelperDir();
        void Opt(W1700kBackendOptions o, string tx, string? version)
        {
            o.TxServeSetting = tx;
            o.HelperDirectory = helper;
            o.ReadControllerVersion = _ => version;
        }

        using (var rig = new W1700kTxRig("builtin-has", configure: o => Opt(o, "", W1700kVersionWithVerb())))
        using (var s = rig.Connect("w1700k-builtin"))
        {
            Check(s.WaitReady(10000) == RadioStates.Ready && s.Caps.Tx != null && s.Caps.Tx.TakesRadio && s.Caps.TxAbsent == null && rig.ServiceCount == 1 && rig.Service.Closed,
                  "X1: Built in, a controller with the verb: the radio states a transmitter (asked by its caps-only start, its input closed) and no tx_absent");
        }
        using (var rig = new W1700kTxRig("builtin-none", configure: o => Opt(o, "", W1700kVersion100)))
        using (var s = rig.Connect("w1700k-builtin"))
        {
            Check(s.WaitReady(10000) == RadioStates.Ready && s.Caps.Tx == null && rig.ServiceCount == 0 &&
                  s.Caps.TxAbsent?.Reason == W1700kTxAbsentReasons.NoVerb && s.Caps.TxAbsent.Setting == W1700kTxSettings.BuiltIn,
                  "X3: Built in, today's controller: no transmitter, no service started, and caps.tx_absent says no_verb, started built in: " + s.Caps.TxAbsent?.Text);
            s.ClaimLo();
            using var pano = s.OpenPanorama(new PanoramaRequest { Bins = 4096, Fps = 10, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
            pano.Connection.StartReader();
            var rows = RowsFor(pano, 1200);
            Check(rows.Count >= 2, $"...never a receiver failure: it receives ({rows.Count} rows)");
        }
        using (var rig = new W1700kTxRig("builtin-off", configure: o => Opt(o, "none", W1700kVersionWithVerb())))
        using (var s = rig.Connect("w1700k-builtin"))
        {
            Check(s.WaitReady(10000) == RadioStates.Ready && s.Caps.Tx == null && rig.ServiceCount == 0 && s.Caps.TxAbsent?.Reason == W1700kTxAbsentReasons.Off &&
                  s.Caps.TxAbsent.Setting == W1700kTxSettings.Off,
                  "Off: no service started even with a fake one at hand, and tx_absent says off, started off");
        }
        var broken = FakeTxService.DefaultCaps();
        broken.Remove("max_ms");
        using (var rig = new W1700kTxRig("builtin-failed", caps: broken, configure: o => Opt(o, "", W1700kVersionWithVerb())))
        using (var s = rig.Connect("w1700k-builtin"))
        {
            Check(s.WaitReady(10000) == RadioStates.Ready && s.Caps.Tx == null && rig.ServiceCount == 1 && s.Caps.TxAbsent?.Reason == W1700kTxAbsentReasons.Failed &&
                  s.Caps.TxAbsent.Text.Contains("max_ms"),
                  "a service that was started and could not state one: tx_absent failed, in its words: " + s.Caps.TxAbsent?.Text);
        }
        using (var rig = new W1700kTxRig("builtin-legacy"))
        using (var s = rig.Connect("w1700k-builtin"))
        {
            Check(s.WaitReady(10000) == RadioStates.Ready && s.Caps.Tx != null && s.Caps.TxAbsent == null,
                  "with no launch value resolved (a gate's), the given service stands as before");
        }
    }

    /// <summary>The instrument Device Setup reads: in process, and as the real executable.</summary>
    private static void W1700kBuiltInCheckCases()
    {
        string helper = W1700kGateHelperDir();
        var none = W1700kBackend.CheckTransmitter(new W1700kBackendOptions
        {
            TxServeSetting = "", HelperDirectory = helper, ServeCommand = W1700kDefaults.ServeCommandIn(helper), ReadControllerVersion = _ => W1700kVersion100,
        }, _ => { });
        Check(none.GetBool("transmitter", true) == false && none.GetString("setting") == "builtin" &&
              TxAbsence.Parse(none.GetObject("tx_absent"))?.Reason == W1700kTxAbsentReasons.NoVerb && !none.Has("tx"),
              "D5: the check says no transmitter, why (tx_absent, the wire's own shape) and what it was started with: " + none);
        using (var rig = new W1700kTxRig("check-has"))
        {
            var has = W1700kBackend.CheckTransmitter(new W1700kBackendOptions
            {
                TxServeSetting = "", HelperDirectory = helper, ServeCommand = W1700kDefaults.ServeCommandIn(helper),
                ReadControllerVersion = _ => W1700kVersionWithVerb(), TxServeFactory = () => new FakeTxService(rig).Serve(_ => { }),
            }, _ => { });
            var tx = TxCaps.Parse(has.GetObject("tx"));
            Check(has.GetBool("transmitter") && has.GetString("command") == "\"" + W1700kDefaults.ControllerPathFor(helper) + "\" tx-serve" &&
                  tx != null && tx.TakesRadio && tx.Bands.Length == 2 && has.GetObject("service")?.GetLong("wire") == 1 &&
                  !has.GetObject("service")!.Has("id") && !has.GetObject("service")!.Has("ok") && !has.Has("tx_absent"),
                  "...and with a service: the command, its hello, and caps.tx as the wire carries it");
        }

        // The real executable: one JSON line, exit 0, nothing bound and no controller started.
        string exe = SidecarExe();
        foreach (var (args, reason) in new[]
                 {
                     ("--w1700k --w1700k-tx-check --w1700k-tx-serve none", W1700kTxAbsentReasons.Off),
                     ("--w1700k --w1700k-tx-check --w1700k-serve none", W1700kTxAbsentReasons.NoController),
                     ("--w1700k --w1700k-tx-check --w1700k-serve \"C:\\Py\\python.exe C:\\co\\host\\bin\\w1700k serve\"", W1700kTxAbsentReasons.OwnController),
                 })
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            JsonObject? o = null;
            try { o = Json.ParseObject(output.Trim()); } catch (JsonParseException) { }
            Check(p.HasExited && p.ExitCode == 0 && o != null && o.GetBool("transmitter", true) == false && TxAbsence.Parse(o.GetObject("tx_absent"))?.Reason == reason &&
                  output.Trim().Split('\n').Length == 1,
                  $"ShackLinkUsrp {args}: exit 0, ONE JSON line, {reason}: {output.Trim()}");
        }
    }
}
