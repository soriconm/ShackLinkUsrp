// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Radio;

/// <summary>
/// How a <c>step</c> source runs (ShackLink docs/usrp_b210_support.md section 9.17 P4-a', 9.18 P4b2) -
/// pure rules, decided once at <c>tx_start</c> and shared by both radios. There are two regimes, and which
/// one a sweep gets decides how exact it is:
/// <list type="bullet">
/// <item><b>Baseband</b>, when every point lies inside the usable band (TxUsableFraction) of some admitted
/// TX rate around the sweep's centre - at most 1.6 MHz wide at 2 MS/s. The LO parks at the centre and the
/// steps are GENERATED: the carrier moves at an exact sample count, on both radios, phase-continuous.
/// The rate is the lowest admitted one that holds the sweep. Every step says <c>timing</c> =
/// <see cref="TimingSample"/>.</item>
/// <item><b>Tuned</b>, wider: the carrier is baseband 0 Hz and every step is a frequency write - the DUC's
/// CORDIC inside its reach of a parked LO (<c>dsp</c>), the synthesizer beyond it (<c>rf</c>), at the lowest
/// admitted rate. The sweep is cut into as few equal windows as the reach allows and the LO parks at the
/// centre of each. UHD applies a write when it is written, not at a sample boundary (radio_ctrl_proc.v:
/// an untimed setting goes at once; b200_impl.cpp: the AD9361 is SPI on the local control, which no
/// command time reaches), so on UHD these steps say <see cref="TimingBound"/>; the synthetic radio, which
/// owns its clock, applies them at the exact sample and says <see cref="TimingSample"/>.</item>
/// </list>
/// Timed CORDIC writes (set_command_time) would make the tuned regime exact on UHD too, and are left out
/// until they can be proven on this board: a pending timed command holds the radio's whole command
/// processor, and a late one runs at once with no word to the host (section 9.18 P4b2).
/// </summary>
internal sealed class TxStepPlan
{
    /// <summary>
    /// <c>caps.tx.max_step_points</c>. 8,192 is the RF Bench Analyser's trace budget
    /// (<c>SoundCardCapabilities.MaxBins</c>, the point count its sweeps are planned in), so a stepped sweep
    /// never has more points than the trace it fills. At the 5 ms minimum dwell that is 41 s a pass and
    /// 200 <c>tx_step</c> events a second, which the event stream carries; the frequency table is 64 KB.
    /// </summary>
    public const int MaxPoints = 8192;

    /// <summary><c>tx_step.timing</c>: the step's instant is step 0's plus its sample count at the TX rate,
    /// counted by the radio's own DAC - exact against it. The synthetic radio: exact against its clock.</summary>
    public const string TimingSample = "sample";

    /// <summary><c>tx_step.timing</c>: the step's instant is when the write that moved the carrier returned
    /// - an upper bound. A UHD frequency write lands when it arrives, not at a sample.</summary>
    public const string TimingBound = "bound";

    public bool Baseband { get; }
    public double RateHz { get; }

    /// <summary>The sweep's centre: where the LO parks for a baseband sweep and for a tuned one that fits
    /// one window.</summary>
    public long CenterHz { get; }

    public int Points { get; }
    public int DwellMs { get; }
    public bool Repeat { get; }
    public bool Bounce { get; }

    /// <summary>Each point's frequency, by <see cref="TxStartRequest.StepFrequencyHz"/> - the one formula.</summary>
    public long[] FrequencyHz { get; }

    /// <summary>The LO each point's step stands at: the sweep's centre for a baseband sweep, its window's
    /// centre for a tuned one.</summary>
    public long[] LoHz { get; }

    /// <summary>How many LO windows a tuned sweep is cut into (1 for a baseband sweep).</summary>
    public int Windows { get; }

    /// <summary>The DUC's reach used to cut the windows (0 for a baseband sweep).</summary>
    public double ReachHz { get; }

    private readonly long _rate;

    private TxStepPlan(TxStartRequest r, bool baseband, double rateHz, long centerHz, long[] freqs, long[] los, int windows, double reachHz)
    {
        Baseband = baseband;
        RateHz = rateHz;
        _rate = (long)Math.Round(rateHz);
        CenterHz = centerHz;
        Points = r.Points;
        DwellMs = r.DwellMs;
        Repeat = r.Repeat;
        Bounce = r.Bounce;
        FrequencyHz = freqs;
        LoHz = los;
        Windows = windows;
        ReachHz = reachHz;
    }

