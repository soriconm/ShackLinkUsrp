// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// The Pluto backend's gate (ShackLink docs/plutosdr_support.md section 9.5, P2): the backend over ShackLinkIio
// against a FAKE iiod playing the owner's board's own context and read-backs - in process through the real
// protocol server, and the real executable spawned with --iiod. No board, and nothing radiates.

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ShackLinkUsrp.Client;
using ShackLinkUsrp.Host;
using ShackLinkUsrp.Radio;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Tests;

internal static partial class Program
{
    private const string Serial = FakeIiod.BoardSerial;

    /// <summary>A small buffer keeps the cadence quick (13 ms at 2.5 MS/s); the default is P1's ~33 ms.</summary>
    private const int TestBufferSamples = 32768;

    /// <summary>The shared fake board as a BACKEND sees it: paced at the board's own cadence, RX and TX alike.</summary>
    private static FakeIiod AsBoard(FakeIiod f)
    {
        f.Paced = true;
        f.TxPaced = true;
        return f;
    }

    private static FakeIiod Board(string? context = null) => AsBoard(new FakeIiod(context ?? FakeIiod.BoardContext()).LoadBoardState());

    private static (PlutoBackend backend, UsrpServer server, SidecarLog log) StartPluto(PlutoBackendOptions opt)
    {
        var log = new SidecarLog(null, console: false);
        var backend = new PlutoBackend(log, opt);
        var server = new UsrpServer(backend, new UsrpServerOptions { Port = 0, SidecarVersion = "test", LingerSeconds = 60 });
        server.Start();
        backend.Start();
        return (backend, server, log);
    }

    private static PlutoBackendOptions Opts(FakeIiod? fake, Func<IReadOnlyList<(string, int)>>? browse = null, string? hint = "", double rate = 2.5e6) => new()
    {
        Serial = Serial,
        AddressHint = hint == "" ? (fake == null ? null : "127.0.0.1:" + fake.Port.ToString(CultureInfo.InvariantCulture)) : hint,
        WideRateHz = rate,
        Browse = browse ?? (() => Array.Empty<(string, int)>()),
        BufferSamples = TestBufferSamples,
        ConnectTimeoutMs = 2500,
        WatchIntervalMs = 300,
    };

    private static void Stop((PlutoBackend backend, UsrpServer server, SidecarLog log) p)
    {
        p.server.Dispose();
        p.backend.Dispose();
        p.log.Dispose();
    }

    private static string LogText(PlutoBackend b) => string.Join("\n", b.GetLog(400));

    // ---------------------------------------------------------------- the command line

    private static void PlutoCommandLine()
    {
        var o = SidecarOptions.Parse(new[] { "--serial", Serial.ToUpperInvariant(), "--iiod", "169.254.14.41" });
        Check(o.Iiod == "169.254.14.41" && o.Address().Kind == UsrpAddressKind.Iiod && o.Address().RendezvousKey == "iiod-" + Serial,
              "--serial <hw_serial> --iiod <host> is an iiod radio, its rendezvous key iiod-<serial> in the board's own lower case");
        Check(SidecarOptions.Parse(new[] { "--serial", Serial, "--iiod", "mdns" }).Iiod == SidecarLocator.IiodBrowse, "--iiod mdns: no address, browse");
        Check(SidecarOptions.Parse(new[] { "--serial", Serial, "--iiod", "[fe80::1]:30431" }).Iiod == "[fe80::1]:30431", "...an IPv6 hint is bracketed");
        foreach (var (args, word) in new (string[], string)[]
                 {
                     (new[] { "--sim", "--iiod", "1.2.3.4" }, "--sim"),
                     (new[] { "--serial", Serial, "--iiod", "a b" }, "host[:port]"),
                     (new[] { "--serial", Serial, "--iiod", "fe80::1" }, "host[:port]"),
                     (new[] { "--serial", Serial, "--iiod", "1.2.3.4", "--uhd", "C:\\uhd" }, "--uhd"),
                     (new[] { "--serial", Serial, "--iiod", "1.2.3.4", "--otw", "sc8" }, "--otw"),
                     (new[] { "--serial", Serial, "--iiod", "1.2.3.4", "--allow-known-codec-fault", "--args", "x=1" }, "--args, --allow-known-codec-fault"),
                     (new[] { "--serial", Serial, "--iiod", "1.2.3.4", "--dc-offset", "1000" }, "--dc-offset"),
                     (new[] { "--serial", "iiod" }, "reserved"),
                 })
        {
            string? why = null;
            try { SidecarOptions.Parse(args); }
            catch (ArgumentException ex) { why = ex.Message; }
            Check(why != null && why.Contains(word, StringComparison.Ordinal), $"refused in words naming {word}: {string.Join(" ", args)} [{why}]");
        }
        // B9 is two bounds: the no-FIR floor AND the board's statement - a range stated below the floor (a FIR's,
        // or a board claiming more) still offers nothing under 2,083,334 S/s.
        var withFir = PlutoBackend.OfferedRates(520_833, 30_720_000);
        Check(withFir.Min() == 2.5e6 && withFir.Max() == 30.72e6 && !withFir.Contains(1e6) && !withFir.Contains(2e6),
              "B9's rates under a FIR's stated range: no 1 or 2 MS/s (below the no-FIR floor), nothing above 30.72 - " +
              string.Join(", ", withFir.Select(r => (r / 1e6).ToString("0.###", CultureInfo.InvariantCulture))));
        Check(PlutoBackend.OfferedRates(2_083_333, 61_440_000).Length == 16, "...and the board's own no-FIR range offers 2.5 to 61.44, sixteen rates");
        // The library composes the line, the sidecar parses it: they must agree, UHD options and all.
        var line = SidecarLocator.Arguments(UsrpAddress.Iiod(Serial),
            new SidecarLaunch { IiodAddress = "169.254.14.41", WideRateHz = 10e6, UhdDirectory = "C:\\uhd", AllowKnownCodecFault = true, LingerSeconds = 5 }, "tok");
        var back = SidecarOptions.Parse(line.ToArray());
        Check(back.Iiod == "169.254.14.41" && back.Serial == Serial && back.RateHz == 10e6 && back.SpawnToken == "tok" && back.UhdDirectory == null,
              "the library's spawn line for an iiod radio is one this sidecar accepts - without the UHD options the profile carried: " + string.Join(" ", line));
    }

