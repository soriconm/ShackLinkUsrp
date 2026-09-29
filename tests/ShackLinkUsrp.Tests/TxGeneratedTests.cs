// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// P4b2 (ShackLink docs/usrp_b210_support.md section 9.17 P4-a', 9.18 P4b2): the generated sources - AM / FM
// and the stepped sweep. Every transmitting case runs on the synthetic radio, which transmits into itself;
// the UHD half, which no gate here can run, is pinned in its source.

using System.Diagnostics;
using ShackLinkUsrp.Client;
using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Host;
using ShackLinkUsrp.Radio;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Tests;

internal static partial class Program
{
    /// <summary>J_n(x) by its power series - enough terms for any x a gate uses.</summary>
    private static double BesselJ(int n, double x)
    {
        double term = 1;
        for (int i = 1; i <= n; i++) term *= x / 2 / i;
        double sum = 0;
        for (int m = 0; m < 60; m++)
        {
            sum += term;
            term *= -(x / 2) * (x / 2) / ((m + 1.0) * (m + 1.0 + n));
        }
        return sum;
    }

    /// <summary>A 4096-point Hann spectrum of <paramref name="count"/> samples from <paramref name="g"/>, dBFS per
    /// bin, index 2048 = 0 Hz.</summary>
    private static double[] SpectrumOf(IIqGenerator g, int frames = 1)
    {
        var acc = new SpectrumAccumulator(4096, WindowShape.Hann, Integration.Average);
        var iq = new short[4096 * 2];
        for (int f = 0; f < frames; f++)
        {
            g.Fill(iq, 4096);
            acc.AddFrame(iq);
        }
        var row = new short[4096];
        acc.TakeRow(0, 4096, row);
        return row.Select(v => v / 100.0).ToArray();
    }

    // ---------------------------------------------------------------- DSP

    private static void ModGeneratorCases()
    {
        const double rate = 200_000;
        const double bin = rate / 4096;             // 48.828125 Hz
        double offset = 400 * bin, af = 64 * bin;    // everything ON a bin: no scalloping

        var am = ModGenerator.Am(offset, rate, -10, af, 0.5);
        var s = SpectrumOf(am);
        double want = -10 + 20 * Math.Log10(0.5 / 2);
        Check(Math.Abs(s[2048 + 400] + 10) < 0.05 && Math.Abs(s[2048 + 464] - want) < 0.05 && Math.Abs(s[2048 + 336] - want) < 0.05,
              $"AM at 50 %: the carrier reads {F(s[2048 + 400])} dBFS (its level, -10) and each sideband at +-af reads {F(s[2048 + 464])} / {F(s[2048 + 336])} " +
              $"(L + 20 log10(m / 2) = {F(want)})");
        var iq = new short[2 * 20_000];
        ModGenerator.Am(offset, rate, -10, af, 0.5).Fill(iq, 20_000);
        double peak = 0;
        for (int i = 0; i < 20_000; i++) peak = Math.Max(peak, Math.Sqrt((double)iq[2 * i] * iq[2 * i] + (double)iq[2 * i + 1] * iq[2 * i + 1]));
        Check(Math.Abs(20 * Math.Log10(peak / 32767) - ModGenerator.AmPeakDbfs(-10, 0.5)) < 0.01,
              $"its peak envelope is L + 20 log10(1 + m): {F(20 * Math.Log10(peak / 32767))} dBFS (wants {F(ModGenerator.AmPeakDbfs(-10, 0.5))})");
        Check(Throws(() => ModGenerator.Am(offset, rate, -3, af, 1.0)) && !Throws(() => ModGenerator.Am(offset, rate, -6.03, af, 1.0)),
              "an AM carrier whose peak is above full scale is refused, never clipped: -3 dBFS at 100 % (peak +3.01) is refused, -6.03 dBFS (peak -0.01) is built");
        Check(ModGenerator.AmPeakDbfs(-10, 0.5) == TxRules.AmPeakDbfs(-10, 50), "the generator's peak is TxRules' peak - one formula, m as a fraction here and a percent there");

        // FM at beta = 2.405, a hair past J0's first zero (2.404826): the carrier vanishes.
        double beta = 2.405;
        var fm = ModGenerator.Fm(offset, rate, -10, af, beta * af);
        s = SpectrumOf(fm);
        double j1 = -10 + 20 * Math.Log10(Math.Abs(BesselJ(1, beta)));
        double j2 = -10 + 20 * Math.Log10(Math.Abs(BesselJ(2, beta)));
        double j3 = -10 + 20 * Math.Log10(Math.Abs(BesselJ(3, beta)));
        Check(s[2048 + 400] < -10 - 60,
              $"FM at beta = 2.405 nulls the carrier: {F(s[2048 + 400])} dBFS where the unmodulated carrier stands at -10 (J0 = {F(BesselJ(0, beta), "0.00000")})");
        Check(Math.Abs(s[2048 + 464] - j1) < 0.05 && Math.Abs(s[2048 + 336] - j1) < 0.05 && Math.Abs(s[2048 + 528] - j2) < 0.05 &&
              Math.Abs(s[2048 + 272] - j2) < 0.05 && Math.Abs(s[2048 + 592] - j3) < 0.05,
              $"...and its sidebands stand at the Bessel values: J1 {F(s[2048 + 464])} / {F(s[2048 + 336])} (wants {F(j1)}), " +
              $"J2 {F(s[2048 + 528])} (wants {F(j2)}), J3 {F(s[2048 + 592])} (wants {F(j3)})");
        ModGenerator.Fm(offset, rate, -6, af, beta * af).Fill(iq, 20_000);
        double lo = double.MaxValue, hi = 0;
        for (int i = 0; i < 20_000; i++)
        {
            double m = Math.Sqrt((double)iq[2 * i] * iq[2 * i] + (double)iq[2 * i + 1] * iq[2 * i + 1]);
            lo = Math.Min(lo, m);
            hi = Math.Max(hi, m);
        }
        double a = 32767 * Math.Pow(10, -6 / 20.0);
        Check(Math.Abs(lo - a) < 1.5 && Math.Abs(hi - a) < 1.5, $"an FM envelope is its carrier's level: |I + jQ| stays within {F(lo, "0.0")}-{F(hi, "0.0")} of {F(a, "0.0")}");
        Check(Throws(() => ModGenerator.Fm(0, rate, -10, 20_000, 81_000)) && Throws(() => ModGenerator.Fm(0, rate, 0.5, af, 1000)),
              "an FM whose Carson band leaves the Nyquist band, or a carrier above full scale, is refused");

        foreach (var (name, make) in new (string, Func<ModGenerator>)[]
                 { ("AM", () => ModGenerator.Am(offset, rate, -3, af, 0.4)), ("FM", () => ModGenerator.Fm(offset, rate, -3, af, 5000)) })
        {
            var one = make();
            var ten = make();
            var x = new short[2 * 3000];
            var y = new short[2 * 3000];
            one.Fill(x, 3000);
            for (int k = 0; k < 10; k++) ten.Fill(y.AsSpan(k * 600), 300);
            Check(x.SequenceEqual(y), $"{name} is phase-continuous: ten calls of 300 are the same samples as one of 3000 (the carrier and the modulating sine both)");
        }

        // The tone's Retune: the carrier moves at one sample and its phase carries on.
        var t = new ToneGenerator(1000 * bin, rate, -6);
        var u = new short[2 * 1000];
        t.Fill(u, 500);
        t.Retune(-700 * bin);
        t.Fill(u.AsSpan(1000), 500);
        double Arg(int i) => Math.Atan2(u[2 * i + 1], u[2 * i]);
        double Step(int i) => Math.IEEERemainder(Arg(i + 1) - Arg(i), 2 * Math.PI);
        double w1 = 2 * Math.PI * 1000 * bin / rate, w2 = 2 * Math.PI * -700 * bin / rate;
        Check(Math.Abs(Step(498) - w1) < 1e-3 && Math.Abs(Step(499) - w1) < 1e-3 && Math.Abs(Step(500) - w2) < 1e-3,
              $"a tone retuned after sample 499 turns by the old step into sample 500 ({F(Step(499), "0.0000")} rad) and by the new one after it " +
              $"({F(Step(500), "0.0000")} rad, wants {F(w2, "0.0000")}): no phase jump");
    }

