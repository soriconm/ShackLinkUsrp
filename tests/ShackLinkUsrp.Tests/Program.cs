// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// The sidecar's gate (ShackLink docs/usrp_b210_support.md section 9.7): exit 0 = all pass.
// DSP against references, the real host in process against the synthetic radio, and the real
// executable spawned, joined and left to exit (section 9.1 D4-D6). No radio needed.

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ShackLinkUsrp.Client;
using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Host;
using ShackLinkUsrp.Pipelines;
using ShackLinkUsrp.Radio;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Tests;

internal static partial class Program
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
        if (args.Length > 1 && args[0] == "--w1700k-replay") return W1700kReplay(args);
        if (args.Length > 1 && args[0] == "--w1700k-probe") return W1700kProbe(args);
        if (args.Length > 0 && args[0] == "--w1700k-cost") return W1700kCost(args);
        // P0 of ShackLink docs/skimmer_support.md section 26 (BandChannelProbe.cs): instruments, never gates.
        if (args.Length > 0 && args[0] == "--fir-response") return FirResponse(args);
        if (args.Length > 0 && args[0] == "--fir-scaling") return FirScaling(args);
        if (args.Length > 1 && args[0] == "--lease-cost") return LeaseCost(args);
        // --only <text>: run the cases whose name contains it (a way to iterate, never how the gate is run).
        if (args.Length > 1 && args[0] == "--only") _only = args[1];
        var sw = Stopwatch.StartNew();

        Run("ring", RingCases);
        Run("FFT against a direct DFT", FftCases);
        Run("windows", WindowCases);
        Run("spectrum levels", SpectrumLevelCases);
        Run("decimating FIR", FirCases);
        Run("NCO", NcoCases);
        Run("26.14: the vectorised NCO against the exact phasor", NcoVectorCases);
        Run("26.14: the two-stage lease filter against a reference decimator", LeaseFilterReferenceCases);
        Run("26.14: the two-stage filter keeps the single stage's timing", LeaseFilterTimingCases);
        Run("26.14: the lease's clean fraction (ProtocolConstants.LeaseCleanFraction), on the design and on the running filter", LeaseFilterCleanFractionCases);
        Run("26.14: a lease filters through the cascade, pinned in the source", LeaseFilterSourceRules);
        Run("in process: a lease on the synthetic radio", InProcessLease);
        Run("in process: panorama, sweep, retune, rate change", InProcessPanorama);
        Run("A2 F1: a pipeline created inside a change's settle starts at its boundary", RaceNewPipelinesStartAtTheBoundary);
        Run("A2 F2: the first sweep after a change is taken after it", RaceTakeAfterAChange);
        Run("A2 F3: a row never spans a gain, AGC, antenna or bandwidth change", RaceFrontEndChanges);
        Run("A2: a frame that straddles a change's boundary is never integrated", RaceFrameStraddlesAChange);
        Run("A2: replacing an idle accumulator costs no join timeout", ReplacingAnIdleAccumulator);
        Run("A2: what the running gates cannot reach, pinned in the source", A2SourceRules);
        Run("A3: hello states the FFT ceiling and the codec-fault opt-in", StatementsInHello);
        Run("spawned: the real executable, joined, then left to exit", SpawnJoinExit);
        Run("command line: the synthetic radio's fault hook", FaultHookOptions);
        Run("command line: the synthetic radio's settle hook", SettleHookOptions);
        Run("spawned: a radio in a fault ends its sidecar when its last client leaves", SpawnFaultExit);
        Run("build: the protocol library is the sidecar's own configuration (9.11 item 8)", ProtocolLibraryConfiguration);
        Run("P4b: the tone generator", ToneGeneratorCases);
        Run("P4b: the TX interpolator (the synthetic radio's DUC)", TxInterpolatorCases);
        Run("P4b: the TX rate plan against the clocks 9.8 measured", TxRatePlanCases);
        Run("P4b: the transmit queue", TxQueueCases);
        Run("P4b: nothing here loaded UHD", NoUhdInThisProcess);
        Run("P4b: the synthetic radio states its transmitter", SimTxCaps);
        Run("P4b: a tone lands in the synthetic radio's own panorama, parks, and ends", SimToneLoopback);
        Run("P4b: an IQ transmission paced by the synthetic radio's clock", SimIqLoopback);
        Run("P4b: every other way a transmission ends", SimTxEnds);
        Run("P4b: a UHD backend with no radio refuses in words", UhdTxWithoutRadio);
        Run("P4b: what the running gates cannot reach, pinned in the source", TxSourceRules);
        Run("command line: the transmitter's options", TxOptions);
        Run("spawned: the real executable transmits into itself, and loaded no UHD", SpawnTxLoopback);
        Run("P4b2: the AM / FM generator", ModGeneratorCases);
        Run("P4b2: the stepped sweep's plan", TxStepPlanCases);
        Run("P4b2: the stepped carrier moves at its samples", SteppedCarrierCases);
        Run("P4b2: AM and FM land in the synthetic radio's own panorama", SimModLoopback);
        Run("P4b2: a stepped sweep, step by step, heard where each step said", SimStepLoopback);
        Run("P4b2: a baseband sweep's dwell, measured in what the radio received", SimStepDwellBaseband);
        Run("P4b2: a tuned sweep's dwell, measured in what the radio received", SimStepDwellTuned);
        Run("P4b2: the DUC inside its reach, the synthesizer beyond it", SimStepRetunes);
        Run("P4b2: steps before anyone listened are not lost", StepEventsBeforeSubscribers);
        Run("P4b2: repeat and bounce", SimStepSequences);
        Run("P4b2: what the running gates cannot reach, pinned in the source", TxGeneratedSourceRules);
        Run("9.21 S2: AM and FM modulated by the library's waves", ModWaveformCases);
        Run("9.21 S2: a square AM and a two-tone FM in the synthetic radio's own panorama", SimModWaveLoopback);
        Run("9.21 S3: DSB, SSB and PM", SidebandPmCases);
        Run("9.21 S3: DSB, SSB and PM in the synthetic radio's own panorama", SimSidebandPmLoopback);
        Run("9.21 S4: the pulse generator", PulseGeneratorCases);
        Run("9.21 S4: a pulse train in the synthetic radio's panorama, and measured through a lease", SimPulseLoopback);
        Run("9.20: UHD's serial rule, ported (hexadecimal numbers, not strings)", UhdSerialRuleCases);
        Run("9.20: which B2xx to open, and when to refuse", UhdOpenPlanCases);
        Run("9.20: the UHD open path, pinned in the source", UhdOpenSourceRules);
        Run("9.26: --list's answer, from the bench's find strings", ListAnswerCases);
        Run("9.26: the real executable's --list --json, down its no-UHD path", ListJsonSpawnCases);
        Run("9.26: one UHD finder at a time - the device lock, taken for real", DeviceOpsLockCases);
        Run("9.26: both finders hold the lock, pinned in the source", DeviceOpsLockSourceRules);
        Run("9.27: the native rates, offered on every machine (source rules)", NativeRateSourceRules);
        Run("9.27: the analog bandwidth follows the rate until set, and a set one survives a rate change", BandwidthRuleCases);
        Run("9.23 DS2: a ring aligned to the other side's position", RingAlignCases);
        Run("9.23 DS2: the NCO's phase is the radio position's", NcoAnchorCases);
        Run("9.23 DS2: both receive sides of the synthetic radio", SimTwoSides);
        Run("9.23 DS4: 2R2T - a transmission beside two receivers, heard on both", SimTwoSidesTx);
        Run("9.23 DS2: what only a board can prove, pinned in the source", SidesSourceRules);
        Run("W1700K: the export's codec - upstream's conversion, raw and packed slices there and back", W1700kCodecCases);
        Run("W1700K: the reorder window - out of order put back, a hole given up in its place, late after that", W1700kReorderCases);
        Run("W1700K: the band follows the frequency, the gain on upstream's measured steps, the filter", W1700kRuleCases);
        Run("W1700K: flatten (upstream's roll-off correction) and DC removal", W1700kConditionCases);
        Run("W1700K: the conditioner in batches shared by a team - bit for bit the one that ran on the unit", W1700kConditionBatchCases);
        Run("W1700K: a fake unit end to end - open, panorama, losses, retune, band switch, gain, refusals, a stream that dies", W1700kEndToEnd);
        Run("W1700K: closing stops the unit's stream", W1700kClose);
        Run("W1700K: a unit already streaming, a cold one, one out of reach, a port another receiver holds", W1700kRefusals);
        Run("W1700K: a stream that goes silent faults in words", W1700kSilence);
        Run("W1700K: no controller - another program's stream, received as it is", W1700kNoController);
        Run("W1700K: the command line", W1700kCommandLine);
        Run("W1700K: the real executable, --w1700k fed by the fake export", W1700kSpawned);
        Run("W1700K U1: usrp:w1700k[:<name>], its key, IsSameRadio, and the spawn line read back by the sidecar", W1700kAddressCases);
        Run("W1700K U1: spawned through the library for usrp:w1700k:<name>, joined by a second open, gone after its linger", W1700kSpawnedThroughLibrary);
        Run("Pluto P2: the command line - --iiod, its refusals, and the library's spawn line", PlutoCommandLine);
        Run("Pluto P2: the open - SafeIdle first, the sidecar's rate, the board's own caps (B9, B10)", PlutoOpen);
        Run("Pluto P2: found by its serial - a stale address re-found, another board never written (B6)", PlutoFind);
        Run("Pluto P2: a FIR another program loaded - found, said, switched off (section 9.8)", PlutoFir);
        Run("Pluto P2: levels - the 12-bit samples x16 to sc16 full scale (section 9.2 item 8)", PlutoLevels);
        Run("Pluto: an RX LO another program powered down - found, powered up, said, and the tone heard (section 9.29)", PlutoRxLoFoundDown);
        Run("Pluto P2: the wide lease - the board's samples bit for bit, following the LO (B4)", PlutoWideLease);
        Run("Pluto 9.33: a retune throws the board's queue away - exact, nothing stale after it, the skip under two buffers; a gain change throws nothing", PlutoRetuneFlush);
        Run("Pluto P2: the loss oracle - a polled register, and a board that has none (section 9.2 item 5)", PlutoLossOracle);
        Run("Pluto P2: a third program - every value this sidecar did not write faults the radio (B11)", PlutoForeignWriter);
        Run("Pluto P2: the radio's settings, each read back, and the watch quiet through them", PlutoSetters);
        Run("Pluto 9.32: RX B - the second chain: one stream, each side its own samples, gain, AGC and input, ONE centre; a 1R1T board offers RX A alone", PlutoSides);
        Run("Pluto P2: close, and a board that goes away (B10)", PlutoCloseAndLoss);
        Run("Pluto P2: the real executable, spawned with --iiod, loads no UHD", PlutoSpawned);
        Run("Pluto P2: what the running gates cannot reach, pinned in the source", PlutoSourceRules);
        Run("Pluto P4b: the transmitter as it opens - caps from the board, the truth in force, nothing held", PlutoTxCaps);
        Run("Pluto P4b: set_tx is HELD while idle (Q1), and a gain never set is refused", PlutoTxHold);
        Run("Pluto P4b: ShackLink's IQ end to end - the key-up at the first buffer, bit for bit, drained on the board's word", PlutoTxIq);
        Run("Pluto P4b: every other end - abort, starvation, a wedged board, a refused key-up, another writer", PlutoTxEnds);
        Run("Pluto P4b: the generated sources - a tone, a park, a keyed gain move, a baseband and a tuned sweep", PlutoTxGenerated);
        Run("Pluto P4b: what is said while a key-up runs - the truth in force, never the plan", PlutoTxKeyUpWindow);
        Run("Pluto P4b: a board whose transmitter cannot be read still receives, and says it does not transmit", PlutoTxUnreadable);
        Run("Pluto P4b: a board with no debugfs transmits, and never acknowledges a drain it cannot see", PlutoTxNoOracle);
        Run("Pluto P4b: another program while keyed - faulted in words, left as it set it (B11)", PlutoTxForeign);
        Run("Pluto P4b: one clock chain - the TX rates follow the wide rate, which cannot move under a transmission", PlutoTxRate);
        Run("Pluto P4b: what the running gates cannot reach, pinned in the source", PlutoTxSourceRules);
        Run("Pluto P4e: a source at the wide rate goes to the board as it is, bit for bit, in real time", PlutoTxAtTheWideRate);
        Run("Pluto P4e: the interpolator's tail leaves before the end - short of a buffer's edge, and exactly on it", PlutoTxTail);
        Run("Pluto P4e: the interpolator by itself - exact every 8th sample, phase-continuous across calls, its images, its clip count", PlutoUpsamplerUnit);
        Run("Pluto P4e: the drain's deadline is the board's queue at the WIDE rate, the DMA's", PlutoTxDrainDeadline);
        Run("Pluto P4e: a tuned step's interpolated tail leaves at ITS LO before the LO moves", PlutoTxTunedTail);
        Run("Pluto U3: --iiod usb:<hw_serial> - the serial the address's, never a second spelling (9.26 D14)", PlutoUsbCommandLine);
        Run("Pluto U3: the board over its own USB pipes - opened at the rate asked, no ceiling, the route said (9.26 D14, D20; 9.32 Q21)", PlutoUsbOpen);
        Run("Pluto U3: the network routes - Ethernet and the board's USB network, told by the classifier, never capped (9.26 D11; 9.32 Q21)", PlutoRouteLimits);
        Run("Pluto U3: a keyed radio's broken control connection - SafeIdle through pipe 0 again, never a fourth (9.26 D17)", PlutoUsbSafeIdleOnItsOwnPipe);
        Run("Pluto P2: and still nothing here loaded UHD", NoUhdInThisProcess);

        try { Directory.Delete(_temp, true); } catch { }
        Console.WriteLine();
        Console.WriteLine($"ShackLinkUsrp.Tests: {(_fail == 0 ? "ALL PASS" : "FAILED")} ({_pass} passed, {_fail} failed, {sw.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s)");
        return _fail == 0 ? 0 : 1;
    }

    private static string? _only;

    private static void Run(string name, Action body)
    {
        if (_only != null && !name.Contains(_only, StringComparison.OrdinalIgnoreCase)) return;
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

        // Section 26.14: a waiter is woken by the write that reaches ITS position - promptly, never by its timeout - and
        // twelve waiters at twelve positions each come back after their own write, not before.
        var wide = new WideRing(1 << 16);
        var targets = Enumerable.Range(1, 12).Select(i => (long)i * 4096).ToArray();
        var woke = new long[targets.Length];
        var sw = Stopwatch.StartNew();
        var waiters = targets.Select((t, i) => Task.Factory.StartNew(() =>
        {
            bool ok = wide.WaitFor(t, 5000);
            woke[i] = ok ? sw.ElapsedTicks : -1;
        }, TaskCreationOptions.LongRunning)).ToArray();
        Thread.Sleep(100);
        var writtenAt = new long[targets.Length];
        var slice = new short[2 * 2048];
        int reached = 0;
        for (long pos = 0; pos < targets[^1]; pos += 2048)
        {
            wide.Write(slice);
            while (reached < targets.Length && wide.Written >= targets[reached]) writtenAt[reached++] = sw.ElapsedTicks;
            Thread.Sleep(5);
        }
        Check(Task.WaitAll(waiters, 5000), "twelve waiters at twelve positions all return");
        double ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        var late = Enumerable.Range(0, targets.Length).Select(i => ms(woke[i] - writtenAt[i])).ToArray();
        bool early = Enumerable.Range(0, targets.Length).Any(i => woke[i] < 0 || woke[i] < writtenAt[i] - Stopwatch.Frequency / 1000);
        Check(!early && late.Max() < 50,
              $"...each after the write that reached it and within {late.Max():0.0} ms of it (a lost wake would sit out the 5 s timeout)");
        var stuck = Task.Factory.StartNew(() => wide.WaitFor(long.MaxValue, 5000), TaskCreationOptions.LongRunning);
        Thread.Sleep(50);
        var t0 = Stopwatch.StartNew();
        wide.WakeAll();
        Check(stuck.Wait(1000) && !stuck.Result && t0.ElapsedMilliseconds < 1000, "WakeAll wakes a waiter whose data will never come, which then reports it did not arrive");
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

    private static (SimBackend backend, UsrpServer server, SidecarLog log) StartSim(string name, double rateHz = 10e6, int settleMs = 0)
    {
        var log = new SidecarLog(null, console: false);
        var backend = new SimBackend(log, name, rateHz) { SettleMs = settleMs };
        var server = new UsrpServer(backend, new UsrpServerOptions { Port = 0, SidecarVersion = "test", LingerSeconds = 60 });
        server.Start();
        backend.Start();
        return (backend, server, log);
    }

    /// <summary>The level at <paramref name="hz"/> in a row, dBFS: the highest of the seven bins around
    /// it (a tone between bins loses under 0.5 dB to Hann's scalloping). Bin i of a row is at
    /// center_hz + (i - bins/2) Fs / fft_size. NaN when the frequency is outside the row.</summary>
    private static double LevelAt(RowHeader h, short[] bins, double hz, double fs)
    {
        int i = h.Bins / 2 + (int)Math.Round((hz - h.CenterHz) * h.FftSize / fs);
        if (i < 0 || i >= h.Bins) return double.NaN;
        short max = short.MinValue;
        for (int k = Math.Max(0, i - 3); k <= Math.Min(h.Bins - 1, i + 3); k++) max = Math.Max(max, bins[k]);
        return max / 100.0;
    }

    /// <summary>An IQ block's level, dBFS: 20 log10 of its mean magnitude over full scale, so a carrier on
    /// the lease centre reads its own level (the in-process lease case reads the calibration carrier so).</summary>
    private static double MeanDbfs(byte[] sc16, int count)
    {
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            short I = BitConverter.ToInt16(sc16, 4 * i), Q = BitConverter.ToInt16(sc16, 4 * i + 2);
            sum += Math.Sqrt((double)I * I + (double)Q * Q);
        }
        return 20 * Math.Log10(sum / count / 32767);
    }

    /// <summary>The level each push row's gain says the carrier must read: 30 dB (and the synthetic
    /// radio's AGC, which holds 30) -20 dBFS, 40 dB -10.</summary>
    private static double CarrierAt(float gainDb) => float.IsNaN(gainDb) || gainDb == 30f ? -20 : gainDb == 40f ? -10 : double.NaN;

    private static string Gain(float gainDb) => float.IsNaN(gainDb) ? "AGC" : F(gainDb, "0") + " dB";

    /// <summary>Every row in <paramref name="rows"/> read its carrier at the gain it states; the first that
    /// did not, in words, else "".</summary>
    private static string FirstMismeasuredRow(List<(RowHeader h, double carrier)> rows)
    {
        foreach (var (h, carrier) in rows)
            if (!(Math.Abs(carrier - CarrierAt(h.GainDb)) < 0.5))
                return $"row {h.Seq} states {Gain(h.GainDb)} and its carrier reads {F(carrier)} dBFS ({h.Ffts} FFT)";
        return "";
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

    // ---------------------------------------------------------------- A2: the sweep races (CRTU200)
    //
    // Each case runs the synthetic radio with --sim-settle-ms: after a change it keeps generating at the
    // OLD settings for that long and announces exactly where the new ones begin - a real radio's in-flight
    // USB buffers, made long enough to see. Its signals sit at fixed ABSOLUTE frequencies, so a sample
    // filed under the wrong LO shows up where nothing transmits, and a sample filed under the wrong gain
    // reads the wrong level. Each was written, and seen to FAIL, against sidecar 510ee2a before its fix.

    /// <summary>
    /// F1: a pipeline CREATED inside a radio change's settle started at the ring's write position, so
    /// samples still in flight at the old LO went in under the new one. Seen from 100.0 MHz the -20 dBFS
    /// carrier at 100.1 MHz sits 100 kHz up, which a pipeline that believes the LO is 99.5 MHz files at
    /// 99.6 MHz, where nothing transmits: a GHOST. All three kinds, each created right after a set_lo.
    /// </summary>
    private static void RaceNewPipelinesStartAtTheBoundary()
    {
        const double fs = 2e6;
        var (backend, server, log) = StartSim("f1", fs, settleMs: 300);
        try
        {
            using var s = Connect(server, "f1");
            s.WaitReady(5000);
            s.ClaimLo();
            s.Sweep(new PanoramaRequest { Bins = 1024, Integration = IntegrationKind.Average, Window = WindowKind.Hann });

            // A sweep whose geometry differs from this client's last one builds a FRESH accumulator.
            s.SetLo(99_500_000);
            var r = s.Sweep(new PanoramaRequest { Bins = 1024, CenterHz = 99_500_000, SpanHz = 2_000_000, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
            double carrier = LevelAt(r.Header, r.Bins, 100_100_000, fs), ghost = LevelAt(r.Header, r.Bins, 99_600_000, fs);
            Check(r.Header.CenterHz == 99_500_000 && ghost < -60 && Math.Abs(carrier + 20) < 1,
                  $"sweep: a new geometry sent inside set_lo's 300 ms settle integrates nothing from before the boundary - the carrier at 100.1 MHz reads {F(carrier)}, the old LO's ghost of it at 99.6 MHz {F(ghost)} dBFS ({r.Header.Ffts} FFTs)");
            Thread.Sleep(400);

            // A lease at 100.1 MHz with the radio moved back to 100.0 MHz: the carrier is ON its centre. From
            // the old LO (99.5 MHz) it is 500 kHz off it - outside a 200 kHz lease, which hears only the floor.
            s.SetLo(100_000_000);
            using (var lease = s.OpenLease(new LeaseRequest { CenterHz = 100_100_000, Decimation = 10 }))
            {
                IqHeader h0 = default;
                byte[]? d0 = null;
                var first = new ManualResetEventSlim();
                lease.Connection.IqBlockReceived += (c, h, data) =>
                {
                    if (first.IsSet) return;
                    h0 = h;
                    d0 = data;
                    first.Set();
                };
                lease.Connection.StartReader();
                bool got = first.Wait(3000);
                double level = got ? MeanDbfs(d0!, h0.Count) : double.NaN;
                Check(got && Math.Abs(level + 20) < 1,
                      $"lease: opened inside the settle, its FIRST block was taken after the boundary - the carrier on its centre reads {F(level)} dBFS over its {h0.Count} samples (before the boundary a 200 kHz lease hears the -76 dBFS floor)");
            }
            Thread.Sleep(400);

            // A push panorama opened inside the settle: its first row.
            s.SetLo(99_500_000);
            using (var pano = s.OpenPanorama(new PanoramaRequest { Bins = 1024, Fps = 10, Integration = IntegrationKind.Average, Window = WindowKind.Hann }))
            {
                RowHeader h0 = default;
                short[]? b0 = null;
                var first = new ManualResetEventSlim();
                pano.Connection.RowReceived += (c, h, bins) =>
                {
                    if (first.IsSet) return;
                    h0 = h;
                    b0 = (short[])bins.Clone();
                    first.Set();
                };
                pano.Connection.StartReader();
                bool got = first.Wait(3000);
                double pc = got ? LevelAt(h0, b0!, 100_100_000, fs) : double.NaN, pg = got ? LevelAt(h0, b0!, 99_600_000, fs) : double.NaN;
                Check(got && h0.CenterHz == 99_500_000 && pg < -60 && Math.Abs(pc + 20) < 1,
                      $"push panorama: opened inside the settle, its FIRST row integrates nothing from before the boundary - the carrier reads {F(pc)}, the ghost {F(pg)} dBFS ({h0.Ffts} FFTs)");
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
    /// F2: set_lo only MARKED an accumulator for the retune, and its worker applied it between frames, so
    /// a sweep arriving after set_lo's reply but before the next frame boundary was handed the accumulator
    /// from BEFORE the retune. The window is made wide on purpose - 65,536 bins over a quarter of 2 MS/s
    /// is a 2^18-point FFT, one frame every 131 ms - so an unfixed sidecar answers the old centre nearly
    /// every time, and three retunes in a row leave it no way through.
    /// </summary>
    private static void RaceTakeAfterAChange()
    {
        const double fs = 2e6;
        var (backend, server, log) = StartSim("f2", fs, settleMs: 300);
        try
        {
            using var s = Connect(server, "f2");
            s.WaitReady(5000);
            s.ClaimLo();
            var req = new PanoramaRequest { Bins = 65536, SpanHz = 500_000, Integration = IntegrationKind.Average, Window = WindowKind.Hann };
            var reuse = new short[65536];
            var r0 = s.Sweep(req, reuse);
            Check(r0.Header.FftSize == 1 << 18 && r0.Header.CenterHz == 100_000_000,
                  $"a {r0.Header.FftSize}-point accumulator following the LO at 100 MHz: one frame every {F(r0.Header.FftSize / fs * 1000, "0")} ms");
            var said = new List<string>();
            bool allNew = true, allClean = true;
            foreach (long lo in new long[] { 99_500_000, 100_000_000, 99_500_000 })
            {
                Thread.Sleep(450); // frames integrate at the present LO; the previous change is long past its boundary
                s.SetLo(lo);
                var r = s.Sweep(req, reuse);
                // A 99.5 MHz row must not hold the old LO's carrier at 99.6 MHz; a 100.0 MHz row holds the
                // carrier where the new LO puts it, at 100.1 MHz.
                bool down = lo == 99_500_000;
                double probe = LevelAt(r.Header, r.Bins, down ? 99_600_000 : 100_100_000, fs);
                allNew &= r.Header.CenterHz == lo;
                allClean &= down ? probe < -60 : Math.Abs(probe + 20) < 1;
                said.Add($"{F(lo / 1e6, "0.0")} MHz: centre {F(r.Header.CenterHz / 1e6, "0.000")}, {r.Header.Ffts} FFTs, " +
                         $"{(down ? "the ghost at 99.6" : "the carrier at 100.1")} MHz {F(probe)} dBFS");
            }
            Check(allNew, "the first sweep after set_lo's reply names the NEW centre every time, never the accumulator from before the retune: " + string.Join("; ", said));
            Check(allClean, "...and holds nothing from before the retune's boundary");
        }
        finally
        {
            server.Dispose();
            backend.Dispose();
            log.Dispose();
        }
    }

    /// <summary>
    /// F3: set_gain, set_agc, set_antenna and set_bandwidth told no pipeline, so the next row integrated
    /// FFTs from BOTH sides of the change and stated the new gain. The synthetic radio's gain moves every
    /// signal (30 -> 40 dB lifts the -20 dBFS carrier to -10; its AGC holds 30). Two views: the sweep taken
    /// right after each change, whose FFTs must all postdate it (ffts x fft_size / Fs no longer than the
    /// time since the change was asked for), and a push panorama of one 131 ms FFT per row, every row of
    /// which must read the carrier at the gain it states.
    /// </summary>
    private static void RaceFrontEndChanges()
    {
        const double fs = 2e6;
        var (backend, server, log) = StartSim("f3", fs, settleMs: 300);
        try
        {
            using var s = Connect(server, "f3");
            s.WaitReady(5000);
            s.ClaimLo();
            var rows = new List<(RowHeader h, double carrier)>();
            using var pano = s.OpenPanorama(new PanoramaRequest { Bins = 65536, SpanHz = 500_000, Fps = 10, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
            pano.Connection.RowReceived += (c, h, bins) =>
            {
                double level = LevelAt(h, bins, 100_100_000, fs);
                lock (rows) rows.Add((h, level));
            };
            pano.Connection.StartReader();
            var req = new PanoramaRequest { Bins = 1024, Integration = IntegrationKind.Average, Window = WindowKind.Hann };
            s.Sweep(req);

            var steps = new (string What, Action Apply, float Gain, double Carrier)[]
            {
                ("set_gain 40 dB", () => s.SetGain(40), 40f, -10),
                ("set_agc on", () => s.SetAgc(true), float.NaN, -20),
                ("set_antenna TX/RX", () => s.SetAntenna("TX/RX"), float.NaN, -20),
                ("set_bandwidth 1 MHz", () => s.SetBandwidth(1e6), float.NaN, -20),
            };
            foreach (var step in steps)
            {
                Thread.Sleep(450); // ~880 FFTs at the settings in force; the previous change is long settled
                var since = Stopwatch.StartNew();
                step.Apply();
                var r = s.Sweep(req);
                double sinceMs = since.Elapsed.TotalMilliseconds;
                double rowMs = r.Header.Ffts * (double)r.Header.FftSize / fs * 1000;
                double carrier = LevelAt(r.Header, r.Bins, 100_100_000, fs);
                bool stated = float.IsNaN(step.Gain) ? float.IsNaN(r.Header.GainDb) : r.Header.GainDb == step.Gain;
                // The synthetic radio runs at most one 4096-sample block (2 ms here) ahead of the clock.
                Check(rowMs <= sinceMs + 3 && stated && Math.Abs(carrier - step.Carrier) < 0.5,
                      $"{step.What}: the next sweep integrates only FFTs taken after the change - {r.Header.Ffts} FFTs = {F(rowMs, "0.0")} ms of signal, " +
                      $"{F(sinceMs, "0")} ms after it was asked for - at the gain its header states ({Gain(r.Header.GainDb)}): the carrier reads {F(carrier)} dBFS (wants {F(step.Carrier, "0")})");
            }

            Thread.Sleep(600);
            List<(RowHeader h, double carrier)> seen;
            lock (rows) seen = rows.ToList();
            string wrong = FirstMismeasuredRow(seen);
            Check(seen.Count >= 12 && wrong.Length == 0,
                  $"push panorama: each of {seen.Count} rows (one 131 ms FFT each) read the carrier at the gain it states - 30 dB -20, 40 dB -10, AGC -20 dBFS" +
                  (wrong.Length == 0 ? "" : "; " + wrong));
        }
        finally
        {
            server.Dispose();
            backend.Dispose();
            log.Dispose();
        }
    }

    /// <summary>
    /// A frame LONGER than a change's settle straddles its boundary - UHD's settle is 5 ms, and a
    /// 2^18-point frame at 2 MS/s lasts 131 ms - so a frame read after a change was announced must not be
    /// integrated: it holds samples from both sides. Each gain change is sent the moment a push row
    /// arrives, while the next frame is still filling, and its boundary (65 ms on) lands in the MIDDLE of
    /// that frame: a boundary near a frame's start hides under the Hann window's low edge (a first cut
    /// with a 20 ms settle put it 17 % in, where 3 % of the window's weight lives, and saw nothing).
    /// </summary>
    private static void RaceFrameStraddlesAChange()
    {
        const double fs = 2e6;
        var (backend, server, log) = StartSim("straddle", fs, settleMs: 65);
        try
        {
            using var s = Connect(server, "straddle");
            s.WaitReady(5000);
            s.ClaimLo();
            var rows = new List<(RowHeader h, double carrier)>();
            var arrived = new AutoResetEvent(false);
            using var pano = s.OpenPanorama(new PanoramaRequest { Bins = 65536, SpanHz = 500_000, Fps = 10, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
            pano.Connection.RowReceived += (c, h, bins) =>
            {
                double level = LevelAt(h, bins, 100_100_000, fs);
                lock (rows) rows.Add((h, level));
                arrived.Set();
            };
            pano.Connection.StartReader();
            Check(arrived.WaitOne(3000), "rows of one 2^18-point FFT each arrive");
            foreach (double db in new[] { 40.0, 30.0, 40.0, 30.0 })
            {
                arrived.Reset();
                arrived.WaitOne(3000); // a row has just gone out: the next frame is filling now
                s.SetGain(db);
            }
            Thread.Sleep(700);
            List<(RowHeader h, double carrier)> seen;
            lock (rows) seen = rows.ToList();
            string wrong = FirstMismeasuredRow(seen);
            Check(seen.Count >= 8 && wrong.Length == 0,
                  $"four gain changes each landing mid-frame (settle 65 ms, frame 131 ms): each of {seen.Count} rows read the carrier at the gain it states" +
                  (wrong.Length == 0 ? "" : "; " + wrong));
        }
        finally
        {
            server.Dispose();
            backend.Dispose();
            log.Dispose();
        }
    }

    /// <summary>
    /// A sweep with a new geometry REPLACES this client's accumulator, disposing the old one - its thread
    /// joined, for up to 2 s, under the backend's listener lock. An idle accumulator (nobody has pulled
    /// it for IdleStopMs, 30 s in service, 300 ms here) wakes every 100 ms and reads SettledFrom; while
    /// that read took the same lock, the old thread could not finish and every such replacement waited out
    /// the whole join. Found reviewing the F1 fix before it was committed.
    /// </summary>
    private static void ReplacingAnIdleAccumulator()
    {
        int idleWas = SweepWorker.IdleStopMs;
        SweepWorker.IdleStopMs = 300;
        var (backend, server, log) = StartSim("idle");
        try
        {
            using var s = Connect(server, "idle");
            s.WaitReady(5000);
            s.Sweep(new PanoramaRequest { Bins = 1024, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
            Thread.Sleep(900); // idle past 300 ms: the accumulator is paused, waking every 100 ms
            var sw = Stopwatch.StartNew();
            var r = s.Sweep(new PanoramaRequest { Bins = 2048, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
            long ms = sw.ElapsedMilliseconds;
            Check(r.Header.Bins == 2048 && ms < 1000,
                  $"a new geometry replacing an idle accumulator is answered in {ms} ms (the old one's join is 2 s when it cannot finish)");
        }
        finally
        {
            SweepWorker.IdleStopMs = idleWas;
            server.Dispose();
            backend.Dispose();
            log.Dispose();
        }
    }

    private static string RepoRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "ShackLinkUsrp.sln"))) root = root.Parent;
        return root!.FullName;
    }

    /// <summary>The text of the member whose declaration starts with <paramref name="signature"/>: its
    /// braced body, or an expression body up to its semicolon. Null when it is not there.</summary>
    private static string? MemberBody(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return null;
        int brace = source.IndexOf('{', at), arrow = source.IndexOf("=>", at, StringComparison.Ordinal);
        if (arrow >= 0 && (brace < 0 || arrow < brace)) return source.Substring(arrow, source.IndexOf(';', arrow) - arrow + 1);
        int depth = 0;
        for (int i = brace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(brace, i - brace + 1);
        }
        return null;
    }

    /// <summary>
    /// What no running gate above can reach, pinned in the source. EVERY setter that changes the radio
    /// announces - set_setting included, which the synthetic radio has none of (UHD's clock source and IQ
    /// corrections change the samples as surely as a gain does). And a row's gain is the one captured with
    /// its accumulator when it last restarted, never read as the row goes out: a change landing between a
    /// frame's pending-check and its row is a window of microseconds the running cases cannot aim at.
    /// </summary>
    private static void A2SourceRules()
    {
        string root = RepoRoot();
        string backend = File.ReadAllText(Path.Combine(root, "src", "ShackLinkUsrp", "Radio", "RadioBackendBase.cs"));
        foreach (var setter in new[] { "void SetLo(long hz)", "void SetRate(double hz)", "void SetBandwidth(double hz)",
                                       "string SetSetting(string key, string value)",
                                       // section 9.23: the per-side setters, the side switch and a side's centre
                                       "void SetGain(string side, double db)", "void SetAgc(string side, bool on)",
                                       "void SetAntenna(string side, string name)", "void SetRxSides(IReadOnlyList<string> sides)",
                                       "void SetCenter(string side, long hz)" })
        {
            string? body = MemberBody(backend, "public " + setter);
            Check(body != null && body.Contains("Announce"), $"RadioBackendBase.{setter} announces the change to every pipeline");
        }
        // The one-side setters are side A's: each goes through the per-side one, which announces.
        foreach (var (setter, sided) in new[] { ("void SetGain(double db)", "SetGain(RxSides.A, db)"), ("void SetAgc(bool on)", "SetAgc(RxSides.A, on)"),
                                                ("void SetAntenna(string name)", "SetAntenna(RxSides.A, name)") })
        {
            int at = backend.IndexOf("public " + setter, StringComparison.Ordinal);
            string line = at < 0 ? "" : backend.Substring(at, Math.Max(0, backend.IndexOf('\n', at) - at));
            Check(line.Contains("=> " + sided), $"RadioBackendBase.{setter} is side A's, through {sided}");
        }
        string worker = File.ReadAllText(Path.Combine(root, "src", "ShackLinkUsrp", "Pipelines", "SpectrumWorker.cs"));
        int reads = CountOf(worker, "RowGainDb");
        int captures = CountOf(worker, "AccGainDb = ctx.RowGainDb;") + CountOf(worker, "AccGainDb = Ctx.RowGainDb;");
        Check(captures == 2 && reads == captures,
              $"SpectrumWorker reads the radio's gain only to capture it with its accumulator (at construction and at each change applied): {captures} captures, {reads} reads");
        string? header = MemberBody(worker, "protected RowHeader MakeHeader(");
        Check(header != null && header.Contains("GainDb = gainDb,"), "...and a row's header states the gain it is handed, the captured one");
    }

    private static int CountOf(string text, string what)
    {
        int n = 0;
        for (int i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + what.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    // ---------------------------------------------------------------- A3: two statements (CRTU200)

    /// <summary>
    /// CRTU200's A3 from the sidecar's side: hello states the FFT ceiling (caps.max_fft_size) and whether
    /// it was started accepting a board with a known CODEC fault (radio.known_codec_fault), and each is the
    /// truth - the stated ceiling is the one a geometry is refused at, and the opt-in is the command line's,
    /// stated from construction, so a client that says hello during a bitstream load already knows.
    /// </summary>
    private static void StatementsInHello()
    {
        var (backend, server, log) = StartSim("a3");
        try
        {
            using var s = Connect(server, "a3");
            Check(s.Caps.MaxFftSize == 1 << 20 && s.Radio.KnownCodecFault == false,
                  $"the synthetic radio's hello states max_fft_size {s.Caps.MaxFftSize} and known_codec_fault {s.Radio.KnownCodecFault?.ToString() ?? "(not stated)"}");
            s.WaitReady(5000);
            Check(s.Caps.MaxFftSize == 1 << 20 && s.Radio.KnownCodecFault == false, "...and wait_ready re-states both");
            UsrpRequestException? refused = null;
            try { s.Sweep(new PanoramaRequest { Bins = 1 << 18, SpanHz = 100_000 }); }
            catch (UsrpRequestException ex) { refused = ex; }
            Check(refused != null && refused.Code == ErrorCodes.OutOfRange && refused.Error.Extra?.GetLong("max_fft") == s.Caps.MaxFftSize,
                  $"the stated ceiling is the enforced one: 2^18 bins across 100 kHz is refused {refused?.Code} naming max_fft {refused?.Error.Extra?.GetLong("max_fft")}");
        }
        finally
        {
            server.Dispose();
            backend.Dispose();
            log.Dispose();
        }
        foreach (bool optIn in new[] { true, false })
        {
            // Constructing the UHD backend loads no UHD: the radio opens only on Start.
            using var ulog = new SidecarLog(null, console: false);
            var uhd = new UhdBackend(ulog, new UhdBackendOptions { Serial = "KO6ETZB", AllowKnownCodecFault = optIn });
            Check(uhd.Info.KnownCodecFault == optIn && uhd.Caps.MaxFftSize == 1 << 20,
                  $"a UHD sidecar {(optIn ? "started with" : "without")} --allow-known-codec-fault states known_codec_fault {uhd.Info.KnownCodecFault?.ToString() ?? "(not stated)"} " +
                  $"and max_fft_size {uhd.Caps.MaxFftSize} before its radio opens");
            uhd.Dispose();
        }
    }

    // ---------------------------------------------------------------- live (an instrument, never a gate)

    /// <summary><c>--live &lt;serial&gt;|iiod:&lt;hw_serial&gt; [--iiod host[:port]] [--uhd dir] [--otw sc16|sc8] [--rate S/s] [--seconds n]
    /// [--allow-known-codec-fault]</c>: spawn the real sidecar on a real radio through the client library and report what
    /// arrived. <c>iiod:</c> names a Pluto-class board by its serial (ShackLink docs/plutosdr_support.md 9.10), with
    /// <c>--iiod</c> as its stored address (none = browse). Always exits 0 unless it cannot run; the numbers are the result.</summary>
    private static int LiveRun(string[] args)
    {
        string serial = args.Length > 1 ? args[1] : "";
        string? uhd = null, otw = null, iiod = null;
        double rate = 10e6;
        int seconds = 10;
        bool fault = false;
        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--uhd": uhd = args[++i]; break;
                case "--otw": otw = args[++i]; break;
                case "--iiod": iiod = args[++i]; break;
                case "--rate": rate = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seconds": seconds = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--allow-known-codec-fault": fault = true; break;
            }
        }
        bool pluto = serial.StartsWith("iiod:", StringComparison.OrdinalIgnoreCase);
        var address = pluto ? UsrpAddress.Iiod(serial.Substring(5)) : UsrpAddress.Local(serial);
        var extra = new List<string> { "--state-dir", Rendezvous.Root };
        if (otw != null) { extra.Add("--otw"); extra.Add(otw); }
        var opts = new UsrpSessionOptions
        {
            ClientName = "live-probe",
            SidecarExe = SidecarExe(),
            SpawnTimeoutMs = 20000,
            RequestTimeoutMs = 20000,
            Launch = new SidecarLaunch { UhdDirectory = uhd, AllowKnownCodecFault = fault, WideRateHz = rate, LingerSeconds = 2, IiodAddress = iiod, ExtraArgs = extra.ToArray() },
            Log = l => Console.WriteLine("  [session] " + l),
        };
        var sw = Stopwatch.StartNew();
        using var s = UsrpSession.Open(address, opts);
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
        // The transmitter as the radio opened it (section 9.17 P4-b) - read, never keyed.
        var ct = c.Tx;
        var tel0 = s.GetTelemetry().GetObject("radio")?.GetObject("source");
        Console.WriteLine(ct == null
            ? $"caps.tx: none - {tel0?.GetString("tx_why_not", "")}"
            : $"caps.tx: {ct.FreqMinHz}-{ct.FreqMaxHz} Hz, gain {ct.GainMinDb}-{ct.GainMaxDb} step {ct.GainStepDb}, " +
              $"rates {string.Join("/", ct.RatesHz.Select(r => (r / 1e3).ToString("0.###", CultureInfo.InvariantCulture)))} kS/s, " +
              $"antennas {string.Join(",", ct.Antennas)}, quantum {ct.QuantumSamples}, max LO offset {ct.MaxLoOffsetHz} Hz, sources {string.Join(",", ct.Sources)}, " +
              $"modulations {string.Join(",", ct.Modulations)} ({string.Join(",", ct.ModulatingWaveforms)} at {ct.ModulatingHarmonics} harmonics), dwell >= {ct.MinDwellMs} ms, <= {ct.MaxStepPoints} points");
        if (st.Tx != null)
            Console.WriteLine($"state.tx: {st.Tx.CenterHz} Hz (LO {st.Tx.LoHz}), {st.Tx.RateHz} S/s, {st.Tx.GainDb} dB, {st.Tx.Antenna}, keyed {st.Tx.Keyed}");

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
        // A Pluto's radio slot will hold a WIDE lease (decimation 1, B4): the board's whole stream, counted against the clock.
        LeaseStream? wide = pluto ? s.OpenLease(new LeaseRequest { CenterHz = st.LoHz, Decimation = 1 }) : null;
        long wideSamples = 0, wideDisc = 0;
        if (wide != null)
        {
            wide.Connection.IqBlockReceived += (conn, h, data) =>
            {
                Interlocked.Add(ref wideSamples, h.Count);
                if ((h.Flags & IqFlags.Discontinuity) != 0) Interlocked.Increment(ref wideDisc);
            };
            wide.Connection.StartReader();
        }
        pano.Connection.StartReader();
        lease.Connection.StartReader();
        var run = Stopwatch.StartNew();
        Thread.Sleep(seconds * 1000);
        double t = run.Elapsed.TotalSeconds;
        var tel = s.GetTelemetry();
        var src = tel.GetObject("radio")?.GetObject("source");
        if (pluto)
        {
            Console.WriteLine($"over {t:0.0} s at {st.WideRateHz / 1e6:0.##} MS/s from {src?.GetString("address")} (iiod {src?.GetString("iiod_version")}, firmware {src?.GetString("firmware")}):");
            Console.WriteLine($"  board: {src?.GetLong("buffers")} buffers of {src?.GetLong("buffer_samples")} samples = {src?.GetLong("samples") / t / 1e6:0.000} MS/s since open, " +
                              $"{src?.GetLong("buffers_overflow_flagged")} overflow-flagged (oracle {src?["overflow_oracle"]}), ADC clock {src?["adc_clock_hz"]}, " +
                              $"{src?["temperature_c"]} C, watch {src?.GetLong("watch_full")} full + {src?.GetLong("watch_quick")} quick, FIR found: '{src?.GetString("fir_found")}'");
            Console.WriteLine($"  wide lease: {Interlocked.Read(ref wideSamples) / t / 1e6:0.000} MS/s delivered, {Interlocked.Read(ref wideDisc)} discontinuities");
            Console.WriteLine($"  panorama: {rows} rows, {rowFfts / Math.Max(1, rows)} FFTs per row, {rowOverflows} overflows, median bin {rowMedian:0.0} dBFS");
            Console.WriteLine($"  lease {lease.Grant.RateHz / 1000:0} kHz: {samples / t / 1000:0.0} kS/s delivered, {blocks} blocks, {disc} discontinuities");
            foreach (var l in s.GetLog(40)) Console.WriteLine("  | " + l);
            wide?.Dispose();
            return 0;
        }
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
        // Section 9.33: the sidecar opted out of Windows power throttling - read back from Windows, with this gate's own
        // process, which did not, as the control (a read that cannot tell the two apart proves nothing).
        using (var child = Process.GetProcessById(pid))
        {
            var theirs = PowerThrottling.Read(child.Handle);
            var mine = PowerThrottling.Read(Process.GetCurrentProcess().Handle);
            Check(theirs is { } t && PowerThrottling.IsOptedOut(t) && mine is { } m && !PowerThrottling.IsOptedOut(m),
                  $"the spawned sidecar runs with execution-speed throttling OFF (control {theirs?.ControlMask}, state {theirs?.StateMask}), " +
                  $"where this gate's own process does not (control {mine?.ControlMask}, state {mine?.StateMask})");
        }
        Check(a.Caps.MaxFftSize == 1 << 20 && a.Radio.KnownCodecFault == false && b.Caps.MaxFftSize == 1 << 20 && b.Radio.KnownCodecFault == false,
              "across the process boundary both hellos state max_fft_size 2^20 and known_codec_fault false (CRTU200 A3)");
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
        Check(File.Exists(logPath) && File.ReadAllText(logPath).Contains("power throttling: off for this process"),
              "...and that it switched power throttling off for itself");
    }

    private static void FaultHookOptions()
    {
        Check(SidecarOptions.Parse(new[] { "--sim", "--sim-fault-after", "1500" }).SimFaultAfterMs == 1500,
              "--sim-fault-after takes milliseconds");
        Check(SidecarOptions.Parse(new[] { "--sim" }).SimFaultAfterMs == 0, "...and is off unless given");
        string? why = null;
        try { SidecarOptions.Parse(new[] { "--serial", "KO6ETZB", "--sim-fault-after", "10" }); }
        catch (ArgumentException ex) { why = ex.Message; }
        Check(why != null && why.Contains("--sim"), $"...and is refused on a real radio, in words ({why})");
    }

    private static void SettleHookOptions()
    {
        Check(SidecarOptions.Parse(new[] { "--sim", "--sim-settle-ms", "300" }).SimSettleMs == 300, "--sim-settle-ms takes milliseconds");
        Check(SidecarOptions.Parse(new[] { "--sim" }).SimSettleMs == 0, "...and is 0 (a change takes effect at the next sample) unless given");
        string? why = null;
        try { SidecarOptions.Parse(new[] { "--serial", "KO6ETZB", "--sim-settle-ms", "5" }); }
        catch (ArgumentException ex) { why = ex.Message; }
        Check(why != null && why.Contains("--sim"), $"...and is refused on a real radio, in words ({why})");
    }

    /// <summary>
    /// ShackLink docs/usrp_b210_support.md 9.11 item 8: the protocol library is built in the SIDECAR's
    /// configuration. It lives in ShackLink's tree, outside this solution, and MSBuild builds a
    /// ProjectReference the solution does not list in that project's default configuration (Debug) unless
    /// the reference says otherwise - so every Release sidecar shipped the Debug library, its JIT optimizer
    /// off, on the path of every IQ block and panorama row. Read from the PE metadata; nothing is loaded.
    /// </summary>
    private static void ProtocolLibraryConfiguration()
    {
        string exeDir = Path.GetDirectoryName(SidecarExe())!;
        string cfg = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.Name; // bin\<cfg>\net9.0\win-x64\
        bool release = cfg.Equals("Release", StringComparison.OrdinalIgnoreCase);
        foreach (var (what, path) in new[]
                 {
                     ("the sidecar's own assembly", Path.Combine(exeDir, "ShackLinkUsrp.dll")),
                     ("the protocol library it ships beside it", Path.Combine(exeDir, "ShackLinkUsrp.Protocol.dll")),
                     ("the protocol library this gate ran in process", typeof(UsrpServer).Assembly.Location),
                     // Referenced from ShackLink's tree the same way, so built the same way (ShackLink plutosdr 9.9).
                     ("the iiod library it ships beside it", Path.Combine(exeDir, "ShackLinkIio.dll")),
                     ("the iiod library this gate ran in process", typeof(ShackLinkIio.Ad936x.PlutoRadio).Assembly.Location),
                 })
        {
            bool? optimized = JitOptimized(path);
            Check(optimized == release,
                  $"{cfg}: {what} is {(optimized == true ? "optimized" : optimized == false ? "built with the JIT optimizer OFF" : "unreadable")} ({path})");
        }
    }

    /// <summary>False when the assembly's DebuggableAttribute switches the JIT optimizer off (a Debug
    /// build), true when it does not or carries none, null when the file cannot be read.</summary>
    private static bool? JitOptimized(string path)
    {
        try
        {
            using var pe = new PEReader(File.OpenRead(path));
            var md = pe.GetMetadataReader();
            foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes())
            {
                var ca = md.GetCustomAttribute(handle);
                if (ca.Constructor.Kind != HandleKind.MemberReference) continue;
                var ctor = md.GetMemberReference((MemberReferenceHandle)ca.Constructor);
                if (ctor.Parent.Kind != HandleKind.TypeReference ||
                    md.GetString(md.GetTypeReference((TypeReferenceHandle)ctor.Parent).Name) != "DebuggableAttribute") continue;
                var blob = md.GetBlobReader(ca.Value);
                blob.ReadUInt16();                                               // prolog
                if (blob.RemainingBytes >= 6) return (blob.ReadInt32() & 0x100) == 0; // (DebuggingModes): DisableOptimizations
                blob.ReadBoolean();                                              // (bool jitTracking, bool jitOptimizerDisabled)
                return !blob.ReadBoolean();
            }
            return true;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// ShackLink docs/usrp_b210_support.md 9.11 finding 1, on the real executable: a radio in a
    /// fault stays in it until the process ends, so a sidecar with no client left serves nobody.
    /// Lingering made the next two restart attempts join it, read the old fault and restart the
    /// linger by leaving. The linger here is 30 s, so an exit within seconds is the fault rule.
    /// </summary>
    private static void SpawnFaultExit()
    {
        string exe = SidecarExe();
        string stateDir = Rendezvous.Root;
        string name = "fault" + Environment.ProcessId;
        var opts = new UsrpSessionOptions
        {
            ClientName = "spawner",
            SidecarExe = exe,
            Launch = new SidecarLaunch { LingerSeconds = 30, ExtraArgs = new[] { "--state-dir", stateDir, "--sim-fault-after", "1500" } },
        };
        var addr = UsrpAddress.Sim(name);
        string logPath = Rendezvous.LogPathFor("sim-" + name);

        var a = UsrpSession.Open(addr, opts);
        int pid = a.SidecarPid;
        Check(a.Spawned && a.WaitReady(5000) == RadioStates.Ready, $"the spawned synthetic radio is ready (pid {pid})");
        var ev = a.OpenEvents();
        string? faultText = null;
        var faulted = new ManualResetEventSlim();
        ev.Connection.EventReceived += (c, n, body) =>
        {
            if (n != EventNames.RadioFault) return;
            faultText = body.GetString("text", "");
            faulted.Set();
        };
        ev.Connection.StartReader();
        Check(faulted.Wait(8000) && faultText != null && faultText.Contains("--sim-fault-after"),
              $"the radio faults on cue and says so on the event feed ({faultText})");
        Check(a.GetState().Readiness == RadioStates.Fault, "...and its state reads fault");
        Thread.Sleep(1200);
        Check(Rendezvous.IsProcessAlive(pid), "a faulted sidecar stays while a client is connected - that client can still read the fault");

        ev.Dispose();
        var sw = Stopwatch.StartNew();
        a.Dispose();
        bool gone = WaitFor(() => !Rendezvous.IsProcessAlive(pid), 12000);
        long exitMs = sw.ElapsedMilliseconds;
        Check(gone && exitMs < 5000, $"its last client leaving ends it at once: exited {exitMs} ms later, against a 30 s linger");
        Check(!File.Exists(Rendezvous.PathFor("sim-" + name)), "...and removes its rendezvous file");
        Check(File.Exists(logPath) && File.ReadAllText(logPath).Contains("stopping: the radio is in a fault and no client is connected"),
              "its log says why it stopped");

        // The next open - an auto-restart's - spawns a fresh sidecar instead of joining the dead one.
        // That one is left healthy and lingering; its own fault then ends it with nobody connected.
        int pid2;
        using (var b = UsrpSession.Open(addr, opts))
        {
            pid2 = b.SidecarPid;
            Check(b.Spawned && pid2 != pid && b.WaitReady(5000) == RadioStates.Ready,
                  $"the next open spawns a fresh sidecar (pid {pid2}, was {pid}) whose radio is ready");
        }
        Check(WaitFor(() => !Rendezvous.IsProcessAlive(pid2), 12000),
              "a radio that faults while its sidecar lingers with nobody connected ends it too, well inside the 30 s linger");
        Check(File.Exists(logPath) && File.ReadAllText(logPath).Contains("stopping: the radio is in a fault and no client is connected"),
              "...and says so");
    }
}
