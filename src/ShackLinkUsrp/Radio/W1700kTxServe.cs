// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Radio;

/// <summary>A request the W1700K transmit service answered <c>ok:false</c>: its words, and its stable <c>reason</c> token
/// (ShackLink docs/w1700k_support.md "Transmit", the service wire). Retries and buttons are decided on the token, never the words.</summary>
internal sealed class W1700kTxRefusal : Exception
{
    public W1700kTxRefusal(string message, string reason, JsonObject? reply = null) : base(message)
    {
        Reason = reason ?? "";
        Reply = reply;
    }

    /// <summary>"" = the service gave none.</summary>
    public string Reason { get; }

    /// <summary>The whole reply: a refusal may carry numbers (<c>linear_max_level_db</c>, <c>retry_after_ms</c>).</summary>
    public JsonObject? Reply { get; }

    /// <summary>As this sidecar says it: the reason IN BRACKETS before the service's words, the shape
    /// <see cref="W1700kServeException.Said"/> has, so a host classifies both the same way.</summary>
    public string Said => Reason.Length > 0 ? $"the unit's transmitter refused ({Reason}): {Message}" : "the unit's transmitter refused: " + Message;
}

/// <summary>One band the transmit service keys in, as its <c>caps.bands[]</c> states it.</summary>
internal sealed class W1700kTxBand
{
    public string Name = "";
    public int Band;
    public long CenterMinHz, CenterMaxHz;
    public double LevelMinDb, LevelMaxDb, LevelStepDb;
    /// <summary>The highest level an arbitrary envelope stays linear at. NaN = not stated.</summary>
    public double LinearMaxLevelDb = double.NaN;
    /// <summary>How long the carrier settles after a preparation that RELOADED the unit (<c>costs.settle.load.settle_ms</c>,
    /// keyed by this band's name). -1 = not stated.</summary>
    public long LoadSettleMs = -1;
    /// <summary><c>key_down_max_ms</c> on the service's own LEVEL axis, as it states it (ShackLink docs/w1700k_support.md
    /// "TX5-0"): read with <see cref="TxKeyDownLimits.MsAt"/>, the service's rule. Empty = not stated.</summary>
    public TxKeyDownLimit[] KeyDownMaxMs = Array.Empty<TxKeyDownLimit>();
    /// <summary>The list was present and not whole: not stated, and said in the log rather than half read.</summary>
    public bool KeyDownMaxMsUnreadable;
    /// <summary>How many PA indices the band states (<c>pa_indices</c>), and for how many a calibration file of THIS unit is
    /// present (their <c>calibrated</c>). -1 = not stated; the second also where any index does not say.</summary>
    public int PaIndexCount = -1, CalibratedPaIndexCount = -1;
}

/// <summary>
/// The transmit service's <c>caps</c>: EVERY number of this transmitter (ShackLink docs/w1700k_support.md "Transmit" T6). This
/// sidecar holds none of its own - a figure the service does not state is not stated here either, and a caps without the
/// essentials (the input rate, a band with its level range, <c>max_ms</c>, the largest block) is refused in words: no transmitter.
/// </summary>
internal sealed class W1700kTxCapsInfo
{
    public const int WireVersion = 1;

    public int Wire;
    public double InputRateHz;
    public int MaxBlockSamples;
    public int QueueMs, PrebufferMs, StarveMs;
    public long MaxMs;
    public W1700kTxBand[] Bands = Array.Empty<W1700kTxBand>();
    /// <summary>The passbands' names, the service's default FIRST.</summary>
    public string[] Passbands = Array.Empty<string>();
    /// <summary>The first passband's half width: how far from the centre anything sent survives. 0 = not stated.</summary>
    public double PassbandHalfWidthHz;
    public long LoadMs, TuneMs, SetupMs, CloseMs;
    /// <summary>A first preparation on the radio the receiver left LOADED: one band's setup, no reload (the service's path
    /// <c>adopt</c>). 0 = not stated: such a service's first preparation is a load.</summary>
    public long AdoptMs;
    public JsonObject Raw = new();

