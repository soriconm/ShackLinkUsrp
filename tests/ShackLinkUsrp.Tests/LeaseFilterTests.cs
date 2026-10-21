// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// ShackLink docs/skimmer_support.md section 26.14 (2): the two-stage lease filter (halfbands, then the shaping filter at the
// reduced rate) and the vectorised NCO in front of it. Against references: the exact phasor, a direct double-precision
// decimator through the cascade's composite filter, and the single-stage filter's timing. And the lease's clean fraction,
// ProtocolConstants.LeaseCleanFraction, pinned against the filter at every decimation.

using System.Numerics;
using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Tests;

internal static partial class Program
{
    /// <summary>Every depth of cascade (k = 0 .. 7), odd decimations (no halfband at all), the smallest ones, and the
    /// decimations ShackLink's lease rates come to at 40, 61.44 and 30.72 MS/s.</summary>
    private static readonly int[] FilterCaseDecimations = { 1, 2, 3, 4, 6, 12, 16, 20, 25, 32, 40, 50, 64, 80, 100, 125, 128, 160, 200, 256, 320 };

    private static readonly int[] OddChunks = { 1, 2, 3, 5, 7, 8, 9, 15, 16, 17, 63, 64, 65, 255, 1000, 4096, 8192 };

    private static void NcoVectorCases()
    {
        // Whole vector blocks, a scalar tail, and calls that start mid-block - against exp(-j 2 pi shift p / rate) at
        // the exact integer phase of every position p.
        var rnd = new Random(26_14);
        foreach (long shift in new long[] { 0, 1_234_000, -9_876_543, 19_999_999 })
        {
            const double rate = 40e6;
            const long start = 123_456_789;
            var nco = new Nco();
            nco.Set(shift, rate);
            nco.AnchorAt(start);
            int total = 0;
            double worst = 0;
            long pos = start;
            foreach (int count in OddChunks.Concat(OddChunks))
            {
                var iq = new short[2 * count];
                for (int i = 0; i < iq.Length; i++) iq[i] = (short)rnd.Next(-32768, 32768);
                var re = new float[count];
                var im = new float[count];
                nco.Mix(iq, count, re, im);
                for (int n = 0; n < count; n++, pos++)
                {
                    long cyc = (long)(((Int128)shift * pos % (long)rate + (long)rate) % (long)rate);
                    double w = -2 * Math.PI * cyc / rate;
                    var want = new Complex(iq[2 * n] / 32768.0, iq[2 * n + 1] / 32768.0) * new Complex(Math.Cos(w), Math.Sin(w));
                    worst = Math.Max(worst, (new Complex(re[n], im[n]) - want).Magnitude);
                }
                total += count;
            }
            Check(worst < 1e-6, $"shift {shift} Hz: {total} samples in {OddChunks.Length * 2} calls of every size, within {worst:0.0e0} of full scale of the exact phasor " +
                                $"(Vector<float>.Count {Vector<float>.Count})");
        }
    }

    /// <summary>The cascade's composite impulse response: each halfband at its own rate, then the shaping filter, every
    /// stage's taps spread by the rate it runs at (the noble identity).</summary>
    private static double[] CompositeTaps(LeasePlan plan)
    {
        double[] h = { 1.0 };
        int scale = 1;
        foreach (var g in plan.Halfbands)
        {
            int k = g.Length, c = 2 * k - 1;
            var hb = new double[4 * k - 1];
            hb[c] = 0.5;
            for (int i = 1; i <= k; i++) hb[c + 2 * i - 1] = hb[c - 2 * i + 1] = g[i - 1];
            h = Convolve(h, Spread(hb, scale));
            scale *= 2;
        }
        var shaping = plan.ShapingDecimation == 1
            ? new[] { 1.0 }
            : DecimatingFir.Design(plan.ShapingTapsPerPhase * plan.ShapingDecimation + 1, 0.5 / plan.ShapingDecimation);
        return Convolve(h, Spread(shaping, scale));

        static double[] Spread(double[] t, int by)
        {
            var o = new double[(t.Length - 1) * by + 1];
            for (int i = 0; i < t.Length; i++) o[i * by] = t[i];
            return o;
        }
        static double[] Convolve(double[] a, double[] b)
        {
            var o = new double[a.Length + b.Length - 1];
            for (int i = 0; i < a.Length; i++)
                if (a[i] != 0)
                    for (int j = 0; j < b.Length; j++) o[i + j] += a[i] * b[j];
            return o;
        }
    }

