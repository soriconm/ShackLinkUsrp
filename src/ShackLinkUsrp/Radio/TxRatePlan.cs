// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Radio;

/// <summary>
/// Which TX rates the transmitter admits, and how far its DUC reaches - pure rules, shared by both
/// radios (ShackLink docs/usrp_b210_support.md section 9.17 items 4 and 5). The UHD backend feeds them
/// UHD's own answers (the DUC's host rates and the master clock it READ BACK); the synthetic radio feeds
/// them the same answers computed by the ports below, so a client sees one set of rules either way.
/// </summary>
internal static class TxRatePlan
{
    /// <summary>The lowest TX rate offered. 200 kHz divides every wide rate this sidecar offers on Windows
    /// (2, 5, 10, 20 MS/s) and is the DUC's exact tick / n at every clock measured (section 9.17 item 5);
    /// nothing a client of this sidecar generates needs less.</summary>
    public const double MinTxRateHz = 200_000;

    /// <summary>The highest TX rate offered: nothing here needs more, and a TX stream beside a 10 MS/s
    /// receiver on Windows USB has not been measured.</summary>
    public const double MaxTxRateHz = 2_000_000;

    /// <summary>AD9361_MIN_CLOCK_RATE / AD9361_MAX_CLOCK_RATE (ad9361_device.cpp).</summary>
    public const double MinTickHz = 220e3;
    public const double MaxTickHz = 61.44e6;

    /// <summary>The DUC's host rates at <paramref name="tickHz"/>: tick / n for n in 1…128, even n in
    /// 130…256 and multiples of 4 in 260…512 - a port of tx_dsp_core_3000::get_host_rates for the
    /// synthetic radio. The UHD backend asks UHD (uhd_usrp_get_tx_rates) instead.</summary>
    public static double[] DucHostRates(double tickHz)
    {
        var rates = new List<double>();
        for (int n = 512; n > 256; n -= 4) rates.Add(tickHz / n);
        for (int n = 256; n > 128; n -= 2) rates.Add(tickHz / n);
        for (int n = 128; n >= 1; n--) rates.Add(tickHz / n);
        return rates.ToArray();
    }

    /// <summary>The master clock UHD's automatic selection picks for a radio whose every rate divides
    /// <paramref name="wideRateHz"/>, one channel each way: the wide rate times the largest power of two
    /// that stays under the AD9361's ceiling, except that a doubling is skipped (ad936x_manager.cpp
    /// get_auto_tick_rate). The synthetic radio's clock; the UHD backend reads the real one back.</summary>
    public static double ModelTickHz(double wideRateHz)
    {
        if (!(wideRateHz > 0) || wideRateHz > MaxTickHz) return 0;
        int multiplier = 1 << (int)Math.Floor(Math.Log2(MaxTickHz / wideRateHz));
        if (multiplier == 2 && wideRateHz >= MinTickHz) multiplier = 1;
        return wideRateHz * multiplier;
    }

    /// <summary>
    /// The TX rates admitted while the wide rate is <paramref name="wideRateHz"/> and the master clock
    /// <paramref name="tickHz"/>, ascending. A rate is admitted when it is one of the DUC's
    /// <paramref name="ducRates"/>, a whole number of hertz, divides the wide rate (so it leaves the LCM,
    /// and with it UHD's automatic clock, where it was - and UHD's LCM rounds each rate to an integer
    /// first, so a fractional one would not), is tick / n exactly with n = 1 or n even (an odd n runs
    /// the CIC alone and UHD warns of its roll-off), and lies within <see cref="MinTxRateHz"/> …
    /// <see cref="MaxTxRateHz"/>.
    /// </summary>
    public static double[] Admitted(IEnumerable<double> ducRates, double tickHz, double wideRateHz)
    {
        if (!(tickHz > 0) || !(wideRateHz > 0)) return Array.Empty<double>();
        var admitted = new SortedSet<double>();
        foreach (double r in ducRates)
        {
            if (!(r >= MinTxRateHz - 0.5 && r <= MaxTxRateHz + 0.5)) continue;
            double hz = Math.Round(r);
            if (Math.Abs(r - hz) > 1e-6) continue;
            if (!TxRules.DividesWideRate(hz, wideRateHz)) continue;
            double n = tickHz / hz;
            long ni = (long)Math.Round(n);
            if (Math.Abs(n - ni) > 1e-9 * n) continue;
            if (ni != 1 && ni % 2 != 0) continue;
            admitted.Add(hz);
        }
        return admitted.ToArray();
    }

    /// <summary>How far the TX centre may sit from the LO with a transmission's whole band - +-
    /// TxUsableFraction/2 of its rate - inside the part of the AD9361's span that UHD calibrates its TX
    /// filters for (+-TxUsableFraction/2 of the master clock: ad9361_device.cpp set_bw_filter(TX,
    /// _baseband_bw) at every clock change). A park inside it moves the DUC's CORDIC; one beyond it
    /// moves the synthesizer.</summary>
    public static double DspReachHz(double tickHz, double txRateHz) =>
        Math.Max(0, ProtocolConstants.TxUsableFraction / 2.0 * (tickHz - txRateHz));

    /// <summary><c>caps.tx.max_lo_offset_hz</c>: the reach for the widest admitted rate.</summary>
    public static long MaxLoOffsetHz(double tickHz, double[] admitted) =>
        admitted.Length == 0 ? 0 : (long)Math.Floor(DspReachHz(tickHz, admitted.Max()));

    /// <summary>The queue a transmission's writer is paced against: ~50 ms at its rate, and never less
    /// than one of the largest blocks the wire carries.</summary>
    public static int QueueSamplesFor(double txRateHz) =>
        Math.Max(ProtocolConstants.MaxIqBlockSamples, (int)Math.Ceiling(txRateHz * 0.05));
}
