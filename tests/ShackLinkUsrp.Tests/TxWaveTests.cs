// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// S2 (ShackLink docs/usrp_b210_support.md section 9.21 Q5): AM and FM modulated by the protocol library's waves -
// square, triangle, sawtooth and two-tone as well as the sine. Every transmitting case runs on the synthetic radio,
// which transmits into itself. Each spectral line is checked against the theory computed HERE from the library's
// coefficients: AM's sidebands at L + 20 log10(m a_k / 2); a two-tone FM's lines at the Bessel products
// J_n(beta1) J_m(beta2); a square FM's phase against its exact integral, sample by sample.

using ShackLinkUsrp.Client;
using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Radio;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Tests;

internal static partial class Program
{
    /// <summary>A wave from the protocol library, as the sidecar hands it to a generator.</summary>
    private static ModComponent[] WaveOf(string name, double afHz, double af2Hz = 0) =>
        TxWaveforms.For(name, afHz, af2Hz, ProtocolConstants.TxModulatingHarmonics)!.Components
            .Select(c => new ModComponent(c.FrequencyHz, c.Amplitude, c.PhaseRad)).ToArray();

    /// <summary>The level in ONE bin of a panorama row - for on-bin lines closer than LevelAt's +-3 bins.</summary>
    private static double BinLevel(RowHeader h, short[] bins, double hz, double fs)
    {
        int i = h.Bins / 2 + (int)Math.Round((hz - h.CenterHz) * h.FftSize / fs);
        return i < 0 || i >= h.Bins ? double.NaN : bins[i] / 100.0;
    }

    // ---------------------------------------------------------------- DSP

