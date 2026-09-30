// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// A board ShackLink has never seen (ShackLink docs/usrp_b210_support.md 9.26): --list --json is how a
// catalogue gets a serial for a board fresh from power-up, and ONE lock keeps that find from meeting another
// sidecar's open. No radio is opened here: the answer is built from the bench's own find strings, the real
// executable is driven down its no-UHD path, and the lock is taken for real.

using ShackLinkUsrp.Client;
using ShackLinkUsrp.Radio;

namespace ShackLinkUsrp.Tests;

internal static partial class Program
{
    private static void ListAnswerCases()
    {
        var found = new[]
        {
            "type=b200,name=Custom_SDR_B210,serial=B210D8F,product=B210",
            "type=b200,name=Custom_SDR_B210,serial=31B92DD,product=B210",
        };
        var r = ListAnswer.From(found, s => s == "31B92DD" ? 4242 : 0, "4.10.0.0-release");
        Check(r.Ran && r.Error == "" && r.UhdVersion == "4.10.0.0-release" && r.Radios.Count == 2 &&
              r.Radios[0].Serial == "B210D8F" && r.Radios[0].Product == "B210" && r.Radios[0].Name == "Custom_SDR_B210" &&
              r.Radios[0].ServedByPid == 0 && r.Radios[1].ServedByPid == 4242,
              "the answer carries each board UHD found, and the sidecar serving it");
        var back = SidecarDiscovery.Parse(SidecarDiscovery.Compose(r));
        Check(back.Ran && back.Radios.Count == 2 && back.Radios[1].ServedByPid == 4242,
              "...in the library's spelling, which is what every consumer reads it with");
        var thrower = ListAnswer.From(found, _ => throw new IOException("rendezvous unreadable"), "x");
        Check(thrower.Radios.All(x => x.ServedByPid == 0), "an unreadable rendezvous file is 'not served', never a failed list");

        var human = string.Join("\n", ListAnswer.HumanLines(r));
        Check(human.Contains("usrp:B210D8F  product=B210  name=Custom_SDR_B210  free") && human.Contains("served by pid 4242"),
              "the human form lists each board: " + human);
        var dup = string.Join("\n", ListAnswer.HumanLines(ListAnswer.From(new[] { found[1], found[1] }, _ => 0, "x")));
        Check(dup.Contains("2 devices answer serial 31B92DD") && dup.Contains("usrp_burn_mb_eeprom") && !dup.Contains("b2xx_fx3_utils"),
              "two boards with one serial are warned about, naming the tool that CAN re-serialise: " + dup);
        Check(string.Join("\n", ListAnswer.HumanLines(ListAnswer.From(Array.Empty<string>(), _ => 0, "x"))) == "no B2xx found",
              "nothing found says so");
        Check(string.Join("\n", ListAnswer.HumanLines(new DiscoveryResult { Error = "UHD could not be loaded: x" })) == "error: UHD could not be loaded: x",
              "an error is the one line");
    }

