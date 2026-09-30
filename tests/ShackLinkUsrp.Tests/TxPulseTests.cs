// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// S4 (ShackLink docs/usrp_b210_support.md section 9.21 Q4): the pulse, generated in the samples as ONE burst. Its
// envelope is the protocol library's TxPulse.Envelope evaluated at each sample's instant, so its width and period
// are exact, never rounded to samples. Its lines stand at the envelope's Fourier coefficients, computed HERE by
// integrating that envelope; and the synthetic radio, which transmits into itself, is read through a lease: the
// width, period and 10-90 % rise measured in what it received.

using ShackLinkUsrp.Client;
using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Radio;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Tests;

internal static partial class Program
{
    /// <summary>|c_k| of a pulse train's envelope - (1/P) times the integral over a period of e(t) e^(-j 2 pi k t / P) -
    /// by direct integration on a fine grid.</summary>
    private static double PulseLine(int k, double w, double p, double tr, double tf)
    {
        const int N = 200_000;
        double re = 0, im = 0;
        for (int i = 0; i < N; i++)
        {
            double t = (i + 0.5) * p / N, e = TxPulse.Envelope(t, w, p, tr, tf), ph = -2 * Math.PI * k * t / p;
            re += e * Math.Cos(ph);
            im += e * Math.Sin(ph);
        }
        return Math.Sqrt(re * re + im * im) / N;
    }

    /// <summary>The instants (fractional samples) where a sampled envelope crosses <paramref name="level"/> going up, and
    /// going down, linearly interpolated.</summary>
    private static (List<double> up, List<double> down) Crossings(double[] env, double level)
    {
        var up = new List<double>();
        var down = new List<double>();
        for (int n = 1; n < env.Length; n++)
        {
            if (env[n - 1] < level && env[n] >= level) up.Add(n - 1 + (level - env[n - 1]) / (env[n] - env[n - 1]));
            if (env[n - 1] >= level && env[n] < level) down.Add(n - 1 + (env[n - 1] - level) / (env[n - 1] - env[n]));
        }
        return (up, down);
    }

    private static double[] Envelope(short[] iq, int count, double amp)
    {
        var e = new double[count];
        for (int n = 0; n < count; n++) e[n] = Math.Sqrt((double)iq[2 * n] * iq[2 * n] + (double)iq[2 * n + 1] * iq[2 * n + 1]) / amp;
        return e;
    }

    // ---------------------------------------------------------------- the generator

