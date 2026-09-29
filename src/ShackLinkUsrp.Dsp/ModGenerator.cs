// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace ShackLinkUsrp.Dsp;

/// <summary>
/// The <c>mod</c> source (ShackLink docs/usrp_b210_support.md section 9.17 P4-a', the RF Bench Analyser's
/// W9): a carrier at <see cref="OffsetHz"/> from the TX centre, amplitude- or frequency-modulated by ONE
/// sine at <see cref="AfHz"/> - the only modulating waveform (<c>caps.tx.modulating_waveforms</c>). The
/// level is the CARRIER's against the DAC's full scale, the convention <see cref="ToneGenerator"/> keeps:
/// an unmodulated carrier of amplitude A has |I + jQ| = A = 32767 x 10^(L/20).
/// <list type="bullet">
/// <item><b>AM</b>: A (1 + m sin(2 pi af t)) at the carrier - the carrier at L, each sideband at
/// L + 20 log10(m / 2), the peak at L + 20 log10(1 + m).</item>
/// <item><b>FM</b>: the phase is the INTEGRAL of the instantaneous frequency offset + dev cos(2 pi af t),
/// taken exactly: beta sin(2 pi af t) with beta = dev / af, on top of the carrier's own phase. So the
/// modulation index is beta at every rate, with no discrete-sum error, and the carrier stands at
/// L + 20 log10|J0(beta)|, the k-th sideband pair at L + 20 log10|Jk(beta)|. The envelope is A: an FM
/// signal's peak is its carrier's level.</item>
/// </list>
/// A signal above full scale, or one whose band leaves the TX rate's Nyquist band, is REFUSED here, never
/// clipped. TxRules refuses it first and more strictly (the usable 80 % of the rate, the AM peak): what
/// reaches this constructor has passed that, so these asserts can only fire on a bug.
/// Phase-continuous across every call.
/// </summary>
public sealed class ModGenerator : IIqGenerator
{
    public enum Kind
    {
        Am,
        Fm,
    }

    private const int RenormEvery = 1024;

    private readonly double _amplitude;
    private readonly double _stepRe, _stepIm;   // the carrier's rotation per sample
    private readonly double _modStep;           // the modulating sine's phase per sample, radians
    private double _re = 1.0, _im = 0.0;        // the carrier's phasor
    private double _modPhase;                   // [0, 2 pi)
    private int _sinceRenorm;

    public Kind Modulation { get; }
    public double OffsetHz { get; }
    public double RateHz { get; }
    public double LevelDbfs { get; }
    public double AfHz { get; }

    /// <summary>AM: the depth m, 0 &lt; m &lt;= 1 (100 % is 1). 0 for FM.</summary>
    public double AmDepth { get; }

    /// <summary>FM: the peak deviation, Hz. 0 for AM.</summary>
    public double FmDeviationHz { get; }

    /// <summary>FM: the modulation index dev / af. 0 for AM.</summary>
    public double Beta => Modulation == Kind.Fm ? FmDeviationHz / AfHz : 0;

    /// <summary>The one-sided half-width around the carrier that TxRules sizes a rate by: af for AM,
    /// Carson's dev + af for FM.</summary>
    public double HalfBandwidthHz => Modulation == Kind.Am ? AfHz : FmDeviationHz + AfHz;

    /// <summary>An AM carrier's peak, dBFS: <c>level + 20 log10(1 + m)</c> - TxRules.AmPeakDbfs, with m a
    /// fraction.</summary>
    public static double AmPeakDbfs(double levelDbfs, double depth) => levelDbfs + 20.0 * Math.Log10(1.0 + depth);

    public static ModGenerator Am(double offsetHz, double rateHz, double levelDbfs, double afHz, double depth) =>
        new(Kind.Am, offsetHz, rateHz, levelDbfs, afHz, depth, 0);

    public static ModGenerator Fm(double offsetHz, double rateHz, double levelDbfs, double afHz, double deviationHz) =>
        new(Kind.Fm, offsetHz, rateHz, levelDbfs, afHz, 0, deviationHz);

    private ModGenerator(Kind kind, double offsetHz, double rateHz, double levelDbfs, double afHz, double depth, double deviationHz)
    {
        if (!(rateHz > 0)) throw new ArgumentOutOfRangeException(nameof(rateHz));
        if (!(afHz > 0)) throw new ArgumentOutOfRangeException(nameof(afHz), "the modulating frequency must be above 0");
        if (kind == Kind.Am)
        {
            if (!(depth > 0 && depth <= 1)) throw new ArgumentOutOfRangeException(nameof(depth), "an AM depth must be above 0 and at most 100 %");
            if (!(AmPeakDbfs(levelDbfs, depth) <= 1e-9))
                throw new ArgumentOutOfRangeException(nameof(levelDbfs), "an AM carrier whose peak is above digital full scale is refused, never clipped");
        }
        else
        {
            if (!(deviationHz > 0)) throw new ArgumentOutOfRangeException(nameof(deviationHz), "an FM deviation must be above 0");
            if (!(levelDbfs <= 0)) throw new ArgumentOutOfRangeException(nameof(levelDbfs), "a carrier above digital full scale is refused, never clipped");
        }
        Modulation = kind;
        OffsetHz = offsetHz;
        RateHz = rateHz;
        LevelDbfs = levelDbfs;
        AfHz = afHz;
        AmDepth = kind == Kind.Am ? depth : 0;
        FmDeviationHz = kind == Kind.Fm ? deviationHz : 0;
        if (!(Math.Abs(offsetHz) + HalfBandwidthHz < rateHz / 2))
            throw new ArgumentOutOfRangeException(nameof(offsetHz), "the modulated signal must lie inside the TX rate's Nyquist band");
        _amplitude = 32767.0 * Math.Pow(10, levelDbfs / 20.0);
        double w = 2.0 * Math.PI * offsetHz / rateHz;
        _stepRe = Math.Cos(w);
        _stepIm = Math.Sin(w);
        _modStep = 2.0 * Math.PI * afHz / rateHz;
    }

    /// <summary>Write <paramref name="count"/> samples, interleaved I, Q.</summary>
    public void Fill(Span<short> iq, int count)
    {
        double a = _amplitude, pr = _re, pi = _im, sr = _stepRe, si = _stepIm, mp = _modPhase, ms = _modStep;
        const double TwoPi = 2.0 * Math.PI;
        bool am = Modulation == Kind.Am;
        double m = AmDepth, beta = Beta;
        for (int n = 0; n < count; n++)
        {
            double s = Math.Sin(mp);
            double ore, oim;
            if (am)
            {
                double env = a * (1.0 + m * s);
                ore = env * pr;
                oim = env * pi;
            }
            else
            {
                // The carrier's phasor turned by beta sin(theta): the exact integral of the deviation.
                double psi = beta * s;
                double c = Math.Cos(psi), d = Math.Sin(psi);
                ore = a * (pr * c - pi * d);
                oim = a * (pr * d + pi * c);
            }
            // Clamped, never wrapped: at the full-scale peak a hair of rounding would reach 32768.
            iq[2 * n] = (short)Math.Clamp(Math.Round(ore), -32767, 32767);
            iq[2 * n + 1] = (short)Math.Clamp(Math.Round(oim), -32767, 32767);
            double npr = pr * sr - pi * si;
            pi = pr * si + pi * sr;
            pr = npr;
            mp += ms;
            if (mp >= TwoPi) mp -= TwoPi;
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
        _modPhase = mp;
    }
}
