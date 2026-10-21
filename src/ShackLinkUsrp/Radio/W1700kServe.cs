// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Radio;

/// <summary>A request <c>w1700k serve</c> answered <c>ok:false</c>: its words, as the unit's controller gave them.</summary>
internal sealed class W1700kServeException : Exception
{
    public W1700kServeException(string message) : base(message) { }
}

/// <summary>
/// The client of upstream's control protocol, <c>w1700k serve</c> (h0m3us3r/w1700k-sdr <c>host/w1700k/README.md</c>, MIT): one
/// JSON object per line each way. Every request gets ONE reply - <c>{"id":N,"ok":true,"state":STATE}</c> or
/// <c>{"id":N,"ok":false,"error":"..."}</c> - and events without an id arrive in between (<c>state</c>, <c>progress</c>,
/// <c>log</c>). <c>status</c> is answered at once from the controller's snapshot; everything else runs one at a time, in order,
/// AFTER the controller has attached to the unit. This sidecar keeps the controller as upstream wrote it (C:\Dev\SDRW1700K
/// docs/03 section 6, phase 1): the firmware load, the WM mailbox and the RF programs stay upstream's.
/// </summary>
internal sealed class W1700kServe : IDisposable
{
    private readonly TextWriter _input;
    private readonly TextReader _output;
    private readonly Action _close;
    private readonly Action<string> _log;
    private readonly object _writeLock = new();
    private readonly object _stateLock = new();
    private readonly Dictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly ManualResetEventSlim _attached = new(false);
    private Thread? _reader;
    private long _nextId;
    private JsonObject? _state;
    private string _attachError = "";
    private volatile string _exited = "";
    private volatile bool _disposed;

    /// <summary>A controller over any pair of text streams: a child process's (<see cref="Spawn"/>), or a gate's fake.</summary>
    public W1700kServe(TextWriter input, TextReader output, Action close, Action<string> log)
    {
        _input = input;
        _output = output;
        _close = close;
        _log = log;
    }

    /// <summary>The default: upstream's checkout in WSL, as it was built and installed (C:\Dev\SDRW1700K docs/05 option 2).</summary>
    public const string DefaultCommand = "wsl.exe -d Ubuntu-24.04 -- bash -lc \"cd ~/w1700k-sdr && exec host/bin/w1700k serve\"";

