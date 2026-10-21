// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;

namespace ShackLinkUsrp.Dsp;

/// <summary>
/// A lease's channel filter in two stages (ShackLink docs/usrp_b210_support.md section 5.4, docs/skimmer_support.md
/// section 26.14): k halfbands, each dividing the rate by 2, then the shaping <see cref="DecimatingFir"/> at D / 2^k.
/// The single-stage filter paid its taps a phase on EVERY input sample of the wide stream; here only the first
/// halfband does, and it costs a few multiplies.
/// <para><b>The halfbands keep their images out of the whole output band</b>: each is designed so that what it folds
/// onto [-R/2, R/2] (R the lease rate) is at or below <see cref="HalfbandTargetDb"/>. The shaping filter alone then
/// decides the lease's edge and its clean fraction, exactly as the single stage did.</para>
/// <para><b>Timing is the single stage's, exactly.</b> The cascade's group delay is longer; the difference
/// (<see cref="LeadSamples"/>) is taken off the front of the stream, so output j is centred on input
/// (j + 1) D - 1 - 8D, the instant <see cref="DecimatingFir"/>(D) at 16 taps a phase centres it on, and is completed
/// LeadSamples inputs later. So a lease's sample k still means the radio position it meant (origin + k D, the
/// same fixed offset), and only its latency grows - by LeadSamples / Fs.</para>
/// </summary>
public sealed class LeaseDecimator
{
    /// <summary>What a halfband may fold onto the output band, dB. Below the shaping filter's own -80 dB by enough that
    /// the clean fraction stays the shaping filter's.</summary>
    public const double HalfbandTargetDb = -100;

    /// <summary>The shaping filter never decimates by less than this: by 1 it would be no filter at all, and the
    /// last halfband's transition would become the lease's edge.</summary>
    public const int MinShapingDecimation = 2;

    /// <summary>The single stage this cascade replaces: its taps a phase fix the reference timing.</summary>
    public const int ReferenceTapsPerPhase = DecimatingFir.TapsPerPhase;

    private readonly HalfbandDecimator[] _halfbands;
    private readonly DecimatingFir _shaping;
    private readonly float[][] _scratchRe, _scratchIm;
    private int _lead;

    public int Decimation { get; }
    public LeasePlan Plan { get; }

    /// <summary>The inputs taken off the front so the timing is the single stage's (see the class remarks).</summary>
    public int LeadSamples => Plan.LeadSamples;

    public LeaseDecimator(int decimation) : this(decimation, LeasePlan.DefaultShapingTapsPerPhase) { }

    public LeaseDecimator(int decimation, int shapingTapsPerPhase) : this(LeasePlan.For(decimation, shapingTapsPerPhase)) { }

    public LeaseDecimator(LeasePlan plan)
    {
        Plan = plan;
        Decimation = plan.Decimation;
        _halfbands = plan.Halfbands.Select(p => new HalfbandDecimator(p)).ToArray();
        _shaping = new DecimatingFir(plan.ShapingDecimation, plan.ShapingTapsPerPhase);
        _scratchRe = new float[_halfbands.Length][];
        _scratchIm = new float[_halfbands.Length][];
        for (int i = 0; i < _halfbands.Length; i++)
        {
            _scratchRe[i] = Array.Empty<float>();
            _scratchIm[i] = Array.Empty<float>();
        }
        _lead = plan.LeadSamples;
    }

    /// <summary>Push <paramref name="count"/> complex inputs; write each completed output into outRe/outIm starting at
    /// <paramref name="outOffset"/>. Returns the outputs written. <see cref="DecimatingFir.Process"/>'s contract.</summary>
    public int Process(ReadOnlySpan<float> re, ReadOnlySpan<float> im, int count, float[] outRe, float[] outIm, int outOffset)
    {
        if (_lead > 0)
        {
            int skip = Math.Min(_lead, count);
            _lead -= skip;
            re = re.Slice(skip);
            im = im.Slice(skip);
            count -= skip;
            if (count == 0) return 0;
        }
        for (int s = 0; s < _halfbands.Length; s++)
        {
            int need = count / 2 + 1;
            if (_scratchRe[s].Length < need)
            {
                _scratchRe[s] = new float[Math.Max(need, 2 * _scratchRe[s].Length)];
                _scratchIm[s] = new float[_scratchRe[s].Length];
            }
            count = _halfbands[s].Process(re, im, count, _scratchRe[s], _scratchIm[s]);
            re = _scratchRe[s];
            im = _scratchIm[s];
            if (count == 0) return 0;
        }
        return _shaping.Process(re, im, count, outRe, outIm, outOffset);
    }