    /// <summary>The real executable, down the one path that needs no UHD: a --uhd folder with no uhd.dll.
    /// The answer file is written anyway, with the reason, and the exit is 1.</summary>
    private static void ListJsonSpawnCases()
    {
        string exe = SidecarExe();
        string emptyUhd = Path.Combine(Path.GetTempPath(), "slu-no-uhd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyUhd);
        try
        {
            var result = SidecarDiscovery.Run(exe, emptyUhd, 30000);
            Check(!result.Ran && result.Error.StartsWith("UHD could not be loaded: ", StringComparison.Ordinal) && result.Error.Contains("uhd.dll"),
                  "a list whose UHD cannot load writes its answer anyway, saying why: " + result.Error);
            bool finished = SidecarSpawner.RunToExit(exe, new[] { "--json", Path.Combine(emptyUhd, "x.json") }, 30000, out int code);
            Check(finished && code == 2 && !File.Exists(Path.Combine(emptyUhd, "x.json")),
                  $"--json without --list is refused (exit {code}) and writes nothing");
            string leftovers = string.Join(", ", Directory.GetFiles(Path.GetTempPath(), "ShackLinkUsrp-list-*.json*"));
            Check(leftovers.Length == 0, "the runner leaves no answer file (or half-written one) behind: " + leftovers);
        }
        finally
        {
            try { Directory.Delete(emptyUhd, true); } catch { }
        }
    }

    /// <summary>The lock is a real named mutex: a second holder waits, a wait that runs out goes ahead and
    /// SAYS so, and a holder that died without releasing does not wedge every later open.</summary>
    private static void DeviceOpsLockCases()
    {
        Check(UhdDeviceOpsLock.MutexName == @"Local\ShackLinkUsrp.UhdDeviceOps" && UhdDeviceOpsLock.DefaultWait >= TimeSpan.FromSeconds(30),
              "one machine-session-wide name, and a wait longer than a cold board's open");
        var held = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using (UhdDeviceOpsLock.Acquire(TimeSpan.FromSeconds(5), null))
            {
                held.Set();
                release.Wait(10000);
            }
        }) { IsBackground = true };
        holder.Start();
        Check(held.Wait(5000), "the first holder takes it");
        var said = new List<string>();
        var t = System.Diagnostics.Stopwatch.StartNew();
        using (UhdDeviceOpsLock.Acquire(TimeSpan.FromMilliseconds(300), said.Add)) { }
        Check(t.ElapsedMilliseconds >= 250 && said.Count == 1 && said[0].Contains("going ahead without it"),
              $"a second holder waits its time and then goes ahead, saying so ({t.ElapsedMilliseconds} ms): {string.Join(" | ", said)}");
        release.Set();
        holder.Join(5000);
        said.Clear();
        t.Restart();
        using (UhdDeviceOpsLock.Acquire(TimeSpan.FromSeconds(5), said.Add)) { }
        Check(said.Count == 0 && t.ElapsedMilliseconds < 1000, "once released, the next holder takes it at once and says nothing");

        var died = new Thread(() =>
        {
            var m = new Mutex(false, UhdDeviceOpsLock.MutexName);
            m.WaitOne();   // taken and never released: the thread ends holding it
        }) { IsBackground = true };
        died.Start();
        died.Join(5000);
        said.Clear();
        using (UhdDeviceOpsLock.Acquire(TimeSpan.FromSeconds(5), said.Add)) { }
        Check(said.Count == 1 && said[0].Contains("ended while holding it"), "an abandoned lock is taken, and said: " + string.Join(" | ", said));
    }

    /// <summary>Both finders hold the lock around UHD, and there is no third (UhdProbe is a separate bench tool).</summary>
    private static void DeviceOpsLockSourceRules()
    {
        string root = RepoRoot();
        string ub = File.ReadAllText(Path.Combine(root, "src", "ShackLinkUsrp", "Radio", "UhdBackend.cs"));
        string? open = MemberBody(ub, "protected override void OpenRadio()");
        int lockAt = open?.IndexOf("using (UhdDeviceOpsLock.Acquire(", StringComparison.Ordinal) ?? -1;
        string block = lockAt >= 0 ? MemberBody(open!.Substring(lockAt), "using (UhdDeviceOpsLock.Acquire(") ?? "" : "";
        Check(block.Contains("UhdUsrp.Find(\"type=b200\")") && block.Contains("UhdUsrp.Open(args)"),
              "OpenRadio holds the device lock around BOTH the find and the make");
        string prog = File.ReadAllText(Path.Combine(root, "src", "ShackLinkUsrp", "Program.cs"));
        string? list = MemberBody(prog, "private static int RunList(SidecarOptions opt)");
        int l = list?.IndexOf("using (UhdDeviceOpsLock.Acquire(", StringComparison.Ordinal) ?? -1;
        int f = list?.IndexOf("UhdUsrp.Find(\"type=b200\")", StringComparison.Ordinal) ?? -1;
        Check(l >= 0 && f > l && list!.Substring(l, f - l).IndexOf(';') < 0,
              "RunList's find is the statement the device lock guards");
        var others = Directory.GetFiles(Path.Combine(root, "src", "ShackLinkUsrp"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                        !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(p => !p.EndsWith("UhdBackend.cs") && !p.EndsWith("Program.cs") && !p.EndsWith("UhdUsrp.cs"))
            .Where(p => File.ReadAllText(p).Contains("UhdUsrp.Find(") || File.ReadAllText(p).Contains("UhdUsrp.Open("))
            .ToList();
        Check(others.Count == 0, "no other code in the sidecar finds or makes a radio outside the lock: " + string.Join(", ", others));
    }
}
