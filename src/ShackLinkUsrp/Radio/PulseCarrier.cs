// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Radio;

/// <summary>
/// The pulse source (ShackLink docs/usrp_b210_support.md section 9.21 Q4): a carrier at <c>offset_hz</c> from the TX
/// centre, keyed on and off IN THE SAMPLES by the protocol library's envelope (<see cref="TxPulse.Envelope"/>) - the
/// ONE formula a client predicts it by - as one burst for the whole train. Each sample's envelope is evaluated at
/// that sample's instant counted from the key-up, so the width and the period are exact, never rounded to samples;
/// that is sound only because each edge (the integral of a Hann window) is at least 5 samples long, which this
/// refuses below, as TxRules refused first. The level is the ON level: the carrier's while the pulse is up.
/// <para>
/// Between pulses the samples are exactly zero, but the transmitter stays keyed (the ATR holds it for the burst), so
/// what leaves a B2xx then is its LO leakage and its chain's noise - an on/off ratio this cannot state (section 9.21
/// Q4). The synthetic radio models neither.
/// </para>
/// </summary>
internal sealed class PulseCarrier : IIqGenerator
{
    private const int RenormEvery = 1024;

    private readonly double _amplitude;
    private readonly double _stepRe, _stepIm;
    private double _re = 1.0, _im = 0.0;
    private int _sinceRenorm;
    private long _n;                     // samples written since the key-up

    public double RateHz { get; }
    public double WidthS { get; }
    public double PeriodS { get; }
    public double RiseS { get; }
    public double FallS { get; }

    public PulseCarrier(double offsetHz, double rateHz, double levelDbfs, double widthS, double periodS, double riseS, double fallS)
    {
        if (!(rateHz > 0)) throw new ArgumentOutOfRangeException(nameof(rateHz));
        if (!(levelDbfs <= 0)) throw new ArgumentOutOfRangeException(nameof(levelDbfs), "a carrier above digital full scale is refused, never clipped");
        double shortest = TxPulse.MinEdgeSeconds(rateHz);
        if (!(riseS >= shortest * (1 - 1e-9) && fallS >= shortest * (1 - 1e-9)))
            throw new ArgumentOutOfRangeException(nameof(riseS), "an edge shorter than 5 samples at this rate is refused, never sharpened by aliasing");
        if (!(widthS >= (riseS + fallS) / 2 * (1 - 1e-9) && periodS >= (widthS + (riseS + fallS) / 2) * (1 - 1e-9)))
            throw new ArgumentOutOfRangeException(nameof(periodS), "a pulse whose flat top is negative, or whose envelope never returns to zero, is refused");
        if (!(Math.Abs(offsetHz) + 2.0 / Math.Min(riseS, fallS) < rateHz / 2))
            throw new ArgumentOutOfRangeException(nameof(offsetHz), "the pulse must lie inside the TX rate's Nyquist band");
        RateHz = rateHz;
        WidthS = widthS;
        PeriodS = periodS;
        RiseS = riseS;
        FallS = fallS;
        _amplitude = 32767.0 * Math.Pow(10, levelDbfs / 20.0);
        double w = 2.0 * Math.PI * offsetHz / rateHz;
        _stepRe = Math.Cos(w);
        _stepIm = Math.Sin(w);
    }

    /// <summary>The pulse a request's signal describes: its width and period, and its EFFECTIVE edges (TxRules.Describe
    /// put the fastest the widest rate holds where the request named none).</summary>
    public static PulseCarrier For(TxStartRequest request, TxSignal signal, double rateHz) =>
        new(request.OffsetHz, rateHz, request.LevelDbfs, signal.PulseWidthUs * 1e-6, signal.PulsePeriodUs * 1e-6,
            signal.PulseEdgeRiseS, signal.PulseEdgeFallS);

    public void Fill(Span<short> iq, int count)
    {
        double a = _amplitude, pr = _re, pi = _im, sr = _stepRe, si = _stepIm;
        for (int k = 0; k < count; k++)
        {
            double env = a * TxPulse.Envelope(_n / RateHz, WidthS, PeriodS, RiseS, FallS);
            iq[2 * k] = (short)Math.Clamp(Math.Round(env * pr), -32767, 32767);
            iq[2 * k + 1] = (short)Math.Clamp(Math.Round(env * pi), -32767, 32767);
            _n++;
            double npr = pr * sr - pi * si;
            pi = pr * si + pi * sr;
            pr = npr;
            if (++_sinceRenorm >= RenormEvery)
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