    private static void LeaseFilterReferenceCases()
    {
        // The running cascade, fed in chunks of every size, against y[j] = sum_t h[t] x[t_j - t] in double precision,
        // t_j = (j + 1) D - 1 + lead, the inputs before the lead being dropped (never history).
        foreach (int d in FilterCaseDecimations)
        {
            var plan = LeasePlan.For(d);
            var h = CompositeTaps(plan);
            Check((h.Length - 1) / 2 == plan.GroupDelay, $"D={d}: the composite filter's centre is the plan's group delay ({plan.GroupDelay})");
            int total = Math.Max(30_000, 120 * d) + plan.LeadSamples;
            var rnd = new Random(d);
            var xr = new float[total];
            var xi = new float[total];
            for (int n = 0; n < total; n++) { xr[n] = (float)(rnd.NextDouble() - 0.5); xi[n] = (float)(rnd.NextDouble() - 0.5); }

            var dec = new LeaseDecimator(plan);
            var got = new List<Complex>();
            var oRe = new float[8192 / Math.Max(1, d) + 4096];
            var oIm = new float[oRe.Length];
            int at = 0, c = 0;
            while (at < total)
            {
                int count = Math.Min(OddChunks[c++ % OddChunks.Length], total - at);
                int m = dec.Process(xr.AsSpan(at, count), xi.AsSpan(at, count), count, oRe, oIm, 0);
                for (int i = 0; i < m; i++) got.Add(new Complex(oRe[i], oIm[i]));
                at += count;
            }

            int expect = (total - plan.LeadSamples) / d;
            double worst = 0;
            for (int j = 0; j < Math.Min(expect, got.Count); j++)
            {
                long tj = (long)(j + 1) * d - 1 + plan.LeadSamples;
                double sr = 0, si = 0;
                for (int t = 0; t < h.Length; t++)
                {
                    long n = tj - t;
                    if (n < plan.LeadSamples) break;
                    sr += h[t] * xr[n];
                    si += h[t] * xi[n];
                }
                worst = Math.Max(worst, (got[j] - new Complex(sr, si)).Magnitude);
            }
            Check(got.Count == expect && worst < 2e-6,
                  $"D={d} ({plan.Halfbands.Count} halfbands, lead {plan.LeadSamples}): {got.Count} outputs of {expect} from {total} inputs in chunks of every size, " +
                  $"each within {worst:0.0e0} of the reference decimator");
        }
    }

    private static void LeaseFilterTimingCases()
    {
        // A tone through the cascade and through the single stage it replaced: the same instant at every output, so the
        // same phase. One wide sample of misalignment is 360 x / D degrees: 0.54 at D = 200, 6.75 at D = 16.
        foreach (int d in new[] { 4, 16, 20, 25, 40, 50, 80, 160, 200, 320 })
        {
            var plan = LeasePlan.For(d);
            foreach (double x in new[] { 0.3, -0.21, 0.05 })
            {
                int total = 400 * d + plan.LeadSamples + plan.GroupDelay;
                var re = new float[total];
                var im = new float[total];
                for (int n = 0; n < total; n++)
                {
                    double ph = 2 * Math.PI * x / d * n;
                    re[n] = (float)(0.5 * Math.Cos(ph));
                    im[n] = (float)(0.5 * Math.Sin(ph));
                }
                var single = new DecimatingFir(d);
                var cascade = new LeaseDecimator(plan);
                var aRe = new float[total / d + 2];
                var aIm = new float[aRe.Length];
                var bRe = new float[aRe.Length];
                var bIm = new float[aRe.Length];
                int na = single.Process(re, im, total, aRe, aIm, 0);
                int nb = cascade.Process(re, im, total, bRe, bIm, 0);
                int from = SettledFrom(plan);
                double worstDeg = 0, worstDb = 0;
                int pairs = 0;
                for (int j = from; j < Math.Min(na, nb); j++)
                {
                    var r = new Complex(bRe[j], bIm[j]) / new Complex(aRe[j], aIm[j]);
                    double deg = r.Phase * 180 / Math.PI;
                    worstDeg = Math.Max(worstDeg, Math.Abs(deg));
                    worstDb = Math.Max(worstDb, Math.Abs(20 * Math.Log10(r.Magnitude)));
                    pairs++;
                }
                Check(pairs > 100 && worstDeg < 0.02 && worstDb < 0.01,
                      $"D={d}, a tone at {x:0.00} of the lease: the cascade reads the single stage's samples within {worstDeg:0.0000} deg and {worstDb:0.0000} dB " +
                      $"over {pairs} outputs (one wide sample late would be {360 * Math.Abs(x) / d:0.00} deg)");
            }
        }
    }