    private static void ModWaveformCases()
    {
        const double rate = 200_000;
        const double bin = rate / 4096;             // 48.828125 Hz
        double offset = 400 * bin, af = 32 * bin;    // everything ON a bin: no scalloping
        const double level = -10, m = 0.5;
        const int K = ProtocolConstants.TxModulatingHarmonics;

        foreach (var name in new[] { TxWords.Square, TxWords.Triangle, TxWords.Sawtooth })
        {
            var a = TxWaveforms.Coefficients(name, K)!;
            var s = SpectrumOf(ModGenerator.Am(offset, rate, level, WaveOf(name, af), m), 2);
            double worst = 0;
            int lines = 0;
            bool gaps = true;
            for (int k = 1; k <= K; k++)
            {
                foreach (int side in new[] { -1, 1 })
                {
                    double got = s[2048 + 400 + side * 32 * k];
                    if (a[k] != 0)
                    {
                        worst = Math.Max(worst, Math.Abs(got - (level + 20 * Math.Log10(m * Math.Abs(a[k]) / 2))));
                        lines++;
                    }
                    else
                    {
                        gaps &= got < level - 90;
                    }
                }
            }
            Check(Math.Abs(s[2048 + 400] - level) < 0.05 && worst < 0.05 && gaps,
                  $"AM by a {name} at 50 %: the carrier at {F(s[2048 + 400])} dBFS, and all {lines} sideband lines at L + 20 log10(m a_k / 2) " +
                  $"to {F(worst, "0.000")} dB; {(name == TxWords.Sawtooth ? "no gaps (every harmonic)" : "nothing at the even harmonics")}");
            Check(s.Where((v, i) => Math.Abs(i - (2048 + 400)) > 32 * K + 2).All(v => v < level - 90),
                  $"...and nothing beyond its {K}th harmonic: the wave is cut there (section 9.21 Q5)");
        }

        double af2 = 90 * bin;
        var two = SpectrumOf(ModGenerator.Am(offset, rate, level, WaveOf(TxWords.TwoTone, af, af2), m), 2);
        double tw = level + 20 * Math.Log10(m * 0.5 / 2);
        Check(new[] { 32, -32, 90, -90 }.All(d => Math.Abs(two[2048 + 400 + d] - tw) < 0.05) && two[2048 + 400 + 58] < level - 90 && two[2048 + 400 + 122] < level - 90,
              $"AM by a two-tone: four lines at L + 20 log10(m / 4) = {F(tw)} ({F(two[2048 + 432])}, {F(two[2048 + 490])}), and none at the tones' difference or sum");

        // The peak: every wave peaks at 1, so AM's peak is L + 20 log10(1 + m) whatever the wave.
        foreach (var name in new[] { TxWords.Square, TxWords.Sawtooth, TxWords.Triangle })
        {
            var iq = new short[2 * 40_000];
            ModGenerator.Am(offset, rate, -6.03, WaveOf(name, 997), 1.0).Fill(iq, 40_000);
            double peak = 0;
            for (int i = 0; i < 40_000; i++) peak = Math.Max(peak, Math.Sqrt((double)iq[2 * i] * iq[2 * i] + (double)iq[2 * i + 1] * iq[2 * i + 1]));
            double want = ModGenerator.AmPeakDbfs(-6.03, 1.0), got = 20 * Math.Log10(peak / 32767);
            Check(got <= want + 0.001 && got > want - 0.05,
                  $"a {name} AM at 100 % and -6.03 dBFS peaks at {F(got, "0.000")} dBFS (L + 20 log10(1 + m) = {F(want, "0.000")}): never above full scale");
        }

        // FM by a square: the phase, sample by sample, against its exact integral computed here.
        {
            double dev = 5000, a2 = 997;
            var wave = WaveOf(TxWords.Square, a2);
            var g = ModGenerator.Fm(offset, rate, level, wave, dev);
            var iq = new short[2 * 50_000];
            g.Fill(iq, 50_000);
            double worst = 0, amp = 32767 * Math.Pow(10, level / 20.0);
            for (int n = 0; n < 50_000; n++)
            {
                double t = n / rate, psi = 2 * Math.PI * offset * t;
                foreach (var c in wave) psi += dev * c.Amplitude / c.FrequencyHz * (Math.Cos(c.PhaseRad) - Math.Cos(2 * Math.PI * c.FrequencyHz * t + c.PhaseRad));
                double er = iq[2 * n] / amp, ei = iq[2 * n + 1] / amp;
                double d = Math.Atan2(ei * Math.Cos(psi) - er * Math.Sin(psi), er * Math.Cos(psi) + ei * Math.Sin(psi));
                worst = Math.Max(worst, Math.Abs(d));
            }
            Check(worst < 3e-4,
                  $"FM by a square: 50,000 samples follow the exact integral of dev x(t) to {F(worst * 1e6, "0")} microradians (the sc16 rounding) - " +
                  "the instantaneous frequency IS the carrier plus dev times the wave");
        }

        // FM by a two-tone: lines at n f1 + m f2 standing at J_n(beta1) J_m(beta2), beta_i = (dev / 2) / f_i.
        {
            double f1 = 32 * bin, f2 = 90 * bin, dev = 2.4 * f1;
            double b1 = dev / 2 / f1, b2 = dev / 2 / f2;
            var fm = SpectrumOf(ModGenerator.Fm(0, rate, level, WaveOf(TxWords.TwoTone, f1, f2), dev), 2);
            double worst = 0;
            var said = new List<string>();
            foreach (var (n, mm) in new[] { (0, 0), (1, 0), (-1, 0), (2, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-2, 1) })
            {
                double want = level + 20 * Math.Log10(Math.Abs(BesselJ(Math.Abs(n), b1) * BesselJ(Math.Abs(mm), b2)));
                double got = fm[2048 + n * 32 + mm * 90];
                worst = Math.Max(worst, Math.Abs(got - want));
                said.Add($"({n},{mm}) {F(got)}/{F(want)}");
            }
            Check(worst < 0.05,
                  $"FM by a two-tone (beta {F(b1, "0.000")} and {F(b2, "0.000")}): its lines stand at the Bessel products J_n(b1) J_m(b2) to {F(worst, "0.000")} dB - {string.Join(", ", said)}");
        }

        foreach (var (name, make) in new (string, Func<ModGenerator>)[]
                 {
                     ("a square AM", () => ModGenerator.Am(offset, rate, -8, WaveOf(TxWords.Square, af), 0.4)),
                     ("a sawtooth FM", () => ModGenerator.Fm(offset, rate, -8, WaveOf(TxWords.Sawtooth, 997), 3000)),
                     ("a two-tone FM", () => ModGenerator.Fm(offset, rate, -8, WaveOf(TxWords.TwoTone, 700, 1900), 3000)),
                 })
        {
            var one = make();
            var ten = make();
            var x = new short[2 * 3000];
            var y = new short[2 * 3000];
            one.Fill(x, 3000);
            for (int k = 0; k < 10; k++) ten.Fill(y.AsSpan(k * 600), 300);
            Check(x.SequenceEqual(y), $"{name} is phase-continuous: ten calls of 300 are the same samples as one of 3000 (every component's phasor)");
        }
        Check(ModGenerator.Fm(0, rate, level, WaveOf(TxWords.Square, 1000), 1000).HalfBandwidthHz == 16_000 &&
              ModGenerator.Am(0, rate, level, WaveOf(TxWords.Square, 1000), 0.3).HalfBandwidthHz == 15_000,
              "a generator's own half-width is the wave's highest component (AM) and Carson's dev + it (FM)");
    }

