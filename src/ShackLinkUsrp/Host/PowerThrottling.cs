// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace ShackLinkUsrp.Host;

/// <summary>
/// The sidecar opts out of Windows power throttling (EcoQoS) for itself (owner, 2026-10-21; ShackLink
/// docs/usrp_b210_support.md section 9.33). It is a windowless background process carrying a real-time stream, which is
/// exactly what Windows may decide to run slowly: identical runs of 16 leases measured up to 3.3x apart in CPU time, the
/// ingest thread's fixed work swinging with them, and switching throttling off from outside narrowed it. A radio's stream
/// does not wait, so a slowed process loses samples, not time.
/// </summary>
internal static class PowerThrottling
{
    private const int ProcessPowerThrottlingClass = 4;                 // PROCESS_INFORMATION_CLASS.ProcessPowerThrottling
    internal const uint CurrentVersion = 1;                            // PROCESS_POWER_THROTTLING_CURRENT_VERSION
    internal const uint ExecutionSpeed = 0x1;                          // PROCESS_POWER_THROTTLING_EXECUTION_SPEED

    [StructLayout(LayoutKind.Sequential)]
    internal struct State
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref State info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessInformation(IntPtr process, int infoClass, ref State info, int size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>Switch execution-speed throttling OFF for this process: the control bit set, the state bit clear. Returns the
    /// sentence for the log ("" off Windows, where there is nothing to switch).</summary>
    public static string OptOut()
    {
        if (!OperatingSystem.IsWindows()) return "";
        var s = new State { Version = CurrentVersion, ControlMask = ExecutionSpeed, StateMask = 0 };
        if (SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottlingClass, ref s, Marshal.SizeOf<State>()))
            return "power throttling: off for this process (Windows may otherwise run a windowless process slowly, and a radio's stream does not wait)";
        return $"power throttling: could not be switched off (Win32 error {Marshal.GetLastWin32Error()}); the stream may be slowed when Windows throttles this process";
    }

    /// <summary>A process's throttling state as Windows holds it, or null where it cannot be read: how the gate proves a
    /// spawned sidecar opted out.</summary>
    public static State? Read(IntPtr process)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var s = new State { Version = CurrentVersion };
        return GetProcessInformation(process, ProcessPowerThrottlingClass, ref s, Marshal.SizeOf<State>()) ? s : null;
    }

    /// <summary>True when <paramref name="s"/> says execution-speed throttling is explicitly OFF.</summary>
    public static bool IsOptedOut(State s) => (s.ControlMask & ExecutionSpeed) != 0 && (s.StateMask & ExecutionSpeed) == 0;
}
