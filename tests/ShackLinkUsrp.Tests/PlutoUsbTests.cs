// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// The Pluto backend over its board's OWN USB pipes, and every route's ceilings (ShackLink docs/plutosdr_support.md section 9.26, U3):
// against the shared fake board's USB face (ShackLink's IiodWireTest\FakeIiod.cs - three pipes, one handle, OPEN_PIPE on a live pipe
// kills it), through the real protocol server. No board, and nothing radiates.

using System.Globalization;
using ShackLinkIio.Protocol;
using ShackLinkIio.Usb;
using ShackLinkUsrp.Client;
using ShackLinkUsrp.Host;
using ShackLinkUsrp.Radio;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Tests;

internal static partial class Program
{
    private static int _usbFakes;

    /// <summary>A backend over the fake's USB face: no address, no browse - the route is the fake's pipes, under a name of its own.</summary>
    private static PlutoBackendOptions UsbOpts(FakeIiod fake, double rate = 2.5e6)
    {
        string name = "usb:sidecar-fake-" + Interlocked.Increment(ref _usbFakes).ToString(CultureInfo.InvariantCulture);
        return Opts(fake, rate: rate) with { Usb = true, AddressHint = null, OpenUsbRoute = _ => IiodUsbRoute.Over(name, fake.OpenUsb) };
    }

    private static JsonObject? SourceTelemetry(UsrpSession s) => s.GetTelemetry().GetObject("radio")?.GetObject("source");

    // ---------------------------------------------------------------- the command line (D14)

    private static void PlutoUsbCommandLine()
    {
        var o = SidecarOptions.Parse(new[] { "--serial", Serial, "--iiod", "usb:" + Serial.ToUpperInvariant() });
        Check(o.IiodUsb && o.Address().Kind == UsrpAddressKind.Iiod && o.Address().Serial == Serial,
              "--iiod usb:<hw_serial> is the board over its own USB pipes, the serial the address's (any case)");
        string? why = null;
        try { SidecarOptions.Parse(new[] { "--serial", Serial, "--iiod", "usb:00000000000000000000000000000000" }); }
        catch (ArgumentException ex) { why = ex.Message; }
        Check(why != null && why.Contains("names a board other than", StringComparison.Ordinal),
              $"a usb: form naming ANOTHER board than --serial is refused in words - the serial is never spelled twice [{why}]");
        Check(!SidecarOptions.Parse(new[] { "--serial", Serial, "--iiod", "usbhost:30431" }).IiodUsb,
              "a host whose name starts with usb is still host:port (the form is usb: exactly)");
        var line = SidecarLocator.Arguments(UsrpAddress.Iiod(Serial), new SidecarLaunch { IiodUsb = true, IiodAddress = "169.254.13.164", WideRateHz = 5e6 }, "tok");
        var back = SidecarOptions.Parse(line.ToArray());
        Check(back.IiodUsb && back.Iiod == "usb:" + Serial && back.RateHz == 5e6 && back.Serial == Serial,
              "the library's spawn line over the pipes is one this sidecar accepts: " + string.Join(" ", line));
    }

    // ---------------------------------------------------------------- the open over USB, its ceilings, the substitution (D14-D16, D20)

