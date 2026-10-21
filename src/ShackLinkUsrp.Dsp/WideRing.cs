// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace ShackLinkUsrp.Dsp;

/// <summary>
/// The wide stream between the radio thread and every consumer (ShackLink docs/usrp_b210_support.md
/// section 9.6): ONE producer that never blocks, any number of consumers each with its own read
/// position. Samples are interleaved sc16 (I, Q) exactly as UHD delivers them; positions count
/// complex samples since the ring was created and never go backwards. A consumer that falls more
/// than <see cref="Capacity"/> behind has been LAPPED: its read fails, and it counts the samples it
/// missed and jumps forward - the radio is never slowed down for a consumer (an overflow in UHD is a
/// sample the FPGA already threw away; a lap here is the same fact one stage later).
/// <para><b>A waiter is woken when ITS position is reached, not on every write</b> (ShackLink docs/skimmer_support.md
/// section 26.14): each registers the position it waits for and its own event, and the writer sets only the events whose
/// position it has passed. A PulseAll on every write woke every waiting lease about four times per chunk, three of them
/// for nothing, and with eight leases that waking cost more than their arithmetic.</para>
/// </summary>
public sealed class WideRing
{
    private readonly short[] _buf;
    private readonly int _capacity;
    private readonly int _mask;
    private long _written;
    private int _waiters;
    // The waiters, one slot each, claimed by compare-and-swap: no lock and no allocation on either side, and the writer
    // (the radio's own thread, which must never block) scans the slots up to the highest ever claimed.
    private const int WaiterSlots = 256;
    private readonly Waiter?[] _slots = new Waiter?[WaiterSlots];
    private int _slotsUsed;
    // Moved by WakeAll: a waiter that sees it move returns (a pipeline being stopped does not sit out its timeout).
    private int _wakeAllGen;

    private sealed class Waiter
    {
        public long Target;
        public readonly ManualResetEventSlim Event = new(false, spinCount: 1);
    }

    // A thread waits on one ring at a time (WaitFor blocks), so one waiter object per thread serves every ring.
    [ThreadStatic] private static Waiter? _mine;

    /// <param name="capacitySamples">Rounded up to a power of two.</param>
    public WideRing(int capacitySamples)
    {
        int c = 1;
        while (c < capacitySamples) c <<= 1;
        _capacity = c;
        _mask = c - 1;
        _buf = new short[c * 2];
    }

    /// <summary>Complex samples the ring holds before the oldest is overwritten.</summary>
    public int Capacity => _capacity;

    /// <summary>Total complex samples ever written: the position the next sample will have.</summary>
    public long Written => Volatile.Read(ref _written);

    /// <summary>Append interleaved I,Q. Never blocks; wakes every waiting consumer.</summary>
    public void Write(ReadOnlySpan<short> iq)
    {
        int n = iq.Length / 2;
        if (n == 0) return;
        if (n > _capacity)
        {
            // Only the newest Capacity samples can be kept; the rest are overwritten at once.
            int skip = n - _capacity;
            iq = iq.Slice(skip * 2);
            Volatile.Write(ref _written, _written + skip);
            n = _capacity;
        }
        long w = _written;
        int start = (int)(w & _mask);
        int first = Math.Min(n, _capacity - start);
        iq.Slice(0, first * 2).CopyTo(_buf.AsSpan(start * 2));
        if (n > first) iq.Slice(first * 2, (n - first) * 2).CopyTo(_buf.AsSpan(0));
        // A full fence, not a release store: the read of _waiters below must not be reordered ahead
        // of this store (x86 reorders a store after a later load), or a consumer that registered as a
        // waiter between the two could sleep through the data it is waiting for.
        Interlocked.Exchange(ref _written, w + n);
        if (Volatile.Read(ref _waiters) > 0) WakeReached(w + n);
    }

