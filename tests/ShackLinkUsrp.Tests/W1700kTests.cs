// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// The W1700K backend's gate (C:\Dev\SDRW1700K docs/05 option 2), with no unit: the export's codec against its own far end, the
// band / gain / filter rules, and the real backend driven end to end by a FAKE UNIT - an in-memory `w1700k serve` speaking
// upstream's JSON lines, and an exporter sending the unit's datagrams over loopback from a scene of absolute-frequency tones, so a
// retune and a band switch move them across the panorama where a radio would. Plus the instrument --w1700k-replay, which sends a
// recorded capture to a running sidecar (never a gate).

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ShackLinkUsrp.Client;
using ShackLinkUsrp.Host;
using ShackLinkUsrp.Radio;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Tests;

internal static partial class Program
{
    // ---------------------------------------------------------------- the fake unit

    /// <summary>The unit's export, sent over loopback at the wide rate in real time: one datagram per 2048 samples, its sequence
    /// counting every completion. What it carries is a scene of tones at ABSOLUTE frequencies heard about the LO in force.</summary>
    private sealed class FakeExport : IDisposable
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly IPEndPoint _to;
        private readonly Thread _thread;
        private readonly object _lock = new();
        private volatile bool _stop;
        private (long Hz, double Dbfs)[] _scene;
        private long _lo;
        private double _rate;
        private byte[][] _slices = Array.Empty<byte[]>();
        private int _dropNext, _eventsNext;
        public volatile bool Running;
        public volatile bool Packed;
        /// <summary>The LO's leak, as measured on the unit (C:\Dev\SDRW1700K, 2026-10-20).</summary>
        public const double LeakDbfs = -37;
        public ulong Seq = 7_000_000;
        public long Sent;

        public FakeExport(IPEndPoint to, (long Hz, double Dbfs)[] scene, long lo, double rate)
        {
            _to = to;
            _scene = scene;
            _lo = lo;
            _rate = rate;
            Rebuild();
            _thread = new Thread(Loop) { IsBackground = true, Name = "FakeExport" };
            _thread.Start();
        }

        public void Tune(long lo, double rate)
        {
            lock (_lock)
            {
                _lo = lo;
                _rate = rate;
                Rebuild();
            }
        }

        /// <summary>The next <paramref name="n"/> completions are lost between the unit and this machine.</summary>
        public void DropNext(int n)
        {
            lock (_lock) _dropNext += n;
        }

        /// <summary>The next completion is a firmware event: a short slice, no samples.</summary>
        public void EventNext()
        {
            lock (_lock) _eventsNext++;
        }