    private static void PulseGeneratorCases()
    {
        const double rate = 200_000;
        const double bin = rate / 4096;
        const double level = -10;
        double amp = 32767 * Math.Pow(10, level / 20.0);
        double offset = 400 * bin;

        // The envelope, sample by sample, is the library's at each sample's instant.
        {
            double w = 300e-6, p = 1280e-6, tr = 25e-6, tf = 40e-6;
            var g = new PulseCarrier(offset, rate, level, w, p, tr, tf);
            var iq = new short[2 * 20_000];
            g.Fill(iq, 20_000);
            var env = Envelope(iq, 20_000, amp);
            double worst = 0;
            for (int n = 0; n < 20_000; n++) worst = Math.Max(worst, Math.Abs(env[n] - TxPulse.Envelope(n / rate, w, p, tr, tf)));
            Check(worst < 1e-4, $"20,000 samples' envelope is TxPulse.Envelope at each sample's instant to {F(worst * 1e6, "0")} ppm of the level (the sc16 rounding)");
            bool zero = true;
            for (int n = 0; n < 20_000; n++)
                if (TxPulse.Envelope(n / rate, w, p, tr, tf) == 0) zero &= iq[2 * n] == 0 && iq[2 * n + 1] == 0;
            Check(zero, "between pulses the samples are EXACTLY zero: the off-state in the samples is the DAC's zero, all else is the radio's");
        }

        // Continuous-time timing: a period of 1000.3 us is 200.06 samples, and every period measured is that, never 200 or 201.
        {
            double w = 250e-6, p = 1000.3e-6, t = 100e-6;
            var g = new PulseCarrier(0, rate, level, w, p, t, t);
            int n = (int)(rate * p * 60);
            var iq = new short[2 * n];
            g.Fill(iq, n);
            var (up, down) = Crossings(Envelope(iq, n, amp), 0.5);
            var periods = up.Zip(up.Skip(1), (x, y) => y - x).ToArray();
            var widths = up.Zip(down, (x, y) => y - x).ToArray();
            double want = p * rate;
            Check(periods.Length >= 55 && periods.All(v => Math.Abs(v - want) < 0.02) && Math.Abs(periods.Average() - want) < 0.002,
                  $"a period of {F(p * 1e6, "0.0")} us is {F(want, "0.00")} samples: {periods.Length} periods measured at {F(periods.Min(), "0.000")}-{F(periods.Max(), "0.000")}, " +
                  "never rounded to 200 or 201 - the timing is continuous");
            Check(widths.All(v => Math.Abs(v - w * rate) < 0.02), $"...and every width 50 % to 50 % is {F(w * rate, "0.00")} samples ({F(widths.Min(), "0.000")}-{F(widths.Max(), "0.000")})");
        }

        // The rise, read at 2 MS/s: 200 samples an edge, so a linear crossing is exact to far better than 0.1 %. (Read
        // from 200 kS/s, 20 samples an edge, the curvature at 10 % and 90 % puts a linear crossing 0.5 % long.)
        {
            const double fast = 2e6;
            double t = 100e-6, want = TxPulse.TenNinetyFromEdgeSeconds(t);
            var g = new PulseCarrier(0, fast, level, 250e-6, 1000e-6, t, t);
            int n = (int)(fast * 10e-3);
            var iq = new short[2 * n];
            g.Fill(iq, n);
            var e = Envelope(iq, n, amp);
            var (u10, _) = Crossings(e, 0.1);
            var (u90, _) = Crossings(e, 0.9);
            double rise = u90.Zip(u10, (b, a) => b - a).Average() / fast;
            Check(Math.Abs(rise - want) / want < 0.001,
                  $"...its 10-90 % rise {F(rise * 1e6, "0.000")} us, the edge's {F(want * 1e6, "0.000")} us (0.482188 of its 100 us), read at 2 MS/s");
        }

        // Its spectrum: lines at k x PRF standing at L + 20 log10|c_k|, the envelope's Fourier coefficients.
        {
            double p = 256 / rate, w = 60 / rate, tr = 25e-6, tf = 25e-6;   // 256 samples: 16 periods in the FFT, lines 16 bins apart
            var s = SpectrumOf(new PulseCarrier(offset, rate, level, w, p, tr, tf), 2);
            double worst = 0;
            var said = new List<string>();
            foreach (int k in new[] { 0, 1, -1, 2, -2, 3, 5, 6 })
            {
                double want = level + 20 * Math.Log10(PulseLine(Math.Abs(k), w, p, tr, tf));
                double got = s[2048 + 400 + 16 * k];
                worst = Math.Max(worst, Math.Abs(got - want));
                said.Add($"{k}: {F(got)}/{F(want)}");
            }
            Check(worst < 0.05, $"a {F(w * 1e6, "0")} us pulse every {F(p * 1e6, "0")} us: its lines at L + 20 log10|c_k| to {F(worst, "0.000")} dB - {string.Join(", ", said)}");
            Check(Math.Abs(s[2048 + 400] - (level + 20 * Math.Log10(TxPulse.Duty(w, p)))) < 0.05,
                  $"...the carrier's line at L + 20 log10(duty) = {F(level + 20 * Math.Log10(TxPulse.Duty(w, p)))} ({F(s[2048 + 400])}), whatever the edges");
        }

        {
            var one = new PulseCarrier(offset, rate, -8, 100e-6, 777.7e-6, 30e-6, 50e-6);
            var ten = new PulseCarrier(offset, rate, -8, 100e-6, 777.7e-6, 30e-6, 50e-6);
            var x = new short[2 * 3000];
            var y = new short[2 * 3000];
            one.Fill(x, 3000);
            for (int k = 0; k < 10; k++) ten.Fill(y.AsSpan(k * 600), 300);
            Check(x.SequenceEqual(y), "a pulse train is continuous: ten calls of 300 are the same samples as one of 3000 (its clock is the sample count)");
        }
        Check(Throws(() => new PulseCarrier(0, rate, -3, 100e-6, 1e-3, 24.9e-6, 30e-6)) && !Throws(() => new PulseCarrier(0, rate, -3, 100e-6, 1e-3, 25e-6, 30e-6)) &&
              Throws(() => new PulseCarrier(0, rate, 0.5, 100e-6, 1e-3, 30e-6, 30e-6)) &&
              Throws(() => new PulseCarrier(0, rate, -3, 20e-6, 1e-3, 30e-6, 30e-6)) &&
              Throws(() => new PulseCarrier(0, rate, -3, 100e-6, 120e-6, 30e-6, 30e-6)),
              "refused, never aliased or clipped: an edge under 5 samples (25 us at 200 kS/s fits), a level above full scale, a width under its edges, a period with no return to zero");
    }

