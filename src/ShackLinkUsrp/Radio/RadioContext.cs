// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using ShackLinkUsrp.Dsp;

namespace ShackLinkUsrp.Radio;

/// <summary>What a pipeline may read about the radio it hangs off. Pipelines never call the
/// radio: they read the ring and these facts, and are TOLD about retunes and rate changes.</summary>
internal interface IRadioContext
{
    WideRing Ring { get; }
    long LoHz { get; }
    double WideRateHz { get; }
    /// <summary>Radio overflows since the radio opened (monotonic): headers report deltas.</summary>
    long Overflows { get; }
    /// <summary>The gain a row states: NaN while AGC decides (a level nobody set is not a number).</summary>
    float RowGainDb { get; }
    int MaxFftSize { get; }
    void Log(string line);
}

/// <summary>A pipeline that must follow the radio when it is retuned or re-rated.</summary>
internal interface IRadioListener
{
    /// <summary>Samples at and after <paramref name="position"/> were taken at <paramref name="loHz"/>.</summary>
    void OnRadioRetuned(long loHz, long position);

    /// <summary>Samples at and after <paramref name="position"/> run at <paramref name="wideRateHz"/>.</summary>
    void OnRateChanged(double wideRateHz, long position);
}

/// <summary>The sidecar's one monotonic clock for time_ticks (100 ns units).</summary>
internal static class SidecarClock
{
    private static readonly Stopwatch Sw = Stopwatch.StartNew();

    public static long Ticks => (long)(Sw.ElapsedTicks * (10_000_000.0 / Stopwatch.Frequency));
}
