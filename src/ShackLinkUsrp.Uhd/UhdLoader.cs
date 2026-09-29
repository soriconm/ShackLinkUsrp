// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ShackLinkUsrp.Uhd;

/// <summary>How uhd.dll itself is loaded. Its dependencies (boost_*.dll, libusb-1.0.dll) are
/// resolved by BASE NAME through the loader's search, so the mechanism decides which copies of
/// them this process gets. <see cref="AlteredSearchPath"/> is the default: LoadLibraryEx with
/// LOAD_WITH_ALTERED_SEARCH_PATH starts the dependency search in uhd.dll's own directory, which is
/// exactly "the set that was built together".</summary>
public enum UhdLoadMechanism
{
    AlteredSearchPath,
    NativeLibraryLoad,
    SetDllDirectory,
}

/// <summary>Where to find UHD and what environment it must see before it loads.</summary>
public sealed record UhdLoadOptions
{
    /// <summary>A directory holding uhd.dll (the <c>--uhd</c> option). Null = the Ettus install,
    /// then PATH as a last resort that is said out loud.</summary>
    public string? UhdDirectory { get; init; }

    /// <summary>Sets UHD_B200_IGNORE_CODEC_LOOPBACK=1 - honoured ONLY by the patched diagnostic
    /// build (<c>patches/uhd-4.8-b210-codec-loopback.patch</c>), and only meaningful for a board
    /// with the known RX D8 fault. The stock UHD ignores it and refuses such a board, as it should.</summary>
    public bool AllowKnownCodecFault { get; init; }

    /// <summary>UHD_IMAGES_DIR. Null = keep an inherited value, else the Ettus install's images.</summary>
    public string? ImagesDirectory { get; init; }

    public UhdLoadMechanism Mechanism { get; init; } = UhdLoadMechanism.AlteredSearchPath;
}

/// <summary>
/// Loads exactly one uhd.dll into this process and routes every <c>[DllImport("uhd")]</c> in this
/// assembly to it. The sidecar's own process: a process-wide choice (SetDllDirectory, the
/// environment) is acceptable here in a way it never was inside ShackLinkServer.
/// </summary>
public static class UhdLoader
{
    private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectoryW(string? lpPathName);