    private static TxCaps CapsAt(double wideRateHz)
    {
        double tick = TxRatePlan.ModelTickHz(wideRateHz);
        return new TxCaps
        {
            FreqMinHz = 50_000_000,
            FreqMaxHz = 6_000_000_000,
            RatesHz = TxRatePlan.Admitted(TxRatePlan.DucHostRates(tick), tick, wideRateHz),
            MinDwellMs = ProtocolConstants.TxMinDwellMs,
            MaxStepPoints = TxStepPlan.MaxPoints,
        };
    }

    private static TxStartRequest Sweep(long start, long stop, int points, int dwellMs, bool repeat = false, bool bounce = false, string spacing = TxWords.Lin) =>
        new() { Source = TxSourceKinds.Step, MaxMs = 60_000, StartHz = start, StopHz = stop, Points = points, DwellMs = dwellMs, Repeat = repeat, Bounce = bounce, Spacing = spacing, LevelDbfs = -10 };

    private static void TxStepPlanCases()
    {
        var c2 = CapsAt(2e6);
        double tick2 = TxRatePlan.ModelTickHz(2e6);
        var p = TxStepPlan.For(Sweep(100_000_000, 100_250_000, 5, 5), c2, tick2)!;
        Check(p.Baseband && p.RateHz == 400_000 && p.CenterHz == 100_125_000 && p.LoHz.All(l => l == 100_125_000),
              $"a 250 kHz sweep fits a rate's usable band, so it is generated in baseband at the LOWEST rate that holds it: {F(p.RateHz, "0")} S/s " +
              $"(250 kS/s holds +-100 kHz, not +-125), the LO at its centre {p.CenterHz} Hz");
        var edge = TxStepPlan.For(Sweep(100_000_000, 101_600_000, 3, 5), c2, tick2)!;
        var over = TxStepPlan.For(Sweep(100_000_000, 101_700_000, 3, 5), c2, tick2)!;
        Check(edge.Baseband && edge.RateHz == 2e6 && !over.Baseband && over.RateHz == 200e3 && over.Windows == 1 && over.LoHz.All(l => l == 100_850_000),
              $"1.6 MHz is the widest a baseband sweep can be (2 MS/s x 0.8); 1.7 MHz is TUNED: {F(over.RateHz, "0")} S/s, {over.Windows} LO window, the LO at its centre");
        var wide = TxStepPlan.For(Sweep(100_000_000, 140_000_000, 5, 5), c2, tick2)!;
        Check(!wide.Baseband && wide.Windows == 2 && wide.LoHz.SequenceEqual(new long[] { 110_000_000, 110_000_000, 130_000_000, 130_000_000, 130_000_000 }),
              $"100-140 MHz in 5 points is cut into {wide.Windows} windows of the DUC's reach ({F(wide.ReachHz / 1e6, "0.00")} MHz at a 32 MHz clock): LOs " +
              string.Join(", ", wide.LoHz.Select(l => F(l / 1e6, "0"))) + " MHz");
        var log = TxStepPlan.For(Sweep(50_000_000, 6_000_000_000, 101, 5, spacing: TxWords.Log), c2, tick2)!;
        bool inReach = true;
        for (int i = 0; i < log.Points; i++) inReach &= Math.Abs(log.FrequencyHz[i] - log.LoHz[i]) <= log.ReachHz;
        Check(inReach && log.FrequencyHz[0] == 50_000_000 && log.FrequencyHz[100] == 6_000_000_000 && log.LoHz.All(l => l >= 50_000_000 && l <= 6_000_000_000),
              $"50 MHz - 6 GHz, 101 log points: {log.Windows} windows, every point within the reach of its window's LO, every LO inside the sweep");
        Check(TxStepPlan.For(Sweep(100_000_000, 101_000_000, TxStepPlan.MaxPoints + 1, 5), c2, tick2) == null && TxStepPlan.MaxPoints == 8192,
              "more than 8,192 points (the RF Bench Analyser's trace budget) makes no plan");

        long[] Seq(TxStepPlan q, int n) => Enumerable.Range(0, n).Select(k => (long)q.IndexOf(k)).ToArray();
        var once = TxStepPlan.For(Sweep(100_000_000, 100_100_000, 5, 5), c2, tick2)!;
        var rep = TxStepPlan.For(Sweep(100_000_000, 100_100_000, 5, 5, repeat: true), c2, tick2)!;
        var bnc = TxStepPlan.For(Sweep(100_000_000, 100_100_000, 5, 5, bounce: true), c2, tick2)!;
        var both = TxStepPlan.For(Sweep(100_000_000, 100_100_000, 5, 5, repeat: true, bounce: true), c2, tick2)!;
        var two = TxStepPlan.For(Sweep(100_000_000, 100_100_000, 2, 5, repeat: true, bounce: true), c2, tick2)!;
        Check(once.StepCount == 5 && Seq(once, 5).SequenceEqual(new long[] { 0, 1, 2, 3, 4 }) &&
              rep.StepCount == long.MaxValue && Seq(rep, 12).SequenceEqual(new long[] { 0, 1, 2, 3, 4, 0, 1, 2, 3, 4, 0, 1 }) &&
              bnc.StepCount == 9 && Seq(bnc, 9).SequenceEqual(new long[] { 0, 1, 2, 3, 4, 3, 2, 1, 0 }) &&
              Seq(both, 12).SequenceEqual(new long[] { 0, 1, 2, 3, 4, 3, 2, 1, 0, 1, 2, 3 }) &&
              Seq(two, 5).SequenceEqual(new long[] { 0, 1, 0, 1, 0 }),
              "the sequences: once 0-4 (5 steps); repeat 0-4, 0-4; bounce 0-4-0 (9 steps, the far end not doubled); repeat + bounce 0-4-1, 0-4-1 " +
              "(no turning point doubled); two points bouncing 0,1,0,1");

        var c10 = CapsAt(10e6);
        var odd = TxStepPlan.For(Sweep(100_000_000, 100_240_000, 3, 5), c10, TxRatePlan.ModelTickHz(10e6))!;
        Check(odd.Baseband && odd.RateHz == 312_500 && odd.StartSample(1) == 1562 && odd.StartSample(2) == 3125 && odd.StartSample(1000) == 1_562_500 &&
              odd.StartSample(1001) == 1_564_062,
              $"a 5 ms dwell at {F(odd.RateHz, "0")} S/s is 1562.5 samples: steps start at 1562, 3125 ... 1,562,500 at step 1000 - exact in integers, never drifting");
        Check(bnc.EndSample == bnc.StartSample(9) && rep.EndSample == long.MaxValue,
              "a run that does not repeat ends after its last dwell; a repeating one never does");
        Check(TxStepPlan.RetuneFor(130_000_000, 110_000_000) == TxWords.RetuneRf && TxStepPlan.RetuneFor(110_000_000, 110_000_000) == TxWords.RetuneDsp,
              "a step whose LO differs from the one standing moves the synthesizer (rf); otherwise the DUC (dsp)");
    }