    public static W1700kTxCapsInfo Parse(JsonObject o)
    {
        var c = new W1700kTxCapsInfo { Raw = o, Wire = (int)o.GetLong("wire", 0) };
        var input = o.GetObject("input") ?? throw new InvalidDataException("the transmit service's caps state no \"input\"");
        c.InputRateHz = input.GetDouble("rate_hz", 0);
        if (!(c.InputRateHz > 0)) throw new InvalidDataException("the transmit service's caps state no input rate (input.rate_hz)");
        string format = input.GetString("format", "") ?? "";
        if (format != "sc16le") throw new InvalidDataException($"the transmit service takes \"{format}\" samples; this sidecar sends sc16le");
        c.MaxBlockSamples = (int)input.GetLong("max_block_samples", 0);
        if (c.MaxBlockSamples < 1) throw new InvalidDataException("the transmit service's caps state no largest block (input.max_block_samples)");
        c.MaxMs = o.GetLong("max_ms", 0);
        if (c.MaxMs < 1) throw new InvalidDataException("the transmit service's caps state no longest key-down (max_ms)");
        c.QueueMs = (int)o.GetLong("queue_ms", 0);
        c.PrebufferMs = (int)o.GetLong("prebuffer_ms", 0);
        c.StarveMs = (int)o.GetLong("starve_ms", 0);

        var bands = new List<W1700kTxBand>();
        foreach (var item in (IEnumerable<object>?)o.GetArray("bands") ?? Array.Empty<object>())
        {
            if (item is not JsonObject b) continue;
            var band = new W1700kTxBand
            {
                Name = b.GetString("name", "") ?? "",
                Band = (int)b.GetLong("band", -1),
                CenterMinHz = b.GetLong("center_min_hz", 0),
                CenterMaxHz = b.GetLong("center_max_hz", 0),
                LevelMinDb = b.GetDouble("level_min_db"),
                LevelMaxDb = b.GetDouble("level_max_db"),
                LevelStepDb = b.GetDouble("level_step_db"),
                LinearMaxLevelDb = b.GetDouble("linear_max_level_db"),
            };
            if (band.CenterMinHz <= 0 || band.CenterMaxHz < band.CenterMinHz)
                throw new InvalidDataException($"the transmit service's band \"{band.Name}\" states no frequency range");
            if (double.IsNaN(band.LevelMinDb) || double.IsNaN(band.LevelMaxDb) || !(band.LevelMaxDb > band.LevelMinDb) || !(band.LevelStepDb > 0))
                throw new InvalidDataException($"the transmit service's band \"{band.Name}\" states no level range and step");
            band.KeyDownMaxMsUnreadable = !TxKeyDownLimits.TryParse(b.GetArray("key_down_max_ms"), out band.KeyDownMaxMs);
            if (b.GetArray("pa_indices") is { } pas)
            {
                var indices = pas.OfType<JsonObject>().ToList();
                band.PaIndexCount = indices.Count;
                // Counted only where EVERY index says: an index that does not is no evidence that this unit is uncalibrated.
                band.CalibratedPaIndexCount = indices.All(p => p["calibrated"] is bool) ? indices.Count(p => p.GetBool("calibrated")) : -1;
            }
            bands.Add(band);
        }
        if (bands.Count == 0) throw new InvalidDataException("the transmit service's caps state no band");
        c.Bands = bands.OrderBy(b => b.CenterMinHz).ToArray();

        var names = new List<string>();
        foreach (var item in (IEnumerable<object>?)o.GetArray("passbands") ?? Array.Empty<object>())
        {
            if (item is not JsonObject p || string.IsNullOrEmpty(p.GetString("name", ""))) continue;
            if (names.Count == 0) c.PassbandHalfWidthHz = Math.Max(0, p.GetDouble("half_width_hz", 0));
            names.Add(p.GetString("name", "")!);
        }
        c.Passbands = names.ToArray();
        // TX5-1: every passband with its width and whether a constant envelope may go through it, and the envelope claims the
        // service honours with its own thresholds - passed through. An envelope object naming no mode states no claim.
        c.PassbandList = TxPassband.ParseAll(o.GetArray("passbands"));
        var env = TxEnvelopeCaps.Parse(o.GetObject("envelope"));
        c.Envelope = env != null && env.Modes.Length > 0 ? env : null;

        var costs = o.GetObject("costs");
        var prepare = costs?.GetObject("prepare");
        c.LoadMs = Math.Max(0, prepare?.GetLong("load_ms", 0) ?? 0);
        c.TuneMs = Math.Max(0, prepare?.GetLong("tune_ms", 0) ?? 0);
        c.SetupMs = Math.Max(0, prepare?.GetLong("setup_ms", 0) ?? 0);
        c.AdoptMs = Math.Max(0, prepare?.GetLong("adopt_ms", 0) ?? 0);
        c.CloseMs = Math.Max(0, costs?.GetLong("close_ms", 0) ?? 0);
        // The settle after a LOAD, per band by name (ShackLink TX4: the operator is told before the take what a reload costs
        // the carrier). Absent, or a band the table does not name, is not stated.
        var loadSettle = costs?.GetObject("settle")?.GetObject("load")?.GetObject("settle_ms");
        if (loadSettle != null)
            foreach (var b in c.Bands)
                if (loadSettle.TryGetLong(b.Name, out long ms) && ms >= 0) b.LoadSettleMs = ms;
        // X2: what the level figures were measured on, and whether a calibration is per unit and shipped - passed through.
        c.Tables = TxTables.Parse(o.GetObject("tables"));
        c.Calibration = TxCalibration.Parse(o.GetObject("calibration"));
        return c;
    }