    /// <summary>Start <paramref name="commandLine"/> (its first word the program, the rest its arguments, as a shell would
    /// split them) with its standard streams redirected. Its stderr is the sidecar's log.</summary>
    public static W1700kServe Spawn(string commandLine, Action<string> log)
    {
        var (exe, args) = SplitCommand(commandLine);
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"\"{commandLine}\" did not start");
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) log("serve stderr: " + e.Data); };
        p.BeginErrorReadLine();
        p.StandardInput.AutoFlush = true;
        p.StandardInput.NewLine = "\n";
        return new W1700kServe(p.StandardInput, p.StandardOutput, () =>
        {
            // End of input is serve's own end (its "for line in stdin" loop); it stops nothing on the unit by itself.
            try { p.StandardInput.Close(); } catch { }
            if (!p.WaitForExit(5000))
            {
                log($"serve (pid {p.Id}) did not exit 5 s after its input closed: killed");
                try { p.Kill(entireProcessTree: true); } catch { }
            }
            p.Dispose();
        }, log);
    }

    /// <summary>The first word of <paramref name="commandLine"/> (quoted or not) and the rest, verbatim.</summary>
    internal static (string Exe, string Args) SplitCommand(string commandLine)
    {
        string s = (commandLine ?? "").Trim();
        if (s.Length == 0) throw new ArgumentException("an empty serve command");
        if (s[0] == '"')
        {
            int close = s.IndexOf('"', 1);
            if (close < 0) throw new ArgumentException($"an unclosed quote in the serve command: {s}");
            return (s.Substring(1, close - 1), s.Substring(close + 1).TrimStart());
        }
        int space = s.IndexOf(' ');
        return space < 0 ? (s, "") : (s.Substring(0, space), s.Substring(space + 1).TrimStart());
    }

    /// <summary>Raised on every <c>state</c> event and every reply's state, on the reader thread.</summary>
    public event Action<JsonObject>? StateChanged;

    /// <summary>The controller ended (its output closed): why, in words.</summary>
    public event Action<string>? Exited;

    /// <summary>The last STATE the controller sent; null before any.</summary>
    public JsonObject? State
    {
        get { lock (_stateLock) return _state; }
    }

    /// <summary>Why the controller ended; "" while it runs.</summary>
    public string ExitedWhy => _exited;

    public void Start()
    {
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "W1700kServe" };
        _reader.Start();
    }

    /// <summary>
    /// Wait until the controller has ATTACHED to the unit - read its state for this boot and noticed a stream already running.
    /// A <c>status</c> reply cannot say so (it is the snapshot from before the attach); the attach's end is its first
    /// <c>state</c> event, or the log line saying the unit could not be reached. Throws with the controller's words.
    /// </summary>
    public JsonObject WaitAttached(int timeoutMs)
    {
        if (!_attached.Wait(timeoutMs))
            throw new TimeoutException($"w1700k serve did not reach the unit within {timeoutMs / 1000} s");
        if (_exited.Length > 0) throw new IOException("w1700k serve ended: " + _exited);
        if (_attachError.Length > 0) throw new W1700kServeException(_attachError);
        return State ?? throw new IOException("w1700k serve attached and stated nothing");
    }

    /// <summary>One request and its reply's STATE. <c>ok:false</c> throws <see cref="W1700kServeException"/> in the controller's
    /// words; no reply in <paramref name="timeoutMs"/> throws <see cref="TimeoutException"/>.</summary>
    public JsonObject Request(JsonObject request, int timeoutMs)
    {
        if (_exited.Length > 0) throw new IOException("w1700k serve ended: " + _exited);
        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;
        request.Set("id", id);
        string line = Json.Serialize(request);
        try
        {
            lock (_writeLock)
            {
                _input.Write(line + "\n");
                _input.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            lock (_pending) _pending.Remove(id);
            throw new IOException($"w1700k serve took no request ({ex.Message})");
        }
        if (!tcs.Task.Wait(timeoutMs))
        {
            lock (_pending) _pending.Remove(id);
            throw new TimeoutException($"w1700k serve did not answer {request.GetString("op")} within {(timeoutMs / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s");
        }
        var reply = tcs.Task.Result;
        if (!reply.GetBool("ok"))
            throw new W1700kServeException(reply.GetString("error", "") is { Length: > 0 } e ? e : $"{request.GetString("op")} failed, with no reason given");
        return reply.GetObject("state") ?? State ?? new JsonObject();
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
                    _log("serve: " + line);     // a line that is not the protocol's (a shell's banner): kept, said as is
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
        _attached.Set();
        List<TaskCompletionSource<JsonObject>> waiting;
        lock (_pending)
        {
            waiting = _pending.Values.ToList();
            _pending.Clear();
        }
        foreach (var t in waiting) t.TrySetException(new IOException("w1700k serve ended: " + why));
        if (!_disposed) Exited?.Invoke(why);
    }

    private void Dispatch(JsonObject o)
    {
        if (o.TryGetLong("id", out long id))
        {
            if (o.GetObject("state") is { } st) TakeState(st);
            TaskCompletionSource<JsonObject>? t;
            lock (_pending) _pending.Remove(id, out t);
            t?.TrySetResult(o);
            return;
        }
        switch (o.GetString("event"))
        {
            case "state":
                if (o.GetObject("state") is { } s)
                {
                    TakeState(s);
                    _attached.Set();
                }
                break;
            case "log":
                string level = o.GetString("level", "info");
                string text = o.GetString("text", "");
                _log($"unit {level}: {text}");
                // The worker's attach failed: it logs this and goes on serving, every later request failing the same way.
                if (!_attached.IsSet && text.StartsWith("cannot reach the unit", StringComparison.Ordinal))
                {
                    _attachError = text;
                    _attached.Set();
                }
                break;
            case "progress":
                _log($"unit: {o.GetString("text", "")} ({o.GetDouble("fraction", 0) * 100:0}%)");
                break;
        }
    }

    private void TakeState(JsonObject s)
    {
        lock (_stateLock) _state = s;
        StateChanged?.Invoke(s);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _close(); } catch { }
        _reader?.Join(2000);
    }
}
