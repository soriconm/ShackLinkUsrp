// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using ShackLinkIio.Ad936x;
using ShackLinkIio.Protocol;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Radio;

/// <summary>
/// A transmission on a Pluto-class board (ShackLink docs/plutosdr_support.md section 9.15), <see cref="UhdTransmission"/>'s
/// shape over iiod: ONE sender thread owns the TX DMA's stream - its own connection - for the transmission's life.
/// <list type="bullet">
/// <item><b>The key-up is at the FIRST WHOLE BUFFER</b> (item 4), never at <c>tx_start</c>: an IQ transmission radiates
/// nothing before its first sample, and a powered LO radiates its leakage. <see cref="PlutoRadio.KeyTx"/> writes the
/// bandwidth, the HELD attenuation and the TX LO, reads them back, and only then powers the LO - and a refusal there leaves
/// the board quiet.</item>
/// <item><b>Whole kernel buffers</b> (item 5): a <c>WRITEBUF</c> blocks while the board's are full, so the DMA paces this
/// thread, the queue paces the <c>tx</c> stream and TCP paces the client - no timer. The underflow flag is read after every
/// push; the first read is the idle DMA before the burst and is discarded.</item>
/// <item><b>The drain is the board's word</b> (item 6, section 9.2 item 3): the last buffer padded with zeros, the flag
/// cleared BEFORE it is pushed, then polled until it rises - the DMA ran dry after the last sample - or a deadline.</item>
/// <item><b>Every end</b> (item 7) writes SafeIdle, read back, and THEN closes the TX buffer. An abort closes the TX
/// connection first, which ends a push the board is holding.</item>
/// <item><b>Steps</b> (item 8): a baseband step moved inside the generator at its sample and is announced with the key-up's
/// instant plus that count (<c>timing</c> = sample, relative to step 0). A TUNED step is an LO write, and the board still
/// holds up to <see cref="PlutoTxStream.BuffersCount"/> buffers of the previous step: the write waits until the DMA has run
/// DRY, so the previous step's samples all left at the previous LO. Each tuned step therefore costs a gap of zeros, and is
/// announced with the instant the write returned (<c>timing</c> = bound).</item>
/// </list>
/// </summary>
internal sealed class PlutoTransmission : RadioTransmission
{
    /// <summary>After the last push, how long beyond the board's queue the DMA is given to run dry.</summary>
    public const int DrainMarginMs = 250;

    /// <summary>How often the underflow flag is polled while a drain or a tuned step waits for the DMA to run dry.</summary>
    private const int DryPollMs = 5;

    private readonly PlutoBackend _pluto;
    private readonly PlutoTxStream _stream;
    private readonly Thread _sender;
    private int _streamClosed;

    public PlutoTransmission(PlutoBackend backend, TxStartRequest request, TxGrant grant, TxStepPlan? plan, TxSignal? signal, PlutoTxStream stream)
        : base(backend, request, grant, plan, signal)
    {
        _pluto = backend;
        _stream = stream;
        _sender = new Thread(SenderLoop) { IsBackground = true, Name = "PlutoTx", Priority = ThreadPriority.Highest };
        _sender.Start();
    }

    /// <summary>The board's queue in samples: what may still be in flight after a push returns.</summary>
    private long QueueSamples => (long)_stream.BuffersCount * _stream.SamplesPerBuffer;

    private long QueueMs => (long)Math.Ceiling(1000.0 * QueueSamples / Grant.RateHz);

    /// <summary>An abort ends a push the board is holding: the TX connection goes, and the sender writes SafeIdle on the
    /// control connection.</summary>
    protected override void OnAbortRequested() => CloseStream();

    private void CloseStream()
    {
        if (Interlocked.Exchange(ref _streamClosed, 1) != 0) return;
        try { _stream.Dispose(); } catch { }
    }

