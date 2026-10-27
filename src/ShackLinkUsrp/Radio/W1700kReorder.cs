// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace ShackLinkUsrp.Radio;

/// <summary>
/// The export's datagrams put back in <c>seq</c> order before anything uses them. A network adapter may hand UDP datagrams to the
/// socket out of order - measured on a Realtek RTL8159 USB 10 GbE adapter (ShackLink docs/w1700k_support.md, 2026-10-27): nothing
/// lost, but at 160 MS/s 16,597 of 819,719 datagrams arrived after a later one, up to 48 behind it, where the onboard 2.5 GbE port had
/// delivered every one in order. Taken as they came, each was a lost completion AND a dropped slice: overflows announced for samples
/// that were on their way, and a splice in the ring.
/// <para>A datagram that arrives ahead of a hole is HELD (copied; the socket's buffer is reused). A place in the hole is given up only
/// when a datagram arrives a whole <see cref="Window"/> or more ahead of it, or at a <see cref="Flush"/>, and a hole is reported as
/// ONE <see cref="ISink.Missing"/> run, in its place in the order (just before the next delivery). One that arrives after its place
/// was given up is late, as before - and so is one from before a run's FIRST datagram, which sets where the run starts. In order,
/// nothing is copied and nothing waits, so a hole costs at most a window of delay (256 slices: 13 ms at 40 MS/s, 1.6 ms at 320).</para>
/// <para>ONE thread drives it (the receiver's); its counters are written there and read anywhere.</para>
/// </summary>
internal sealed class W1700kReorder
{
    /// <summary>Five times the furthest behind measured at 160 MS/s (48), and over three times 320's (70, on a link that could not carry
    /// it). 2 MiB of held slices at most.</summary>
    public const int DefaultWindow = 256;

    /// <summary>Where the ordered datagrams go.</summary>
    public interface ISink
    {
        /// <summary>The next datagram in sequence order. <paramref name="slice"/> is its slice, except for an
        /// <see cref="W1700kExport.Kind.Unknown"/> one too big to hold, which is delivered with its size and no bytes.</summary>
        void Deliver(W1700kExport.Kind kind, ulong seq, int sliceBytes, ReadOnlySpan<byte> slice);

        /// <summary><paramref name="count"/> completions from <paramref name="firstSeq"/> on never arrived: given up, here in the order.</summary>
        void Missing(ulong firstSeq, ulong count);
    }

    /// <summary>What became of one datagram.</summary>
    public enum Placed { Delivered, Held, Late }

    private readonly ISink _sink;
    private readonly ulong _mask;
    private readonly bool[] _held;
    private readonly W1700kExport.Kind[] _kind;
    private readonly int[] _sliceBytes;
    private readonly byte[]?[] _data;
    private int _heldCount;
    private bool _started;
    private ulong _next;
    private ulong _highest;
    // The places given up and not yet said: a hole the window slides past one place at a time is ONE missing run, said just
    // before the next delivery (or at a flush), never one run per place.
    private ulong _missFrom, _missCount;

    /// <summary>Datagrams that arrived after a later one and were put back in their place.</summary>
    public long Restored;

    /// <summary>The furthest behind a restored datagram arrived: how many later ones had come first.</summary>
    public long RestoredMax;

