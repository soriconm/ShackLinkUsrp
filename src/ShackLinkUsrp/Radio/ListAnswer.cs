// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using ShackLinkUsrp.Client;

namespace ShackLinkUsrp.Radio;

/// <summary>
/// What <c>--list</c> answers, from what UHD's find printed (ShackLink docs/usrp_b210_support.md 9.26 D2).
/// Pure, so the gate drives it with the bench's own find strings; the answer's spelling belongs to the
/// MIT library (<see cref="SidecarDiscovery.Compose"/>), which is what every consumer reads it with.
/// </summary>
internal static class ListAnswer
{
    /// <param name="found">UHD's find result, one <c>key=value,...</c> address per board.</param>
    /// <param name="servedBy">The pid of the sidecar serving a serial (its rendezvous file), 0 for none.</param>
    public static DiscoveryResult From(IReadOnlyList<string> found, Func<string, int> servedBy, string uhdVersion)
    {
        var result = new DiscoveryResult { Ran = true, UhdVersion = uhdVersion ?? "" };
        foreach (var dev in found ?? Array.Empty<string>())
        {
            var kv = UhdDeviceSelection.ParseAddress(dev);
            kv.TryGetValue("serial", out var serial);
            kv.TryGetValue("product", out var product);
            kv.TryGetValue("name", out var name);
            serial ??= "";
            int pid = 0;
            if (serial.Length > 0)
            {
                try { pid = servedBy?.Invoke(serial) ?? 0; } catch { pid = 0; }
            }
            result.Radios.Add(new DiscoveredRadio { Serial = serial, Product = product ?? "", Name = name ?? "", ServedByPid = pid });
        }
        return result;
    }

    /// <summary>The human form <c>--list</c> prints without <c>--json</c>.</summary>
    public static IEnumerable<string> HumanLines(DiscoveryResult result)
    {
        if (result.Error.Length > 0)
        {
            yield return "error: " + result.Error;
            yield break;
        }
        foreach (var r in result.Radios)
        {
            string busy = r.ServedByPid > 0 ? $"served by pid {r.ServedByPid}" : "free";
            yield return $"usrp:{r.Serial}  product={r.Product}  name={r.Name}  {busy}";
        }
        foreach (var dup in result.Radios.Where(r => r.Serial.Length > 0).GroupBy(r => r.Serial).Where(g => g.Count() > 1))
            yield return $"warning: {dup.Count()} devices answer serial {dup.Key}; clones ship one EEPROM serial - " +
                         UhdDeviceSelection.ReserialiseRemedy("one of them");
        if (result.Radios.Count == 0) yield return "no B2xx found";
    }
}