    private static void SteppedCarrierCases()
    {
        var caps = CapsAt(2e6);
        var plan = TxStepPlan.For(Sweep(100_000_000, 100_250_000, 5, 5, repeat: true, bounce: true), caps, TxRatePlan.ModelTickHz(2e6))!;
        var one = new SteppedCarrier(plan, -6);
        const int total = 20_000;
        var x = new short[2 * total];
        one.Fill(x, total);
        var chunked = new SteppedCarrier(plan, -6);
        var y = new short[2 * total];
        int at = 0;
        foreach (int n in Enumerable.Repeat(new[] { 37, 1999, 2000, 1, 513 }, 50).SelectMany(v => v))
        {
            if (at >= total) break;
            int take = Math.Min(n, total - at);
            chunked.Fill(y.AsSpan(2 * at), take);
            at += take;
        }
        Check(x.SequenceEqual(y), "a stepped carrier filled in odd chunks that straddle its steps is the same samples as one fill: phase-continuous, and each step at its sample");
        double Step(int i) => Math.IEEERemainder(Math.Atan2(x[2 * i + 3], x[2 * i + 2]) - Math.Atan2(x[2 * i + 1], x[2 * i]), 2 * Math.PI);
        double W(long k) => 2 * Math.PI * (plan.FrequencyHz[plan.IndexOf(k)] - plan.CenterHz) / plan.RateHz;
        bool exact = true;
        string why = "";
        for (long k = 1; plan.StartSample(k) + 2 < total; k++)
        {
            int b = (int)plan.StartSample(k);
            if (!(Math.Abs(Step(b - 1) - W(k - 1)) < 1e-3 && Math.Abs(Step(b) - W(k)) < 1e-3))
            {
                exact = false;
                why = $" (step {k} at sample {b}: {F(Step(b - 1), "0.0000")} then {F(Step(b), "0.0000")} rad)";
                break;
            }
        }
        Check(exact, $"in baseband the carrier turns by step k's frequency from sample floor(k x 5 ms x {F(plan.RateHz, "0")} S/s) on, and by step k-1's up to it{why}");
        var tuned = TxStepPlan.For(Sweep(100_000_000, 140_000_000, 5, 5), caps, TxRatePlan.ModelTickHz(2e6))!;
        var dc = new SteppedCarrier(tuned, -6);
        var z = new short[2 * 5000];
        dc.Fill(z, 5000);
        short a = (short)Math.Round(32767 * Math.Pow(10, -6 / 20.0));
        bool flat = true;
        for (int i = 0; i < 5000; i++) flat &= z[2 * i] == a && z[2 * i + 1] == 0;
        Check(flat, "a tuned sweep's samples are baseband 0 Hz throughout - the frequency writes move the carrier, and the samples say nothing about it");
    }

