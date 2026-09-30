// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// ShackLink docs/usrp_b210_support.md 9.27: the radio is offered at its NATIVE rates on every machine (the
// owner's rule: never filter an option by what one machine measured), and the analog bandwidth survives a
// rate change - UHD re-programs the AD9361's filter from the clock at every master-clock change.

using ShackLinkUsrp.Radio;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Tests;

internal static partial class Program
{
    /// <summary>The UHD backend's offer is the library's native list, with no condition on the OS or the USB
    /// format, and its two-side rates the ones the clock rule admits - pinned in the source, as no gate opens
    /// a radio.</summary>
    private static void NativeRateSourceRules()
    {
        string ub = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ShackLinkUsrp", "Radio", "UhdBackend.cs"));
        string? offered = MemberBody(ub, "private static double[] OfferedRates()");
        Check(offered != null && offered.Contains("B2xxRates.Native") && !offered.Contains("OperatingSystem") && !offered.Contains("otw"),
              "the offered rates are the native list - no OS, no USB format, no measurement decides them");
        Check(ub.Contains("DualRatesHz = twoSides ? B2xxRates.TwoSide(OfferedRates()) : null") && !ub.Contains("_opt.DualRatesHz"),
              "two sides are offered every native rate the clock rule admits, with no bench list in the way");
        Check(!ub.Contains("OfferedRates(_opt.OtwFormat)") && !ub.Contains("OfferedRates(opt.OtwFormat)"),
              "...and nothing asks the offer about the USB format");

        // The clock is the sidecar's, one side or two, at open and at every rate change: UHD's automatic clock
        // took the LCM of the second RX channel's OLD rate and stayed at 50 MHz (--rate-sweep: 8 of 18 coerced).
        string? clock = MemberBody(ub, "private void SetClockFor(UhdUsrp u, double clockHz, double rateHz, bool twoSides)");
        Check(clock != null && !clock.Contains("!_explicitClock && !twoSides") && clock.Contains("u.SetMasterClockRate(clockHz)"),
              "SetClockFor sets the clock for ONE side too - no early return that hands it back to UHD's LCM");
        string? rate = MemberBody(ub, "protected override double ApplyRate(double hz)");
        Check(rate != null && rate.Contains("SetClockFor(u, ClockFor(_streamSides.Length, hz), hz, _streamSides.Length > 1)") &&
              !rate.Contains("eff = u.SetRxRate(hz)"),
              "every rate change sets the clock itself, never leaving it to UHD's automatic choice");
        string? open = MemberBody(ub, "protected override void OpenRadio()");
        Check(open != null && open.Contains("SetClockFor(usrp, ClockFor(1, _opt.WideRateHz), _opt.WideRateHz, twoSides: false)"),
              "...and so does the open");

        // Every native rate keeps a transmitter: an admitted TX rate at the clock that rate gets.
        var none = B2xxRates.Native.Where(w => TxRatePlan.Admitted(TxRatePlan.DucHostRates(TxRatePlan.ModelTickHz(w)), TxRatePlan.ModelTickHz(w), w).Length == 0).ToList();
        Check(none.Count == 0, "every native wide rate admits a TX rate at its clock" + (none.Count == 0 ? "" : ": NONE at " + string.Join(", ", none)));
    }

    private static void BandwidthRuleCases()
    {
        var caps = new RadioCaps { BandwidthMinHz = 200e3, BandwidthMaxHz = 56e6 };
        Check(RadioBackendBase.BandwidthForRate(10e6, caps) == 10e6, "the bandwidth that follows 10 MS/s is 10 MHz");
        Check(RadioBackendBase.BandwidthForRate(61.44e6, caps) == 56e6, "...and 61.44 MS/s is held to the widest filter, 56 MHz");
        Check(RadioBackendBase.BandwidthForRate(100e3, caps) == 200e3, "...and a rate under the narrowest filter gets the narrowest");
        Check(RadioBackendBase.BandwidthForRate(5e6, null) == 5e6, "no caps: the rate itself");

        var (backend, server, log) = StartSim("bw", 10e6);
        try
        {
            using var s = Connect(server, "bw-test");
            Check(s.WaitReady(5000) == RadioStates.Ready, "the synthetic radio opens");
            s.ClaimLo();
            var st = s.SetRate(2e6);
            Check(Math.Abs(st.BandwidthHz - 2e6) < 1, $"with none set, a rate change takes the bandwidth with it ({st.BandwidthHz:0} Hz at 2 MS/s)");
            st = s.SetBandwidth(1.5e6);
            Check(Math.Abs(st.BandwidthHz - 1.5e6) < 1, $"a set bandwidth is applied ({st.BandwidthHz:0} Hz)");
            st = s.SetRate(10e6);
            Check(Math.Abs(st.BandwidthHz - 1.5e6) < 1 && Math.Abs(st.WideRateHz - 10e6) < 1,
                  $"...and SURVIVES a rate change, as set - UHD would have re-programmed it from the new clock ({st.BandwidthHz:0} Hz at {st.WideRateHz:0} S/s)");
            st = s.GetState();
            Check(Math.Abs(st.BandwidthHz - 1.5e6) < 1, "the state keeps saying what is in force");
        }
        finally
        {
            server.Dispose();
            backend.Dispose();
            log.Dispose();
        }
    }
}
