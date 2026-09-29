// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// The sidecar's gate (ShackLink docs/usrp_b210_support.md section 9.7): exit 0 = all pass.
// DSP against references, the real host in process against the synthetic radio, and the real
// executable spawned, joined and left to exit (section 9.1 D4-D6). No radio needed.

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ShackLinkUsrp.Client;
using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Host;
using ShackLinkUsrp.Radio;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Tests;

internal static class Program
{
    private static int _pass, _fail;
    private static string _temp = "";

    private static int Main(string[] args)
    {
        _temp = Path.Combine(Path.GetTempPath(), "ShackLinkUsrpTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        Rendezvous.RootOverride = Path.Combine(_temp, "state");
        Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
        if (args.Length > 0 && args[0] == "--live") return LiveRun(args);
        var sw = Stopwatch.StartNew();

        Run("ring", RingCases);
        Run("FFT against a direct DFT", FftCases);
        Run("windows", WindowCases);
        Run("spectrum levels", SpectrumLevelCases);
        Run("decimating FIR", FirCases);
        Run("NCO", NcoCases);
        Run("in process: a lease on the synthetic radio", InProcessLease);
        Run("in process: panorama, sweep, retune, rate change", InProcessPanorama);
        Run("spawned: the real executable, joined, then left to exit", SpawnJoinExit);

        try { Directory.Delete(_temp, true); } catch { }
        Console.WriteLine();
        Console.WriteLine($"ShackLinkUsrp.Tests: {(_fail == 0 ? "ALL PASS" : "FAILED")} ({_pass} passed, {_fail} failed, {sw.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s)");
        return _fail == 0 ? 0 : 1;
    }

    private static void Run(string name, Action body)
    {
        Console.WriteLine();
        Console.WriteLine("== " + name);
        try { body(); }
        catch (Exception ex) { Fail($"{name} threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
    }

    private static void Check(bool ok, string what)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + what); }
        else Fail(what);
    }

    private static void Fail(string what)
    {
        _fail++;
        Console.WriteLine("  FAIL  " + what);
    }

    private static string F(double v, string fmt = "0.00") => v.ToString(fmt, CultureInfo.InvariantCulture);