    // ---------------------------------------------------------------- the synthetic radio: AM / FM

    private static void SimModLoopback()
    {
        var (backend, server, log) = StartSimTx("mod");
        try
        {
            using var s = Connect(server, "mod");
            s.WaitReady(5000);
            Check(s.Radio.Source == RadioSources.Sim, "before anything is keyed, the radio says it is synthetic");
            s.ClaimTx();
            long center = 100_000_000 + 20 * OnBin, carrier = center + OnBin, af = 2 * OnBin;
            s.SetTx(new TxSettings { CenterHz = center, GainDb = 50, RateHz = 250_000 });
            var (pano, rows) = ProbeRows(s, carrier, carrier + af, carrier - af, center - OnBin, center + OnBin, center + 2 * OnBin, center + 3 * OnBin, center);
            Thread.Sleep(400);
            var caps = s.Caps.Tx!;

            var amReq = new TxStartRequest { Source = TxSourceKinds.Mod, MaxMs = 20_000, Modulation = TxWords.Am, AfHz = af, AmDepthPercent = 50, OffsetHz = OnBin, LevelDbfs = -10 };
            var am = s.StartTx(amReq);
            Check(am.Grant.Source == TxSourceKinds.Mod && am.Grant.RateHz == TxRules.RateFor(amReq, caps) && am.Grant.RateHz == 200e3 && am.Grant.CenterHz == carrier,
                  $"AM runs at the rate TxRules.RateFor picks ({F(am.Grant.RateHz, "0")} S/s, the lowest holding its carrier and +-af), its carrier at {am.Grant.CenterHz} Hz");
            Thread.Sleep(800);
            var last = Snapshot(rows)[^1];
            double level = -10 + 50 + SimBackend.DefaultTxCouplingDb, side = level + 20 * Math.Log10(0.5 / 2);
            Check(Math.Abs(last.levels[0] - level) < 0.3 && Math.Abs(last.levels[1] - side) < 0.3 && Math.Abs(last.levels[2] - side) < 0.3,
                  $"heard in the panorama: the carrier at {F(last.levels[0])} dBFS (L + G + coupling = {F(level)}), the sidebands at +-af " +
                  $"{F(last.levels[1])} / {F(last.levels[2])} (L + 20 log10(m / 2) = {F(side)})");
            var ra = am.Stop(drain: true);
            Check(ra.Acked && ra.Reason == EndReasons.Drained && s.GetState().Tx!.RateHz == 250_000,
                  $"drained on the radio's word, and the client's 250 kS/s is put back: {s.GetState().Tx!.RateHz} S/s");

            // FM at beta = 2.405 on the TX centre: the carrier nulls, the sidebands stand at J1 and J2.
            double beta = 2.405;
            var fmReq = new TxStartRequest { Source = TxSourceKinds.Mod, MaxMs = 20_000, Modulation = TxWords.Fm, AfHz = OnBin, FmDeviationHz = beta * OnBin, OffsetHz = 0, LevelDbfs = -10 };
            var fm = s.StartTx(fmReq);
            Check(fm.Grant.RateHz == TxRules.RateFor(fmReq, caps) && fm.Grant.RateHz == 200e3,
                  $"FM at +-{F(beta * OnBin, "0")} Hz, af {OnBin} Hz runs at the rate RateFor picks: {F(fm.Grant.RateHz, "0")} S/s");
            Thread.Sleep(800);
            last = Snapshot(rows)[^1];
            double j1 = level + 20 * Math.Log10(BesselJ(1, beta)), j2 = level + 20 * Math.Log10(BesselJ(2, beta)), j3 = level + 20 * Math.Log10(BesselJ(3, beta));
            Check(last.levels[7] < level - 30,
                  $"FM at beta = 2.405 nulls its carrier in the synthetic radio's own panorama: {F(last.levels[7])} dBFS at {center} Hz, where the carrier alone stands at {F(level)}");
            Check(Math.Abs(last.levels[3] - j1) < 0.3 && Math.Abs(last.levels[4] - j1) < 0.3 && Math.Abs(last.levels[5] - j2) < 0.3 && Math.Abs(last.levels[6] - j3) < 0.3,
                  $"...and its sidebands at J1 {F(last.levels[3])} / {F(last.levels[4])} (wants {F(j1)}), J2 {F(last.levels[5])} (wants {F(j2)}), J3 {F(last.levels[6])} (wants {F(j3)})");
            fm.Stop(drain: false);

            var wideFm = new TxStartRequest { Source = TxSourceKinds.Mod, MaxMs = 5_000, Modulation = TxWords.Fm, AfHz = 15_000, FmDeviationHz = 75_000, LevelDbfs = -10 };
            var wf = s.StartTx(wideFm);
            Check(wf.Grant.RateHz == 250_000 && wf.Grant.RateHz == TxRules.RateFor(wideFm, caps),
                  $"a broadcast-width FM (75 kHz deviation, 15 kHz af: Carson +-90 kHz) runs at {F(wf.Grant.RateHz, "0")} S/s, the lowest that holds it");
            wf.Stop(drain: false);
            pano.Dispose();
        }
        finally
        {
            server.Dispose();
            backend.Dispose();
            log.Dispose();
        }
    }

    // ---------------------------------------------------------------- the synthetic radio: the stepped sweep

    private static List<JsonObject> Steps(List<(string name, JsonObject body)> events)
    {
        lock (events) return events.Where(e => e.name == EventNames.TxStep).Select(e => e.body).ToList();
    }