    public void Reset()
    {
        foreach (var h in _halfbands) h.Reset();
        _shaping.Reset();
        _lead = Plan.LeadSamples;
    }
}

/// <summary>
/// How a lease of decimation D is cut: which halfbands, then the shaping filter at D / 2^k. Pure arithmetic over D,
/// cached, so every lease of one decimation runs the same filter and two leases at one centre stay in phase.
/// </summary>
public sealed class LeasePlan
{
    /// <summary>The shaping filter's taps a phase: 24, not the single stage's 16, because the halfbands made the longer
    /// filter nearly free. At 16 a lease was clean (images at or below -80 dB) over 66 % of its width; at 24 over 78 %
    /// (ShackLink docs/skimmer_support.md section 26.14, measured by --fir-response and --fir-scaling).</summary>
    public const int DefaultShapingTapsPerPhase = 24;

    // Relative cost, in multiplies per WIDE input sample, of moving one sample through a stage's bookkeeping (the
    // split into even and odd, the history copy): a halfband is cheap enough that its overhead decides how deep the
    // cascade should go. Calibrated against --fir-scaling, not derived.
    private const double HalfbandOverheadPerInput = 1.0;
    private const double ShapingOverheadPerInput = 2.0;

    private static readonly ConcurrentDictionary<(int, int), LeasePlan> Cache = new();

    public int Decimation { get; }
    public int ShapingDecimation { get; }
    public int ShapingTapsPerPhase { get; }

    /// <summary>The halfbands' pair coefficients, wide rate first.</summary>
    public IReadOnlyList<double[]> Halfbands { get; }

    /// <summary>The cascade's group delay in wide input samples: each halfband's (2K - 1) at its own rate, plus the
    /// shaping filter's tapsPerPhase x D / 2.</summary>
    public int GroupDelay { get; }

    /// <summary>The single stage's group delay, 16 x D / 2 (zero at D = 1): the timing this plan keeps.</summary>
    public int ReferenceGroupDelay { get; }

    /// <summary>GroupDelay - ReferenceGroupDelay: the inputs a <see cref="LeaseDecimator"/> drops at its start.</summary>
    public int LeadSamples => GroupDelay - ReferenceGroupDelay;

    /// <summary>Estimated multiplies per wide input sample (the plan's own cost model, used to choose k).</summary>
    public double EstimatedCost { get; }

    private LeasePlan(int d, int shapingD, int tapsPerPhase, double[][] halfbands, double cost)
    {
        Decimation = d;
        ShapingDecimation = shapingD;
        ShapingTapsPerPhase = tapsPerPhase;
        Halfbands = halfbands;
        int delay = 0, scale = 1;
        foreach (var hb in halfbands)
        {
            delay += (2 * hb.Length - 1) * scale;
            scale *= 2;
        }
        delay += shapingD == 1 ? 0 : tapsPerPhase * shapingD / 2 * scale;
        GroupDelay = delay;
        ReferenceGroupDelay = d == 1 ? 0 : LeaseDecimator.ReferenceTapsPerPhase * d / 2;
        EstimatedCost = cost;
        if (LeadSamples < 0) throw new InvalidOperationException($"a lease plan for D={d} is shorter than the single stage it replaces");
    }

    public static LeasePlan For(int decimation, int shapingTapsPerPhase = DefaultShapingTapsPerPhase)
    {
        if (decimation < 1) throw new ArgumentOutOfRangeException(nameof(decimation));
        if (shapingTapsPerPhase < LeaseDecimator.ReferenceTapsPerPhase || shapingTapsPerPhase % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(shapingTapsPerPhase), "an even number, at least the single stage's 16");
        return Cache.GetOrAdd((decimation, shapingTapsPerPhase), key => Build(key.Item1, key.Item2, depth: -1)!);
    }

    /// <summary>The plan with exactly <paramref name="depth"/> halfbands, or null when the decimation does not allow
    /// that many (an instrument's question: what each depth costs). Not cached.</summary>
    public static LeasePlan? WithDepth(int decimation, int shapingTapsPerPhase, int depth) =>
        Build(decimation, shapingTapsPerPhase, depth);