    private void SenderLoop()
    {
        int n = _stream.SamplesPerBuffer;
        var bytes = new byte[_stream.BytesPerBuffer];   // once, reused
        var samples = new short[n * 2];
        bool keyed = false, acked = false, completed = false, firstFlag = true;
        long handed = 0;          // TX samples pushed to the board, counted from the key-up
        string text = "";
        try
        {
            void KeyUpNow()
            {
                // THE KEY-UP: the held attenuation and LO written, read back, and only then the LO powered.
                _pluto.KeyUp(this);
                keyed = true;
                NoteKeyed(null);
                // A stepped source's step 0 is the key-up itself.
                if (Plan != null) NoteStep(KeyedTicks, TxStepPlan.TimingSample);
            }

            while (!AbortRequested)
            {
                int filled = 0;
                bool last = false;
                if (Generator != null)
                {
                    if (DrainRequested) break;
                    // A generated source keys at its start: its samples are the sidecar's own and never wait.
                    if (!keyed) KeyUpNow();
                    while (filled < n)
                    {
                        if (Plan != null)
                        {
                            long at = handed + filled;
                            if (at >= Plan.EndSample)
                            {
                                completed = true;
                                last = true;
                                break;
                            }
                            if (NextStep >= 1 && NextStep < Plan.StepCount && at >= Plan.StartSample(NextStep))
                            {
                                if (Plan.Baseband)
                                {
                                    // The generator moves the carrier at exactly this sample: its instant is counted, not measured.
                                    NoteStep(TicksOfStep(NextStep), TxStepPlan.TimingSample);
                                    continue;
                                }
                                // A tuned step: what is filled goes out at THIS LO, then the DMA runs dry, then the LO moves.
                                if (filled > 0) break;
                                TunedStep();
                                continue;
                            }
                            int want = n - filled;
                            long next = Math.Max(1, NextStep);
                            if (next < Plan.StepCount) want = (int)Math.Min(want, Math.Max(1, Plan.StartSample(next) - at));
                            if (!Plan.Repeat) want = (int)Math.Min(want, Plan.EndSample - at);
                            Generator.Fill(samples.AsSpan(filled * 2, want * 2), want);
                            filled += want;
                        }
                        else
                        {
                            Generator.Fill(samples.AsSpan(filled * 2, (n - filled) * 2), n - filled);
                            filled = n;
                        }
                    }
                }
                else
                {
                    // ShackLink's IQ: a whole buffer before it is pushed, unless the stream has ended.
                    while (filled < n && !AbortRequested)
                    {
                        int got = Queue!.Read(samples.AsSpan(filled * 2, (n - filled) * 2), n - filled, 20);
                        filled += got;
                        if (got == 0 && DrainRequested && Queue.IsDrained)
                        {
                            last = true;
                            break;
                        }
                    }
                    if (AbortRequested) break;
                    if (filled == 0 && last) break;
                }
                if (filled == 0)
                {
                    if (last) break;
                    continue;
                }
                if (filled < n) Array.Clear(samples, filled * 2, (n - filled) * 2);

                // An IQ transmission keys at its first WHOLE buffer (or its only, shorter one): nothing radiates before it.
                if (!keyed) KeyUpNow();
                // The board's own sc16 is the wire's: little-endian I then Q, unchanged (section 2.5).
                MemoryMarshal.AsBytes(samples.AsSpan()).CopyTo(bytes);
                if (last)
                {
                    // The drain's word comes from the flag rising AFTER this, the last push: clear it first.
                    _pluto.ReadAndClearTxUnderflow();
                    firstFlag = false;
                }
                _stream.WriteBuffer(bytes);
                handed += filled;
                NoteSamples(filled);
                if (!last)
                {
                    bool? under = _pluto.ReadAndClearTxUnderflow();
                    if (under == true && !firstFlag) NoteUnderflow();
                    firstFlag = false;
                }
                if (last) break;
            }

            if (!keyed)
            {
                text = "no sample was transmitted, so there was no burst to end";
            }
            else if (!AbortRequested)
            {
                // Drained, completed, or a generated source told to drain: wait for the DMA to run dry.
                if (Generator != null && !completed) _pluto.ReadAndClearTxUnderflow();
                long deadlineTs = Stopwatch.GetTimestamp() + (long)((QueueMs + DrainMarginMs) / 1000.0 * Stopwatch.Frequency);
                if (DrainRequested && !completed) deadlineTs = Math.Min(deadlineTs, Math.Max(DrainDeadlineTs, Stopwatch.GetTimestamp()));
                bool? dry = WaitDry(deadlineTs);
                acked = dry == true;
                if (dry == null) text = "this board has no debugfs, so it cannot say when the last sample left (caps.overflow_oracle false)";
                else if (!acked) text = $"the board did not run dry within {QueueMs + DrainMarginMs} ms of the last push";
            }
        }
        catch (Exception ex) when (AbortRequested && (ex is IiodException || ex is IOException || ex is ObjectDisposedException))
        {
            // The abort closed the TX connection under a push: that is how an abort ends a push the board holds.
        }
        catch (Exception ex)
        {
            completed = false;
            text = ex is PlutoRefusalException || ex is IiodException || ex is Server.RadioRefusal
                ? (keyed ? "the board failed under the transmission: " : "the key-up was refused: ") + ex.Message
                : $"the transmit thread failed: {ex.GetType().Name}: {ex.Message}";
            EndedByRadio(EndReasons.RadioFault, text);
        }
        finally
        {
            // Item 7: SafeIdle, read back - THEN the TX buffer closed (the DAC back to a DDS whose scales are 0).
            string quiet = _pluto.TxEndSafeIdle(keyed);
            if (quiet.Length > 0) text = text.Length > 0 ? text + "; " + quiet : quiet;
            CloseStream();
            Finish(acked, text, null);
            if (completed) EndedBySource(SweepCompletedText());
        }
    }

    /// <summary>Until the underflow flag rises (true), the deadline passes (false), or the board cannot say (null).</summary>
    private bool? WaitDry(long deadlineTs)
    {
        while (true)
        {
            bool? f = _pluto.ReadAndClearTxUnderflow();
            if (f != false) return f;
            if (AbortRequested || Stopwatch.GetTimestamp() >= deadlineTs) return false;
            Thread.Sleep(DryPollMs);
        }
    }

    /// <summary>A tuned step: the previous step's samples all leave at the previous LO - the DMA runs dry - then the LO moves,
    /// read back, and the step is announced at the instant the write returned (an upper bound).</summary>
    private void TunedStep()
    {
        var plan = Plan!;
        int index = plan.IndexOf(NextStep);
        long lo = plan.LoHz[index];
        long deadlineTs = Stopwatch.GetTimestamp() + (long)((QueueMs + DrainMarginMs) / 1000.0 * Stopwatch.Frequency);
        if (WaitDry(deadlineTs) == false && !AbortRequested)
            throw new PlutoRefusalException(false, $"the board did not run dry within {QueueMs + DrainMarginMs} ms before a tuned step's LO move");
        var r = Backend.StepTune(plan.FrequencyHz[index], lo, moveLo: true);
        NoteStep(SidecarClock.Ticks, TxStepPlan.TimingBound, r.CenterHz, r.LoHz);
    }
}
