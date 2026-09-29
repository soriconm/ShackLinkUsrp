// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using ShackLinkUsrp.Uhd;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Radio;

/// <summary>
/// A transmission through UHD (ShackLink docs/usrp_b210_support.md section 9.17 P4-b). Its TX streamer
/// is made for it and freed when it ends (item 7: the streamer's existence powers the TX chain), and ONE
/// sender thread owns that streamer for its whole life - the sends, the async poll that brings
/// BURST_ACK and UNDERFLOW, the end-of-burst and the free - because the C API writes the streamer's
/// error string on every call. The first send carries start-of-burst; every way out sends end-of-burst
/// once anything was sent; <c>acked</c> is BURST_ACK and nothing else.
/// </summary>
internal sealed class UhdTransmission : RadioTransmission
{
    /// <summary>After an abort's end-of-burst, how long the radio is given to acknowledge it before the
    /// streamer is freed regardless.</summary>
    public const int AbortAckMs = 250;

    private readonly UhdTxStreamer _streamer;
    private readonly Action<UhdAsyncEventCode> _asyncSeen;
    private readonly Thread _sender;
    private long _abortTs;

    public UhdTransmission(RadioBackendBase backend, TxStartRequest request, TxGrant grant, UhdTxStreamer streamer,
                           Action<UhdAsyncEventCode> asyncSeen)
        : base(backend, request, grant)
    {
        _streamer = streamer;
        _asyncSeen = asyncSeen;
        _sender = new Thread(SenderLoop) { IsBackground = true, Name = "UhdTx", Priority = ThreadPriority.Highest };
        _sender.Start();
    }

    protected override void OnAbortRequested() => Interlocked.CompareExchange(ref _abortTs, Stopwatch.GetTimestamp(), 0);

    private static long MsToTicks(int ms) => (long)(ms / 1000.0 * Stopwatch.Frequency);

    private void SenderLoop()
    {
        UhdUsrp.TrySetThreadPriority();
        var s = _streamer;
        int quantum = Math.Max(1, s.MaxSamplesPerPacket);
        int chunk = quantum * 4;
        var buf = new short[chunk * 2];
        bool started = false;
        bool acked = false;
        string text = "";
        try
        {
            while (!AbortRequested)
            {
                int n;
                if (Tone != null)
                {
                    // Nothing of a tone is queued host-side: a drain ends it at once.
                    if (DrainRequested) break;
                    Tone.Fill(buf, chunk);
                    n = chunk;
                }
                else
                {
                    n = Queue!.Read(buf, chunk, 20);
                    if (n == 0)
                    {
                        Poll(0.0, ref acked);
                        if (DrainRequested && Queue.IsDrained) break;
                        continue;
                    }
                }
                int off = 0;
                while (off < n && !AbortRequested)
                {
                    // Blocks while UHD's send buffers are full: the radio's own rate paces this loop.
                    int sent = s.Send(buf.AsSpan(off * 2, (n - off) * 2), startOfBurst: !started, 0.1);
                    if (sent > 0)
                    {
                        if (!started)
                        {
                            started = true;
                            NoteKeyed(null);
                        }
                        off += sent;
                        NoteSamples(sent);
                    }
                    Poll(0.0, ref acked);
                }
            }
            if (!started)
            {
                text = "no sample was transmitted, so there was no burst to end";
            }
            else
            {
                s.SendEndOfBurst(0.5);
                long deadline = DrainRequested && !AbortRequested ? DrainDeadlineTs : Stopwatch.GetTimestamp() + MsToTicks(AbortAckMs);
                while (!acked)
                {
                    long now = Stopwatch.GetTimestamp();
                    long abortTs = Interlocked.Read(ref _abortTs);
                    if (abortTs != 0) deadline = Math.Min(deadline, abortTs + MsToTicks(AbortAckMs));
                    if (now >= deadline) break;
                    Poll(0.02, ref acked);
                }
                if (!acked) text = "the radio did not acknowledge the end-of-burst in time";
            }
        }
        catch (Exception ex)
        {
            text = ex is UhdException ? "the radio failed under the transmission: " + ex.Message
                                      : $"the transmit thread failed: {ex.GetType().Name}: {ex.Message}";
            EndedByRadio(EndReasons.RadioFault, text);
            if (started)
                try { s.SendEndOfBurst(0.1); } catch { }
        }
        finally
        {
            try { s.Dispose(); } catch { }
            Finish(acked, text, null);
        }
    }

    /// <summary>Read what the async channel holds, waiting at most <paramref name="timeoutSeconds"/> for
    /// the first message.</summary>
    private void Poll(double timeoutSeconds, ref bool acked)
    {
        double wait = timeoutSeconds;
        while (_streamer.PollAsync(wait, out var ev))
        {
            wait = 0.0;
            _asyncSeen(ev.Code);
            if ((ev.Code & UhdAsyncEventCode.BurstAck) != 0) acked = true;
            if ((ev.Code & (UhdAsyncEventCode.Underflow | UhdAsyncEventCode.UnderflowInPacket)) != 0) NoteUnderflow();
        }
    }
}
