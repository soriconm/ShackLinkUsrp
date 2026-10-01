// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// A FAKE iiod for the Pluto backend's gate (ShackLink docs/plutosdr_support.md section 9.5, P2): lifted from
// ShackLink's IiodWireTest\FakeIiod.cs (section 2.3's framing, as the owner's board sends it), with its fixture -
// the board's own PRINT - and what a BACKEND needs that a wire gate did not: buffers paced in real time at the rate
// the board was set to, a tone at a fixed RF frequency (or the wire gate's ramp), and the RX overflow flag raised on
// request. Written from section 2.3 and from the owner's board's replies, never from libiio.

#nullable disable

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ShackLinkUsrp.Tests;

internal sealed class FakeIiod : IDisposable
{
    public const string Phy = "iio:device0", Dds = "iio:device2", Lpc = "iio:device3";
    public const string BoardSerial = "104473023196000bf5ff1a00aae12c3ca8";
    public const uint StatusRegister = 0x80000088;

    private readonly TcpListener _listener;
    private readonly Thread _acceptThread;
    private readonly List<TcpClient> _clients = new();
    private readonly Dictionary<string, string> _deviceIds = new(StringComparer.Ordinal);
    private volatile bool _stopped;

    public readonly object Lock = new();

    public string VersionLine = "0.25.v0.25  ";

    /// <summary>Attribute values keyed by <see cref="Key"/>; a WRITE to an absent key is -ENOENT.</summary>
    public readonly Dictionary<string, string> Attrs = new(StringComparer.Ordinal);

    /// <summary>HDL-core registers keyed <c>deviceId|0xADDR</c>.</summary>
    public readonly Dictionary<string, uint> Registers = new(StringComparer.Ordinal);

    public readonly HashSet<string> IgnoreWritesTo = new(StringComparer.Ordinal);
    public readonly List<string> Commands = new();

    /// <summary>Every WRITE as <c>key=value</c> (the value without its NUL), in order - the register selector's included.</summary>
    public readonly List<string> Writes = new();

    public int Accepted;

    // ---- the signal (section 2.5: 12 significant bits, LSB-aligned, sign-extended in 16)
    /// <summary>A complex tone at this ABSOLUTE RF frequency, heard about the RX LO when inside the rate; 0 = the ramp.</summary>
    public long ToneRfHz;
    /// <summary>Its amplitude in 12-bit counts (2048 = full scale).</summary>
    public double ToneAmplitude = 204.8;     // -20 dBFS

    // ---- fault injection
    public string MaskOverride;
    /// <summary>Raise the RX core's overflow flag after every this many buffers served; 0 = never.</summary>
    public int OverflowEveryBuffers;
    public int OverflowsRaised;
    public long BuffersServed;

    private readonly Dictionary<string, uint> _selected = new(StringComparer.Ordinal);