    /// <summary>The plan for a <c>step</c> request TxRules has admitted against <paramref name="caps"/>, at a
    /// master clock of <paramref name="tickHz"/>. Null when no admitted rate exists at all.</summary>
    public static TxStepPlan? For(TxStartRequest r, TxCaps caps, double tickHz)
    {
        if (r.Points < 2 || r.Points > MaxPoints || r.DwellMs < 1 || caps.RatesHz == null) return null;
        double[] rates = caps.RatesHz.Where(x => x > 0).OrderBy(x => x).ToArray();
        if (rates.Length == 0) return null;
        var freqs = new long[r.Points];
        for (int i = 0; i < r.Points; i++) freqs[i] = TxStartRequest.StepFrequencyHz(r.StartHz, r.StopHz, r.Points, r.Spacing, i);
        long center = r.StartHz + (r.StopHz - r.StartHz) / 2;
        long maxOff = Math.Max(r.StopHz - center, center - r.StartHz);

        foreach (double rate in rates)
        {
            if (maxOff <= rate * ProtocolConstants.TxUsableFraction / 2.0 + 1e-6)
                return new TxStepPlan(r, baseband: true, rate, center, freqs, Enumerable.Repeat(center, r.Points).ToArray(), 1, 0);
        }

        // Tuned: a carrier at baseband 0 Hz at the lowest admitted rate (what TxRules.RateFor answers for a
        // signal with no width), moved by the DUC inside its reach and by the synthesizer beyond.
        double tuned = rates[0];
        double reach = TxRatePlan.DspReachHz(tickHz, tuned);
        var los = new long[r.Points];
        long span = r.StopHz - r.StartHz;
        int windows;
        if (reach < 1)
        {
            // No reach at all: the LO sits on every carrier.
            for (int i = 0; i < r.Points; i++) los[i] = freqs[i];
            windows = r.Points;
        }
        else if (span / 2.0 <= reach)
        {
            for (int i = 0; i < r.Points; i++) los[i] = center;
            windows = 1;
        }
        else
        {
            // As few equal windows as the reach allows, 2 Hz narrower each for the centres' rounding; a point
            // lands in the window that contains it, so its offset from that window's centre is within reach.
            windows = (int)Math.Ceiling(span / (2.0 * reach - 2.0));
            double width = span / (double)windows;
            for (int i = 0; i < r.Points; i++)
            {
                int w = Math.Min(windows - 1, (int)Math.Floor((freqs[i] - r.StartHz) / width));
                los[i] = r.StartHz + (long)Math.Round((w + 0.5) * width);
            }
        }
        return new TxStepPlan(r, baseband: false, tuned, center, freqs, los, windows, reach);
    }

    /// <summary>Steps in the whole run: <c>points</c>, or <c>2 points - 1</c> for a bounce (the far end is
    /// not doubled); <see cref="long.MaxValue"/> when it repeats.</summary>
    public long StepCount => Repeat ? long.MaxValue : Bounce ? 2L * Points - 1 : Points;

    /// <summary>The point index the <paramref name="k"/>-th step stands at. A bounce runs 0 … N-1 … 1 and
    /// round again (0 … N-1 … 0 once, without repeat) - a turning point is never doubled; a repeat without
    /// bounce runs 0 … N-1, 0 … N-1.</summary>
    public int IndexOf(long k)
    {
        if (k <= 0) return 0;
        int n = Points;
        if (!Bounce) return (int)(Repeat ? k % n : Math.Min(k, n - 1));
        long period = 2L * n - 2;
        long j = Repeat ? k % period : Math.Min(k, period);
        return (int)(j < n ? j : period - j);
    }

    /// <summary>The TX sample at which the <paramref name="k"/>-th step begins, counted from the key-up:
    /// <c>floor(k x dwell_ms x rate / 1000)</c>, exact in integers - so a dwell that is not a whole number of
    /// samples (5 ms at 312.5 kS/s is 1562.5) alternates by one sample and never drifts.</summary>
    public long StartSample(long k)
    {
        long kd = k * DwellMs;
        return kd / 1000 * _rate + kd % 1000 * _rate / 1000;
    }

    /// <summary>Where a run that does not repeat ends: the last dwell's last sample, plus one.</summary>
    public long EndSample => Repeat ? long.MaxValue : StartSample(StepCount);

    /// <summary>A step's retune word: the synthesizer's when its LO differs from the one standing, the DUC's
    /// otherwise (for a baseband step, the generator's - the digital path moved it and the LO did not).</summary>
    public static string RetuneFor(long lo, long standingLo) => lo != standingLo ? TxWords.RetuneRf : TxWords.RetuneDsp;
}