    // ---------------------------------------------------------------- open

    private static void PlutoOpen()
    {
        using (var ulog = new SidecarLog(null, console: false))
        {
            // Constructing the backend opens nothing: what a hello during the open states.
            var before = new PlutoBackend(ulog, Opts(null, hint: "127.0.0.1:1"));
            Check(before.Info.Source == RadioSources.Iiod && before.Info.Serial == Serial && before.Caps.OverflowOracle == null &&
                  before.Caps.LeaseDecimMin == 1 && before.Caps.RxSides!.SequenceEqual(new[] { "A" }),
                  "before the board is open: source iiod, the serial, lease_decim_min 1, side A - and overflow_oracle NOT STATED until the board says");
            before.Dispose();
        }
        using var fake = Board();
        var p = StartPluto(Opts(fake));
        try
        {
            using var s = Connect(p.server, "pluto-open");
            Check(s.Radio.Source == RadioSources.Iiod && s.Radio.Serial == Serial, "hello: source iiod, the serial");
            Check(s.WaitReady(10000) == RadioStates.Ready, "the board opens: " + s.FaultText);
            Check(s.Radio.Product == "Analog Devices PlutoSDR Rev.C (Z7020-AD9361)" && s.Radio.Serial == Serial && s.Radio.KnownCodecFault == false,
                  $"wait_ready names it by its hw_model (B7): {s.Radio.Product}");
            var c = s.Caps;
            var rates = c.WideRatesHz.Select(r => (r / 1e6).ToString("0.###", CultureInfo.InvariantCulture)).ToArray();
            Check(string.Join(",", rates) == "2.5,4,5,8,10,12.5,15.36,16,20,25,30.72,32,40,50,56,61.44",
                  "B9: every native rate at or above the no-FIR floor, inside the board's own range - 30.72 and 61.44 included: " + string.Join(", ", rates));
            Check(c.LeaseDecimMin == 1 && c.RxSides != null && c.RxSides.SequenceEqual(new[] { "A" }) && c.OverflowOracle == true && c.Tx != null,
                  "lease_decim_min 1 (B4), one receive side, a loss oracle - and, since P4, a transmitter (its caps are PlutoTxTests')");
            Check(c.FreqMinHz == 70_000_000 && c.FreqMaxHz == 6_000_000_000 && c.GainMinDb == -10 && c.GainMaxDb == 73 && c.GainStepDb == 1 &&
                  c.Antennas.Length == 12 && c.Antennas[0] == "A_BALANCED" && c.BandwidthMinHz == 200_000 && c.BandwidthMaxHz == 56e6 && c.Agc,
                  "the RX LO, bandwidth and ports are the board's own statements; the gain is the union of its bands (section 9.2 item 7)");

            // B10: SafeIdle is the FIRST write - before any rate, LO, gain or FIR write.
            var writes = fake.AttributeWrites();
            Check(writes.Count > 11 && writes.Take(2).All(w => w.Contains("OUTPUT|voltage") && w.EndsWith("hardwaregain=-89.75", StringComparison.Ordinal)) &&
                  writes.Skip(2).Take(8).All(w => w.Contains("|scale=0")) &&
                  writes[10] == FakeIiod.Key(FakeIiod.Phy, "OUTPUT", "altvoltage1", "powerdown") + "=1",
                  "SafeIdle is the first thing written: both TX attenuations, every DDS scale, the TX LO powered down - " + string.Join(" | ", writes.Take(12)));
            Check(LogText(p.backend).Contains("this board was NOT quiet when the sidecar opened it - the TX LO is powered at 2400.000 MHz"),
                  "...and the log says what it found, in words");
            Check(fake.Get(FakeIiod.Phy, "INPUT", "voltage0", "sampling_frequency") == "2500000" && fake.Get(FakeIiod.Lpc, "INPUT", "voltage0", "sampling_frequency") == "2500000",
                  "opened at the sidecar's rate (2.5 MS/s here), never the board's 30.72; the RX DMA at the full rate, not /8");
            Check(fake.Get(FakeIiod.Phy, "OUTPUT", "altvoltage0", "frequency") == "100000000" && fake.Get(FakeIiod.Phy, "INPUT", "voltage0", "gain_control_mode") == "manual" &&
                  fake.Get(FakeIiod.Phy, "INPUT", "voltage0", "hardwaregain") == "30.000000 dB" && fake.Get(FakeIiod.Phy, "INPUT", "voltage0", "rf_bandwidth") == "2500000",
                  "the RX LO at 100 MHz, manual gain 30 dB, the analog bandwidth following the rate");
            var st = s.GetState();
            Check(st.LoHz == 100_000_000 && st.WideRateHz == 2.5e6 && st.GainDb == 30 && !st.Agc && st.Antenna == "A_BALANCED" && st.BandwidthHz == 2.5e6,
                  "the state is the board's read-back");
            Check(fake.Get(FakeIiod.Phy, "OUTPUT", "altvoltage1", "powerdown") == "1", "the board is quiet");
            Check(WaitFor(() => (s.GetTelemetry().GetObject("radio")?.GetObject("source")?.GetLong("buffers") ?? 0) >= 20, 3000), "buffers stream");
            var tel = s.GetTelemetry().GetObject("radio")!;
            Check(tel.GetLong("overflows") == 0 && tel.GetObject("source")?.GetString("kind") == "iiod" &&
                  tel.GetObject("source")?.GetDouble("adc_clock_hz", 0) == 20e6,
                  "the sticky overflow flag found set (from before) was cleared at open and never counted; the ADC clock is the board's read-back");
            Check(fake.Commands.Count(x => x.StartsWith("OPEN iio:device3 32768 00000003", StringComparison.Ordinal)) == 1,
                  "ONE stream: RX1 (mask 00000003), 32768 samples a buffer");
        }
        finally
        {
            Stop(p);
        }
    }