    /// <summary>A baseband sweep through the real server: its grant, its steps in order at the formula's
    /// frequencies, each heard where its event said, its end said on the radio's word, and the client's centre
    /// and rate put back. Then a park of a stepped source is refused.</summary>
    private static void SimStepLoopback()
    {
        var (backend, server, log) = StartSimTx("step");
        try
        {
            using var s = Connect(server, "step");
            using var watcher = Connect(server, "watcher");
            s.WaitReady(5000);
            Check(s.Radio.Source == RadioSources.Sim, "before anything is keyed, the radio says it is synthetic");
            s.ClaimTx();
            long configured = 100_000_000 + 13 * OnBin;
            s.SetTx(new TxSettings { CenterHz = configured, GainDb = 50, RateHz = 250_000 });
            long c = 100_000_000 + 20 * OnBin, start = c - 8 * OnBin, stop = c + 8 * OnBin;
            var req = Sweep(start, stop, 5, 400, bounce: true);
            long[] freqs = Enumerable.Range(0, 5).Select(i => TxStartRequest.StepFrequencyHz(start, stop, 5, TxWords.Lin, i)).ToArray();
            var (pano, rows) = ProbeRows(s, freqs);
            var events = TxEvents(watcher, out var ev);
            Thread.Sleep(400);

            var t = s.StartTx(req);
            Check(t.Grant.Source == TxSourceKinds.Step && t.Grant.RateHz == 400_000 && t.Grant.LoHz == c && t.Grant.CenterHz == start &&
                  t.Grant.StartHz == start && t.Grant.StopHz == stop && t.Grant.Points == 5,
                  $"the grant: {F(t.Grant.RateHz, "0")} S/s (the lowest rate whose usable band holds the 250 kHz sweep), the LO parked at its centre " +
                  $"{t.Grant.LoHz} Hz, the carrier at its start {t.Grant.CenterHz} Hz, start/stop/points {t.Grant.StartHz}/{t.Grant.StopHz}/{t.Grant.Points}");
            var during = s.GetState().Tx!;
            Check(during.Keyed && during.Source == TxSourceKinds.Step && during.LoHz == c,
                  $"state.tx while it runs: keyed, source {during.Source}, the LO at {during.LoHz} Hz");
            bool ended = WaitIdle(events, EndReasons.Drained, 6000);
            Thread.Sleep(300);
            var steps = Steps(events);
            var idx = steps.Select(b => b.GetLong("index")).ToArray();
            Check(ended && idx.SequenceEqual(new long[] { 0, 1, 2, 3, 4, 3, 2, 1, 0 }),
                  $"a bounce without repeat runs 0-4-0 and then ENDS by itself, drained: tx_step indices {string.Join(",", idx)}");
            Check(steps.All(b => b.GetLong("center_hz") == freqs[b.GetLong("index")] && b.GetString("retune") == TxWords.RetuneDsp && b.GetString("timing") == TxStepPlan.TimingSample),
                  "every step is at the formula's frequency (TxStartRequest.StepFrequencyHz), moved by the digital path (dsp), and says timing = sample");
            var ticks = steps.Select(b => b.GetLong("time_ticks")).ToArray();
            var gaps = ticks.Zip(ticks.Skip(1), (a, b) => b - a).ToArray();
            Check(gaps.Length == 8 && gaps.All(g => Math.Abs(g - 4_000_000) <= 1),
                  $"its instants are 400 ms apart on the synthetic radio's clock, to the tick: {string.Join(", ", gaps.Select(g => F(g / 1e4, "0.0000")))} ms");
            List<(string name, JsonObject body)> seen;
            lock (events) seen = events.ToList();
            var idle = seen.Last(e => e.name == EventNames.Tx && e.body.GetString("state") == TxStates.Idle).body;
            Check(idle.GetBool("acked") && idle.GetString("text", "").Contains("completed") && seen.FindLastIndex(e => e.name == EventNames.TxStep) < seen.IndexOf(seen.Last(e => e.name == EventNames.Tx)),
                  $"its end is said after its last step, acknowledged: \"{idle.GetString("text")}\"");
            Check(idle.GetLong("samples") == 9L * 400 * 400,
                  $"exactly the sweep went out: {idle.GetLong("samples")} samples (9 steps x 400 ms x 400 kS/s = 1440000)");

            // Heard where each event said: every row taken inside a dwell reads that step's carrier at
            // L + G + coupling, and the other four frequencies at the floor.
            double want = -10 + 50 + SimBackend.DefaultTxCouplingDb;
            var all = Snapshot(rows);
            int judged = 0, wrong = 0;
            string firstWrong = "";
            for (int k = 0; k < steps.Count; k++)
            {
                long from = ticks[k] + 1_500_000, to = ticks[k] + 4_000_000 - 200_000;
                int i = (int)steps[k].GetLong("index");
                foreach (var r in all.Where(r => r.h.TimeTicks >= from && r.h.TimeTicks <= to && r.h.Ffts > 0))
                {
                    judged++;
                    bool ok = Math.Abs(r.levels[i] - want) < 1 && r.levels.Where((v, j) => j != i).All(v => v < -70);
                    if (!ok && wrong++ == 0)
                        firstWrong = $" - row {r.h.Seq} in step {k} reads {string.Join(" / ", r.levels.Select(v => F(v, "0.0")))}";
                }
            }
            Check(judged >= 14 && wrong == 0,
                  $"each step's carrier is heard where its event said: {judged} rows inside the dwells, each reading its step at {F(want)} dBFS and the other four at the floor{firstWrong}");
            var after = s.GetState().Tx!;
            Check(!after.Keyed && after.CenterHz == configured && after.LoHz == configured && after.RateHz == 250_000,
                  $"after it the client's centre ({after.CenterHz} Hz, the LO {after.LoHz}) and rate ({F(after.RateHz, "0")} S/s) are put back");

            // A park of a stepped source is refused: it moves its own carrier.
            var t2 = s.StartTx(Sweep(start, stop, 5, 50, repeat: true));
            UsrpRequestException? refused = null;
            try { s.SetTx(new TxSettings { CenterHz = c + OnBin }); }
            catch (UsrpRequestException ex) { refused = ex; }
            Check(refused?.Code == ErrorCodes.TxActive && s.GetState().Tx!.Keyed,
                  $"a park of a stepped source is refused ({refused?.Code}: \"{refused?.Error.Text}\") and the sweep runs on");
            var r2 = t2.Stop(drain: false);
            Check(r2.Reason == EndReasons.Stopped, $"a repeating sweep runs until it is stopped: {r2}");
            ev.Dispose();
            pano.Dispose();
        }
        finally
        {
            server.Dispose();
            backend.Dispose();
            log.Dispose();
        }
    }

