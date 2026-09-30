// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// S3 (ShackLink docs/usrp_b210_support.md section 9.21 Q1-Q3): PM, DSB and SSB. The level of a suppressed-carrier
// signal is its PEAK (PEP); an SSB is generated exactly, with no Hilbert filter, so its opposite sideband and its
// carrier are the sc16 rounding's; a PM of beta radians stands at the same Bessel lines as an FM of index beta.
// Every transmitting case runs on the synthetic radio, which transmits into itself.

using ShackLinkUsrp.Client;
using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Radio;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Tests;

internal static partial class Program
{
    private static double AnalyticPeakOf(string name, double af, double af2 = 0) =>
        TxWaveforms.For(name, af, af2, ProtocolConstants.TxModulatingHarmonics)!.AnalyticPeak;

    private static double PeakEnvelopeDbfs(IIqGenerator g, int count)
    {
        var iq = new short[2 * count];
        g.Fill(iq, count);
        double peak = 0;
        for (int i = 0; i < count; i++) peak = Math.Max(peak, Math.Sqrt((double)iq[2 * i] * iq[2 * i] + (double)iq[2 * i + 1] * iq[2 * i + 1]));
        return 20 * Math.Log10(peak / 32767);
    }

    // ---------------------------------------------------------------- DSP

    private static void SidebandPmCases()
    {
        const double rate = 200_000;
        const double bin = rate / 4096;             // 48.828125 Hz
        double offset = 400 * bin, af = 32 * bin, af2 = 90 * bin;
        const double level = -10;
        const int C = 2048 + 400;                   // the carrier's bin

        // DSB: A x(t), the carrier suppressed; the level is the PEAK (Q1).
        var dsb = SpectrumOf(ModGenerator.Dsb(offset, rate, level, WaveOf(TxWords.Sine, af)), 2);
        double pair = level + 20 * Math.Log10(0.5);
        Check(Math.Abs(dsb[C + 32] - pair) < 0.05 && Math.Abs(dsb[C - 32] - pair) < 0.05 && dsb[C] < level - 100,
              $"a DSB sine: two lines at L - 6.02 = {F(pair)} ({F(dsb[C + 32])} / {F(dsb[C - 32])}), the carrier suppressed to {F(dsb[C])} dBFS");
        double dsbPeak = PeakEnvelopeDbfs(ModGenerator.Dsb(offset, rate, level, WaveOf(TxWords.Sine, af)), 20_000);
        Check(dsbPeak <= level + 0.001 && dsbPeak > level - 0.01, $"...and its envelope PEAKS at its level: {F(dsbPeak, "0.000")} dBFS (PEP, section 9.21 Q1)");
        var sqa = TxWaveforms.Coefficients(TxWords.Square, ProtocolConstants.TxModulatingHarmonics)!;
        var dsq = SpectrumOf(ModGenerator.Dsb(offset, rate, level, WaveOf(TxWords.Square, af)), 2);
        double worst = 0;
        for (int k = 1; k <= 15; k += 2)
            foreach (int side in new[] { -1, 1 })
                worst = Math.Max(worst, Math.Abs(dsq[C + side * 32 * k] - (level + 20 * Math.Log10(sqa[k] / 2))));
        Check(worst < 0.05 && dsq[C] < level - 100, $"a DSB square: every odd harmonic's pair at L + 20 log10(a_k / 2) to {F(worst, "0.000")} dB, the carrier suppressed");

        // SSB, generated exactly (Q2): one line for a sine, nothing at its mirror and nothing at the carrier. The
        // opposite sideband is the MIRROR line; the rest of that side holds only the sc16 rounding's spurs, which an
        // on-bin (strictly periodic) test tone bunches into a few lines rather than spreading as noise - bounded apart.
        foreach (bool upper in new[] { true, false })
        {
            string name = upper ? "USB" : "LSB";
            int sign = upper ? 1 : -1;
            var ssb = SpectrumOf(ModGenerator.Ssb(offset, rate, level, WaveOf(TxWords.Sine, af), 1.0, upper), 2);
            double mirror = ssb[C - sign * 32], spur = ssb.Where((v, i) => sign * (i - C) < 0).Max();
            Check(Math.Abs(ssb[C + sign * 32] - level) < 0.05 && mirror < level - 100 && ssb[C] < level - 100 && spur < level - 90,
                  $"a single-tone {name}: ONE line at its level ({F(ssb[C + sign * 32])} dBFS) {(upper ? "above" : "below")} the carrier; its mirror " +
                  $"{F(mirror - level)} dBc, the carrier {F(ssb[C] - level)} dBc - no Hilbert filter; the worst rounding spur on that side {F(spur - level)} dBc");
        }
        var two = SpectrumOf(ModGenerator.Ssb(offset, rate, level, WaveOf(TxWords.TwoTone, af, af2), AnalyticPeakOf(TxWords.TwoTone, af, af2), true), 2);
        double twoMirror = Math.Max(two[C - 32], two[C - 90]), twoSpur = two.Where((v, i) => i < C).Max();
        Check(Math.Abs(two[C + 32] - pair) < 0.05 && Math.Abs(two[C + 90] - pair) < 0.05 && twoMirror < level - 100 && twoSpur < level - 90,
              $"a two-tone USB: each tone at L - 6.02 ({F(two[C + 32])}, {F(two[C + 90])}), their mirrors no higher than {F(twoMirror - level)} dBc " +
              $"(the worst rounding spur below the carrier {F(twoSpur - level)} dBc)");
        double twoPeak = PeakEnvelopeDbfs(ModGenerator.Ssb(offset, rate, level, WaveOf(TxWords.TwoTone, af, af2), AnalyticPeakOf(TxWords.TwoTone, af, af2), true), 20_000);
        Check(twoPeak <= level + 0.001 && twoPeak > level - 0.01, $"...and its envelope peaks at its level, {F(twoPeak, "0.000")} dBFS, every beat: the two-tone's PEP");
        double pa = AnalyticPeakOf(TxWords.Square, af);
        var usq = SpectrumOf(ModGenerator.Ssb(offset, rate, level, WaveOf(TxWords.Square, af), pa, true), 2);
        worst = 0;
        for (int k = 1; k <= 15; k += 2) worst = Math.Max(worst, Math.Abs(usq[C + 32 * k] - (level + 20 * Math.Log10(sqa[k] / pa))));
        double usqMirror = Enumerable.Range(0, 8).Max(i => usq[C - 32 * (2 * i + 1)]), usqSpur = usq.Where((v, i) => i < C).Max();
        double usqPeak = PeakEnvelopeDbfs(ModGenerator.Ssb(offset, rate, level, WaveOf(TxWords.Square, af), pa, true), 20_000);
        Check(worst < 0.05 && usqMirror < level - 100 && usqSpur < level - 90 && usqPeak <= level + 0.001,
              $"a square USB: every odd harmonic at L + 20 log10(a_k / P_a) to {F(worst, "0.000")} dB on ONE side, the eight mirrors no higher than " +
              $"{F(usqMirror - level)} dBc (the worst rounding spur on that side {F(usqSpur - level)} dBc), its envelope peaking at {F(usqPeak, "0.000")} dBFS: " +
              $"scaled by its analytic peak {F(pa, "0.0000")}, so the level is the peak");

        // PM (Q3): beta radians of a sine stands at the Bessel lines; a square turns the phase by beta x(t), sample by sample.
        double beta = 2.405;
        var pm = SpectrumOf(ModGenerator.Pm(offset, rate, level, WaveOf(TxWords.Sine, af), beta, beta * af), 2);
        double j1 = level + 20 * Math.Log10(BesselJ(1, beta)), j2 = level + 20 * Math.Log10(BesselJ(2, beta)), j3 = level + 20 * Math.Log10(BesselJ(3, beta));
        Check(pm[C] < level - 60 && Math.Abs(pm[C + 32] - j1) < 0.05 && Math.Abs(pm[C - 32] - j1) < 0.05 && Math.Abs(pm[C + 64] - j2) < 0.05 && Math.Abs(pm[C - 96] - j3) < 0.05,
              $"a PM of beta = 2.405 rad nulls its carrier ({F(pm[C])} dBFS) and puts J1 {F(pm[C + 32])} (wants {F(j1)}), J2 {F(pm[C + 64])} (wants {F(j2)}), J3 {F(pm[C - 96])} (wants {F(j3)})");
        {
            double b = Math.PI / 2, a2 = 997;
            var wave = WaveOf(TxWords.Square, a2);
            var g = ModGenerator.Pm(offset, rate, level, wave, b, b * TxWaveforms.For(TxWords.Square, a2, 0, 15)!.PeakSlopeHz);
            var iq = new short[2 * 50_000];
            g.Fill(iq, 50_000);
            double w = 0, amp = 32767 * Math.Pow(10, level / 20.0);
            for (int n = 0; n < 50_000; n++)
            {
                double t = n / rate, x = 0;
                foreach (var c in wave) x += c.Amplitude * Math.Sin(2 * Math.PI * c.FrequencyHz * t + c.PhaseRad);
                double psi = 2 * Math.PI * offset * t + b * x;
                double er = iq[2 * n] / amp, ei = iq[2 * n + 1] / amp;
                w = Math.Max(w, Math.Abs(Math.Atan2(ei * Math.Cos(psi) - er * Math.Sin(psi), er * Math.Cos(psi) + ei * Math.Sin(psi))));
            }
            Check(w < 3e-4, $"a square PM of pi/2: 50,000 samples turned by beta x(t) to {F(w * 1e6, "0")} microradians - the phase IS beta times the wave");
        }

        foreach (var (name, make) in new (string, Func<ModGenerator>)[]
                 {
                     ("a square DSB", () => ModGenerator.Dsb(offset, rate, -8, WaveOf(TxWords.Square, af))),
                     ("a two-tone LSB", () => ModGenerator.Ssb(offset, rate, -8, WaveOf(TxWords.TwoTone, 700, 1900), 1.0, false)),
                     ("a sawtooth PM", () => ModGenerator.Pm(offset, rate, -8, WaveOf(TxWords.Sawtooth, 997), 1.0, 1.0 * 997 * 8.56)),
                 })
        {
            var one = make();
            var ten = make();
            var x = new short[2 * 3000];
            var y = new short[2 * 3000];
            one.Fill(x, 3000);
            for (int k = 0; k < 10; k++) ten.Fill(y.AsSpan(k * 600), 300);
            Check(x.SequenceEqual(y), $"{name} is phase-continuous: ten calls of 300 are the same samples as one of 3000");
        }
        Check(Throws(() => ModGenerator.Dsb(offset, rate, 0.5, WaveOf(TxWords.Sine, af))) && !Throws(() => ModGenerator.Dsb(offset, rate, 0, WaveOf(TxWords.Sine, af))) &&
              Throws(() => ModGenerator.Ssb(offset, rate, 0.5, WaveOf(TxWords.Sine, af), 1, true)) &&
              Throws(() => ModGenerator.Ssb(offset, rate, -3, WaveOf(TxWords.Sine, af), 0, true)) &&
              Throws(() => ModGenerator.Pm(0, rate, -3, WaveOf(TxWords.Sine, 20_000), 5, 100_000)),
              "refused, never clipped: a DSB or SSB above 0 dBFS (0 fits: the level is the peak), an SSB with no analytic peak, a PM whose Carson band leaves Nyquist");
    }