    private static void LeaseFilterCleanFractionCases()
    {
        double share = ProtocolConstants.LeaseCleanFraction;
        // On the design, at every decimation the gate exercises and every one from 2 to 64: nothing folds into a segment
        // reaching LeaseCleanFraction of the lease stronger than -80 dB, anywhere inside it.
        var all = Enumerable.Range(2, 63).Concat(FilterCaseDecimations).Concat(new[] { 400, 512, 640 }).Distinct().OrderBy(v => v);
        double worst = double.MinValue;
        int worstD = 0;
        foreach (int d in all)
        {
            double w = LeasePlan.For(d).WorstAliasWithinDb(share);
            if (w > worst) { worst = w; worstD = d; }
        }
        Check(worst <= -80, $"ProtocolConstants.LeaseCleanFraction = {share}: the strongest image inside such a segment, at any decimation, is {worst:0.0} dB (D={worstD})");
        Check(LeasePlan.For(80).WorstAliasDb(0.80) > -80,
              $"...and the constant is not loose by much: at 80 % the same filter lets an image in at {LeasePlan.For(80).WorstAliasDb(0.80):0.0} dB");

        // On the running filter, in time: the worst image at the constant's edge, and a tone in each halfband's fold zone.
        foreach (int d in new[] { 16, 20, 25, 80, 200, 320 })
        {
            var plan = LeasePlan.For(d);
            double x = share / 2;
            int mWorst = 1;
            double hWorst = 0;
            for (int m = -(d / 2 + 1); m <= d / 2 + 1; m++)
            {
                if (m % d == 0) continue;
                double hm = plan.MagnitudeAt((x + m) / d);
                if (hm > hWorst) { hWorst = hm; mWorst = m; }
            }
            double pass = ToneLevelDb(plan, x / d);
            double image = ToneLevelDb(plan, (x + mWorst) / d);
            Check(image - pass <= -80, $"D={d}: the worst image at {share * 100:0} % of the lease (m = {mWorst}), measured on the running filter: {image - pass:0.0} dB");
            for (int j = 1; j <= plan.Halfbands.Count; j++)
            {
                double fold = 1.0 / (1 << j) + x / d;   // stage j's own Nyquist, plus the edge: folds onto the edge
                double lvl = ToneLevelDb(plan, fold) - pass;
                Check(lvl <= LeaseDecimator.HalfbandTargetDb + 5,
                      $"D={d}: a tone in halfband {j}'s fold zone arrives at {lvl:0.0} dB (designed {LeaseDecimator.HalfbandTargetDb} dB)");
            }
        }
    }

    /// <summary>The first output whose whole window lies inside what was fed: output j is centred GroupDelay before input
    /// (j + 1) D - 1 + lead and reaches as far again before that, and nothing before the lead is fed.</summary>
    private static int SettledFrom(LeasePlan plan) => 2 * plan.GroupDelay / plan.Decimation + 2;

    /// <summary>A complex tone at <paramref name="f"/> cycles per wide sample through a fresh cascade: its steady-state
    /// output power, dB re a unit input.</summary>
    private static double ToneLevelDb(LeasePlan plan, double f)
    {
        int d = plan.Decimation;
        int total = 300 * d + plan.LeadSamples + plan.GroupDelay;
        var re = new float[total];
        var im = new float[total];
        for (int n = 0; n < total; n++)
        {
            double ph = 2 * Math.PI * (f * n - Math.Floor(f * n));
            re[n] = (float)Math.Cos(ph);
            im[n] = (float)Math.Sin(ph);
        }
        var dec = new LeaseDecimator(plan);
        var oRe = new float[total / d + 2];
        var oIm = new float[oRe.Length];
        int m = dec.Process(re, im, total, oRe, oIm, 0);
        int from = SettledFrom(plan);
        double p = 0;
        for (int j = from; j < m; j++) p += oRe[j] * (double)oRe[j] + oIm[j] * (double)oIm[j];
        return 10 * Math.Log10(Math.Max(1e-30, p / Math.Max(1, m - from)));
    }

    private static void LeaseFilterSourceRules()
    {
        string pipeline = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ShackLinkUsrp", "Pipelines", "LeasePipeline.cs"));
        string? run = MemberBody(pipeline, "private void RunCore(");
        Check(run != null && run.Contains("new LeaseDecimator(FilterPlan)") && !run.Contains("new DecimatingFir("),
              "a lease filters through the two-stage cascade, never the single stage directly");
        Check(run != null && CountOf(run, "ncos[s].AnchorAt(pos);") == 1 && run.IndexOf("ncos[s].AnchorAt(pos);", StringComparison.Ordinal) <
              run.IndexOf("ncos[s].Mix(", StringComparison.Ordinal),
              "...and its NCO is still anchored to the radio position before it mixes each chunk");
    }
}