    /// <summary>
    /// The dwell measured against the synthetic radio's clock, from what it RECEIVED: a lease across the
    /// sweep, each sample's frequency read from its phase step, the index where each step's carrier takes
    /// over found to the sample. The spacing must be the dwell in lease samples exactly - a dwell counted in
    /// wall time would jitter by milliseconds, one counted in the generator's blocks by 512 lease samples.
    /// </summary>
    private static void StepDwellAgainstTheClock(string name, double wideRateHz, long rxLo, long leaseCenter, long start, long stop, int decimation,
                                                 double couplingDb, double gainDb, bool wantBaseband)
    {
        var (backend, server, log) = StartSimTx(name, rateHz: wideRateHz, couplingDb: couplingDb);
        try
        {
            using var s = Connect(server, name);
            s.WaitReady(5000);
            Check(s.Radio.Source == RadioSources.Sim, $"{name}: the radio is synthetic ({F(wideRateHz / 1e6, "0")} MS/s)");
            s.ClaimLo();
            if (s.GetState().LoHz != rxLo) s.SetLo(rxLo);
            s.ClaimTx();
            s.SetTx(new TxSettings { GainDb = gainDb });
            var events = TxEvents(s, out var ev);
            using var lease = s.OpenLease(new LeaseRequest { CenterHz = leaseCenter, Decimation = decimation });
            double leaseRate = lease.Grant.RateHz;
            var blocks = new List<(IqHeader h, byte[] b)>();
            lease.Connection.IqBlockReceived += (c, h, b) => { lock (blocks) blocks.Add((h, b)); };
            lease.Connection.StartReader();
            Thread.Sleep(300);
            const int dwellMs = 20;
            var req = Sweep(start, stop, 3, dwellMs, repeat: true);
            req.LevelDbfs = -3;
            var t = s.StartTx(req);
            Thread.Sleep(1200);
            t.Stop(drain: false);
            Thread.Sleep(200);
            ev.Dispose();
            List<(IqHeader h, byte[] b)> got;
            lock (blocks) got = blocks.ToList();
            bool contiguous = true;
            for (int i = 1; i < got.Count; i++) contiguous &= got[i].h.FirstSample == got[i - 1].h.FirstSample + (ulong)got[i - 1].h.Count;
            var steps = Steps(events);
            long[] stepHz = steps.Select(b => b.GetLong("center_hz")).Distinct().OrderBy(v => v).ToArray();
            Check(contiguous && steps.Count >= 40 && stepHz.Length == 3 && t.Grant.RateHz > 0,
                  $"{name}: {steps.Count} steps announced over the three points {string.Join(", ", stepHz)} Hz at {F(t.Grant.RateHz, "0")} S/s; " +
                  $"{got.Count} lease blocks at {F(leaseRate, "0")} S/s, contiguous");
            Check(steps.All(b => b.GetString("retune") == TxWords.RetuneDsp && b.GetString("timing") == TxStepPlan.TimingSample),
                  $"{name}: every step is the digital path's and says timing = sample (the synthetic radio owns its clock)");

            // Every sample's frequency, from its phase step.
            int total = got.Sum(g => g.h.Count);
            var iq = new short[2 * total];
            int at = 0;
            foreach (var (h, b) in got)
            {
                Buffer.BlockCopy(b, 0, iq, 2 * at * 2, h.Count * 4);
                at += h.Count;
            }
            double[] offsets = stepHz.Select(f => (double)(f - leaseCenter)).ToArray();
            int ClassAt(int n)
            {
                double re = (double)iq[2 * n + 2] * iq[2 * n] + (double)iq[2 * n + 3] * iq[2 * n + 1];
                double im = (double)iq[2 * n + 3] * iq[2 * n] - (double)iq[2 * n + 2] * iq[2 * n + 1];
                double f = Math.Atan2(im, re) * leaseRate / (2 * Math.PI);
                int best = -1;
                double bestErr = 0.1 * (offsets[1] - offsets[0]);
                for (int j = 0; j < 3; j++)
                    if (Math.Abs(f - offsets[j]) < bestErr) { best = j; bestErr = Math.Abs(f - offsets[j]); }
                return best;
            }
            // Runs of one class at least 200 samples long; a run's start is where its carrier took over.
            var runs = new List<(int cls, int from)>();
            int cur = -2, runFrom = 0, len = 0;
            for (int n = 0; n + 1 < total; n++)
            {
                int c = ClassAt(n);
                if (c == cur) { len++; continue; }
                if (cur >= 0 && len >= 200 && (runs.Count == 0 || runs[^1].cls != cur)) runs.Add((cur, runFrom));
                cur = c;
                runFrom = n;
                len = 1;
            }
            double perDwell = dwellMs / 1000.0 * leaseRate;
            // The first run begins at the key-up and the last is cut by the stop: judge the transitions between.
            // A step's carrier is judged to take over once its frequency has settled, which a 100 kHz jump
            // (2 -> 0) takes a few samples longer to do than a 50 kHz one: so a step is compared with the one
            // three before it - the same jump - to the sample, and with its neighbour loosely.
            var starts = runs.Skip(1).Select(r => r.from).ToArray();
            var sameJump = starts.Zip(starts.Skip(3), (a, b) => b - a).ToArray();
            var next = starts.Zip(starts.Skip(1), (a, b) => b - a).ToArray();
            var order = runs.Select(r => r.cls).ToArray();
            bool cyclic = order.Zip(order.Skip(1), (a, b) => b == (a + 1) % 3).All(v => v);
            double worst = sameJump.Length == 0 ? double.NaN : sameJump.Max(v => Math.Abs(v - 3 * perDwell));
            double loose = next.Length == 0 ? double.NaN : next.Max(v => Math.Abs(v - perDwell));
            Check(sameJump.Length >= 30 && worst <= 1 && loose <= 12 && cyclic,
                  $"{name}: measured in the samples the radio RECEIVED, each step's carrier takes over {F(3 * perDwell, "0")} lease samples " +
                  $"(3 x {dwellMs} ms) after the same step three before it, the worst {F(worst, "0")} samples off over {sameJump.Length}, and within " +
                  $"{F(loose, "0")} of {F(perDwell, "0")} after its neighbour - in the order 0, 1, 2, 0 ...");
            var r0 = t.Grant;
            string said = s.GetLog(200).LastOrDefault(l => l.Contains("transmission opened: step")) ?? "";
            Check(r0.LoHz == start + (stop - start) / 2 && r0.RateHz == 200e3 && said.Contains(wantBaseband ? "a baseband sweep" : "a tuned sweep"),
                  $"{name}: the sidecar ran it as a {(wantBaseband ? "baseband" : "tuned")} sweep, the LO parked at its centre {r0.LoHz} Hz, " +
                  $"{F(r0.RateHz, "0")} S/s: \"{said}\"");
        }
        finally
        {
            server.Dispose();
            backend.Dispose();
            log.Dispose();
        }
    }