    // ---------------------------------------------------------------- the synthetic radio

    private static void SimSidebandPmLoopback()
    {
        var (backend, server, log) = StartSimTx("sideband");
        try
        {
            using var s = Connect(server, "sideband");
            s.WaitReady(5000);
            Check(s.Radio.Source == RadioSources.Sim, "before anything is keyed, the radio says it is synthetic");
            var caps = s.Caps.Tx!;
            Check(caps.Modulations.SequenceEqual(new[] { TxWords.Am, TxWords.Fm, TxWords.Pm, TxWords.Dsb, TxWords.Ssb }),
                  $"caps.tx modulates {string.Join(", ", caps.Modulations)}");
            s.ClaimTx();
            const double pbin = 2e6 / 4096;
            long center = 100_000_000 + 20 * OnBin, carrier = center + OnBin;
            double af = 4 * pbin, af2 = 10 * pbin;
            // 80 dB of gain: the carrier -10 dBFS in the synthetic receiver, ~89 dB over its floor, so -80 dBc can be SEEN.
            s.SetTx(new TxSettings { CenterHz = center, GainDb = 80, RateHz = 250_000 });
            var rows = new List<(RowHeader h, short[] bins)>();
            var pano = s.OpenPanorama(new PanoramaRequest { Bins = 4096, Fps = 10, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
            pano.Connection.RowReceived += (c, h, bins) => { lock (rows) rows.Add((h, (short[])bins.Clone())); };
            pano.Connection.StartReader();
            Thread.Sleep(400);
            double level = -10 + 80 + SimBackend.DefaultTxCouplingDb;
            (RowHeader h, short[] b) Last() { lock (rows) return rows[^1]; }
            double At((RowHeader h, short[] b) r, double hz) => BinLevel(r.h, r.b, hz, 2e6);
            TxStartRequest Req(string modulation) => new()
            {
                Source = TxSourceKinds.Mod, MaxMs = 20_000, Modulation = modulation, AfHz = af, OffsetHz = OnBin, LevelDbfs = -10,
            };

            var dsbReq = Req(TxWords.Dsb);
            var dsb = s.StartTx(dsbReq);
            Check(dsb.Grant.RateHz == TxRules.RateFor(dsbReq, caps) && dsb.Grant.PeakDbfs == -10 && Math.Abs(dsb.Grant.MeanDbfs + 13.0103) < 1e-3 &&
                  dsb.Grant.BandLowHz == -af && dsb.Grant.BandHighHz == af,
                  $"a DSB's grant: {F(dsb.Grant.RateHz, "0")} S/s, peak {F(dsb.Grant.PeakDbfs)} (its level), mean {F(dsb.Grant.MeanDbfs, "0.00")}, +-af");
            Thread.Sleep(800);
            var r = Last();
            double pair = level - 6.0206;
            Check(Math.Abs(At(r, carrier + af) - pair) < 0.05 && Math.Abs(At(r, carrier - af) - pair) < 0.05 && At(r, carrier) < level - 80,
                  $"heard in the synthetic radio's own panorama: the pair at {F(At(r, carrier + af))} / {F(At(r, carrier - af))} (L - 6.02 = {F(pair)}), " +
                  $"the carrier at {F(At(r, carrier))} dBFS, {F(At(r, carrier) - level)} dBc");
            dsb.Stop(drain: false);

            foreach (string sb in new[] { TxWords.Usb, TxWords.Lsb })
            {
                var req = Req(TxWords.Ssb);
                req.Sideband = sb;
                var t = s.StartTx(req);
                int sign = sb == TxWords.Usb ? 1 : -1;
                Check(t.Grant.PeakDbfs == -10 && t.Grant.MeanDbfs == -10 && t.Grant.BandLowHz == (sign > 0 ? af : -af) && t.Grant.BandHighHz == (sign > 0 ? af : -af),
                      $"a single-tone {sb.ToUpperInvariant()}'s grant: peak = mean = its level, its band the one line at {F(sign * af, "0")} Hz");
                Thread.Sleep(800);
                r = Last();
                double line = At(r, carrier + sign * af), other = At(r, carrier - sign * af), car = At(r, carrier);
                Check(Math.Abs(line - level) < 0.05 && other < level - 80 && car < level - 80,
                      $"heard: ONE line at {F(line)} dBFS (its level, {F(level)}), {(sign > 0 ? "above" : "below")} the carrier; the other side {F(other - level)} dBc and the carrier {F(car - level)} dBc");
                t.Stop(drain: false);
            }

            var twoReq = Req(TxWords.Ssb);
            twoReq.Sideband = TxWords.Usb;
            twoReq.ModulatingWaveform = TxWords.TwoTone;
            twoReq.Af2Hz = af2;
            var two = s.StartTx(twoReq);
            Check(two.Grant.BandLowHz == af && two.Grant.BandHighHz == af2 && Math.Abs(two.Grant.MeanDbfs + 13.0103) < 1e-3,
                  $"a two-tone USB's grant: {F(af, "0")}-{F(af2, "0")} Hz one-sided, mean L - 3.01");
            Thread.Sleep(800);
            r = Last();
            double worstOther = new[] { af, af2 }.Max(f => At(r, carrier - f));
            Check(Math.Abs(At(r, carrier + af) - pair) < 0.05 && Math.Abs(At(r, carrier + af2) - pair) < 0.05 && worstOther < level - 80,
                  $"heard: the two tones at {F(At(r, carrier + af))} / {F(At(r, carrier + af2))} (L - 6.02 = {F(pair)}), their images below the carrier {F(worstOther - level)} dBc");
            two.Stop(drain: false);

            double beta = 2.405;
            var pmReq = Req(TxWords.Pm);
            pmReq.PmDeviationDeg = beta * 180 / Math.PI;
            var pm = s.StartTx(pmReq);
            Check(pm.Grant.RateHz == TxRules.RateFor(pmReq, caps) && Math.Abs(pm.Grant.BandHighHz - (beta + 1) * af) < 1e-6,
                  $"a PM of {F(pmReq.PmDeviationDeg, "0.00")} degrees: {F(pm.Grant.RateHz, "0")} S/s, its Carson band +-{F(pm.Grant.BandHighHz, "0")} Hz = (beta + 1) af");
            Thread.Sleep(800);
            r = Last();
            double j1 = level + 20 * Math.Log10(BesselJ(1, beta)), j2 = level + 20 * Math.Log10(BesselJ(2, beta)), j3 = level + 20 * Math.Log10(BesselJ(3, beta));
            double pw = new[] { Math.Abs(At(r, carrier + af) - j1), Math.Abs(At(r, carrier - af) - j1), Math.Abs(At(r, carrier + 2 * af) - j2), Math.Abs(At(r, carrier - 3 * af) - j3) }.Max();
            Check(At(r, carrier) < level - 30 && pw < 0.05,
                  $"heard: the carrier nulled ({F(At(r, carrier))} dBFS) and the Bessel lines to {F(pw, "0.000")} dB - J1 {F(At(r, carrier + af))} (wants {F(j1)}), " +
                  $"J2 {F(At(r, carrier + 2 * af))} (wants {F(j2)}), J3 {F(At(r, carrier - 3 * af))} (wants {F(j3)})");
            pm.Stop(drain: false);
            pano.Dispose();
        }
        finally
        {
            server.Dispose();
            backend.Dispose();
            log.Dispose();
        }
    }
}
