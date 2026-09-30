// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace ShackLinkUsrp.Dsp;

/// <summary>
/// One sinusoid of a modulating wave, <c>Amplitude x sin(2 pi FrequencyHz t + PhaseRad)</c> - the protocol library's
/// <c>TxComponent</c>, restated so this project stays free of the wire. A generator is handed the library's
/// components (<c>TxWaveforms</c>, ShackLink docs/usrp_b210_support.md section 9.21 Q5), scaled so the wave never
/// exceeds 1.
/// </summary>
public readonly record struct ModComponent(double FrequencyHz, double Amplitude, double PhaseRad = 0);

/// <summary>
/// The <c>mod</c> source (ShackLink docs/usrp_b210_support.md sections 9.17 P4-a', 9.21): a carrier at
/// <see cref="OffsetHz"/> from the TX centre, modulated by a WAVE x(t) - a sum of sinusoids (<see cref="ModComponent"/>),
/// one for a sine, a cut Fourier series for a square, triangle or sawtooth, two for a two-tone - scaled by the
/// library so |x| never exceeds 1. The level is the CARRIER's against the DAC's full scale, the convention
/// <see cref="ToneGenerator"/> keeps: an unmodulated carrier of amplitude A has |I + jQ| = A = 32767 x 10^(L/20).
/// <list type="bullet">
/// <item><b>AM</b>: A (1 + m x(t)) at the carrier - each component k's sidebands at L + 20 log10(m a_k / 2), the
/// peak at L + 20 log10(1 + m).</item>
/// <item><b>FM</b>: the instantaneous frequency is offset + dev x(t), and the phase is its INTEGRAL, taken exactly
/// per component: 2 pi dev a_k (cos phi_k - cos(2 pi f_k t + phi_k)) / (2 pi f_k), so it is exact at every rate with
/// no discrete-sum error. For a sine that is beta (1 - cos theta): the carrier at L + 20 log10|J0(beta)|, the k-th
/// pair at L + 20 log10|Jk(beta)|. The envelope is A: an FM signal's peak is its carrier's level.</item>
/// </list>
/// Every component carries its own phasor, rotated each sample and renormalised on the one sample count, so the
/// samples are the same however the calls are cut. A signal above full scale, or one whose band leaves the TX
/// rate's Nyquist band, is REFUSED here, never clipped. TxRules refuses it first and more strictly (the usable 80 %
/// of the rate, the AM peak): what reaches this constructor has passed that, so these asserts can only fire on a bug.
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
    private double _re = 1.0, _im = 0.0;        // the carrier's phasor
    private readonly ModComponent[] _wave;
    private readonly double[] _amp;             // a_k
    private readonly double[] _wRe, _wIm;       // each component's phasor: (cos, sin) of 2 pi f_k t + phi_k
    private readonly double[] _sRe, _sIm;       // each component's rotation per sample
    private readonly double[] _fmCoef;          // FM: dev a_k / f_k, the exact integral's coefficient
    private readonly double _fmStart;           // FM: the sum of dev a_k cos(phi_k) / f_k, so the phase starts at 0
    private int _sinceRenorm;

    public Kind Modulation { get; }
    public double OffsetHz { get; }
    public double RateHz { get; }
    public double LevelDbfs { get; }

    /// <summary>The wave's components, as handed in.</summary>
    public IReadOnlyList<ModComponent> Wave => _wave;

    /// <summary>The lowest component's frequency: a sine's af.</summary>
    public double AfHz { get; }

    /// <summary>The highest component's frequency.</summary>
    public double TopHz { get; }

    /// <summary>AM: the depth m, 0 &lt; m &lt;= 1 (100 % is 1). 0 for FM.</summary>
    public double AmDepth { get; }

    /// <summary>FM: the peak deviation, Hz (the wave peaks at 1). 0 for AM.</summary>
    public double FmDeviationHz { get; }

    /// <summary>FM: the modulation index dev / af of a sine. 0 for AM.</summary>
    public double Beta => Modulation == Kind.Fm ? FmDeviationHz / AfHz : 0;

    /// <summary>The one-sided half-width around the carrier: the highest component for AM, Carson's dev + it for FM.</summary>
    public double HalfBandwidthHz => Modulation == Kind.Am ? TopHz : FmDeviationHz + TopHz;

    /// <summary>An AM carrier's peak, dBFS: <c>level + 20 log10(1 + m)</c> - TxRules.AmPeakDbfs, with m a
    /// fraction. It holds for every wave, because every wave peaks at 1.</summary>
    public static double AmPeakDbfs(double levelDbfs, double depth) => levelDbfs + 20.0 * Math.Log10(1.0 + depth);

    /// <summary>A sine: one component of amplitude 1.</summary>
    public static ModComponent[] Sine(double afHz) => new[] { new ModComponent(afHz, 1.0) };

    public static ModGenerator Am(double offsetHz, double rateHz, double levelDbfs, double afHz, double depth) =>
        Am(offsetHz, rateHz, levelDbfs, Sine(afHz), depth);

    public static ModGenerator Am(double offsetHz, double rateHz, double levelDbfs, IReadOnlyList<ModComponent> wave, double depth) =>
        new(Kind.Am, offsetHz, rateHz, levelDbfs, wave, depth, 0);

    public static ModGenerator Fm(double offsetHz, double rateHz, double levelDbfs, double afHz, double deviationHz) =>
        Fm(offsetHz, rateHz, levelDbfs, Sine(afHz), deviationHz);

    public static ModGenerator Fm(double offsetHz, double rateHz, double levelDbfs, IReadOnlyList<ModComponent> wave, double deviationHz) =>
        new(Kind.Fm, offsetHz, rateHz, levelDbfs, wave, 0, deviationHz);

    private ModGenerator(Kind kind, double offsetHz, double rateHz, double levelDbfs, IReadOnlyList<ModComponent> wave, double depth, double deviationHz)
    {
        if (!(rateHz > 0)) throw new ArgumentOutOfRangeException(nameof(rateHz));
        if (wave == null || wave.Count == 0) throw new ArgumentException("a modulated carrier needs a modulating wave", nameof(wave));
        foreach (var c in wave)
            if (!(c.FrequencyHz > 0)) throw new ArgumentOutOfRangeException(nameof(wave), "every component of the modulating wave must be above 0 Hz");
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
        _wave = wave.ToArray();
        AfHz = _wave.Min(c => c.FrequencyHz);
        TopHz = _wave.Max(c => c.FrequencyHz);
        AmDepth = kind == Kind.Am ? depth : 0;
        FmDeviationHz = kind == Kind.Fm ? deviationHz : 0;
        if (!(Math.Abs(offsetHz) + HalfBandwidthHz < rateHz / 2))
            throw new ArgumentOutOfRangeException(nameof(offsetHz), "the modulated signal must lie inside the TX rate's Nyquist band");
        _amplitude = 32767.0 * Math.Pow(10, levelDbfs / 20.0);
        double w = 2.0 * Math.PI * offsetHz / rateHz;
        _stepRe = Math.Cos(w);
        _stepIm = Math.Sin(w);

        int n = _wave.Length;
        _amp = new double[n];
        _wRe = new double[n];
        _wIm = new double[n];
        _sRe = new double[n];
        _sIm = new double[n];
        _fmCoef = new double[n];
        for (int k = 0; k < n; k++)
        {
            var c = _wave[k];
            _amp[k] = c.Amplitude;
            _wRe[k] = Math.Cos(c.PhaseRad);
            _wIm[k] = Math.Sin(c.PhaseRad);
            double step = 2.0 * Math.PI * c.FrequencyHz / rateHz;
            _sRe[k] = Math.Cos(step);
            _sIm[k] = Math.Sin(step);
            _fmCoef[k] = FmDeviationHz * c.Amplitude / c.FrequencyHz;
            _fmStart += _fmCoef[k] * _wRe[k];
        }
    }

    /// <summary>Write <paramref name="count"/> samples, interleaved I, Q.</summary>
    public void Fill(Span<short> iq, int count)
    {
        double a = _amplitude, pr = _re, pi = _im, sr = _stepRe, si = _stepIm;
        bool am = Modulation == Kind.Am;
        double m = AmDepth;
        int nw = _amp.Length;
        var amp = _amp;
        var wRe = _wRe;
        var wIm = _wIm;
        var sRe = _sRe;
        var sIm = _sIm;
        var coef = _fmCoef;
        for (int n = 0; n < count; n++)
        {
            // The wave (the sum of a_k sin) and, for FM, the cosine sum its exact integral needs; then every
            // component's phasor turns by its own step.
            double x = 0, c = 0;
            for (int k = 0; k < nw; k++)
            {
                double wr = wRe[k], wi = wIm[k];
                x += amp[k] * wi;
                c += coef[k] * wr;
                wRe[k] = wr * sRe[k] - wi * sIm[k];
                wIm[k] = wr * sIm[k] + wi * sRe[k];
            }
            double ore, oim;
            if (am)
            {
                double env = a * (1.0 + m * x);
                ore = env * pr;
                oim = env * pi;
            }
            else
            {
                // The carrier's phasor turned by the integral of dev x(t), taken exactly per component.
                double psi = _fmStart - c;
                double cs = Math.Cos(psi), sn = Math.Sin(psi);
                ore = a * (pr * cs - pi * sn);
                oim = a * (pr * sn + pi * cs);
            }
            // Clamped, never wrapped: at the full-scale peak a hair of rounding would reach 32768.
            iq[2 * n] = (short)Math.Clamp(Math.Round(ore), -32767, 32767);
            iq[2 * n + 1] = (short)Math.Clamp(Math.Round(oim), -32767, 32767);
            double npr = pr * sr - pi * si;
            pi = pr * si + pi * sr;
            pr = npr;
            if (++_sinceRenorm >= RenormEvery)
            {
                double mag = Math.Sqrt(pr * pr + pi * pi);
                pr /= mag;
                pi /= mag;
                for (int k = 0; k < nw; k++)
                {
                    double g = Math.Sqrt(wRe[k] * wRe[k] + wIm[k] * wIm[k]);
                    wRe[k] /= g;
                    wIm[k] /= g;
                }
                _sinceRenorm = 0;
            }
        }
        _re = pr;
        _im = pi;
    }
}
