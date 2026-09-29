// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace ShackLinkUsrp.Dsp;

/// <summary>
/// The <c>tone</c> source (ShackLink docs/usrp_b210_support.md section 9.17 P4-a, the RF Bench Analyser's
/// W9): one unmodulated carrier at <see cref="OffsetHz"/> from the TX centre, as interleaved sc16 at the
/// TX rate. Its level is the CARRIER's against the DAC's full scale: a complex exponential of amplitude
/// A has |I + jQ| = A, and 0 dBFS is A = 32767, where I and Q each just reach full scale - the same
/// convention the panorama reads a -20 dBFS carrier by. Phase-continuous across every call, and across
/// a <see cref="Retune"/>: a stepped source's baseband step is this carrier moving at one sample.
/// </summary>
public sealed class ToneGenerator : IIqGenerator
{
    private readonly double _amplitude;
    private double _stepRe, _stepIm;
    private double _re = 1.0, _im = 0.0;
    private int _sinceRenorm;

    public double OffsetHz { get; private set; }
    public double RateHz { get; }
    public double LevelDbfs { get; }

    public ToneGenerator(double offsetHz, double rateHz, double levelDbfs)
    {
        if (!(rateHz > 0)) throw new ArgumentOutOfRangeException(nameof(rateHz));
        if (!(levelDbfs <= 0)) throw new ArgumentOutOfRangeException(nameof(levelDbfs), "a carrier above digital full scale is refused, never clipped");
        RateHz = rateHz;
        LevelDbfs = levelDbfs;
        _amplitude = 32767.0 * Math.Pow(10, levelDbfs / 20.0);
        Retune(offsetHz);
    }

    /// <summary>Move the carrier to <paramref name="offsetHz"/> from the next sample on. The phase carries
    /// on from where it stands - the DUC's own accumulator keeps its phase across a frequency write too
    /// (duc_chain.v: <c>phase &lt;= phase + phase_inc</c>).</summary>
    public void Retune(double offsetHz)
    {
        if (Math.Abs(offsetHz) >= RateHz / 2) throw new ArgumentOutOfRangeException(nameof(offsetHz), "the carrier must lie inside the TX rate's Nyquist band");
        OffsetHz = offsetHz;
        double w = 2.0 * Math.PI * offsetHz / RateHz;
        _stepRe = Math.Cos(w);
        _stepIm = Math.Sin(w);
    }

    /// <summary>Write <paramref name="count"/> samples, interleaved I, Q.</summary>
    public void Fill(Span<short> iq, int count)
    {
        double a = _amplitude, pr = _re, pi = _im, sr = _stepRe, si = _stepIm;
        for (int n = 0; n < count; n++)
        {
            // Clamped, never wrapped: at 0 dBFS a phasor a hair over unit length would round to 32768.
            iq[2 * n] = (short)Math.Clamp(Math.Round(a * pr), -32767, 32767);
            iq[2 * n + 1] = (short)Math.Clamp(Math.Round(a * pi), -32767, 32767);
            double npr = pr * sr - pi * si;
            pi = pr * si + pi * sr;
            pr = npr;
            if (++_sinceRenorm >= 1024)
            {
                double mag = Math.Sqrt(pr * pr + pi * pi);
                pr /= mag;
                pi /= mag;
                _sinceRenorm = 0;
            }
        }
        _re = pr;
        _im = pi;
    }
}
