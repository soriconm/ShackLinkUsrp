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
/// <para>
/// A stepped source (section 9.18 P4b2) is counted in the samples this thread hands UHD. A baseband step
/// moved inside the generator at its sample, and is announced with the key-up's instant plus that count
/// (<c>timing</c> = sample: exact against the DAC, relative to step 0). A tuned step is a frequency write
/// made here once the previous step's last sample has been handed over; UHD applies it when it arrives,
/// not at a sample, so it is announced with the instant the write returned (<c>timing</c> = bound). A
/// sweep that does not repeat ends once its last sample is handed over, with the end-of-burst and the
/// radio's acknowledgement like a drain.
/// </para>
/// </summary>
internal sealed class UhdTransmission : RadioTransmission
{
    /// <summary>After an abort's end-of-burst, how long the radio is given to acknowledge it before the
    /// streamer is freed regardless.</summary>
    public const int AbortAckMs = 250;

    /// <summary>After a sweep's last sample was handed over, how long UHD's own buffers (up to 16 frames,
    /// 163 ms at 200 kS/s) are given to play out and the radio to acknowledge the end-of-burst.</summary>
    public const int CompletionAckMs = 2000;

    private readonly UhdTxStreamer _streamer;
    private readonly Action<UhdAsyncEventCode> _asyncSeen;
    private readonly Thread _sender;
    private long _abortTs;

    public UhdTransmission(RadioBackendBase backend, TxStartRequest request, TxGrant grant, TxStepPlan? plan, TxSignal? signal,
                           UhdTxStreamer streamer, Action<UhdAsyncEventCode> asyncSeen)
        : base(backend, request, grant, plan, signal)
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
        bool completed = false;
        long handed = 0;   // TX samples UHD has taken, counted from the key-up
        string text = "";
        try
        {
            while (!AbortRequested)
            {
                int n;
                if (Generator != null)
                {
                    // Nothing generated is queued host-side: a drain ends it at once.
                    if (DrainRequested) break;
                    n = chunk;
                    if (Plan != null)
                    {
                        if (handed >= Plan.EndSample)
                        {
                            completed = true;
                            break;
                        }
                        if (NextStep >= 1 && NextStep < Plan.StepCount && handed >= Plan.StartSample(NextStep))
                        {
                            TakeStep();
                            continue;
                        }
                        // Never hand over past the next step's first sample, or past the sweep's end.
                        long next = Math.Max(1, NextStep);
                        if (next < Plan.StepCount) n = (int)Math.Min(n, Plan.StartSample(next) - handed);
                        if (!Plan.Repeat) n = (int)Math.Min(n, Plan.EndSample - handed);
                    }
                    Generator.Fill(buf, n);
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
                            // A stepped source's step 0 is the key-up itself.
                            if (Plan != null) NoteStep(KeyedTicks, TxStepPlan.TimingSample);
                        }
                        off += sent;
                        handed += sent;
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
                long now0 = Stopwatch.GetTimestamp();
                long deadline = completed ? now0 + MsToTicks(CompletionAckMs)
                              : DrainRequested && !AbortRequested ? DrainDeadlineTs
                              : now0 + MsToTicks(AbortAckMs);
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
            completed = false;
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
            if (completed) EndedBySource(SweepCompletedText());
        }
    }

    /// <summary>The samples before step <see cref="RadioTransmission.NextStep"/> have all been handed over.</summary>
    private void TakeStep()
    {
        var plan = Plan!;
        if (plan.Baseband)
        {
            // The generator moved the carrier at exactly this sample: its instant is counted, not measured.
            NoteStep(TicksOfStep(NextStep), TxStepPlan.TimingSample);
            return;
        }
        // A frequency write, untimed: it lands when it arrives, so the instant it returned is an upper bound.
        int index = plan.IndexOf(NextStep);
        long lo = plan.LoHz[index];
        var r = Backend.StepTune(plan.FrequencyHz[index], lo, moveLo: lo != StepLoHz);
        NoteStep(SidecarClock.Ticks, TxStepPlan.TimingBound, r.CenterHz, r.LoHz);
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
