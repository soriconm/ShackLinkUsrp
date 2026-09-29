// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace ShackLinkUsrp.Radio;

/// <summary>
/// An IQ transmission's samples between the wire and the radio: a bounded ring of interleaved sc16 whose
/// full state BLOCKS the writer - the <c>tx</c> stream's reader thread - so TCP backpressure paces the
/// client with no timer anywhere (ShackLink docs/usrp_b210_support.md section 9.17 P4-a). The radio side
/// reads whatever is there. A drain closes it to writers and lets the reader take the rest; an abort
/// discards it and wakes everyone.
/// </summary>
internal sealed class TxSampleQueue
{
    private readonly object _gate = new();
    private readonly short[] _buf;
    private int _head;        // sample index of the oldest queued sample
    private int _count;
    private bool _closed;
    private bool _aborted;

    public int Capacity { get; }

    public TxSampleQueue(int capacitySamples)
    {
        if (capacitySamples < 1) throw new ArgumentOutOfRangeException(nameof(capacitySamples));
        Capacity = capacitySamples;
        _buf = new short[capacitySamples * 2];
    }

    public int Count
    {
        get { lock (_gate) return _count; }
    }

    /// <summary>Closed to writers and empty: every sample it was given has been read.</summary>
    public bool IsDrained
    {
        get { lock (_gate) return (_closed || _aborted) && _count == 0; }
    }

    /// <summary>
    /// Append <paramref name="count"/> samples of little-endian sc16 bytes, BLOCKING while there is no
    /// room. False once the queue is closed or aborted: nothing handed over after a drain began is part
    /// of the burst. A block larger than the whole queue is refused in words - the grant states the
    /// largest block, and it fits.
    /// </summary>
    public bool Write(ReadOnlySpan<byte> sc16, int count)
    {
        if (count < 0 || sc16.Length < count * 4) throw new ArgumentOutOfRangeException(nameof(count));
        if (count > Capacity)
            throw new ArgumentOutOfRangeException(nameof(count), $"a block of {count} samples is larger than the {Capacity}-sample transmit queue");
        var samples = MemoryMarshal.Cast<byte, short>(sc16.Slice(0, count * 4));   // x64: little-endian, as the wire
        lock (_gate)
        {
            while (!_closed && !_aborted && Capacity - _count < count) Monitor.Wait(_gate);
            if (_closed || _aborted) return false;
            int tail = (_head + _count) % Capacity;
            int first = Math.Min(count, Capacity - tail);
            samples.Slice(0, first * 2).CopyTo(_buf.AsSpan(tail * 2));
            samples.Slice(first * 2).CopyTo(_buf.AsSpan(0));
            _count += count;
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    /// <summary>Take up to <paramref name="max"/> samples, waiting up to <paramref name="waitMs"/> for the
    /// first; 0 when none came.</summary>
    public int Read(Span<short> dst, int max, int waitMs)
    {
        lock (_gate)
        {
            if (_count == 0 && waitMs > 0 && !_closed && !_aborted) Monitor.Wait(_gate, waitMs);
            int n = Math.Min(max, _count);
            if (n == 0) return 0;
            int first = Math.Min(n, Capacity - _head);
            _buf.AsSpan(_head * 2, first * 2).CopyTo(dst);
            _buf.AsSpan(0, (n - first) * 2).CopyTo(dst.Slice(first * 2));
            _head = (_head + n) % Capacity;
            _count -= n;
            Monitor.PulseAll(_gate);
            return n;
        }
    }

    /// <summary>A drain: no more writes; what is queued stays for the reader.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>An abort: what is queued is discarded, and every waiter wakes.</summary>
    public void Abort()
    {
        lock (_gate)
        {
            _aborted = true;
            _count = 0;
            Monitor.PulseAll(_gate);
        }
    }
}
