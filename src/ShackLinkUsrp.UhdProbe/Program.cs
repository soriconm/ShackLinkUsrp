// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using ShackLinkUsrp.Uhd;

namespace ShackLinkUsrp.UhdProbe;

/// <summary>
/// An INSTRUMENT, never a gate: it drives the ShackLinkUsrp.Uhd binding against a real radio and prints
/// what it measured. Exit 0 whenever it ran (a bad measurement is still a measurement), 2 on a usage
/// error, 3 when UHD or the radio could not be reached at all.
///
///   --uhd DIR  --allow-known-codec-fault  --images DIR  --args "serial=..."  --load altered|native|dlldir
///   --find | --info | --stream [--rate 10e6] [--freq 100e6] [--gain 30] [--seconds 5] [--otw sc16|sc8]
///   --txqc (TxQcProbe: the AD9361 TX quad cal under the TXQC diagnostic uhd.dll; UHD_TXQC_LOG set; nothing sent)
///                              [--lo-offset 0] [--buffer SAMPLES]
/// </summary>
internal static class Program
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static int Main(string[] argv)
    {
        var a = new Args(argv);
        if (a.Has("--help") || a.Mode.Length == 0)
        {
            Console.WriteLine("usage: ShackLinkUsrp.UhdProbe (--find | --info | --stream | --txqc) [--uhd DIR] [--allow-known-codec-fault] " +
                              "[--images DIR] [--args \"serial=...\"] [--load altered|native|dlldir] [--rate HZ] [--freq HZ] " +
                              "[--gain DB] [--seconds S] [--otw sc16|sc8] [--lo-offset HZ] [--buffer SAMPLES]");
            return a.Has("--help") ? 0 : 2;
        }

        var options = new UhdLoadOptions
        {
            UhdDirectory = a.Get("--uhd"),
            AllowKnownCodecFault = a.Has("--allow-known-codec-fault"),
            ImagesDirectory = a.Get("--images"),
            Mechanism = (a.Get("--load") ?? "altered").ToLowerInvariant() switch
            {
                "native" => UhdLoadMechanism.NativeLibraryLoad,
                "dlldir" => UhdLoadMechanism.SetDllDirectory,
                _ => UhdLoadMechanism.AlteredSearchPath,
            },
        };

        try
        {
            UhdLoader.Load(options, s => Console.WriteLine("[load] " + s));
        }
        catch (Exception ex)
        {
            Console.WriteLine("[load] FAILED: " + ex.Message);
            return 3;
        }

        string args = a.Get("--args") ?? "type=b200";
        try
        {
            return a.Mode switch
            {
                "--find" => Find(args),
                "--info" => Info(args),
                "--stream" => Stream(args, a),
                "--txqc" => TxQcProbe.Run(args, a.Get, a.GetDouble),
                _ => 2,
            };
        }
        catch (UhdException ex)
        {
            Console.WriteLine($"[uhd] {ex.Message}");
            return 3;
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"[usage] {ex.Message}");
            return 2;
        }
    }

    private static int Find(string args)
    {
        var sw = Stopwatch.StartNew();
        IReadOnlyList<string> found = UhdUsrp.Find(args);
        Console.WriteLine($"[find] uhd_usrp_find(\"{args}\") -> {found.Count} device(s) in {sw.ElapsedMilliseconds} ms");
        foreach (string d in found) Console.WriteLine("  " + d);
        return 0;
    }

    private static int Info(string args)
    {
        using UhdUsrp usrp = OpenTimed(args);
        Console.WriteLine("[info] " + usrp.PrettyPrint.Replace("\n", "\n       ", StringComparison.Ordinal));
        Console.WriteLine($"[info] mboard name: {usrp.GetMboardName()}");
        UhdRxInfo ri = usrp.GetRxInfo();
        Console.WriteLine($"[info] rx info: mboard_id={ri.MboardId} mboard_name={ri.MboardName} mboard_serial={ri.MboardSerial} " +
                          $"rx_id={ri.RxId} subdev={ri.RxSubdevName} spec={ri.RxSubdevSpec} rx_serial={ri.RxSerial} antenna={ri.RxAntenna}");
        Console.WriteLine($"[info] master clock: {usrp.GetMasterClockRate().ToString("R", Inv)} Hz; clock source {usrp.GetClockSource()} of [{string.Join(", ", usrp.GetClockSources())}]");
        Console.WriteLine($"[info] rx channels: {usrp.RxChannelCount}; current rate {usrp.GetRxRate().ToString("R", Inv)} S/s");
        Console.WriteLine($"[info] rx rates (current clock): {Describe(usrp.GetRxRates())}");
        Console.WriteLine($"[info] rx freq range: {Describe(usrp.GetRxFrequencyRange())}; front end {Describe(usrp.GetFrontEndRxFrequencyRange())}");
        Console.WriteLine($"[info] rx gain range: {Describe(usrp.GetRxGainRange())}; stages [{string.Join(", ", usrp.GetRxGainNames())}]");
        Console.WriteLine($"[info] rx bandwidth range: {Describe(usrp.GetRxBandwidthRange())}; current {usrp.GetRxBandwidth().ToString("R", Inv)} Hz");
        Console.WriteLine($"[info] rx antennas: [{string.Join(", ", usrp.GetRxAntennas())}], selected {usrp.GetRxAntenna()}");
        return 0;
    }

    private static int Stream(string args, Args a)
    {
        double rate = a.GetDouble("--rate", 10e6);
        double freq = a.GetDouble("--freq", 100e6);
        double gain = a.GetDouble("--gain", 30);
        double seconds = a.GetDouble("--seconds", 5);
        double loOffset = a.GetDouble("--lo-offset", 0);
        string otw = a.Get("--otw") ?? "sc16";

        using UhdUsrp usrp = OpenTimed(args);
        double gotRate = usrp.SetRxRate(rate);
        UhdTuneResult tune = usrp.TuneRx(freq, loOffset);
        double gotGain = usrp.SetRxGain(gain);
        Console.WriteLine($"[stream] rate requested {rate.ToString("R", Inv)} -> {gotRate.ToString("R", Inv)} S/s; master clock {usrp.GetMasterClockRate().ToString("R", Inv)} Hz");
        Console.WriteLine($"[stream] tune {freq.ToString("R", Inv)} Hz: rf target {tune.TargetRfHz.ToString("R", Inv)} actual {tune.ActualRfHz.ToString("R", Inv)}, " +
                          $"dsp target {tune.TargetDspHz.ToString("R", Inv)} actual {tune.ActualDspHz.ToString("R", Inv)}; readback {tune.ReadbackHz.ToString("R", Inv)}");
        Console.WriteLine($"[stream] gain requested {gain} -> {gotGain.ToString("R", Inv)} dB; otw {otw}");

        using UhdRxStreamer rx = usrp.CreateRxStreamer(otw);
        int bufSamples = (int)a.GetDouble("--buffer", Math.Max(rx.MaxSamplesPerPacket * 16, 16384));
        var buf = new short[bufSamples * 2];
        Console.WriteLine($"[stream] streamer ready: max_num_samps {rx.MaxSamplesPerPacket}, recv buffer {bufSamples} samples");

        UhdError prio = UhdUsrp.TrySetThreadPriority();
        Console.WriteLine($"[stream] uhd_set_thread_priority(0.5, realtime) -> {prio}");

        long total = 0, calls = 0, overflows = 0, timeouts = 0, oosCount = 0, otherErrors = 0, gapSamples = 0;
        // Samples that arrived AFTER the clock started: the first buffer was filled before its own
        // arrival started the stopwatch, and counting it overstated the rate by one buffer.
        long rateSamples = 0;
        double sumI = 0, sumQ = 0, sumI2 = 0, sumQ2 = 0;
        long statSamples = 0;
        double lastTime = double.NaN;
        long lastCount = 0;
        var errorCodes = new Dictionary<UhdRxErrorCode, long>();

        var start = Stopwatch.StartNew();
        rx.StartContinuous();
        Stopwatch? sinceFirst = null;
        double firstSampleMs = double.NaN;

        while (true)
        {
            // The first recv waits out the stream's start-up; afterwards a stall this long is a fault.
            double timeout = sinceFirst == null ? 5.0 : 0.5;
            int got = rx.Recv(buf, timeout, out UhdRxMetadata md);
            calls++;
            if (md.ErrorCode != UhdRxErrorCode.None)
            {
                errorCodes[md.ErrorCode] = errorCodes.GetValueOrDefault(md.ErrorCode) + 1;
                if (md.ErrorCode == UhdRxErrorCode.Overflow) overflows++;
                else if (md.ErrorCode == UhdRxErrorCode.Timeout) timeouts++;
                else otherErrors++;
            }
            if (md.OutOfSequence) oosCount++;

            if (got > 0)
            {
                bool firstBuffer = sinceFirst == null;
                if (firstBuffer)
                {
                    sinceFirst = Stopwatch.StartNew();
                    firstSampleMs = start.Elapsed.TotalMilliseconds;
                }
                else
                {
                    rateSamples += got;
                }
                // Gap accounting from UHD's own time stamps: where this block SHOULD have started
                // given the last one, against where it did. This is benchmark_rate's "dropped".
                if (md.HasTimeSpec)
                {
                    double t = md.FullSecs + md.FracSecs;
                    if (!double.IsNaN(lastTime))
                    {
                        double expected = lastTime + lastCount / gotRate;
                        double gap = (t - expected) * gotRate;
                        if (gap > 0.5) gapSamples += (long)Math.Round(gap);
                    }
                    lastTime = t;
                    lastCount = got;
                }
                total += got;
                // DC statistics on every 8th buffer: enough samples to say where the mean sits, cheap
                // enough that the instrument does not cause the overflows it is counting.
                if ((calls & 7) == 0)
                {
                    for (int i = 0; i < got; i++)
                    {
                        double iv = buf[2 * i], qv = buf[2 * i + 1];
                        sumI += iv; sumQ += qv; sumI2 += iv * iv; sumQ2 += qv * qv;
                    }
                    statSamples += got;
                }
            }
            if (sinceFirst != null && sinceFirst.Elapsed.TotalSeconds >= seconds) break;
            if (sinceFirst == null && start.Elapsed.TotalSeconds > 10)
            {
                Console.WriteLine("[stream] no samples within 10 s of the start command");
                break;
            }
        }
        double elapsed = sinceFirst?.Elapsed.TotalSeconds ?? 0;
        rx.Stop();

        double delivered = elapsed > 0 ? rateSamples / elapsed : 0;
        double meanI = statSamples > 0 ? sumI / statSamples : double.NaN;
        double meanQ = statSamples > 0 ? sumQ / statSamples : double.NaN;
        double sdI = statSamples > 0 ? Math.Sqrt(Math.Max(0, sumI2 / statSamples - meanI * meanI)) : double.NaN;
        double sdQ = statSamples > 0 ? Math.Sqrt(Math.Max(0, sumQ2 / statSamples - meanQ * meanQ)) : double.NaN;
        double lossPct = total + gapSamples > 0 ? 100.0 * gapSamples / (total + gapSamples) : 0;

        Console.WriteLine($"[stream] first samples {firstSampleMs:F0} ms after the start command");
        Console.WriteLine($"[stream] {total} samples in {elapsed:F3} s = {delivered.ToString("F0", Inv)} S/s delivered " +
                          $"({100.0 * delivered / gotRate:F2} % of {gotRate.ToString("R", Inv)}); {calls} recv calls");
        Console.WriteLine($"[stream] overflows {overflows}, timeouts {timeouts}, out-of-sequence {oosCount}, other errors {otherErrors}; " +
                          $"time-stamp gaps {gapSamples} samples ({lossPct:F3} % lost)");
        if (errorCodes.Count > 0)
            Console.WriteLine("[stream] error codes: " + string.Join(", ", errorCodes.Select(kv => $"{kv.Key}={kv.Value}")));
        Console.WriteLine($"[stream] I mean {meanI:F1} sd {sdI:F1}; Q mean {meanQ:F1} sd {sdQ:F1} (sc16 counts over {statSamples} samples)");
        Console.WriteLine(string.Create(Inv,
            $"SUMMARY otw={otw} rate={gotRate} delivered={delivered:F0} pct={100.0 * delivered / gotRate:F2} overflows={overflows} " +
            $"timeouts={timeouts} oos={oosCount} gap_samples={gapSamples} loss_pct={lossPct:F3} meanI={meanI:F1} meanQ={meanQ:F1} sdI={sdI:F1} sdQ={sdQ:F1}"));
        return 0;
    }

    private static UhdUsrp OpenTimed(string args)
    {
        Console.WriteLine($"[open] uhd_usrp_make(\"{args}\") - a cold B2xx loads firmware and bitstream first (~16 s)");
        var sw = Stopwatch.StartNew();
        UhdUsrp usrp = UhdUsrp.Open(args);
        Console.WriteLine($"[open] opened in {sw.Elapsed.TotalSeconds:F1} s");
        return usrp;
    }

    private static string Describe(UhdMetaRange r)
    {
        if (r.Ranges.Count == 0) return "(empty)";
        string head = $"{r.Start.ToString("G9", Inv)}..{r.Stop.ToString("G9", Inv)} step {r.Step.ToString("G6", Inv)}";
        if (r.Ranges.Count <= 1) return head;
        IEnumerable<string> parts = r.Ranges.Take(6).Select(x => $"[{x.Start.ToString("G9", Inv)}..{x.Stop.ToString("G9", Inv)}/{x.Step.ToString("G6", Inv)}]");
        return $"{head} over {r.Ranges.Count} ranges {string.Join(" ", parts)}{(r.Ranges.Count > 6 ? " ..." : "")}";
    }

    private sealed class Args
    {
        private readonly string[] _v;
        public string Mode { get; }

        public Args(string[] v)
        {
            _v = v;
            Mode = v.FirstOrDefault(x => x is "--find" or "--info" or "--stream" or "--txqc") ?? "";
        }

        public bool Has(string name) => _v.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

        public string? Get(string name)
        {
            for (int i = 0; i < _v.Length - 1; i++)
                if (string.Equals(_v[i], name, StringComparison.OrdinalIgnoreCase)) return _v[i + 1];
            return null;
        }

        public double GetDouble(string name, double fallback)
        {
            string? s = Get(name);
            if (s == null) return fallback;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            throw new ArgumentException($"{name} expects a number, got '{s}'");
        }
    }
}
