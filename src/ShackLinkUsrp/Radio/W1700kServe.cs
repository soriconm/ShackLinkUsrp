// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Radio;

/// <summary>A request <c>w1700k serve</c> answered <c>ok:false</c>: its words, as the unit's controller gave them, and its stable
/// <c>reason</c> where it has one (sdrw1700k-cb, 5a88a04: <c>rate_exceeds_link</c>, <c>router_busy</c>, <c>host_key_untrusted</c>).</summary>
internal sealed class W1700kServeException : Exception
{
    public W1700kServeException(string message, string reason = "") : base(message) => Reason = reason ?? "";

    /// <summary>"" = the controller gave none (any other refusal, or a build from before 5a88a04).</summary>
    public string Reason { get; }

    /// <summary>
    /// The refusal as this sidecar says it, at open and to a request alike: the reason IN BRACKETS before the controller's words,
    /// <c>the unit refused (rate_exceeds_link): ...</c>. A host classifies a fault by its text (ShackLink's
    /// <c>UsrpKind.PermanentPhrases</c>), so the bracketed token is the stable part and the words are the operator's.
    /// </summary>
    public string Said => Reason.Length > 0 ? $"the unit refused ({Reason}): {Message}" : "the unit refused: " + Message;
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

    /// <summary>The default: the bundled <c>w1700k.exe</c> beside this executable (C:\Dev\SDRW1700K docs/07; the WSL checkout was the
    /// default until 2026-10-21). Its ONE owner is the protocol library, which a host reads to judge or say an empty launch profile
    /// (ShackLink Device Setup).</summary>
    public static string DefaultCommand => ShackLinkUsrp.Client.W1700kDefaults.ServeCommandIn(AppContext.BaseDirectory);

    /// <summary>
    /// The bundled controller's <c>--version</c> (one JSON line, no unit contact; ~110 ms on the station that measured it): what it
    /// printed, or null when <paramref name="exe"/> is not there; anything else that stops it answering throws, in words. Its input is
    /// closed at once (it reads none), no window, a hard cap - the doctor runner's rules (ShackLink docs/device_setup_support.md W15).
    /// Asked only to learn whether it has a transmit service (ShackLink docs/w1700k_support.md D4).
    /// </summary>
    public static string? ReadVersion(string exe, int timeoutMs)
    {
        if (!File.Exists(exe)) return null;
        var psi = new ProcessStartInfo(exe, "--version")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} --version did not start");
        try { p.StandardInput.Close(); } catch { }
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(Math.Max(1, timeoutMs)))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"it did not answer within {timeoutMs} ms");
        }
        p.WaitForExit();
        string text = output.Wait(2000) ? output.Result : "";
        if (p.ExitCode != 0 && text.Trim().Length == 0)
            throw new InvalidOperationException($"it exited {p.ExitCode}" + (errors.Wait(500) && errors.Result.Trim().Length > 0 ? ": " + errors.Result.Trim() : ""));
        return text;
    }

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
        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new InvalidOperationException($"\"{commandLine}\" did not start");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3)
        {
            // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND. .NET's own sentence names the working directory, which the operator did
            // not choose and which is not where the controller was looked for.
            throw new FileNotFoundException(NotInstalled(exe, AppContext.BaseDirectory), exe, ex);
        }
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

    /// <summary>The sentence a controller that is not there is refused with. ShackLink never retries it
    /// (<c>UsrpKind.PermanentPhrases</c>: <c>w1700k controller is not installed</c>) and adds where to set another one, so a change
    /// to its first clause is a change there. The bundled default is said as such: a sidecar run from a build folder has no
    /// <c>..\W1700K\</c> beside it unless one was put there.</summary>
    internal static string NotInstalled(string exe, string helperDirectory)
    {
        string bundled = ShackLinkUsrp.Client.W1700kDefaults.ControllerPathFor(helperDirectory);
        bool isBundled;
        try { isBundled = string.Equals(Path.GetFullPath(exe), bundled, StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { isBundled = false; }
        return isBundled
            ? $"the W1700K controller is not installed: there is no {Path.GetFileName(bundled)} in {Path.GetDirectoryName(bundled)}, " +
              "where the bundled controller goes (a sibling of this helper's folder)"
            : $"the W1700K controller is not installed: {exe} was not found";
    }

    /// <summary>The first word of <paramref name="commandLine"/> (quoted or not) and the rest, verbatim: the protocol library's
    /// split, so a host judging a controller splits it as this one starts it.</summary>
    internal static (string Exe, string Args) SplitCommand(string commandLine)
    {
        ShackLinkUsrp.Client.W1700kDefaults.SplitCommand(commandLine, out string exe, out string args);
        return (exe, args);
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
            throw new W1700kServeException(reply.GetString("error", "") is { Length: > 0 } e ? e : $"{request.GetString("op")} failed, with no reason given",
                                           reply.GetString("reason", "") ?? "");
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