        /// <summary>64 slices (131,072 samples) of the scene about the LO, looped: the seam is a phase step every 3.3 ms at
        /// 40 MS/s, which an averaged Hann panorama does not see.</summary>
        private void Rebuild()
        {
            const int Slices = 64;
            int n = Slices * W1700kExport.SamplesPerSlice;
            var iq = new double[2 * n];
            // As the unit's decimation delivers it: each tone down by the measured roll-off at its offset, and the LO's leak at DC.
            var rolloff = W1700kCondition.RolloffFor(_rate);
            double leak = 32767 * Math.Pow(10, LeakDbfs / 20) / Math.Sqrt(2);
            for (int i = 0; i < n; i++)
            {
                iq[2 * i] = leak;
                iq[2 * i + 1] = leak;
            }
            foreach (var (hz, dbfs) in _scene)
            {
                double off = hz - _lo;
                if (Math.Abs(off) >= _rate / 2) continue;
                double rolled = rolloff == null ? 0 : W1700kCondition.RolloffDb(rolloff, Math.Abs(off) / (_rate / 2));
                double a = 32767 * Math.Pow(10, (dbfs + rolled) / 20), w = 2 * Math.PI * off / _rate;
                for (int i = 0; i < n; i++)
                {
                    iq[2 * i] += a * Math.Cos(w * i);
                    iq[2 * i + 1] += a * Math.Sin(w * i);
                }
            }
            ulong r = 0x2545F4914F6CDD1DUL;
            var s = new short[2 * W1700kExport.SamplesPerSlice];
            var slices = new byte[Slices][];
            for (int k = 0; k < Slices; k++)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    r ^= r << 13; r ^= r >> 7; r ^= r << 17;
                    double noise = ((r >> 40) / (double)(1UL << 24) - 0.5) * 16;    // a few LSB of 14 bits
                    s[i] = (short)Math.Clamp(Math.Round(iq[2 * k * W1700kExport.SamplesPerSlice + i] + noise), -32768, 32767);
                }
                var slice = new byte[Packed ? W1700kExport.PackedSliceBytes : W1700kExport.RawSliceBytes];
                if (Packed) W1700kExport.PackHalf(s, slice);
                else W1700kExport.PackRaw(s, slice);
                slices[k] = slice;
            }
            _slices = slices;
        }

        private void Loop()
        {
            var dg = new byte[W1700kExport.HeaderBytes + W1700kExport.RawSliceBytes];
            var ev = new byte[64];
            var clock = Stopwatch.StartNew();
            long due = 0;
            int k = 0;
            bool packed = Packed;
            while (!_stop)
            {
                if (!Running)
                {
                    Thread.Sleep(2);
                    clock.Restart();
                    due = 0;
                    continue;
                }
                double rate;
                lock (_lock) rate = _rate;
                long want = (long)(clock.Elapsed.TotalSeconds * rate / W1700kExport.SamplesPerSlice);
                if (due >= want)
                {
                    Thread.Sleep(1);
                    continue;
                }
                for (; due < want && !_stop; due++)
                {
                    byte[] slice;
                    bool drop, isEvent;
                    lock (_lock)
                    {
                        if (packed != Packed)
                        {
                            packed = Packed;
                            Rebuild();
                        }
                        drop = _dropNext > 0;
                        if (drop) _dropNext--;
                        isEvent = !drop && _eventsNext > 0;
                        if (isEvent) _eventsNext--;
                        slice = _slices[k % _slices.Length];
                    }
                    ulong seq = Seq++;
                    if (drop) continue;
                    int len = isEvent ? W1700kExport.Compose(dg, seq, ev) : W1700kExport.Compose(dg, seq, slice);
                    if (!isEvent) k++;
                    try { _socket.SendTo(dg, 0, len, SocketFlags.None, _to); } catch (SocketException) { }
                    Interlocked.Increment(ref Sent);
                }
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(2000);
            _socket.Dispose();
        }
    }

    /// <summary>A TextReader of whole lines handed over from another thread (the fake unit's output).</summary>
    private sealed class LineReader : TextReader
    {
        private readonly BlockingCollection<string> _lines = new();
        public void Add(string line) { try { _lines.Add(line); } catch (InvalidOperationException) { } }
        public void End() => _lines.CompleteAdding();
        public override string? ReadLine()
        {
            try { return _lines.Take(); } catch (InvalidOperationException) { return null; }
        }
    }

    /// <summary>A TextWriter that hands each whole line to <paramref name="onLine"/> (the fake unit's input).</summary>
    private sealed class LineWriter : TextWriter
    {
        private readonly StringBuilder _sb = new();
        private readonly Action<string> _onLine;
        public LineWriter(Action<string> onLine) { _onLine = onLine; }
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value)
        {
            if (value != '\n') { _sb.Append(value); return; }
            string line = _sb.ToString();
            _sb.Clear();
            _onLine(line);
        }
        public override void Write(string? value)
        {
            foreach (char c in value ?? "") Write(c);
        }
    }

    /// <summary>
    /// A unit and its controller as <c>w1700k serve</c> presents them (host/w1700k/controller.py): STATE as upstream composes it,
    /// <c>status</c> answered at once, everything else one at a time on a worker after the attach, a <c>state</c> event around every
    /// operation (busy set, then cleared) and the reply after it. Every request it took is kept for the gate to read.
    /// </summary>
    private sealed class FakeUnit
    {
        public readonly LineReader Out = new();
        public readonly LineWriter In;
        public readonly FakeExport Export;
        public readonly List<JsonObject> Requests = new();
        private readonly BlockingCollection<JsonObject> _work = new();
        private readonly object _lock = new();
        public bool Loaded;
        public int Band;
        public readonly long[] Lo = { 2_442_000_000, 5_250_000_000, 6_105_000_000 };
        public readonly long[] Rate = { 320_000_000, 320_000_000, 320_000_000 };
        public readonly (long Min, long Max)[] Spans = { (2_080_700_000, 2_748_000_000), (4_646_600_000, 7_509_000_000), (4_707_200_000, 7_576_200_000) };
        public readonly JsonObject[] Rf = new JsonObject[3];
        public bool Running;
        public string? Error;
        public string? AttachError;
        public int Attaches;

        public FakeUnit(FakeExport export, bool loaded = true, bool running = false)
        {
            Export = export;
            Loaded = loaded;
            Running = running;
            for (int b = 0; b < 3; b++)
                Rf[b] = new JsonObject().Set("agc", true).Set("lna", null).Set("lpf", null).Set("filter_mhz", 320);
            In = new LineWriter(OnLine);
            new Thread(Worker) { IsBackground = true, Name = "FakeUnit" }.Start();
        }

        public W1700kServe Serve(Action<string> log) => new(In, Out, () => { _work.CompleteAdding(); Out.End(); }, log);

        /// <summary>The unit realises an LO on its synthesizer's grid: here, 4 Hz on band 0 (2.98 on the real one).</summary>
        public static long Realise(int band, long hz) => band == 0 ? hz / 4 * 4 : hz;

        public JsonObject State(string? busy = null)
        {
            lock (_lock)
            {
                var spans = new JsonArray().Add(Loaded ? Spans[Band].Min : W1700kBackend.TuneReach[Band].Min).Add(Loaded ? Spans[Band].Max : W1700kBackend.TuneReach[Band].Max);
                var rf = new JsonObject();
                foreach (var kv in Rf[Band]) rf.Set(kv.Key, kv.Value);
                rf.Set("lo_hz", Lo[Band]).Set("lo_range_hz", spans);
                return new JsonObject()
                    .Set("busy", busy)
                    .Set("radio", new JsonObject().Set("loaded", Loaded).Set("band", Band).Set("lo_hz", Lo[Band]).Set("sample_rate", Rate[Band])
                        .Set("capture_rate", Rate[Band]).Set("bands_ready", Loaded ? new JsonArray().Add(0).Add(1).Add(2) : new JsonArray())
                        .Set("synth_locked", new JsonArray().Add(true).Add(true).Add(true)))
                    .Set("stream", new JsonObject().Set("running", Running).Set("packing", Running ? "raw" : null)
                        .Set("sample_rate", Rate[Band]).Set("center_hz", Lo[Band]))
                    .Set("rf", rf)
                    .Set("error", Error);
            }
        }

        private void Send(JsonObject o) => Out.Add(Json.Serialize(o));

        private void Event(string? busy = null) => Send(new JsonObject().Set("event", "state").Set("state", State(busy)));

        private void OnLine(string line)
        {
            var o = Json.ParseObject(line);
            lock (Requests) Requests.Add(o);
            if (o.GetString("op") == "status") Send(new JsonObject().Set("id", o.GetLong("id")).Set("ok", true).Set("state", State()));
            else try { _work.Add(o); } catch (InvalidOperationException) { }
        }

        /// <summary>The stream ends on its own (an overrun on the unit), as the controller's poll reports it.</summary>
        public void StreamDies(string why)
        {
            lock (_lock)
            {
                Running = false;
                Error = "the stream ended: " + why;
            }
            Export.Running = false;
            Event();
        }

        private void Worker()
        {
            Thread.Sleep(50);
            Interlocked.Increment(ref Attaches);
            if (AttachError != null) Send(new JsonObject().Set("event", "log").Set("level", "error").Set("text", "cannot reach the unit: " + AttachError));
            else
            {
                if (Running)
                {
                    Export.Tune(Lo[Band], Rate[Band]);
                    Export.Running = true;
                }
                Event();
            }
            foreach (var r in _work.GetConsumingEnumerable())
            {
                string op = r.GetString("op") ?? "";
                long id = r.GetLong("id");
                Event(op switch { "configure" => "configuring", "start" => "starting", "stop" => "stopping", "load" => "loading", _ => null });
                string? err = null;
                try { Do(op, r); }
                catch (Exception ex) { err = ex.Message; }
                lock (_lock) Error = err;
                Event();
                Send(err == null
                    ? new JsonObject().Set("id", id).Set("ok", true).Set("state", State())
                    : new JsonObject().Set("id", id).Set("ok", false).Set("error", err));
            }
        }

        private void CheckSpan(int band, long hz)
        {
            var (min, max) = Spans[band];
            if (hz < min || hz > max) throw new InvalidOperationException($"band {band} locks {min / 1e6:0.0}-{max / 1e6:0.0} MHz on this unit");
        }

        private void Do(string op, JsonObject r)
        {
            lock (_lock)
            {
                if (op != "load" && op != "configure" && !Loaded) throw new InvalidOperationException("the radio is not loaded");
                switch (op)
                {
                    case "configure":
                        {
                            int b = (int)r.GetLong("band", -1);
                            if (b < 0 || b > 2) throw new InvalidOperationException("band 0, 1 or 2");
                            long rate = r.GetLong("sample_rate", Rate[b]);
                            if (rate != 40_000_000 && rate != 80_000_000 && rate != 160_000_000 && rate != 320_000_000)
                                throw new InvalidOperationException("sample rate 40, 80, 160, 320 Msps, or 320 Msps captured at 640 (640/2)");
                            if (r.TryGetLong("lo_hz", out long lo)) CheckSpan(b, lo);
                            Loaded = true;
                            Band = b;
                            Rate[b] = rate;
                            if (r.TryGetLong("lo_hz", out lo)) Lo[b] = Realise(b, lo);
                            break;
                        }
                    case "start":
                        Running = true;
                        break;
                    case "stop":
                        Running = false;
                        break;
                    case "tune":
                        {
                            long hz = r.GetLong("hz");
                            CheckSpan(Band, hz);
                            Lo[Band] = Realise(Band, hz);
                            break;
                        }
                    case "gain":
                        if (r.GetBool("agc")) Rf[Band] = Rf[Band].Set("agc", true).Set("lna", null).Set("lpf", null);
                        else
                        {
                            if (!r.TryGetLong("lna", out long lna) || !r.TryGetLong("lpf", out long lpf) || lna < 0 || lna > 4 || lpf < 0 || lpf > 9)
                                throw new InvalidOperationException("gain: the AGC, or LNA index 0..4 and LPF index 0..9");
                            Rf[Band] = Rf[Band].Set("agc", false).Set("lna", lna).Set("lpf", lpf);
                        }
                        break;
                    case "filter":
                        {
                            long mhz = r.GetLong("mhz");
                            if (Array.IndexOf(W1700kBackend.FiltersMhz, (int)mhz) < 0) throw new InvalidOperationException("filter: one of 20, 40, 80, 160, 320 MHz");
                            Rf[Band] = Rf[Band].Set("filter_mhz", mhz);
                            break;
                        }
                    default:
                        throw new InvalidOperationException($"unknown op '{op}'");
                }
                Export.Tune(Lo[Band], Rate[Band]);
                Export.Running = Running;
            }
        }

        public List<JsonObject> Taken(string op)
        {
            lock (Requests) return Requests.Where(q => q.GetString("op") == op).ToList();
        }
    }

    private static readonly (long Hz, double Dbfs)[] W1700kScene =
    {
        (2_450_000_000, -10),     // 8 MHz above the 2442 MHz default LO
        (2_425_000_000, -30),     // 17 MHz below it: 0.85 of Nyquist, 14.6 dB down the roll-off
        (2_460_000_000, -20),     // 18 MHz above it: 0.9 of Nyquist, 17.4 dB down
        (5_305_000_000, -20),     // band 1, and band 2 where they overlap
    };

    private static int FreeUdpPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    private sealed class W1700kRig : IDisposable
    {
        public readonly FakeExport Export;
        public readonly FakeUnit Unit;
        public readonly W1700kBackend Backend;
        public readonly UsrpServer Server;
        public readonly SidecarLog Log;
        public readonly IPEndPoint Bind;

        public W1700kRig(string name, bool loaded = true, bool running = false, string? attachError = null, bool controlled = true, int silenceMs = 3000)
        {
            Bind = new IPEndPoint(IPAddress.Loopback, FreeUdpPort());
            Export = new FakeExport(Bind, W1700kScene, 2_442_000_000, 40e6);
            Unit = new FakeUnit(Export, loaded, running) { AttachError = attachError };
            if (!controlled)
            {
                Unit.Band = 0;
                Unit.Rate[0] = 40_000_000;
                Export.Tune(2_442_000_000, 40e6);
                Export.Running = true;
            }
            Log = new SidecarLog(null, console: false);
            var unit = Unit;
            var log = Log;
            Backend = new W1700kBackend(Log, new W1700kBackendOptions
            {
                Name = name,
                Bind = Bind,
                ServeCommand = null,
                ServeFactory = controlled ? () => unit.Serve(log.Write) : null,
                ReceiveBufferBytes = 64 << 20,
                SilenceFaultMs = silenceMs,
                AttachTimeoutMs = 5000,
                LoadTimeoutMs = 5000,
                CommandTimeoutMs = 5000,
            });
            Server = new UsrpServer(Backend, new UsrpServerOptions { Port = 0, SidecarVersion = "test", LingerSeconds = 60 });
            Server.Start();
            Backend.Start();
        }

        public UsrpSession Connect(string client) => Program.Connect(Server, client);

        public void Dispose()
        {
            Server.Dispose();
            Backend.Dispose();
            Export.Dispose();
            Log.Dispose();
        }
    }

    private static string RefusalOf(Action a)
    {
        try { a(); return ""; }
        catch (UsrpRequestException ex) { return ex.Code + ": " + ex.Message; }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }

    private static JsonObject? W1700kTelemetry(UsrpSession s) => s.GetTelemetry().GetObject("radio")?.GetObject("source");

    /// <summary>Rows of a panorama for <paramref name="ms"/>, after the first <paramref name="skip"/> (each row copied).</summary>
    private static List<(RowHeader h, short[] bins)> RowsFor(PanoramaStream pano, int ms, Func<RowHeader, bool>? keep = null)
    {
        var rows = new List<(RowHeader, short[])>();
        void On(object? c, RowHeader h, short[] bins)
        {
            if (keep == null || keep(h)) lock (rows) rows.Add((h, (short[])bins.Clone()));
        }
        pano.Connection.RowReceived += On;
        Thread.Sleep(ms);
        pano.Connection.RowReceived -= On;
        lock (rows) return rows.ToList();
    }

    // ---------------------------------------------------------------- the cases

    private static void W1700kCodecCases()
    {
        // The word: 14 bits each, I in 29:16 and Q in 13:0, sign-extended and x4 to sc16 full scale.
        Check(W1700kExport.I(0x1FFF_0000) == 8191 * 2 && W1700kExport.Q(0x0000_1FFF) == 8191 * 2, "the largest 14-bit value reads 8191 x2: the radio's full scale is -6 dBFS here, the flatten's headroom");
        Check(W1700kExport.I(0x2000_0000) == -8192 * 2 && W1700kExport.Q(0x0000_2000) == -8192 * 2, "the most negative reads -8192 x2");
        Check(W1700kExport.I(0x3FFF_0000) == -2 && W1700kExport.Q(0x0000_3FFF) == -2, "-1 in 14 bits reads -2: the sign is extended");
        Check(W1700kExport.I(0xC000_C000) == 0 && W1700kExport.Q(0xC000_C000) == 0, "the spare bits are never read as sample bits");
        // Upstream's own conversion (publish.cpp) keeps 14 bits; this one is it x2, for every word.
        var rnd = new Random(7);
        bool upstream = true;
        for (int t = 0; t < 100_000; t++)
        {
            uint w = (uint)rnd.NextInt64(0, 1L << 32) & ~W1700kExport.SpareMask;
            short ui = (short)((int)(w << 2) >> 18), uq = (short)((int)(w << 18) >> 18);
            if (W1700kExport.I(w) != ui * 2 || W1700kExport.Q(w) != uq * 2) upstream = false;
        }
        Check(upstream, "100,000 random words: exactly upstream's 14-bit conversion, x2");

        var s = new short[2 * W1700kExport.SamplesPerSlice];
        for (int i = 0; i < s.Length; i++) s[i] = (short)(rnd.Next(-8192, 8192) * 2);
        s[0] = -16384; s[1] = 16382;
        var raw = new byte[W1700kExport.RawSliceBytes];
        var back = new short[s.Length];
        W1700kExport.PackRaw(s, raw);
        W1700kExport.ConvertRaw(raw, back);
        Check(back.SequenceEqual(s), "a raw slice: 2048 samples there and back, bit for bit");
        bool spare = true;
        for (int k = 0; k < W1700kExport.SamplesPerSlice; k++)
            if ((BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4 * k)) & W1700kExport.SpareMask) != 0) spare = false;
        Check(spare, "...with the spare bits 0, as the unit sends them");
        var packed = new byte[W1700kExport.PackedSliceBytes];
        W1700kExport.PackHalf(s, packed);
        Array.Clear(back);
        W1700kExport.ConvertPacked(packed, back);
        Check(back.SequenceEqual(s), "a packed slice (8 samples in 7 words, upstream's rule): 2048 samples there and back, bit for bit");

        var dg = new byte[W1700kExport.HeaderBytes + W1700kExport.RawSliceBytes];
        int len = W1700kExport.Compose(dg, 123_456_789_012UL, raw);
        Check(W1700kExport.Classify(dg.AsSpan(0, len), out ulong seq, out int sb) == W1700kExport.Kind.Raw && seq == 123_456_789_012UL && sb == 8192,
              "a raw datagram: its kind, sequence and slice length read back");
        len = W1700kExport.Compose(dg, 5, packed);
        Check(W1700kExport.Classify(dg.AsSpan(0, len), out _, out _) == W1700kExport.Kind.Packed, "a packed datagram (7168 bytes)");
        len = W1700kExport.Compose(dg, 6, new byte[200]);
        Check(W1700kExport.Classify(dg.AsSpan(0, len), out _, out _) == W1700kExport.Kind.Event, "a slice under 4096 bytes is a firmware event");
        len = W1700kExport.Compose(dg, 7, new byte[5000]);
        Check(W1700kExport.Classify(dg.AsSpan(0, len), out _, out _) == W1700kExport.Kind.Unknown, "5000 bytes is neither: unknown");
        Check(W1700kExport.Classify(dg.AsSpan(0, 10), out _, out _) == W1700kExport.Kind.Malformed, "under a header: malformed");
        len = W1700kExport.Compose(dg, 8, raw);
        Check(W1700kExport.Classify(dg.AsSpan(0, len - 4), out _, out _) == W1700kExport.Kind.Malformed, "a slice shorter than its header says: malformed");
        dg[0] ^= 0xFF;
        Check(W1700kExport.Classify(dg.AsSpan(0, len), out _, out _) == W1700kExport.Kind.Malformed, "another magic: malformed");
        dg[0] ^= 0xFF;
        dg[4] = 16;
        Check(W1700kExport.Classify(dg.AsSpan(0, len), out _, out _) == W1700kExport.Kind.Malformed, "an offset other than 0 (the 10G path sends whole slices): malformed");
    }

    private static void W1700kRuleCases()
    {
        var reach = W1700kBackend.TuneReach;
        Check(W1700kBackend.BandFor(2_442_000_000, -1, reach) == 0, "2442 MHz is band 0's");
        Check(W1700kBackend.BandFor(5_300_000_000, 0, reach) == 1, "5300 MHz from band 0: band 1, the first that tunes it");
        Check(W1700kBackend.BandFor(5_300_000_000, 2, reach) == 2, "...but band 2 in force keeps it: the overlap is the antenna's to choose");
        Check(W1700kBackend.BandFor(3_500_000_000, 0, reach) == null, "3500 MHz: no band (the 2.75-4.64 GHz gap)");
        Check(W1700kBackend.ParseBand("band 1 (5 GHz)") == 1 && W1700kBackend.ParseBand("Band 2") == 2 && W1700kBackend.ParseBand("0") == 0 &&
              W1700kBackend.ParseBand("band 3") == null && W1700kBackend.ParseBand("RX2") == null, "an antenna names a band: its name, band N or N");

        Check(W1700kBackend.GainFor(0) == (0, 1) && W1700kBackend.DbFor(0, 1) == 0, "0 dB is LNA 0 LPF 1");
        Check(W1700kBackend.GainFor(W1700kBackend.GainMaxDb) == (4, 9) && W1700kBackend.DbFor(4, 9) == W1700kBackend.GainMaxDb,
              $"the top, {F(W1700kBackend.GainMaxDb, "0.0")} dB, is LNA 4 LPF 9 (the highest gain upstream measured)");
        Check(W1700kBackend.GainFor(20) == (3, 2) && W1700kBackend.DbFor(3, 2) == 20.7, "20 dB: LNA 3 (18.7) and one LPF step - 20.7 dB, the step nearest");
        bool near = true, monotone = true;
        double prev = -1;
        for (double db = 0; db <= W1700kBackend.GainMaxDb; db += 0.1)
        {
            var (lna, lpf) = W1700kBackend.GainFor(db);
            double got = W1700kBackend.DbFor(lna, lpf);
            if (Math.Abs(got - db) > 1.0 + 1e-9) near = false;
            if (got < prev - 1e-9) monotone = false;
            prev = got;
        }
        Check(near, "every gain on the axis lands within a dB (the LPF's 2 dB step / 2)");
        Check(monotone, "a higher gain asked is never a lower gain realised");
        Check(W1700kBackend.FilterFor(10e6) == 20 && W1700kBackend.FilterFor(40e6) == 40 && W1700kBackend.FilterFor(41e6) == 80 &&
              W1700kBackend.FilterFor(1e9) == 320, "the analog filter: the narrowest of 20/40/80/160/320 MHz that passes the bandwidth asked");
    }

    private static void W1700kConditionCases()
    {
        foreach (double rate in new[] { 40e6, 80e6, 160e6, 320e6 })
        {
            var taps = W1700kCondition.TapsFor(rate)!;
            var table = W1700kCondition.RolloffFor(rate)!;
            double worst = 0;
            for (int k = 0; k <= 95; k++)
            {
                double x = k / 100.0;
                double want = Math.Min(-W1700kCondition.RolloffDb(table, x), W1700kCondition.MaxGainDb);
                worst = Math.Max(worst, Math.Abs(W1700kCondition.ResponseDb(taps, x) - want));
            }
            Check(taps.Length == 129 && worst < 0.2,
                  $"{rate / 1e6:0} MS/s: upstream's 129-tap design is the inverse roll-off to 0.95 of Nyquist within {F(worst, "0.000")} dB (upstream: 0.15)");
        }
        Check(W1700kCondition.TapsFor(50e6) == null, "a rate with no table has no correction");
        bool symmetric = true;
        var t40 = W1700kCondition.TapsFor(40e6)!;
        for (int k = 0; k < 64; k++) if (t40[k] != t40[128 - k]) symmetric = false;
        Check(symmetric, "the taps are symmetric: linear phase, which the folded FIR relies on");

        // The FIR (SIMD, folded) against the direct convolution.
        var rnd = new Random(3);
        int n2 = 2 * 2048;
        var work = new float[2 * (128 + 2048) + 16];
        for (int i = 0; i < 2 * (128 + 2048); i++) work[i] = (float)(rnd.NextDouble() * 2 - 1);
        var got = new float[n2];
        W1700kCondition.Fir(t40, work, got, n2);
        double err = 0;
        for (int f = 0; f < n2; f++)
        {
            double sum = 0;
            for (int k = 0; k < 129; k++) sum += t40[k] * work[f + 2 * k];
            err = Math.Max(err, Math.Abs(sum - got[f]));
        }
        Check(err < 1e-4, $"the folded FIR is the direct convolution (largest difference {err:E1})");

        // A tone at 0.9 of Nyquist, attenuated as the unit's decimation does, comes back to its level, 64 samples late.
        var cond = new W1700kCondition { RateHz = 40e6, RemoveDc = false };
        double rolled = W1700kCondition.RolloffDb(W1700kCondition.RolloffFor(40e6)!, 0.9);
        double a = 32767 * Math.Pow(10, (-20 + rolled) / 20), w = Math.PI * 0.9;
        var block = new short[4096];
        double power = 0;
        int counted = 0;
        for (int s = 0, p = 0; s < 20; s++)
        {
            for (int i = 0; i < 2048; i++, p++)
            {
                block[2 * i] = (short)Math.Round(a * Math.Cos(w * p));
                block[2 * i + 1] = (short)Math.Round(a * Math.Sin(w * p));
            }
            cond.Process(block);
            if (s < 2) continue;
            for (int i = 0; i < 4096; i++) power += (double)block[i] * block[i];
            counted += 2048;
        }
        double level = 10 * Math.Log10(power / counted) - 20 * Math.Log10(32767);
        Check(Math.Abs(level + 20) < 0.3, $"a -20 dBFS tone at 0.9 of Nyquist, delivered at {F(-20 + rolled)} dBFS, reads {F(level)} dBFS flattened");

        // DC: the leak out, a tone 1 kHz from it untouched.
        var dcOnly = new W1700kCondition { RateHz = 40e6, Flatten = false };
        Check(Math.Abs(W1700kCondition.DcCornerHz(40e6) - 6.07) < 0.05, $"the DC blocker's corner at 40 MS/s: {F(W1700kCondition.DcCornerHz(40e6))} Hz");
        // 1250 Hz: 32 whole cycles in the 1,024,000 samples averaged, so the tone's own mean is zero there.
        double leakA = 32767 * Math.Pow(10, -37 / 20.0), tone = 32767 * Math.Pow(10, -30 / 20.0), wt = 2 * Math.PI * 1250 / 40e6;
        double sI = 0, sQ = 0, pTone = 0;
        int m = 0;
        for (int s = 0, p = 0; s < 3000; s++)
        {
            for (int i = 0; i < 2048; i++, p++)
            {
                block[2 * i] = (short)Math.Round(leakA + tone * Math.Cos(wt * p));
                block[2 * i + 1] = (short)Math.Round(-leakA + tone * Math.Sin(wt * p));
            }
            dcOnly.Process(block);
            if (s < 2500) continue;     // 5.1 M samples: five time constants
            for (int i = 0; i < 2048; i++)
            {
                sI += block[2 * i];
                sQ += block[2 * i + 1];
                pTone += (double)block[2 * i] * block[2 * i] + (double)block[2 * i + 1] * block[2 * i + 1];
                m++;
            }
        }
        double residual = 20 * Math.Log10(Math.Sqrt(sI * sI + sQ * sQ) / m / 32767 + 1e-12);
        double toneLevel = 10 * Math.Log10(pTone / m) - 20 * Math.Log10(32767);
        Check(residual < -75, $"the -37 dBFS leak, after five time constants: {F(residual)} dBFS of DC left");
        Check(Math.Abs(toneLevel + 30) < 0.1, $"a -30 dBFS tone 1.25 kHz from the LO reads {F(toneLevel)} dBFS through it");

        // An instrument line, never a check (a Debug build and a loaded machine both move it): what one slice costs.
        var both = new W1700kCondition { RateHz = 40e6 };
        for (int s = 0; s < 2000; s++) both.Process(block);       // past the JIT's tiers
        var sw = Stopwatch.StartNew();
        for (int s = 0; s < 2000; s++) both.Process(block);
        double us = sw.Elapsed.TotalMilliseconds * 1000 / 2000;
        Console.WriteLine($"  INFO  flatten + DC: {F(us, "0.0")} us a slice, {F(2048 / us, "0.0")} MS/s on one thread ({F(us * 19531 / 1e4, "0.0")} % of a core at 40 MS/s)");
    }

    private static void W1700kEndToEnd()
    {
        using var rig = new W1700kRig("gate");
        using var s = rig.Connect("w1700k-gate");
        Check(s.WaitReady(10000) == RadioStates.Ready, "the radio opens against the fake unit: " + s.GetState().FaultText);
        Check(s.Radio.Source == RadioSources.W1700k && s.Radio.Serial == "w1700k-gate", "hello: source w1700k, serial w1700k-<name>");
        Check(s.Caps.WideRatesHz.SequenceEqual(new[] { 40e6 }) && s.Caps.Tx == null && s.Caps.OverflowOracle == true &&
              s.Caps.RxSides != null && s.Caps.RxSides.SequenceEqual(RxSides.OnlyA) && s.Caps.Antennas.Length == 3,
              "caps: 40 MS/s only (the link's), no transmitter, one side, three bands as antennas, lost samples seen");
        var cfg = rig.Unit.Taken("configure");
        Check(cfg.Count == 1 && cfg[0].GetLong("band") == 0 && cfg[0].GetLong("sample_rate") == 40_000_000 && cfg[0].GetLong("lo_hz") == 2_442_000_000,
              "the open configures band 0 at 40 MS/s and 2442 MHz - the unit was at 320 MS/s, more than the link carries");
        Check(rig.Unit.Taken("start").Count == 1 && rig.Unit.Requests.FindIndex(q => q.GetString("op") == "start") > rig.Unit.Requests.FindIndex(q => q.GetString("op") == "configure"),
              "...and then starts the stream");
        var st = s.GetState();
        Check(st.LoHz == 2_442_000_000 && st.WideRateHz == 40e6 && st.Agc && st.Antenna == W1700kBackend.BandNames[0] && st.BandwidthHz == 320e6,
              $"state: 2442 MHz, 40 MS/s, AGC, band 0, the unit's 320 MHz filter ({st.LoHz}, {st.WideRateHz}, {st.Agc}, {st.Antenna}, {st.BandwidthHz})");

        s.ClaimLo();
        using var pano = s.OpenPanorama(new PanoramaRequest { Bins = 4096, Fps = 10, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
        pano.Connection.StartReader();
        var rows = RowsFor(pano, 1500, h => h.CenterHz == 2_442_000_000);
        Check(rows.Count >= 3, $"panorama rows arrive from the export ({rows.Count} in 1.5 s)");
        if (rows.Count > 0)
        {
            var (h, b) = rows[^1];
            double a = LevelAt(h, b, 2_450_000_000, 40e6), c = LevelAt(h, b, 2_425_000_000, 40e6), quiet = LevelAt(h, b, 2_435_000_000, 40e6);
            Check(Math.Abs(a + 10) < 1, $"the -10 dBFS tone at 2450 MHz reads {F(a)} dBFS: the 14-bit word at sc16 full scale, the axis right");
            Check(Math.Abs(c + 30) < 1, $"the -30 dBFS tone at 2425 MHz, 17 MHz BELOW the LO, reads {F(c)} dBFS: I and Q the right way round, and flattened");
            Check(quiet < -60, $"between them the floor reads {F(quiet)} dBFS");
            double edge = LevelAt(h, b, 2_460_000_000, 40e6), dc = LevelAt(h, b, 2_442_000_000, 40e6);
            Check(Math.Abs(edge + 20) < 1, $"the -20 dBFS tone at 0.9 of Nyquist, delivered 17.4 dB down the roll-off, reads {F(edge)} dBFS: flattened");
            Check(dc < -60, $"the LO's -37 dBFS leak is gone from the centre: {F(dc)} dBFS");
        }

        // Both are settings, and off is what the unit delivers.
        Check(s.GetSettings().Cast<JsonObject>().Any(o => o.GetString("key") == "flatten" && o.GetString("value") == "true") &&
              s.GetSettings().Cast<JsonObject>().Any(o => o.GetString("key") == "dc_removal" && o.GetString("value") == "true"),
              "flatten and dc_removal are settings, on");
        Check(s.SetSetting("flatten", "false") == "false" && s.SetSetting("dc_removal", "off") == "false", "...and set off");
        rows = RowsFor(pano, 1000, h => h.CenterHz == 2_442_000_000);
        if (rows.Count > 0)
        {
            var (h, b) = rows[^1];
            double edge = LevelAt(h, b, 2_460_000_000, 40e6), dc = LevelAt(h, b, 2_442_000_000, 40e6);
            Check(Math.Abs(edge + 37.4) < 1, $"unflattened, the edge tone reads {F(edge)} dBFS: the unit's own roll-off");
            Check(Math.Abs(dc - FakeExport.LeakDbfs) < 1, $"with no DC removal, the leak reads {F(dc)} dBFS");
        }
        else Check(false, "rows with the conditioning off");
        s.SetSetting("flatten", "true");
        s.SetSetting("dc_removal", "true");
        Check(RefusalOf(() => s.SetSetting("flatten", "maybe")).StartsWith(ErrorCodes.BadRequest), "flatten is true or false");
        Check(RefusalOf(() => s.SetSetting("band", "1")).StartsWith(ErrorCodes.Unsupported), "band is the radio's to state, not a setting");

        // Lost completions, and an event, on the way.
        rig.Export.DropNext(5);
        rig.Export.EventNext();
        Check(WaitFor(() => (W1700kTelemetry(s)?.GetLong("events") ?? 0) >= 1 && (W1700kTelemetry(s)?.GetLong("seq_missing") ?? 0) >= 5, 3000),
              "a lost run and an event both reached the receiver");
        var tel = W1700kTelemetry(s)!;
        Check(tel.GetLong("seq_gaps") == 1 && tel.GetLong("seq_missing") == 5, $"5 lost completions: one gap of 5 ({tel.GetLong("seq_gaps")}, {tel.GetLong("seq_missing")})");
        // Every lost sample has ONE cause and is counted once: a completion that never arrived, a slice the conditioning worker had no
        // room for (a Debug build cannot flatten 40 MS/s), or a slice of no known size. The counters move under the read, so the
        // identity is taken from a snapshot in which it holds - a miscount never produces one.
        string ledger = "";
        Check(WaitFor(() =>
        {
            var all = s.GetTelemetry().GetObject("radio")!;
            var src = all.GetObject("source")!;
            long ovf = all.GetLong("overflows"), miss = src.GetLong("seq_missing"), drop = src.GetLong("queue_dropped"), unk = src.GetLong("unknown_size");
            ledger = $"overflows {ovf}, missing {miss}, queue-dropped {drop}, unknown {unk}";
            return ovf == 2048 * (miss + drop + unk);
        }, 3000), "every lost sample is 2048 x (missing completions + slices the worker dropped + unknown slices): " + ledger);
        Check(tel.GetLong("events") == 1, "the event carried no samples and no gap: it is a completion");
        Check(tel.GetLong("malformed") == 0 && tel.GetLong("seq_late") == 0 && tel.GetLong("unknown_size") == 0, "nothing malformed, late or of an unknown size");
        Console.WriteLine($"  INFO  the conditioning worker: {tel.GetLong("queue_dropped")} slices dropped, deepest {tel.GetLong("queue_max_depth")} of {tel.GetLong("queue_capacity")}, " +
                          $"{tel.GetLong("raw")} raw slices in {F(rig.Export.Sent / 19531.25, "0.0")} s of export");

        // Packed ("half") slices, as the unit sends above 160 MS/s: the same samples.
        rig.Export.Packed = true;
        Check(WaitFor(() => (W1700kTelemetry(s)?.GetLong("packed") ?? 0) > 2000, 3000), "packed slices reach the ring");
        rows = RowsFor(pano, 800, h => h.CenterHz == 2_442_000_000);
        Check(rows.Count > 0 && Math.Abs(LevelAt(rows[^1].h, rows[^1].bins, 2_450_000_000, 40e6) + 10) < 1, "...and the tone reads -10 dBFS through them");
        rig.Export.Packed = false;

        // A retune inside the band.
        st = s.SetLo(2_440_000_001);
        var tune = rig.Unit.Taken("tune");
        Check(tune.Count == 1 && tune[0].GetLong("hz") == 2_440_000_001, "set_lo inside band 0 is ONE tune, mid-stream");
        Check(st.LoHz == FakeUnit.Realise(0, 2_440_000_001), $"the state carries the LO the unit REALISED ({st.LoHz})");
        rows = RowsFor(pano, 1200, h => h.CenterHz == st.LoHz);
        if (rows.Count > 0)
        {
            var (h, b) = rows[^1];
            Check(Math.Abs(LevelAt(h, b, 2_450_000_000, 40e6) + 10) < 1, "the tone stays at 2450 MHz on the retuned axis: the scene is absolute");
        }
        else Check(false, "rows at the retuned centre");

        // Another band: the frequency picks it.
        st = s.SetLo(5_300_000_000);
        cfg = rig.Unit.Taken("configure");
        Check(cfg.Count == 2 && cfg[1].GetLong("band") == 1 && cfg[1].GetLong("lo_hz") == 5_300_000_000 && cfg[1].GetLong("sample_rate") == 40_000_000,
              "5300 MHz from band 0: configure band 1 at 5300 MHz AND at the link's 40 MS/s - upstream keeps a rate per band, 320 MS/s after a load");
        Check(rig.Unit.Rate[1] == 40_000_000, "...so band 1 streams at 40 MS/s, never at its own 320");
        Check(st.Antenna == W1700kBackend.BandNames[1] && st.LoHz == 5_300_000_000, $"the antenna reads band 1 ({st.Antenna})");
        rows = RowsFor(pano, 1200, h => h.CenterHz == 5_300_000_000);
        if (rows.Count > 0)
        {
            var (h, b) = rows[^1];
            Check(Math.Abs(LevelAt(h, b, 5_305_000_000, 40e6) + 20) < 1, "band 1 hears its tone at 5305 MHz");
        }
        else Check(false, "rows at 5300 MHz");

        int requests = rig.Unit.Requests.Count;
        string why = RefusalOf(() => s.SetLo(3_500_000_000));
        Check(why.StartsWith(ErrorCodes.OutOfRange) && why.Contains("no band") && why.Contains("band 0 2080.7-2748.0 MHz"),
              "3500 MHz is refused in words naming each band's span - band 0's as the unit MEASURED it: " + why);
        Check(rig.Unit.Requests.Count == requests, "...before anything reached the unit");

        st = s.SetAntenna(W1700kBackend.BandNames[2]);
        cfg = rig.Unit.Taken("configure");
        Check(cfg[^1].GetLong("band") == 2 && cfg[^1].GetLong("lo_hz") == 5_300_000_000 && cfg[^1].GetLong("sample_rate") == 40_000_000 &&
              st.Antenna == W1700kBackend.BandNames[2] && st.LoHz == 5_300_000_000,
              "antenna band 2 at 5300 MHz: the other front end, the same frequency, the link's rate");
        why = RefusalOf(() => s.SetAntenna(W1700kBackend.BandNames[0]));
        Check(why.StartsWith(ErrorCodes.OutOfRange) && why.Contains("does not lock"), "band 0 cannot reach 5300 MHz: refused, naming the remedy: " + why);
        why = RefusalOf(() => s.SetAntenna("RX2"));
        Check(why.StartsWith(ErrorCodes.OutOfRange), "an input that is not a band is refused: " + why);

        // Gain, AGC, filter.
        st = s.SetGain(20);
        var gain = rig.Unit.Taken("gain");
        Check(gain.Count == 1 && gain[0].GetLong("lna") == 3 && gain[0].GetLong("lpf") == 2 && !gain[0].GetBool("agc"),
              "set_gain 20: LNA 3 LPF 2 on the unit");
        Check(st.GainDb == 20.7 && !st.Agc, $"...read back as 20.7 dB, the step it is ({st.GainDb})");
        s.SetAgc(true);
        gain = rig.Unit.Taken("gain");
        Check(gain.Count == 2 && gain[1].GetBool("agc"), "set_agc on: the unit's AGC");
        st = s.SetAgc(false);
        gain = rig.Unit.Taken("gain");
        Check(gain.Count == 3 && gain[2].GetLong("lna") == 3 && gain[2].GetLong("lpf") == 2 && st.GainDb == 20.7,
              "set_agc off: the last manual gain again, said in the state");
        st = s.SetBandwidth(30e6);
        var filter = rig.Unit.Taken("filter");
        Check(filter.Count == 1 && filter[0].GetLong("mhz") == 40 && st.BandwidthHz == 40e6, "set_bandwidth 30 MHz: the 40 MHz filter, read back as such");

        why = RefusalOf(() => s.SetRate(80e6));
        Check(why.StartsWith(ErrorCodes.OutOfRange), "80 MS/s is not offered on this link: refused by the server: " + why);
        why = RefusalOf(() => s.ClaimTx());
        why = why.Length == 0 ? RefusalOf(() => s.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone })) : why;
        Check(why.Length > 0, "nothing transmits: " + why);
        why = RefusalOf(() => s.SetRxSides(new[] { RxSides.A, RxSides.B }));
        Check(why.Length > 0, "no RX B: " + why);

        // The stream ends on its own on the unit: the radio ends, in the unit's words.
        rig.Unit.StreamDies("overrun at chunk 81234");
        Check(WaitFor(() => s.GetState().Readiness == RadioStates.Fault, 3000), "a stream that ends on its own faults the radio");
        Check(s.GetState().FaultText.Contains("overrun at chunk 81234"), "...in the unit's words: " + s.GetState().FaultText);
    }

    private static void W1700kClose()
    {
        var rig = new W1700kRig("close");
        using (var s = rig.Connect("close"))
            Check(s.WaitReady(10000) == RadioStates.Ready, "open");
        var unit = rig.Unit;
        rig.Dispose();
        Check(unit.Taken("stop").Count == 1 && !unit.Running, "closing the radio stops the unit's stream: it never runs on after the sidecar");
    }

    private static void W1700kRefusals()
    {
        using (var rig = new W1700kRig("busy", running: true))
        {
            using var s = rig.Connect("busy");
            s.WaitReady(10000);
            var st = s.GetState();
            Check(st.Readiness == RadioStates.Fault && st.FaultText.Contains("already streaming") && st.FaultText.Contains("--w1700k-serve none"),
                  "a unit found streaming (another controller) is refused, naming both remedies: " + st.FaultText);
            Check(rig.Unit.Taken("configure").Count == 0 && rig.Unit.Taken("start").Count == 0 && rig.Unit.Taken("stop").Count == 0,
                  "...and nothing was sent to it: not a configure, not a start, not a stop");
        }
        using (var rig = new W1700kRig("cold", loaded: false))
        {
            using var s = rig.Connect("cold");
            Check(s.WaitReady(10000) == RadioStates.Ready && rig.Unit.Loaded, "a radio not loaded is loaded by the open's configure (upstream loads it there)");
        }
        using (var rig = new W1700kRig("away", attachError: "ssh: connect to host 10.77.0.1 port 22: Connection timed out"))
        {
            using var s = rig.Connect("away");
            s.WaitReady(10000);
            var st = s.GetState();
            Check(st.Readiness == RadioStates.Fault && st.FaultText.Contains("Connection timed out"), "a unit the controller cannot reach faults in its words: " + st.FaultText);
        }
        // The export's port held by another receiver.
        var held = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        held.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        try
        {
            var log = new SidecarLog(null, console: false);
            using var backend = new W1700kBackend(log, new W1700kBackendOptions { Name = "held", Bind = (IPEndPoint)held.LocalEndPoint!, ServeCommand = null });
            backend.Start();
            backend.WaitReady(5000);
            var st = backend.GetState();
            Check(st.Readiness == RadioStates.Fault && st.FaultText.Contains("held by another program"), "a port another receiver holds: refused in words: " + st.FaultText);
            log.Dispose();
        }
        finally { held.Dispose(); }
    }

    private static void W1700kSilence()
    {
        using var rig = new W1700kRig("silent", silenceMs: 800);
        using var s = rig.Connect("silent");
        Check(s.WaitReady(10000) == RadioStates.Ready, "open");
        rig.Export.Running = false;        // the cable pulled, or a firewall
        Check(WaitFor(() => s.GetState().Readiness == RadioStates.Fault, 3000), "a stream this sidecar started that goes silent faults the radio");
        string t = s.GetState().FaultText;
        Check(t.Contains("no datagram") && t.Contains("firewall"), "...naming what to check: " + t);
        Check(WaitFor(() => rig.Unit.Taken("stop").Count == 1 && !rig.Unit.Running, 3000),
              "...and stops the unit's stream at once: a persistent sidecar holding a faulted radio leaves nothing streaming");
    }

    private static void W1700kNoController()
    {
        using var rig = new W1700kRig("none", controlled: false);
        using var s = rig.Connect("none");
        Check(s.WaitReady(10000) == RadioStates.Ready, "--w1700k-serve none: ready at once, receiving");
        s.ClaimLo();
        using var pano = s.OpenPanorama(new PanoramaRequest { Bins = 4096, Fps = 10, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
        pano.Connection.StartReader();
        var rows = RowsFor(pano, 1200);
        Check(rows.Count >= 2 && Math.Abs(LevelAt(rows[^1].h, rows[^1].bins, 2_450_000_000, 40e6) + 10) < 1,
              "it receives another program's stream, labelled from its command line");
        Check(RefusalOf(() => s.SetLo(2_442_000_000)).Length == 0, "set_lo to the LO it is labelled with is no change, accepted");
        string why = RefusalOf(() => s.SetLo(2_430_000_000));
        Check(why.StartsWith(ErrorCodes.Unsupported) && why.Contains("--w1700k-serve none"), "a retune is refused in words - it changes nothing on the unit: " + why);
        why = RefusalOf(() => s.SetGain(10));
        Check(why.StartsWith(ErrorCodes.Unsupported), "so is a gain: " + why);
        Check(rig.Unit.Requests.Count == 0, "nothing was ever sent to a controller");
    }

    private static void W1700kCommandLine()
    {
        var o = SidecarOptions.Parse(new[] { "--w1700k", "--port", "5555", "--persist" });
        Check(o.W1700k && o.RateHz == 40e6 && o.LoHz == 2_442_000_000 && o.W1700kServe == W1700kServe.DefaultCommand &&
              o.W1700kBind.ToString() == "10.77.0.10:7700" && o.W1700kRatesHz.SequenceEqual(new[] { 40e6 }),
              "--w1700k: 40 MS/s at 2442 MHz, the export on 10.77.0.10:7700, upstream's serve in WSL");
        o = SidecarOptions.Parse(new[] { "--w1700k", "--w1700k-serve", "none", "--w1700k-bind", "127.0.0.1:7701", "--w1700k-rates", "40e6,80e6", "--rate", "80e6", "--lo", "5.8e9" });
        Check(o.W1700kServe == null && o.W1700kBind.Port == 7701 && o.RateHz == 80e6 && o.LoHz == 5_800_000_000 && o.W1700kRatesHz.Length == 2,
              "none, another bind, two rates, the rate and LO given");
        Check(Throws(() => SidecarOptions.Parse(new[] { "--w1700k", "--sim" })), "--w1700k with --sim is refused");
        Check(Throws(() => SidecarOptions.Parse(new[] { "--sim", "--w1700k-bind", "127.0.0.1:7701" })), "a --w1700k-... option without --w1700k is refused");
        Check(Throws(() => SidecarOptions.Parse(new[] { "--w1700k", "--rate", "80e6" })), "a rate the link is not offered is refused");
        Check(Throws(() => SidecarOptions.Parse(new[] { "--w1700k", "--w1700k-rates", "50e6" })), "a rate the unit does not have is refused");
        Check(Throws(() => SidecarOptions.Parse(new[] { "--w1700k", "--otw", "sc8" })), "a UHD option is refused");
        var (exe, args) = W1700kServe.SplitCommand(W1700kServe.DefaultCommand);
        Check(exe == "wsl.exe" && args.StartsWith("-d Ubuntu-24.04 -- bash -lc"), "the default controller command splits as a shell would: " + exe + " | " + args);
        (exe, args) = W1700kServe.SplitCommand("\"C:\\Program Files\\x\\serve.exe\" --a b");
        Check(exe == "C:\\Program Files\\x\\serve.exe" && args == "--a b", "a quoted program path");
    }

    /// <summary>The real executable, --w1700k with no controller, fed by the fake exporter: what the command line reaches.</summary>
    private static void W1700kSpawned()
    {
        string exe = SidecarExe();
        int udp = FreeUdpPort();
        var export = new FakeExport(new IPEndPoint(IPAddress.Loopback, udp), W1700kScene, 2_442_000_000, 40e6) { Running = true };
        var psi = new ProcessStartInfo(exe, $"--w1700k --w1700k-name spawned --w1700k-serve none --w1700k-bind 127.0.0.1:{udp} --port 0 --console --first-client-timeout 20 --linger 1 --state-dir \"{Rendezvous.Root}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi)!;
        try
        {
            int port = 0;
            var sw = Stopwatch.StartNew();
            while (port == 0 && sw.ElapsedMilliseconds < 10000)
            {
                string? line = p.StandardOutput.ReadLine();
                if (line == null) break;
                if (line.StartsWith("SHACKLINKUSRP LISTENING ")) port = int.Parse(line.Substring(24), CultureInfo.InvariantCulture);
            }
            Check(port > 0, $"the sidecar listens ({port})");
            if (port == 0) return;
            _ = Task.Run(() => { while (p.StandardOutput.ReadLine() != null) { } });
            using var s = UsrpSession.Open(UsrpAddress.Remote("127.0.0.1", port), new UsrpSessionOptions { ClientName = "spawned" });
            Check(s.WaitReady(10000) == RadioStates.Ready && s.Radio.Source == RadioSources.W1700k && s.Radio.Serial == "w1700k-spawned",
                  "joined as usrp://127.0.0.1:<port>: a W1700K, ready");
            Check(!File.Exists(Rendezvous.PathFor("w1700k-spawned")), "it published no rendezvous file: only its address reaches it");
            s.ClaimLo();
            using var pano = s.OpenPanorama(new PanoramaRequest { Bins = 4096, Fps = 10, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
            pano.Connection.StartReader();
            var rows = RowsFor(pano, 1500);
            Check(rows.Count >= 2 && Math.Abs(LevelAt(rows[^1].h, rows[^1].bins, 2_450_000_000, 40e6) + 10) < 1,
                  "across the process boundary the export's tone reads -10 dBFS at 2450 MHz");
        }
        finally
        {
            export.Dispose();
            if (!p.WaitForExit(8000))
            {
                Check(false, "the sidecar exits after its last client leaves");
                p.Kill();
            }
        }
    }

    // ---------------------------------------------------------------- the instruments

    /// <summary>
    /// <c>--w1700k-probe &lt;port&gt; [--seconds s] [--bins n]</c>: join a running W1700K sidecar as <c>usrp://127.0.0.1:&lt;port&gt;</c>,
    /// read a panorama, and print what the radio says it is, its strongest peaks, the floor across the span and the ingest's counters - a
    /// client that is not ShackLink, so the sidecar's half can be seen alone. An INSTRUMENT: it always exits 0.
    /// </summary>
    private static int W1700kProbe(string[] args)
    {
        int port = int.Parse(args[1], CultureInfo.InvariantCulture), bins = 4096;
        double seconds = 5;
        long tune = 0;
        for (int i = 2; i + 1 < args.Length; i += 2)
        {
            if (args[i] == "--seconds") seconds = double.Parse(args[i + 1], CultureInfo.InvariantCulture);
            else if (args[i] == "--bins") bins = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            else if (args[i] == "--tune") tune = (long)double.Parse(args[i + 1], CultureInfo.InvariantCulture);
        }
        try
        {
            using var s = UsrpSession.Open(UsrpAddress.Remote("127.0.0.1", port), new UsrpSessionOptions { ClientName = "w1700k-probe", RequestTimeoutMs = 60000 });
            string ready = s.WaitReady(30000);
            var st = s.GetState();
            if (tune > 0 && ready == RadioStates.Ready)
            {
                s.ClaimLo();
                var sw = Stopwatch.StartNew();
                try
                {
                    st = s.SetLo(tune);
                    Console.WriteLine($"set_lo {tune}: {st.LoHz} Hz realised, {st.Antenna}, in {sw.ElapsedMilliseconds} ms");
                }
                catch (UsrpRequestException ex)
                {
                    Console.WriteLine($"set_lo {tune} refused after {sw.ElapsedMilliseconds} ms: {ex.Code}: {ex.Message}");
                }
            }
            Console.WriteLine($"{s.Radio.Product} {s.Radio.Serial} ({s.Radio.Source}): {ready}{(st.FaultText.Length > 0 ? " - " + st.FaultText : "")}");
            Console.WriteLine($"  LO {st.LoHz} Hz, {F(st.WideRateHz / 1e6, "0.###")} MS/s, {st.Antenna}, gain {(st.Agc ? "AGC" : F(st.GainDb, "0.0") + " dB")}, " +
                              $"bandwidth {(st.BandwidthHz > 0 ? F(st.BandwidthHz / 1e6, "0") + " MHz" : "not stated")}");
            if (ready != RadioStates.Ready) return 0;
            using var pano = s.OpenPanorama(new PanoramaRequest { Bins = bins, Fps = 10, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
            pano.Connection.StartReader();
            var rows = RowsFor(pano, (int)(seconds * 1000));
            Console.WriteLine($"  {rows.Count} rows in {seconds} s");
            if (rows.Count > 0)
            {
                var (h, b) = rows[^1];
                double fs = st.WideRateHz, binHz = fs / h.FftSize;
                var peaks = Enumerable.Range(2, h.Bins - 4).Where(i => b[i] >= b[i - 1] && b[i] > b[i + 1] && b[i] >= b[i - 2] && b[i] > b[i + 2])
                    .OrderByDescending(i => b[i]).Take(8);
                foreach (int i in peaks)
                    Console.WriteLine($"  peak {F((h.CenterHz + (i - h.Bins / 2) * binHz) / 1e6, "0.000")} MHz: {F(b[i] / 100.0, "0.0")} dBFS");
                // The floor in eighths of the span: the roll-off shows as edges below the middle, the flatten as edges level with it.
                var eighths = Enumerable.Range(0, 8).Select(k =>
                {
                    var part = b.Skip(k * h.Bins / 8).Take(h.Bins / 8).Select(v => v / 100.0).OrderBy(v => v).ToArray();
                    return part[part.Length / 2].ToString("0.0", CultureInfo.InvariantCulture);
                });
                Console.WriteLine($"  median level by eighths of the span: {string.Join("  ", eighths)} dBFS");
            }
            var tel = s.GetTelemetry().GetObject("radio")!;
            var src = tel.GetObject("source")!;
            Console.WriteLine($"  overflows {tel.GetLong("overflows")}; datagrams {src.GetLong("datagrams")}, raw {src.GetLong("raw")}, packed {src.GetLong("packed")}, " +
                              $"events {src.GetLong("events")}, seq missing {src.GetLong("seq_missing")} in {src.GetLong("seq_gaps")} gaps, late {src.GetLong("seq_late")}, " +
                              $"queue dropped {src.GetLong("queue_dropped")} (deepest {src.GetLong("queue_max_depth")}), flatten {src.GetBool("flattening")}, " +
                              $"dc {src.GetBool("dc_removal")}, clipped {src.GetLong("flatten_clipped")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"probe: {ex.GetType().Name}: {ex.Message}");
        }
        return 0;
    }

    /// <summary>
    /// <c>--w1700k-replay &lt;capture.iq&gt; [--to ip:port] [--seconds s]</c>: send a capture recorded from the unit's export
    /// (C:\Dev\SDRW1700K tools/w1700k-receive.py: the slices concatenated, and its .meta of descriptor words) to a running
    /// sidecar, looped, at 40 MS/s in real time, its sequence continuous. An INSTRUMENT: it always exits 0.
    /// </summary>
    private static int W1700kReplay(string[] args)
    {
        string path = args[1];
        var to = new IPEndPoint(IPAddress.Loopback, 7701);
        double seconds = 3600, rate = 40e6;
        for (int i = 2; i + 1 < args.Length; i += 2)
        {
            if (args[i] == "--to") to = IPEndPoint.Parse(args[i + 1]);
            else if (args[i] == "--seconds") seconds = double.Parse(args[i + 1], CultureInfo.InvariantCulture);
            else if (args[i] == "--rate") rate = double.Parse(args[i + 1], CultureInfo.InvariantCulture);
        }
        byte[] iq = File.ReadAllBytes(path);
        var sizes = new List<int>();
        string meta = Path.ChangeExtension(path, ".meta");
        if (File.Exists(meta))
        {
            var m = File.ReadAllBytes(meta);
            for (int i = 0; i + 4 <= m.Length; i += 4) sizes.Add((int)(BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(i)) >> 16));
        }
        if (sizes.Count == 0 || sizes.Sum() != iq.Length)
        {
            if (sizes.Count > 0) Console.WriteLine($"{meta} does not describe {path} ({sizes.Sum()} bytes against {iq.Length}): read as raw slices");
            sizes = Enumerable.Repeat(W1700kExport.RawSliceBytes, iq.Length / W1700kExport.RawSliceBytes).ToList();
        }
        var offsets = new int[sizes.Count];
        for (int i = 1; i < sizes.Count; i++) offsets[i] = offsets[i - 1] + sizes[i - 1];
        long dataSlices = sizes.Count(x => x >= W1700kExport.EventBelowBytes);
        Console.WriteLine($"replaying {sizes.Count} slices ({dataSlices} with samples, {dataSlices * W1700kExport.SamplesPerSlice / rate:0.000} s) to {to}, looped, at {rate / 1e6:0} MS/s, for {seconds} s");
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var dg = new byte[W1700kExport.HeaderBytes + 65536];
        ulong seq = 1_000_000;
        long sent = 0;
        int k = 0;
        var clock = Stopwatch.StartNew();
        long due = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            long want = (long)(clock.Elapsed.TotalSeconds * rate / W1700kExport.SamplesPerSlice);
            if (due >= want) { Thread.Sleep(1); continue; }
            for (; due < want; k = (k + 1) % sizes.Count)
            {
                int len = W1700kExport.Compose(dg, seq++, iq.AsSpan(offsets[k], sizes[k]));
                sock.SendTo(dg, 0, len, SocketFlags.None, to);
                sent++;
                if (sizes[k] >= W1700kExport.EventBelowBytes) due++;
            }
            if (sent % 195_310 < 20) Console.WriteLine($"{clock.Elapsed.TotalSeconds:0} s: {sent} datagrams");
        }
        return 0;
    }
}
