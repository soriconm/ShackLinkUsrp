// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Radio;

/// <summary>
/// One open transmission - what both radios share (ShackLink docs/usrp_b210_support.md section 9.17
/// P4-a, P4-b). Its SOURCE is an IQ queue the <c>tx</c> stream fills, or a generator; its RADIO side (a
/// UHD sender thread, or the synthetic radio's own clock) takes samples from the source, reports what
/// happened through the Note* calls, and calls <see cref="Finish"/> exactly once, when it has let go of
/// the transmitter. <see cref="Stop"/> asks for a drain or an abort and waits for that - it never
/// declares an acknowledgement the radio did not give.
/// </summary>
internal abstract class RadioTransmission : ITxTransmission
{
    /// <summary>How long an abort waits for the radio to let go before it says it did not.</summary>
    public const int AbortWaitMs = 2000;

    private readonly RadioBackendBase _backend;
    private readonly ManualResetEventSlim _finished = new(false);
    private readonly object _gate = new();
    private volatile bool _drain;
    private volatile bool _abort;
    private int _stopping;
    private int _endedRaised;
    private int _finishing;
    private long _drainDeadlineTs;
    private long _samples;
    private long _underflows;
    private long _keyedTs;
    private long _finishedTs;
    private bool _acked;
    private string _finishText = "";

    protected RadioTransmission(RadioBackendBase backend, TxStartRequest request, TxGrant grant)
    {
        _backend = backend;
        Request = request;
        Grant = grant;
        if (request.Source == TxSourceKinds.Iq)
        {
            Queue = new TxSampleQueue(grant.QueueSamples);
        }
        else if (request.Source == TxSourceKinds.Tone)
        {
            Tone = new ToneGenerator(request.OffsetHz, grant.RateHz, request.LevelDbfs);
        }
        else
        {
            throw new RadioRefusal(ErrorCodes.Unsupported,
                $"this sidecar does not build a \"{request.Source}\" source yet; it builds {TxSourceKinds.Iq} and {TxSourceKinds.Tone}");
        }
        CenterHz = request.Source == TxSourceKinds.Iq ? grant.CenterHz : grant.CenterHz - request.OffsetHz;
    }

    public TxStartRequest Request { get; }
    public TxGrant Grant { get; }

    /// <summary>An IQ transmission's samples; null for a generated one.</summary>
    protected TxSampleQueue? Queue { get; }

    /// <summary>A tone's generator; null otherwise.</summary>
    protected ToneGenerator? Tone { get; }

    /// <summary>Where baseband 0 Hz leaves: the TX centre (a tone sits <c>offset_hz</c> from it). A park moves it.</summary>
    public long CenterHz
    {
        get => Volatile.Read(ref _centerHz);
        private set => Volatile.Write(ref _centerHz, value);
    }
    private long _centerHz;

    public bool IsFinished => _finished.IsSet;
    public bool IsKeyed => Interlocked.Read(ref _keyedTs) != 0;
    protected bool DrainRequested => _drain;
    protected bool AbortRequested => _abort;
    protected long DrainDeadlineTs => Interlocked.Read(ref _drainDeadlineTs);
    public long Samples => Interlocked.Read(ref _samples);
    public long Underflows => Interlocked.Read(ref _underflows);

    public event Action<StreamEnd>? Ended;
#pragma warning disable CS0067 // a stepped source raises it (P4b2); none here does yet
    public event Action<JsonObject>? Step;
#pragma warning restore CS0067

    /// <summary>A park of a generated carrier (the server allows it for a tone only): the carrier moves
    /// to <paramref name="centerHz"/> + its offset.</summary>
    public void Park(long centerHz)
    {
        CenterHz = centerHz;
        Grant.CenterHz = centerHz + Request.OffsetHz;
    }

    // ------------------------------------------------------------------ the radio side

    /// <summary>The first sample went out. <paramref name="position"/>: the ring position from which the
    /// receiver hears it, when the radio knows it exactly (the synthetic one); null = now.</summary>
    protected void NoteKeyed(long? position)
    {
        if (Interlocked.CompareExchange(ref _keyedTs, Stopwatch.GetTimestamp(), 0) != 0) return;
        _backend.OnTransmissionEdge(this, keyed: true, position);
    }