    // ---------------------------------------------------------------- found by its serial (B6, section 9.9 item 1)

    private static void PlutoFind()
    {
        using (var fake = Board())
        {
            var p = StartPluto(Opts(fake, browse: () => new[] { ("127.0.0.1", fake.Port) }, hint: "127.0.0.1:" + FreePort()));
            try
            {
                Check(p.backend.WaitReady(15000), "a stale stored address (nothing listens) falls back to the browse, which finds the board by its serial");
                string log = LogText(p.backend);
                Check(log.Contains("re-finding it by its serial") && log.Contains($"found board {Serial} at 127.0.0.1:{fake.Port}") && log.Contains("is stale"),
                      "...and the log says the stored address is stale and where the board is now");
            }
            finally { Stop(p); }
        }

        string otherSerial = "1044730231960000000000000000000000";
        using (var wrong = AsBoard(new FakeIiod(FakeIiod.BoardContext().Replace(Serial, otherSerial)).LoadBoardState()))
        using (var right = Board())
        {
            var p = StartPluto(Opts(null, browse: () => new[] { ("127.0.0.1", wrong.Port), ("127.0.0.1", right.Port) }, hint: null));
            try
            {
                Check(p.backend.WaitReady(15000) && p.backend.Info.Serial == Serial, "with no stored address, the browse's answers are opened read-only and the one stating the serial is taken");
                Check(wrong.Writes.Count == 0, "NOTHING was written to the board stating another serial (not even a register selector)");
            }
            finally { Stop(p); }
        }

        using (var wrong = AsBoard(new FakeIiod(FakeIiod.BoardContext().Replace(Serial, otherSerial)).LoadBoardState()))
        {
            var p = StartPluto(Opts(wrong));
            try
            {
                Check(!p.backend.WaitReady(15000), "a different board at the stored address, and a browse that finds nothing: the radio faults");
                string why = p.backend.GetState().FaultText;
                Check(why.Contains($"no board stating hw_serial {Serial} answered") && why.Contains("a different board answers") && why.Contains(otherSerial) &&
                      why.Contains("got no answer") && why.Contains("Windows Firewall"),
                      "...naming both serials, and saying the browse got no answer - with the firewall as one reason: " + why);
                Check(wrong.Writes.Count == 0, "...having written NOTHING to the other board");
            }
            finally { Stop(p); }
        }

        string noSerial = Regex.Replace(FakeIiod.BoardContext(), "<context-attribute name=\"hw_serial\"[^>]*/>", "");
        using (var anon = AsBoard(new FakeIiod(noSerial).LoadBoardState()))
        {
            var p = StartPluto(Opts(anon));
            try
            {
                Check(!p.backend.WaitReady(15000) && p.backend.GetState().FaultText.Contains("states no hw_serial") && anon.Writes.Count == 0,
                      "a board that states no hw_serial is never adopted, and never written: " + p.backend.GetState().FaultText);
            }
            finally { Stop(p); }
        }
    }

    // ---------------------------------------------------------------- the FIR another program loaded (section 9.8)