    /// <summary>The service's <c>tables</c> and <c>calibration</c>, as it states them; null = not stated.</summary>
    public TxTables? Tables;
    public TxCalibration? Calibration;
    /// <summary>The service's passbands in its order, and the envelope claims it honours (null = none stated): TX5-1.</summary>
    public TxPassband[] PassbandList = Array.Empty<TxPassband>();
    public TxEnvelopeCaps? Envelope;

    /// <summary>The longest key-down by level, per band, in the service's own figures - what the log says in place of the
    /// overall <c>max_ms</c>, which is the longest at ANY level and false at a level whose own figure is shorter (TX5-0 K7).</summary>
    public string KeyDownWords() => string.Join("; ", Bands.Select(b =>
        b.KeyDownMaxMs.Length > 0
            ? b.Name + " " + string.Join(", ", b.KeyDownMaxMs.Select(e =>
                $"{(e.Ms / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)} s to {e.TopDb.ToString("0.#", CultureInfo.InvariantCulture)} dB"))
            : $"{b.Name} {(b.KeyDownMaxMsUnreadable ? "unreadable" : "not stated")} by level ({MaxMs} ms at most)"));

    public W1700kTxBand? BandAt(long hz) => Bands.FirstOrDefault(b => hz >= b.CenterMinHz && hz <= b.CenterMaxHz);

    /// <summary>The widest level range of any band: the sidecar wire's gain axis, 0 at a band's lowest level.</summary>
    public double LevelSpanDb => Bands.Max(b => b.LevelMaxDb - b.LevelMinDb);

    public string BandsWords() => string.Join(", ", Bands.Select(b =>
        $"{b.Name} {(b.CenterMinHz / 1e6).ToString("0.###", CultureInfo.InvariantCulture)}-{(b.CenterMaxHz / 1e6).ToString("0.###", CultureInfo.InvariantCulture)} MHz"));
}

/// <summary>
/// The client of the W1700K transmit service (C:\Dev\SDRW1700K <c>tools\w1700k-tx-serve.py</c>; ShackLink
/// docs/w1700k_support.md "Transmit", wire 1) - this backend is its ONE client, and the service is its CHILD: stdin EOF is the
/// transmitter's dead-man, and nothing listens on a port (T2).
/// <para><b>stdin is framed</b>: 1 byte type, a u32 little-endian length, the payload. <c>J</c> = one UTF-8 JSON request;
/// <c>S</c> = sc16 little-endian interleaved I, Q. <b>stdout</b> is one JSON object a line: a reply carries its request's
/// <c>id</c>, an event (<c>keyed</c>, <c>ended</c>) carries <c>event</c>. stderr is this sidecar's log.</para>
/// <para><b>A sample write BLOCKS while the service's queue is full: that is the transmit path's only clock.</b> Requests and
/// samples share the one pipe, so a request waits for the block in flight - at most one block's worth of the queue draining,
/// because the service never leaves its reader blocked while no transmission is open. A stop that is not answered is followed
/// by <see cref="Kill"/>: the service gone IS the unit unkeyed.</para>
/// </summary>
internal sealed class W1700kTxServe : IDisposable
{
    public const byte FrameJson = (byte)'J';
    public const byte FrameSamples = (byte)'S';
    public const int MaxJsonBytes = 64 * 1024;

    private readonly Stream _input;
    private readonly TextReader _output;
    private readonly Action _close;
    private readonly Action _kill;
    private readonly Action<string> _log;
    private readonly object _writeLock = new();
    private readonly Dictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly byte[] _head = new byte[5];
    private Thread? _reader;
    private long _nextId;
    private volatile string _exited = "";
    private volatile bool _disposed;

    /// <summary>A service over any pair of streams: a child process's (<see cref="Spawn"/>), or a gate's fake.
    /// <paramref name="close"/> ends its input (the orderly end); <paramref name="kill"/> ends IT.</summary>
    public W1700kTxServe(Stream input, TextReader output, Action close, Action kill, Action<string> log)
    {
        _input = input;
        _output = output;
        _close = close;
        _kill = kill;
        _log = log;
    }

    /// <summary>Start <paramref name="commandLine"/> with its standard streams redirected, as <see cref="W1700kServe.Spawn"/>
    /// does; its stdin is taken as BYTES.</summary>
    public static W1700kTxServe Spawn(string commandLine, Action<string> log)
    {
        var (exe, args) = W1700kServe.SplitCommand(commandLine);
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new InvalidOperationException($"\"{commandLine}\" did not start");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3)
        {
            throw new FileNotFoundException($"the W1700K transmit service is not installed: {exe} was not found", exe, ex);
        }
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) log("tx service stderr: " + e.Data); };
        p.BeginErrorReadLine();
        var stdin = p.StandardInput.BaseStream;
        void Kill()
        {
            // The PROCESS gone, not the kill sent: the controller started after it must find the router's claim released, and
            // Windows drops a mutex at process exit (ShackLink docs/w1700k_support.md "The built-in transmit service", the claim).
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    if (!p.WaitForExit(3000)) log($"the transmit service (pid {p.Id}) was killed and had not exited 3 s later");
                }
            }
            catch { }
        }
        return new W1700kTxServe(stdin, p.StandardOutput, () =>
        {
            // End of input is the service's orderly end: it unkeys, closes its station and exits.
            try { stdin.Close(); } catch { }
            if (!p.WaitForExit(8000))
            {
                log($"the transmit service (pid {p.Id}) did not exit 8 s after its input closed: killed");
                Kill();
            }
            p.Dispose();
        }, Kill, log);
    }

    /// <summary>A transmission's first RF: the service's <c>keyed</c> event, on the reader thread.</summary>
    public event Action<JsonObject>? Keyed;

    /// <summary>A transmission the service ended on its own: its <c>ended</c> event, on the reader thread.</summary>
    public event Action<JsonObject>? Ended;

    /// <summary>The service ended (its output closed) without this side disposing it: why, in words.</summary>
    public event Action<string>? Exited;

    /// <summary>Why the service ended; "" while it runs.</summary>
    public string ExitedWhy => _exited;

    public void Start()
    {
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "W1700kTxServe" };
        _reader.Start();
    }

    /// <summary>One request and its reply. <c>ok:false</c> throws <see cref="W1700kTxRefusal"/> with the service's token and
    /// words; no reply in <paramref name="timeoutMs"/> throws <see cref="TimeoutException"/>.</summary>
    public JsonObject Request(JsonObject request, int timeoutMs)
    {
        if (_exited.Length > 0) throw new IOException("the transmit service ended: " + _exited);
        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;
        request.Set("id", id);
        byte[] body = Encoding.UTF8.GetBytes(Json.Serialize(request));
        if (body.Length > MaxJsonBytes) throw new ArgumentException($"a request of {body.Length} bytes is over the wire's {MaxJsonBytes}");
        try
        {
            WriteFrame(FrameJson, body, 0, body.Length);
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            lock (_pending) _pending.Remove(id);
            throw new IOException($"the transmit service took no request ({ex.Message})");
        }
        string op = request.GetString("op") ?? "";
        if (!tcs.Task.Wait(timeoutMs))
        {
            lock (_pending) _pending.Remove(id);
            throw new TimeoutException($"the transmit service did not answer {op} within {(timeoutMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)} s");
        }
        var reply = tcs.Task.Result;
        if (!reply.GetBool("ok"))
            throw new W1700kTxRefusal(reply.GetString("error", "") is { Length: > 0 } e ? e : $"{op} failed, with no reason given",
                                      reply.GetString("reason", "") ?? "", reply);
        return reply;
    }

    /// <summary>Send <paramref name="byteCount"/> bytes of sc16 (a multiple of 4). BLOCKS while the service's queue is full.
    /// Throws <see cref="IOException"/> once the service is gone.</summary>
    public void WriteSamples(byte[] sc16, int offset, int byteCount)
    {
        if (byteCount <= 0) return;
        if ((byteCount & 3) != 0) throw new ArgumentException("a sample frame is whole sc16 pairs: a multiple of 4 bytes", nameof(byteCount));
        if (_exited.Length > 0) throw new IOException("the transmit service ended: " + _exited);
        try
        {
            WriteFrame(FrameSamples, sc16, offset, byteCount);
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("the transmit service is closed", ex);
        }
    }

    private void WriteFrame(byte type, byte[] payload, int offset, int count)
    {
        lock (_writeLock)
        {
            _head[0] = type;
            BinaryPrimitives.WriteUInt32LittleEndian(_head.AsSpan(1), (uint)count);
            _input.Write(_head, 0, 5);
            _input.Write(payload, offset, count);
            _input.Flush();
        }
    }

    private void ReadLoop()
    {
        string why = "its output ended";
        try
        {
            string? line;
            while ((line = _output.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                JsonObject o;
                try
                {
                    o = Json.ParseObject(line);
                }
                catch (JsonParseException)
                {
                    _log("tx service: " + line);     // not the protocol's (an interpreter's banner): kept, said as is
                    continue;
                }
                Dispatch(o);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            why = "its output failed: " + ex.Message;
        }
        _exited = why;
        List<TaskCompletionSource<JsonObject>> waiting;
        lock (_pending)
        {
            waiting = _pending.Values.ToList();
            _pending.Clear();
        }
        foreach (var t in waiting) t.TrySetException(new IOException("the transmit service ended: " + why));
        if (!_disposed) Exited?.Invoke(why);
    }

    private void Dispatch(JsonObject o)
    {
        if (o.TryGetLong("id", out long id))
        {
            TaskCompletionSource<JsonObject>? t;
            lock (_pending) _pending.Remove(id, out t);
            t?.TrySetResult(o);
            return;
        }
        switch (o.GetString("event"))
        {
            case "keyed":
                Keyed?.Invoke(o);
                break;
            case "ended":
                Ended?.Invoke(o);
                break;
            case "log":
                _log($"tx service {o.GetString("level", "info")}: {o.GetString("text", "")}");
                break;
        }
    }

    /// <summary>End the service NOW, with no orderly close: the dead-man. The unit unkeys by itself once its feeder's link
    /// is gone (SDRW1700K: within 0.3 s, measured).</summary>
    public void Kill()
    {
        _disposed = true;
        try { _kill(); } catch { }
    }

    /// <summary>The orderly end: the service's input closes, it unkeys, closes its station and exits.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _close(); } catch { }
        if (_reader != null && Thread.CurrentThread != _reader) _reader.Join(2000);
    }
}
