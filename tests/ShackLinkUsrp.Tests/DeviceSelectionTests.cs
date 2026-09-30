// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Which B2xx to open when UHD cannot be asked by serial (ShackLink docs/usrp_b210_support.md 9.20). UHD's
// serial filter reads serials as hexadecimal NUMBERS, so the stock install could not open this bench's
// re-serialised clone (KO6ETZB) while listing it. No radio is opened here: the rule, the plan and the
// read-back are pure, and the UHD path is pinned in the source.

using ShackLinkUsrp.Radio;

namespace ShackLinkUsrp.Tests;

internal static partial class Program
{
    /// <summary>What the stock UHD 4.10 find printed on this bench, 2026-09-29 (the sidecar's own log).</summary>
    private const string BenchFind = "type=b200,name=Custom_SDR_B210,serial=KO6ETZB,product=B210";

    private static void UhdSerialRuleCases()
    {
        // UHD's rule, ported (std::stoi(s, 0, 16), MSVC's 32-bit long; false when either throws).
        Check(UhdDeviceSelection.UhdSerialsMatch("31B92DD", "31B92DD"), "a hex serial matches itself (this board's serial before re-serialising)");
        Check(UhdDeviceSelection.UhdSerialsMatch("31b92dd", "31B92DD"), "...as a NUMBER, so letter case does not matter");
        Check(!UhdDeviceSelection.UhdSerialsMatch("KO6ETZB", "KO6ETZB"),
              "KO6ETZB does not match ITSELF: 'K' is no hex digit, stoi throws invalid_argument, UHD answers false - the bench's failure");
        Check(UhdDeviceSelection.UhdSerialsMatch("30AD2D7", "30AD2D7Z"),
              "two different strings with one hex prefix read as the SAME number (stoi stops at the first non-digit)");
        Check(UhdDeviceSelection.UhdSerialsMatch("0x1F", "1F") && UhdDeviceSelection.UhdSerialsMatch(" 1F", "1F"),
              "a 0x prefix and leading whitespace are skipped, as strtol does");
        Check(UhdDeviceSelection.UhdSerialsMatch("7FFFFFFF", "7FFFFFFF") && !UhdDeviceSelection.UhdSerialsMatch("80000000", "80000000"),
              "the int range: 7FFFFFFF matches, 80000000 is out_of_range and never matches");
        Check(!UhdDeviceSelection.UhdSerialsMatch("", "") && !UhdDeviceSelection.UhdSerialsMatch(null!, "1F"),
              "an empty or absent serial never matches");

        var a = UhdDeviceSelection.ParseAddress(BenchFind);
        Check(a["type"] == "b200" && a["name"] == "Custom_SDR_B210" && a["serial"] == "KO6ETZB" && a["product"] == "B210",
              "a find result reads as its keys and values");
    }

