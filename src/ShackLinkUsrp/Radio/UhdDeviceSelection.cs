// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace ShackLinkUsrp.Radio;

/// <summary>
/// WHICH B2xx TO OPEN, WHEN UHD CANNOT BE ASKED BY SERIAL (ShackLink docs/usrp_b210_support.md 9.20).
///
/// <para>UHD filters every find and every make by <c>uhd::utils::serial_numbers_match</c>
/// (<c>host/lib/utils/serial_number.cpp</c>, read in the 4.10 tree): both serials go through
/// <c>std::stoi(s, 0, 16)</c> and are compared as NUMBERS. A serial that is not hexadecimal - this bench's
/// re-serialised clone, <c>KO6ETZB</c> - throws inside that call, the match answers false, and no
/// <c>serial=</c> hint can ever select the board, not even against itself: the stock install said
/// "no B2xx with serial KO6ETZB is attached" and in the same breath listed it. The number rule also
/// joins serials that are different strings (<c>30AD2D7</c> and <c>30AD2D7Z</c> both read 0x30AD2D7).
/// UHD's B2xx code itself opens a handle by exact STRING equality once it has been handed one discovered
/// address, so what cannot be done by serial can be done by being the only B2xx a <c>type=b200</c> hint
/// finds - and then the serial is checked on the open radio.</para>
/// </summary>
internal static class UhdDeviceSelection
{
    /// <summary>
    /// UHD's own rule, ported: <c>std::stoi(a, 0, 16) == std::stoi(b, 0, 16)</c>, false when either
    /// throws. MSVC's <c>stoi</c> is <c>strtol</c> into a 32-bit <c>long</c>: leading whitespace, an
    /// optional sign, an optional <c>0x</c>, then as many hex digits as there are (the rest ignored);
    /// no digit at all is <c>invalid_argument</c>, a value outside the int range <c>out_of_range</c>.
    /// </summary>
    public static bool UhdSerialsMatch(string a, string b)
    {
        if (!TryStoiHex(a, out int x) || !TryStoiHex(b, out int y)) return false;
        return unchecked((uint)x) == unchecked((uint)y);
    }

    private static bool TryStoiHex(string s, out int value)
    {
        value = 0;
        if (s == null) return false;
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || (s[i] >= '\t' && s[i] <= '\r'))) i++;
        bool negative = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) { negative = s[i] == '-'; i++; }
        if (i + 2 < s.Length && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X') && Hex(s[i + 2]) >= 0) i += 2;
        long magnitude = 0;
        int digits = 0;
        bool overflow = false;
        while (i < s.Length && Hex(s[i]) >= 0)
        {
            magnitude = magnitude * 16 + Hex(s[i]);
            if (magnitude > 0x80000000L) overflow = true;   // keep reading, as strtol does; cap the number
            if (overflow) magnitude = 0x80000001L;
            digits++;
            i++;
        }
        if (digits == 0) return false;                        // invalid_argument
        long signed = negative ? -magnitude : magnitude;
        if (signed > int.MaxValue || signed < int.MinValue) return false;   // out_of_range
        value = (int)signed;
        return true;
    }

    private static int Hex(char c) =>
        c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;

    /// <summary>One address UHD's find printed: <c>type=b200,name=...,serial=...,product=...</c>.</summary>
    public static Dictionary<string, string> ParseAddress(string address)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in (address ?? "").Split(','))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) d[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
        }
        return d;
    }

    /// <summary>How to open a board: the device args, whether its serial must be checked once it is open,
    /// a note for the log, or a refusal in words (then nothing is opened).</summary>
    public sealed record OpenPlan(string Args, bool MustConfirmSerial, string Note, string Refusal)
    {
        public bool Refused => Refusal.Length > 0;
    }

    /// <summary>
    /// Plan the open of <paramref name="serial"/> from what an UNFILTERED <c>type=b200</c> find printed
    /// (a find cannot be filtered by a serial UHD cannot match). The board is chosen by EXACT string here;
    /// UHD is asked by serial only when its number rule picks this board and no other; otherwise, when it
    /// is the only B2xx attached, by <c>type=b200</c> alone with the serial checked after the open; with
    /// another B2xx attached that UHD could open instead, refused in words.
    /// </summary>
    public static OpenPlan Plan(string serial, IReadOnlyList<string> found)
    {
        serial ??= "";
        var all = (found ?? Array.Empty<string>()).Select(ParseAddress).ToList();
        var serials = all.Select(d => d.TryGetValue("serial", out var s) ? s : "").ToList();
        if (all.Count == 0)
            return Refuse($"no B2xx is attached (UHD found none); is serial {serial} plugged into a USB 3 port?");
        int mine = serials.Count(s => string.Equals(s, serial, StringComparison.Ordinal));
        if (mine == 0)
            return Refuse($"no B2xx with serial {serial} is attached; UHD sees: {string.Join(" | ", found!)}");
        if (mine > 1)
            return Refuse($"{mine} devices answer serial {serial}; clones ship one EEPROM serial - re-serialise one with b2xx_fx3_utils (hazard 22)");

        var others = serials.Where(s => !string.Equals(s, serial, StringComparison.Ordinal)).ToList();
        bool uhdReadsIt = UhdSerialsMatch(serial, serial);
        string? collider = uhdReadsIt ? others.FirstOrDefault(o => UhdSerialsMatch(serial, o)) : null;
        if (uhdReadsIt && collider == null)
            return new OpenPlan($"type=b200,serial={serial}", false, "", "");

        string why = uhdReadsIt
            ? $"'{serial}' reads as the same number as '{collider}'"
            : $"'{serial}' is not a hexadecimal number";
        string rule = "UHD compares serials as hexadecimal numbers (uhd::utils::serial_numbers_match), and " + why;
        if (others.Count == 0)
            return new OpenPlan("type=b200", true,
                $"UHD cannot select serial {serial} by its serial: {rule}. It is the only B2xx attached, so it is opened " +
                "as the only one and its serial is checked once it is open.", "");
        return Refuse($"UHD cannot select serial {serial} while another B2xx is attached ({string.Join(", ", others)}): {rule}. " +
                      "Detach the other B2xx, or give this one a hexadecimal serial with b2xx_fx3_utils.");
    }

    /// <summary>
    /// Is the board UHD opened the one that was asked for? "" when it is. The serial is the open radio's own
    /// EEPROM word (<c>uhd_usrp_get_rx_info</c>'s <c>mboard_serial</c>). Must a check be made
    /// (<see cref="OpenPlan.MustConfirmSerial"/>), a board that states no serial is refused: nothing then
    /// says which board this is.
    /// </summary>
    public static string OpenedSerialRefusal(string asked, string? readBack, bool mustConfirm)
    {
        if (string.IsNullOrEmpty(readBack))
            return mustConfirm
                ? $"the B2xx UHD opened states no serial, so nothing says it is {asked} - closed without streaming"
                : "";
        return string.Equals(readBack, asked, StringComparison.Ordinal)
            ? ""
            : $"UHD opened the B2xx with serial {readBack}, not {asked} - closed without streaming";
    }

    private static OpenPlan Refuse(string words) => new OpenPlan("", false, "", words);
}