    private static void SimStepDwellBaseband() =>
        // 100.5 MHz +-50 kHz: a baseband sweep at 200 kS/s, read by a 250 kS/s lease (D = 8 of 2 MS/s).
        StepDwellAgainstTheClock("dwell-baseband", 2e6, 100_000_000, 100_500_000, 100_450_000, 100_550_000, 8, SimBackend.DefaultTxCouplingDb, 60, wantBaseband: true);

    private static void SimStepDwellTuned() =>
        // 96.5 MHz +-1.25 MHz is wider than any rate's usable band: TUNED, the carrier moved by the synthetic
        // DUC at its exact sample, read by a 5 MS/s lease (D = 2 of 10 MS/s) with the receiver retuned to its
        // centre - the scene's -20 dBFS carrier at 100.1 MHz is then outside the lease.
        StepDwellAgainstTheClock("dwell-tuned", 10e6, 96_500_000, 96_500_000, 95_250_000, 97_750_000, 2, -40, 40, wantBaseband: false);

    /// <summary>A sweep wider than the DUC's reach: the synthesizer moves where a step's window changes, and
    /// the DUC everywhere else - said on every step, and the LO in the state follows.</summary>
    private static void SimStepRetunes()
    {
        var (backend, server, log) = StartSimTx("retune");
        try
        {
            using var s = Connect(server, "retune");
            s.WaitReady(5000);
            s.ClaimTx();
            s.SetTx(new TxSettings { GainDb = 10 });
            var events = TxEvents(s, out var ev);
            var t = s.StartTx(Sweep(100_000_000, 140_000_000, 5, 100, bounce: true));
            Check(t.Grant.LoHz == 110_000_000 && t.Grant.CenterHz == 100_000_000 && t.Grant.RateHz == 200e3,
                  $"a 40 MHz sweep at a 32 MHz clock: the LO parked at its first window's centre, {t.Grant.LoHz} Hz, the carrier at {t.Grant.CenterHz} Hz, {F(t.Grant.RateHz, "0")} S/s");
            var los = new List<long>();
            var watch = Task.Run(() =>
            {
                for (int i = 0; i < 12; i++)
                {
                    los.Add(s.GetState().Tx!.LoHz);
                    Thread.Sleep(80);
                }
            });
            Check(WaitIdle(events, EndReasons.Drained, 4000), "it runs 0-4-0 and ends by itself");
            watch.Wait(3000);
            var steps = Steps(events);
            var words = steps.Select(b => b.GetString("retune")).ToArray();
            var want = new[] { "dsp", "dsp", "rf", "dsp", "dsp", "dsp", "dsp", "rf", "dsp" };
            Check(words.SequenceEqual(want) && steps.Select(b => b.GetLong("center_hz")).SequenceEqual(new long[] { 100, 110, 120, 130, 140, 130, 120, 110, 100 }.Select(v => v * 1_000_000)),
                  $"retune per step: {string.Join(" ", words)} - the synthesizer where the sweep crosses from the 110 MHz window to the 130 MHz one and back, the DUC otherwise");
            Check(los.Contains(110_000_000) && los.Contains(130_000_000),
                  $"state.tx's LO follows the windows: {string.Join(", ", los.Distinct().Select(v => F(v / 1e6, "0")))} MHz");
            ev.Dispose();
        }
        finally
        {
            server.Dispose();
            backend.Dispose();
            log.Dispose();
        }
    }

    /// <summary>The steps (and the end) that happen before anyone subscribes are handed to the first handler,
    /// in order: the server subscribes only when StartTx has returned, and a sweep's step 0 is its key-up.</summary>
    private static void StepEventsBeforeSubscribers()
    {
        var log = new SidecarLog(null, console: false);
        var backend = new SimBackend(log, "late", 2e6);
        backend.Start();
        try
        {
            backend.WaitReady(5000);
            var tr = backend.StartTx(Sweep(100_000_000, 100_100_000, 3, 5));
            Thread.Sleep(400);
            var got = new List<JsonObject>();
            StreamEnd? end = null;
            tr.Step += b => { lock (got) got.Add(b); };
            tr.Ended += e => end = e;
            Check(WaitFor(() => end != null, 2000) && end!.Reason == EndReasons.Drained,
                  $"a 15 ms sweep that ended before anyone listened still tells its first handler it ended: {end?.Reason}");
            List<JsonObject> steps;
            lock (got) steps = got.ToList();
            Check(steps.Select(b => b.GetLong("index")).SequenceEqual(new long[] { 0, 1, 2 }),
                  $"...and every step, in order: {string.Join(",", steps.Select(b => b.GetLong("index")))}");
            tr.Dispose();
        }
        finally
        {
            backend.Dispose();
            log.Dispose();
        }
    }