    // UHD reads its switches with C getenv(). The UCRT copies the environment into its own table
    // at start-up and does not see a later Win32 SetEnvironmentVariable - the failure recorded in
    // ShackLink's docs/soapy_sdr_support.md §19 ("No devices found for driver: uhd"). uhd.dll is
    // an MSVC build on the shared ucrtbase.dll, so _putenv_s here writes the table it will read.
    [DllImport("ucrtbase.dll", EntryPoint = "_putenv_s", CallingConvention = CallingConvention.Cdecl,
               CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int CrtPutEnv(string name, string value);

    [DllImport("ucrtbase.dll", EntryPoint = "getenv", CallingConvention = CallingConvention.Cdecl,
               CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr CrtGetEnv(string name);

    private static readonly object Gate = new();
    private static IntPtr _handle;
    private static bool _resolverSet;

    public static bool IsLoaded => _handle != IntPtr.Zero;

    /// <summary>The full path of the uhd.dll this process loaded.</summary>
    public static string LoadedPath { get; private set; } = "";

    /// <summary>uhd_get_version_string, e.g. "4.8.0.HEAD-0-g…".</summary>
    public static string Version { get; private set; } = "";

    /// <summary>uhd_get_abi_string.</summary>
    public static string Abi { get; private set; } = "";

    /// <summary>Where the dependencies UHD pulled in actually came from, read from the process's
    /// module list after the load - evidence, not an assumption about search order.</summary>
    public static IReadOnlyList<string> DependencyPaths { get; private set; } = Array.Empty<string>();

    public static void Load(UhdLoadOptions options, Action<string>? log = null)
    {
        log ??= _ => { };
        lock (Gate)
        {
            string path = ResolveDll(options.UhdDirectory, log);
            if (IsLoaded)
            {
                if (!string.Equals(Path.GetFullPath(path), LoadedPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"uhd.dll is already loaded from {LoadedPath}; a process can hold only one UHD, so {path} cannot be loaded as well.");
                return;
            }

            // Environment FIRST: UHD may read it during static initialisation or its first find.
            if (options.AllowKnownCodecFault)
                SetEnvironment("UHD_B200_IGNORE_CODEC_LOOPBACK", "1", log);

            string? images = options.ImagesDirectory;
            if (string.IsNullOrWhiteSpace(images))
            {
                string inherited = Environment.GetEnvironmentVariable("UHD_IMAGES_DIR") ?? "";
                if (inherited.Length > 0)
                {
                    log($"UHD_IMAGES_DIR inherited: {inherited}");
                }
                else
                {
                    string pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                             "UHD", "share", "uhd", "images");
                    if (Directory.Exists(pf)) images = pf;
                    else log("UHD_IMAGES_DIR unset and no Ettus images directory found; a cold board cannot be given its firmware or bitstream.");
                }
            }
            if (!string.IsNullOrWhiteSpace(images))
            {
                if (!Directory.Exists(images))
                    throw new DirectoryNotFoundException($"The UHD images directory '{images}' does not exist.");
                SetEnvironment("UHD_IMAGES_DIR", Path.GetFullPath(images), log);
            }

            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full)!;
            IntPtr h;
            switch (options.Mechanism)
            {
                case UhdLoadMechanism.NativeLibraryLoad:
                    h = NativeLibrary.Load(full);
                    break;
                case UhdLoadMechanism.SetDllDirectory:
                    if (!SetDllDirectoryW(dir))
                        throw new InvalidOperationException($"SetDllDirectory('{dir}') failed (win32 {Marshal.GetLastWin32Error()}).");
                    h = LoadLibraryW(full);
                    if (h == IntPtr.Zero)
                        throw new DllNotFoundException($"LoadLibrary('{full}') failed (win32 {Marshal.GetLastWin32Error()}).");
                    break;
                default:
                    h = LoadLibraryExW(full, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
                    if (h == IntPtr.Zero)
                        throw new DllNotFoundException(
                            $"LoadLibraryEx('{full}', LOAD_WITH_ALTERED_SEARCH_PATH) failed (win32 {Marshal.GetLastWin32Error()}) - " +
                            "a dependency (boost_*.dll, libusb-1.0.dll) is missing from that directory or from the system.");
                    break;
            }

            _handle = h;
            LoadedPath = full;
            if (!_resolverSet)
            {
                NativeLibrary.SetDllImportResolver(typeof(UhdLoader).Assembly, Resolve);
                _resolverSet = true;
            }

            string layout = UhdNative.LayoutProblem();
            if (layout.Length > 0)
                throw new InvalidOperationException("The UHD C API binding does not match the header layout: " + layout);

            Version = ReadVersion(isAbi: false);
            Abi = ReadVersion(isAbi: true);
            DependencyPaths = ReadDependencyPaths();
            log($"UHD {Version} (ABI {Abi}) loaded from {LoadedPath} via {options.Mechanism}");
            foreach (string dep in DependencyPaths) log($"  dependency: {dep}");
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (string.Equals(libraryName, UhdNative.Lib, StringComparison.OrdinalIgnoreCase))
        {
            if (_handle == IntPtr.Zero)
                throw new DllNotFoundException("uhd.dll has not been loaded - call UhdLoader.Load first.");
            return _handle;
        }
        return IntPtr.Zero; // everything else (kernel32, ucrtbase) takes the default search
    }

    private static string ResolveDll(string? explicitDir, Action<string> log)
    {
        if (!string.IsNullOrWhiteSpace(explicitDir))
        {
            string p = Directory.Exists(explicitDir) ? Path.Combine(explicitDir, "uhd.dll") : explicitDir;
            if (!File.Exists(p))
                throw new FileNotFoundException($"No uhd.dll at '{p}' (the --uhd directory).", p);
            return p;
        }

        string ettus = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UHD", "bin", "uhd.dll");
        if (File.Exists(ettus)) return ettus;

        foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string p;
            try { p = Path.Combine(d.Trim().Trim('"'), "uhd.dll"); } catch { continue; }
            if (File.Exists(p))
            {
                log($"No Ettus UHD install at {Path.GetDirectoryName(ettus)}; falling back to the uhd.dll found on PATH: {p}");
                return p;
            }
        }
        throw new FileNotFoundException(
            "UHD is not installed: no uhd.dll in C:\\Program Files\\UHD\\bin or on PATH. Install the Ettus UHD " +
            "package (it also installs the USB driver), or pass --uhd <directory>.");
    }

    private static void SetEnvironment(string name, string value, Action<string> log)
    {
        Environment.SetEnvironmentVariable(name, value);
        int rc = CrtPutEnv(name, value);
        if (rc != 0)
            throw new InvalidOperationException($"The C runtime refused {name} (_putenv_s error {rc}).");
        string seen = Marshal.PtrToStringAnsi(CrtGetEnv(name)) ?? "<unset>";
        log($"env {name}={value} (C runtime reads: {seen})");
    }

    private static unsafe string ReadVersion(bool isAbi)
    {
        string s = UhdNative.ReadString((buf, len) => isAbi
            ? UhdNative.uhd_get_abi_string((byte*)buf, len)
            : UhdNative.uhd_get_version_string((byte*)buf, len), out UhdError e);
        return e == UhdError.None ? s : $"<{e}>";
    }

    private static IReadOnlyList<string> ReadDependencyPaths()
    {
        var list = new List<string>();
        try
        {
            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
            {
                string n = m.ModuleName ?? "";
                if (n.StartsWith("boost_", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("libusb", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("uhd.dll", StringComparison.OrdinalIgnoreCase))
                    list.Add(m.FileName ?? n);
            }
        }
        catch (Exception ex)
        {
            list.Add("(module list unreadable: " + ex.Message + ")");
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }
}