    private static void PlutoFir()
    {
        using (var fake = Board().LoadForeignFir())
        {
            var p = StartPluto(Opts(fake));
            try
            {
                Check(p.backend.WaitReady(15000), "a board with another program's FIR opens");
                Check(fake.Get(FakeIiod.Phy, "INPUT", "voltage0", "filter_fir_en") == "0" && fake.Get(FakeIiod.Phy, "OUTPUT", "voltage0", "filter_fir_en") == "0",
                      "...with the FIR switched off on both chains");
                Check(p.backend.Caps.WideRatesHz.Max() == 61.44e6, "...so the rates are the no-FIR range again, up to 61.44 MS/s (the FIR capped them at 30.72)");
                string log = LogText(p.backend);
                Check(log.Contains("FIR: a FIR was loaded (RX on, TX on)") && log.Contains("520833...30720000"),
                      "...and said in words, with the range it had imposed");
                var writes = fake.AttributeWrites();
                int safeIdle = writes.FindIndex(w => w.EndsWith("altvoltage1|powerdown=1", StringComparison.Ordinal));
                int fir = writes.FindIndex(w => w.Contains("voltage_filter_fir_en=0"));
                Check(safeIdle >= 0 && fir > safeIdle, "SafeIdle still came first; the FIR switch after it");
            }
            finally { Stop(p); }
        }
        using (var fake = Board().LoadForeignFir())
        {
            // A FIR whose rate the board can only reach WITH it: the rate moves first, inside the FIR's range.
            fake.Set(FakeIiod.Phy, "INPUT", "voltage0", "sampling_frequency", "1000000");
            var p = StartPluto(Opts(fake));
            try
            {
                Check(p.backend.WaitReady(15000) && LogText(p.backend).Contains("which needs its FIR: moving to 2500000 S/s before switching the FIR off"),
                      "a rate below the no-FIR floor is moved before the FIR is switched off");
            }
            finally { Stop(p); }
        }
        using (var fake = Board().LoadForeignFir())
        {
            fake.IgnoreWritesTo.Add(FakeIiod.Key(FakeIiod.Phy, "INPUT", "out", "voltage_filter_fir_en"));
            var p = StartPluto(Opts(fake));
            try
            {
                Check(!p.backend.WaitReady(15000) && p.backend.GetState().FaultText.Contains("still in force"),
                      "a FIR that will not switch off faults the open in words: " + p.backend.GetState().FaultText);
            }
            finally { Stop(p); }
        }
    }

    // ---------------------------------------------------------------- levels and the wide lease

