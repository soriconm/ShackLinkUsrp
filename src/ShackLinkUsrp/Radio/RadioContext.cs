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
    /// <summary>The first ring position taken at the radio's settings as they are NOW: the latest
    /// boundary any change announced. A pipeline created later starts no earlier - the samples before it
    /// were still in flight at the old settings when it was created (CRTU200's A2 F1).</summary>
    long SettledFrom { get; }
    void Log(string line);
}

/// <summary>A pipeline that must follow the radio when it changes. EVERY change the owner makes is
/// announced, each with the ring position from which its samples were taken at the new settings.</summary>
internal interface IRadioListener
{
    /// <summary>Samples at and after <paramref name="position"/> were taken at <paramref name="loHz"/>.</summary>
    void OnRadioRetuned(long loHz, long position);

    /// <summary>Samples at and after <paramref name="position"/> run at <paramref name="wideRateHz"/>.</summary>
    void OnRateChanged(double wideRateHz, long position);

    /// <summary>Samples at and after <paramref name="position"/> were taken after a change to the receive
    /// chain that leaves the LO and the rate alone: the gain, AGC, the antenna, the analog bandwidth or
    /// a named radio setting. A spectrum that integrates across one mixes two radios (A2 F3).</summary>
    void OnFrontEndChanged(long position);
}

/// <summary>The sidecar's one monotonic clock for time_ticks (100 ns units).</summary>
internal static class SidecarClock
{
    private static readonly long Origin = Stopwatch.GetTimestamp();

    public static long Ticks => FromTimestamp(Stopwatch.GetTimestamp());

    /// <summary>A <see cref="Stopwatch.GetTimestamp"/> value on this clock - how the synthetic radio stamps the
    /// instant its own clock gives a ring position.</summary>
    public static long FromTimestamp(long stopwatchTimestamp) =>
        (long)((stopwatchTimestamp - Origin) * (10_000_000.0 / Stopwatch.Frequency));
}