    private static void PlutoUsbOpen()
    {
        using var fake = Board();
        var opt = UsbOpts(fake, rate: 10e6);
        var p = StartPluto(opt);
        try
        {
            using var s = Connect(p.server, "pluto-usb");
            Check(s.WaitReady(15000) == RadioStates.Ready, "the board opens over its own USB pipes: " + s.FaultText);
            string log = LogText(p.backend);
            Check(fake.UsbHandleOpen && !log.Contains("127.0.0.1", StringComparison.Ordinal) && log.Contains("over its own USB pipes", StringComparison.Ordinal) &&
                  !log.Contains("_iio._tcp browse", StringComparison.Ordinal),
                  $"...through the fake's USB face: no address, no browse, nothing on its network face [handle {fake.UsbHandleOpen}]");
            var c = s.Caps;
            Check(c.MaxWideRateHz == 7e6 && c.Link == "its own USB pipes" && c.Tx?.MaxWideRateHz == 4e6,
                  $"caps: receive 7 MS/s and transmit 4 MS/s over the pipes (Q13), and the link said [{c.MaxWideRateHz}, {c.Link}, {c.Tx?.MaxWideRateHz}]");
            Check(Math.Abs(s.GetState().WideRateHz - 5e6) < 1 && log.Contains("opening at 5000000 S/s instead", StringComparison.Ordinal),
                  "D16: asked to open at 10 MS/s (a slot stored from its Ethernet days), it opens at 5 - the highest offered within 7 - and SAYS so");
            Check(c.WideRatesHz.Contains(8e6) && c.WideRatesHz.Contains(10e6) && c.WideRatesHz.Contains(61.44e6),
                  "B9: every rate is still OFFERED - the ceiling refuses, it never filters");
            var tel = SourceTelemetry(s);
            Check(tel != null && tel.GetString("route") == "usb-pipes" && tel.GetString("address").StartsWith("usb:sidecar-fake-", StringComparison.Ordinal) &&
                  tel.GetDouble("rx_max_wide_rate_hz", 0) == 7e6 && tel.GetDouble("tx_max_wide_rate_hz", 0) == 4e6,
                  $"D20: telemetry says the route, the route's name (never usb:...:0) and both ceilings [{tel?.GetString("address")}]");
            var iiod = s.GetSettings().OfType<JsonObject>().FirstOrDefault(x => x.GetString("key") == "iiod");
            Check(iiod != null && iiod.GetString("value").StartsWith("usb:sidecar-fake-", StringComparison.Ordinal) && !iiod.GetString("value").EndsWith(":0", StringComparison.Ordinal) &&
                  iiod.GetString("text").Contains("its own USB pipes", StringComparison.Ordinal),
                  $"...and so does the iiod setting [{iiod?.GetString("value")}]");

            s.ClaimLo();
            var r8 = RefusedTx(() => s.SetRate(8e6));
            Check(r8?.Code == ErrorCodes.OutOfRange && r8.Error.Text.Contains("its own USB pipes", StringComparison.Ordinal) && Math.Abs(s.GetState().WideRateHz - 5e6) < 1,
                  "set_rate to 8 MS/s - offered, above the pipes' 7 - refused OUT_OF_RANGE in words naming the link, the rate unchanged: " + r8?.Error.Text);
            RadioRefusal? own = null;
            try { p.backend.SetRate(10e6); }
            catch (RadioRefusal ex) { own = ex; }
            Check(own?.Code == ErrorCodes.OutOfRange && Math.Abs(s.GetState().WideRateHz - 5e6) < 1,
                  "...and the backend's OWN net refuses it too, asked directly, before the stream is touched");
            Check(Math.Abs(s.SetRate(4e6).WideRateHz - 4e6) < 1, "4 MS/s, within it, is admitted");
            long b0 = SourceTelemetry(s)?.GetLong("buffers") ?? 0;
            Check(WaitFor(() => (SourceTelemetry(s)?.GetLong("buffers") ?? 0) > b0 + 5, 5000), "the receive stream runs over its pipe");
            List<string> usbLog;
            lock (fake.Lock) usbLog = fake.UsbControlLog.ToList();
            Check(usbLog.Count(x => x == "RESET") == 1 && usbLog.Contains("OPEN 0") && usbLog.Contains("OPEN 1") && fake.UsbLiveSessionsKilled == 0,
                  $"control on pipe 0, receive on pipe 1, ONE handle, nothing killed [{string.Join(", ", usbLog)}]");
        }
        finally { Stop(p); }
        Check(!fake.UsbHandleOpen && BoardQuiet(fake), "at close: SafeIdle, then the interface let go - another program may take it");
    }

    // ---------------------------------------------------------------- the network routes' ceilings (D11, D15)

