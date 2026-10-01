// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using ShackLinkUsrp.Uhd;

namespace ShackLinkUsrp.UhdProbe;

/// <summary>
/// <c>--txqc</c>: an INSTRUMENT for the AD9361's TX quadrature calibration (CRTU200's T1, 2026-10-01). It needs
/// the diagnostic uhd.dll built from C:\Dev\USRP_B210\uhd-4.10-diagnostic with the TXQC logger, which writes the
/// chip's own convergence flags and correction words after every calibration run to <c>UHD_TXQC_LOG</c>; this
/// probe only causes calibrations and writes MARK lines into the same file between them.
///
/// NOTHING IS SENT. A TX streamer is held open and never written, which is the state every sidecar open
/// already makes with its probe streamer: the AD9361's TX chain is powered (that is what makes the
/// calibrations meaningful), and the B210's ATR stays in IDLE / RX_ONLY because the FPGA never runs TX
/// (b200_impl.cpp update_atrs), so the TX path to the port is switched off. The receiver is on RX2.
///
/// Legs (--legs, comma list): repeat (away and back to each frequency, n times - the 100 MHz window
/// calibration at the same frequency, again and again), chains (a new TX streamer at each frequency, n times -
/// the calibration every sidecar key-up runs), table (CRTU200's 120-carrier walk, twice), rxband (repeat with
/// the RX LO in the other gain-table band).
/// </summary>
internal static class TxQcProbe
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string _log = "";
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static int Run(string args, Func<string, string?> get, Func<string, double, double> getDouble)
    {
        string? log = Environment.GetEnvironmentVariable("UHD_TXQC_LOG");
        if (string.IsNullOrWhiteSpace(log))
        {
            Console.WriteLine("[txqc] UHD_TXQC_LOG is not set: the diagnostic uhd.dll would log nowhere - set it before starting this process");
            return 2;
        }
        _log = log;

        double clock = getDouble("--clock", 40e6);
        double rxRate = getDouble("--rate", 10e6);
        double rxBw = getDouble("--bw", 10e6);
        double rxHz = getDouble("--rx-freq", 100e6);
        double txRate = getDouble("--tx-rate", 2e6);
        double txGain = getDouble("--gain", 60);
        double loOffset = getDouble("--lo-offset", -1.25e6);
        int n = (int)getDouble("--n", 6);
        double away = getDouble("--away", 300e6);
        string[] legs = (get("--legs") ?? "repeat,chains,table").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        double[] freqs = (get("--freqs") ?? "309.09e6,548.18e6,1500e6,2412e6")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.Parse(s, NumberStyles.Float, Inv)).ToArray();

        Console.WriteLine($"[open] uhd_usrp_make(\"{args}\")");
        using UhdUsrp usrp = UhdUsrp.Open(args);
        UhdRxInfo ri = usrp.GetRxInfo();
        Mark($"OPEN serial={ri.MboardSerial} name={ri.MboardName} env_sweep={Environment.GetEnvironmentVariable("UHD_TXQC_SWEEP") ?? ""} " +
             $"env_reset={Environment.GetEnvironmentVariable("UHD_TXQC_RESET") ?? ""}");

        double gotClock = usrp.SetMasterClockRate(clock);
        double gotRx = usrp.SetRxRate(rxRate);
        double gotBw = usrp.SetRxBandwidth(rxBw);
        usrp.SetRxAntenna("RX2");
        usrp.TuneRx(rxHz);
        usrp.SetRxGain(30);
        double gotTxRate = usrp.SetTxRate(txRate);
        double gotGain = usrp.SetTxGain(txGain);
        Mark(string.Create(Inv, $"SETUP clock={gotClock} rx_rate={gotRx} rx_bw={gotBw} rx_hz={usrp.GetRxFrequency()} rx_ant={usrp.GetRxAntenna()} " +
                                $"tx_rate={gotTxRate} tx_gain={gotGain} lo_offset={loOffset}"));

        // The receiver runs, as it does under the sidecar; its samples are discarded.
        using UhdRxStreamer rx = usrp.CreateRxStreamer("sc16");
        var stopRx = false;
        var rxThread = new Thread(() =>
        {
            var buf = new short[Math.Max(rx.MaxSamplesPerPacket, 1) * 16 * 2];
            try
            {
                rx.StartContinuous();
                while (!Volatile.Read(ref stopRx)) rx.Recv(buf, 0.25, out _);
            }
            catch (Exception ex) { Console.WriteLine("[rx] " + ex.Message); }
        }) { IsBackground = true, Name = "TxqcRx" };
        rxThread.Start();
        Thread.Sleep(300);

        long before = TxqcLines();
        UhdTxStreamer tx = usrp.CreateTxStreamer();
        Mark("TX_STREAMER created (never written)");
        if (TxqcLines() == before)
        {
            Console.WriteLine("[txqc] creating a TX streamer wrote no TXQC line: the loaded uhd.dll is not the TXQC diagnostic build " +
                              "(or UHD_TXQC_LOG is not reaching it) - nothing measured");
            Volatile.Write(ref stopRx, true);
            rxThread.Join(2000);
            tx.Dispose();
            return 3;
        }

        try
        {
            foreach (string leg in legs)
            {
                switch (leg)
                {
                    case "repeat":
                        foreach (double f in freqs) Repeat(usrp, f, away, loOffset, n, "repeat");
                        break;
                    case "chains":
                        foreach (double f in freqs)
                        {
                            Tune(usrp, Away(f, away), loOffset, "chains-away");
                            Tune(usrp, f, loOffset, "chains-at");
                            for (int i = 0; i < n; i++)
                            {
                                tx.Dispose();
                                tx = usrp.CreateTxStreamer();
                                Mark(string.Create(Inv, $"CHAINS f={f} i={i} (a new TX streamer, nothing tuned)"));
                            }
                        }
                        break;
                    case "table":
                        for (int pass = 0; pass < 2; pass++)
                        {
                            Tune(usrp, 4e9, loOffset, $"table-reset pass={pass}");
                            double step = (2700e6 - 70e6) / 119.0;
                            for (int k = 0; k < 120; k++) Tune(usrp, 70e6 + k * step, loOffset, $"table pass={pass} k={k}");
                        }
                        break;
                    case "rxband":
                        foreach (double rxb in new[] { 100e6, 2400e6 })
                        {
                            usrp.TuneRx(rxb);
                            Mark(string.Create(Inv, $"RXBAND rx_hz={usrp.GetRxFrequency()}"));
                            foreach (double f in new[] { 548.18e6, 1500e6 }) Repeat(usrp, f, away, loOffset, 3, "rxband");
                        }
                        usrp.TuneRx(rxHz);
                        break;
                    default:
                        Console.WriteLine($"[txqc] unknown leg '{leg}' - skipped");
                        break;
                }
            }
        }
        finally
        {
            Volatile.Write(ref stopRx, true);
            rxThread.Join(2000);
            try { rx.Stop(); } catch (Exception ex) { Console.WriteLine("[rx stop] " + ex.Message); }
            tx.Dispose();
        }
        Mark($"DONE {TxqcLines()} TXQC lines in the log");
        return 0;
    }

    private static double Away(double f, double away) => f + away <= 5.9e9 ? f + away : f - away;

    private static void Repeat(UhdUsrp usrp, double f, double away, double loOffset, int n, string leg)
    {
        for (int i = 0; i < n; i++)
        {
            Tune(usrp, Away(f, away), loOffset, $"{leg}-away f={f.ToString("R", Inv)} i={i}");
            Tune(usrp, f, loOffset, $"{leg}-at f={f.ToString("R", Inv)} i={i}");
        }
    }

    private static void Tune(UhdUsrp usrp, double carrierHz, double loOffset, string what)
    {
        UhdTuneResult r = usrp.TuneTx(carrierHz, UhdTxTunePolicy.LoOffset, loOffset);
        Mark(string.Create(Inv, $"TUNE {what} carrier={carrierHz:F0} lo={r.ActualRfHz:F0} dsp={r.ActualDspHz:F0} readback={r.ReadbackHz:F0}"));
    }

    private static long TxqcLines()
    {
        try { return File.ReadLines(_log).LongCount(l => l.StartsWith("TXQC ", StringComparison.Ordinal)); }
        catch (IOException) { return 0; }
    }

    private static void Mark(string text)
    {
        string line = string.Create(Inv, $"MARK t_ms={Clock.Elapsed.TotalMilliseconds:F0} {text}");
        Console.WriteLine(line);
        File.AppendAllText(_log, line + Environment.NewLine);
    }
}
