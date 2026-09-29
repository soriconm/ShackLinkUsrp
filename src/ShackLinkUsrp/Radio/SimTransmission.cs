// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Radio;

/// <summary>
/// A transmission on the synthetic radio (ShackLink docs/usrp_b210_support.md section 9.17 P4-b): it
/// TRANSMITS INTO ITSELF. Its DAC is the radio's own generator thread, which takes TX samples at the TX
/// rate against the same clock that paces the receive stream - so a writer is paced by it exactly as a
/// real radio's full queue paces one - and mixes them into the receive stream at the TX centre and a
/// stated coupling. A gate SEES the transmission in the panorama, and nothing anywhere radiated.
/// Every call below <see cref="Mix"/> runs on that one thread.
/// </summary>
internal sealed class SimTransmission : RadioTransmission
{
    private TxInterpolator? _interp;
    private short[] _in = new short[8192];
    private double[] _re = new double[4096];
    private double[] _im = new double[4096];
    private double _ncoRe = 1.0, _ncoIm = 0.0;
    private bool _starving;

    public SimTransmission(RadioBackendBase backend, TxStartRequest request, TxGrant grant) : base(backend, request, grant) { }

    /// <summary>
    /// The synthetic radio's clock reached this transmission for a block of <paramref name="n"/> wide
    /// samples at ring <paramref name="position"/>, taken at <paramref name="loHz"/> and
    /// <paramref name="rateHz"/>: take the TX samples that block's time holds and add them into
    /// <paramref name="acc"/> (interleaved, sc16 units) at <paramref name="amplitude"/> x their level.
    /// A key-up, and the end of a burst, fall on block boundaries, so the edges this announces are the
    /// exact ring positions.
    /// </summary>
    public void Mix(double[] acc, int n, long loHz, double rateHz, double amplitude, long position)
    {
        if (IsFinished) return;
        if (AbortRequested)
        {
            // Ended where it stood: this block carries none of it.
            Finish(acked: false, "", position);
            return;
        }
        if (_interp == null || _interp.OutRateHz != rateHz) _interp = new TxInterpolator(Grant.RateHz, rateHz);
        int need = _interp.InputNeeded(n);
        if (_in.Length < need * 2) _in = new short[need * 2];
        int got;
        if (Tone != null)
        {
            if (DrainRequested)
            {
                // Nothing of a tone is queued: its end-of-burst is now, and the radio's word is that it ended.
                Finish(acked: true, "", position);
                return;
            }
            Tone.Fill(_in, need);
            got = need;
        }
        else
        {
            got = Queue!.Read(_in, need, 0);
            if (!IsKeyed && got == 0)
            {
                // An IQ transmission radiates nothing before its first sample.
                if (DrainRequested && Queue.IsDrained) Finish(acked: false, "no sample was transmitted, so there was no burst to end", position);
                return;
            }
        }
        if (!IsKeyed) NoteKeyed(position);
        NoteSamples(got);
        bool endOfBurst = false;
        if (got < need)
        {
            Array.Clear(_in, got * 2, (need - got) * 2);
            if (DrainRequested && Queue!.IsDrained)
            {
                endOfBurst = true;
            }
            else if (!_starving)
            {
                // A real radio sends zeros until more arrive and says so once per shortfall.
                _starving = true;
                NoteUnderflow();
            }
        }
        else
        {
            _starving = false;
        }

        _interp.Push(_in, need);
        if (_re.Length < n)
        {
            _re = new double[n];
            _im = new double[n];
        }
        _interp.Produce(n, _re, _im);

        // Heard only where the receiver's span holds the whole band; a real receiver's filters would
        // take anything beyond it, and folding it back in would put a signal where nothing transmits.
        double shift = CenterHz - loHz;
        if (Math.Abs(shift) + Grant.RateHz * ProtocolConstants.TxUsableFraction / 2 < rateHz / 2)
        {
            double w = 2 * Math.PI * shift / rateHz;
            double sr = Math.Cos(w), si = Math.Sin(w);
            double pr = _ncoRe, pi = _ncoIm;
            for (int j = 0; j < n; j++)
            {
                double xr = _re[j], xi = _im[j];
                acc[2 * j] += amplitude * (xr * pr - xi * pi);
                acc[2 * j + 1] += amplitude * (xr * pi + xi * pr);
                double npr = pr * sr - pi * si;
                pi = pr * si + pi * sr;
                pr = npr;
            }
            double mag = Math.Sqrt(pr * pr + pi * pi);
            _ncoRe = pr / mag;
            _ncoIm = pi / mag;
        }
        if (endOfBurst) Finish(acked: true, "", position + n);
    }

    /// <summary>The synthetic radio stopped under the transmission (a fault, or it closed).</summary>
    public void RadioStopped(string text, long position)
    {
        if (IsFinished) return;
        EndedByRadio(EndReasons.RadioFault, text);
        Finish(acked: false, text, position);
    }
}
