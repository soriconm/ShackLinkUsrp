// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// P0 of ShackLink docs/skimmer_support.md section 26 (band channels): MEASUREMENTS, never gates - every mode here exits 0.
//   --fir-response                 the lease filter's response at the 69 % and 75 % edges, from DecimatingFir's own taps
//   --fir-scaling [--seconds s]    DecimatingFir alone on 1..16 threads: does the compute of 16 leases scale
//   --lease-cost <port> --pid <n> [--seconds s] [--counts 0,1,2,4,8]
//                                  a RUNNING sidecar's CPU and losses with N leases laid out as HF behind a transverter

using System.Diagnostics;
using System.Globalization;
using ShackLinkUsrp.Client;
using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Tests;

internal static partial class Program
{
    /// <summary>The ten HF bands, whole (ADIF's edges, ShackLink AdifLog.BandForFreq), in the order the lease probe opens them:
    /// 10 m first (the widest lease), then by width.</summary>
    private static readonly (string Name, double Lo, double Hi)[] HfBands =
    {
        ("10m", 28.0e6, 29.7e6), ("20m", 14.0e6, 14.35e6), ("40m", 7.0e6, 7.3e6), ("80m", 3.5e6, 4.0e6),
        ("15m", 21.0e6, 21.45e6), ("160m", 1.8e6, 2.0e6), ("17m", 18.068e6, 18.168e6), ("30m", 10.1e6, 10.15e6),
        ("12m", 24.89e6, 24.99e6), ("60m", 5.06e6, 5.45e6),
    };

    /// <summary>The middle of 1.8-29.7 MHz: where the radio's centre sits in the band-channel layout (docs section 26.4).</summary>
    private const double HfBlockCentreHz = 15.75e6;

    /// <summary>The lease's clean fraction: the protocol library's, the ONE owner (docs section 26.14). P0 used the old
    /// filter's own comment, 0.69.</summary>
    private static double LeaseCleanFraction => ProtocolConstants.LeaseCleanFraction;

    /// <summary>The lease rates ShackLink offers, as UsrpKindTest pins UsrpKind.CleanLeaseRates at 10 and 20 MS/s (every
    /// one divides 40 MS/s). P0 sized leases by an unfiltered decimation (78, 92, 55 ...) that ShackLink never asks for.</summary>
    private static readonly double[] CleanLeaseRates = { 200e3, 250e3, 400e3, 500e3, 800e3, 1e6, 2e6, 2.5e6 };

    /// <summary>The decimation of the smallest clean lease rate whose clean fraction carries the whole band (section
    /// 26.4's rule), or of the widest one when none does.</summary>
    private static int BandDecimation(double wideHz, double widthHz)
    {
        var usable = CleanLeaseRates.Where(r => Math.Abs(wideHz / r - Math.Round(wideHz / r)) < 1e-9).ToArray();
        double rate = usable.FirstOrDefault(r => r * LeaseCleanFraction >= widthHz, usable.Max());
        return (int)Math.Round(wideHz / rate);
    }

    // ------------------------------------------------------------------ --fir-response

    /// <summary>The decimations the lease rates ShackLink offers come to (UsrpKind.CleanLeaseRates, 200 kHz .. 2.5 MS/s):
    /// at the W1700K's 40 MS/s, and at a B210's 61.44 and 30.72 MS/s.</summary>
    private static readonly int[] OfferedDecimations = { 16, 20, 25, 32, 40, 50, 64, 80, 100, 125, 128, 160, 200, 256, 320 };