    private static void PlutoLevels()
    {
        using var fake = Board();
        fake.ToneRfHz = 100_100_000;            // +100 kHz from the LO
        fake.ToneAmplitude = 2048 * 0.1;         // -20 dBFS of the 12-bit full scale
        var p = StartPluto(Opts(fake));
        try
        {
            using var s = Connect(p.server, "pluto-levels");
            s.WaitReady(10000);
            s.ClaimLo();
            using var pano = s.OpenPanorama(new PanoramaRequest { Bins = 4096, Fps = 10, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
            var rows = new List<(RowHeader h, short[] b)>();
            pano.Connection.RowReceived += (c, h, bins) => { lock (rows) rows.Add((h, (short[])bins.Clone())); };
            pano.Connection.StartReader();
            Check(WaitFor(() => { lock (rows) return rows.Count >= 4; }, 6000), "rows arrive");
            (RowHeader h, short[] b) last;
            lock (rows) last = rows[^1];
            double level = LevelAt(last.h, last.b, 100_100_000, 2.5e6);
            Check(Math.Abs(level + 20) < 0.5,
                  $"a -20 dBFS tone in the board's 12 bits reads {F(level)} dBFS: the x16 to sc16 full scale is the backend's (section 9.2 item 8; without it, -44)");
        }
        finally { Stop(p); }
    }

    // ---------------------------------------------------------------- the RX LO another program powered down (section 9.29)

    private static void PlutoRxLoFoundDown()
    {
        // SoapyPlutoSDR writes powerdown 1 to the RX LO when its receive stream closes, and the bit is sticky: the sidecar that
        // opened next streamed a DC spike at every frequency. The fake now hears its tone only while the RX LO is powered.
        using (var fake = Board())
        {
            fake.Set(FakeIiod.Phy, "OUTPUT", "altvoltage0", "powerdown", "1");
            fake.ToneRfHz = 100_100_000;
            fake.ToneAmplitude = 2048 * 0.1;
            var p = StartPluto(Opts(fake));
            try
            {
                Check(p.backend.WaitReady(15000), "a board whose RX LO another program powered down opens");
                Check(fake.Get(FakeIiod.Phy, "OUTPUT", "altvoltage0", "powerdown") == "0", "...with its RX LO powered up");
                Check(LogText(p.backend).Contains("RX LO: found powered DOWN - another program left it so"), "...and said in words");
                var writes = fake.AttributeWrites();
                int safeIdle = writes.FindIndex(w => w.EndsWith("altvoltage1|powerdown=1", StringComparison.Ordinal));
                int up = writes.FindIndex(w => w.EndsWith("altvoltage0|powerdown=0", StringComparison.Ordinal));
                Check(safeIdle >= 0 && up > safeIdle, "SafeIdle still came first; the RX LO after it");

                using var s = Connect(p.server, "pluto-rxlo");
                s.WaitReady(10000);
                s.ClaimLo();
                using var pano = s.OpenPanorama(new PanoramaRequest { Bins = 4096, Fps = 10, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
                var rows = new List<(RowHeader h, short[] b)>();
                pano.Connection.RowReceived += (c, h, bins) => { lock (rows) rows.Add((h, (short[])bins.Clone())); };
                pano.Connection.StartReader();
                Check(WaitFor(() => { lock (rows) return rows.Count >= 4; }, 6000), "rows arrive");
                (RowHeader h, short[] b) last;
                lock (rows) last = rows[^1];
                double level = LevelAt(last.h, last.b, 100_100_000, 2.5e6);
                Check(Math.Abs(level + 20) < 0.5, $"...and the -20 dBFS tone is HEARD: it reads {F(level)} dBFS (with the RX LO left down, nothing)");
            }
            finally { Stop(p); }
        }
        using (var fake = Board())
        {
            var p = StartPluto(Opts(fake));
            try
            {
                Check(p.backend.WaitReady(15000), "a board whose RX LO is powered opens");
                Check(!fake.AttributeWrites().Any(w => w.Contains("altvoltage0|powerdown=")) && !LogText(p.backend).Contains("RX LO: found"),
                      "...and its RX LO is neither written nor mentioned: only a board found wrong is put right");
            }
            finally { Stop(p); }
        }
    }

    private static void PlutoWideLease()
    {
        using var fake = Board();   // the ramp: I = n mod 2048, Q = -I
        var p = StartPluto(Opts(fake));
        try
        {
            using var s = Connect(p.server, "pluto-wide");
            s.WaitReady(10000);
            s.ClaimLo();
            using var lease = s.OpenLease(new LeaseRequest { CenterHz = 100_000_000, Decimation = 1 });
            Check(lease.Grant.Decimation == 1 && lease.Grant.RateHz == 2.5e6, "a decimation-1 lease on the LO: the whole 2.5 MS/s stream (B4)");
            var blocks = new List<(IqHeader h, byte[] d)>();
            lease.Connection.IqBlockReceived += (c, h, d) => { lock (blocks) blocks.Add((h, d)); };
            lease.Connection.StartReader();
            Check(WaitFor(() => { lock (blocks) return blocks.Count >= 20; }, 6000), "blocks arrive");
            List<(IqHeader h, byte[] d)> got;
            lock (blocks) got = blocks.ToList();
            bool exact = got.All(b =>
            {
                short i0 = BitConverter.ToInt16(b.d, 0);
                int r0 = (i0 >> 4) & 0x7FF;
                for (int k = 0; k < b.h.Count; k++)
                {
                    int r = (r0 + k) & 0x7FF;
                    if (BitConverter.ToInt16(b.d, 4 * k) != (short)(r << 4) || BitConverter.ToInt16(b.d, 4 * k + 2) != (short)((-r) << 4)) return false;
                }
                return true;
            });
            Check(exact, $"its samples ARE the board's, x16, bit for bit - no NCO, no filter, no float round trip ({got.Count} blocks of the ramp checked)");
            bool contiguous = true;
            for (int k = 1; k < got.Count; k++)
                if (got[k].h.FirstSample != got[k - 1].h.FirstSample + (ulong)got[k - 1].h.Count + ((got[k].h.Flags & IqFlags.Discontinuity) != 0 ? got[k].h.Dropped : 0))
                    contiguous = false;
            Check(contiguous, "every block starts where the previous ended (or says what it dropped)");

            s.SetLo(145_000_000);
            Check(WaitFor(() => { lock (blocks) return blocks.Any(b => b.h.CenterHz == 145_000_000 && (b.h.Flags & IqFlags.Retuned) != 0); }, 5000),
                  "after set_lo the wide lease FOLLOWS: its next block carries 145 MHz and the retuned flag - it was not ended");
            Check(fake.Get(FakeIiod.Phy, "OUTPUT", "altvoltage0", "frequency") == "145000000", "...the board's RX LO moved");
            var tel = s.GetTelemetry().GetArray("streams")!.OfType<JsonObject>().First(o => o.GetString("kind") == "lease");
            Check(tel.GetLong("center_hz") == 145_000_000, "...and the server's view of its centre with it");
            Check(s.GetState().Readiness == RadioStates.Ready, "the move was this sidecar's own: the foreign-writer watch stayed quiet");
        }
        finally { Stop(p); }
    }

    // ---------------------------------------------------------------- the loss oracle (section 9.2 item 5)

    private static void PlutoLossOracle()
    {
        using (var fake = Board())
        {
            var p = StartPluto(Opts(fake));
            try
            {
                using var s = Connect(p.server, "pluto-loss");
                s.WaitReady(10000);
                s.ClaimLo();
                using var lease = s.OpenLease(new LeaseRequest { CenterHz = 100_000_000, Decimation = 25 });
                long leaseOverflows = 0;
                lease.Connection.IqBlockReceived += (c, h, d) => Interlocked.Add(ref leaseOverflows, h.Overflows);
                lease.Connection.StartReader();
                Thread.Sleep(300);
                fake.OverflowEveryBuffers = 10;
                Check(WaitFor(() => fake.OverflowsRaised >= 4, 6000), "the board raises its overflow flag");
                fake.OverflowEveryBuffers = 0;
                Thread.Sleep(400);
                long counted = s.GetTelemetry().GetObject("radio")!.GetLong("overflows");
                Check(counted == fake.OverflowsRaised, $"every flag the board raised is counted once ({counted} of {fake.OverflowsRaised}), read after every buffer");
                Check(Interlocked.Read(ref leaseOverflows) > 0, $"...and reaches a lease's block headers ({Interlocked.Read(ref leaseOverflows)})");
                lock (fake.Lock)
                    Check((fake.Registers[FakeIiod.RegKey(FakeIiod.Lpc, FakeIiod.StatusRegister)] & 0x4) == 0, "...each cleared write-1-to-clear");
                Check(s.GetState().Readiness == RadioStates.Ready, "a loss-flagged buffer runs the quick watch - and our own board passes it");
            }
            finally { Stop(p); }
        }
        string noDebugfs = FakeIiod.BoardContext().Replace("<debug-attribute name=\"direct_reg_access\" />", "");
        using (var fake = AsBoard(new FakeIiod(noDebugfs).LoadBoardState()))
        {
            var p = StartPluto(Opts(fake));
            try
            {
                using var s = Connect(p.server, "pluto-nodebugfs");
                Check(s.WaitReady(10000) == RadioStates.Ready && s.Caps.OverflowOracle == false,
                      "a board with no debugfs opens and states overflow_oracle FALSE - a counter of 0 from it would be a lie");
                Check(LogText(p.backend).Contains("loss oracle: NONE"), "...and its log says lost samples cannot be seen");
            }
            finally { Stop(p); }
        }
    }

    // ---------------------------------------------------------------- a third program (B11)

    private static void PlutoForeignWriter()
    {
        void Case(string what, Action<FakeIiod> foreign, string mustSay)
        {
            using var fake = Board();
            var p = StartPluto(Opts(fake));
            bool stopped = false;
            try
            {
                using var s = Connect(p.server, "pluto-foreign");
                s.WaitReady(10000);
                string? text = null;
                var faulted = new ManualResetEventSlim();
                using var ev = s.OpenEvents();
                ev.Connection.EventReceived += (c, n, body) =>
                {
                    if (n != EventNames.RadioFault) return;
                    text = body.GetString("text", "");
                    faulted.Set();
                };
                ev.Connection.StartReader();
                Thread.Sleep(500);
                int powerdownsBefore = fake.AttributeWrites().Count(w => w.EndsWith("powerdown=1", StringComparison.Ordinal));
                foreign(fake);
                var sw = Stopwatch.StartNew();
                Check(faulted.Wait(4000) && text != null && text.Contains(mustSay),
                      $"{what}: the radio faults in {sw.ElapsedMilliseconds} ms, in words: {text}");
                Stop(p);
                stopped = true;
                Check(fake.AttributeWrites().Count(w => w.EndsWith("powerdown=1", StringComparison.Ordinal)) == powerdownsBefore,
                      "...and NO SafeIdle is written over the other program's settings, not even at close (B10's own rule for a board another program holds)");
            }
            finally
            {
                if (!stopped) Stop(p);
            }
        }

        Case("another program moves the RX LO", f => f.Set(FakeIiod.Phy, "OUTPUT", "altvoltage0", "frequency", "433920000"),
             PlutoBackend.ForeignWriterPhrase + " under the sidecar: the RX LO reads 433.920 MHz, and this sidecar last set 100.000 MHz (ad9361-phy OUTPUT altvoltage0 frequency)");
        Case("another program powers the RX LO down (SoapyPlutoSDR closing its stream, section 9.29)",
             f => f.Set(FakeIiod.Phy, "OUTPUT", "altvoltage0", "powerdown", "1"),
             "the RX LO reads powered DOWN, and this sidecar receives on it (ad9361-phy OUTPUT altvoltage0 powerdown = 1)");
        Case("another program changes the rate", f => f.Set(FakeIiod.Phy, "INPUT", "voltage0", "sampling_frequency", "20000000"),
             "the sample rate reads 20000000 S/s, and this sidecar last set 2500000 S/s");
        Case("another program powers the TX LO", f => f.Set(FakeIiod.Phy, "OUTPUT", "altvoltage1", "powerdown", "0"),
             "while this sidecar holds it idle, the TX LO is powered at 2400.000 MHz");
        Case("another program turns a DDS tone on", f => f.Set(FakeIiod.Dds, "OUTPUT", "altvoltage4", "scale", "0.250000"),
             "the DDS tone TX2_I_F1 is at scale 0.25");
        Case("another program loads a FIR", f => { lock (f.Lock) f.LoadForeignFir(); }, "a FIR was loaded (RX on, TX on)");
        Case("another program reads the board's samples", f => f.MaskOverride = "0000000f",
             PlutoBackend.ForeignReaderPhrase + "'s samples: the board's channel mask reads 0000000f, and this sidecar's is 00000003");
    }

    // ---------------------------------------------------------------- the radio's settings

    private static void PlutoSetters()
    {
        using var fake = Board();
        var p = StartPluto(Opts(fake));
        try
        {
            using var s = Connect(p.server, "pluto-set");
            s.WaitReady(10000);
            s.ClaimLo();
            using var pano = s.OpenPanorama(new PanoramaRequest { Bins = 1024, Fps = 10, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
            var rows = new List<RowHeader>();
            pano.Connection.RowReceived += (c, h, b) => { lock (rows) rows.Add(h); };
            pano.Connection.StartReader();

            var st = s.SetRate(5e6);
            Check(st.WideRateHz == 5e6 && fake.Get(FakeIiod.Phy, "INPUT", "voltage0", "sampling_frequency") == "5000000" &&
                  fake.Get(FakeIiod.Lpc, "INPUT", "voltage0", "sampling_frequency") == "5000000",
                  "set_rate 5 MS/s: ONE AD936x write, the RX DMA put at the full rate again, the state its read-back");
            Check(fake.Commands.Count(x => x.StartsWith("OPEN iio:device3", StringComparison.Ordinal)) == 2 &&
                  fake.Commands.Count(x => x == "CLOSE iio:device3") >= 1, "...the stream CLOSEd and made again around it");
            Check(WaitFor(() => { lock (rows) return rows.Count > 0 && rows[^1].SpanHz == 5_000_000; }, 5000), "...and the panorama re-derives a 5 MHz span from the new rate");
            Check(s.GetTelemetry().GetObject("radio")?.GetObject("source")?.GetDouble("adc_clock_hz", 0) == 40e6, "the ADC clock is read again (the chain is the board's)");

            Check(Throws(() => s.SetGain(80)), "80 dB is outside the stated union (-10...73): the server refuses it before the board is asked");
            st = s.SetGain(73);
            Check(st.GainDb == 71 && !st.Agc && fake.Get(FakeIiod.Phy, "INPUT", "voltage0", "hardwaregain") == "71.000000 dB",
                  $"set_gain 73 dB - inside the union, above THIS band's 71 - replies with what the band could apply: {st.GainDb} dB (section 9.2 item 7)");
            st = s.SetAgc(true);
            Check(st.Agc && fake.Get(FakeIiod.Phy, "INPUT", "voltage0", "gain_control_mode") == "slow_attack", "AGC on is slow_attack");
            fake.Set(FakeIiod.Phy, "INPUT", "voltage0", "hardwaregain", "52.000000 dB");   // the AGC moved it, as it may
            Thread.Sleep(700);
            st = s.SetAgc(false);
            Check(!st.Agc && st.GainDb == 52 && fake.Get(FakeIiod.Phy, "INPUT", "voltage0", "gain_control_mode") == "manual",
                  "AGC off is manual, and the gain in force is what the AGC left, read back (52 dB)");
            st = s.SetAntenna("B_BALANCED");
            Check(st.Antenna == "B_BALANCED" && fake.Get(FakeIiod.Phy, "INPUT", "voltage0", "rf_port_select") == "B_BALANCED", "an RX port the board lists");
            st = s.SetBandwidth(1e6);
            Check(st.BandwidthHz == 1e6 && fake.Get(FakeIiod.Phy, "INPUT", "voltage0", "rf_bandwidth") == "1000000", "the analog bandwidth, read back");
            st = s.SetLo(433_920_000);
            Check(st.LoHz == 433_920_000 && fake.Get(FakeIiod.Phy, "OUTPUT", "altvoltage0", "frequency") == "433920000", "set_lo, read back");

            var settings = s.GetSettings().OfType<JsonObject>().ToDictionary(o => o.GetString("key", ""), o => o);
            Check(settings.ContainsKey("dc_correction") && settings.ContainsKey("iq_balance") && settings.ContainsKey("xo_correction") &&
                  settings["iiod"].GetString("value", "") == "127.0.0.1:" + fake.Port && settings["firmware"].GetString("value", "") == "v0.38",
                  "get_settings: the AD936x's tracking and XO, and where and what the board is (read-only)");
            Check(s.SetSetting("dc_correction", "false") == "false" &&
                  fake.Get(FakeIiod.Phy, "INPUT", "voltage0", "rf_dc_offset_tracking_en") == "0" && fake.Get(FakeIiod.Phy, "INPUT", "voltage0", "bb_dc_offset_tracking_en") == "0",
                  "dc_correction off writes both loops");
            Check(s.SetSetting("xo_correction", "40000010") == "40000010", "the XO correction, read back");
            var ro = Throws(() => s.SetSetting("iiod", "10.0.0.1"));
            Check(ro, "the board's address is the board's to state, not a setting");
            Check(Throws(() => s.SetRxSides(new[] { "A", "B" })), "a second receive side is refused (section 9.2 item 6)");
            Thread.Sleep(800);
            Check(s.GetState().Readiness == RadioStates.Ready,
                  "every change above was this sidecar's own: the foreign-writer watch, running all the while, stayed quiet");
        }
        finally { Stop(p); }
    }

    // ---------------------------------------------------------------- close, and a board that goes away

    private static void PlutoCloseAndLoss()
    {
        using (var fake = Board())
        {
            var p = StartPluto(Opts(fake));
            p.backend.WaitReady(10000);
            Thread.Sleep(400);
            int before = fake.AttributeWrites().Count;
            p.server.Dispose();
            p.backend.Dispose();
            var after = fake.AttributeWrites().Skip(before).ToList();
            Check(after.Count == 11 && after[^1].EndsWith("altvoltage1|powerdown=1", StringComparison.Ordinal),
                  $"close writes SafeIdle again (B10) - {after.Count} writes, the TX LO powerdown last");
            int close = fake.Commands.LastIndexOf("CLOSE iio:device3");
            Check(close >= 0, "...having CLOSEd the stream on the board first");
            Check(LogText(p.backend).Contains("SafeIdle at close: the board reads back quiet"), "...and the log says the board reads back quiet");
            p.log.Dispose();
        }
        using (var fake = Board())
        {
            var p = StartPluto(Opts(fake));
            try
            {
                p.backend.WaitReady(10000);
                fake.Dispose();     // the link goes, and the board answers nothing more
                Check(WaitFor(() => p.backend.GetState().Readiness == RadioStates.Fault, 8000) &&
                      p.backend.GetState().FaultText.StartsWith("the board stopped streaming", StringComparison.Ordinal),
                      "a board that goes away faults the radio: " + p.backend.GetState().FaultText);
                Check(LogText(p.backend).Contains("SafeIdle at the fault could not be written") && LogText(p.backend).Contains("start-up sweep"),
                      "...SafeIdle is attempted on the fault path and its failure said, with what clears it");
            }
            finally { Stop(p); }
        }
    }

    // ---------------------------------------------------------------- the real executable

    private static void PlutoSpawned()
    {
        using var fake = Board();
        var addr = UsrpAddress.Iiod(Serial.ToUpperInvariant());
        var opts = new UsrpSessionOptions
        {
            ClientName = "pluto-spawner",
            SidecarExe = SidecarExe(),
            SpawnTimeoutMs = 20000,
            Launch = new SidecarLaunch
            {
                IiodAddress = "127.0.0.1:" + fake.Port.ToString(CultureInfo.InvariantCulture),
                WideRateHz = 2.5e6,
                LingerSeconds = 1,
                UhdDirectory = Path.Combine(_temp, "no-uhd-here"),   // a profile's UHD option: never passed to an iiod radio
                ExtraArgs = new[] { "--state-dir", Rendezvous.Root },
            },
        };
        int pid;
        using (var s = UsrpSession.Open(addr, opts))
        {
            pid = s.SidecarPid;
            Check(s.Spawned && s.WaitReady(15000) == RadioStates.Ready && s.Radio.Source == RadioSources.Iiod && s.Radio.Serial == Serial,
                  $"the real executable, spawned for usrp:iiod:<serial> with the stored address: ready on the fake board (pid {pid}) {s.FaultText}");
            Check(File.Exists(Rendezvous.PathFor("iiod-" + Serial)), "its rendezvous file is iiod-<serial>");
            using (var p = Process.GetProcessById(pid))
                Check(!UhdDllIn(p), "the sidecar serving a Pluto has NO uhd.dll loaded (B2)");
            Check(fake.Get(FakeIiod.Phy, "OUTPUT", "altvoltage1", "powerdown") == "1" && fake.Get(FakeIiod.Phy, "INPUT", "voltage0", "sampling_frequency") == "2500000",
                  "it quieted the board and opened it at the rate the launch asked for");
            using var s2 = UsrpSession.Open(UsrpAddress.Iiod(Serial), opts);
            Check(!s2.Spawned && s2.SidecarPid == pid, "a second open of the same board in another spelling JOINS it (one spawn mutex, one sidecar)");
        }
        Check(WaitFor(() => !Rendezvous.IsProcessAlive(pid), 8000), "after the last client leaves it exits by itself");
        Check(fake.AttributeWrites().Count(w => w.EndsWith("altvoltage1|powerdown=1", StringComparison.Ordinal)) == 2,
              "...having written SafeIdle twice: at open, and at close");
    }

    /// <summary>What a running gate cannot see, pinned in the source.</summary>
    private static void PlutoSourceRules()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ShackLinkUsrp", "Radio", "PlutoBackend.cs"));
        Check(!Regex.IsMatch(src, @"\bUhd(Loader|Usrp|Native|RxStreamer|TxStreamer)\b"), "PlutoBackend reaches nothing of UHD's (B2: an iiod radio loads none)");
        string? open = MemberBody(src, "protected override void OpenRadio()");
        int safe = open?.IndexOf(".SafeIdle()", StringComparison.Ordinal) ?? -1;
        int firstWrite = new[] { ".SetSampleRate(", ".SetRxLo(", ".SetRxGain(", ".DisableFir(", ".SetRxBandwidth(", ".ReadAndClearRxOverflow(" }
            .Select(w => open?.IndexOf(w, StringComparison.Ordinal) ?? -1).Where(i => i >= 0).DefaultIfEmpty(int.MaxValue).Min();
        Check(open != null && safe > 0 && safe < firstWrite, "in OpenRadio, SafeIdle comes before every other write (B10)");
        string program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ShackLinkUsrp", "Program.cs"));
        Check(program.Contains("string key = opt.Address().RendezvousKey!;") && !program.Contains("\"sim-\" + opt.SimName"),
              "the sidecar takes its rendezvous key from the address grammar, never spelled a second time");
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