    // Set the event of every waiter whose position has been written. The waiter's side of the ordering is in WaitFor.
    private void WakeReached(long written)
    {
        int used = Volatile.Read(ref _slotsUsed);
        for (int i = 0; i < used; i++)
        {
            var r = Volatile.Read(ref _slots[i]);
            if (r != null && Volatile.Read(ref r.Target) <= written) r.Event.Set();
        }
    }

    /// <summary>Block until <see cref="Written"/> reaches <paramref name="position"/>. False on timeout.</summary>
    public bool WaitFor(long position, int timeoutMs)
    {
        if (Written >= position) return true;
        int gen = Volatile.Read(ref _wakeAllGen);
        var me = _mine ??= new Waiter();
        me.Event.Reset();
        Volatile.Write(ref me.Target, position);
        // Claim a slot (a full fence), THEN read Written: the writer stores Written, fences, then reads the slots - so
        // either it sees this waiter or this waiter sees its write.
        int slot = -1;
        for (int i = 0; i < WaiterSlots; i++)
            if (Interlocked.CompareExchange(ref _slots[i], me, null) == null) { slot = i; break; }
        if (slot < 0) return PollFor(position, timeoutMs);
        int used;
        while ((used = Volatile.Read(ref _slotsUsed)) <= slot && Interlocked.CompareExchange(ref _slotsUsed, slot + 1, used) != used) { }
        Interlocked.Increment(ref _waiters);
        try
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (Written < position)
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0 || !me.Event.Wait((int)left)) return Written >= position;
                me.Event.Reset();
                if (Volatile.Read(ref _wakeAllGen) != gen) return Written >= position;
            }
            return true;
        }
        finally
        {
            Volatile.Write(ref _slots[slot], null);
            Interlocked.Decrement(ref _waiters);
        }
    }

    // Every slot taken (more waiters than any client set has needed): wait by looking, correct and slower.
    private bool PollFor(long position, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Written < position)
        {
            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(1);
        }
        return true;
    }

    /// <summary>Wake every waiter, and make it RETURN - true if its data is there, false if not - so a pipeline being
    /// stopped does not sit out its timeout. A waiter that was not being stopped simply waits again.</summary>
    public void WakeAll()
    {
        Interlocked.Increment(ref _wakeAllGen);
        int used = Volatile.Read(ref _slotsUsed);
        for (int i = 0; i < used; i++) Volatile.Read(ref _slots[i])?.Event.Set();
    }

    /// <summary>Copy <paramref name="count"/> samples starting at <paramref name="position"/>
    /// into <paramref name="dest"/> (interleaved). False when any of them has been overwritten -
    /// checked AFTER the copy, because the producer may lap the reader while it copies.</summary>
    public bool TryRead(long position, int count, Span<short> dest)
    {
        if (count <= 0) return true;
        if (count > _capacity) throw new ArgumentOutOfRangeException(nameof(count));
        long w = Volatile.Read(ref _written);
        if (position + count > w) throw new InvalidOperationException("reading beyond what has been written");
        if (w - position > _capacity) return false;
        int start = (int)(position & _mask);
        int first = Math.Min(count, _capacity - start);
        _buf.AsSpan(start * 2, first * 2).CopyTo(dest);
        if (count > first) _buf.AsSpan(0, (count - first) * 2).CopyTo(dest.Slice(first * 2));
        long w2 = Volatile.Read(ref _written);
        return w2 - position <= _capacity;
    }

    /// <summary>Move the write position forward to <paramref name="position"/> without writing - how a receive
    /// side switched on starts at the position the other side has reached, so one position is one instant on
    /// both (ShackLink docs/usrp_b210_support.md section 9.23.2 item 1). Only while nothing writes the ring;
    /// never backwards. What was in the ring before is stale and no reader is left holding it (a side's
    /// pipelines end when it is switched off).</summary>
    public void AlignTo(long position)
    {
        if (position <= Volatile.Read(ref _written)) return;
        Interlocked.Exchange(ref _written, position);
        if (Volatile.Read(ref _waiters) > 0) WakeReached(position);
    }

    /// <summary>The oldest position still readable.</summary>
    public long Oldest => Math.Max(0, Written - _capacity);
}