    private static LeasePlan? Build(int d, int tapsPerPhase, int depth)
    {
        // Every depth k the decimation allows (D / 2^k whole and at least MinShapingDecimation), cheapest wins.
        LeasePlan? best = null;
        var stages = new List<double[]>();
        for (int k = 0; depth < 0 || k <= depth; k++)
        {
            if (k > 0)
            {
                if (d % (1 << k) != 0 || d / (1 << k) < LeaseDecimator.MinShapingDecimation) break;
                // Stage k runs at Fs / 2^(k-1); the output band's edge R / 2 = Fs / (2D) is 2^(k-1) / (2D) of it.
                double pass = (double)(1 << (k - 1)) / (2.0 * d);
                var hb = HalfbandDecimator.Design(pass, LeaseDecimator.HalfbandTargetDb);
                if (hb == null) break;
                stages.Add(hb);
            }
            int shapingD = d >> k;
            double cost = 0;
            for (int j = 0; j < stages.Count; j++)
                cost += ((stages[j].Length + 1) / 2.0 + HalfbandOverheadPerInput) / (1 << j);
            cost += shapingD == 1 ? 0 : (tapsPerPhase + ShapingOverheadPerInput) / (1 << k);
            if (depth >= 0 ? k == depth : best == null || cost < best.EstimatedCost)
                best = new LeasePlan(d, shapingD, tapsPerPhase, stages.ToArray(), cost);
        }
        return best;
    }

    // ------------------------------------------------------------------ the composite response

    private double[]? _shapingTaps;

    /// <summary>|H(f)| of the whole cascade at <paramref name="f"/> cycles per WIDE input sample: the product of each
    /// halfband at its own rate and the shaping filter at Fs / 2^k (the noble identity: decimating in stages is
    /// decimating once through this filter). From the double-precision design, before the float rounding.</summary>
    public double MagnitudeAt(double f)
    {
        double a = 1;
        int scale = 1;
        foreach (var hb in Halfbands)
        {
            a *= HalfbandDecimator.Amplitude(hb, f * scale);
            scale *= 2;
        }
        var h = _shapingTaps ??= ShapingDecimation == 1
            ? new[] { 1.0 }
            : DecimatingFir.Design(ShapingTapsPerPhase * ShapingDecimation + 1, 0.5 / ShapingDecimation);
        double fs = f * scale, re = 0, im = 0;
        for (int n = 0; n < h.Length; n++)
        {
            re += h[n] * Math.Cos(2 * Math.PI * fs * n);
            im -= h[n] * Math.Sin(2 * Math.PI * fs * n);
        }
        return Math.Abs(a) * Math.Sqrt(re * re + im * im);
    }

    /// <summary>The gain at the edge of a segment that reaches <paramref name="share"/> of the lease (x = share / 2
    /// cycles per output sample), dB.</summary>
    public double PassDb(double share) => 20 * Math.Log10(Math.Max(1e-15, MagnitudeAt(share / 2 / Decimation)));

    /// <summary>The strongest image that folds onto the edge of a segment reaching <paramref name="share"/> of the
    /// lease, relative to the gain there, dB: every input frequency (x + m) / D, m a non-zero integer, through every
    /// stage. A signal that strong just outside the segment appears this far down inside it.</summary>
    public double WorstAliasDb(double share)
    {
        double x = share / 2;
        double pass = MagnitudeAt(x / Decimation), worst = 0;
        int half = Decimation / 2 + 1;
        for (int m = -half; m <= half; m++)
            if (m % Decimation != 0) worst = Math.Max(worst, MagnitudeAt((x + m) / Decimation));   // m = +-D is x itself
        return 20 * Math.Log10(Math.Max(1e-15, worst) / Math.Max(1e-15, pass));
    }

    /// <summary>The strongest image anywhere inside a segment reaching <paramref name="share"/> of the lease: the
    /// response lobes, so the edge alone is not the worst point. Sampled every half a percent and at the edge.</summary>
    public double WorstAliasWithinDb(double share)
    {
        double worst = WorstAliasDb(share);
        for (double s = 0.005; s < share; s += 0.005) worst = Math.Max(worst, WorstAliasDb(s));
        return worst;
    }

    public override string ToString() =>
        $"D={Decimation}: {Halfbands.Count} halfband(s) [{string.Join(", ", Halfbands.Select(h => 4 * h.Length - 1))} taps] " +
        $"then {ShapingTapsPerPhase} taps a phase at D={ShapingDecimation}; delay {GroupDelay} (lead {LeadSamples})";
}
