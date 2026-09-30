// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// P4b (ShackLink docs/usrp_b210_support.md section 9.17 P4-b, P4-f): the sidecar's transmitter. Every
// case that transmits runs against the synthetic radio, which TRANSMITS INTO ITSELF, and says so: the
// radio states source = sim before anything is keyed, and neither this process nor the spawned sidecar
// has uhd.dll loaded. Nothing here radiates, anywhere.

using System.Diagnostics;
using System.Globalization;
using ShackLinkUsrp.Client;
using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Host;
using ShackLinkUsrp.Radio;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Uhd;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Tests;

internal static partial class Program
{
    /// <summary>One panorama bin at 2 MS/s over 4096 bins is 488.28125 Hz; 32 of them are 15,625 Hz, the
    /// smallest whole-hertz step that lands a frequency ON a bin - so a level reads without Hann's
    /// scalloping. Every frequency the loopback cases probe is a multiple of it from the LO.</summary>
    private const long OnBin = 15_625;

    // ---------------------------------------------------------------- DSP

    private static void ToneGeneratorCases()
    {
        const double rate = 200_000;
        double offset = 400 * rate / 4096;                 // bin +400, exactly
        var g = new ToneGenerator(offset, rate, -10);
        var iq = new short[4096 * 2];
        g.Fill(iq, 4096);
        var acc = new SpectrumAccumulator(4096, WindowShape.Hann, Integration.Average);
        acc.AddFrame(iq);
        var row = new short[4096];
        acc.TakeRow(0, 4096, row);
        int peak = Array.IndexOf(row, row.Max());
        Check(peak == 2048 + 400 && Math.Abs(row[peak] / 100.0 + 10) < 0.05,
              $"a -10 dBFS tone at +400 bins reads {F(row[peak] / 100.0)} dBFS at shifted index {peak}");
        double mag = 0;
        for (int i = 0; i < 4096; i++) mag += Math.Sqrt((double)iq[2 * i] * iq[2 * i] + (double)iq[2 * i + 1] * iq[2 * i + 1]);
        Check(Math.Abs(20 * Math.Log10(mag / 4096 / 32767) + 10) < 0.01, "its magnitude is its level: |I + jQ| = 32767 x 10^(L/20)");

        var one = new ToneGenerator(12_345, rate, -3);
        var ten = new ToneGenerator(12_345, rate, -3);
        var a = new short[2000];
        var b = new short[2000];
        one.Fill(a, 1000);
        for (int k = 0; k < 10; k++) ten.Fill(b.AsSpan(k * 200), 100);
        Check(a.SequenceEqual(b), "phase-continuous: ten calls of 100 are the same samples as one of 1000");

        var full = new ToneGenerator(offset, rate, 0);
        var fs = new short[200_000];
        full.Fill(fs, 100_000);
        Check(fs.All(v => v >= -32767 && v <= 32767) && fs.Max() >= 32760,
              "at 0 dBFS every sample stays inside full scale and none wraps to -32768");
        Check(Throws(() => new ToneGenerator(0, rate, 0.1)), "a carrier above full scale is refused, never clipped");
        Check(Throws(() => new ToneGenerator(100_000, rate, -10)), "a carrier outside the rate's Nyquist band is refused");
    }

    /// <summary>A tone at 0.3 of the TX rate - near the edge TxUsableFraction allows - raised to each
    /// output rate: it must arrive at its level and frequency, and the images a plain interpolator would
    /// leave (signals nobody sent) must be far down.</summary>
    private static void TxInterpolatorCases()
    {
        foreach (var (tx, outRate, l) in new[] { (200e3, 2e6, 8), (200e3, 10e6, 8), (250e3, 1e6, 4), (2e6, 2e6, 1) })
        {
            var interp = new TxInterpolator(tx, outRate);
            const int n = 8192;
            int bin = (int)Math.Round(0.3 * tx / outRate * n);
            double f = bin * outRate / n;
            var gen = new ToneGenerator(f, tx, -6);
            var inBuf = new short[2 * 100_000];
            var re = new double[n];
            var im = new double[n];
            var sc16 = new short[2 * n];
            var acc = new SpectrumAccumulator(n, WindowShape.BlackmanHarris, Integration.Average);
            for (int block = 0; block < 6; block++)
            {
                int need = interp.InputNeeded(n);
                gen.Fill(inBuf, need);
                interp.Push(inBuf, need);
                interp.Produce(n, re, im);
                if (block == 0) continue; // the FIR fills
                for (int i = 0; i < n; i++)
                {
                    sc16[2 * i] = (short)Math.Round(re[i] * 32768);
                    sc16[2 * i + 1] = (short)Math.Round(im[i] * 32768);
                }
                acc.AddFrame(sc16);
            }
            var row = new short[n];
            acc.TakeRow(0, n, row);
            double level = row.Skip(n / 2 + bin - 2).Take(5).Max() / 100.0;
            double spur = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
                if (Math.Abs(i - (n / 2 + bin)) > 12) spur = Math.Max(spur, row[i] / 100.0);
            Check(interp.Upsampling == l && Math.Abs(level + 6) < 0.1 && spur - level < -60,
                  $"{F(tx / 1e3, "0")} kS/s -> {F(outRate / 1e6, "0.##")} MS/s (L = {interp.Upsampling}): a -6 dBFS tone at 0.3 of the TX rate reads {F(level)} dBFS " +
                  $"at its frequency, and the strongest thing anywhere else is {F(spur - level, "0.0")} dBc");
        }
    }