    private static int FirResponse(string[] args)
    {
        // The cascade first (section 26.14 (2)): what a lease filters with now.
        int[] tpps = { 16, 20, 24, 32 };
        double[] shares = { 0.66, 0.69, 0.72, 0.75, 0.78, 0.80, 0.84 };
        for (int i = 1; i + 1 < args.Length; i += 2)
        {
            if (args[i] == "--taps") tpps = args[i + 1].Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            else if (args[i] == "--shares") shares = args[i + 1].Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        }
        Console.WriteLine("LeaseDecimator: halfbands by 2^k, then the Kaiser shaping filter at D/2^k. 'share' = the fraction of the lease a segment");
        Console.WriteLine("reaches (x = share/2 cycles per output sample); each cell is the strongest image folding onto that edge, through EVERY stage,");
        Console.WriteLine("relative to the gain there (dB), and that gain. From the double-precision design.");
        foreach (int tpp in tpps)
        {
            Console.WriteLine();
            Console.WriteLine($"shaping filter {tpp} taps a phase:");
            Console.WriteLine("    D  " + string.Join("  ", shares.Select(s => $"{F(s * 100, "0")} %: alias / pass ".PadRight(24))) + "  plan");
            foreach (int d in OfferedDecimations)
            {
                var plan = LeasePlan.For(d, tpp);
                var cells = shares.Select(s => $"{F(plan.WorstAliasDb(s), "0.0").PadLeft(6)} / {F(plan.PassDb(s), "0.00").PadLeft(5)} dB".PadRight(24));
                Console.WriteLine($"  {d,3}  " + string.Join("  ", cells) + "  " + plan);
            }
        }
        Console.WriteLine();
        Console.WriteLine("The single stage it replaced (DecimatingFir alone, 16 taps a phase, images m = -4..4 only):");
        Console.WriteLine("DecimatingFir: 16 taps a phase, Kaiser beta 8, cutoff 0.5/D (-6 dB at the output Nyquist). x = output frequency in");
        Console.WriteLine("cycles per OUTPUT sample (the lease's edge is 0.5). 'pass' = the gain at x; 'alias' = the strongest image that folds onto x");
        Console.WriteLine("(input at x+m, m != 0), relative to the gain at x. A segment of width W in a lease of rate R reaches x = W/(2R).");
        Console.WriteLine();
        double[] xs = { 0.25, 0.30, 0.33, 0.345, 0.36, 0.375 };
        Console.WriteLine("    D  " + string.Join("  ", xs.Select(x => $"x={F(x, "0.000")} ({F(2 * x * 100, "0")} %): pass / alias")));
        foreach (int d in new[] { 16, 20, 50, 64, 80, 100, 160, 200 })
        {
            var h = DecimatingFir.Design(DecimatingFir.TapsPerPhase * d + 1, 0.5 / d);
            double Db(double xOut)
            {
                double f = xOut / d, re = 0, im = 0;
                for (int n = 0; n < h.Length; n++)
                {
                    re += h[n] * Math.Cos(2 * Math.PI * f * n);
                    im -= h[n] * Math.Sin(2 * Math.PI * f * n);
                }
                return 10 * Math.Log10(Math.Max(1e-30, re * re + im * im));
            }
            var cells = xs.Select(x =>
            {
                double pass = Db(x);
                double alias = Enumerable.Range(-4, 9).Where(m => m != 0).Max(m => Db(Math.Abs(x + m)));
                return $"{F(pass, "0.00").PadLeft(6)} dB / {F(alias - pass, "0.0").PadLeft(6)} dB";
            });
            Console.WriteLine($"  {d,3}  " + string.Join("  ", cells));
        }
        return 0;
    }

    // ------------------------------------------------------------------ --fir-scaling

    /// <summary>One worker's DSP: what a lease thread does to a chunk of the wide stream, or a part of it.</summary>
    private enum LeaseWork { SingleStage, Cascade, NcoOnly, NcoAndCascade }

    private static Action<long> LeaseWorker(LeaseWork work, int d, int tpp, int seed, out int chunk, LeasePlan? plan = null)
    {
        int ch = Math.Max(d, 8192 / d * d);   // LeasePipeline's chunk
        chunk = ch;
        var rnd = new Random(seed);
        var raw = new short[2 * ch];
        for (int k = 0; k < raw.Length; k++) raw[k] = (short)rnd.Next(-8000, 8000);
        var re = new float[ch];
        var im = new float[ch];
        for (int k = 0; k < ch; k++) { re[k] = raw[2 * k] / 32768f; im[k] = raw[2 * k + 1] / 32768f; }
        var oRe = new float[ch / d + 2];
        var oIm = new float[ch / d + 2];
        var nco = new Nco();
        nco.Set(1_234_000, 40e6);
        var single = new DecimatingFir(d);
        var cascade = plan != null ? new LeaseDecimator(plan) : new LeaseDecimator(d, tpp);
        long pos = 0;
        return work switch
        {
            LeaseWork.SingleStage => _ => single.Process(re, im, ch, oRe, oIm, 0),
            LeaseWork.Cascade => _ => cascade.Process(re, im, ch, oRe, oIm, 0),
            LeaseWork.NcoOnly => _ => { nco.AnchorAt(pos); nco.Mix(raw, ch, re, im); pos += ch; },
            _ => _ => { nco.AnchorAt(pos); nco.Mix(raw, ch, re, im); pos += ch; cascade.Process(re, im, ch, oRe, oIm, 0); },
        };
    }