    private static void PlutoRouteLimits()
    {
        using (var fake = Board())
        {
            string? asked = null;
            var p = StartPluto(Opts(fake, rate: 12.5e6) with { ClassifyAddress = h => { asked = h; return PlutoRoute.Ethernet; } });
            try
            {
                using var s = Connect(p.server, "pluto-eth");
                Check(s.WaitReady(15000) == RadioStates.Ready, "over the network: " + s.FaultText);
                var c = s.Caps;
                Check(asked == "127.0.0.1" && c.MaxWideRateHz == 10e6 && c.Link == "its Ethernet port" && c.Tx?.MaxWideRateHz == 2.5e6,
                      $"Ethernet: the classifier is asked the HOST it reached; receive 10 MS/s, transmit 2.5 (Q7, Q13) [{asked}, {c.MaxWideRateHz}, {c.Tx?.MaxWideRateHz}]");
                Check(Math.Abs(s.GetState().WideRateHz - 10e6) < 1, "asked to open at 12.5 MS/s, above Ethernet's 10: it opens at 10, said");
                Check(SourceTelemetry(s)?.GetString("route") == "ethernet" && SourceTelemetry(s)?.GetString("address") == "127.0.0.1:" + fake.Port.ToString(CultureInfo.InvariantCulture),
                      "telemetry: route ethernet, the address as before");
                s.ClaimLo();
                Check(RefusedTx(() => s.SetRate(12.5e6))?.Code == ErrorCodes.OutOfRange, "set_rate 12.5 MS/s over Ethernet: refused (Q13 - it was offered and lossy, it is refused now)");
            }
            finally { Stop(p); }
        }
        using (var fake = Board())
        {
            var p = StartPluto(Opts(fake, rate: 10e6) with { ClassifyAddress = _ => PlutoRoute.UsbNetwork });
            try
            {
                using var s = Connect(p.server, "pluto-rndis");
                Check(s.WaitReady(15000) == RadioStates.Ready, "over a board's USB network: " + s.FaultText);
                var c = s.Caps;
                Check(c.MaxWideRateHz == 5e6 && c.Link == "its USB network (RNDIS)" && c.Tx?.MaxWideRateHz == 2.5e6 && Math.Abs(s.GetState().WideRateHz - 5e6) < 1 &&
                      SourceTelemetry(s)?.GetString("route") == "usb-network",
                      $"RNDIS (Q14): receive 5 MS/s, transmit 2.5, opened at 5 - the slowest route says so [{c.MaxWideRateHz}, {c.Link}]");
            }
            finally { Stop(p); }
        }
        using (var fake = Board())
        {
            var p = StartPluto(Opts(fake, rate: 2.5e6) with { ClassifyAddress = _ => throw new InvalidOperationException("no adapters") });
            try
            {
                using var s = Connect(p.server, "pluto-unclassified");
                Check(s.WaitReady(15000) == RadioStates.Ready && s.Caps.MaxWideRateHz == 10e6 && s.Caps.Link == "its Ethernet port",
                      "a classifier that fails reads Ethernet - today's behaviour - and the board still opens");
            }
            finally { Stop(p); }
        }
    }

    // ---------------------------------------------------------------- D17: SafeIdle on pipe 0 while all three pipes are held

    private static void PlutoUsbSafeIdleOnItsOwnPipe()
    {
        using var fake = Board();
        var p = StartPlutoTx(UsbOpts(fake, rate: 2.5e6));
        try
        {
            using var s = Connect(p.server, "pluto-usb-tx");
            Check(s.WaitReady(15000) == RadioStates.Ready, "over USB: " + s.FaultText);
            s.ClaimTx();
            s.SetTx(new TxSettings { CenterHz = TxCentre, GainDb = 40 });
            var events = TxEvents(s, out var ev);
            var t = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone, MaxMs = 30_000, OffsetHz = 50_000, LevelDbfs = -10 });
            Check(WaitFor(() => !BoardQuiet(fake) && fake.UsbLiveSessions == 3, 5000),
                  $"keyed over USB: the LO powered, and the board's THREE pipes live - control, receive, transmit ({fake.UsbLiveSessions})");
            int mark;
            lock (fake.Lock) mark = fake.UsbControlLog.Count;
            lock (fake.Lock)
            {
                fake.NextStatusOverrideKeyword = "READ";
                fake.NextStatusOverride = "abc";
            }
            Check(WaitFor(() => BoardQuiet(fake), 8000), "the control connection breaks under the keyed radio, and the board is QUIETED anyway");
            Check(WaitFor(() => s.GetState().Readiness == RadioStates.Fault, 8000) && WaitTx(events, TxStates.Idle, null, 5000),
                  "...the radio faults (its control connection is gone) and the transmission is over, said");
            Thread.Sleep(300);
            List<string> after;
            lock (fake.Lock) after = fake.UsbControlLog.Skip(mark).ToList();
            int close0 = after.IndexOf("CLOSE 0"), open0 = after.IndexOf("OPEN 0");
            Check(close0 >= 0 && open0 > close0 && !after.Any(x => x.StartsWith("OPEN ", StringComparison.Ordinal) && x != "OPEN 0") && fake.UsbLiveSessionsKilled == 0,
                  $"D17: SafeIdle went through PIPE 0 again - closed, then opened - never a fourth pipe, nothing killed [{string.Join(", ", after)}]");
            string log = LogText(p.backend);
            Check(!log.Contains("could not be written", StringComparison.Ordinal) && !log.Contains("did NOT take", StringComparison.Ordinal) &&
                  !log.Contains("all are in use", StringComparison.Ordinal) &&
                  (log.Contains("SafeIdle at the fault: the board reads back quiet", StringComparison.Ordinal) ||
                   log.Contains("transmission ended: SafeIdle written", StringComparison.Ordinal)),
                  "...and every SafeIdle path said it took: the transmission's end and the pump's fault, one after the other on the board's lock");
            ev.Dispose();
            try { t.Stop(false, 1000); } catch (Exception) { }
        }
        finally { Stop(p); }
        Check(!fake.UsbHandleOpen, "at close the interface is let go");
    }
}