    /// <summary>
    /// Section 9.17 items 4 and 5 against the master clocks 9.8 measured on the board - 32 MHz at 2 MS/s,
    /// 40 at 10, 20 at 20: the synthetic radio's model of UHD's automatic clock must pick those, and every
    /// rate admitted at any offered wide rate divides it, is tick / n exactly with n even (or 1), is a
    /// whole number of hertz, and lies in the offered band.
    /// </summary>
    private static void TxRatePlanCases()
    {
        Check(TxRatePlan.ModelTickHz(2e6) == 32e6 && TxRatePlan.ModelTickHz(10e6) == 40e6 && TxRatePlan.ModelTickHz(20e6) == 20e6,
              $"UHD's automatic clock, ported: 2 MS/s -> {F(TxRatePlan.ModelTickHz(2e6) / 1e6, "0.##")} MHz, 10 -> {F(TxRatePlan.ModelTickHz(10e6) / 1e6, "0.##")}, " +
              $"20 -> {F(TxRatePlan.ModelTickHz(20e6) / 1e6, "0.##")} (the clocks 9.8 measured: 32, 40, 20)");
        double[] Plan(double wide) => TxRatePlan.Admitted(TxRatePlan.DucHostRates(TxRatePlan.ModelTickHz(wide)), TxRatePlan.ModelTickHz(wide), wide);
        var at2 = Plan(2e6);
        Check(at2.SequenceEqual(new[] { 200e3, 250e3, 400e3, 500e3, 1e6, 2e6 }),
              $"at 2 MS/s the admitted TX rates are {string.Join(", ", at2.Select(r => F(r / 1e3, "0.#")))} kS/s");
        var at20 = Plan(20e6);
        Check(!at20.Contains(800e3) && at20.Contains(1e6) && at20.Contains(400e3),
              "at 20 MS/s (a 20 MHz clock) 800 kS/s is left out - n = 25 is odd, the CIC alone - while 1 MS/s (n = 20) and 400 kS/s (n = 50) are in");
        Check(!Plan(10e6).Any(r => Math.Abs(r - 10e6 / 6) < 1),
              "a rate that is not a whole number of hertz (10 MS/s / 6) is left out: UHD's LCM rounds it and would move the clock");
        bool ruled = true;
        string why = "";
        foreach (double wide in new[] { 2e6, 5e6, 10e6, 20e6 })
        {
            double tick = TxRatePlan.ModelTickHz(wide);
            var plan = Plan(wide);
            if (!plan.Contains(200e3)) { ruled = false; why = $"200 kS/s is not admitted at {wide}"; }
            foreach (double r in plan)
            {
                double n = tick / r;
                if (!TxRules.DividesWideRate(r, wide) || r != Math.Round(r) || n != Math.Round(n) || (n != 1 && n % 2 != 0) ||
                    r < TxRatePlan.MinTxRateHz || r > TxRatePlan.MaxTxRateHz)
                {
                    ruled = false;
                    why = $"{r} at {wide} (n = {n})";
                }
            }
        }
        Check(ruled, "at 2, 5, 10 and 20 MS/s every admitted rate divides the wide rate, is tick / n exactly with n even or 1, is whole hertz, lies " +
                     "in 200 kS/s - 2 MS/s, and 200 kS/s is admitted at all four (section 9.17 item 5)" + (why.Length > 0 ? ": " + why : ""));
        Check(TxRatePlan.DspReachHz(32e6, 200e3) == 0.4 * (32e6 - 200e3),
              $"the DUC's reach at a 32 MHz clock for a 200 kS/s transmission: {F(TxRatePlan.DspReachHz(32e6, 200e3) / 1e6, "0.00")} MHz");
        Check(TxRatePlan.QueueSamplesFor(200e3) == ProtocolConstants.MaxIqBlockSamples && TxRatePlan.QueueSamplesFor(2e6) == 100_000,
              "a transmission's queue: one largest block at 200 kS/s, 50 ms at 2 MS/s");
    }

    private static byte[] Indexed(int from, int count)
    {
        var b = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            BitConverter.TryWriteBytes(b.AsSpan(4 * i), (short)(from + i));
            BitConverter.TryWriteBytes(b.AsSpan(4 * i + 2), (short)-(from + i));
        }
        return b;
    }

    private static void TxQueueCases()
    {
        var q = new TxSampleQueue(1000);
        Check(q.Write(Indexed(0, 600), 600), "a write that fits is taken at once");
        var blocked = Task.Run(() => q.Write(Indexed(600, 600), 600));
        Thread.Sleep(150);
        Check(!blocked.IsCompleted, "a write that does not fit BLOCKS - the pacing");
        var got = new short[2 * 1200];
        int n = q.Read(got, 300, 0);
        Check(n == 300 && blocked.Wait(1000) && blocked.Result, "...until the radio side takes enough, then it completes");
        int m = q.Read(got.AsSpan(600), 900, 0);
        bool inOrder = n + m == 1200;
        for (int i = 0; i < n + m && inOrder; i++) inOrder = got[2 * i] == (short)i && got[2 * i + 1] == (short)-i;
        Check(inOrder, "every sample comes out once, in order, across the ring's wrap");
        Check(q.Read(got, 10, 50) == 0, "an empty queue reads nothing after its wait");

        q.Write(Indexed(0, 100), 100);
        q.Close();
        Check(!q.Write(Indexed(0, 10), 10), "a drain closes it to writers");
        Check(!q.IsDrained && q.Read(got, 1000, 0) == 100 && q.IsDrained, "...and the reader still takes what was queued, then it is drained");

        var a = new TxSampleQueue(100);
        a.Write(Indexed(0, 100), 100);
        var waiting = Task.Run(() => a.Write(Indexed(0, 50), 50));
        Thread.Sleep(100);
        a.Abort();
        Check(waiting.Wait(1000) && !waiting.Result && a.Count == 0, "an abort discards the queue and wakes a blocked writer, which is told no");
        Check(Throws(() => new TxSampleQueue(10).Write(Indexed(0, 11), 11)), "a block larger than the whole queue is refused in words");
    }

    private static bool UhdDllIn(Process p)
    {
        try
        {
            p.Refresh();
            return p.Modules.Cast<ProcessModule>().Any(m => string.Equals(m.ModuleName, "uhd.dll", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return true; // unreadable is not proof of absence
        }
    }

    private static void NoUhdInThisProcess()
    {
        Check(!UhdLoader.IsLoaded && !UhdDllIn(Process.GetCurrentProcess()),
              "this gate has not loaded uhd.dll: every transmitting case below runs on the synthetic radio, which radiates nothing");
    }

    // ---------------------------------------------------------------- the synthetic radio transmits into itself

    private static (SimBackend backend, UsrpServer server, SidecarLog log) StartSimTx(string name, double rateHz = 2e6, int starveMs = 500,
                                                                                    int faultAfterMs = 0, double couplingDb = SimBackend.DefaultTxCouplingDb)
    {
        var log = new SidecarLog(null, console: false);
        var backend = new SimBackend(log, name, rateHz) { FaultAfterMs = faultAfterMs, TxCouplingDb = couplingDb };
        var server = new UsrpServer(backend, new UsrpServerOptions { Port = 0, SidecarVersion = "test", LingerSeconds = 60, TxStarveMs = starveMs });
        server.Start();
        backend.Start();
        return (backend, server, log);
    }

    /// <summary>A panorama over the whole 2 MS/s span whose rows record the level at each probe frequency.</summary>
    private static (PanoramaStream pano, List<(RowHeader h, double[] levels)> rows) ProbeRows(UsrpSession s, params long[] probes)
    {
        var rows = new List<(RowHeader h, double[] levels)>();
        var pano = s.OpenPanorama(new PanoramaRequest { Bins = 4096, Fps = 10, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
        pano.Connection.RowReceived += (c, h, bins) =>
        {
            var levels = probes.Select(p => LevelAt(h, bins, p, 2e6)).ToArray();
            lock (rows) rows.Add((h, levels));
        };
        pano.Connection.StartReader();
        return (pano, rows);
    }

    private static List<(RowHeader h, double[] levels)> Snapshot(List<(RowHeader h, double[] levels)> rows)
    {
        lock (rows) return rows.ToList();
    }

    private static List<(string name, JsonObject body)> TxEvents(UsrpSession s, out EventStream ev)
    {
        var events = new List<(string, JsonObject)>();
        ev = s.OpenEvents();
        ev.Connection.EventReceived += (c, n, body) =>
        {
            if (n == EventNames.Tx || n == EventNames.TxUnderflow || n == EventNames.TxStep) lock (events) events.Add((n, body));
        };
        ev.Connection.StartReader();
        return events;
    }

    private static void SimTxCaps()
    {
        var (backend, server, log) = StartSimTx("txcaps");
        try
        {
            using var s = Connect(server, "txcaps");
            s.WaitReady(5000);
            var tc = s.Caps.Tx;
            Check(s.Radio.Source == RadioSources.Sim && tc != null, $"the synthetic radio states a transmitter (caps.tx) and that it is synthetic ({s.Radio.Source})");
            Check(tc!.Sources.SequenceEqual(new[] { TxSourceKinds.Iq, TxSourceKinds.Tone, TxSourceKinds.Mod, TxSourceKinds.Step }) &&
                  tc.Modulations.SequenceEqual(new[] { TxWords.Am, TxWords.Fm }) && tc.ModulatingWaveforms.SequenceEqual(TxWaveforms.All) &&
                  tc.ModulatingHarmonics == ProtocolConstants.TxModulatingHarmonics,
                  $"it builds {string.Join(", ", tc.Sources)}; it modulates {string.Join(" and ", tc.Modulations)} by {string.Join(", ", tc.ModulatingWaveforms)} " +
                  $"({tc.ModulatingHarmonics} harmonics)");
            Check(tc.MinDwellMs == ProtocolConstants.TxMinDwellMs && tc.MinDwellMs == 5 && tc.MaxStepPoints == 8192,
                  $"a step lasts at least {tc.MinDwellMs} ms and a sweep has at most {tc.MaxStepPoints} points (the RF Bench Analyser's trace budget)");
            Check(tc.RatesHz.SequenceEqual(new[] { 200e3, 250e3, 400e3, 500e3, 1e6, 2e6 }) && tc.GainMinDb == 0 && tc.GainMaxDb == 89.75 && tc.GainStepDb == 0.25,
                  $"at 2 MS/s: TX rates {string.Join("/", tc.RatesHz.Select(r => F(r / 1e3, "0")))} kS/s, gain {F(tc.GainMinDb, "0.##")}-{F(tc.GainMaxDb, "0.##")} in {F(tc.GainStepDb, "0.##")} dB (the B210's)");
            Check(tc.MaxMs == ProtocolConstants.DefaultTxMaxMs && tc.QuantumSamples == 0 && tc.MaxLoOffsetHz == (long)(0.4 * (32e6 - 2e6)),
                  $"max_ms {tc.MaxMs} (the server's ceiling), quantum {tc.QuantumSamples} (not stated: no transport packet), max LO offset {tc.MaxLoOffsetHz} Hz");
            var tx = s.GetState().Tx;
            Check(tx != null && tx.CenterHz == 100_000_000 && tx.RateHz == 200e3 && tx.GainDb == 0 && tx.Antenna == "TX/RX" && !tx.Keyed && tx.Owner == null,
                  $"state.tx as a B210 comes up: {tx?.CenterHz} Hz, {tx?.RateHz} S/s, {tx?.GainDb} dB, {tx?.Antenna}, unkeyed, nobody's");

            s.ClaimLo();
            s.ClaimTx();
            UsrpRequestException? refused = null;
            try { s.SetTx(new TxSettings { RateHz = 312_500 }); }
            catch (UsrpRequestException ex) { refused = ex; }
            Check(refused?.Code == ErrorCodes.OutOfRange, $"312.5 kS/s is refused at 2 MS/s: it does not divide it ({refused?.Code})");
            var reply = s.SetTx(new TxSettings { RateHz = 250_000, GainDb = 40.3 });
            Check(reply.State.Tx!.RateHz == 250_000 && reply.State.Tx.GainDb == 40.25, $"set_tx reads back: 250 kS/s, and 40.3 dB on the 0.25 dB grid as {F(reply.State.Tx.GainDb, "0.##")}");

            s.SetRate(10e6);
            s.WaitReady(5000);
            Check(s.Caps.Tx!.RatesHz.Contains(312_500) && s.Caps.Tx.RatesHz.Contains(1.25e6),
                  $"at 10 MS/s (a 40 MHz clock) the admitted rates change with it: {string.Join("/", s.Caps.Tx.RatesHz.Select(r => F(r / 1e3, "0.#")))} kS/s");
            s.SetTx(new TxSettings { RateHz = 312_500 });
            s.SetRate(2e6);
            var moved = s.GetState().Tx!;
            Check(moved.RateHz == 200e3, $"a TX rate that no longer divides the new wide rate is moved to the lowest the new one admits: 312.5 kS/s -> {moved.RateHz} S/s");
        }
        finally
        {
            server.Dispose();
            backend.Dispose();
            log.Dispose();
        }
    }

    /// <summary>
    /// A tone into the synthetic radio: it lands in its own panorama at the carrier and at L + G + coupling,
    /// with no row integrating across its key-up or key-down; it parks by the DUC inside its reach and by the
    /// synthesizer beyond it; it runs at the lowest admitted rate and puts back the rate a client set.
    /// </summary>
    private static void SimToneLoopback()
    {
        var (backend, server, log) = StartSimTx("tone");
        try
        {
            using var s = Connect(server, "tone");
            s.WaitReady(5000);
            Check(s.Radio.Source == RadioSources.Sim, "before anything is keyed, the radio says it is synthetic");
            s.ClaimTx();
            long center = 100_000_000 + 20 * OnBin, carrier = center + OnBin;
            long parked = 100_000_000 + 32 * OnBin, parkedCarrier = parked + OnBin;
            s.SetTx(new TxSettings { CenterHz = center, GainDb = 50, RateHz = 250_000 });
            // The third probe is where a carrier 20.015625 MHz up would FOLD to in a 2 MS/s span, were the
            // synthetic radio to add a transmission its receiver cannot hold.
            long folded = 100_000_000 + OnBin;
            var (pano, rows) = ProbeRows(s, carrier, parkedCarrier, folded);
            var events = TxEvents(s, out var ev);
            Thread.Sleep(500);

            // One key-up and key-down, nothing else: every row either side of each.
            int before = Snapshot(rows).Count;
            var t = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone, MaxMs = 20_000, OffsetHz = OnBin, LevelDbfs = -10 });
            Check(t.Grant.Source == TxSourceKinds.Tone && t.Grant.RateHz == 200e3 && t.Grant.CenterHz == carrier && t.Grant.Stream == 0 && t.Grant.LevelDbfs == -10,
                  $"the tone's grant: {t.Grant.RateHz} S/s (the lowest admitted), its carrier at {t.Grant.CenterHz} Hz, {t.Grant.LevelDbfs} dBFS, no stream");
            var during = s.GetState().Tx!;
            Check(during.Keyed && during.Source == TxSourceKinds.Tone && during.RateHz == 200e3,
                  $"state.tx while it runs: keyed, source {during.Source}, the radio at {during.RateHz} S/s");
            Thread.Sleep(900);
            var r1 = t.Stop(drain: true);
            Check(r1.WasOpen && r1.Acked && r1.Reason == EndReasons.Drained && r1.Samples > 150_000 && r1.OnMs >= 800,
                  $"drained, and the radio's word that it ended: {r1}");
            Thread.Sleep(500);
            var cycle = Snapshot(rows).Skip(before).Where(r => r.h.Ffts > 0).ToList();
            double want = -10 + 50 + SimBackend.DefaultTxCouplingDb;
            var on = cycle.Where(r => Math.Abs(r.levels[0] - want) < 1).ToList();
            var off = cycle.Where(r => r.levels[0] < -70).ToList();
            var straddled = cycle.Except(on).Except(off).ToList();
            Check(on.Count >= 5 && off.Count >= 3,
                  $"heard at its carrier: {on.Count} rows read {F(on.Select(r => r.levels[0]).DefaultIfEmpty(double.NaN).Average())} dBFS " +
                  $"(-10 dBFS at 50 dB, coupling {SimBackend.DefaultTxCouplingDb} dB: {F(want)}); {off.Count} rows either side read the floor");
            Check(straddled.Count == 0,
                  $"no row integrates across the key-up or the key-down (both announced as front-end changes): {straddled.Count} of {cycle.Count} rows read in between" +
                  (straddled.Count > 0 ? $" - row {straddled[0].h.Seq} reads {F(straddled[0].levels[0])} dBFS over {straddled[0].h.Ffts} FFTs" : ""));
            Check(s.GetState().Tx!.RateHz == 250_000,
                  $"after it, the rate the client set is put back: {s.GetState().Tx!.RateHz} S/s");
            List<(string name, JsonObject body)> seen;
            lock (events) seen = events.ToList();
            var keyed = seen.FirstOrDefault(e => e.name == EventNames.Tx && e.body.GetString("state") == TxStates.Keyed).body;
            var idle = seen.FirstOrDefault(e => e.name == EventNames.Tx && e.body.GetString("state") == TxStates.Idle).body;
            Check(keyed != null && idle != null && idle.GetString("reason") == EndReasons.Drained && idle.GetBool("acked") &&
                  idle.GetLong("time_ticks") > keyed.GetLong("time_ticks"),
                  "every client is told: tx keyed, then tx idle (drained, acked), stamped on the radio's clock");

            // The park.
            var t2 = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone, MaxMs = 20_000, OffsetHz = OnBin, LevelDbfs = -10 });
            Thread.Sleep(500);
            long ticksBefore = SidecarClock.Ticks;
            var p1 = s.SetTx(new TxSettings { CenterHz = parked });
            Check(p1.Retune == TxWords.RetuneDsp && p1.TimeTicks >= ticksBefore && p1.State.Tx!.CenterHz == parked && p1.State.Tx.LoHz == center,
                  $"a park {F((parked - center) / 1e3, "0.0")} kHz away is the DUC's ({p1.Retune}), from time_ticks {p1.TimeTicks}; the LO stays at {p1.State.Tx!.LoHz} Hz");
            Thread.Sleep(700);
            var last = Snapshot(rows)[^1];
            Check(Math.Abs(last.levels[1] - want) < 1 && last.levels[0] < -70,
                  $"...and the carrier stands at {parkedCarrier} Hz ({F(last.levels[1])} dBFS), gone from {carrier} Hz ({F(last.levels[0])})");
            long telCenter = s.GetTelemetry().GetObject("radio")?.GetObject("tx")?.GetLong("center_hz") ?? 0;
            Check(telCenter == parkedCarrier, $"the sidecar's grant follows the park: its telemetry puts the carrier at {telCenter} Hz");
            var p2 = s.SetTx(new TxSettings { CenterHz = 120_000_000 });
            Check(p2.Retune == TxWords.RetuneRf && p2.State.Tx!.LoHz == 120_000_000,
                  $"a park 19.5 MHz away is beyond the DUC's reach at a 32 MHz clock ({F(TxRatePlan.DspReachHz(32e6, 200e3) / 1e6, "0.00")} MHz): the synthesizer's ({p2.Retune}), LO {p2.State.Tx!.LoHz} Hz");
            Thread.Sleep(400);
            last = Snapshot(rows)[^1];
            Check(last.levels[0] < -70 && last.levels[1] < -70 && last.levels[2] < -70,
                  $"...and nothing is heard in a receiver tuned 20 MHz away - not at the old carriers, nor folded back to {folded} Hz ({F(last.levels[2])} dBFS)");
            var r2 = t2.Stop(drain: false);
            Check(r2.Reason == EndReasons.Stopped && !s.GetState().Tx!.Keyed, $"an abort ends it where it stands: {r2}");
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
    /// An IQ transmission into the synthetic radio: nothing is keyed before its first block; one second of
    /// samples takes about one second to go out, paced by the radio's clock with no timer; it is heard at its
    /// frequency and level; the drain ends on the radio's word with every sample sent; a pause shorter than
    /// the starvation limit is an underflow, counted and said.
    /// </summary>
    private static void SimIqLoopback()
    {
        var (backend, server, log) = StartSimTx("iq");
        try
        {
            using var s = Connect(server, "iq");
            s.WaitReady(5000);
            s.ClaimTx();
            long center = 100_000_000 + 13 * OnBin, carrier = center + OnBin;
            s.SetTx(new TxSettings { CenterHz = center, RateHz = 250_000, GainDb = 40 });
            var (pano, rows) = ProbeRows(s, carrier);
            var events = TxEvents(s, out var ev);
            Thread.Sleep(300);

            var t = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 30_000 });
            Check(t.Stream != null && t.Grant.RateHz == 250_000 && t.Grant.CenterHz == center && t.Grant.QueueSamples == ProtocolConstants.MaxIqBlockSamples,
                  $"the grant: a stream, {t.Grant.RateHz} S/s at {t.Grant.CenterHz} Hz, a queue of {t.Grant.QueueSamples} samples");
            Thread.Sleep(300);
            Check(!s.GetState().Tx!.Keyed && Snapshot(rows)[^1].levels[0] < -70, "nothing is keyed, and nothing heard, before the first block");

            const int total = 250_000;
            var iq = new short[2 * total];
            new ToneGenerator(OnBin, 250_000, -6).Fill(iq, total);
            var sw = Stopwatch.StartNew();
            double midLevel = double.NaN;
            var probe = Task.Run(() =>
            {
                Thread.Sleep(700);
                midLevel = Snapshot(rows)[^1].levels[0];
            });
            t.Stream!.Write(iq, 0, total);
            double handed = sw.Elapsed.TotalSeconds;
            var r = t.Stop(drain: true);
            double wentOut = sw.Elapsed.TotalSeconds;
            probe.Wait(2000);
            Check(r.Acked && r.Reason == EndReasons.Drained && r.Samples == total && r.Underflows == 0,
                  $"drained on the radio's word, every sample sent: {r}");
            Check(wentOut > 0.93 && wentOut < 1.25 && handed > 0.6,
                  $"one second of samples at 250 kS/s went out in {F(wentOut, "0.000")} s, taken by the synthetic radio's clock - the writer was held " +
                  $"{F(handed, "0.000")} s by the full queue alone, no timer anywhere");
            double want = -6 + 40 + SimBackend.DefaultTxCouplingDb;
            Check(Math.Abs(midLevel - want) < 1, $"heard while it ran at {carrier} Hz: {F(midLevel)} dBFS (-6 dBFS at 40 dB, {SimBackend.DefaultTxCouplingDb} dB coupling: {F(want)})");

            // An underflow: a pause shorter than the starvation limit.
            var u = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 30_000 });
            u.Stream!.Write(iq, 0, 12_500);
            Thread.Sleep(250);
            u.Stream.Write(iq, 25_000, 12_500);
            var ru = u.Stop(drain: true);
            Thread.Sleep(1100); // the server says an underflow at most once a second
            bool said;
            lock (events) said = events.Any(e => e.name == EventNames.TxUnderflow && e.body.GetLong("count") >= 1);
            Check(ru.Acked && ru.Samples == 25_000 && ru.Underflows == 1 && said,
                  $"a 250 ms gap in the feed is one underflow, counted ({ru.Underflows}) and said on the event feed ({said}); the rest still drained: {ru}");

            // A drain with nothing sent.
            var e0 = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 30_000 });
            var r0 = e0.Stop(drain: true);
            Check(!r0.Acked && r0.Samples == 0 && r0.Text.Contains("no sample was transmitted"),
                  $"a drain of a transmission that sent nothing is not an acknowledgement: {r0}");
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

    private static bool WaitIdle(List<(string name, JsonObject body)> events, string reason, int ms) =>
        WaitFor(() =>
        {
            lock (events) return events.Any(e => e.name == EventNames.Tx && e.body.GetString("state") == TxStates.Idle && e.body.GetString("reason") == reason);
        }, ms);

    /// <summary>Every other way a transmission ends, each letting go of the synthetic transmitter - which is
    /// then free, unkeyed and silent - and each said to every client.</summary>
    private static void SimTxEnds()
    {
        var (backend, server, log) = StartSimTx("ends", starveMs: 300);
        try
        {
            using var s = Connect(server, "ends");
            using var watcher = Connect(server, "watcher");
            s.WaitReady(5000);
            s.ClaimTx();
            s.SetTx(new TxSettings { CenterHz = 100_000_000 + 13 * OnBin, RateHz = 250_000, GainDb = 40 });
            var events = TxEvents(watcher, out var ev);
            var iq = new short[2 * 250_000];
            new ToneGenerator(OnBin, 250_000, -6).Fill(iq, 250_000);

            // An abort while a writer is held by the full queue.
            var t = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 30_000 });
            Exception? writerSaw = null;
            var writer = Task.Run(() =>
            {
                try { for (int k = 0; k < 20; k++) t.Stream!.Write(iq, 0, 250_000); }
                catch (Exception ex) { writerSaw = ex; }
            });
            Thread.Sleep(400);
            var sw = Stopwatch.StartNew();
            var ra = s.StopTx(drain: false);
            bool unblocked = writer.Wait(3000);
            Check(ra.Reason == EndReasons.Stopped && !ra.Acked && sw.ElapsedMilliseconds < 1000 && unblocked && writerSaw is IOException,
                  $"an abort ends it at once ({sw.ElapsedMilliseconds} ms) and the held writer learns it ({writerSaw?.GetType().Name}): {ra}");
            t.Dispose();
            Check(!s.GetState().Tx!.Keyed && backend.GetTelemetry().GetObject("tx")!.GetBool("open") == false, "...the transmitter is let go");

            // Starvation: a keyed IQ transmission fed nothing.
            var st = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 30_000 });
            st.Stream!.Write(iq, 0, 25_000);
            sw.Restart();
            Check(WaitIdle(events, EndReasons.Starved, 3000) && sw.ElapsedMilliseconds < 1500,
                  $"fed nothing for its 300 ms starvation limit, it is ended rather than left keyed ({sw.ElapsedMilliseconds} ms after its last block)");
            Check(WaitFor(() => st.Stream.End?.Reason == EndReasons.Starved, 2000), $"...and its stream says why ({st.Stream.End?.Reason})");
            st.Dispose();

