// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// The Pluto backend's TRANSMITTER (ShackLink docs/plutosdr_support.md section 9.15, P4b), through the real protocol server,
// against the shared fake board (ShackLink's IiodWireTest\FakeIiod.cs): it has a TX DMA that is paced, raises its underflow
// flag when it runs dry, and records every buffer it was handed with the LO, powerdown and attenuation the buffer went out
// at. NOTHING RADIATES: there is no board here, and the radio says source = iiod at a loopback address.

using System.Diagnostics;
using System.Globalization;
using ShackLinkIio.Ad936x;
using ShackLinkUsrp.Client;
using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Host;
using ShackLinkUsrp.Radio;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Tests;

internal static partial class Program
{
    /// <summary>145.5 MHz: a multiple of four, so the fake's synthesizer lands on it exactly.</summary>
    private const long TxCentre = 145_500_000;

    private static readonly string TxLoFreq = FakeIiod.Key(FakeIiod.Phy, "OUTPUT", "altvoltage1", "frequency");
    private static readonly string TxLoPd = FakeIiod.Key(FakeIiod.Phy, "OUTPUT", "altvoltage1", "powerdown");
    private static readonly string Tx1Gain = FakeIiod.Key(FakeIiod.Phy, "OUTPUT", "voltage0", "hardwaregain");
    private static readonly string Tx1Bw = FakeIiod.Key(FakeIiod.Phy, "OUTPUT", "voltage0", "rf_bandwidth");

    private static (PlutoBackend backend, UsrpServer server, SidecarLog log) StartPlutoTx(PlutoBackendOptions opt, int starveMs = 500)
    {
        var log = new SidecarLog(null, console: false);
        var backend = new PlutoBackend(log, opt);
        var server = new UsrpServer(backend, new UsrpServerOptions { Port = 0, SidecarVersion = "test", LingerSeconds = 60, TxStarveMs = starveMs });
        server.Start();
        backend.Start();
        return (backend, server, log);
    }

    private static UsrpRequestException? RefusedTx(Action body)
    {
        try { body(); }
        catch (UsrpRequestException ex) { return ex; }
        return null;
    }

    /// <summary>The writes that touch the transmitter (its LO, TX1's attenuation and bandwidth, the TX DMA's rate), in order.</summary>
    private static List<string> TxWritesOf(FakeIiod f) =>
        f.AttributeWrites().Where(w => w.StartsWith(TxLoFreq + "=", StringComparison.Ordinal) || w.StartsWith(TxLoPd + "=", StringComparison.Ordinal) ||
                                       w.StartsWith(Tx1Gain + "=", StringComparison.Ordinal) || w.StartsWith(Tx1Bw + "=", StringComparison.Ordinal) ||
                                       (w.StartsWith(FakeIiod.Dds + "|OUTPUT|voltage", StringComparison.Ordinal) && w.Contains("|sampling_frequency="))).ToList();

    private static bool BoardQuiet(FakeIiod f) =>
        f.Get(FakeIiod.Phy, "OUTPUT", "altvoltage1", "powerdown") == "1" &&
        f.Get(FakeIiod.Phy, "OUTPUT", "voltage0", "hardwaregain") == "-89.750000 dB" &&
        f.Get(FakeIiod.Phy, "OUTPUT", "voltage1", "hardwaregain") == "-89.750000 dB";

    private static List<FakeIiod.TxBufferRecord> TxBuffersOf(FakeIiod f)
    {
        lock (f.Lock) return f.TxBuffers.ToList();
    }

    /// <summary>The samples the fake's DMA was handed, as sc16 pairs, concatenated.</summary>
    private static short[] TxSamplesOf(FakeIiod f)
    {
        var b = TxBuffersOf(f);
        var all = new short[b.Sum(x => x.Payload.Length) / 2];
        int at = 0;
        foreach (var x in b)
        {
            Buffer.BlockCopy(x.Payload, 0, all, at * 2, x.Payload.Length);
            at += x.Payload.Length / 2;
        }
        return all;
    }

    private static bool WaitTx(List<(string name, JsonObject body)> events, string state, string? reason, int ms) =>
        WaitFor(() =>
        {
            lock (events) return events.Any(e => e.name == EventNames.Tx && e.body.GetString("state") == state && (reason == null || e.body.GetString("reason") == reason));
        }, ms);

    // ---------------------------------------------------------------- the transmitter as it opens

    private static void PlutoTxCaps()
    {
        using var fake = Board();
        var p = StartPlutoTx(Opts(fake));
        try
        {
            using var s = Connect(p.server, "pluto-tx-caps");
            s.WaitReady(15000);
            Check(s.Radio.Source == RadioSources.Iiod, "the radio is source = iiod, at a LOOPBACK fake: nothing here can radiate");
            var c = s.Caps.Tx;
            Check(c != null && c.Timebase == TxTimebases.Host, "caps.tx is stated, timebase HOST (section 9.2 item 2): the sidecar stamps what it did");
            Check(c != null && c.RatesHz.SequenceEqual(new[] { 312_500.0, 2_500_000.0 }),
                  $"its rates are the TX DMA's own list at 2.5 MS/s - the wide rate and /8 - never a list of ours [{string.Join(", ", c?.RatesHz ?? Array.Empty<double>())}]");
            Check(c != null && c.GainMinDb == 0 && c.GainMaxDb == 89.75 && c.GainStepDb == 0.25 && c.FreqMinHz == 46_875_001 && c.FreqMaxHz == 6_000_000_000,
                  "gain 0-89.75 in 0.25 (the wire's 89.75 + hardwaregain); the TX LO 46.875001 MHz-6 GHz, as the board states it");
            Check(c != null && c.MaxLoOffsetHz == 0 && c.DualRx && c.Offers(TxSourceKinds.Iq) && c.Offers(TxSourceKinds.Tone) && c.Offers(TxSourceKinds.Mod) &&
                  c.Offers(TxSourceKinds.Step) && c.QuantumSamples == PlutoBackend.TxBufferSamplesFor(2_500_000) / 8 && c.QuantumSamples == 8192,
                  $"no LO offset (no DUC), dual_rx (TX1A keys whether RX B streams or not, 9.32 D24), all four sources (B5: streamed IQ, never the DDS), the quantum one TX DMA buffer in SOURCE samples " +
                  $"at the lowest rate ({c?.QuantumSamples}: 65,536 at the wide rate, interpolated from 8,192)");
            Check(c != null && c.MaxWideRateHz == 0,
                  $"caps.tx states NO max_wide_rate_hz (9.32 Q21): 2.5 MS/s is what ONE board was measured to transmit at beside its own receiver, " +
                  $"and another board, PC or link carries something else [{c?.MaxWideRateHz}]");
            var st = s.GetState().Tx!;
            Check(st.CenterHz == 0 && st.LoHz == 0 && st.GainDb == 0 && st.RateHz == 312_500 && st.Held != null && st.Held.CenterHz == 0 && double.IsNaN(st.Held.GainDb),
                  "state.tx at open: the truth in force (LO powered down = 0, gain 0 = the maximum attenuation), the lowest SOURCE rate, and NOTHING held");
            Check(fake.Get(FakeIiod.Dds, "OUTPUT", "voltage0", "sampling_frequency") == "2500000" && BoardQuiet(fake),
                  "the TX DMA was put at the WIDE rate, 2.5 MS/s - the one setting the owner's board's DMA runs right at (section 9.20) - and the board reads quiet");
            Check(!fake.AttributeWrites().Any(w => w.StartsWith(FakeIiod.Dds + "|OUTPUT|voltage", StringComparison.Ordinal) && w.Contains("|sampling_frequency=") &&
                                                    !w.EndsWith("=2500000", StringComparison.Ordinal)),
                  "...and NEVER at the wide/8 it lists: every write of the DMA's rate was the wide rate");
            Check(!fake.AttributeWrites().Any(w => w.StartsWith(TxLoFreq + "=", StringComparison.Ordinal)),
                  "the TX LO's FREQUENCY was never written while receiving (hazard 6: a quad-cal may clear the powerdown)");
            var tel = s.GetTelemetry().GetObject("radio")?.GetObject("source");
            Check(tel != null && tel.GetBool("tx_available") && tel.GetString("tx_keyed_expect") == "idle (SafeIdle)", "telemetry: a transmitter, idle");
        }
        finally { Stop(p); }

        // The synthetic radio states the other timebase, in the same commit (section 9.15 item 2).
        var (sb, ss, sl) = StartSimTx("timebase");
        try
        {
            using var s = Connect(ss, "timebase");
            s.WaitReady(5000);
            Check(s.Caps.Tx?.Timebase == TxTimebases.Radio, "...and the synthetic radio states timebase RADIO (TX and RX on one clock)");
        }
        finally
        {
            ss.Dispose();
            sb.Dispose();
            sl.Dispose();
        }
    }