    protected void NoteSamples(long n)
    {
        if (n > 0) Interlocked.Add(ref _samples, n);
    }

    /// <summary>The radio ran short mid-burst: counted, and told to the server as it happens.</summary>
    protected void NoteUnderflow()
    {
        Interlocked.Increment(ref _underflows);
        _backend.RaiseTxUnderflow(1);
    }

    /// <summary>The radio failed under the transmission. The server is told once, on another thread -
    /// never from inside <see cref="Stop"/>, which already knows - and the radio side goes on to let go
    /// and <see cref="Finish"/>.</summary>
    protected void EndedByRadio(string reason, string text)
    {
        _abort = true;
        Queue?.Abort();
        if (Volatile.Read(ref _stopping) != 0) return;
        if (Interlocked.Exchange(ref _endedRaised, 1) != 0) return;
        var end = new StreamEnd { Reason = reason, Text = text };
        ThreadPool.QueueUserWorkItem(_ => Ended?.Invoke(end));
    }

    /// <summary>The radio has let go of the transmitter: once. <paramref name="acked"/> is the RADIO's
    /// word that the last sample left it - nothing else may set it.</summary>
    protected void Finish(bool acked, string text, long? position)
    {
        if (Interlocked.Exchange(ref _finishing, 1) != 0) return;
        lock (_gate)
        {
            _acked = acked;
            _finishText = text ?? "";
        }
        Interlocked.Exchange(ref _finishedTs, Stopwatch.GetTimestamp());
        try
        {
            _backend.OnTransmissionEdge(this, keyed: false, position);
        }
        finally
        {
            _finished.Set();
        }
    }

    /// <summary>A drain was asked for: the radio sends what is queued, then the end-of-burst, and waits
    /// for its acknowledgement until <see cref="DrainDeadlineTs"/>.</summary>
    protected virtual void OnDrainRequested() { }

    /// <summary>An abort was asked for: end the burst where it stands.</summary>
    protected virtual void OnAbortRequested() { }

    // ------------------------------------------------------------------ ITxTransmission

    public bool Write(IqHeader header, byte[] samples)
    {
        if (Queue == null || _drain || _abort) return false;
        return Queue.Write(samples, header.Count);
    }

    public TxStopResult Stop(bool drain, int timeoutMs)
    {
        Interlocked.Exchange(ref _stopping, 1);
        var sw = Stopwatch.StartNew();
        string stopText = "";
        if (drain && !_abort && !_finished.IsSet)
        {
            Interlocked.Exchange(ref _drainDeadlineTs, Stopwatch.GetTimestamp() + (long)(Math.Max(0, timeoutMs) / 1000.0 * Stopwatch.Frequency));
            _drain = true;
            Queue?.Close();
            OnDrainRequested();
            if (!_finished.Wait(Math.Max(0, timeoutMs)))
                stopText = $"the radio did not finish the drain within {timeoutMs} ms, so the burst was ended where it stood";
        }
        if (!_finished.IsSet)
        {
            _abort = true;
            Queue?.Abort();
            OnAbortRequested();
            if (!_finished.Wait(AbortWaitMs))
                stopText = Join(stopText, $"the radio had not let go of the transmitter {AbortWaitMs} ms after it was told to stop");
        }
        var res = new TxStopResult { WasOpen = true, Drain = drain };
        lock (_gate)
        {
            res.Acked = _acked;
            res.Text = Join(stopText, _finishText);
        }
        res.Samples = Samples;
        res.Underflows = Underflows;
        long keyed = Interlocked.Read(ref _keyedTs);
        long ended = Interlocked.Read(ref _finishedTs);
        if (keyed != 0) res.OnMs = (long)(((ended != 0 ? ended : Stopwatch.GetTimestamp()) - keyed) * 1000.0 / Stopwatch.Frequency);
        return res;
    }

    private static string Join(string a, string b) =>
        string.IsNullOrEmpty(a) ? b ?? "" : string.IsNullOrEmpty(b) ? a : a + "; " + b;

    public void Dispose()
    {
        if (_finished.IsSet) return;
        try { Stop(false, 0); } catch { }
    }
}