    // ---------------------------------------------------------------- the synthetic radio

    private static void SimModWaveLoopback()
    {
        var (backend, server, log) = StartSimTx("wave");
        try
        {
            using var s = Connect(server, "wave");
            s.WaitReady(5000);
            Check(s.Radio.Source == RadioSources.Sim, "before anything is keyed, the radio says it is synthetic");
            var caps = s.Caps.Tx!;
            Check(caps.ModulatingWaveforms.SequenceEqual(TxWaveforms.All) && caps.ModulatingHarmonics == 15 &&
                  caps.OffersModulation(TxWords.Am) && caps.OffersModulation(TxWords.Fm),
                  $"caps.tx: the waves {string.Join(", ", caps.ModulatingWaveforms)} at {caps.ModulatingHarmonics} harmonics, for {string.Join(" and ", caps.Modulations)}");
            s.ClaimTx();
            const double pbin = 2e6 / 4096;             // the panorama's bin: 488.28125 Hz
            long center = 100_000_000 + 20 * OnBin, carrier = center + OnBin;
            double af = 4 * pbin;
            // 70 dB of gain puts the carrier at -20 dBFS, so the 15th harmonic's sidebands stand ~45 dB over the
            // synthetic receiver's floor (~-99 dBFS) and the empty even harmonics can be told from it.
            s.SetTx(new TxSettings { CenterHz = center, GainDb = 70, RateHz = 250_000 });
            var rows = new List<(RowHeader h, short[] bins)>();
            var pano = s.OpenPanorama(new PanoramaRequest { Bins = 4096, Fps = 10, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
            pano.Connection.RowReceived += (c, h, bins) => { lock (rows) rows.Add((h, (short[])bins.Clone())); };
            pano.Connection.StartReader();
            Thread.Sleep(400);
            double level = -10 + 70 + SimBackend.DefaultTxCouplingDb;
            (RowHeader h, short[] b) Last() { lock (rows) return rows[^1]; }

            var amReq = new TxStartRequest
            {
                Source = TxSourceKinds.Mod, MaxMs = 20_000, Modulation = TxWords.Am, AfHz = af, AmDepthPercent = 50,
                ModulatingWaveform = TxWords.Square, OffsetHz = OnBin, LevelDbfs = -10,
            };
            var am = s.StartTx(amReq);
            var sig = TxRules.Describe(amReq, caps)!;
            Check(am.Grant.RateHz == TxRules.RateFor(amReq, caps) && am.Grant.RateHz == 200e3 && am.Grant.CenterHz == carrier &&
                  am.Grant.PeakDbfs == sig.PeakDbfs && am.Grant.MeanDbfs == sig.MeanDbfs && am.Grant.BandHighHz == 15 * af,
                  $"a square AM runs at the rate RateFor picks ({F(am.Grant.RateHz, "0")} S/s: its 15th harmonic at {F(15 * af, "0")} Hz fits 200 k's 80 kHz), " +
                  $"and its grant states peak {F(am.Grant.PeakDbfs, "0.000")}, mean {F(am.Grant.MeanDbfs, "0.000")}, +-{F(am.Grant.BandHighHz, "0")} Hz");
            Thread.Sleep(800);
            var (h, b) = Last();
            var a = TxRules.Describe(amReq, caps)!.Waveform!;
            double worst = 0;
            var said = new List<string>();
            foreach (int k in new[] { 1, 3, 5, 7, 15 })
                foreach (int side in new[] { -1, 1 })
                {
                    double want = level + 20 * Math.Log10(0.5 * a.Component((k - 1) / 2).Amplitude / 2);
                    double got = BinLevel(h, b, carrier + side * k * af, 2e6);
                    worst = Math.Max(worst, Math.Abs(got - want));
                    if (side > 0) said.Add($"{k}: {F(got)}/{F(want)}");
                }
            double even = Math.Max(BinLevel(h, b, carrier + 2 * af, 2e6), BinLevel(h, b, carrier - 4 * af, 2e6));
            Check(Math.Abs(BinLevel(h, b, carrier, 2e6) - level) < 0.05 && worst < 0.05 && even < level - 60,
                  $"heard in the synthetic radio's own panorama: the carrier at {F(BinLevel(h, b, carrier, 2e6))} dBFS (wants {F(level)}), the odd harmonics' sidebands " +
                  $"at L + 20 log10(m a_k / 2) to {F(worst, "0.000")} dB ({string.Join(", ", said)}), the even ones at the floor ({F(even)})");
            am.Stop(drain: false);

            // FM by a two-tone: the Bessel products, in the panorama. The tones are 4 and 10 bins: every product then
            // lies on an EVEN bin, so no line sits in its neighbour's Hann leakage (at +-1 bin, half the amplitude,
            // adding coherently) - with 4 and 11 the products fill every bin and read up to 0.9 dB off.
            double f1 = 4 * pbin, f2 = 10 * pbin, dev = 2.4 * f1;
            double b1 = dev / 2 / f1, b2 = dev / 2 / f2;
            var fmReq = new TxStartRequest
            {
                Source = TxSourceKinds.Mod, MaxMs = 20_000, Modulation = TxWords.Fm, AfHz = f1, Af2Hz = f2, ModulatingWaveform = TxWords.TwoTone,
                FmDeviationHz = dev, OffsetHz = OnBin, LevelDbfs = -10,
            };
            var fm = s.StartTx(fmReq);
            Check(fm.Grant.RateHz == TxRules.RateFor(fmReq, caps) && Math.Abs(fm.Grant.BandHighHz - (dev + f2)) < 1e-6,
                  $"a two-tone FM runs at {F(fm.Grant.RateHz, "0")} S/s, its Carson band +-{F(fm.Grant.BandHighHz, "0")} Hz (dev + the higher tone)");
            Thread.Sleep(800);
            (h, b) = Last();
            worst = 0;
            said.Clear();
            foreach (var (n, mm) in new[] { (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1) })
            {
                double want = level + 20 * Math.Log10(Math.Abs(BesselJ(Math.Abs(n), b1) * BesselJ(Math.Abs(mm), b2)));
                double got = BinLevel(h, b, carrier + n * f1 + mm * f2, 2e6);
                worst = Math.Max(worst, Math.Abs(got - want));
                said.Add($"({n},{mm}) {F(got)}/{F(want)}");
            }
            Check(worst < 0.05,
                  $"...and its lines stand at J_n({F(b1, "0.000")}) J_m({F(b2, "0.000")}) to {F(worst, "0.000")} dB in the synthetic radio's own panorama: {string.Join(", ", said)}");
            fm.Stop(drain: false);

            var tooFast = new TxStartRequest
            {
                Source = TxSourceKinds.Mod, MaxMs = 5000, Modulation = TxWords.Am, AfHz = 60_000, AmDepthPercent = 30,
                ModulatingWaveform = TxWords.Square, LevelDbfs = -10,
            };
            try
            {
                s.StartTx(tooFast);
                Check(false, "a square AM at 60 kHz should have been refused");
            }
            catch (UsrpRequestException ex)
            {
                Check(ex.Code == ErrorCodes.OutOfRange && ex.Error.Text.Contains("900000 Hz"),
                      $"a square AM whose 15th harmonic no rate holds is refused, never cut shorter: {ex.Error.Text}");
            }
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