    public W1700kReorder(ISink sink, int window = DefaultWindow)
    {
        if (window < 1 || (window & (window - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(window), "a power of two");
        _sink = sink;
        Window = window;
        _mask = (ulong)window - 1;
        _held = new bool[window];
        _kind = new W1700kExport.Kind[window];
        _sliceBytes = new int[window];
        _data = new byte[]?[window];
    }

    public int Window { get; }

    /// <summary>Datagrams held behind a hole right now.</summary>
    public int Held => Volatile.Read(ref _heldCount);

    /// <summary>The next sequence number to deliver; null before the first datagram and after a <see cref="Reset"/>.</summary>
    public ulong? Next => _started ? _next : null;

    /// <summary>One datagram as it came off the socket.</summary>
    public Placed Accept(W1700kExport.Kind kind, ulong seq, int sliceBytes, ReadOnlySpan<byte> slice)
    {
        if (!_started)
        {
            _started = true;
            _next = seq;
            _highest = seq;
        }
        if (seq < _next) return Placed.Late;
        // Further ahead than the window reaches: the oldest places are given up (or delivered, if they came) until it fits.
        if (seq - _next >= (ulong)Window)
        {
            ReleaseTo(seq - (ulong)Window + 1);
            if (seq < _next) return Placed.Late;          // a repeat of one held, delivered just now
        }
        int i = (int)(seq & _mask);
        if (seq != _next && _held[i]) return Placed.Late;  // a repeat of one still held
        if (seq < _highest)
        {
            Restored++;
            long behind = (long)(_highest - seq);
            if (behind > RestoredMax) RestoredMax = behind;
        }
        else _highest = seq;
        if (seq == _next)
        {
            SayMissing();
            _sink.Deliver(kind, seq, sliceBytes, slice);
            _next++;
            Drain();
            return Placed.Delivered;
        }
        _held[i] = true;
        _kind[i] = kind;
        _sliceBytes[i] = sliceBytes;
        if (slice.Length > 0 && slice.Length <= W1700kExport.RawSliceBytes)
        {
            var buf = _data[i] ??= new byte[W1700kExport.RawSliceBytes];
            slice.CopyTo(buf);
        }
        Volatile.Write(ref _heldCount, _heldCount + 1);
        return Placed.Held;
    }

    /// <summary>Everything held, delivered in order, its holes given up: the stream has paused, changed, or ended.</summary>
    public void Flush()
    {
        if (_heldCount > 0) ReleaseTo(_highest + 1);
        SayMissing();
    }

    /// <summary>A new run: forget the order. Whatever is held, and any hole not yet said, is dropped - <see cref="Flush"/> first to
    /// keep them.</summary>
    public void Reset()
    {
        Array.Clear(_held);
        Volatile.Write(ref _heldCount, 0);
        _missCount = 0;
        _started = false;
    }

    /// <summary>The held ones from <see cref="_next"/> on, while they run without a hole.</summary>
    private void Drain()
    {
        while (_heldCount > 0)
        {
            int i = (int)(_next & _mask);
            if (!_held[i]) return;
            DeliverHeld(i);
            _next++;
        }
    }

    /// <summary>Every place before <paramref name="upTo"/>: a held datagram delivered, a hole given up (said with the run it is in).</summary>
    private void ReleaseTo(ulong upTo)
    {
        while (_next < upTo)
        {
            if (_heldCount == 0)
            {
                // Nothing held: the rest is one hole, however long, and no loop over it.
                GiveUp(_next, upTo - _next);
                _next = upTo;
                break;
            }
            int i = (int)(_next & _mask);
            if (_held[i]) DeliverHeld(i);
            else GiveUp(_next, 1);
            _next++;
        }
        Drain();
    }

    private void GiveUp(ulong from, ulong count)
    {
        if (_missCount == 0) _missFrom = from;
        _missCount += count;
    }

    private void SayMissing()
    {
        if (_missCount == 0) return;
        ulong n = _missCount;
        _missCount = 0;
        _sink.Missing(_missFrom, n);
    }

    private void DeliverHeld(int i)
    {
        SayMissing();
        _held[i] = false;
        Volatile.Write(ref _heldCount, _heldCount - 1);
        int bytes = _sliceBytes[i];
        var data = _data[i];
        _sink.Deliver(_kind[i], _next, bytes, bytes > 0 && bytes <= W1700kExport.RawSliceBytes && data != null ? data.AsSpan(0, bytes) : ReadOnlySpan<byte>.Empty);
    }
}
