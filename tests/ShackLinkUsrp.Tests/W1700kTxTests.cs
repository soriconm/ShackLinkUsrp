// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// The W1700K transmitter's gate (ShackLink docs/w1700k_support.md "Transmit"), with no unit and NOTHING KEYED: the real backend
// driven through the real server by a FAKE TRANSMIT SERVICE speaking the service wire (framed stdin, JSON lines out) and the
// fake unit of W1700kTests.cs, whose capture stays disconnected after a preparation until it is loaded - so a give-back that
// forgets the load, the rate or the order of the two children shows as silence, a wrong rate, or a recorded violation.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Text;
using ShackLinkUsrp.Client;
using ShackLinkUsrp.Host;
using ShackLinkUsrp.Radio;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Tests;

internal static partial class Program
{
    /// <summary>The service's stdin as the backend writes it: bytes in, whole frames out to the fake service ON THE WRITER'S
    /// THREAD, so a sample frame the service has no room for blocks its writer exactly as a full pipe would.</summary>
    private sealed class FrameStream : Stream
    {
        private readonly FakeTxService _svc;
        private readonly List<byte> _buf = new();
        public FrameStream(FakeTxService svc) { _svc = svc; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!_svc.Alive) throw new IOException("the pipe is being closed");
            _buf.AddRange(buffer.AsSpan(offset, count).ToArray());
            while (_buf.Count >= 5)
            {
                int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(_buf.GetRange(1, 4).ToArray());
                if (_buf.Count < 5 + len) return;
                byte type = _buf[0];
                var payload = _buf.GetRange(5, len).ToArray();
                _buf.RemoveRange(0, 5 + len);
                if (type == W1700kTxServe.FrameJson) _svc.OnJson(Json.ParseObject(Encoding.UTF8.GetString(payload)));
                else if (type == W1700kTxServe.FrameSamples) _svc.OnSamples(payload);
                else _svc.Fatal($"frame type {type}");
                if (!_svc.Alive) throw new IOException("the pipe is being closed");
            }
        }
    }

    /// <summary>
    /// The unit's transmit service as the wire describes it: <c>caps</c> with no unit access, a <c>prepare</c> that needs the
    /// controller gone and disconnects the capture, a <c>start</c> that radiates nothing before its prebuffer, a reader that
    /// blocks while its queue is full, playback at the input rate in real time, a drain acknowledged only when everything
    /// played, and its own ends.
    /// </summary>
    private sealed class FakeTxService
    {
        public const int Rate = 48000, QueueCap = 18000, Prebuffer = 4800;
        public readonly LineReader Out = new();
        public readonly List<JsonObject> Requests = new();
        private readonly W1700kTxRig _rig;
        private readonly object _lock = new();
        public volatile bool Alive = true, Killed, Closed, SilentOnStop;
        /// <summary>The unit's feeder never confirms what it played: a drain ends, unacknowledged.</summary>
        public volatile bool NoAck;
        public (string Op, string Reason, string Error)? Refuse;
        /// <summary>Fields a refusal carries beside its reason and words.</summary>
        public JsonObject? RefuseWith;
        public string State = "idle";
        public long CenterHz;
        public long SamplesIn, SamplesPlayed, Discarded, Underruns, Starts;
        public readonly List<short> Heard = new();
        public double LastLevelDb = double.NaN;
        public string LastEnvelope = "";
        private bool _open, _keyed, _draining, _short;
        private long _queued, _maxMs, _playedThisTx;
        private double _debt;
        private readonly Stopwatch _on = new();

        public FakeTxService(W1700kTxRig rig)
        {
            _rig = rig;
            new Thread(Player) { IsBackground = true, Name = "FakeTxService" }.Start();
        }

        public W1700kTxServe Serve(Action<string> log) =>
            new(new FrameStream(this), Out, () => { Closed = true; End(); }, () => { Killed = true; End(); }, log);

        private void End()
        {
            lock (_lock)
            {
                Alive = false;
                _open = false;
                Monitor.PulseAll(_lock);
            }
            Out.End();
        }

        /// <summary>The service process dies on its own.</summary>
        public void Die() => End();

        public void Fatal(string why)
        {
            Send(new JsonObject().Set("event", "ended").Set("reason", "fatal").Set("text", why));
            End();
        }

        private void Send(JsonObject o) => Out.Add(Json.Serialize(o));

        private static JsonObject Ok(JsonObject r) => new JsonObject().Set("id", r.GetLong("id")).Set("ok", true);

        private static JsonObject No(JsonObject r, string reason, string error) =>
            new JsonObject().Set("id", r.GetLong("id")).Set("ok", false).Set("reason", reason).Set("error", error);

        public List<JsonObject> Taken(string op)
        {
            lock (Requests) return Requests.Where(q => q.GetString("op") == op).ToList();
        }

        public void OnJson(JsonObject r)
        {
            lock (Requests) Requests.Add(r);
            string op = r.GetString("op") ?? "";
            if (Refuse is { } refuse && refuse.Op == op)
            {
                var no = No(r, refuse.Reason, refuse.Error);
                if (RefuseWith != null)
                    foreach (var kv in RefuseWith) no.Set(kv.Key, kv.Value);
                Send(no);
                return;
            }
            switch (op)
            {
                case "hello":
                    Send(Ok(r).Set("wire", 1).Set("service", "w1700k-tx (a gate's)").Set("version", "test").Set("dry", true));
                    break;
                case "caps":
                    {
                        var reply = Ok(r);
                        foreach (var kv in _rig.Caps) reply.Set(kv.Key, kv.Value);
                        Send(reply);
                        break;
                    }
                case "status":
                    Send(Ok(r).Set("state", State).Set("center_hz", CenterHz));
                    break;
                case "prepare":
                    {
                        long hz = r.GetLong("center_hz");
                        if (!((hz >= 2_391_000_000 && hz <= 2_449_000_000) || (hz >= 5_651_000_000 && hz <= 5_849_000_000)))
                        {
                            Send(No(r, "out_of_band", $"{hz / 1e6:0.000} MHz is outside 2391-2449 and 5651-5849 MHz"));
                            break;
                        }
                        lock (_lock)
                            if (_open)
                            {
                                Send(No(r, "keyed", "a transmission is open"));
                                break;
                            }
                        _rig.NotePrepare();
                        Thread.Sleep(30);
                        _rig.CaptureBroken = true;          // the preparation disconnects the capture: only a load restores reception
                        State = "prepared";
                        CenterHz = hz;
                        Send(Ok(r).Set("center_hz", hz).Set("lo_hz", hz).Set("band", hz < 3_000_000_000 ? 0 : 1).Set("path", "load")
                            .Set("took_ms", 30).Set("settle_ms", 2000).Set("settle_measured", false));
                        break;
                    }
                case "start":
                    {
                        if (State != "prepared")
                        {
                            Send(No(r, "not_prepared", "the unit is not prepared"));
                            break;
                        }
                        double level = r.GetDouble("level_db");
                        string envelope = r.GetString("envelope", "") ?? "";
                        double linear = CenterHz < 3_000_000_000 ? -17.1 : 0;
                        if (envelope != "constant" && level > linear + 1e-9)
                        {
                            Send(No(r, "nonlinear_level", $"an arbitrary envelope is linear only at {F(linear, "0.#")} dB or less on this band").Set("linear_max_level_db", linear));
                            break;
                        }
                        long maxMs = r.GetLong("max_ms");
                        if (maxMs < 1 || maxMs > _rig.Caps.GetLong("max_ms"))
                        {
                            Send(No(r, "max_ms", "a key-down is at most " + _rig.Caps.GetLong("max_ms") + " ms"));
                            break;
                        }
                        lock (_lock)
                        {
                            _open = true;
                            _keyed = _draining = _short = false;
                            _queued = _playedThisTx = 0;
                            _debt = 0;
                            _maxMs = maxMs;
                            Starts++;
                            LastLevelDb = level;
                            LastEnvelope = envelope;
                        }
                        // What a level is REALISED as: the unit's own half-dB steps here, so the read-back is not the echo of the ask.
                        double realised = Math.Floor(level * 2 + 1e-9) / 2;
                        Send(Ok(r).Set("level_db", realised).Set("pa_index", 32).Set("digital_db", realised).Set("leakage_dbc", -47.0)
                            .Set("queue_samples", QueueCap).Set("max_ms", maxMs));
                        break;
                    }
                case "stop":
                    {
                        if (SilentOnStop) return;
                        bool drain = r.GetBool("drain");
                        bool wasOpen, emptied = false;
                        lock (_lock)
                        {
                            wasOpen = _open;
                            if (wasOpen && drain)
                            {
                                _draining = true;
                                var until = Stopwatch.StartNew();
                                long limit = Math.Max(0, r.GetLong("timeout_ms", 3000));
                                while (_open && _queued > 0 && until.ElapsedMilliseconds < limit) Monitor.Wait(_lock, 20);
                                emptied = _queued == 0;
                            }
                            _open = false;
                            _queued = 0;
                            Monitor.PulseAll(_lock);
                        }
                        if (!wasOpen)
                        {
                            Send(Ok(r).Set("was_open", false).Set("drain", drain).Set("acked", false));
                            break;
                        }
                        Send(Ok(r).Set("was_open", true).Set("drain", drain).Set("acked", drain && emptied && !NoAck)
                            .Set("samples_in", Interlocked.Read(ref SamplesIn)).Set("samples_played", Interlocked.Read(ref SamplesPlayed))
                            .Set("underruns", Interlocked.Read(ref Underruns)).Set("on_ms", _on.ElapsedMilliseconds).Set("unit_rate_hz", 15886.4)
                            .Set("reason", drain ? "drained" : "stopped").Set("text", ""));
                        break;
                    }
                default:
                    Send(No(r, "unknown_op", $"unknown op '{op}'"));
                    break;
            }
        }

        public void OnSamples(byte[] sc16)
        {
            int n = sc16.Length / 4;
            lock (_lock)
            {
                if (!_open)
                {
                    Discarded += n;         // frames in flight after an end: discarded and counted, never an error
                    return;
                }
                while (_open && _queued + n > QueueCap) Monitor.Wait(_lock, 50);
                if (!_open)
                {
                    Discarded += n;
                    return;
                }
                _queued += n;
                Interlocked.Add(ref SamplesIn, n);
                if (Heard.Count < 400_000)
                    for (int i = 0; i < n * 2; i++) Heard.Add(BinaryPrimitives.ReadInt16LittleEndian(sc16.AsSpan(i * 2)));
                Monitor.PulseAll(_lock);
            }
        }

        /// <summary>The service ends the open transmission itself.</summary>
        public void EndNow(string reason, string text)
        {
            lock (_lock)
            {
                if (!_open) return;
                _open = false;
                _queued = 0;
                Monitor.PulseAll(_lock);
            }
            Send(new JsonObject().Set("event", "ended").Set("reason", reason).Set("text", text)
                .Set("samples_in", Interlocked.Read(ref SamplesIn)).Set("samples_played", Interlocked.Read(ref SamplesPlayed)));
        }

        private void Player()
        {
            var clock = Stopwatch.StartNew();
            double last = 0;
            while (Alive)
            {
                Thread.Sleep(5);
                string? cap = null;
                lock (_lock)
                {
                    double now = clock.Elapsed.TotalSeconds;
                    double dt = now - last;
                    last = now;
                    if (!_open) continue;
                    if (!_keyed)
                    {
                        if (_queued < Prebuffer && !(_draining && _queued > 0)) continue;
                        _keyed = true;
                        _on.Restart();
                        Send(new JsonObject().Set("event", "keyed").Set("t_unix_us", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000));
                        continue;
                    }
                    _debt += dt * Rate;
                    long due = (long)_debt;
                    long take = Math.Min(due, _queued);
                    _queued -= take;
                    _debt -= take;
                    _playedThisTx += take;
                    Interlocked.Add(ref SamplesPlayed, take);
                    if (take < due)
                    {
                        _debt = 0;
                        if (!_short && !_draining) Interlocked.Increment(ref Underruns);
                        _short = true;
                    }
                    else _short = false;
                    if (_on.ElapsedMilliseconds > _maxMs) cap = $"the key-down reached its {_maxMs} ms";
                    Monitor.PulseAll(_lock);
                }
                if (cap != null) EndNow("cap", cap);
            }
        }

        public static JsonObject DefaultCaps() => new JsonObject()
            .Set("wire", 1)
            .Set("input", new JsonObject().Set("rate_hz", Rate).Set("format", "sc16le").Set("max_block_samples", 960))
            .Set("queue_ms", 375).Set("prebuffer_ms", 100).Set("starve_ms", 300).Set("resampler_delay_ms", 4).Set("unit_rate_hz", 15886)
            .Set("max_ms", 15000)
            .Set("envelope", new JsonObject().Set("on", 0.85).Set("off", 0.03).Set("window_ms", 200).Set("max_between", 0.25))
            .Set("bands", new JsonArray()
                .Add(new JsonObject().Set("name", "13cm").Set("band", 0).Set("center_min_hz", 2_391_000_000).Set("center_max_hz", 2_449_000_000)
                    .Set("groups", new JsonArray().Add(new JsonArray().Add(2_391_000_000).Add(2_449_000_000)))
                    .Set("level_min_db", -40.0).Set("level_max_db", 0.0).Set("level_step_db", 0.1).Set("linear_max_level_db", -17.1))
                .Add(new JsonObject().Set("name", "6cm").Set("band", 1).Set("center_min_hz", 5_651_000_000).Set("center_max_hz", 5_849_000_000)
                    .Set("level_min_db", -40.0).Set("level_max_db", 0.0).Set("level_step_db", 0.1).Set("linear_max_level_db", 0.0)))
            .Set("passbands", new JsonArray().Add(new JsonObject().Set("name", "voice").Set("half_width_hz", 3400)
                .Set("occupied", new JsonArray().Add(new JsonObject().Set("down_db", 25).Set("half_width_hz", 16000))).Set("constant_ok", false)))
            .Set("costs", new JsonObject().Set("prepare", new JsonObject().Set("tune_ms", 1000).Set("setup_ms", 4000).Set("load_ms", 11000)).Set("close_ms", 1000));
    }

    /// <summary>The real backend and server over a fake unit, a fake transmit service, and a record of what ran while what else did.</summary>
    private sealed class W1700kTxRig : IDisposable
    {
        public readonly FakeExport Export;
        public readonly List<FakeUnit> Units = new();
        public readonly List<FakeTxService> Services = new();
        public readonly List<string> Violations = new();
        public volatile bool CaptureBroken;
        public JsonObject Caps = FakeTxService.DefaultCaps();
        /// <summary>Set a service up before the backend has it.</summary>
        public Action<FakeTxService>? Arm;
        public readonly W1700kBackend Backend;
        public readonly UsrpServer Server;
        public readonly SidecarLog Log;
        private readonly object _lock = new();

        public FakeUnit Unit { get { lock (_lock) return Units[^1]; } }
        public FakeTxService Service { get { lock (_lock) return Services[^1]; } }
        public int ServiceCount { get { lock (_lock) return Services.Count; } }
        public int UnitCount { get { lock (_lock) return Units.Count; } }

        /// <param name="txCommand">A REAL service's command line in place of the fake one (it must touch no unit).</param>
        /// <param name="configure">Set the options before the backend has them (the built-in service's launch value, folder, version reader).</param>
        public W1700kTxRig(string name, bool tx = true, JsonObject? caps = null, string? txCommand = null, Action<W1700kBackendOptions>? configure = null)
        {
            if (caps != null) Caps = caps;
            var bind = new IPEndPoint(IPAddress.Loopback, FreeUdpPort());
            Export = new FakeExport(bind, W1700kScene, 2_442_000_000, 40e6);
            Log = new SidecarLog(null, console: Environment.GetEnvironmentVariable("W1700K_TX_LOG") == "1");
            var options = new W1700kBackendOptions
            {
                Name = name,
                Bind = bind,
                ServeCommand = null,
                ServeFactory = NewUnit,
                TxServeCommand = txCommand,
                TxServeFactory = tx && txCommand == null ? NewService : null,
                ReceiveBufferBytes = 64 << 20,
                AttachTimeoutMs = 5000,
                LoadTimeoutMs = 5000,
                CommandTimeoutMs = 5000,
                TxRequestTimeoutMs = 3000,
            };
            configure?.Invoke(options);
            Backend = new W1700kBackend(Log, options);
            Server = new UsrpServer(Backend, new UsrpServerOptions { Port = 0, SidecarVersion = "test", LingerSeconds = 60 });
            Server.Start();
            Backend.Start();
        }

        private W1700kServe NewUnit()
        {
            lock (_lock)
            {
                if (Services.Any(s => s.Alive && s.State != "idle"))
                    Violations.Add("w1700k serve was started while a transmit service that had prepared the unit was still running");
                var prev = Units.Count > 0 ? Units[^1] : null;
                var u = new FakeUnit(Export, loaded: prev?.Loaded ?? true) { CaptureBroken = () => CaptureBroken, OnLoad = () => CaptureBroken = false };
                if (prev != null)
                {
                    u.Band = prev.Band;
                    for (int b = 0; b < 3; b++)
                    {
                        u.Lo[b] = prev.Lo[b];
                        u.Rate[b] = prev.Rate[b];
                        u.Rf[b] = prev.Rf[b];
                    }
                }
                Units.Add(u);
                return u.Serve(Log.Write);
            }
        }

        private W1700kTxServe NewService()
        {
            lock (_lock)
            {
                var s = new FakeTxService(this);
                Arm?.Invoke(s);
                Services.Add(s);
                return s.Serve(Log.Write);
            }
        }

        /// <summary>A prepare touches the unit: the controller must have EXITED, and its stream stopped.</summary>
        public void NotePrepare()
        {
            lock (_lock)
            {
                if (Units.Any(u => !u.Closed)) Violations.Add("the unit was prepared while w1700k serve was still running");
                if (Units[^1].Running) Violations.Add("the unit was prepared while its receive stream was running");
            }
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

    private static UsrpRequestException? Refused(Action a)
    {
        try { a(); return null; }
        catch (UsrpRequestException ex) { return ex; }
    }

    // ---------------------------------------------------------------- the cases

    private static void W1700kTxWireCases()
    {
        // The wire's two additions are ABSENT for every radio that transmits beside its receiver: an older peer parses unchanged.
        var plain = new TxCaps { FreqMinHz = 1, FreqMaxHz = 2, RatesHz = new[] { 200e3 } };
        Check(!plain.ToJson().Has("takes_radio") && !plain.ToJson().Has("take_ms") && !TxCaps.Parse(plain.ToJson()).TakesRadio,
              "caps.tx of a radio that does not take: no takes_radio on the wire, and it parses false");
        var taking = TxCaps.Parse(new TxCaps { FreqMinHz = 1, FreqMaxHz = 2, TakesRadio = true, TakeMs = 11000, GiveMs = 12000 }.ToJson());
        Check(taking.TakesRadio && taking.TakeMs == 11000 && taking.GiveMs == 12000 && taking.Clone().TakesRadio, "takes_radio, take_ms and give_ms round-trip, and Clone keeps them");
        Check(!new TxState().ToJson().Has("taken") && TxState.Parse(new TxState().ToJson()).Taken == null,
              "state.tx.taken is absent - not false - where the radio does not state it");
        Check(TxState.Parse(new TxState { Taken = false }.ToJson()).Taken == false && TxState.Parse(new TxState { Taken = true }.ToJson()).Taken == true,
              "...and round-trips both ways where it does");

        var info = W1700kTxCapsInfo.Parse(FakeTxService.DefaultCaps());
        var caps = W1700kBackend.TxCapsFrom(info);
        Check(info.Bands.Length == 2 && info.BandAt(2_392_000_000)?.Name == "13cm" && info.BandAt(5_760_000_000)?.Name == "6cm" && info.BandAt(3_400_000_000) == null,
              "the service's bands: 13cm and 6cm, and nothing between them");
        Check(caps.FreqMinHz == 2_391_000_000 && caps.FreqMaxHz == 5_849_000_000 && caps.RatesHz.SequenceEqual(new[] { 48000.0 }) && caps.MaxMs == 15000 &&
              caps.GainMinDb == 0 && caps.GainMaxDb == 40 && caps.GainStepDb == 0.1 && caps.QuantumSamples == 960,
              $"caps.tx is the service's statement: 48 kS/s in, 15 s, gain 0..40 dB = level -40..0 ({caps.GainMinDb}..{caps.GainMaxDb} step {caps.GainStepDb})");
        Check(caps.TakesRadio && caps.TakeMs == 11000 && caps.GiveMs == 12000 && caps.Timebase == TxTimebases.Host &&
              caps.Sources.SequenceEqual(new[] { TxSourceKinds.Iq, TxSourceKinds.Tone }) && caps.MaxLoOffsetHz == 0,
              "...it takes the radio (11 s to take, 12 s to give back), stamps on the host's clock, and builds iq and tone only");

        var adopting = FakeTxService.DefaultCaps();
        adopting.GetObject("costs").GetObject("prepare").Set("adopt_ms", 1300);
        var adoptCaps = W1700kBackend.TxCapsFrom(W1700kTxCapsInfo.Parse(adopting));
        Check(adoptCaps.TakeMs == 1300 && adoptCaps.GiveMs == 12000,
              "a service that adopts the radio the receiver left states its take as the adopt's 1.3 s; the give-back is still a load");

        // TX4: each band's facts and how the radio was prepared, ABSENT for every radio that does not state them.
        Check(!plain.ToJson().Has("bands") && TxCaps.Parse(plain.ToJson()).Bands.Length == 0 && !new TxState { Taken = true }.ToJson().Has("prepared") &&
              TxState.Parse(new TxState { Taken = true }.ToJson()).Prepared == null,
              "caps.tx.bands and state.tx.prepared are absent - not empty - where a radio states neither, and parse as none");
        var b13 = caps.BandAt(2_392_000_000);
        var b6 = caps.BandAt(5_760_000_000);
        Check(caps.Bands.Length == 2 && b13?.Name == "13cm" && b6?.Name == "6cm" && caps.BandAt(3_400_000_000) == null &&
              Math.Abs(b13.GainMaxDb - 40) < 1e-9 && Math.Abs(b13.GainStepDb - 0.1) < 1e-9 && Math.Abs(b13.LinearMaxGainDb - 22.9) < 1e-9 &&
              Math.Abs(b6.LinearMaxGainDb - 40) < 1e-9 && b13.LoadSettleMs == -1 && b6.LoadSettleMs == -1,
              $"caps.tx.bands is the service's, on the wire's gain axis: 13cm linear to gain {F(b13?.LinearMaxGainDb ?? double.NaN, "0.#")} (level -17.1), 6cm to its top; a settle it does not state is not stated");
        var settling = FakeTxService.DefaultCaps();
        settling.GetObject("costs").Set("settle", new JsonObject().Set("load", new JsonObject().Set("measured", true)
            .Set("settle_ms", new JsonObject().Set("13cm", 30000).Set("6cm", 60000))));
        var settleCaps = TxCaps.Parse(W1700kBackend.TxCapsFrom(W1700kTxCapsInfo.Parse(settling)).ToJson());
        Check(settleCaps.BandAt(2_392_000_000)?.LoadSettleMs == 30000 && settleCaps.BandAt(5_760_000_000)?.LoadSettleMs == 60000 &&
              settleCaps.Clone().Bands.Length == 2 && !ReferenceEquals(settleCaps.Clone().Bands[0], settleCaps.Bands[0]),
              "the settle after a load is the service's own, per band by name (30 s on 13cm, 60 s on 6cm here), across the wire, and Clone copies the bands");
        var noLinear = FakeTxService.DefaultCaps();
        ((JsonObject)noLinear.GetArray("bands")[0]).Remove("linear_max_level_db");
        Check(double.IsNaN(TxCaps.Parse(W1700kBackend.TxCapsFrom(W1700kTxCapsInfo.Parse(noLinear)).ToJson()).Bands[0].LinearMaxGainDb),
              "a band whose linear limit the service does not state has none on the wire - NaN, never 0 or the top");
        var prep = TxState.Parse(new TxState { Taken = true, Prepared = new TxPrepared { Path = "adopt", TookMs = 1100, SettleMs = 0, SettleMeasured = true } }.ToJson()).Prepared;
        Check(prep != null && prep.Path == "adopt" && prep.TookMs == 1100 && prep.SettleMs == 0 && prep.SettleMeasured &&
              TxState.Parse(new TxState { Prepared = new TxPrepared() }.ToJson()).Prepared.TookMs == -1,
              "state.tx.prepared round-trips path, took, settle (0 is a figure) and whether it was measured; an unsaid figure stays unsaid");

        string Why(Action<JsonObject> breakIt)
        {
            var c = FakeTxService.DefaultCaps();
            breakIt(c);
            try { W1700kTxCapsInfo.Parse(c); return ""; }
            catch (InvalidDataException ex) { return ex.Message; }
        }
        Check(Why(c => c.Remove("max_ms")).Contains("max_ms") && Why(c => c.Remove("bands")).Contains("no band") &&
              Why(c => c.GetObject("input").Set("format", "cf32")).Contains("sc16le") && Why(c => c.GetObject("input").Remove("rate_hz")).Contains("rate"),
              "a caps without its essentials is refused in words: no number is invented for the unit");
        Check(Why(c => ((JsonObject)c.GetArray("bands")[0]).Remove("level_step_db")).Contains("level range and step"),
              "...a band with no level step included");
        var refusal = new W1700kTxRefusal("an arbitrary envelope is linear only at -17.1 dB", "nonlinear_level");
        Check(refusal.Said == "the unit's transmitter refused (nonlinear_level): an arbitrary envelope is linear only at -17.1 dB",
              "a refusal is said with its token in brackets before the service's words: " + refusal.Said);
    }

    private static void W1700kTxEndToEnd()
    {
        using var rig = new W1700kTxRig("txgate");
        using var s = rig.Connect("w1700k-tx");
        Check(s.WaitReady(10000) == RadioStates.Ready, "the radio opens: " + s.GetState().FaultText);
        var tx = s.Caps.Tx;
        Check(tx != null && tx.TakesRadio && tx.RatesHz.SequenceEqual(new[] { 48000.0 }) && tx.MaxMs == 15000 && tx.GainMaxDb == 40,
              "caps.tx is stated at open, from the service's caps");
        Check(rig.ServiceCount == 1 && rig.Service.Closed && rig.Service.Taken("prepare").Count == 0 && rig.Violations.Count == 0,
              "...asked of a service that touched nothing on the unit and was let go again, while the controller ran");
        Check(s.GetState().Tx?.Taken == false, "state.tx.taken is false: the radio receives");

        s.ClaimLo();
        using var pano = s.OpenPanorama(new PanoramaRequest { Bins = 4096, Fps = 10, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
        pano.Connection.StartReader();
        var before = RowsFor(pano, 1000, h => h.CenterHz == 2_442_000_000);
        Check(before.Count >= 3 && Math.Abs(LevelAt(before[^1].h, before[^1].bins, 2_450_000_000, 40e6) + 10) < 1, $"it receives ({before.Count} rows, the scene's tone where it is)");

        // ---- not taken: nothing keys
        s.ClaimTx();
        s.SetTx(new TxSettings { GainDb = 20 });
        var early = Refused(() => s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 5000 }));
        Check(early != null && rig.ServiceCount == 1, "a key-up on a radio that was not taken is refused, and no service is started for it: " + early?.Message);
        var outside = Refused(() => s.SetTx(new TxSettings { CenterHz = 3_400_000_000 }));
        Check(outside != null && outside.Message.Contains("no band this unit transmits in") && outside.Message.Contains("13cm") && s.GetState().Tx?.Taken == false &&
              rig.ServiceCount == 1, "a centre in no band is refused in words naming the bands, and takes nothing: " + outside?.Message);

        // ---- the take
        var sw = Stopwatch.StartNew();
        var took = s.SetTx(new TxSettings { CenterHz = 2_392_000_000 });
        Check(took.State.Tx?.Taken == true && took.State.Tx.CenterHz == 2_392_000_000 && took.State.Tx.LoHz == 2_392_000_000,
              $"set_tx's centre TAKES the radio: taken, prepared at 2392 MHz ({sw.ElapsedMilliseconds} ms against the fake)");
        var tookPrep = took.State.Tx?.Prepared;
        Check(tookPrep != null && tookPrep.Path == "load" && tookPrep.TookMs == 30 && tookPrep.SettleMs == 2000 && !tookPrep.SettleMeasured,
              $"...and state.tx.prepared says how, in the service's words: path {tookPrep?.Path}, {tookPrep?.TookMs} ms, its carrier settling {tookPrep?.SettleMs} ms, not measured");
        Check(rig.Violations.Count == 0 && rig.Units[0].Taken("stop").Count == 1 && rig.Units[0].Closed && rig.ServiceCount == 2 &&
              rig.Service.Taken("prepare").Count == 1, "the receive stream was stopped and the controller had EXITED before the unit was prepared: " + string.Join("; ", rig.Violations));
        // The rows already on their way arrive first (how many is this machine's load); then none.
        int trailing = 0;
        for (int k = 0; k < 10 && RowsFor(pano, 400).Count > 0; k++) trailing++;
        var silent = RowsFor(pano, 700);
        Check(trailing < 10 && silent.Count == 0, $"reception has stopped ({silent.Count} rows in 0.7 s, once the rows on their way had arrived)");
        Check(s.GetState().Readiness == RadioStates.Ready && W1700kTelemetry(s)?.GetBool("tx_taken") == true, "...and that is not a fault: the radio is ready, taken");
        string rx = RefusalOf(() => s.SetLo(2_440_000_000));
        Check(rx.Contains("transmitter is taken") && rx.Contains("release"), "the receiver refuses a retune in words naming the release: " + rx);

        // ---- IQ, paced by the service, drained on the unit's word
        var events = TxEvents(s, out var ev);
        var t = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 10_000 });
        Check(t.Stream != null && t.Grant.RateHz == 48000 && t.Grant.CenterHz == 2_392_000_000 && t.Grant.MaxBlockSamples == 960 && t.Grant.QueueSamples == 2400,
              $"the grant: 48 kS/s at 2392 MHz, blocks of at most {t.Grant.MaxBlockSamples}, a queue of {t.Grant.QueueSamples}");
        Check(rig.Service.LastLevelDb == -20 && rig.Service.LastEnvelope == "any" && t.Grant.GainDb == 20,
              $"the service was started at level {rig.Service.LastLevelDb} dB (gain 20 - 40), envelope any; the grant states what it realised ({t.Grant.GainDb} dB)");
        Thread.Sleep(300);
        Check(!s.GetState().Tx!.Keyed && Interlocked.Read(ref rig.Service.SamplesPlayed) == 0, "nothing is keyed before the first block");
        const int total = 192_000;      // 4 s
        var iq = new short[2 * total];
        for (int i = 0; i < total; i++)
        {
            iq[2 * i] = (short)(i % 20000);
            iq[2 * i + 1] = (short)(-(i % 20000));
        }
        sw.Restart();
        t.Stream!.Write(iq, 0, total);
        double handed = sw.Elapsed.TotalSeconds;
        var r = t.Stop(drain: true);
        double out_ = sw.Elapsed.TotalSeconds;
        // The two queues hold about 0.45 s; what the stream's socket buffers hold beyond that is this machine's loopback (measured here).
        Check(handed > 1.0 && handed < 3.9, $"the writer was PACED by the service: 4 s of samples took {F(handed)} s to hand over - {F(4 - handed)} s were ahead of the unit, in the queues and the socket");
        Check(r.Acked && r.Reason == EndReasons.Drained && r.Samples == total && out_ > 3.9 && out_ < 5.2,
              $"the drain is the unit's word: acked, {r.Samples} samples, out in {F(out_)} s for 4 s of samples ({r})");
        List<short> heard;
        lock (rig.Service.Heard) heard = rig.Service.Heard.ToList();
        Check(Interlocked.Read(ref rig.Service.SamplesIn) == total && heard.Count == 2 * total && heard.SequenceEqual(iq),
              "the service got exactly the samples sent, in order");
        Check(Interlocked.Read(ref rig.Service.Underruns) == 0, "...and never ran short of them");
        WaitFor(() => { lock (events) return events.Count(e => e.name == EventNames.Tx) >= 2; }, 2000);
        List<(string name, JsonObject body)> seen;
        lock (events) seen = events.ToList();
        Check(seen.Any(e => e.name == EventNames.Tx && e.body.GetString("state") == TxStates.Keyed) &&
              seen.Any(e => e.name == EventNames.Tx && e.body.GetString("state") == TxStates.Idle && e.body.GetString("reason") == EndReasons.Drained),
              "the tx events: keyed (the service's own word), then idle, drained");
        Check(W1700kTelemetry(s)?.GetDouble("tx_unit_rate_hz", 0) == 15886.4, "the unit's own sample clock, as its feeder measured it, is in the telemetry");

        // ---- the service's refusals arrive as the radio's, token and words
        s.SetTx(new TxSettings { GainDb = 30 });
        var hot = Refused(() => s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 5000 }));
        Check(hot != null && hot.Code == ErrorCodes.OutOfRange && hot.Message.Contains("(nonlinear_level)") && hot.Message.Contains("-17.1") && !s.GetState().Tx!.Keyed,
              "level -10 dB on 13cm with an arbitrary envelope is REFUSED by the service, in its words: " + hot?.Message);
        Check(RefusalOf(() => s.SetTx(new TxSettings { GainDb = 41 })).Contains("OUT_OF_RANGE", StringComparison.OrdinalIgnoreCase) ||
              RefusalOf(() => s.SetTx(new TxSettings { GainDb = 41 })).Contains("outside"), "a gain above the level axis is refused by the rules");

        // ---- a tone
        s.SetTx(new TxSettings { GainDb = 20 });
        long heardBefore = Interlocked.Read(ref rig.Service.SamplesIn);
        lock (rig.Service.Heard) rig.Service.Heard.Clear();
        var tone = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone, MaxMs = 5000, OffsetHz = 1000, LevelDbfs = -6 });
        Thread.Sleep(600);
        var rt = tone.Stop(drain: false);
        lock (rig.Service.Heard) heard = rig.Service.Heard.ToList();
        double peak = heard.Count == 0 ? 0 : heard.Max(v => Math.Abs((double)v));
        Check(rt.Reason == EndReasons.Stopped && Interlocked.Read(ref rig.Service.SamplesIn) - heardBefore > 15_000 && Math.Abs(20 * Math.Log10(peak / 32767) + 6) < 0.2,
              $"a tone goes to the service at its level ({F(20 * Math.Log10(Math.Max(peak, 1) / 32767))} dBFS), paced, and stops where it stands ({rt})");
        var far = Refused(() => s.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone, MaxMs = 5000, OffsetHz = 10_000, LevelDbfs = -6 }));
        Check(far != null && far.Message.Contains("passband") && far.Message.Contains("3400"), "a tone outside the service's passband is refused: it would key on nothing - " + far?.Message);

        // ---- a move while taken, and a move refused
        var moved = s.SetTx(new TxSettings { CenterHz = 5_760_000_000 });
        Check(moved.State.Tx?.CenterHz == 5_760_000_000 && moved.State.Tx.Taken == true && rig.ServiceCount == 2 && rig.Service.Taken("prepare").Count == 2,
              "a new centre on a taken radio is a second preparation by the SAME service: 5760 MHz");
        s.SetTx(new TxSettings { GainDb = 40 });
        var top = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 5000 });
        Check(rig.Service.LastLevelDb == 0, "on 6cm the level axis is linear to its top: gain 40 = level 0 dB is started");
        top.Stop(drain: false);
        rig.Service.Refuse = ("prepare", "interlock", "the FEM word reads 0xaa88882b, not rest");
        var stuck = Refused(() => s.SetTx(new TxSettings { CenterHz = 5_700_000_000 }));
        rig.Service.Refuse = null;
        Check(stuck != null && stuck.Message.Contains("(interlock)") && stuck.Message.Contains("stays prepared where it was") && s.GetState().Tx?.Taken == true &&
              s.GetState().Tx!.CenterHz == 5_760_000_000, "a move the service refuses leaves the radio taken and prepared where it was: " + stuck?.Message);

        // ---- give back
        Check(rig.UnitCount == 1, "through all of that the controller was never started again: one take");
        sw.Restart();
        var back = s.ReleaseTx();
        Check(back.Tx?.Taken == false && back.Tx.CenterHz == 0 && rig.Services[1].Closed && !rig.Services[1].Killed,
              $"release_tx gives the radio back: not taken, the service's input closed ({sw.ElapsedMilliseconds} ms against the fake)");
        Check(back.Tx?.Prepared == null, "...and nothing is prepared any more: state.tx.prepared is gone");
        Check(rig.Violations.Count == 0 && rig.UnitCount == 2, "the service had gone before the controller was started again: " + string.Join("; ", rig.Violations));
        var u2 = rig.Unit;
        int iLoad = u2.Requests.FindIndex(q => q.GetString("op") == "load"), iCfg = u2.Requests.FindIndex(q => q.GetString("op") == "configure"),
            iStart = u2.Requests.FindIndex(q => q.GetString("op") == "start");
        Check(iLoad >= 0 && iLoad < iCfg && iCfg < iStart && u2.Taken("configure")[0].GetLong("sample_rate") == 40_000_000 &&
              u2.Taken("configure")[0].GetLong("lo_hz") == 2_442_000_000 && u2.Taken("configure")[0].GetLong("band") == 0,
              "load (only a load restores reception), then configure with the link's rate and the LO it had, then start");
        var after = RowsFor(pano, 1500, h => h.CenterHz == 2_442_000_000);
        var st = s.GetState();
        Check(after.Count >= 3 && Math.Abs(LevelAt(after[^1].h, after[^1].bins, 2_450_000_000, 40e6) + 10) < 1 && st.WideRateHz == 40e6 && st.LoHz == 2_442_000_000 &&
              st.Readiness == RadioStates.Ready, $"it receives again where it was: {after.Count} rows, the tone back at its level, 40 MS/s");
        Check(RefusalOf(() => s.SetLo(2_440_000_000)) == "", "...and the receiver takes a retune again");
        ev.Dispose();
    }

    private static void W1700kTxFaults()
    {
        // ---- a manual gain and a filter are stated again after the load
        using (var rig = new W1700kTxRig("txgain"))
        using (var s = rig.Connect("w1700k-tx"))
        {
            s.WaitReady(10000);
            s.ClaimLo();
            s.SetGain(20);
            s.SetBandwidth(80e6);
            double gain = s.GetState().GainDb;
            s.ClaimTx();
            s.SetTx(new TxSettings { CenterHz = 2_392_000_000, GainDb = 10 });
            s.ReleaseTx();
            var st = s.GetState();
            var u = rig.Unit;
            Check(u.Taken("gain").Count == 1 && u.Taken("filter").Count == 1 && u.Taken("filter")[0].GetLong("mhz") == 80 && !st.Agc && st.GainDb == gain &&
                  st.BandwidthHz == 80e6, $"after the load the receiver's manual gain and filter are stated again ({st.GainDb} dB, {st.BandwidthHz / 1e6} MHz)");
        }

        // ---- the service ends a transmission itself; frames in flight are discarded; the next start works
        using (var rig = new W1700kTxRig("txended"))
        using (var s = rig.Connect("w1700k-tx"))
        {
            s.WaitReady(10000);
            s.ClaimTx();
            s.SetTx(new TxSettings { CenterHz = 2_392_000_000, GainDb = 20 });
            var events = TxEvents(s, out var ev);
            var t = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 10_000 });
            var iq = new short[2 * 48_000];
            var writer = Task.Run(() =>
            {
                try { for (int k = 0; k < 10; k++) t.Stream!.Write(iq, 0, 48_000); return ""; }
                catch (IOException ex) { return ex.Message; }
            });
            Thread.Sleep(700);
            rig.Service.EndNow("thermal", "the board rose 5.0 degC over its first reading");
            Check(writer.Wait(4000) && writer.Result.Length > 0, "the writer learns its transmission was ended: " + (writer.IsCompleted ? writer.Result : "still writing"));
            WaitFor(() => { lock (events) return events.Any(e => e.name == EventNames.Tx && e.body.GetString("state") == TxStates.Idle); }, 3000);
            (string name, JsonObject body) idle;
            lock (events) idle = events.LastOrDefault(e => e.name == EventNames.Tx && e.body.GetString("state") == TxStates.Idle);
            Check(idle.body != null && idle.body.GetString("reason") == EndReasons.RadioFault && (idle.body.GetString("text") ?? "").Contains("(thermal)") &&
                  (idle.body.GetString("text") ?? "").Contains("5.0 degC"), "the tx event says why, the service's token and words: " + idle.body?.GetString("text"));
            Check(rig.Service.Alive && s.GetState().Tx?.Taken == true && s.GetState().Readiness == RadioStates.Ready,
                  "the service lives on and the radio stays taken: an ended key-down is not a lost transmitter");
            var again = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 10_000 });
            again.Stream!.Write(iq, 0, 9600);
            var r = again.Stop(drain: true);
            Check(r.Acked && r.Samples == 9600, $"the next key-down works ({r})");
            rig.Service.NoAck = true;
            var third = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 10_000 });
            third.Stream!.Write(iq, 0, 9600);
            var unacked = third.Stop(drain: true);
            Check(!unacked.Acked && unacked.Drain && unacked.Samples == 9600,
                  $"a drain the unit's feeder does not confirm is NOT acknowledged: acked is the unit's word and nothing else's ({unacked})");
            ev.Dispose();
        }

        // ---- the service dies under a key-down: the transmission ends, the radio goes back to its receiver
        using (var rig = new W1700kTxRig("txdies"))
        using (var s = rig.Connect("w1700k-tx"))
        {
            s.WaitReady(10000);
            s.ClaimLo();
            using var pano = s.OpenPanorama(new PanoramaRequest { Bins = 4096, Fps = 10, Integration = IntegrationKind.Average, Window = WindowKind.Hann });
            pano.Connection.StartReader();
            s.ClaimTx();
            s.SetTx(new TxSettings { CenterHz = 2_392_000_000, GainDb = 20 });
            var t = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone, MaxMs = 10_000, OffsetHz = 1000, LevelDbfs = -6 });
            Thread.Sleep(400);
            rig.Service.Die();
            Check(WaitFor(() => s.GetState().Tx?.Taken == false && !s.GetState().Tx!.Keyed, 6000), "a service that dies is the end of the transmission and of the take");
            var rows = RowsFor(pano, 1500, h => h.CenterHz == 2_442_000_000);
            Check(rows.Count >= 3 && rig.Unit.Taken("load").Count == 1 && s.GetState().Readiness == RadioStates.Ready && rig.Violations.Count == 0,
                  $"...and the radio is given back to its receiver by itself: {rows.Count} rows after it ({string.Join("; ", rig.Violations)})");
            var owner = s.GetState().Tx?.Owner;
            Check(owner != null && Refused(() => s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 1000 })) != null,
                  "its owner still holds the claim, and a key-up is refused until it takes the radio again");
        }

        // ---- a stop that is not answered: the dead-man
        using (var rig = new W1700kTxRig("txsilent"))
        using (var s = rig.Connect("w1700k-tx"))
        {
            s.WaitReady(10000);
            s.ClaimTx();
            s.SetTx(new TxSettings { CenterHz = 2_392_000_000, GainDb = 20 });
            var t = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone, MaxMs = 10_000, OffsetHz = 1000, LevelDbfs = -6 });
            Thread.Sleep(300);
            var svc = rig.Service;
            svc.SilentOnStop = true;
            var sw = Stopwatch.StartNew();
            var r = t.Stop(drain: false);
            Check(svc.Killed && sw.ElapsedMilliseconds < 4000 && r.Text.Contains("did not answer the stop") && !r.Acked,
                  $"a stop the service does not answer ENDS the service ({sw.ElapsedMilliseconds} ms): {r.Text}");
            Check(WaitFor(() => s.GetState().Tx?.Taken == false, 6000) && rig.Unit.Taken("load").Count == 1, "...and the radio goes back to its receiver");
        }

        // ---- the owner's connection closes while the radio is taken
        using (var rig = new W1700kTxRig("txgone"))
        using (var watcher = rig.Connect("w1700k-watch"))
        {
            watcher.WaitReady(10000);
            var owner = rig.Connect("w1700k-owner");
            owner.ClaimTx();
            owner.SetTx(new TxSettings { CenterHz = 5_760_000_000, GainDb = 30 });
            Check(watcher.GetState().Tx?.Taken == true, "another client sees the radio taken");
            owner.Dispose();
            Check(WaitFor(() => watcher.GetState().Tx?.Taken == false && watcher.GetState().Tx?.Owner == null, 6000) && rig.Unit.Taken("load").Count == 1 &&
                  rig.Services[1].Closed && rig.Violations.Count == 0, "an owner that disconnects gives the radio back: the claim was the take's lifetime");
        }

        // ---- the first preparation refused, and one that fails
        using (var rig = new W1700kTxRig("txrefused"))
        using (var s = rig.Connect("w1700k-tx"))
        {
            s.WaitReady(10000);
            s.ClaimTx();
            rig.Arm = svc => svc.Refuse = ("prepare", "unit_unreachable", "the unit does not answer on 192.168.1.1");
            var no = Refused(() => s.SetTx(new TxSettings { CenterHz = 2_392_000_000 }));
            Check(no != null && no.Message.Contains("(unit_unreachable)") && no.Message.Contains("given back to its receiver") && s.GetState().Tx?.Taken == false,
                  "a first preparation the service refuses takes nothing: " + no?.Message);
            Check(s.GetState().Readiness == RadioStates.Ready && rig.Unit.Taken("start").Count == 1 && rig.Violations.Count == 0, "...and reception is back");
            rig.Arm = svc => svc.Refuse = ("hello", "broken", "no station");
            var dead = Refused(() => s.SetTx(new TxSettings { CenterHz = 2_392_000_000 }));
            Check(dead != null && dead.Message.Contains("did not start") && s.GetState().Tx?.Taken == false && s.GetState().Readiness == RadioStates.Ready &&
                  rig.Unit.Taken("load").Count == 0, "a service that does not start takes nothing, and the unit - untouched - needs no load: " + dead?.Message);
        }

        // ---- a MOVE that fails after it touched the unit: the service says nothing is prepared, and the radio goes back
        using (var rig = new W1700kTxRig("txunprepared"))
        using (var s = rig.Connect("w1700k-tx"))
        {
            s.WaitReady(10000);
            s.ClaimTx();
            s.SetTx(new TxSettings { CenterHz = 2_392_000_000, GainDb = 10 });
            rig.Service.Refuse = ("prepare", "fault", "the synthesizer did not lock at 5700 MHz");
            rig.Service.RefuseWith = new JsonObject().Set("prepared", false);
            var no = Refused(() => s.SetTx(new TxSettings { CenterHz = 5_700_000_000 }));
            Check(no != null && no.Message.Contains("(fault)") && no.Message.Contains("given back to its receiver") && !no.Message.Contains("stays prepared") &&
                  s.GetState().Tx?.Taken == false && rig.Unit.Taken("load").Count == 1 && s.GetState().Readiness == RadioStates.Ready,
                  "a move that left the unit unprepared (\"prepared\": false) is not called prepared: the radio is given back, with a load - " + no?.Message);
        }

        // ---- no transmit service: no transmitter, as before
        using (var rig = new W1700kTxRig("txnone", tx: false))
        using (var s = rig.Connect("w1700k-rx"))
        {
            s.WaitReady(10000);
            s.ClaimTx();
            var none = Refused(() => s.SetTx(new TxSettings { CenterHz = 2_392_000_000 }));
            Check(s.Caps.Tx == null && s.GetState().Tx?.Taken == null && none?.Message.Contains("does not transmit") == true && rig.ServiceCount == 0,
                  "without --w1700k-tx-serve the radio states no transmitter, nothing says taken, and no service is ever started: " + none?.Message);
            s.ReleaseTx();
            Check(s.GetState().Readiness == RadioStates.Ready && rig.UnitCount == 1, "...and a release there gives nothing back: the receiver was never stopped");
        }

        // ---- a service whose caps are unusable is no transmitter, and the receiver is untouched
        var bad = FakeTxService.DefaultCaps();
        bad.Remove("max_ms");
        using (var rig = new W1700kTxRig("txbadcaps", caps: bad))
        using (var s = rig.Connect("w1700k-rx"))
        {
            Check(s.WaitReady(10000) == RadioStates.Ready && s.Caps.Tx == null, "a service that states no longest key-down is no transmitter; the radio still receives");
        }
    }

    /// <summary>The SDRW1700K session's service itself, in its no-unit mode (<c>--fake</c>: a clock in place of the radio, nothing
    /// keyed, nothing reached). Where the script or a Python to run it is not on this machine the case says so and checks
    /// nothing: it is the fake of this file that is the gate, and this is the proof that the fake is like the thing.</summary>
    private static void W1700kTxRealService()
    {
        string script = Environment.GetEnvironmentVariable("W1700K_TX_SERVE") ?? @"C:\Dev\SDRW1700K\tools\w1700k-tx-serve.py";
        string python = Environment.GetEnvironmentVariable("W1700K_TX_PYTHON") ?? "python";
        if (!File.Exists(script))
        {
            Console.WriteLine($"  SKIP  no service at {script} (set W1700K_TX_SERVE): nothing checked");
            return;
        }
        using var rig = new W1700kTxRig("txreal", txCommand: $"{python} \"{script}\" --fake --fake-prepare-s 0.3");
        using var s = rig.Connect("w1700k-tx");
        Check(s.WaitReady(20000) == RadioStates.Ready, "the radio opens: " + s.GetState().FaultText);
        var tx = s.Caps.Tx;
        if (tx == null)
        {
            Console.WriteLine("  SKIP  the service did not state a transmitter here (no Python, or a module it needs is missing): nothing checked");
            return;
        }
        // The longest key-down is the SERVICE's figure and moves with its measurements (T24: 15 s by policy; T25, 2026-11-10: 30 min
        // measured): only its presence is asserted, never a number this file would have to chase.
        Check(tx.TakesRadio && tx.RatesHz.SequenceEqual(new[] { 48000.0 }) && tx.MaxMs > 0 && tx.GainMinDb == 0 && tx.GainMaxDb == 40 &&
              tx.FreqMinHz == 2_391_000_000 && tx.FreqMaxHz == 5_849_000_000 && tx.Sources.SequenceEqual(new[] { TxSourceKinds.Iq, TxSourceKinds.Tone }),
              $"the REAL service's caps are read: 48 kS/s, at most {tx.MaxMs} ms a key-down, gain 0..{tx.GainMaxDb} dB, 2391-5849 MHz, taking the radio ({tx.TakeMs} ms to take)");
        // TX4: the REAL service's own band facts reach the wire - the fake's shape is not evidence of the service's (names, the linear
        // limit, the settle after a load keyed by band name). The figures are the service's and only their presence is asserted.
        var r13 = tx.BandAt(2_392_000_000);
        var r6 = tx.BandAt(5_680_000_000);
        Check(r13?.Name == "13cm" && r6?.Name == "6cm" && !double.IsNaN(r13.LinearMaxGainDb) && r13.LinearMaxGainDb > 0 && r13.LinearMaxGainDb < r13.GainMaxDb &&
              r13.LoadSettleMs > 0 && r6.LoadSettleMs > 0,
              $"TX4: the REAL service's bands reach the wire - 13cm linear to gain {F(r13?.LinearMaxGainDb ?? double.NaN, "0.#")} of {F(r13?.GainMaxDb ?? double.NaN, "0.#")}, " +
              $"6cm to {F(r6?.LinearMaxGainDb ?? double.NaN, "0.#")}; after a load the carrier settles {r13?.LoadSettleMs} / {r6?.LoadSettleMs} ms (13cm / 6cm)");
        s.ClaimTx();
        var took = s.SetTx(new TxSettings { CenterHz = 2_392_000_000, GainDb = 20 });
        Check(took.State.Tx?.Taken == true && took.State.Tx.CenterHz == 2_392_000_000 && rig.Units[0].Closed && rig.Units[0].Taken("stop").Count == 1,
              "it is taken and prepared at 2392 MHz, the controller gone first");
        var realPrep = took.State.Tx?.Prepared;
        Check(realPrep != null && realPrep.Path.Length > 0 && realPrep.TookMs >= 0,
              $"TX4: ...and state.tx.prepared says how, in the REAL service's words: path {realPrep?.Path}, {realPrep?.TookMs} ms, settle {realPrep?.SettleMs} ms" +
              (realPrep?.SettleMeasured == true ? " (measured)" : ""));
        Check(rig.Units[0].Taken("load").Count == 0 && rig.Units[0].Requests.All(q => q.GetString("op") != "unload"),
              "...and left LOADED: the controller was stopped and closed, never unloaded, which is what the service's adopt relies on");

        var events = TxEvents(s, out var ev);
        var t = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 10_000 });
        const int total = 96_000;       // 2 s
        var iq = new short[2 * total];
        for (int i = 0; i < total; i++)
        {
            double a = 2 * Math.PI * 1000 * i / 48000.0;
            iq[2 * i] = (short)(20000 * Math.Cos(a));
            iq[2 * i + 1] = (short)(20000 * Math.Sin(a));
        }
        var sw = Stopwatch.StartNew();
        t.Stream!.Write(iq, 0, total);
        var r = t.Stop(drain: true);
        double out_ = sw.Elapsed.TotalSeconds;
        Check(r.Acked && r.Reason == EndReasons.Drained && r.Samples == total && r.Underflows == 0 && out_ > 1.9 && out_ < 4.5,
              $"2 s of IQ: drained on the service's word, {r.Samples} samples, no underrun, out in {F(out_)} s ({r})");
        WaitFor(() => { lock (events) return events.Count(e => e.name == EventNames.Tx) >= 2; }, 3000);
        lock (events)
            Check(events.Any(e => e.name == EventNames.Tx && e.body.GetString("state") == TxStates.Keyed), "its keyed event became the tx event");
        Check(t.Grant.GainDb == 20 && Math.Abs((W1700kTelemetry(s)?.GetDouble("tx_unit_rate_hz", 0) ?? 0) - 15888) < 5,
              $"the level it realised is the gain granted ({t.Grant.GainDb} dB), and the unit's clock it reported is in the telemetry ({W1700kTelemetry(s)?.GetDouble("tx_unit_rate_hz", 0)} S/s)");

        s.SetTx(new TxSettings { GainDb = 30 });
        var hot = Refused(() => s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 5000 }));
        Check(hot != null && hot.Message.Contains("(nonlinear_level)"), "level -10 dB on 13 cm with an arbitrary envelope is the service's own refusal: " + hot?.Message);

        s.SetTx(new TxSettings { GainDb = 20 });
        var tone = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Tone, MaxMs = 5000, OffsetHz = 1000, LevelDbfs = -6 });
        Thread.Sleep(700);
        var rt = tone.Stop(drain: false);
        Check(rt.Reason == EndReasons.Stopped && rt.Samples > 15_000 && !rt.Text.Contains("did not answer"), $"a tone, stopped where it stood and ANSWERED ({rt})");

        var moved = s.SetTx(new TxSettings { CenterHz = 5_680_000_000, GainDb = 40 });
        var top = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 5000 });
        top.Stream!.Write(iq, 0, 9600);
        var rtop = top.Stop(drain: true);
        Check(moved.State.Tx?.CenterHz == 5_680_000_000 && top.Grant.GainDb == 40 && rtop.Acked,
              $"a move to 5680 MHz by the same service, and its top level (gain 40 = level 0) keys there ({rtop})");
        var outside = Refused(() => s.SetTx(new TxSettings { CenterHz = 2_450_000_000 }));
        Check(outside != null && s.GetState().Tx?.Taken == true, "2450 MHz, inside this sidecar's stated range and outside the service's band, is refused and the radio stays taken: " + outside?.Message);

        var back = s.ReleaseTx();
        Check(back.Tx?.Taken == false && rig.UnitCount == 2 && rig.Unit.Taken("start").Count == 1 && s.GetState().Readiness == RadioStates.Ready,
              "released: the service's input closed, the controller started again, the radio receives");
        ev.Dispose();
    }

    /// <summary>The synthetic radio's own take (<c>--sim-takes-radio</c>): what ShackLink's gate keys through.</summary>
    private static void SimTakesRadio()
    {
        using var log = new SidecarLog(null, console: false);
        var backend = new SimBackend(log, "takes", 2e6) { TakesRadio = true };
        var server = new UsrpServer(backend, new UsrpServerOptions { Port = 0, SidecarVersion = "test", LingerSeconds = 60 });
        server.Start();
        backend.Start();
        try
        {
            using var s = Connect(server, "sim-takes");
            s.WaitReady(5000);
            var tx = s.Caps.Tx;
            Check(tx != null && tx.TakesRadio && tx.TakeMs == SimBackend.TakeMs && tx.GiveMs == SimBackend.GiveMs && s.GetState().Tx?.Taken == false &&
                  s.GetState().Tx!.CenterHz == 0, "the synthetic radio states that its transmitter takes it, and that it is not taken");
            double rate = tx!.RatesHz.Min();
            s.ClaimTx();
            s.SetTx(new TxSettings { GainDb = 40, RateHz = rate });
            var early = Refused(() => s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 5000 }));
            Check(early != null && s.GetState().Tx?.Taken == false, "a key-up on it before the take is refused: " + early?.Message);
            var sw = Stopwatch.StartNew();
            var took = s.SetTx(new TxSettings { CenterHz = 100_400_000 });
            Check(took.State.Tx?.Taken == true && took.State.Tx.CenterHz == 100_400_000 && sw.ElapsedMilliseconds >= SimBackend.TakeMs - 20,
                  $"set_tx's centre takes it, and costs what it says ({sw.ElapsedMilliseconds} ms)");
            sw.Restart();
            s.SetTx(new TxSettings { CenterHz = 100_500_000 });
            Check(sw.ElapsedMilliseconds < SimBackend.TakeMs - 50 && s.GetState().Tx?.Taken == true, $"a move of a taken radio is not a second take ({sw.ElapsedMilliseconds} ms)");
            var t = s.StartTx(new TxStartRequest { Source = TxSourceKinds.Iq, MaxMs = 10_000 });
            var iq = new short[2 * 50_000];
            t.Stream!.Write(iq, 0, 50_000);
            var r = t.Stop(drain: true);
            Check(r.Acked && r.Samples == 50_000 && s.GetState().Tx?.Taken == true, $"it keys while taken, and stays taken after the key-down ({r})");
            var back = s.ReleaseTx();
            Check(back.Tx?.Taken == false && back.Tx.CenterHz == 0, "release_tx gives it back");

            using var watcher = Connect(server, "sim-watch");
            var owner = Connect(server, "sim-owner");
            owner.ClaimTx();
            owner.SetTx(new TxSettings { CenterHz = 100_400_000 });
            Check(watcher.GetState().Tx?.Taken == true, "taken again by another client");
            owner.Dispose();
            Check(WaitFor(() => watcher.GetState().Tx?.Taken == false && watcher.GetState().Tx?.Owner == null, 4000), "...whose disconnect gives it back");
        }
        finally
        {
            server.Dispose();
            backend.Dispose();
        }
    }
}