    // ---------------------------------------------------------------- the synthetic radio

    private static void SimPulseLoopback()
    {
        var (backend, server, log) = StartSimTx("pulse");
        try
        {
            using var s = Connect(server, "pulse");
            s.WaitReady(5000);
            Check(s.Radio.Source == RadioSources.Sim, "before anything is keyed, the radio says it is synthetic");
            var caps = s.Caps.Tx!;
            Check(caps.OffersModulation(TxWords.Pulse), $"caps.tx modulates {string.Join(", ", caps.Modulations)}");
            s.ClaimTx();
            const double pbin = 2e6 / 4096;
            long center = 100_000_000 + 20 * OnBin, carrier = center + OnBin;
            s.SetTx(new TxSettings { CenterHz = center, GainDb = 70, RateHz = 250_000 });
            var rows = new List<(RowHeader h, short[] bins)>();
            var pano = s.OpenPanorama(new PanoramaRequest { Bins = 4096, Fps = 10, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
            pano.Connection.RowReceived += (c, h, bins) => { lock (rows) rows.Add((h, (short[])bins.Clone())); };
            pano.Connection.StartReader();
            // A lease on the carrier, 250 kS/s: the scene's own carrier at 100.1 MHz lies outside it.
            using var lease = s.OpenLease(new LeaseRequest { CenterHz = carrier, Decimation = 8 });
            double leaseRate = lease.Grant.RateHz;
            var blocks = new List<(IqHeader h, byte[] b)>();
            lease.Connection.IqBlockReceived += (c, h, b) => { lock (blocks) blocks.Add((h, (byte[])b.Clone())); };
            lease.Connection.StartReader();
            Thread.Sleep(400);
            double level = -10 + 70 + SimBackend.DefaultTxCouplingDb;

            // PRF 4 panorama bins (1953.125 Hz: a 512 us period), 128 us wide, 20 us edges (10-90 %).
            double p = 1 / (4 * pbin), w = 128e-6;
            var req = new TxStartRequest
            {
                Source = TxSourceKinds.Mod, MaxMs = 20_000, Modulation = TxWords.Pulse, OffsetHz = OnBin, LevelDbfs = -10,
                PulseWidthUs = w * 1e6, PulsePeriodUs = p * 1e6, PulseRiseUs = 20, PulseFallUs = 20,
            };
            var t = s.StartTx(req);
            double edge = TxPulse.EdgeSecondsFromTenNinety(20e-6);
            Check(t.Grant.RateHz == TxRules.RateFor(req, caps) && t.Grant.RateHz == 200e3 && t.Grant.PulseRiseUs == 20 && t.Grant.PulseFallUs == 20 &&
                  Math.Abs(t.Grant.PulsePrfHz - 4 * pbin) < 1e-6 && Math.Abs(t.Grant.PulseDuty - w / p) < 1e-12 && t.Grant.PeakDbfs == -10,
                  $"the grant: {F(t.Grant.RateHz, "0")} S/s (edges of {F(edge * 1e6, "0.0")} us whole occupy +-{F(2 / edge, "0")} Hz), rise/fall {F(t.Grant.PulseRiseUs)}/{F(t.Grant.PulseFallUs)} us, " +
                  $"PRF {F(t.Grant.PulsePrfHz, "0.000")} Hz, duty {F(t.Grant.PulseDuty, "0.0000")}, peak {F(t.Grant.PeakDbfs)} (the on-level)");
            Thread.Sleep(900);
            (RowHeader h, short[] b) r;
            lock (rows) r = rows[^1];
            double worst = 0;
            var said = new List<string>();
            foreach (int k in new[] { 0, 1, -1, 2, -2, 3, 5, 6 })
            {
                double want = level + 20 * Math.Log10(PulseLine(Math.Abs(k), w, p, edge, edge));
                double got = BinLevel(r.h, r.b, carrier + k / p, 2e6);
                worst = Math.Max(worst, Math.Abs(got - want));
                said.Add($"{k}: {F(got)}/{F(want)}");
            }
            Check(worst < 0.05, $"heard in the synthetic radio's own panorama: the lines at k x PRF at L + 20 log10|c_k| to {F(worst, "0.000")} dB - {string.Join(", ", said)}");
            t.Stop(drain: false);
            Thread.Sleep(200);

            // What the radio RECEIVED, through the lease: the envelope's width, period and rise.
            List<(IqHeader h, byte[] b)> got2;
            lock (blocks) got2 = blocks.ToList();
            int total = got2.Sum(g => g.h.Count);
            var iq = new short[2 * total];
            int at = 0;
            foreach (var (h, b) in got2)
            {
                Buffer.BlockCopy(b, 0, iq, 2 * at * 2, h.Count * 4);
                at += h.Count;
            }
            var env = Envelope(iq, total, 1);
            double top = env.OrderByDescending(v => v).Skip(total / 50).First();
            for (int n = 0; n < total; n++) env[n] /= top;
            var (up, down) = Crossings(env, 0.5);
            var periods = up.Zip(up.Skip(1), (x, y) => (y - x) / leaseRate).Where(v => v < 2 * p).ToArray();
            var widths = up.Select(u => down.FirstOrDefault(d => d > u) - u).Where(v => v > 0 && v < p * leaseRate).Select(v => v / leaseRate).ToArray();
            var (u10, _) = Crossings(env, 0.1);
            var (u90, _) = Crossings(env, 0.9);
            var rises = u90.Select(u => u - u10.LastOrDefault(v => v < u)).Where(v => v > 0 && v < p * leaseRate).Select(v => v / leaseRate).ToArray();
            Check(periods.Length >= 100 && periods.All(v => Math.Abs(v - p) < 0.3e-6),
                  $"through a {F(leaseRate, "0")} S/s lease: {periods.Length} periods of {F(p * 1e6, "0")} us, each within 0.3 us ({F(periods.Min() * 1e6, "0.00")}-{F(periods.Max() * 1e6, "0.00")})");
            Check(widths.Length >= 100 && widths.All(v => Math.Abs(v - w) < 0.5e-6),
                  $"...{widths.Length} widths of {F(w * 1e6, "0")} us 50 % to 50 %, each within 0.5 us ({F(widths.Min() * 1e6, "0.00")}-{F(widths.Max() * 1e6, "0.00")})");
            Check(rises.Length >= 100 && Math.Abs(rises.Average() - 20e-6) < 1e-6,
                  $"...and a 10-90 % rise of {F(rises.Average() * 1e6, "0.00")} us for the 20 us asked (the lease's own filter adds its rise in quadrature)");

            // No edges named: the fastest the widest admitted rate holds, and the carrier's line at L + 20 log10(duty).
            s.SetTx(new TxSettings { CenterHz = s.GetState().LoHz });
            long c2 = s.GetState().LoHz + OnBin;
            var fast = new TxStartRequest
            {
                Source = TxSourceKinds.Mod, MaxMs = 20_000, Modulation = TxWords.Pulse, OffsetHz = OnBin, LevelDbfs = -10,
                PulseWidthUs = 64, PulsePeriodUs = p * 1e6,
            };
            var f = s.StartTx(fast);
            double fastest = TxPulse.TenNinetyFromEdgeSeconds(2 / (ProtocolConstants.TxUsableFraction / 2 * 2e6 - OnBin)) * 1e6;
            Check(f.Grant.RateHz == 2e6 && Math.Abs(f.Grant.PulseRiseUs - fastest) < 1e-6,
                  $"a pulse with no edges named, {OnBin} Hz off its centre, runs at the widest rate ({F(f.Grant.RateHz, "0")} S/s) with the fastest edges that fit " +
                  $"there, {F(f.Grant.PulseRiseUs, "0.000")} us 10-90 %");
            Thread.Sleep(900);
            lock (rows) r = rows[^1];
            double duty = BinLevel(r.h, r.b, c2, 2e6), wantDuty = level + 20 * Math.Log10(64e-6 / p);
            Check(Math.Abs(duty - wantDuty) < 0.05, $"...its carrier's line at L + 20 log10(duty) = {F(wantDuty)} ({F(duty)} dBFS), whatever the edges");
            f.Stop(drain: false);
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