    private static bool WaitFor(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (cond()) return true;
            Thread.Sleep(10);
        }
        return cond();
    }

    // ---------------------------------------------------------------- DSP

    private static void RingCases()
    {
        var ring = new WideRing(1000);
        Check(ring.Capacity == 1024, "capacity rounds up to a power of two");
        var a = new short[2000];
        for (int i = 0; i < 1000; i++) { a[2 * i] = (short)i; a[2 * i + 1] = (short)-i; }
        ring.Write(a);
        var r = new short[200];
        Check(ring.TryRead(900, 100, r) && r[0] == 900 && r[1] == -900 && r[198] == 999, "a read returns the samples at their positions");
        ring.Write(a);                                  // 2000 written: positions 976.. survive
        Check(!ring.TryRead(900, 50, r), "a lapped position is refused, not returned stale");
        Check(ring.TryRead(1990, 10, r) && r[0] == 990, "a read across the wrap is contiguous");
        Check(ring.Oldest == 2000 - 1024, "Oldest = written - capacity");
        var waiter = Task.Run(() => ring.WaitFor(2500, 2000));
        Thread.Sleep(50);
        ring.Write(new short[1000]);
        Check(waiter.Wait(1000) && waiter.Result, "a waiter wakes when the data it waits for is written");
    }

    private static void FftCases()
    {
        foreach (int n in new[] { 16, 64, 1024 })
        {
            var rnd = new Random(n);
            var re = new float[n];
            var im = new float[n];
            var x = new Complex[n];
            for (int i = 0; i < n; i++)
            {
                re[i] = (float)(rnd.NextDouble() * 2 - 1);
                im[i] = (float)(rnd.NextDouble() * 2 - 1);
                x[i] = new Complex(re[i], im[i]);
            }
            new ComplexFft(n).Forward(re, im);
            double maxErr = 0;
            for (int k = 0; k < n; k++)
            {
                Complex s = 0;
                for (int t = 0; t < n; t++) s += x[t] * Complex.FromPolarCoordinates(1, -2 * Math.PI * k * t / n);
                maxErr = Math.Max(maxErr, (s - new Complex(re[k], im[k])).Magnitude / Math.Sqrt(n));
            }
            Check(maxErr < 1e-4, $"N={n}: matches the direct DFT (max normalised error {maxErr:E2})");
        }
        Check(Throws(() => new ComplexFft(1000)), "a size that is not a power of two is refused");
    }

    private static bool Throws(Action a)
    {
        try { a(); return false; } catch { return true; }
    }

    private static void WindowCases()
    {
        Check(Math.Abs(new SpectralWindow(WindowShape.Rectangular, 4096).EnbwBins - 1.0) < 1e-9, "rectangular ENBW = 1 bin");
        Check(Math.Abs(new SpectralWindow(WindowShape.Hann, 4096).EnbwBins - 1.5) < 1e-6, "Hann ENBW = 1.5 bins");
        double bh = new SpectralWindow(WindowShape.BlackmanHarris, 4096).EnbwBins;
        Check(Math.Abs(bh - 2.0044) < 1e-3, $"Blackman-Harris ENBW = 2.004 bins ({F(bh, "0.0000")})");
        Check(Math.Abs(new SpectralWindow(WindowShape.Hann, 4096).CoherentSum - 2048) < 1e-6, "Hann coherent sum = N/2");
    }

    private static short[] Tone(int n, double cyclesPerSample, double dbfs)
    {
        var iq = new short[n * 2];
        double a = 32767 * Math.Pow(10, dbfs / 20);
        for (int i = 0; i < n; i++)
        {
            double ph = 2 * Math.PI * cyclesPerSample * i;
            iq[2 * i] = (short)Math.Round(a * Math.Cos(ph));
            iq[2 * i + 1] = (short)Math.Round(a * Math.Sin(ph));
        }
        return iq;
    }

    private static void SpectrumLevelCases()
    {
        const int n = 1024;
        foreach (var shape in new[] { WindowShape.Rectangular, WindowShape.Hann, WindowShape.BlackmanHarris })
        {
            var acc = new SpectrumAccumulator(n, shape, Integration.Average);
            acc.AddFrame(Tone(n, 100.0 / n, -20));
            var row = new short[n];
            acc.TakeRow(0, n, row);
            int peak = Array.IndexOf(row, row.Max());
            Check(peak == n / 2 + 100 && Math.Abs(row[peak] / 100.0 + 20) < 0.05,
                  $"{shape}: a -20 dBFS tone on bin +100 reads {F(row[peak] / 100.0)} dBFS at shifted index {peak} (0 Hz is index N/2)");
        }
        var avg = new SpectrumAccumulator(n, WindowShape.Hann, Integration.Average);
        var peakAcc = new SpectrumAccumulator(n, WindowShape.Hann, Integration.Peak);
        foreach (var level in new[] { -40.0, -20.0 })
        {
            avg.AddFrame(Tone(n, 50.0 / n, level));
            peakAcc.AddFrame(Tone(n, 50.0 / n, level));
        }
        var ra = new short[n];
        var rp = new short[n];
        avg.TakeRow(0, n, ra);
        peakAcc.TakeRow(0, n, rp);
        double expectAvg = 10 * Math.Log10((Math.Pow(10, -4.0) + Math.Pow(10, -2.0)) / 2);
        Check(Math.Abs(ra[n / 2 + 50] / 100.0 - expectAvg) < 0.05, $"average integrates POWER over frames ({F(ra[n / 2 + 50] / 100.0)} vs {F(expectAvg)})");
        Check(Math.Abs(rp[n / 2 + 50] / 100.0 + 20) < 0.05, "peak keeps the largest frame");
        var crop = new SpectrumAccumulator(n, WindowShape.Hann, Integration.Sample);
        crop.AddFrame(Tone(n, 100.0 / n, -20));
        var rc = new short[64];
        crop.TakeRow(n / 2 + 100 - 32, 64, rc);
        Check(Array.IndexOf(rc, rc.Max()) == 32, "a cropped row keeps the tone where the crop put it");
        var empty = new short[4];
        new SpectrumAccumulator(64, WindowShape.Hann, Integration.Average).TakeRow(0, 4, empty);
        Check(empty.All(v => v == short.MinValue), "a row with no frame reads the floor, never a plausible level");
    }

    private static void FirCases()
    {
        foreach (int d in new[] { 2, 10, 50 })
        {
            var fir = new DecimatingFir(d);
            double Response(double cyclesPerInputSample)
            {
                int n = d * 4000;
                var re = new float[n];
                var im = new float[n];
                for (int i = 0; i < n; i++)
                {
                    re[i] = (float)Math.Cos(2 * Math.PI * cyclesPerInputSample * i);
                    im[i] = (float)Math.Sin(2 * Math.PI * cyclesPerInputSample * i);
                }
                fir.Reset();
                var or = new float[n / d + 2];
                var oi = new float[n / d + 2];
                int m = fir.Process(re, im, n, or, oi, 0);
                double p = 0;
                int from = m / 2;
                for (int i = from; i < m; i++) p += or[i] * or[i] + oi[i] * oi[i];
                return 10 * Math.Log10(p / (m - from));
            }
            double outNyq = 0.5 / d;
            double dc = Response(0);
            double pass = Response(0.5 * outNyq);
            double edge = Response(0.6 * outNyq);
            double alias = Response(1.5 * outNyq);
            Check(Math.Abs(dc) < 0.01, $"D={d}: unit gain at DC ({F(dc, "0.000")} dB)");
            Check(Math.Abs(pass) < 0.1, $"D={d}: flat at half the output Nyquist ({F(pass, "0.000")} dB)");
            Check(Math.Abs(edge) < 0.5, $"D={d}: within 0.5 dB at 60 % of the output Nyquist ({F(edge, "0.000")} dB)");
            Check(alias < -70, $"D={d}: a tone that would alias onto 50 % of the band is down {F(-alias, "0.0")} dB");
        }
    }

    private static void NcoCases()
    {
        var nco = new Nco();
        nco.Set(250_000, 10e6);
        var iq = Tone(10000, 0.025, -6);                // +250 kHz at 10 MS/s
        var re = new float[10000];
        var im = new float[10000];
        nco.Mix(iq, 10000, re, im);
        double sr = 0, si = 0;
        for (int i = 0; i < 10000; i++) { sr += re[i]; si += im[i]; }
        double mag = Math.Sqrt(sr * sr + si * si) / 10000;
        Check(Math.Abs(20 * Math.Log10(mag) + 6) < 0.01, $"a +250 kHz tone lands on DC at its level ({F(20 * Math.Log10(mag))} dBFS)");
    }

    // ---------------------------------------------------------------- in process

    private static (SimBackend backend, UsrpServer server, SidecarLog log) StartSim(string name)
    {
        var log = new SidecarLog(null, console: false);
        var backend = new SimBackend(log, name, 10e6);
        var server = new UsrpServer(backend, new UsrpServerOptions { Port = 0, SidecarVersion = "test", LingerSeconds = 60 });
        server.Start();
        backend.Start();
        return (backend, server, log);
    }

    private static UsrpSession Connect(UsrpServer server, string client) =>
        UsrpSession.Open(UsrpAddress.Remote("127.0.0.1", server.Port), new UsrpSessionOptions { ClientName = client });

    private static void InProcessLease()
    {
        var (backend, server, log) = StartSim("lease");
        try
        {
            using var s = Connect(server, "lease-test");
            Check(s.WaitReady(5000) == RadioStates.Ready, "the synthetic radio opens");
            Check(s.Radio.Source == RadioSources.Sim && s.Radio.Serial == "sim-lease", "it says it is synthetic, by name");
            s.ClaimLo();
            using var lease = s.OpenLease(new LeaseRequest { CenterHz = 100_100_000, Decimation = 50 });
            Check(Math.Abs(lease.Grant.RateHz - 200_000) < 1e-6, "a 200 kHz lease (D=50 of 10 MS/s)");
            var blocks = new List<(IqHeader h, byte[] s)>();
            var first = Stopwatch.StartNew();
            lease.Connection.IqBlockReceived += (c, h, samples) => { lock (blocks) blocks.Add((h, samples)); };
            lease.Connection.StartReader();
            Thread.Sleep(1500);
            List<(IqHeader h, byte[] s)> got;
            lock (blocks) got = blocks.ToList();
            long samples = got.Sum(b => (long)b.h.Count);
            double rate = samples / first.Elapsed.TotalSeconds;
            Check(rate > 150_000 && rate < 230_000, $"delivered ~200 kS/s against the clock ({F(rate / 1000, "0.0")} kS/s over {F(first.Elapsed.TotalSeconds)} s)");
            bool contiguous = true;
            for (int i = 1; i < got.Count; i++)
            {
                ulong want = got[i - 1].h.FirstSample + (ulong)got[i - 1].h.Count;
                if ((got[i].h.Flags & IqFlags.Discontinuity) != 0) want += got[i].h.Dropped;
                if (got[i].h.FirstSample != want) contiguous = false;
            }
            Check(contiguous, "every block starts where the previous ended (or declares the gap)");
            Check(got.All(b => b.h.Count <= 4000), "a 200 kHz lease's blocks are at most 20 ms (4000 samples)");
            // The -20 dBFS calibration carrier sits on the lease centre: its level is the mean magnitude.
            var tail = got.Skip(got.Count / 2).ToList();
            double sumMag = 0;
            long cnt = 0;
            foreach (var (h, bytes) in tail)
                for (int i = 0; i < h.Count; i++)
                {
                    short I = BitConverter.ToInt16(bytes, 4 * i), Q = BitConverter.ToInt16(bytes, 4 * i + 2);
                    sumMag += Math.Sqrt((double)I * I + (double)Q * Q);
                    cnt++;
                }
            double dbfs = 20 * Math.Log10(sumMag / cnt / 32767);
            Check(Math.Abs(dbfs + 20) < 0.3, $"the -20 dBFS carrier on the lease centre reads {F(dbfs)} dBFS through NCO + FIR");

            long moved = lease.Tune(100_150_000);
            Check(moved == 100_150_000, "tune_lease moves the lease");
            Check(WaitFor(() => { lock (blocks) return blocks.Any(b => b.h.CenterHz == 100_150_000 && (b.h.Flags & IqFlags.Retuned) != 0); }, 2000),
                  "the first block after the retune carries the new centre and the retuned flag");
            var tel = s.GetTelemetry();
            Check(tel.GetObject("radio")?.GetArray("pipelines")?.Count >= 1, "telemetry lists the lease pipeline");
        }
        finally
        {
            server.Dispose();
            backend.Dispose();
            log.Dispose();
        }
    }

    private static void InProcessPanorama()
    {
        var (backend, server, log) = StartSim("pano");
        try
        {
            using var s = Connect(server, "pano-test");
            s.WaitReady(5000);
            s.ClaimLo();
            using var pano = s.OpenPanorama(new PanoramaRequest { Bins = 4096, Fps = 10, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
            Check(pano.Grant.FftSize == 4096 && pano.Grant.Bins == 4096 && pano.Grant.SpanHz == 10_000_000 && pano.Grant.CenterHz == 100_000_000,
                  $"a full-span panorama: FFT {pano.Grant.FftSize}, {pano.Grant.Bins} bins over {pano.Grant.SpanHz} Hz at {pano.Grant.CenterHz}");
            var rows = new List<(RowHeader h, short[] b)>();
            pano.Connection.RowReceived += (c, h, bins) => { lock (rows) rows.Add((h, (short[])bins.Clone())); };
            pano.Connection.StartReader();
            Check(WaitFor(() => { lock (rows) return rows.Count >= 4; }, 5000), "rows arrive");
            (RowHeader h, short[] b) last;
            lock (rows) last = rows[^1];
            double binHz = 10e6 / 4096;
            int carrierBin = 2048 + (int)Math.Round(100_000 / binHz);
            short peakVal = last.b.Skip(carrierBin - 3).Take(7).Max();
            Check(Math.Abs(peakVal / 100.0 + 20) < 0.5, $"the -20 dBFS carrier at +100 kHz reads {F(peakVal / 100.0)} dBFS");
            var sorted = last.b.OrderBy(v => v).ToArray();
            double floor = sorted[sorted.Length / 2] / 100.0;
            double expectFloor = -65 + 10 * Math.Log10(1.5 / 4096);
            Check(Math.Abs(floor - expectFloor) < 1.0, $"the noise floor reads {F(floor)} dBFS per bin (expected {F(expectFloor)} for -65 dBFS through a 1.5-bin ENBW)");
            Check(last.h.Ffts >= 150 && last.h.Integration == IntegrationKind.Average && last.h.Window == WindowKind.Hann,
                  $"a 10 fps row integrates every FFT of its 100 ms ({last.h.Ffts} FFTs; 244 is all of them)");

            var sweep = s.Sweep(new PanoramaRequest { Bins = 1024, SpanHz = 2_500_000, CenterHz = 100_000_000, Integration = IntegrationKind.Peak, Window = WindowKind.BlackmanHarris });
            Check(sweep.Header.Bins == 1024 && sweep.Header.FftSize == 4096 && sweep.Header.SpanHz == 2_500_000,
                  "sweep: 1024 bins over a quarter span crop a 4096-point FFT");
            Thread.Sleep(300);
            var sweep2 = s.Sweep(new PanoramaRequest { Bins = 1024, SpanHz = 2_500_000, CenterHz = 100_000_000, Integration = IntegrationKind.Peak, Window = WindowKind.BlackmanHarris });
            Check(sweep2.Header.Ffts >= 300, $"the second sweep integrates everything since the first ({sweep2.Header.Ffts} FFTs in ~300 ms)");
            int cBin = 512 + (int)Math.Round(100_000 / binHz);
            Check(Math.Abs(sweep2.Bins.Skip(cBin - 3).Take(7).Max() / 100.0 + 20) < 1.0, "the carrier is in the sweep at its level");

            // Retune: the carrier moves by the retune in the panorama.
            s.SetLo(99_500_000);
            Thread.Sleep(600);
            lock (rows) last = rows[^1];
            int moved = 2048 + (int)Math.Round(600_000 / binHz);
            Check(last.h.CenterHz == 99_500_000 && Math.Abs(last.b.Skip(moved - 3).Take(7).Max() / 100.0 + 20) < 0.5,
                  "after set_lo the panorama follows the LO and the carrier sits 600 kHz up");

            // Rate change: the panorama keeps its bins and re-derives its span.
            s.SetRate(2e6);
            Thread.Sleep(1200);
            lock (rows) last = rows[^1];
            Check(last.h.SpanHz == 2_000_000 && last.h.Bins == 4096, $"after set_rate 2 MS/s the row covers 2 MHz in 4096 bins ({last.h.SpanHz})");
        }
        finally
        {
            server.Dispose();
            backend.Dispose();
            log.Dispose();
        }
    }

    // ---------------------------------------------------------------- live (an instrument, never a gate)

    /// <summary><c>--live &lt;serial&gt; [--uhd dir] [--otw sc16|sc8] [--rate S/s] [--seconds n] [--allow-known-codec-fault]</c>:
    /// spawn the real sidecar on a real radio through the client library and report what arrived.
    /// Always exits 0 unless it cannot run; the numbers are the result.</summary>
    private static int LiveRun(string[] args)
    {
        string serial = args.Length > 1 ? args[1] : "";
        string? uhd = null, otw = null;
        double rate = 10e6;
        int seconds = 10;
        bool fault = false;
        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--uhd": uhd = args[++i]; break;
                case "--otw": otw = args[++i]; break;
                case "--rate": rate = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seconds": seconds = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--allow-known-codec-fault": fault = true; break;
            }
        }
        var extra = new List<string> { "--state-dir", Rendezvous.Root };
        if (otw != null) { extra.Add("--otw"); extra.Add(otw); }
        var opts = new UsrpSessionOptions
        {
            ClientName = "live-probe",
            SidecarExe = SidecarExe(),
            SpawnTimeoutMs = 20000,
            RequestTimeoutMs = 20000,
            Launch = new SidecarLaunch { UhdDirectory = uhd, AllowKnownCodecFault = fault, WideRateHz = rate, LingerSeconds = 2, ExtraArgs = extra.ToArray() },
            Log = l => Console.WriteLine("  [session] " + l),
        };
        var sw = Stopwatch.StartNew();
        using var s = UsrpSession.Open(UsrpAddress.Local(serial), opts);
        Console.WriteLine($"joined/spawned in {sw.ElapsedMilliseconds} ms: pid {s.SidecarPid}, port {s.Port}, state {s.Hello.State.Readiness}");
        string ready = s.WaitReady(60000);
        Console.WriteLine($"radio {ready} after {sw.Elapsed.TotalSeconds:0.0} s: {s.Radio.Product} {s.Radio.Serial} '{s.Radio.Name}' ({s.Radio.Source})" +
                          (ready == RadioStates.Ready ? "" : " - " + s.FaultText));
        if (ready != RadioStates.Ready)
        {
            foreach (var l in s.GetLog(20)) Console.WriteLine("  | " + l);
            return 1;
        }
        var c = s.Caps;
        Console.WriteLine($"caps: {c.FreqMinHz}-{c.FreqMaxHz} Hz, gain {c.GainMinDb}-{c.GainMaxDb} step {c.GainStepDb}, " +
                          $"rates {string.Join("/", c.WideRatesHz.Select(r => (r / 1e6).ToString("0.##", CultureInfo.InvariantCulture)))} MS/s, " +
                          $"antennas {string.Join(",", c.Antennas)}, bandwidth {c.BandwidthMinHz}-{c.BandwidthMaxHz}");
        var st = s.ClaimLo();
        Console.WriteLine($"state: LO {st.LoHz}, rate {st.WideRateHz}, gain {st.GainDb}, {st.Antenna}, bw {st.BandwidthHz}, owner {st.Owner?.Client}");

        using var pano = s.OpenPanorama(new PanoramaRequest { Bins = 8192, Fps = 10, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
        using var lease = s.OpenLease(new LeaseRequest { CenterHz = st.LoHz + 1_000_000, Decimation = 50 });
        long rows = 0, rowFfts = 0, rowDropped = 0, rowOverflows = 0;
        long samples = 0, blocks = 0, disc = 0, leaseDropped = 0;
        double rowMedian = double.NaN;
        pano.Connection.RowReceived += (conn, h, bins) =>
        {
            Interlocked.Increment(ref rows);
            Interlocked.Add(ref rowFfts, h.Ffts);
            Interlocked.Add(ref rowDropped, h.Dropped);
            Interlocked.Add(ref rowOverflows, h.Overflows);
            var sorted = bins.OrderBy(v => v).ToArray();
            rowMedian = sorted[sorted.Length / 2] / 100.0;
        };
        lease.Connection.IqBlockReceived += (conn, h, data) =>
        {
            Interlocked.Increment(ref blocks);
            Interlocked.Add(ref samples, h.Count);
            if ((h.Flags & IqFlags.Discontinuity) != 0) { Interlocked.Increment(ref disc); Interlocked.Add(ref leaseDropped, h.Dropped); }
        };
        pano.Connection.StartReader();
        lease.Connection.StartReader();
        var run = Stopwatch.StartNew();
        Thread.Sleep(seconds * 1000);
        double t = run.Elapsed.TotalSeconds;
        var tel = s.GetTelemetry();
        var src = tel.GetObject("radio")?.GetObject("source");
        Console.WriteLine($"over {t:0.0} s at {st.WideRateHz / 1e6:0.##} MS/s ({src?.GetString("otw_format")}):");
        Console.WriteLine($"  panorama: {rows} rows ({rows / t:0.0}/s), {rowFfts / Math.Max(1, rows)} FFTs per row of {st.WideRateHz / 10 / 8192:0} possible, " +
                          $"{rowDropped} wide samples missed, {rowOverflows} overflows, median bin {rowMedian:0.0} dBFS");
        Console.WriteLine($"  lease {lease.Grant.RateHz / 1000:0} kHz: {samples / t / 1000:0.0} kS/s delivered, {blocks} blocks, {disc} discontinuities ({leaseDropped} samples)");
        Console.WriteLine($"  radio: recv {src?.GetLong("recv_samples")} samples = {src?.GetLong("recv_samples") / t / 1e6:0.00} MS/s, " +
                          $"overflows {tel.GetObject("radio")?.GetLong("overflows")}, timeouts {src?.GetLong("timeouts")}, " +
                          $"master clock {src?.GetDouble("master_clock_hz") / 1e6:0.##} MHz, UHD {src?.GetString("uhd_version")}");
        return 0;
    }

    // ---------------------------------------------------------------- spawned

    private static string SidecarExe()
    {
        // tests\ShackLinkUsrp.Tests\bin\<cfg>\net9.0\win-x64\ -> src\ShackLinkUsrp\bin\<cfg>\net9.0\win-x64\
        var here = new DirectoryInfo(AppContext.BaseDirectory);
        string cfg = here.Parent!.Parent!.Name;
        var root = here;
        while (root != null && !File.Exists(Path.Combine(root.FullName, "ShackLinkUsrp.sln"))) root = root.Parent;
        return Path.Combine(root!.FullName, "src", "ShackLinkUsrp", "bin", cfg, "net9.0", "win-x64", "ShackLinkUsrp.exe");
    }

    private static void SpawnJoinExit()
    {
        string exe = SidecarExe();
        Check(File.Exists(exe), "the sidecar executable is built: " + exe);
        string stateDir = Rendezvous.Root;
        string name = "gate" + Environment.ProcessId;
        var opts = new UsrpSessionOptions
        {
            ClientName = "spawner",
            SidecarExe = exe,
            Launch = new SidecarLaunch { LingerSeconds = 1, ExtraArgs = new[] { "--state-dir", stateDir } },
        };
        var addr = UsrpAddress.Sim(name);

        // Two programs opening at once: the mutex makes it ONE sidecar.
        UsrpSession? a = null, b = null;
        var ta = Task.Run(() => a = UsrpSession.Open(addr, opts));
        var tb = Task.Run(() => b = UsrpSession.Open(addr, new UsrpSessionOptions
        {
            ClientName = "joiner",
            SidecarExe = exe,
            Launch = opts.Launch,
        }));
        Task.WaitAll(ta, tb);
        Check(a != null && b != null && a.SidecarPid == b.SidecarPid && a.SidecarPid > 0,
              $"two opens at once share one sidecar (pids {a?.SidecarPid} / {b?.SidecarPid})");
        Check(a!.Spawned != b!.Spawned, "exactly one of them spawned it; the other joined");
        int pid = a.SidecarPid;
        Check(File.Exists(Rendezvous.PathFor("sim-" + name)), "the sidecar published its rendezvous file in the redirected state directory");
        Check(a.WaitReady(5000) == RadioStates.Ready && a.Radio.Serial == "sim-" + name, "the spawned synthetic radio is ready");
        a.ClaimLo();
        var nb = Throws(() => b.SetLo(101_000_000));
        Check(nb, "the joiner may not move the spawner's LO");

        using (var lease = b.OpenLease(new LeaseRequest { CenterHz = 100_100_000, Decimation = 100 }))
        {
            int n = 0;
            lease.Connection.IqBlockReceived += (c, h, s) => Interlocked.Increment(ref n);
            lease.Connection.StartReader();
            Check(WaitFor(() => Volatile.Read(ref n) >= 5, 3000), "the joiner receives lease blocks from the spawned sidecar");
        }

        // D4: the spawner leaving does NOT take the sidecar away from the joiner.
        var spawner = a.Spawned ? a : b;
        var joiner = a.Spawned ? b : a;
        spawner.Dispose();
        Thread.Sleep(2500); // longer than the 1 s linger
        bool alive = Rendezvous.IsProcessAlive(pid);
        Check(alive, "the sidecar outlives the program that spawned it while another client is connected");
        Check(!Throws(() => joiner.GetState()), "...and keeps serving that client");

        joiner.Dispose();
        Check(WaitFor(() => !Rendezvous.IsProcessAlive(pid), 6000), "after the last client leaves it exits by itself (linger 1 s)");
        Check(!File.Exists(Rendezvous.PathFor("sim-" + name)), "...and removes its rendezvous file");
        string logPath = Rendezvous.LogPathFor("sim-" + name);
        Check(File.Exists(logPath) && File.ReadAllText(logPath).Contains("stopping: the last client left"),
              "its log says why it stopped");
    }
}
