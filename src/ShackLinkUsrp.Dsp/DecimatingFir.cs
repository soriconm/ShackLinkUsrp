// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;

namespace ShackLinkUsrp.Dsp;

/// <summary>
/// A lease's channel filter: a Kaiser-windowed sinc low-pass evaluated only at every D-th input
/// (polyphase in effect - the cost is <see cref="TapsPerPhase"/> complex x real MACs per INPUT
/// sample whatever D is). The input is kept in a doubled circular history so the window a dot
/// product reads is always contiguous, and the dot products run as <see cref="Vector{T}"/>.
/// <para>Design (docs/usrp_b210_support.md section 5.4): the -6 dB edge sits at the output
/// Nyquist, so the alias-free band is the central ~69 % of the lease at 16 taps per phase and
/// Kaiser beta 8 (~ -80 dB stopband). A client that needs a wider clean band leases a wider rate.</para>
/// </summary>
public sealed class DecimatingFir
{
    public const int TapsPerPhase = 16;
    private const double KaiserBeta = 8.0;

    private readonly float[] _taps;     // time-reversed, padded to a multiple of the SIMD width
    private readonly float[] _hre;      // doubled history, length 2L
    private readonly float[] _him;
    private readonly int _len;          // L = padded tap count
    private int _pos;                   // index of the newest sample in the first copy
    private int _phase;                 // inputs since the last output

    public int Decimation { get; }
    public int TapCount => _len;

    public DecimatingFir(int decimation)
    {
        if (decimation < 1) throw new ArgumentOutOfRangeException(nameof(decimation));
        Decimation = decimation;
        int designTaps = decimation == 1 ? 1 : TapsPerPhase * decimation + 1;
        int vw = Vector<float>.Count;
        _len = (designTaps + vw - 1) / vw * vw;
        var h = decimation == 1 ? new[] { 1.0 } : Design(designTaps, 0.5 / decimation);
        _taps = new float[_len];
        // Reverse into the tail of the padded array: leading zeros multiply the oldest history.
        for (int i = 0; i < h.Length; i++) _taps[_len - 1 - i] = (float)h[i];
        _hre = new float[2 * _len];
        _him = new float[2 * _len];
        _pos = _len - 1;
    }

    /// <summary>Kaiser-windowed sinc, cutoff <paramref name="cutoff"/> in cycles per sample,
    /// normalised to unit DC gain so a full-scale input stays full scale at the output.</summary>
    public static double[] Design(int taps, double cutoff)
    {
        var h = new double[taps];
        double m = (taps - 1) / 2.0;
        double i0b = BesselI0(KaiserBeta);
        double sum = 0;
        for (int n = 0; n < taps; n++)
        {
            double t = n - m;
            double sinc = t == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * t) / (Math.PI * t);
            double r = m == 0 ? 0 : t / m;
            double w = BesselI0(KaiserBeta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0b;
            h[n] = sinc * w;
            sum += h[n];
        }
        for (int n = 0; n < taps; n++) h[n] /= sum;
        return h;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 64; k++)
        {
            term *= q / (k * k);
            sum += term;
            if (term < 1e-17 * sum) break;
        }
        return sum;
    }

    /// <summary>Push <paramref name="count"/> complex inputs; write each completed output into
    /// outRe/outIm starting at <paramref name="outOffset"/>. Returns the outputs written.</summary>
    public int Process(ReadOnlySpan<float> re, ReadOnlySpan<float> im, int count, float[] outRe, float[] outIm, int outOffset)
    {
        int produced = 0;
        int vw = Vector<float>.Count;
        for (int n = 0; n < count; n++)
        {
            _pos++;
            if (_pos >= _len) _pos = 0;
            _hre[_pos] = re[n];
            _hre[_pos + _len] = re[n];
            _him[_pos] = im[n];
            _him[_pos + _len] = im[n];
            if (++_phase < Decimation) continue;
            _phase = 0;
            // The L most recent samples, oldest first, are contiguous at [pos+1, pos+L].
            int start = _pos + 1;
            var accR = Vector<float>.Zero;
            var accI = Vector<float>.Zero;
            for (int k = 0; k < _len; k += vw)
            {
                var t = new Vector<float>(_taps, k);
                accR += t * new Vector<float>(_hre, start + k);
                accI += t * new Vector<float>(_him, start + k);
            }
            outRe[outOffset + produced] = Vector.Dot(accR, Vector<float>.One);
            outIm[outOffset + produced] = Vector.Dot(accI, Vector<float>.One);
            produced++;
        }
        return produced;
    }

    public void Reset()
    {
        Array.Clear(_hre);
        Array.Clear(_him);
        _phase = 0;
    }
}
