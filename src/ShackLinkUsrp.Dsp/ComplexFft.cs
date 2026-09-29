// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;

namespace ShackLinkUsrp.Dsp;

/// <summary>
/// An in-place radix-2 complex FFT on split real/imaginary float arrays, sizes 2^4..2^20.
/// Twiddles are stored per STAGE and contiguously, so every stage whose half-length reaches a SIMD
/// width runs its butterflies as <see cref="Vector{T}"/> operations. A GPL program may use FFTW
/// instead (docs/usrp_b210_support.md section 5.2); this one is the dependency-free baseline, and
/// the panorama is self-limiting either way: a pipeline that cannot keep up is lapped by the ring
/// and says so in its rows (<c>dropped</c>, fewer <c>ffts</c>), it never slows the radio.
/// </summary>
public sealed class ComplexFft
{
    private readonly int _n;
    private readonly int _log2;
    private readonly int[] _bitrev;
    private readonly float[][] _stageCos;
    private readonly float[][] _stageSin;

    public ComplexFft(int size)
    {
        if (size < 16 || size > (1 << 20) || (size & (size - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(size), "FFT size must be a power of two in 16..2^20");
        _n = size;
        _log2 = BitOperations.Log2((uint)size);
        _bitrev = new int[size];
        for (int i = 0; i < size; i++)
        {
            int r = 0;
            for (int b = 0; b < _log2; b++)
                if ((i & (1 << b)) != 0) r |= 1 << (_log2 - 1 - b);
            _bitrev[i] = r;
        }
        _stageCos = new float[_log2][];
        _stageSin = new float[_log2][];
        for (int s = 0; s < _log2; s++)
        {
            int half = 1 << s;
            int len = half * 2;
            var c = new float[half];
            var sn = new float[half];
            for (int k = 0; k < half; k++)
            {
                double a = -2.0 * Math.PI * k / len;
                c[k] = (float)Math.Cos(a);
                sn[k] = (float)Math.Sin(a);
            }
            _stageCos[s] = c;
            _stageSin[s] = sn;
        }
    }

    public int Size => _n;

    /// <summary>Forward transform (e^-j), unnormalised, in place.</summary>
    public void Forward(float[] re, float[] im)
    {
        if (re.Length < _n || im.Length < _n) throw new ArgumentException("buffers are shorter than the FFT");
        for (int i = 0; i < _n; i++)
        {
            int j = _bitrev[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        int vw = Vector<float>.Count;
        for (int s = 0; s < _log2; s++)
        {
            int half = 1 << s;
            int len = half << 1;
            float[] wc = _stageCos[s];
            float[] ws = _stageSin[s];
            if (half >= vw && Vector.IsHardwareAccelerated)
            {
                for (int start = 0; start < _n; start += len)
                {
                    for (int k = 0; k < half; k += vw)
                    {
                        int a = start + k;
                        int b = a + half;
                        var wr = new Vector<float>(wc, k);
                        var wi = new Vector<float>(ws, k);
                        var br = new Vector<float>(re, b);
                        var bi = new Vector<float>(im, b);
                        var tr = wr * br - wi * bi;
                        var ti = wr * bi + wi * br;
                        var ar = new Vector<float>(re, a);
                        var ai = new Vector<float>(im, a);
                        (ar - tr).CopyTo(re, b);
                        (ai - ti).CopyTo(im, b);
                        (ar + tr).CopyTo(re, a);
                        (ai + ti).CopyTo(im, a);
                    }
                }
            }
            else
            {
                for (int start = 0; start < _n; start += len)
                {
                    for (int k = 0; k < half; k++)
                    {
                        int a = start + k;
                        int b = a + half;
                        float wr = wc[k], wi = ws[k];
                        float tr = wr * re[b] - wi * im[b];
                        float ti = wr * im[b] + wi * re[b];
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                    }
                }
            }
        }
    }
}