    /// <summary>Repeat and bounce as a client sees them, over the wire.</summary>
    private static void SimStepSequences()
    {
        var (backend, server, log) = StartSimTx("seq");
        try
        {
            using var s = Connect(server, "seq");
            s.WaitReady(5000);
            s.ClaimTx();
            foreach (var (bounce, want) in new[] { (false, new long[] { 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3 }), (true, new long[] { 0, 1, 2, 3, 2, 1, 0, 1, 2, 3, 2, 1 }) })
            {
                var events = TxEvents(s, out var ev);
                var t = s.StartTx(Sweep(100_000_000, 100_150_000, 4, 20, repeat: true, bounce: bounce));
                Check(WaitFor(() => Steps(events).Count >= 14, 3000), $"repeat{(bounce ? " + bounce" : "")}: steps keep coming");
                var r = t.Stop(drain: false);
                var steps = Steps(events);
                var idx = steps.Take(12).Select(b => b.GetLong("index")).ToArray();
                var gaps = steps.Zip(steps.Skip(1), (a, b) => b.GetLong("time_ticks") - a.GetLong("time_ticks")).ToArray();
                Check(idx.SequenceEqual(want) && gaps.All(g => Math.Abs(g - 200_000) <= 1) && r.Reason == EndReasons.Stopped,
                      $"repeat{(bounce ? " + bounce" : "")}: {string.Join(",", idx)} ... every step 20 ms after the last to the tick, until it is stopped");
                ev.Dispose();
            }
        }
        finally
        {
            server.Dispose();
            backend.Dispose();
            log.Dispose();
        }
    }

    /// <summary>
    /// What no running gate can reach - the UHD half never runs here - pinned in the source: a baseband step is
    /// announced with the counted instant and says sample; a tuned one is a write made after the previous step's
    /// samples were handed over, and says bound with the instant the write RETURNED; the sender never hands UHD
    /// a sample past the next step or the sweep's end; a completed sweep ends through the one end-of-burst and
    /// waits for the radio's word; the binding has no timed command, so nothing can claim one; and both radios
    /// state the generated sources through ONE helper.
    /// </summary>
    private static void TxGeneratedSourceRules()
    {
        string root = RepoRoot();
        string Src(params string[] p) => File.ReadAllText(Path.Combine(new[] { root, "src" }.Concat(p).ToArray()));
        string tx = Src("ShackLinkUsrp", "Radio", "UhdTransmission.cs");
        string? take = MemberBody(tx, "private void TakeStep()");
        Check(take != null && take.Contains("NoteStep(TicksOfStep(NextStep), TxStepPlan.TimingSample)") &&
              take.IndexOf("Backend.StepTune(", StringComparison.Ordinal) >= 0 &&
              take.IndexOf("Backend.StepTune(", StringComparison.Ordinal) < take.IndexOf("NoteStep(SidecarClock.Ticks, TxStepPlan.TimingBound", StringComparison.Ordinal),
              "UhdTransmission.TakeStep: a baseband step says sample at its counted instant; a tuned step is written first and then says bound, stamped when the write returned");
        string? loop = MemberBody(tx, "private void SenderLoop()");
        Check(loop != null && loop.Contains("Plan.StartSample(next) - handed") && loop.Contains("Plan.EndSample - handed") &&
              loop.Contains("handed >= Plan.StartSample(NextStep)") && loop.Contains("TakeStep();"),
              "UhdTransmission.SenderLoop never hands UHD a sample past the next step or the sweep's end, and takes the step once the samples before it are handed over");
        Check(loop != null && CountOf(loop, "SendEndOfBurst(") == 2 && loop.Contains("completed ? now0 + MsToTicks(CompletionAckMs)") &&
              loop.IndexOf("Finish(acked, text, null);", StringComparison.Ordinal) < loop.IndexOf("if (completed) EndedBySource(", StringComparison.Ordinal),
              "...a completed sweep ends through the same end-of-burst, waits for BURST_ACK, and says so only after the radio side has finished");
        string native = Src("ShackLinkUsrp.Uhd", "UhdNative.cs");
        Check(!native.Contains("set_command_time") && !native.Contains("clear_command_time"),
              "the UHD binding declares no timed command: a tuned step on UHD cannot claim a sample's timing (the timed path waits for proof on the board, 9.18 P4b2)");
        string ub = Src("ShackLinkUsrp", "Radio", "UhdBackend.cs");
        string? stepTune = MemberBody(ub, "protected override (long CenterHz, long LoHz) ApplyTxStepTune(");
        Check(stepTune != null && stepTune.Contains("UhdTxTunePolicy.LoOffset, loHz - carrierHz") && stepTune.Contains("UhdTxTunePolicy.KeepLo"),
              "UhdBackend.ApplyTxStepTune: the synthesizer to the window's LO (rf MANUAL + dsp AUTO) when it moves, the DUC alone (rf NONE) otherwise");
        int stated = 0;
        foreach (string f in Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            string text = File.ReadAllText(f);
            stated += CountOf(text, "Sources = new[]") + CountOf(text, "Modulations = new[]");
        }
        string sb = Src("ShackLinkUsrp", "Radio", "SimBackend.cs");
        Check(CountOf(ub, "StateGeneratedSources(") == 1 && CountOf(sb, "StateGeneratedSources(") == 1 && stated == 1,
              "both radios state their sources through RadioBackendBase.StateGeneratedSources, and nothing else in the sidecar lists them");
    }
}