            // The cap.
            sw.Restart();
            var c = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone, MaxMs = 400, OffsetHz = OnBin, LevelDbfs = -10 });
            Check(WaitIdle(events, EndReasons.Cap, 3000) && sw.ElapsedMilliseconds < 1200,
                  $"a tone with max_ms 400 is ended by the sidecar ({sw.ElapsedMilliseconds} ms)");
            c.Dispose();

            // Released.
            var rel = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone, MaxMs = 30_000, OffsetHz = OnBin, LevelDbfs = -10 });
            s.ReleaseTx();
            Check(WaitIdle(events, EndReasons.Released, 2000) && s.GetState().Tx!.Owner == null, "releasing the transmitter ends its transmission");
            rel.Dispose();

            // Its client gone.
            var gone = Connect(server, "gone");
            gone.ClaimTx();
            gone.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone, MaxMs = 30_000, OffsetHz = OnBin, LevelDbfs = -10 });
            Thread.Sleep(200);
            sw.Restart();
            gone.Control.Dispose(); // the connection drops; the transmission handle is never stopped
            bool goneSaid = WaitIdle(events, EndReasons.ClientGone, 2000);
            long goneMs = sw.ElapsedMilliseconds;
            try { gone.Dispose(); } catch { }
            Check(goneSaid && goneMs < 1000 &&
                  WaitFor(() => watcher.GetState().Tx is { Keyed: false, Owner: null }, 1000),
                  $"a client gone ends its transmission at once ({goneMs} ms), and the transmitter is free");
            ev.Dispose();
        }
        finally
        {
            server.Dispose();
            backend.Dispose();
            log.Dispose();
        }

        // A radio fault under a transmission: the transmitter lets go on the radio's own thread, so the
        // server's stop does not wait out the abort's 2 s.
        var (fb, fs, fl) = StartSimTx("fault", faultAfterMs: 1500);
        try
        {
            using var s = Connect(fs, "fault");
            s.WaitReady(5000);
            s.ClaimTx();
            var events = TxEvents(s, out var ev);
            s.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone, MaxMs = 30_000, OffsetHz = OnBin, LevelDbfs = -10 });
            var sw = Stopwatch.StartNew();
            bool faulted = WaitIdle(events, EndReasons.RadioFault, 4000);
            List<(string name, JsonObject body)> seen;
            lock (events) seen = events.ToList();
            var idle = seen.LastOrDefault(e => e.name == EventNames.Tx && e.body.GetString("state") == TxStates.Idle).body;
            Check(faulted && sw.ElapsedMilliseconds < 2500 && idle?.GetString("text", "").Contains("--sim-fault-after") == true,
                  $"a radio fault ends the transmission as radio_fault, in the radio's words, {sw.ElapsedMilliseconds} ms after it was opened (the fault fires at 1.5 s)");
            ev.Dispose();
        }
        finally
        {
            fs.Dispose();
            fb.Dispose();
            fl.Dispose();
        }
    }

    private static void UhdTxWithoutRadio()
    {
        using var ulog = new SidecarLog(null, console: false);
        var uhd = new UhdBackend(ulog, new UhdBackendOptions { Serial = "KO6ETZB" });
        RadioRefusal? start = null, config = null;
        try { uhd.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone, MaxMs = 100, LevelDbfs = -10 }); }
        catch (RadioRefusal ex) { start = ex; }
        try { uhd.ConfigureTx(new TxSettings { GainDb = 10 }); }
        catch (RadioRefusal ex) { config = ex; }
        Check(uhd.TxCaps == null && start?.Code == ErrorCodes.NotReady && config?.Code == ErrorCodes.NotReady && start.Message.Contains("not open"),
              $"a UHD backend whose radio never opened states no transmitter and refuses tx_start and set_tx in words: {start?.Code} \"{start?.Message}\"");
        var server = new UsrpServer(uhd, new UsrpServerOptions { Port = 0, SidecarVersion = "test" });
        server.Start();
        try
        {
            using var s = UsrpSession.Open(UsrpAddress.Remote("127.0.0.1", server.Port), new UsrpSessionOptions { ClientName = "no-radio" });
            UsrpRequestException? refused = null;
            try { s.SetTx(new TxSettings { GainDb = 10 }); }
            catch (UsrpRequestException ex) { refused = ex; }
            Check(s.Caps.Tx == null && refused?.Code == ErrorCodes.NotReady,
                  $"...and over the wire its hello carries no caps.tx and set_tx is {refused?.Code}: \"{refused?.Error.Text}\"");
        }
        finally
        {
            server.Dispose();
            uhd.Dispose();
        }
        Check(!UhdLoader.IsLoaded, "...having loaded no UHD");
    }

    /// <summary>
    /// What no running gate can reach - the UHD half never runs here - pinned in the source: every way
    /// out of the sender ends the burst; start-of-burst rides the first send; one thread owns a TX streamer
    /// (the C API writes its error string on every call); <c>acked</c> is BURST_ACK and nothing else; the
    /// TX metadata's by-value flags are checked at load; the TX chain is set up before the receiver
    /// streams, with an admitted rate set before any streamer exists.
    /// </summary>
    private static void TxSourceRules()
    {
        string root = RepoRoot();
        string Src(params string[] p) => File.ReadAllText(Path.Combine(new[] { root, "src" }.Concat(p).ToArray()));
        string tx = Src("ShackLinkUsrp", "Radio", "UhdTransmission.cs");
        string? loop = MemberBody(tx, "private void SenderLoop()");
        Check(loop != null && CountOf(loop, "SendEndOfBurst(") == 2 && loop.Contains("startOfBurst: !started"),
              "UhdTransmission.SenderLoop: start-of-burst on the first send, and an end-of-burst on the normal way out and on the fault path");
        int outside = 0;
        foreach (string f in Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || f.EndsWith("UhdTxStreamer.cs") || f.EndsWith("UhdTransmission.cs")) continue;
            string text = File.ReadAllText(f);
            outside += CountOf(text, ".PollAsync(") + CountOf(text, ".SendEndOfBurst(") + CountOf(text, "startOfBurst:");
        }
        Check(outside == 0, $"nothing but the one sender thread drives a TX streamer's send and async poll ({outside} calls elsewhere)");
        string? poll = MemberBody(tx, "private void Poll(");
        Check(CountOf(tx, "acked = true") == 1 && poll != null && poll.Contains("BurstAck) != 0) acked = true"),
              "acked is set in one place: on BURST_ACK");
        string rt = Src("ShackLinkUsrp", "Radio", "RadioTransmission.cs");
        Check(CountOf(rt, "_acked = ") == 1 && MemberBody(rt, "protected void Finish(")?.Contains("_acked = acked") == true,
              "RadioTransmission reports acked only as the radio side finished it");
        string loader = Src("ShackLinkUsrp.Uhd", "UhdLoader.cs");
        Check(loader.Contains("UhdNative.TxMetadataProblem()"), "UhdLoader.Load checks the TX metadata flags against UHD's own getters");
        string ub = Src("ShackLinkUsrp", "Radio", "UhdBackend.cs");
        string? open = MemberBody(ub, "protected override void OpenRadio()");
        string? setup = MemberBody(ub, "private void OpenTransmitter(");
        Check(open != null && open.IndexOf("OpenTransmitter(", StringComparison.Ordinal) >= 0 &&
              open.IndexOf("OpenTransmitter(", StringComparison.Ordinal) < open.IndexOf("StartStreaming()", StringComparison.Ordinal),
              "UhdBackend.OpenRadio sets the transmitter up before the receiver streams");
        Check(setup != null && setup.IndexOf("SetTxRate(", StringComparison.Ordinal) >= 0 &&
              setup.IndexOf("SetTxRate(", StringComparison.Ordinal) < setup.IndexOf("CreateTxStreamer(", StringComparison.Ordinal),
              "...setting an admitted TX rate before any TX streamer exists (creating one re-applies whatever rate UHD holds)");
        Check(CountOf(ub, "CreateTxStreamer(") == 2, "a TX streamer is made in two places only: the probe at open and each transmission");
    }

    private static void TxOptions()
    {
        var d = SidecarOptions.Parse(new[] { "--sim" });
        Check(d.TxMaxMs == ProtocolConstants.DefaultTxMaxMs && d.TxStarveMs == ProtocolConstants.DefaultTxStarveMs && d.SimTxCouplingDb == SimBackend.DefaultTxCouplingDb,
              "by default: the one-hour ceiling, 500 ms starvation, -80 dB coupling");
        var o = SidecarOptions.Parse(new[] { "--sim", "--tx-max-ms", "400", "--tx-starve-ms", "300", "--sim-tx-coupling", "-60.5" });
        Check(o.TxMaxMs == 400 && o.TxStarveMs == 300 && o.SimTxCouplingDb == -60.5, "--tx-max-ms, --tx-starve-ms and a negative --sim-tx-coupling are read");
        string? why = null;
        try { SidecarOptions.Parse(new[] { "--serial", "KO6ETZB", "--sim-tx-coupling", "-60" }); }
        catch (ArgumentException ex) { why = ex.Message; }
        Check(why != null && why.Contains("--sim"), $"--sim-tx-coupling is refused on a real radio, in words ({why})");
        Check(Throws(() => SidecarOptions.Parse(new[] { "--sim", "--tx-max-ms", "0" })), "--tx-max-ms 0 is refused: every transmission has a ceiling");
    }

    /// <summary>The real executable: its radio says it is synthetic before anything is keyed, the process has
    /// no uhd.dll, and a tone sent to it is heard in its own panorama at the coupling its command line set.</summary>
    private static void SpawnTxLoopback()
    {
        string name = "tx" + Environment.ProcessId;
        var opts = new UsrpSessionOptions
        {
            ClientName = "tx-spawner",
            SidecarExe = SidecarExe(),
            Launch = new SidecarLaunch { LingerSeconds = 1, WideRateHz = 2e6, ExtraArgs = new[] { "--state-dir", Rendezvous.Root, "--sim-tx-coupling", "-70" } },
        };
        using var s = UsrpSession.Open(UsrpAddress.Sim(name), opts);
        int pid = s.SidecarPid;
        Check(s.WaitReady(5000) == RadioStates.Ready && s.Radio.Source == RadioSources.Sim && s.Caps.Tx != null,
              $"the spawned sidecar's radio is synthetic ({s.Radio.Source}) and states a transmitter");
        using (var p = Process.GetProcessById(pid))
            Check(!UhdDllIn(p), $"the sidecar process under test (pid {pid}) has no uhd.dll loaded");
        s.ClaimTx();
        long center = 100_000_000 + 20 * OnBin, carrier = center + OnBin;
        s.SetTx(new TxSettings { CenterHz = center, GainDb = 30 });
        var (pano, rows) = ProbeRows(s, carrier);
        var t = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone, MaxMs = 5_000, OffsetHz = OnBin, LevelDbfs = -20 });
        Thread.Sleep(800);
        double level = Snapshot(rows)[^1].levels[0];
        var r = t.Stop(drain: true);
        Check(Math.Abs(level - (-20 + 30 - 70)) < 1 && r.Acked,
              $"a -20 dBFS tone at 30 dB is heard at {F(level)} dBFS with --sim-tx-coupling -70 (wants -60), and drains acknowledged");
        Check(s.GetTelemetry().GetObject("radio")?.GetObject("source")?.GetDouble("tx_coupling_db", 0) == -70,
              "the coupling is stated in its telemetry (tx_coupling_db)");
        pano.Dispose();
    }
}