    // ---------------------------------------------------------------- the hold (Q1)

    private static void PlutoTxHold()
    {
        using var fake = Board();
        var p = StartPlutoTx(Opts(fake));
        try
        {
            using var s = Connect(p.server, "pluto-tx-hold");
            s.WaitReady(15000);
            s.ClaimTx();
            int before = TxWritesOf(fake).Count;
            var reply = s.SetTx(new TxSettings { CenterHz = TxCentre, GainDb = 59.8, RateHz = 312_500 });
            var st = reply.State.Tx!;
            Check(st.Held != null && st.Held.CenterHz == TxCentre && st.Held.GainDb == 59.75 && st.CenterHz == 0 && st.LoHz == 0 && st.GainDb == 0,
                  "set_tx while idle: held {145.5 MHz, 59.75 dB on the grid}, and the truth in force unchanged (0, 0, 0) - fail-closed (Q1)");
            var w = TxWritesOf(fake).Skip(before).ToList();
            Check(w.Count == 0 && BoardQuiet(fake),
                  $"...and NOTHING reached the board: not the centre, not the gain, and not even the source's rate - the TX DMA stays at the wide rate [{string.Join(" | ", w)}]");
            Check(RefusedTx(() => s.SetTx(new TxSettings { GainDb = 95 }))?.Code == ErrorCodes.OutOfRange, "a gain above 89.75 is refused OUT_OF_RANGE");
            var off = RefusedTx(() => s.SetTx(new TxSettings { LoOffsetHz = 100_000 }));
            Check(off?.Code == ErrorCodes.Unsupported && off.Message.Contains("no LO offset"), "an LO offset is refused: there is no DUC to place one [" + off?.Message + "]");
            Check(RefusedTx(() => s.SetTx(new TxSettings { CenterHz = 10_000_000 }))?.Code == ErrorCodes.OutOfRange, "a centre below the TX LO's range is refused");
            Check(RefusedTx(() => s.SetTx(new TxSettings { RateHz = 1_000_000 }))?.Code == ErrorCodes.OutOfRange, "a rate the TX DMA does not list is refused");
            Check(s.GetState().Tx!.Held!.CenterHz == TxCentre, "...and a refusal changed nothing held");
        }
        finally { Stop(p); }

        // A gain never set: the transmission is refused - it keys at a stated level (P4-4's rule, at the radio).
        using var fresh = Board();
        var q = StartPlutoTx(Opts(fresh));
        try
        {
            using var s = Connect(q.server, "pluto-tx-nogain");
            s.WaitReady(15000);
            s.ClaimTx();
            s.SetTx(new TxSettings { CenterHz = TxCentre });
            var r = RefusedTx(() => s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 5000 }));
            Check(r?.Code == ErrorCodes.BadRequest && r.Message.Contains("never set"), "tx_start with no gain ever set is refused in words: " + r?.Message);
            Check(fresh.TxOpens == 0 && BoardQuiet(fresh), "...before the TX DMA was even opened");
        }
        finally { Stop(q); }
    }

    // ---------------------------------------------------------------- ShackLink's IQ, end to end

    private static void PlutoTxIq()
    {
        using var fake = Board();
        var p = StartPlutoTx(Opts(fake));
        try
        {
            using var s = Connect(p.server, "pluto-tx-iq");
            s.WaitReady(15000);
            s.ClaimTx();
            s.SetTx(new TxSettings { CenterHz = TxCentre, GainDb = 59.75, RateHz = 312_500 });
            var events = TxEvents(s, out var ev);
            int mark = fake.AttributeWrites().Count;

            var t = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 30_000 });
            Check(t.Grant.CenterHz == TxCentre && t.Grant.RateHz == 312_500 && t.Grant.GainDb == 59.75 && t.Grant.LoHz == TxCentre,
                  $"tx_start iq is granted at the HELD centre and gain, the LO on the carrier ({t.Grant.CenterHz} Hz, {t.Grant.GainDb} dB)");
            Thread.Sleep(300);
            Check(fake.TxOpens == 1 && TxBuffersOf(fake).Count == 0 && BoardQuiet(fake) && !s.GetState().Tx!.Keyed,
                  "the TX DMA is OPEN, but nothing is keyed before the first sample: the LO is down, the attenuation 89.75");

            const int total = 312_500;    // one second
            var iq = new short[2 * total];
            new ToneGenerator(25_000, 312_500, -6).Fill(iq, total);
            var sw = Stopwatch.StartNew();
            t.Stream!.Write(iq, 0, total);
            var r = t.Stop(drain: true);
            double secs = sw.Elapsed.TotalSeconds;
            Check(r.Acked && r.Reason == EndReasons.Drained && r.Samples == total && r.Underflows == 0,
                  $"drained on the BOARD's word - its DMA ran dry after the last sample - every sample sent, no underflow: {r}");
            Check(secs > 0.8 && secs < 2.0, $"one second of samples went out in {F(secs, "0.000")} s: the DMA paced the writer through the queue and TCP, no timer");

            var w = fake.AttributeWrites().Skip(mark).ToList();
            int bw = w.IndexOf(Tx1Bw + "=2500000"), att = w.IndexOf(Tx1Gain + "=-30"), lo = w.IndexOf(TxLoFreq + "=145500000"), up = w.IndexOf(TxLoPd + "=0");
            Check(bw >= 0 && att > bw && lo > att && up > lo,
                  $"the KEY-UP, at the first buffer: bandwidth (the wide rate), attenuation 30 dB (gain 59.75), TX LO, then powerdown 0 [{string.Join(" | ", w.Take(5))}]");
            int down = w.FindIndex(up + 1, x => x == TxLoPd + "=1");
            Check(down > up && BoardQuiet(fake), "every end is SafeIdle: the board reads quiet again");
            List<string> cmds;
            lock (fake.Lock) cmds = fake.Commands.ToList();
            int pdCmd = cmds.FindLastIndex(x => x.StartsWith("WRITE iio:device0 OUTPUT altvoltage1 powerdown", StringComparison.Ordinal));
            int close = cmds.FindLastIndex(x => x == "CLOSE iio:device2");
            Check(pdCmd >= 0 && close > pdCmd && fake.TxReleases == 1, "SafeIdle FIRST, then the TX buffer CLOSEd (item 7)");

            var bufs = TxBuffersOf(fake);
            Check(bufs.Count > 0 && bufs.All(b => !b.LoPoweredDown && b.LoHz == TxCentre && b.AttenuationDb == 30 && b.RateHz == 2_500_000 && b.ConsumedRateHz == 2_500_000),
                  $"every one of the {bufs.Count} buffers went out with the LO powered at 145.5 MHz, 30 dB, the TX DMA at the WIDE rate (2.5 MS/s, stated and " +
                  "consumed - section 9.21) - the board's own record");
            double went = (bufs[^1].DoneTs - bufs[0].StartTs) / (double)Stopwatch.Frequency, owedS = bufs.Sum(b => b.Payload.Length / 4) / 2_500_000.0;
            Check(Math.Abs(went - owedS) < 0.05 * owedS && owedS > 1.0 && owedS < 1.15,
                  $"the board CONSUMED it in real time: {F(went, "0.000")} s for {F(owedS, "0.000")} s of samples at 2.5 MS/s - one second of the client's " +
                  "samples took one second of air, plus the padding and the filter's tail");
            var outAt = bufs.SelectMany(b => fake.TxStatesDuring(b)).ToList();
            Check(outAt.Count >= bufs.Count && outAt.All(x => !x.LoPoweredDown && x.LoHz == TxCentre && x.AttenuationDb == 30),
                  "...and every buffer was CONSUMED - went OUT - at that state, not merely pushed at it (the fake's DMA, paced)");
            var last = bufs[^1];
            var quietAt = fake.TxHistory.FirstOrDefault(h => h.Timestamp > last.StartTs && h.LoPoweredDown);
            Check(quietAt != null && quietAt.Timestamp >= last.DoneTs,
                  $"the DRAIN: the last sample left before SafeIdle powered the LO down ({(quietAt == null ? "never" : F((quietAt.Timestamp - last.DoneTs) * 1000.0 / Stopwatch.Frequency, "0.0") + " ms after")})");
            var sent = TxSamplesOf(fake);
            int D = TxUpsampler.DelayInput;
            bool exact = sent.Length >= 2 * 8 * (total + D);
            for (int n = 0; exact && n < total; n++)
                exact = sent[2 * 8 * (n + D)] == iq[2 * n] && sent[2 * 8 * (n + D) + 1] == iq[2 * n + 1];
            Check(exact, $"x8 at the board: every 8th sample it received IS the client's, BIT FOR BIT, all {total} of them ({D} source samples late, the filter's centre)");
            int quietFrom = 2 * 8 * (total + 2 * D + 1);
            Check(sent.Length > quietFrom && sent.Skip(quietFrom).All(v => v == 0) && sent.Skip(2 * 8 * (total + D)).Take(quietFrom - 2 * 8 * (total + D)).Any(v => v != 0),
                  "...then the filter's tail - pushed out before the end, never cut - then only zeros");
            double img = WorstImageDbc(sent, 8192, 65536, 2_500_000, 312_500, 25_000, 8);
            Check(img <= -74, $"IMAGE REJECTION, measured in what the board was sent: the strongest image of the 25 kHz tone (k x 312.5 kHz +- 25 kHz) at {F(img, "0.0")} dBc - " +
                              "no worse than the 12-bit DAC's own floor (74 dB), the HDL interpolator's job done here");
            var stuffed = new short[iq.Length * 8];
            for (int n = 0; n < total; n++)
            {
                stuffed[16 * n] = iq[2 * n];
                stuffed[16 * n + 1] = iq[2 * n + 1];
            }
            double raw = WorstImageDbc(stuffed, 8192, 65536, 2_500_000, 312_500, 25_000, 8);
            Check(raw > -3, $"ORACLE: the same measurement on the tone ZERO-STUFFED x8 - the interpolation undone - reads its images at {F(raw, "0.0")} dBc: it sees images when they are there");
            Check(WaitTx(events, TxStates.Keyed, null, 1000) && WaitTx(events, TxStates.Idle, EndReasons.Drained, 2000),
                  "tx keyed, then idle (drained), said to every client");
            var after = s.GetState().Tx!;
            Check(after.CenterHz == 0 && after.GainDb == 0 && after.Held?.CenterHz == TxCentre && after.Held?.GainDb == 59.75,
                  "afterwards: the truth in force idle again, and what was held still held for the next key-up");
            var tel = s.GetTelemetry().GetObject("radio")?.GetObject("source");
            Check(tel != null && tel.GetLong("tx_key_ups") == 1 && tel.GetString("tx_keyed_expect") == "idle (SafeIdle)", "telemetry: one key-up, idle again");
            ev.Dispose();
        }
        finally { Stop(p); }
    }

    // ---------------------------------------------------------------- every other end

    private static void PlutoTxEnds()
    {
        using var fake = Board();
        var p = StartPlutoTx(Opts(fake), starveMs: 300);
        try
        {
            using var s = Connect(p.server, "pluto-tx-ends");
            s.WaitReady(15000);
            s.ClaimTx();
            s.SetTx(new TxSettings { CenterHz = TxCentre, GainDb = 40, RateHz = 312_500 });
            var events = TxEvents(s, out var ev);
            var iq = new short[2 * 100_000];
            new ToneGenerator(10_000, 312_500, -10).Fill(iq, 100_000);

            // An abort: ended where it stood, not acknowledged, and quiet.
            var a = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 30_000 });
            a.Stream!.Write(iq, 0, 100_000);
            var ra = a.Stop(drain: false);
            Check(!ra.Acked && BoardQuiet(fake) && fake.TxReleases == 1, $"an abort: not acknowledged, SafeIdle, the TX buffer closed: {ra}");

            // A pause longer than the board's queue (four 8,192-sample buffers, 105 ms) and shorter than the starvation limit:
            // the DMA runs dry mid-burst, and that is an UNDERFLOW, counted.
            var u = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 30_000 });
            u.Stream!.Write(iq, 0, 40_000);
            Thread.Sleep(200);
            u.Stream.Write(iq, 0, 40_000);
            var ru = u.Stop(drain: true);
            Check(ru.Acked && ru.Underflows >= 1 && ru.Samples == 80_000,
                  $"a feed that pauses past the board's queue is an UNDERFLOW, read off the board's flag and counted ({ru.Underflows}); the rest still drained: {ru}");

            // Starvation: the server ends a keyed transmission that is fed nothing.
            var b = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 30_000 });
            b.Stream!.Write(iq, 0, 20_000);
            Check(WaitTx(events, TxStates.Idle, EndReasons.Starved, 3000) && WaitFor(() => BoardQuiet(fake), 2000),
                  "a keyed transmission fed nothing for 300 ms is ended STARVED, and the board quieted");
            b.Dispose();

            // A wedged board: a push the board never answers is ended by the abort, and the control connection still quiets it.
            var c = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 30_000 });
            c.Stream!.Write(iq, 0, 30_000);
            Check(WaitFor(() => fake.Get(FakeIiod.Phy, "OUTPUT", "altvoltage1", "powerdown") == "0", 2000), "keyed");
            fake.StallTxWriteBuf = true;
            var writer = Task.Run(() => { try { c.Stream.Write(iq, 0, 100_000); } catch { } });
            Thread.Sleep(400);
            var sw = Stopwatch.StartNew();
            var rc = c.Stop(drain: false);
            double ms = sw.Elapsed.TotalMilliseconds;
            fake.StallTxWriteBuf = false;
            Check(ms < 4000 && WaitFor(() => BoardQuiet(fake), 3000), $"a WEDGED push is ended by the abort in {ms:0} ms, and SafeIdle on the control connection quiets the board");
            writer.Wait(3000);

            // A refused key-up: the synthesizer lands 50 Hz off. Nothing is powered, the board is quiet, and it is said.
            fake.TxLoLandingOffsetHz = -50;
            int mark = fake.AttributeWrites().Count;
            var d = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 30_000 });
            try { d.Stream!.Write(iq, 0, 30_000); } catch { }
            Check(WaitTx(events, TxStates.Idle, EndReasons.RadioFault, 3000), "a REFUSED key-up ends the transmission radio_fault");
            fake.TxLoLandingOffsetHz = 0;
            Check(!fake.AttributeWrites().Skip(mark).Any(x => x == TxLoPd + "=0") && BoardQuiet(fake),
                  "...powerdown 0 was never written, and the board reads quiet");
            var tel = s.GetTelemetry().GetObject("radio")?.GetObject("source");
            Check(tel != null && tel.GetLong("tx_key_ups_refused") == 1 && LogText(p.backend).Contains("did not take what was asked"),
                  "...counted, and the board's words are in the log");
            d.Dispose();

            // Another program holds the TX DMA: one writer per device, refused in words before anything is keyed.
            using (var other = ShackLinkIio.Protocol.IiodConnection.Connect("127.0.0.1", fake.Port, 3000))
            {
                other.Open(FakeIiod.Dds, 4096, "00000003", false);
                var e = RefusedTx(() => s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 5000 }));
                Check(e?.Code == ErrorCodes.TxActive && e.Message.Contains("another program holds this board's transmitter"),
                      "a TX DMA another program holds refuses tx_start TX_ACTIVE, in words: " + e?.Message);
            }
            Check(BoardQuiet(fake) && p.backend.GetState().Readiness == RadioStates.Ready, "...the board quiet and the radio still ready");
            ev.Dispose();
        }
        finally { Stop(p); }
    }

    // ---------------------------------------------------------------- the generated sources

    private static void PlutoTxGenerated()
    {
        using var fake = Board();
        var p = StartPlutoTx(Opts(fake));
        try
        {
            using var s = Connect(p.server, "pluto-tx-gen");
            s.WaitReady(15000);
            s.ClaimTx();
            s.SetTx(new TxSettings { CenterHz = TxCentre, GainDb = 60 });
            var events = TxEvents(s, out var ev);

            // A tone: keyed at once, at the held centre, 50 kHz from it in the samples.
            var t = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone, MaxMs = 30_000, OffsetHz = 50_000, LevelDbfs = -10 });
            // The server says keyed at a generated source's tx_start (its own fact); the BOARD keys a moment later, at the key-up.
            bool keyedEv = WaitTx(events, TxStates.Keyed, null, 2000) && WaitFor(() => fake.Get(FakeIiod.Phy, "OUTPUT", "altvoltage1", "powerdown") == "0", 2000);
            string? loNow = fake.Get(FakeIiod.Phy, "OUTPUT", "altvoltage1", "frequency");
            Check(keyedEv && loNow == "145500000", $"a tone keys at its start (event {keyedEv}), the LO at the held centre ({loNow})");
            Thread.Sleep(400);
            // The SECOND buffer: the first opens with the interpolator's start (its history is zeros), which is no tone yet.
            var first = TxBuffersOf(fake).Skip(1).First();
            var x = new short[first.Payload.Length / 2];
            Buffer.BlockCopy(first.Payload, 0, x, 0, first.Payload.Length);
            double turn = 0;
            int pairs = x.Length / 2 - 1;
            for (int i = 0; i < pairs; i++)
            {
                double a0 = Math.Atan2(x[2 * i + 1], x[2 * i]), a1 = Math.Atan2(x[2 * i + 3], x[2 * i + 2]);
                double dd = a1 - a0;
                if (dd > Math.PI) dd -= 2 * Math.PI;
                if (dd < -Math.PI) dd += 2 * Math.PI;
                turn += dd;
            }
            double hz = turn / pairs / (2 * Math.PI) * first.RateHz;
            Check(Math.Abs(hz - 50_000) < 50 && first.RateHz == 2_500_000 && t.Grant.RateHz == 312_500,
                  $"the samples ARE the tone: {F(hz, "0")} Hz from the LO, generated at {t.Grant.RateHz:0} S/s (the lowest rate that holds it) and sent at the " +
                  $"DMA's {first.RateHz:0} - a generated source is interpolated x8 too");
            var genImg = WorstImageDbc(TxSamplesOf(fake), 8192, 65536, 2_500_000, 312_500, 50_000, 8);
            Check(genImg <= -74, $"...its images measured in what the board was sent: {F(genImg, "0.0")} dBc");

            // A park while keyed: no CORDIC, so the synthesizer - rf - read back; the next transmission keys there too.
            var park = s.SetTx(new TxSettings { CenterHz = TxCentre + 1_000_000 });
            Check(park.Retune == TxWords.RetuneRf && fake.Get(FakeIiod.Phy, "OUTPUT", "altvoltage1", "frequency") == "146500000" &&
                  s.GetState().Tx!.CenterHz == TxCentre + 1_000_000 && s.GetState().Tx!.Held!.CenterHz == TxCentre + 1_000_000,
                  "a park while keyed moves the SYNTHESIZER (rf), read back; state.tx and what is held follow");

            // A gain move while keyed: written at once, read back - and the watch, expecting it, does not fault.
            s.SetTx(new TxSettings { GainDb = 40 });
            Check(fake.Get(FakeIiod.Phy, "OUTPUT", "voltage0", "hardwaregain") == "-49.750000 dB" && s.GetState().Tx!.GainDb == 40,
                  "a gain move while keyed: 49.75 dB of attenuation, read back, in force");
            Thread.Sleep(1200);   // four of the watch's 300 ms checks
            Check(p.backend.GetState().Readiness == RadioStates.Ready && s.GetState().Tx!.Keyed,
                  "...and the watch, expecting THIS transmitter's state, has not read it as another program's");
            var rt = t.Stop(drain: true);
            Check(rt.Acked && BoardQuiet(fake), $"a generated source drains on the board's word too: {rt}");
            var endBuf = TxBuffersOf(fake).Last();
            var endS = new short[endBuf.Payload.Length / 2];
            Buffer.BlockCopy(endBuf.Payload, 0, endS, 0, endBuf.Payload.Length);
            Check(endS.Skip(endS.Length - 2 * 8 * TxUpsampler.TapsPerPhase).All(v => v == 0) && endS.Take(2 * 8 * TxUpsampler.HalfLength).Any(v => v != 0),
                  "...and its interpolated TAIL went out first: the last buffer the board was sent opens with the tail and ends in zeros - never a carrier cut mid-filter");

            // A BASEBAND sweep: the steps generated at exact samples, the LO written ONCE. (The tone's own "drained" is not this.)
            lock (events) events.Clear();
            int lo0 = fake.AttributeWrites().Count(w => w.StartsWith(TxLoFreq + "=", StringComparison.Ordinal));
            var sweep = s.StartTx(new TxStartRequest
            {
                Source = TxSourceKinds.Step, MaxMs = 30_000, StartHz = TxCentre - 100_000, StopHz = TxCentre + 100_000, Points = 5,
                Spacing = TxWords.Lin, DwellMs = 40, LevelDbfs = -10,
            });
            Check(WaitTx(events, TxStates.Idle, EndReasons.Drained, 4000), "a baseband sweep runs to its end and ends drained");
            List<JsonObject> steps;
            lock (events) steps = events.Where(e => e.name == EventNames.TxStep).Select(e => e.body).ToList();
            Check(steps.Count == 5 && steps.All(e => e.GetString("timing") == TxStepPlan.TimingSample) &&
                  fake.AttributeWrites().Count(w => w.StartsWith(TxLoFreq + "=", StringComparison.Ordinal)) == lo0 + 1,
                  $"five steps, each timing = sample, and the TX LO written ONCE ({steps.Count} steps)");
            Check(BoardQuiet(fake), "...and the board quiet after it");
            sweep.Dispose();

            // A TUNED sweep: every step an LO write, made only once the DMA ran DRY - each buffer went out at its own step's LO.
            lock (events) events.Clear();
            int buf0 = TxBuffersOf(fake).Count;
            var tuned = s.StartTx(new TxStartRequest
            {
                Source = TxSourceKinds.Step, MaxMs = 30_000, StartHz = 145_000_000, StopHz = 148_000_000, Points = 3,
                Spacing = TxWords.Lin, DwellMs = 100, LevelDbfs = -10,
            });
            Check(WaitTx(events, TxStates.Idle, EndReasons.Drained, 6000), "a tuned sweep runs to its end and ends drained");
            lock (events) steps = events.Where(e => e.name == EventNames.TxStep).Select(e => e.body).ToList();
            Check(steps.Count == 3 && steps.Skip(1).All(e => e.GetString("retune") == TxWords.RetuneRf && e.GetString("timing") == TxStepPlan.TimingBound) &&
                  steps.Select(e => e.GetLong("center_hz")).SequenceEqual(new[] { 145_000_000L, 146_500_000L, 148_000_000L }),
                  $"three steps: each LO move rf, timing bound, at 145 / 146.5 / 148 MHz [{string.Join(", ", steps.Select(e => e.GetLong("center_hz")))}]");
            var tunedBufs = TxBuffersOf(fake).Skip(buf0).ToList();
            var los = tunedBufs.Select(b => b.LoHz).ToList();
            bool ordered = los.Count >= 3 && los.SequenceEqual(los.OrderBy(v => v)) &&
                           new[] { 145_000_000L, 146_500_000L, 148_000_000L }.All(f => los.Contains(f)) && los.All(f => f == 145_000_000L || f == 146_500_000L || f == 148_000_000L);
            Check(ordered, $"every buffer was pushed at ITS step's LO, in order ({los.Count} buffers)");
            // The rule a tuned step exists for: the board still holds up to four buffers when a step's last is pushed, so an LO move
            // then would put them out at the NEXT step's LO. What was in force while each buffer was CONSUMED is the proof.
            var moved = tunedBufs.Where(b => fake.TxStatesDuring(b).Any(st => st.LoHz != b.LoHz || st.LoPoweredDown)).ToList();
            Check(moved.Count == 0,
                  $"...and every one was CONSUMED at it too: no LO move while a buffer was going out - each step waited for the DMA to run dry ({moved.Count} of {tunedBufs.Count} left at another LO)");
            // ...and the interpolator's TAIL of a step went out at that step's LO: the first buffer at a new LO starts from a SETTLED filter,
            // so its every-8th samples are the new step's from zero history - the first DelayInput of them exactly zero, nothing carried over.
            var carried = new List<int>();
            for (int i = 1; i < tunedBufs.Count; i++)
            {
                if (tunedBufs[i].LoHz == tunedBufs[i - 1].LoHz) continue;
                var pl = tunedBufs[i].Payload;
                for (int j = 0; j < TxUpsampler.DelayInput; j++)
                    if (BitConverter.ToInt16(pl, 4 * 8 * j) != 0 || BitConverter.ToInt16(pl, 4 * 8 * j + 2) != 0)
                    {
                        carried.Add(i);
                        break;
                    }
            }
            Check(carried.Count == 0 && tunedBufs.Select(b => b.LoHz).Distinct().Count() == 3,
                  $"...and each step's interpolated tail left at ITS OWN LO before the next moved: every step's first buffer starts from a settled filter ({carried.Count} carried the last step's samples over)");
            Check(s.GetState().Tx!.Held!.CenterHz == TxCentre + 1_000_000 && BoardQuiet(fake), "afterwards the held centre is what the client had set, and the board is quiet");
            tuned.Dispose();
            ev.Dispose();
        }
        finally { Stop(p); }
    }

    // ---------------------------------------------------------------- what is said while a key-up runs

    /// <summary>The key-up's window held open (the fake answers the TX LO write 400 ms late): until the board has keyed, state.tx
    /// says what is IN FORCE - nothing - however far the transmission has got; then the carrier it keyed at.</summary>
    private static void PlutoTxKeyUpWindow()
    {
        using var fake = Board();
        var p = StartPlutoTx(Opts(fake));
        try
        {
            using var s = Connect(p.server, "pluto-tx-window");
            s.WaitReady(15000);
            s.ClaimTx();
            s.SetTx(new TxSettings { CenterHz = TxCentre, GainDb = 40 });
            fake.SlowTxLoWriteMs = 400;
            var t = s.StartTx(new TxStartRequest
            {
                Source = TxSourceKinds.Step, MaxMs = 30_000, StartHz = 145_000_000, StopHz = 148_000_000, Points = 3,
                Spacing = TxWords.Lin, DwellMs = 300, LevelDbfs = -10,
            });
            Thread.Sleep(150);
            var during = s.GetState().Tx!;
            Check(fake.Get(FakeIiod.Phy, "OUTPUT", "altvoltage1", "powerdown") == "1" && during.CenterHz == 0 && during.LoHz == 0,
                  $"while the key-up runs the LO is still down, and state.tx says so: centre {during.CenterHz}, LO {during.LoHz} - never the sweep's plan as if it were in force");
            Check(WaitFor(() => s.GetState().Tx!.CenterHz == 145_000_000 && fake.Get(FakeIiod.Phy, "OUTPUT", "altvoltage1", "powerdown") == "0", 3000),
                  "...and once the board has keyed, the step it keyed at");
            fake.SlowTxLoWriteMs = 0;
            t.Stop(drain: false);
            Check(BoardQuiet(fake), "quiet after");
        }
        finally { Stop(p); }
    }

    // ---------------------------------------------------------------- a transmitter that cannot be read

    /// <summary>A board whose TX statements cannot be read still OPENS TO RECEIVE, and says it does not transmit (section 9.15).</summary>
    private static void PlutoTxUnreadable()
    {
        using var fake = Board();
        lock (fake.Lock) fake.Attrs.Remove(FakeIiod.Key(FakeIiod.Phy, "OUTPUT", "altvoltage1", "frequency_available"));
        var p = StartPlutoTx(Opts(fake));
        try
        {
            using var s = Connect(p.server, "pluto-tx-unreadable");
            Check(s.WaitReady(15000) == RadioStates.Ready, "a board whose TX LO range cannot be read still opens: the receiver does not depend on it");
            Check(s.Caps.Tx == null, "...and states NO transmitter (caps.tx absent) - never a guessed one");
            var tel = s.GetTelemetry().GetObject("radio")?.GetObject("source");
            Check(tel != null && !tel.GetBool("tx_available") && tel.GetString("tx_why_not").Contains("could not be read"),
                  "...saying why, in words: " + tel?.GetString("tx_why_not"));
            try { s.ClaimTx(); } catch (UsrpRequestException) { }
            var r = RefusedTx(() => s.SetTx(new TxSettings { CenterHz = TxCentre, GainDb = 30 }));
            Check(r?.Code == ErrorCodes.Unsupported && r.Message.Contains("does not transmit"), "set_tx on it is refused UNSUPPORTED, in words: " + r?.Message);
        }
        finally { Stop(p); }
    }

    // ---------------------------------------------------------------- a board with no debugfs (section 9.2 item 5)

    private static void PlutoTxNoOracle()
    {
        using var fake = AsBoard(new FakeIiod(FakeIiod.WithoutDebugFs(FakeIiod.BoardContext())).LoadBoardState());
        var p = StartPlutoTx(Opts(fake));
        try
        {
            using var s = Connect(p.server, "pluto-tx-nooracle");
            s.WaitReady(15000);
            Check(s.Caps.OverflowOracle == false && s.Caps.Tx != null, "a board with no debugfs states overflow_oracle false - and still a transmitter");
            s.ClaimTx();
            s.SetTx(new TxSettings { CenterHz = TxCentre, GainDb = 30 });
            var t = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone, MaxMs = 10_000, OffsetHz = 10_000, LevelDbfs = -10 });
            Check(WaitFor(() => fake.Get(FakeIiod.Phy, "OUTPUT", "altvoltage1", "powerdown") == "0", 2000), "it keys");
            Thread.Sleep(200);
            var r = t.Stop(drain: true);
            Check(!r.Acked && r.Text.Contains("no debugfs") && BoardQuiet(fake),
                  $"...but a drain is NOT acknowledged - nothing can say when its last sample left - in words, and the board quiet: {r}");
        }
        finally { Stop(p); }
    }

    // ---------------------------------------------------------------- the watch while keyed (B11)

    private static void PlutoTxForeign()
    {
        using var fake = Board();
        var p = StartPlutoTx(Opts(fake));
        try
        {
            using var s = Connect(p.server, "pluto-tx-foreign");
            s.WaitReady(15000);
            s.ClaimTx();
            s.SetTx(new TxSettings { CenterHz = TxCentre, GainDb = 50 });
            var events = TxEvents(s, out var ev);
            var t = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone, MaxMs = 30_000, OffsetHz = 20_000, LevelDbfs = -10 });
            Check(WaitTx(events, TxStates.Keyed, null, 2000) && WaitFor(() => fake.Get(FakeIiod.Phy, "OUTPUT", "altvoltage1", "powerdown") == "0", 2000),
                  "keyed - the board's own key-up done");
            int mark = fake.AttributeWrites().Count;
            var watchBefore = p.backend.GetTelemetry().GetObject("source");
            fake.Set(FakeIiod.Phy, "OUTPUT", "altvoltage1", "frequency", "433000000");
            bool faulted = WaitFor(() => p.backend.GetState().Readiness == RadioStates.Fault, 3000);
            string text = p.backend.GetState().FaultText ?? "";
            var watchAfter = p.backend.GetTelemetry().GetObject("source");
            Check(faulted && text.Contains("another program changed this board") && text.Contains("while this sidecar transmits") && text.Contains("433"),
                  "another program moving the TX LO WHILE KEYED faults the radio in words: " + text +
                  (faulted ? "" : $" [NOT faulted: full watches {watchBefore?.GetLong("watch_full")} -> {watchAfter?.GetLong("watch_full")}, " +
                                  $"expect '{watchAfter?.GetString("tx_keyed_expect")}', state {p.backend.GetState().Readiness}; log: " +
                                  string.Join(" / ", p.backend.GetLog(8)) + "]"));
            Check(WaitTx(events, TxStates.Idle, null, 3000), "...the transmission ends");
            Check(!fake.AttributeWrites().Skip(mark).Any(w => w.StartsWith(TxLoPd + "=", StringComparison.Ordinal) || w.StartsWith(Tx1Gain + "=", StringComparison.Ordinal)),
                  "...and NO SafeIdle is written over the other program's settings (B11: it may be keyed on purpose)");
            t.Dispose();
            ev.Dispose();
        }
        finally { Stop(p); }
    }

    // ---------------------------------------------------------------- one clock chain (item 1)

    private static void PlutoTxRate()
    {
        using var fake = Board();
        var p = StartPlutoTx(Opts(fake));
        try
        {
            using var s = Connect(p.server, "pluto-tx-rate");
            s.WaitReady(15000);
            s.ClaimLo();
            s.SetRate(10e6);
            s.WaitReady(5000);    // re-reads the caps, which follow the wide rate
            var c = s.Caps.Tx;
            Check(c != null && c.RatesHz.SequenceEqual(new[] { 1_250_000.0, 10_000_000.0 }), "a rate change re-reads the TX DMA's list: 1.25 and 10 MS/s at 10 MS/s");
            Check(s.GetState().Tx!.RateHz == 1_250_000 && fake.Get(FakeIiod.Dds, "OUTPUT", "voltage0", "sampling_frequency") == "10000000" &&
                  LogText(p.backend).Contains("TX rate moved from 312500 to 1250000"),
                  "...the source rate the new one does not list moved to the lowest it does, said - and the DMA put at the NEW WIDE rate, 10 MS/s");
            s.ClaimTx();
            s.SetTx(new TxSettings { CenterHz = TxCentre, GainDb = 30 });
            // Section 9.32 Q21 (the owner's rule): a key-up at ANY wide rate the board states. 10 MS/s starved beside its own receiver on the
            // owner's board over Ethernet - and that is the link's to say, through the underflow count and a starved over's end, never a
            // refusal measured on one board.
            int opens = fake.TxOpens;
            var t10 = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone, MaxMs = 30_000, OffsetHz = 10_000, LevelDbfs = -10 });
            Check(WaitFor(() => fake.Get(FakeIiod.Phy, "OUTPUT", "altvoltage1", "powerdown") == "0", 3000) && fake.TxOpens == opens + 1,
                  "a key-up at a 10 MS/s wide rate is ADMITTED and keys - no ceiling measured on one board refuses it (9.32 Q21)");
            t10.Stop(drain: false);
            Check(WaitFor(() => BoardQuiet(fake), 3000), "...and its end is SafeIdle, as every end is");
            // 4 MS/s was Q7's refusal (both 120 s runs on the owner's board starved there): admitted too.
            s.SetRate(4e6);
            s.WaitReady(5000);
            Check(s.Caps.Tx!.RatesHz.SequenceEqual(new[] { 500_000.0, 4_000_000.0 }) && fake.Get(FakeIiod.Dds, "OUTPUT", "voltage0", "sampling_frequency") == "4000000",
                  "at 4 MS/s: the source rates 500 kS/s and 4 MS/s, the DMA at 4 MS/s - the list is the board's (B9)");
            var t4 = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone, MaxMs = 30_000, OffsetHz = 10_000, LevelDbfs = -10 });
            Check(WaitFor(() => fake.Get(FakeIiod.Phy, "OUTPUT", "altvoltage1", "powerdown") == "0", 3000) && fake.TxOpens == opens + 2,
                  "a key-up at 4 MS/s is ADMITTED and keys (Q7's refusal is gone with Q21)");
            t4.Stop(drain: false);
            Check(WaitFor(() => BoardQuiet(fake), 3000), "...and ends quiet");
            s.SetRate(2.5e6);
            s.WaitReady(5000);
            Check(s.Caps.Tx!.RatesHz.SequenceEqual(new[] { 312_500.0, 2_500_000.0 }) && fake.Get(FakeIiod.Dds, "OUTPUT", "voltage0", "sampling_frequency") == "2500000",
                  "at 2.5 MS/s: the source rates 312.5 kS/s and 2.5 MS/s, the DMA at 2.5 MS/s");
            var t = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone, MaxMs = 30_000, OffsetHz = 10_000, LevelDbfs = -10 });
            Check(WaitFor(() => fake.Get(FakeIiod.Phy, "OUTPUT", "altvoltage1", "powerdown") == "0", 2000), "...and the same key-up is admitted and keys");
            var r = RefusedTx(() => s.SetRate(4e6));
            Check(r?.Code == ErrorCodes.TxActive, "set_rate under a transmission is refused TX_ACTIVE: the wide rate is the transmitter's clock too");
            // The backend's OWN net, reached past the server (which refuses first and would hide its absence).
            RadioRefusal? own = null;
            try { p.backend.SetRate(4e6); }
            catch (RadioRefusal ex) { own = ex; }
            Check(own?.Code == ErrorCodes.TxActive && p.backend.GetState().WideRateHz == 2.5e6,
                  "...and the backend refuses it ITSELF, asked directly: the board's rate never moves under its transmitter: " + own?.Message);
            var rt = t.Stop(drain: true);
            Check(rt.Acked && rt.Underflows == 0 && BoardQuiet(fake),
                  $"2.5 MS/s each way runs CLEAN on the board as measured, and so on the fake: drained on the board's word, no underflow: {rt}");
        }
        finally { Stop(p); }
    }

    // ---------------------------------------------------------------- P4e: the interpolation (section 9.21, Q4)

    /// <summary>A sc16 level at <paramref name="f"/>: a 4-term Blackman-Harris window, a DFT evaluated in double precision at exactly that
    /// frequency, normalised to the window's sum - dB re full scale.</summary>
    private static double LevelDb(short[] iq, int start, int count, double fs, double f)
    {
        double sr = 0, si = 0, wsum = 0;
        double c0 = Math.Cos(-2 * Math.PI * f / fs), s0 = Math.Sin(-2 * Math.PI * f / fs), cr = 1, ci = 0;
        for (int n = 0; n < count; n++)
        {
            double a = 2 * Math.PI * n / (count - 1);
            double w = 0.35875 - 0.48829 * Math.Cos(a) + 0.14128 * Math.Cos(2 * a) - 0.01168 * Math.Cos(3 * a);
            double x = iq[2 * (start + n)] / 32768.0, y = iq[2 * (start + n) + 1] / 32768.0;
            sr += w * (x * cr - y * ci);
            si += w * (x * ci + y * cr);
            wsum += w;
            double t = cr * c0 - ci * s0;
            ci = cr * s0 + ci * c0;
            cr = t;
            if ((n & 4095) == 4095)
            {
                // re-anchor the rotation: no drift over 65,536 steps
                double ph = -2 * Math.PI * f / fs * (n + 1);
                cr = Math.Cos(ph);
                ci = Math.Sin(ph);
            }
        }
        return 20 * Math.Log10(Math.Sqrt(sr * sr + si * si) / wsum + 1e-15);
    }

    /// <summary>The strongest image of a tone at <paramref name="f"/> - at k x <paramref name="fsIn"/> +- f, k = 1..L-1, each searched
    /// two bins either side - relative to the tone: dBc, negative. What x<paramref name="l"/> interpolation leaves of the source.</summary>
    private static double WorstImageDbc(short[] iq, int start, int count, double fsOut, double fsIn, double f, int l)
    {
        double tone = LevelDb(iq, start, count, fsOut, f);
        double bin = fsOut / count, worst = -400;
        for (int k = 1; k < l; k++)
            foreach (double centre in new[] { k * fsIn + f, k * fsIn - f })
            {
                double at = centre > fsOut / 2 ? centre - fsOut : centre;
                for (double d = -2; d <= 2; d += 0.5)
                    worst = Math.Max(worst, LevelDb(iq, start, count, fsOut, at + d * bin));
            }
        return worst - tone;
    }

    /// <summary>A source at the WIDE rate needs nothing done: its samples go to the board as they are, bit for bit, in real time.</summary>
    private static void PlutoTxAtTheWideRate()
    {
        using var fake = Board();
        var p = StartPlutoTx(Opts(fake));
        try
        {
            using var s = Connect(p.server, "pluto-tx-wide");
            s.WaitReady(15000);
            s.ClaimTx();
            s.SetTx(new TxSettings { CenterHz = TxCentre, GainDb = 40, RateHz = 2_500_000 });
            var t = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 30_000 });
            const int total = 1_250_000;     // half a second at 2.5 MS/s
            var iq = new short[2 * total];
            new ToneGenerator(300_000, 2_500_000, -6).Fill(iq, total);
            t.Stream!.Write(iq, 0, total);
            var r = t.Stop(drain: true);
            var sent = TxSamplesOf(fake);
            var bufs = TxBuffersOf(fake);
            Check(r.Acked && r.Samples == total && sent.Length >= iq.Length && sent.AsSpan(0, iq.Length).SequenceEqual(iq) && sent.Skip(iq.Length).All(v => v == 0),
                  $"a source at the wide rate goes to the board BIT FOR BIT ({total} samples), then only the last buffer's padding: {r}");
            double went = (bufs[^1].DoneTs - bufs[0].StartTs) / (double)Stopwatch.Frequency;
            Check(bufs.All(b => b.ConsumedRateHz == 2_500_000) && Math.Abs(went - sent.Length / 2 / 2_500_000.0) < 0.03,
                  $"...consumed at 2.5 MS/s in {F(went, "0.000")} s");
        }
        finally { Stop(p); }
    }

    /// <summary>The filter's tail: what is still to come out of a source's last samples leaves BEFORE the end - when the zeros padding
    /// the last buffer cannot hold it (the stream ends within a filter's length of a buffer's edge), one more buffer of zeros; when the
    /// stream ends exactly on the edge, that buffer alone, as the final push the drain is timed from.</summary>
    private static void PlutoTxTail()
    {
        foreach (int shortBy in new[] { 10, 0 })
        {
            using var fake = Board();
            var p = StartPlutoTx(Opts(fake));
            try
            {
                using var s = Connect(p.server, "pluto-tx-tail");
                s.WaitReady(15000);
                s.ClaimTx();
                s.SetTx(new TxSettings { CenterHz = TxCentre, GainDb = 40, RateHz = 312_500 });
                int total = 8192 * 10 - shortBy;
                var iq = new short[2 * total];
                new ToneGenerator(20_000, 312_500, -6).Fill(iq, total);
                var t = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 30_000 });
                t.Stream!.Write(iq, 0, total);
                var r = t.Stop(drain: true);
                var sent = TxSamplesOf(fake);
                int D = TxUpsampler.DelayInput;
                int lastExact = 2 * 8 * (total - 1 + D), quietFrom = 2 * 8 * (total + 2 * D);
                bool exact = sent.Length > lastExact && sent[lastExact] == iq[2 * (total - 1)] && sent[lastExact + 1] == iq[2 * (total - 1) + 1];
                bool tailWhole = sent.Length >= quietFrom && sent.Skip(2 * 8 * (total + D)).Take(quietFrom - 2 * 8 * (total + D)).Any(v => v != 0) &&
                                 sent.Skip(quietFrom).All(v => v == 0);
                Check(r.Acked && exact && tailWhole && TxBuffersOf(fake).Count == 11,
                      $"a stream {(shortBy == 0 ? "ending EXACTLY on a buffer's edge" : $"ending {shortBy} samples short of one")}: its last sample out exactly, its tail " +
                      $"whole in an 11th buffer of zeros, then quiet - and the drain acknowledged after it ({TxBuffersOf(fake).Count} buffers): {r}");
            }
            finally { Stop(p); }
        }
    }

    /// <summary>A tuned step whose boundary falls 23 samples short of a buffer's edge (498 ms at 312.5 kS/s is 155,625 samples: 19 buffers
    /// of 8,192 less 23): the 23 zeros padding that buffer cannot hold the interpolator's tail (29 inputs), so ONE more buffer of zeros
    /// goes out at THAT step's LO before the LO moves - 20 buffers at the first LO, never 19 with the tail's last part at the next.</summary>
    private static void PlutoTxTunedTail()
    {
        using var fake = Board();
        var p = StartPlutoTx(Opts(fake));
        try
        {
            using var s = Connect(p.server, "pluto-tx-tuned-tail");
            s.WaitReady(15000);
            s.ClaimTx();
            s.SetTx(new TxSettings { CenterHz = TxCentre, GainDb = 40 });
            var events = TxEvents(s, out var ev);
            var tuned = s.StartTx(new TxStartRequest
            {
                Source = TxSourceKinds.Step, MaxMs = 30_000, StartHz = 145_000_000, StopHz = 148_000_000, Points = 2,
                Spacing = TxWords.Lin, DwellMs = 498, LevelDbfs = -10,
            });
            Check(WaitTx(events, TxStates.Idle, EndReasons.Drained, 6000), "a two-step tuned sweep runs to its end");
            var bufs = TxBuffersOf(fake);
            var first = bufs.Where(b => b.LoHz == 145_000_000).ToList();
            var tail = first.Count > 0 ? first[^1] : null;
            var ts = new short[tail == null ? 0 : tail.Payload.Length / 2];
            if (tail != null) Buffer.BlockCopy(tail.Payload, 0, ts, 0, tail.Payload.Length);
            Check(tuned.Grant.RateHz == 312_500 && first.Count == 20 && ts.Take(2 * 8 * TxUpsampler.HalfLength).Any(v => v != 0) && ts.Skip(2 * 8 * TxUpsampler.TapsPerPhase).All(v => v == 0),
                  $"the first step: 19 buffers of its own and a 20th of the interpolator's TAIL, all at 145 MHz, before the LO moved ({first.Count} at 145 MHz)");
            tuned.Dispose();
            ev.Dispose();
        }
        finally { Stop(p); }
    }

    /// <summary>The drain's deadline is the board's queue at the rate the DMA really runs at - the WIDE rate - plus the margin: four
    /// 65,536-sample buffers at 2.5 MS/s are 105 ms, so a board that never says its DMA ran dry is given 355 ms, never the 1,089 its
    /// source's rate would make of it.</summary>
    private static void PlutoTxDrainDeadline()
    {
        using var fake = Board();
        fake.TxUnderflowFlagStuck = true;
        var p = StartPlutoTx(Opts(fake));
        try
        {
            using var s = Connect(p.server, "pluto-tx-deadline");
            s.WaitReady(15000);
            s.ClaimTx();
            s.SetTx(new TxSettings { CenterHz = TxCentre, GainDb = 40, RateHz = 312_500 });
            var iq = new short[2 * 50_000];
            new ToneGenerator(20_000, 312_500, -10).Fill(iq, 50_000);
            var t = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 30_000 });
            t.Stream!.Write(iq, 0, 50_000);
            var r = t.Stop(drain: true);
            Check(!r.Acked && r.Text.Contains($"did not run dry within {105 + PlutoTransmission.DrainMarginMs} ms") && BoardQuiet(fake),
                  $"a board whose DMA never says it ran dry: NOT acknowledged, after the queue's time at the WIDE rate plus the margin (355 ms), and quiet: {r}");
        }
        finally { Stop(p); }
    }

    /// <summary>TxUpsampler alone: exact on every 8th sample, the same output however the input is cut into calls, flat to 0.4 of the input
    /// rate, its first image at the stopband's edge, and every clip counted.</summary>
    private static void PlutoUpsamplerUnit()
    {
        const int n = 40_000, L = 8, D = TxUpsampler.HalfLength;
        var rng = new Random(20261001);
        var x = new short[2 * n];
        for (int i = 0; i < x.Length; i++) x[i] = (short)rng.Next(-12000, 12000);
        var whole = new short[2 * n * L];
        new TxUpsampler(L).Process(x, n, whole);
        bool exact = true;
        for (int i = D; i < n && exact; i++) exact = whole[2 * L * i] == x[2 * (i - D)] && whole[2 * L * i + 1] == x[2 * (i - D) + 1];
        Check(exact, $"every 8th output IS the input, bit for bit, {D} input samples late (a Nyquist filter's zero-phase branch is one tap of exactly 1)");

        var pieces = new short[2 * n * L];
        var up = new TxUpsampler(L);
        for (int at = 0; at < n;)
        {
            int k = Math.Min(n - at, 1 + rng.Next(3000));
            up.Process(x.AsSpan(2 * at, 2 * k), k, pieces.AsSpan(2 * L * at, 2 * L * k));
            at += k;
        }
        Check(pieces.AsSpan().SequenceEqual(whole), "PHASE-CONTINUOUS: cut into calls of random length, the output is identical sample for sample");

        foreach (double frac in new[] { 0.1, 0.4 })
        {
            double fsIn = 312_500, f = frac * fsIn;
            var tone = new short[2 * 20_000];
            new ToneGenerator(f, fsIn, -6).Fill(tone, 20_000);
            var o = new short[2 * 20_000 * L];
            new TxUpsampler(L).Process(tone, 20_000, o);
            double inDb = LevelDb(tone, 2048, 8192, fsIn, f), outDb = LevelDb(o, 16_384, 65_536, fsIn * L, f);
            double img = WorstImageDbc(o, 16_384, 65_536, fsIn * L, fsIn, f, L);
            Check(Math.Abs(outDb - inDb) < 0.01 && img <= (frac == 0.4 ? -80 : -85),
                  $"a tone at {F(frac, "0.0")} of the input rate: level {F(outDb - inDb, "0.0000")} dB from the input's, worst image {F(img, "0.0")} dBc " +
                  (frac == 0.4 ? "(at the passband's EDGE: its first image sits on the stopband's edge, 0.6 - computed -86 dB)" : ""));
        }

        // A sine at a quarter of the input rate, sampled 45 degrees off its peaks: every sample at full scale, its true peak 3 dB above it.
        var loud = new short[2 * 64];
        for (int i = 0; i < 64; i++) loud[2 * i] = (short)(i % 4 < 2 ? 32767 : -32767);
        var lo = new short[2 * 64 * L];
        var clip = new TxUpsampler(L);
        clip.Process(loud, 64, lo);
        Check(clip.Clipped > 0 && lo.All(v => v >= short.MinValue && v <= short.MaxValue), $"an overshoot past full scale is CLIPPED and COUNTED ({clip.Clipped}), never wrapped");

        var settle = new TxUpsampler(L);
        var one = new short[] { 1000, 0 };
        var zeros = new short[2 * TxUpsampler.TapsPerPhase];
        var sink = new short[2 * TxUpsampler.TapsPerPhase * L];
        settle.Process(one, 1, sink);
        bool notYet = !settle.Settled;
        settle.Process(zeros, TxUpsampler.TapsPerPhase - 1, sink);
        bool stillNot = !settle.Settled;
        settle.Process(zeros, 1, sink);
        Check(notYet && stillNot && settle.Settled, $"Settled only after {TxUpsampler.TapsPerPhase} zero inputs - when nothing of an earlier sample is still to come out");
    }

    // ---------------------------------------------------------------- what only the source can show

    private static void PlutoTxSourceRules()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "src", "ShackLinkUsrp", "Radio"));
        string backend = File.ReadAllText(Path.Combine(root, "PlutoBackend.cs"));
        string tx = File.ReadAllText(Path.Combine(root, "PlutoTransmission.cs"));
        Check(CountOf(backend + tx, ".KeyTx(") == 1,
              "ONE key-up path: PlutoBackend.KeyUp's KeyTx - the library's sequence, whose refusals leave the board quiet");
        Check(!(backend + tx).Contains("Control.Write") && !(backend + tx).Contains("IioAttrRef"),
              "the sidecar writes NOTHING to the board directly: every write is one of the library's named setters, each read back - " +
              "so no powerdown and no DDS scale (B5: streamed IQ, never the DDS) can be written outside them");
        Check(!(backend + tx).Contains("ToneChannels"), "...and it never even names the DDS's tone channels");
        int safe = tx.IndexOf("_pluto.TxEndSafeIdle(keyed)", StringComparison.Ordinal), close = tx.IndexOf("CloseStream();\n            Finish(", StringComparison.Ordinal);
        if (close < 0) close = tx.IndexOf("CloseStream();\r\n            Finish(", StringComparison.Ordinal);
        Check(safe > 0 && close > safe, "item 7: every end writes SafeIdle BEFORE the TX buffer is closed (the finally's order)");
        int expect = backend.IndexOf("_txExpect = null;\n                var r = _radio;", StringComparison.Ordinal);
        if (expect < 0) expect = backend.IndexOf("_txExpect = null;\r\n                var r = _radio;", StringComparison.Ordinal);
        Check(expect > 0, "the watch's expectation is cleared and SafeIdle written under ONE hold of the board (no window where our own carrier reads as another program's)");
        Check(CountOf(backend, "SetTxCoreRate(") == 2 && CountOf(backend, "SetTxCoreRate(wide)") == 2,
              "section 9.21 (Q4): the TX DMA's rate is SET in two places, and both set it to the WIDE rate - never a source's wide/8");
        Check(CountOf(tx, "new TxUpsampler(") == 1 && CountOf(tx, "_up?.Process(samples, n, dma)") == 1,
              "...and a wide/8 source reaches the DMA through ONE interpolator per transmission, every buffer through it");
    }
}