    private static void UhdOpenPlanCases()
    {
        // THE BENCH: the only B2xx attached, a serial UHD cannot match.
        var p = UhdDeviceSelection.Plan("KO6ETZB", new[] { BenchFind });
        Check(!p.Refused && p.Args == "type=b200" && p.MustConfirmSerial,
              $"KO6ETZB alone: opened as the only B2xx (args '{p.Args}') and its serial REQUIRED on the open radio");
        Check(p.Note.Contains("not a hexadecimal number") && p.Note.Contains("serial_numbers_match") && p.Note.Contains("only B2xx"),
              "...and the log says why: " + p.Note);

        // A serial UHD can match, alone or beside a board it does not confuse with it: asked by serial.
        p = UhdDeviceSelection.Plan("31B92DD", new[] { "type=b200,name=x,serial=31B92DD,product=B210" });
        Check(!p.Refused && p.Args == "type=b200,serial=31B92DD" && !p.MustConfirmSerial && p.Note == "",
              "a hex serial alone: asked by serial, as before");
        p = UhdDeviceSelection.Plan("31B92DD", new[] { "type=b200,serial=31B92DD", "type=b200,serial=30AD2D7" });
        Check(!p.Refused && p.Args == "type=b200,serial=31B92DD", "a hex serial beside another it does not collide with: asked by serial");

        // Two boards, and UHD cannot tell them apart.
        p = UhdDeviceSelection.Plan("KO6ETZB", new[] { BenchFind, "type=b200,name=x,serial=30AD2D7,product=B210" });
        Check(p.Refused && p.Refusal.Contains("30AD2D7") && p.Refusal.Contains("not a hexadecimal number") &&
              p.Refusal.Contains("Detach the other B2xx") && p.Refusal.Contains("b2xx_fx3_utils"),
              "KO6ETZB beside another B2xx: REFUSED - a 'type=b200' open could take the other one: " + p.Refusal);
        p = UhdDeviceSelection.Plan("30AD2D7", new[] { "type=b200,serial=30AD2D7", "type=b200,serial=30AD2D7Z" });
        Check(p.Refused && p.Refusal.Contains("reads as the same number as '30AD2D7Z'"),
              "a hex serial beside one UHD reads as the same NUMBER: refused, naming it: " + p.Refusal);

        // What was refused before is refused the same way (ShackLink reads these sentences, UsrpKind.PermanentPhrases).
        p = UhdDeviceSelection.Plan("KO6ETZB", Array.Empty<string>());
        Check(p.Refused && p.Refusal.StartsWith("no B2xx is attached (UHD found none)"), "nothing attached: " + p.Refusal);
        p = UhdDeviceSelection.Plan("KO6ETZB", new[] { "type=b200,serial=30AD2D7" });
        Check(p.Refused && p.Refusal.StartsWith("no B2xx with serial KO6ETZB is attached; UHD sees: "), "another board only: " + p.Refusal);
        p = UhdDeviceSelection.Plan("KO6ETZB", new[] { BenchFind, BenchFind });
        Check(p.Refused && p.Refusal.Contains("2 devices answer serial KO6ETZB") && p.Refusal.Contains("hazard 22"),
              "two boards with ONE serial (the clones' EEPROM): refused, and still in the words ShackLink reads as permanent");
        p = UhdDeviceSelection.Plan("ko6etzb", new[] { BenchFind });
        Check(p.Refused && p.Refusal.StartsWith("no B2xx with serial ko6etzb is attached"),
              "the board is chosen by EXACT string, as UHD's B2xx code opens a handle: a different case is not this board");

        // The read-back on the open radio.
        Check(UhdDeviceSelection.OpenedSerialRefusal("KO6ETZB", "KO6ETZB", true) == "", "the open radio states the serial asked for: kept");
        var w = UhdDeviceSelection.OpenedSerialRefusal("KO6ETZB", "30AD2D7", true);
        Check(w.Contains("30AD2D7") && w.Contains("not KO6ETZB") && w.Contains("closed without streaming"),
              "another board opened (plugged in between the find and the open): closed, naming both: " + w);
        Check(UhdDeviceSelection.OpenedSerialRefusal("KO6ETZB", "", true).Contains("states no serial"),
              "a board opened without a serial that states none: refused - nothing says which board it is");
        Check(UhdDeviceSelection.OpenedSerialRefusal("31B92DD", "", false) == "",
              "...while one UHD selected BY serial may state none, as before");
        Check(UhdDeviceSelection.OpenedSerialRefusal("31B92DD", "30AD2D7", false).Length > 0,
              "and a serial that disagrees is refused on either path");
    }

    /// <summary>No gate here opens a real radio, so the UHD path is pinned in the source.</summary>
    private static void UhdOpenSourceRules()
    {
        string ub = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ShackLinkUsrp", "Radio", "UhdBackend.cs"));
        string? open = MemberBody(ub, "protected override void OpenRadio()");
        Check(open != null && !open.Contains("serial={_opt.Serial}") && open.Contains("UhdUsrp.Find(\"type=b200\")"),
              "OpenRadio never hands UHD a serial filter to find with: it finds every B2xx and chooses itself");
        int iPlan = open?.IndexOf("UhdDeviceSelection.Plan(_opt.Serial, found)", StringComparison.Ordinal) ?? -1;
        int iRefused = open?.IndexOf("if (plan.Refused) throw", StringComparison.Ordinal) ?? -1;
        int iOpen = open?.IndexOf("UhdUsrp.Open(args)", StringComparison.Ordinal) ?? -1;
        int iArgs = open?.IndexOf("var args = plan.Args;", StringComparison.Ordinal) ?? -1;
        Check(iPlan > 0 && iRefused > iPlan && iArgs > iRefused && iOpen > iArgs,
              "...plans, throws a refusal, and opens with the PLAN's args, in that order");
        int iCheck = open?.IndexOf("UhdDeviceSelection.OpenedSerialRefusal(_opt.Serial, info.MboardSerial, plan.MustConfirmSerial)", StringComparison.Ordinal) ?? -1;
        int iInfo = open?.IndexOf("Info = InfoFor(", StringComparison.Ordinal) ?? -1;
        int iKeep = open?.IndexOf("_usrp = usrp", StringComparison.Ordinal) ?? -1;
        string between = iCheck > 0 && iInfo > iCheck ? open!.Substring(iCheck, iInfo - iCheck) : "";
        Check(iCheck > iOpen && iInfo > iCheck && iKeep > iCheck && between.Contains("if (wrongBoard.Length > 0)") &&
              between.Contains("usrp.Dispose()") && between.Contains("throw new InvalidOperationException(wrongBoard)"),
              "...and checks the open radio's serial, disposing it and THROWING on a refusal, BEFORE it is described or kept");
    }
}
