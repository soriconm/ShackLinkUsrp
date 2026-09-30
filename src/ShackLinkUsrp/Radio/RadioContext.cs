// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using ShackLinkUsrp.Dsp;

namespace ShackLinkUsrp.Radio;

/// <summary>What a pipeline may read about the radio it hangs off - ONE receive side of it (ShackLink
/// docs/usrp_b210_support.md section 9.23): that side's ring, the centre its samples sit on and the gain
/// its rows state. Pipelines never call the radio: they read the ring and these facts, and are TOLD about
/// retunes and rate changes.</summary>
internal interface IRadioContext
{
    /// <summary>"A" or "B" (<see cref="ShackLinkUsrp.Wire.RxSides"/>).</summary>
    string Side { get; }
    WideRing Ring { get; }
    /// <summary>The centre this side's wide stream sits on - never the RF LO, which the radio places.</summary>
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

    /// <summary>The radio was reconfigured under this side with its centre and rate unchanged (section 9.23.2
    /// item 3): the other side was switched on or off - the streamer made again, the clock perhaps moved - or
    /// the RF LO was placed elsewhere under an unmoved centre. Samples at and after <paramref name="position"/>
    /// come after it, and time between the samples before and after it may be missing.</summary>
    void OnRadioReconfigured(long position);
}

/// <summary>
/// One receive side of the radio as its pipelines see it (section 9.23): its own ring - written in lockstep
/// with the other side's, so radio position n is the same instant on both - its own listeners and settle
/// boundary, so a gain change on RX B restarts none of RX A's rows, and the side's centre and gain read from
/// the radio. Built and owned by <see cref="RadioBackendBase"/>.
/// </summary>
internal sealed class SideContext : IRadioContext
{
    private readonly RadioBackendBase _radio;
    internal readonly object ListenersLock = new();
    internal readonly List<IRadioListener> Listeners = new();
    /// <summary>A client's sweep accumulator on this side, by client id.</summary>
    internal readonly Dictionary<int, ShackLinkUsrp.Pipelines.SweepWorker> Sweeps = new();
    internal long SettledFromValue;

    public SideContext(RadioBackendBase radio, string side, int ringSamples)
    {
        _radio = radio;
        Side = side;
        Ring = new WideRing(ringSamples);
    }

    public string Side { get; }
    public WideRing Ring { get; }
    public long LoHz => _radio.CenterOf(Side);
    public double WideRateHz => _radio.WideRateHz;
    public long Overflows => _radio.Overflows;
    public float RowGainDb => _radio.RowGainOf(Side);
    public int MaxFftSize => _radio.MaxFftSize;
    public long SettledFrom => Volatile.Read(ref SettledFromValue);
    public void Log(string line) => _radio.Log(line);

    internal List<IRadioListener> Snapshot()
    {
        lock (ListenersLock) return Listeners.ToList();
    }
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
