// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace ShackLinkUsrp.Radio;

/// <summary>
/// ONE UHD finder at a time across every ShackLinkUsrp process on this machine (ShackLink
/// docs/usrp_b210_support.md 9.26 D3). A B2xx find claims each board's control interface in turn and
/// loads firmware into a cold one; a find running beside another process's open of that same board
/// can take its interface out from under it, or load its firmware twice, and that open fails. So the
/// catalogue's background find (<c>--list</c>) and a radio's open (find + make) hold this lock.
///
/// <para>A Windows named mutex, <c>Local\</c> (this logon session - where the operator's server and
/// every sidecar it spawns run). It is owned by a THREAD: the acquiring thread must release it, which
/// both holders do by acquiring and releasing around synchronous code. A holder that died without
/// releasing leaves it abandoned, which counts as acquired. A wait that runs out goes ahead WITHOUT the
/// lock and says so: two boards opening at once is a race worth one retry, a sidecar that never opens
/// because some other process hung is worse.</para>
/// </summary>
internal static class UhdDeviceOpsLock
{
    public const string MutexName = @"Local\ShackLinkUsrp.UhdDeviceOps";

    /// <summary>Long enough for another board's cold open (firmware, a ~5 MB bitstream, the CODEC init:
    /// ~16 s measured, section 1.1) with room to spare.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(60);

    public static IDisposable Acquire(TimeSpan wait, Action<string>? log)
    {
        var mutex = new Mutex(false, MutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
            log?.Invoke("the UHD device lock was left by a process that ended while holding it; taken");
        }
        if (!owned)
            log?.Invoke($"another ShackLinkUsrp process held the UHD device lock for {wait.TotalSeconds:0.#} s; going ahead without it");
        return new Holder(mutex, owned);
    }

    private sealed class Holder : IDisposable
    {
        private Mutex? _mutex;
        private readonly bool _owned;

        public Holder(Mutex mutex, bool owned)
        {
            _mutex = mutex;
            _owned = owned;
        }

        public void Dispose()
        {
            var m = Interlocked.Exchange(ref _mutex, null);
            if (m == null) return;
            try
            {
                if (_owned) m.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Released on another thread than the one that took it - a caller bug; the handle still closes.
            }
            m.Dispose();
        }
    }
}
