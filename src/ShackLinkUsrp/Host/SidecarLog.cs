// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;

namespace ShackLinkUsrp.Host;

/// <summary>
/// The sidecar's log: a file per radio key (%LocalAppData%\ShackLinkUsrp\logs\&lt;key&gt;.log, the
/// path the client library quotes when a spawned sidecar dies before answering), the last lines in
/// memory for <c>get_log</c>, and optionally the console. The previous run's file is kept as .1.
/// </summary>
internal sealed class SidecarLog : IDisposable
{
    private const int KeepLines = 4000;
    private const long RollBytes = 8L * 1024 * 1024;
    private readonly object _lock = new();
    private readonly Queue<string> _tail = new();
    private readonly bool _console;
    private StreamWriter? _file;
    private readonly string? _path;

    public SidecarLog(string? path, bool console)
    {
        _console = console;
        _path = path;
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
            {
                string prev = path + ".1";
                File.Copy(path, prev, overwrite: true);
            }
            _file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
                                     new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            _file = null;
            if (_console) Console.Error.WriteLine($"log file unavailable ({path}): {ex.Message}");
        }
    }

    public void Write(string message)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + message;
        lock (_lock)
        {
            _tail.Enqueue(line);
            while (_tail.Count > KeepLines) _tail.Dequeue();
            try
            {
                if (_file != null)
                {
                    _file.WriteLine(line);
                    if (_file.BaseStream.Length > RollBytes)
                    {
                        _file.Dispose();
                        File.Copy(_path!, _path + ".1", overwrite: true);
                        _file = new StreamWriter(new FileStream(_path!, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
                                                 new UTF8Encoding(false)) { AutoFlush = true };
                    }
                }
            }
            catch
            {
                // A log that cannot be written must not take the radio down with it.
            }
        }
        if (_console) Console.WriteLine(line);
    }

    public string[] Tail(int lines)
    {
        lock (_lock)
        {
            int skip = Math.Max(0, _tail.Count - lines);
            return _tail.Skip(skip).ToArray();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _file?.Dispose(); } catch { }
            _file = null;
        }
    }
}