    /// <summary>Input samples a second per thread (the mean and the slowest), <paramref name="threads"/> workers at once.</summary>
    private static (double Mean, double Slowest) Throughput(LeaseWork work, int d, int tpp, int threads, double seconds, LeasePlan? plan = null)
    {
        var done = new long[threads];
        var go = new ManualResetEventSlim(false);
        var stop = false;
        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            var step = LeaseWorker(work, d, tpp, t, out int chunk, plan);
            for (int w = 0; w < 50; w++) step(0);   // warm: JIT tiers, caches
            go.Wait();
            long count = 0;
            while (!Volatile.Read(ref stop))
            {
                step(0);
                count += chunk;
            }
            done[t] = count;
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal }).ToList();
        workers.ForEach(w => w.Start());
        Thread.Sleep(300);
        var sw = Stopwatch.StartNew();
        go.Set();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        Volatile.Write(ref stop, true);
        workers.ForEach(w => w.Join());
        double el = sw.Elapsed.TotalSeconds;
        return (done.Average() / el / 1e6, done.Min() / el / 1e6);
    }

    private static int FirScaling(string[] args)
    {
        double seconds = 3;
        int[] ds = { 16, 20, 40, 80, 160, 200 };
        int[] tpps = { LeasePlan.DefaultShapingTapsPerPhase };
        bool legacy = false, depths = false;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--seconds" && i + 1 < args.Length) seconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (args[i] == "--d" && i + 1 < args.Length) ds = args[++i].Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            else if (args[i] == "--taps" && i + 1 < args.Length) tpps = args[++i].Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            else if (args[i] == "--legacy") legacy = true;
            else if (args[i] == "--depths") depths = true;
        }
        string Pct(double msps) => F(40.0 / msps * 100, "0.0").PadLeft(5) + " %";
        Console.WriteLine($"A lease's DSP on one thread, {F(seconds, "0")} s per cell, Vector<float>.Count = {System.Numerics.Vector<float>.Count}, " +
                          $"{Environment.ProcessorCount} logical processors. MS/s of WIDE input, and the share of a core a 40 MS/s lease costs.");
        Console.WriteLine("  'single' = DecimatingFir(D) alone (what a lease filtered with before); 'cascade' = LeaseDecimator alone; 'nco' = the");
        Console.WriteLine("  sc16 conversion and the shift alone; 'lease' = nco + cascade, all of a lease's per-sample work (no ring read, no send).");
        foreach (int tpp in tpps)
        {
            Console.WriteLine();
            Console.WriteLine($"shaping filter {tpp} taps a phase:");
            foreach (int d in ds)
            {
                var single = Throughput(LeaseWork.SingleStage, d, tpp, 1, seconds).Mean;
                var cascade = Throughput(LeaseWork.Cascade, d, tpp, 1, seconds).Mean;
                var nco = Throughput(LeaseWork.NcoOnly, d, tpp, 1, seconds).Mean;
                var lease = Throughput(LeaseWork.NcoAndCascade, d, tpp, 1, seconds).Mean;
                Console.WriteLine($"  D {d,3}: single {F(single, "0").PadLeft(4)} MS/s = {Pct(single)}; cascade {F(cascade, "0").PadLeft(5)} MS/s = {Pct(cascade)}; " +
                                  $"nco {F(nco, "0").PadLeft(4)} MS/s = {Pct(nco)}; lease {F(lease, "0").PadLeft(4)} MS/s = {Pct(lease)}   {LeasePlan.For(d, tpp)}");
            }
        }
        if (depths)
        {
            // How deep the cascade should go: every depth the decimation allows, measured (the plan's cost model chooses).
            Console.WriteLine();
            Console.WriteLine("The cascade alone at every depth k (halfbands) the decimation allows, one thread:");
            foreach (int tpp in tpps)
                foreach (int d in ds)
                {
                    var chosen = LeasePlan.For(d, tpp);
                    for (int k = 0; ; k++)
                    {
                        var plan = LeasePlan.WithDepth(d, tpp, k);
                        if (plan == null) break;
                        var c = Throughput(LeaseWork.Cascade, d, tpp, 1, seconds, plan).Mean;
                        Console.WriteLine($"  D {d,3}, {tpp} taps, k {k}: {F(c, "0").PadLeft(5)} MS/s = {Pct(c)}  (model {F(plan.EstimatedCost, "0.00")})" +
                                          $"{(k == chosen.Halfbands.Count ? "  <- chosen" : "")}   {plan}");
                    }
                }
        }
        Console.WriteLine();
        foreach (int d in ds.Take(1).Concat(ds.Skip(ds.Length / 2).Take(1)).Distinct())
        {
            Console.WriteLine($"A whole lease's DSP (nco + cascade, D {d}, {tpps[0]} taps a phase) on 1..16 threads at once:");
            foreach (int threads in new[] { 1, 2, 4, 8, 12, 16, 20, 24 })
            {
                var (mean, slowest) = Throughput(LeaseWork.NcoAndCascade, d, tpps[0], threads, seconds);
                Console.WriteLine($"  {threads,2} threads: {F(mean, "0.0")} MS/s per thread (slowest {F(slowest, "0.0")}) = {Pct(slowest)} of a core per 40 MS/s lease, worst thread");
            }
        }
        if (!legacy) return 0;
        Console.WriteLine();
        Console.WriteLine($"DecimatingFir(50) ALONE (no NCO, no sc16 conversion, no ring read, no frame send), {F(seconds, "0")} s per row, " +
                          $"Vector<float>.Count = {System.Numerics.Vector<float>.Count}, {Environment.ProcessorCount} logical processors");
        foreach (int threads in new[] { 1, 2, 4, 8, 12, 16 })
        {
            var done = new long[threads];
            var go = new ManualResetEventSlim(false);
            var stop = false;
            var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
            {
                var fir = new DecimatingFir(50);
                var rnd = new Random(t);
                const int n = 8192;
                var re = new float[n];
                var im = new float[n];
                for (int k = 0; k < n; k++) { re[k] = (float)(rnd.NextDouble() - 0.5); im[k] = (float)(rnd.NextDouble() - 0.5); }
                var oRe = new float[n / 50 + 2];
                var oIm = new float[n / 50 + 2];
                go.Wait();
                long count = 0;
                while (!Volatile.Read(ref stop))
                {
                    fir.Process(re, im, n, oRe, oIm, 0);
                    count += n;
                }
                done[t] = count;
            }) { IsBackground = true, Priority = ThreadPriority.AboveNormal }).ToList();
            workers.ForEach(w => w.Start());
            Thread.Sleep(200);
            var sw = Stopwatch.StartNew();
            go.Set();
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            Volatile.Write(ref stop, true);
            workers.ForEach(w => w.Join());
            double el = sw.Elapsed.TotalSeconds;
            double perThread = done.Average() / el / 1e6, worst = done.Min() / el / 1e6;
            Console.WriteLine($"  {threads,2} threads: {F(perThread, "0.0")} MS/s per thread (slowest {F(worst, "0.0")}) = " +
                              $"{F(40.0 / worst * 100, "0.0")} % of a core per 40 MS/s lease, worst thread");
        }
        return 0;
    }

    // ------------------------------------------------------------------ --lease-cost

    private sealed class LeaseTally
    {
        public long Samples, Blocks, Breaks, Dropped;
        public ulong Next;
        public bool Have;
        public readonly object Lock = new();
    }

    private static int LeaseCost(string[] args)
    {
        int port = int.Parse(args[1], CultureInfo.InvariantCulture), pid = 0;
        double seconds = 10;
        int[] counts = { 0, 1, 2, 4, 8 };
        for (int i = 2; i + 1 < args.Length; i += 2)
        {
            if (args[i] == "--pid") pid = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            else if (args[i] == "--seconds") seconds = double.Parse(args[i + 1], CultureInfo.InvariantCulture);
            else if (args[i] == "--counts") counts = args[i + 1].Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        }
        try
        {
            using var s = UsrpSession.Open(UsrpAddress.Remote("127.0.0.1", port), new UsrpSessionOptions { ClientName = "lease-cost", RequestTimeoutMs = 60000 });
            string ready = s.WaitReady(30000);
            var st = s.GetState();
            double wide = st.WideRateHz;
            long lo = st.LoHz;
            using var proc = Process.GetProcessById(pid);
            Console.WriteLine($"{s.Radio.Product} {s.Radio.Serial} ({s.Radio.Source}): {ready}, LO {lo} Hz, {F(wide / 1e6, "0.###")} MS/s, " +
                              $"max_leases {s.Caps.MaxLeases}; sidecar pid {pid}; {F(seconds, "0")} s a row after 2 s settling");
            Console.WriteLine("  HF block centred on the LO at " + F(HfBlockCentreHz / 1e6, "0.00") + " MHz RF; each lease the whole band in its clean " +
                              F(LeaseCleanFraction * 100, "0") + " %");
            double baseCores = double.NaN;
            foreach (int n in counts)
            {
                var open = new List<(LeaseStream Lease, LeaseTally Tally, string Band, int D)>();
                string refused = "";
                for (int i = 0; i < n; i++)
                {
                    // The ten bands first; beyond them, sub-receivers at ShackLink's preferred 400 kHz lease, a megahertz
                    // apart across the span (section 26.8: ten bands + channel 0 + sub-receivers).
                    var b = i < HfBands.Length ? HfBands[i] : (Name: $"sub{i - HfBands.Length + 1}", Lo: 0.0, Hi: 0.0);
                    int d = i < HfBands.Length ? BandDecimation(wide, b.Hi - b.Lo) : (int)Math.Round(wide / 400e3);
                    long centre = i < HfBands.Length
                        ? lo + (long)Math.Round((b.Lo + b.Hi) / 2 - HfBlockCentreHz)
                        : lo + (long)((i - HfBands.Length) * 1_000_000L - 3_500_000L);
                    try
                    {
                        var l = s.OpenLease(new LeaseRequest { CenterHz = centre, Decimation = d });
                        var t = new LeaseTally();
                        l.Connection.IqBlockReceived += (_, h, _) =>
                        {
                            lock (t.Lock)
                            {
                                if (t.Have && h.FirstSample != t.Next) t.Breaks++;
                                t.Next = h.FirstSample + (ulong)Math.Max(0, h.Count);
                                t.Have = true;
                                t.Samples += h.Count;
                                t.Blocks++;
                                t.Dropped += h.Dropped;
                            }
                        };
                        l.Connection.StartReader($"lease-cost-{b.Name}");
                        open.Add((l, t, b.Name, d));
                    }
                    catch (UsrpRequestException ex)
                    {
                        refused = $"lease {i + 1} ({b.Name}) refused: {ex.Code}: {ex.Message}";
                        break;
                    }
                }
                Thread.Sleep(2000);
                long Tel(JsonObject? o, string key) => o?.GetLong(key) ?? 0;
                var tel0 = s.GetTelemetry().GetObject("radio");
                var tallies0 = open.Select(o => { lock (o.Tally.Lock) return (o.Tally.Samples, o.Tally.Breaks, o.Tally.Dropped); }).ToArray();
                proc.Refresh();
                var cpu0 = proc.TotalProcessorTime;
                var sw = Stopwatch.StartNew();
                Thread.Sleep(TimeSpan.FromSeconds(seconds));
                proc.Refresh();
                var cpu1 = proc.TotalProcessorTime;
                double el = sw.Elapsed.TotalSeconds;
                var tel1 = s.GetTelemetry().GetObject("radio");
                double cores = (cpu1 - cpu0).TotalSeconds / el;
                if (n == 0) baseCores = cores;
                long qd = Tel(tel1?.GetObject("source"), "queue_dropped") - Tel(tel0?.GetObject("source"), "queue_dropped");
                long sm = Tel(tel1?.GetObject("source"), "seq_missing") - Tel(tel0?.GetObject("source"), "seq_missing");
                long ov = Tel(tel1, "overflows") - Tel(tel0, "overflows");
                long WideMissed(JsonObject? r) => r?.GetArray("pipelines")?.OfType<JsonObject>().Where(p => p.GetString("kind") == "lease")
                                                      .Sum(p => p.GetLong("wide_missed")) ?? 0;
                long wm = WideMissed(tel1) - WideMissed(tel0);
                string perLease = n == 0 || double.IsNaN(baseCores) ? "" : $", {F((cores - baseCores) / n * 100, "0.0")} % of a core per lease over the 0-lease row";
                Console.WriteLine($"  {n,2} leases: sidecar {F(cores * 100, "0")} % of a core{perLease}; source: queue dropped {qd}, seq missing {sm}, " +
                                  $"overflows {ov}; leases' wide_missed {wm}");
                for (int i = 0; i < open.Count; i++)
                {
                    var (l, t, band, d) = open[i];
                    long sNow, brNow, drNow;
                    lock (t.Lock) { sNow = t.Samples; brNow = t.Breaks; drNow = t.Dropped; }
                    double got = (sNow - tallies0[i].Samples) / el, want = wide / d;
                    Console.WriteLine($"      {band,-4} D {d,3} = {F(want / 1e3, "0.0").PadLeft(7)} kS/s: received {F(got / want * 100, "0.00")} %, " +
                                      $"breaks {brNow - tallies0[i].Breaks}, dropped {drNow - tallies0[i].Dropped}");
                }
                if (refused.Length > 0) Console.WriteLine("      " + refused);
                foreach (var o in open) o.Lease.Dispose();
                Thread.Sleep(1000);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"lease-cost: {ex.GetType().Name}: {ex.Message}");
        }
        return 0;
    }
}
