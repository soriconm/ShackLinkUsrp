// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Radio;

/// <summary>
/// A transmission on a W1700K (ShackLink docs/w1700k_support.md "Transmit"): the samples go to the unit's transmit service
/// (<see cref="W1700kTxServe"/>), whose <c>start</c> the backend has already had answered. ONE sender thread takes them from
/// the queue (ShackLink's IQ) or the generator (a tone) and writes them to the service, and <b>that write blocking is the
/// transmission's only clock</b>: the service holds its reader while the unit's queue is full. Nothing radiates before the
/// service has its prebuffer; RF's start is the service's own <c>keyed</c> event, never this side's guess.
/// <para>The end is the SERVICE's word: a drain is its <c>stop</c> with drain, and <c>acked</c> is true only when the unit's
/// feeder confirmed what was sent had played. A stop that is not answered, and an abort that does not finish, end the service
/// itself (<see cref="W1700kTxServe.Kill"/>): the unit unkeys when its feeder's link is gone.</para>
/// </summary>
internal sealed class W1700kTransmission : RadioTransmission
{
    /// <summary>How long the service is given to answer a stop that ends the burst where it stands.</summary>
    public const int AbortReplyMs = 1000;

    private readonly W1700kBackend _radio;
    private readonly W1700kTxServe _serve;
    private readonly int _blockSamples;
    private readonly Thread _sender;
    private volatile string _endedByService = "";
    private volatile string _endedText = "";

    public W1700kTransmission(W1700kBackend radio, W1700kTxServe serve, int blockSamples, TxStartRequest request, TxGrant grant, TxSignal? signal)
        : base(radio, request, grant, null, signal)
    {
        _radio = radio;
        _serve = serve;
        _blockSamples = Math.Max(1, blockSamples);
        serve.Keyed += OnKeyed;
        serve.Ended += OnEnded;
        serve.Exited += OnExited;
        _sender = new Thread(SendLoop) { IsBackground = true, Name = "W1700kTx" };
        _sender.Start();
    }

    /// <summary>The service's <c>keyed</c>: RF began at its <c>t_unix_us</c>, restated on the sidecar's clock (the same machine's).</summary>
    private void OnKeyed(JsonObject e)
    {
        long ticks = SidecarClock.Ticks;
        if (e.TryGetLong("t_unix_us", out long us) && us > 0)
        {
            long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
            long ago = Math.Clamp(nowUs - us, 0, 5_000_000);
            ticks -= ago * 10;
        }
        NoteKeyed(null, ticks);
    }

    /// <summary>The service ended the transmission itself (its cap, the envelope, the board's temperature, the link, the feeder).</summary>
    private void OnEnded(JsonObject e)
    {
        if (IsFinished) return;
        string reason = e.GetString("reason", "") ?? "";
        string text = e.GetString("text", "") ?? "";
        _endedText = text.Length > 0 ? $"the unit's transmitter ended it ({reason}): {text}" : $"the unit's transmitter ended it ({reason})";
        _endedByService = reason.Length > 0 ? reason : "ended";
        EndedByRadio(reason == EndReasons.Cap ? EndReasons.Cap : EndReasons.RadioFault, _endedText);
    }

    private void OnExited(string why)
    {
        if (IsFinished) return;
        _endedText = "the unit's transmit service ended under the transmission: " + why;
        _endedByService = "service_gone";
        EndedByRadio(EndReasons.RadioFault, _endedText);
    }

    private void SendLoop()
    {
        var block = new short[_blockSamples * 2];
        var bytes = new byte[_blockSamples * 4];
        string fault = "";
        try
        {
            while (!AbortRequested && _endedByService.Length == 0)
            {
                int n;
                if (Generator != null)
                {
                    if (DrainRequested) break;                      // nothing generated is queued: its end is now
                    Generator.Fill(block, _blockSamples);
                    n = _blockSamples;
                }
                else
                {
                    n = Queue!.Read(block, _blockSamples, 50);
                    if (n == 0)
                    {
                        if (DrainRequested && Queue.IsDrained) break;
                        continue;
                    }
                }
                MemoryMarshal.AsBytes(block.AsSpan(0, n * 2)).CopyTo(bytes);     // x64: little-endian, as the wire
                _serve.WriteSamples(bytes, 0, n * 4);                            // BLOCKS: the service paces us
                NoteSamples(n);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            fault = "the unit's transmit service took no more samples: " + ex.Message;
        }

        _serve.Keyed -= OnKeyed;
        bool drain = DrainRequested && !AbortRequested && fault.Length == 0 && _endedByService.Length == 0;
        bool acked = false;
        string text = fault.Length > 0 ? fault : _endedText;
        // An end that began on THIS side - the service took no more samples, or no longer answers - is the radio's, and the
        // server must be told whichever thread noticed first: the service's own exit may never be raised once it is ended here.
        bool radioSide = fault.Length > 0;
        if (_serve.ExitedWhy.Length == 0)
        {
            // The service's own stop, whoever ended it: with nothing open it answers was_open:false and never fails.
            int wait = drain ? (int)Math.Max(AbortReplyMs, Stopwatch_MsUntil(DrainDeadlineTs)) : AbortReplyMs;
            try
            {
                var r = _serve.Request(new JsonObject().Set("op", "stop").Set("drain", drain).Set("timeout_ms", wait), wait + 500);
                acked = drain && r.GetBool("acked");
                long underruns = r.GetLong("underruns", 0);
                for (long i = 0; i < Math.Min(underruns, 1000); i++) NoteUnderflow();
                if (r.GetBool("was_open") && r.TryGetLong("samples_played", out long played) && r.TryGetDouble("unit_rate_hz", out double unit) && unit > 0)
                    _radio.NoteUnitTxRate(unit);
                string said = r.GetString("text", "") ?? "";
                if (text.Length == 0) text = said;
            }
            catch (Exception ex)
            {
                // No answer to a stop is a transmitter that may still be keyed: end the service, and with it the feeder's link.
                _serve.Kill();
                text = (text.Length > 0 ? text + "; " : "") + $"the unit's transmit service did not answer the stop ({ex.Message}), so it was ended: " +
                       "the unit unkeys by itself when its feeder's link is gone";
                radioSide = true;
                _radio.OnTxServiceLost(_serve);
            }
        }
        else if (fault.Length > 0)
        {
            // The service is gone and nobody has given the radio back yet (its exit was seen here, by the write, first).
            _radio.OnTxServiceLost(_serve);
        }
        _serve.Ended -= OnEnded;
        _serve.Exited -= OnExited;
        // Told once, and never from inside a Stop (which already knows): EndedByRadio sees to both.
        if (radioSide) EndedByRadio(EndReasons.RadioFault, text);
        Finish(acked, text, null);
    }

    private static double Stopwatch_MsUntil(long deadlineTs)
    {
        if (deadlineTs == 0) return 0;
        return (deadlineTs - System.Diagnostics.Stopwatch.GetTimestamp()) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    protected override void OnAbortRequested()
    {
        // The sender sees the abort after the block in flight (the service takes it within one block's play time, or - with
        // nothing open - at once). If it does not, the dead-man: the service is ended, and the pipe's writer with it. Its own
        // thread, never the pool's: a stop must not wait for a pool that is busy.
        var serve = _serve;
        new Thread(() =>
        {
            if (_sender.Join(AbortReplyMs + 1000)) return;
            serve.Kill();
            _radio.OnTxServiceLost(serve);
        })
        { IsBackground = true, Name = "W1700kTxDeadMan" }.Start();
    }
}