    public FakeIiod(string contextXml)
    {
        ContextXml = contextXml;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using (var sr = new StringReader(contextXml))
        using (var xr = XmlReader.Create(sr, settings))
        {
            var doc = XDocument.Load(xr);
            foreach (var d in doc.Root.Elements("device"))
            {
                string id = (string)d.Attribute("id");
                _deviceIds[id] = id;
                if (d.Attribute("name") != null) _deviceIds[(string)d.Attribute("name")] = id;
            }
        }
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "FakeIiod accept" };
        _acceptThread.Start();
    }

    /// <summary>The owner's board's PRINT (captured 2026-09-30), copied from ShackLink's IiodWireTest\Fixtures.</summary>
    public static string BoardContext() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pluto-7020-context.xml"));

    public int Port { get; }
    public string ContextXml { get; }

    public static string Key(string deviceId, string kind, string channel, string attr) => deviceId + "|" + (kind ?? "") + "|" + (channel ?? "") + "|" + attr;

    public static string RegKey(string deviceId, uint address) => deviceId + "|0x" + address.ToString("X8", CultureInfo.InvariantCulture);

    public string Get(string dev, string kind, string ch, string attr)
    {
        lock (Lock) return Attrs.TryGetValue(Key(dev, kind, ch, attr), out var v) ? v : null;
    }

    /// <summary>What ANOTHER program does to the board: a value set behind the backend's back.</summary>
    public void Set(string dev, string kind, string ch, string attr, string value)
    {
        lock (Lock) Attrs[Key(dev, kind, ch, attr)] = value;
    }

    /// <summary>The attribute writes (the register selector's left out), in order.</summary>
    public List<string> AttributeWrites()
    {
        lock (Lock) return Writes.Where(w => !w.Contains("direct_reg_access")).ToList();
    }

    /// <summary>The owner's board's read-backs on 2026-09-30 (IiodProbe and the P2 read): FDD, both LOs at 2400 MHz,
    /// the TX LO POWERED at 10 dB of attenuation, every DDS scale 0, 30.72 MS/s, no FIR - as P1 left it.</summary>
    public FakeIiod LoadBoardState()
    {
        void S(string dev, string kind, string ch, string attr, string v) => Attrs[Key(dev, kind, ch, attr)] = v;
        S(Phy, "", null, "ensm_mode", "fdd");
        S(Phy, "", null, "xo_correction", "40000000");
        S(Phy, "", null, "xo_correction_available", "[39999980 1 40000020]");
        S(Phy, "", null, "rx_path_rates", "BBPLL:983040000 ADC:245760000 R2:122880000 R1:61440000 RF:30720000 RXSAMP:30720000");
        S(Phy, "OUTPUT", "altvoltage0", "frequency", "2400000000");
        S(Phy, "OUTPUT", "altvoltage0", "powerdown", "0");
        S(Phy, "OUTPUT", "altvoltage0", "frequency_available", "[70000000 1 6000000000]");
        S(Phy, "OUTPUT", "altvoltage1", "frequency", "2400000000");
        S(Phy, "OUTPUT", "altvoltage1", "powerdown", "0");
        S(Phy, "INPUT", "voltage0", "hardwaregain", "71.000000 dB");
        S(Phy, "INPUT", "voltage0", "hardwaregain_available", "[-3 1 71]");
        S(Phy, "INPUT", "voltage0", "gain_control_mode", "slow_attack");
        S(Phy, "INPUT", "voltage0", "gain_control_mode_available", "manual fast_attack slow_attack hybrid");
        S(Phy, "INPUT", "voltage0", "rf_port_select", "A_BALANCED");
        S(Phy, "INPUT", "voltage0", "rf_port_select_available", "A_BALANCED B_BALANCED C_BALANCED A_N A_P B_N B_P C_N C_P TX_MONITOR1 TX_MONITOR2 TX_MONITOR1_2");
        S(Phy, "INPUT", "voltage0", "sampling_frequency", "30720000");
        S(Phy, "INPUT", "voltage0", "sampling_frequency_available", "[2083333 1 61440000]");
        S(Phy, "INPUT", "voltage0", "rf_bandwidth", "18000000");
        S(Phy, "INPUT", "voltage0", "rf_bandwidth_available", "[200000 1 56000000]");
        S(Phy, "INPUT", "voltage0", "filter_fir_en", "0");
        S(Phy, "OUTPUT", "voltage0", "filter_fir_en", "0");
        S(Phy, "INPUT", "out", "voltage_filter_fir_en", "0");
        S(Phy, "INPUT", "voltage0", "rf_dc_offset_tracking_en", "1");
        S(Phy, "INPUT", "voltage0", "bb_dc_offset_tracking_en", "1");
        S(Phy, "INPUT", "voltage0", "quadrature_tracking_en", "1");
        S(Phy, "OUTPUT", "voltage0", "hardwaregain", "-10.000000 dB");
        S(Phy, "OUTPUT", "voltage1", "hardwaregain", "-10.000000 dB");
        S(Phy, "OUTPUT", "voltage0", "rf_port_select", "A");
        S(Phy, "OUTPUT", "voltage0", "rf_bandwidth", "18000000");
        S(Phy, "OUTPUT", "voltage0", "sampling_frequency", "30720000");
        S(Phy, "INPUT", "temp0", "input", "48246");
        S(Lpc, "INPUT", "voltage0", "sampling_frequency", "30720000");
        S(Lpc, "INPUT", "voltage0", "sampling_frequency_available", "30720000 3840000 ");
        S(Dds, "OUTPUT", "voltage0", "sampling_frequency", "30720000");
        S(Dds, "OUTPUT", "voltage0", "sampling_frequency_available", "30720000 3840000 ");
        S(Lpc, "BUFFER", null, "length_align_bytes", "8");
        S(Dds, "BUFFER", null, "length_align_bytes", "8");
        string[] labels = { "TX1_I_F1", "TX1_I_F2", "TX1_Q_F1", "TX1_Q_F2", "TX2_I_F1", "TX2_I_F2", "TX2_Q_F1", "TX2_Q_F2" };
        for (int i = 0; i < 8; i++)
        {
            string ch = "altvoltage" + i.ToString(CultureInfo.InvariantCulture);
            S(Dds, "OUTPUT", ch, "label", labels[i]);
            S(Dds, "OUTPUT", ch, "raw", "1");
            S(Dds, "OUTPUT", ch, "scale", "0.000000");
            S(Dds, "OUTPUT", ch, "frequency", "9279985");
            S(Dds, "OUTPUT", ch, "phase", labels[i].Contains("_I_") ? "90000" : "0");
        }
        // Sticky from earlier use, as found (section 9.8): RX overflow set, TX underflow set.
        Registers[RegKey(Lpc, StatusRegister)] = 0x4;
        Registers[RegKey(Dds, StatusRegister)] = 0x1;
        Attrs[Key(Phy, "DEBUG", null, "direct_reg_access")] = "";
        Attrs[Key(Lpc, "DEBUG", null, "direct_reg_access")] = "";
        Attrs[Key(Dds, "DEBUG", null, "direct_reg_access")] = "";
        return this;
    }

    /// <summary>The state the owner's Soapy session left (section 9.8): a FIR on both chains, the stated rates
    /// narrowed to 520,833-30,720,000.</summary>
    public FakeIiod LoadForeignFir()
    {
        lock (Lock) ApplyFir(true);
        return this;
    }

    private void ApplyFir(bool on)
    {
        string v = on ? "1" : "0";
        Attrs[Key(Phy, "INPUT", "voltage0", "filter_fir_en")] = v;
        Attrs[Key(Phy, "OUTPUT", "voltage0", "filter_fir_en")] = v;
        Attrs[Key(Phy, "INPUT", "out", "voltage_filter_fir_en")] = v;
        Attrs[Key(Phy, "INPUT", "voltage0", "sampling_frequency_available")] = on ? "[520833 1 30720000]" : "[2083333 1 61440000]";
    }

    /// <summary>Close every connection - a board unplugged, or its link lost - and stop answering.</summary>
    public void Dispose()
    {
        _stopped = true;
        try { _listener.Stop(); } catch { }
        lock (Lock)
            foreach (var c in _clients)
                try { c.Close(); } catch { }
    }

    // ------------------------------------------------------------------ serving

    private sealed class Conn
    {
        public TcpClient Client;
        public NetworkStream Stream;
        public string OpenDevice;
        public string OpenMask;
        public int Ramp;
        public double TonePhase;
        public long Sent;
        public long StartTs;
    }

    private void AcceptLoop()
    {
        while (!_stopped)
        {
            TcpClient c;
            try { c = _listener.AcceptTcpClient(); }
            catch { return; }
            lock (Lock)
            {
                if (_stopped) { c.Close(); return; }
                _clients.Add(c);
                Accepted++;
            }
            var conn = new Conn { Client = c, Stream = c.GetStream() };
            new Thread(() => Serve(conn)) { IsBackground = true, Name = "FakeIiod client" }.Start();
        }
    }

    private void Serve(Conn conn)
    {
        try
        {
            while (!_stopped)
            {
                string line = ReadLine(conn);
                if (line == null) return;
                lock (Lock) Commands.Add(line);
                string[] t = line.Split(' ');
                switch (t[0].ToUpperInvariant())
                {
                    case "VERSION": Send(conn, VersionLine + "\n"); break;
                    case "PRINT":
                        {
                            byte[] b = Encoding.UTF8.GetBytes(ContextXml);
                            Send(conn, b.Length.ToString(CultureInfo.InvariantCulture) + "\n");
                            Send(conn, b, 0, b.Length);
                            Send(conn, "\n");
                            break;
                        }
                    case "TIMEOUT": Send(conn, "0\n"); break;
                    case "READ": DoRead(conn, t); break;
                    case "WRITE": DoWrite(conn, t); break;
                    case "SET":
                        Send(conn, t.Length == 4 && t[2].ToUpperInvariant() == "BUFFERS_COUNT" && Resolve(t[1]) != null ? "0\n" : "-22\n");
                        break;
                    case "OPEN":
                        {
                            string dev = Resolve(t[1]);
                            if (dev == null) { Send(conn, "-19\n"); break; }
                            conn.OpenDevice = dev;
                            conn.OpenMask = t[3];
                            conn.Sent = 0;
                            conn.StartTs = System.Diagnostics.Stopwatch.GetTimestamp();
                            Send(conn, "0\n");
                            break;
                        }
                    case "CLOSE":
                        conn.OpenDevice = null;
                        Send(conn, "0\n");
                        break;
                    case "READBUF": DoReadBuf(conn, t); break;
                    default: Send(conn, "-22\n"); break;
                }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        finally
        {
            try { conn.Client.Close(); } catch { }
        }
    }

    private string Resolve(string token) => _deviceIds.TryGetValue(token, out var id) ? id : null;

    private string TargetKey(string[] t, out string dev, out string attr)
    {
        dev = Resolve(t[1]);
        string kind = "", ch = null;
        string k = t[2].ToUpperInvariant();
        if (k == "DEBUG" || k == "BUFFER") { kind = k; attr = t[3]; }
        else if (k == "INPUT" || k == "OUTPUT") { kind = k; ch = t[3]; attr = t[4]; }
        else attr = t[2];
        return dev == null ? null : Key(dev, kind, ch, attr);
    }

    private void DoRead(Conn conn, string[] t)
    {
        string key = TargetKey(t, out string dev, out string attr);
        if (dev == null) { Send(conn, "-19\n"); return; }
        string v;
        lock (Lock)
        {
            if (!Attrs.TryGetValue(key, out v)) { Send(conn, "-2\n"); return; }
            if (attr == "direct_reg_access")
            {
                uint addr = _selected.TryGetValue(dev, out uint a) ? a : 0;
                v = "0x" + (Registers.TryGetValue(RegKey(dev, addr), out uint r) ? r : 0).ToString("X", CultureInfo.InvariantCulture);
            }
        }
        byte[] b = Encoding.UTF8.GetBytes(v + "\0");
        Send(conn, b.Length.ToString(CultureInfo.InvariantCulture) + "\n");
        Send(conn, b, 0, b.Length);
        Send(conn, "\n");
    }

    private void DoWrite(Conn conn, string[] t)
    {
        int n = int.Parse(t[^1], CultureInfo.InvariantCulture);
        byte[] payload = ReadPayload(conn, n);
        string value = Encoding.UTF8.GetString(payload).TrimEnd('\0');
        string key = TargetKey(t, out string dev, out string attr);
        if (dev == null) { Send(conn, "-19\n"); return; }
        lock (Lock)
        {
            if (!Attrs.ContainsKey(key)) { Send(conn, "-2\n"); return; }
            Writes.Add(key + "=" + value);
            if (IgnoreWritesTo.Contains(key)) { Send(conn, n.ToString(CultureInfo.InvariantCulture) + "\n"); return; }
            if (attr == "direct_reg_access")
            {
                string[] p = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                uint addr = ParseReg(p[0]);
                _selected[dev] = addr;
                if (p.Length > 1)
                {
                    uint val = ParseReg(p[1]);
                    string rk = RegKey(dev, addr);
                    uint cur = Registers.TryGetValue(rk, out uint c) ? c : 0;
                    // the status register is write-1-to-clear (section 2.8); anything else is a plain write
                    Registers[rk] = addr == StatusRegister ? cur & ~val : val;
                }
            }
            else if (key == Key(Phy, "INPUT", "out", "voltage_filter_fir_en"))
            {
                if (value != "0" && value != "1") { Send(conn, "-22\n"); return; }
                ApplyFir(value == "1");
            }
            else if (key == Key(Phy, "INPUT", "voltage0", "hardwaregain") && Attrs[Key(Phy, "INPUT", "voltage0", "gain_control_mode")] != "manual")
            {
                // The driver takes a gain only in manual gain control.
                Send(conn, "-22\n");
                return;
            }
            else
            {
                string stored = BoardFormat(attr, value);
                if (stored == null) { Send(conn, "-22\n"); return; }
                if (attr == "hardwaregain" && key == Key(Phy, "INPUT", "voltage0", "hardwaregain"))
                {
                    // The current band's range, as the driver enforces it.
                    double g = double.Parse(value, CultureInfo.InvariantCulture);
                    if (g < -3 || g > 71) { Send(conn, "-22\n"); return; }
                }
                Attrs[key] = stored;
                if (key == Key(Phy, "INPUT", "voltage0", "sampling_frequency"))
                {
                    // ONE clock chain: the AD936x's rate moves both DMA cores' choices; each core is left at /8.
                    long r = long.Parse(stored, CultureInfo.InvariantCulture);
                    string rs = r.ToString(CultureInfo.InvariantCulture), r8 = (r / 8).ToString(CultureInfo.InvariantCulture);
                    Attrs[Key(Lpc, "INPUT", "voltage0", "sampling_frequency_available")] = rs + " " + r8 + " ";
                    Attrs[Key(Lpc, "INPUT", "voltage0", "sampling_frequency")] = r8;
                    Attrs[Key(Phy, "", null, "rx_path_rates")] = $"BBPLL:983040000 ADC:{r * 8} R2:{r * 4} R1:{r * 2} RF:{r} RXSAMP:{r}";
                }
            }
        }
        Send(conn, n.ToString(CultureInfo.InvariantCulture) + "\n");
    }

    private static string BoardFormat(string attr, string v)
    {
        switch (attr)
        {
            case "hardwaregain":
                return double.TryParse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double g)
                    ? g.ToString("F6", CultureInfo.InvariantCulture) + " dB" : null;
            case "scale":
                return double.TryParse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double s)
                    ? s.ToString("F6", CultureInfo.InvariantCulture) : null;
            case "frequency":
            case "sampling_frequency":
            case "rf_bandwidth":
            case "xo_correction":
                return long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out long f) ? f.ToString(CultureInfo.InvariantCulture) : null;
            case "powerdown":
            case "rf_dc_offset_tracking_en":
            case "bb_dc_offset_tracking_en":
            case "quadrature_tracking_en":
                return v == "0" || v == "1" ? v : null;
            default:
                return v;
        }
    }

    private static uint ParseReg(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(s.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : uint.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>
    /// One kernel buffer, PACED: it is sent no earlier than the rate the RX DMA is set to would have filled it,
    /// counted from the OPEN - so a backend's pump sees the board's own cadence. Its samples are the tone at
    /// <see cref="ToneRfHz"/> about the RX LO as it stands for this buffer, or the ramp (I = n mod 2048, Q = -I).
    /// </summary>
    private void DoReadBuf(Conn conn, string[] t)
    {
        string dev = Resolve(t[1]);
        if (dev == null) { Send(conn, "-19\n"); return; }
        if (conn.OpenDevice != dev) { Send(conn, "-9\n"); return; }
        int total = int.Parse(t[2], CultureInfo.InvariantCulture);
        double rate, lo;
        long toneRf;
        double amp;
        string mask;
        lock (Lock)
        {
            rate = double.Parse(Attrs[Key(Lpc, "INPUT", "voltage0", "sampling_frequency")], CultureInfo.InvariantCulture);
            lo = double.Parse(Attrs[Key(Phy, "OUTPUT", "altvoltage0", "frequency")], CultureInfo.InvariantCulture);
            toneRf = ToneRfHz;
            amp = ToneAmplitude;
            mask = MaskOverride ?? conn.OpenMask;
        }
        int n = total / 4;
        double due = (conn.Sent + n) / rate;
        double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(conn.StartTs).TotalSeconds;
        if (due > elapsed) Thread.Sleep(TimeSpan.FromSeconds(due - elapsed));

        var data = new byte[total];
        double w = 2 * Math.PI * (toneRf - lo) / rate;
        bool inBand = toneRf != 0 && Math.Abs(toneRf - lo) < rate / 2;
        for (int i = 0; i + 3 < total; i += 4)
        {
            short iv, qv;
            if (toneRf == 0)
            {
                iv = (short)(conn.Ramp & 0x7FF);
                qv = (short)-(conn.Ramp & 0x7FF);
                conn.Ramp++;
            }
            else if (inBand)
            {
                iv = (short)Math.Clamp(Math.Round(amp * Math.Cos(conn.TonePhase)), -2048, 2047);
                qv = (short)Math.Clamp(Math.Round(amp * Math.Sin(conn.TonePhase)), -2048, 2047);
                conn.TonePhase += w;
            }
            else iv = qv = 0;
            data[i] = (byte)iv;
            data[i + 1] = (byte)(iv >> 8);
            data[i + 2] = (byte)qv;
            data[i + 3] = (byte)(qv >> 8);
        }
        conn.TonePhase %= 2 * Math.PI;
        conn.Sent += n;
        Send(conn, total.ToString(CultureInfo.InvariantCulture) + "\n" + mask + "\n");
        Send(conn, data, 0, total);
        lock (Lock)
        {
            BuffersServed++;
            if (OverflowEveryBuffers > 0 && BuffersServed % OverflowEveryBuffers == 0)
            {
                string rk = RegKey(Lpc, StatusRegister);
                Registers[rk] = (Registers.TryGetValue(rk, out uint c) ? c : 0) | 0x4;
                OverflowsRaised++;
            }
        }
    }

    private string ReadLine(Conn conn)
    {
        var b = new List<byte>();
        while (true)
        {
            int x = conn.Stream.ReadByte();
            if (x < 0) return null;
            if (x == '\n') break;
            b.Add((byte)x);
        }
        if (b.Count > 0 && b[^1] == '\r') b.RemoveAt(b.Count - 1);
        return Encoding.ASCII.GetString(b.ToArray());
    }

    private static byte[] ReadPayload(Conn conn, int n)
    {
        var p = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = conn.Stream.Read(p, got, n - got);
            if (r <= 0) throw new IOException("client closed mid-payload");
            got += r;
        }
        return p;
    }

    private static void Send(Conn conn, string s)
    {
        byte[] b = Encoding.ASCII.GetBytes(s);
        conn.Stream.Write(b, 0, b.Length);
    }

    private static void Send(Conn conn, byte[] b, int offset, int count) => conn.Stream.Write(b, offset, count);
}
