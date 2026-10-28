// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.ExceptionServices;

namespace ShackLinkUsrp.Radio;

/// <summary>
/// A few dedicated threads that share ONE job's items with the thread that hands it over (<see cref="Run"/>), which works too and
/// returns when every item is done. Items are claimed one at a time, so an uneven item costs nobody a whole share. Each run is a NEW
/// job object: a helper woken late finds its job finished and goes back to sleep - it can never claim an item of the next run.
/// <para>The W1700K's conditioning: past 160 MS/s one thread cannot flatten a slice in the time the next takes to arrive (12.2 us a
/// slice measured, 12.8 us between slices at 160, 6.4 at 320). Idle helpers cost nothing: they sleep on a semaphore.</para>
/// </summary>
internal sealed class WorkTeam : IDisposable
{
    internal sealed class Job
    {
        public Action<int> Body = null!;
        public int Count, Next, Remaining;
        /// <summary>Released ONCE, by whoever finishes the job's last item: the team's, so nothing per job needs disposing.</summary>
        public SemaphoreSlim Done = null!;
        public ExceptionDispatchInfo? Error;

        public void Work()
        {
            int j;
            while ((j = Interlocked.Increment(ref Next) - 1) < Count)
            {
                try
                {
                    Body(j);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref Error, ExceptionDispatchInfo.Capture(ex), null);
                }
                if (Interlocked.Decrement(ref Remaining) == 0) Done.Release();
            }
        }
    }

    private readonly Thread[] _threads;
    private readonly SemaphoreSlim _go = new(0);
    private readonly SemaphoreSlim _done = new(0);
    private volatile Job? _job;
    private volatile bool _stop;

    /// <summary>A team for this machine: one helper per eight logical processors, at least one and at most three.</summary>
    public static int HelpersFor(int processors) => Math.Clamp(processors / 8, 1, 3);

    public WorkTeam(int helpers, string name, ThreadPriority priority)
    {
        _threads = new Thread[Math.Max(0, helpers)];
        for (int i = 0; i < _threads.Length; i++)
        {
            _threads[i] = new Thread(Help) { IsBackground = true, Name = $"{name}{i + 1}", Priority = priority };
            _threads[i].Start();
        }
    }

    /// <summary>Helpers beside the caller.</summary>
    public int Helpers => _threads.Length;

    /// <summary>body(0) .. body(count - 1), shared with the helpers; returns when all are done. One item, or no helpers: the caller
    /// alone, no hand-over. An item that throws: the others still run, and the first exception is rethrown here.</summary>
    public void Run(int count, Action<int> body)
    {
        if (count <= 0) return;
        if (count == 1 || _threads.Length == 0 || _stop)
        {
            for (int j = 0; j < count; j++) body(j);
            return;
        }
        var job = new Job { Body = body, Count = count, Remaining = count, Done = _done };
        _job = job;
        _go.Release(Math.Min(_threads.Length, count - 1));
        job.Work();
        _done.Wait();
        job.Error?.Throw();
    }

    /// <summary>A job the helpers have started and the caller has not joined yet (<see cref="Start"/>).</summary>
    internal sealed class Started
    {
        internal Job? Job;
    }

    /// <summary>body(0) .. body(count - 1) started on the HELPERS alone, so the caller can do other work meanwhile; <see cref="Join"/>
    /// then works what is left and waits for the rest. ONE started job at a time: join it before starting another (the team's
    /// completion signal is one per job). No helpers: nothing starts, and Join does it all.</summary>
    public Started Start(int count, Action<int> body)
    {
        if (count <= 0) return new Started();
        var job = new Job { Body = body, Count = count, Remaining = count, Done = _done };
        if (_threads.Length > 0 && !_stop)
        {
            _job = job;
            _go.Release(Math.Min(_threads.Length, count));
        }
        return new Started { Job = job };
    }

    /// <summary>The caller works what is left of a <see cref="Start"/>ed job, then waits for the helpers' items; the first exception
    /// any item threw is rethrown here.</summary>
    public void Join(Started started)
    {
        var job = started.Job;
        if (job == null) return;
        started.Job = null;
        job.Work();
        _done.Wait();
        job.Error?.Throw();
    }

    private void Help()
    {
        while (true)
        {
            _go.Wait();
            if (_stop) return;
            _job?.Work();
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_threads.Length > 0) _go.Release(_threads.Length);
        foreach (var t in _threads) t.Join(2000);
    }
}
