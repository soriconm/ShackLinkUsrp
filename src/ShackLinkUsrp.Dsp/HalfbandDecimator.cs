// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;

namespace ShackLinkUsrp.Dsp;

/// <summary>
/// One decimate-by-2 stage of a lease's cascade (<see cref="LeaseDecimator"/>): a halfband FIR of 4K - 1 taps whose
/// even taps are zero except the centre (0.5), so an output costs K multiplies after K pre-adds, plus the centre.
/// <para>Polyphase: the input is split into its even samples E and odd samples O, and output m (completed by input
/// 2m + 1) is <c>0.5 E[m + 1 - K] + sum_i g_i (O[m - K + i] + O[m - K + 1 - i])</c>. Every term is a contiguous run
/// of m, so a block of outputs runs as <see cref="Vector{T}"/>. The stage carries K - 1 even and 2K - 1 odd samples
/// of history, and an unpaired last input to the next call.</para>
/// </summary>
public sealed class HalfbandDecimator
{
    private readonly float[] _g;        // K pair coefficients, g_1 nearest the centre
    private readonly int _k;
    private readonly int _histE, _histO;
    private float[] _eRe = Array.Empty<float>(), _eIm = Array.Empty<float>();
    private float[] _oRe = Array.Empty<float>(), _oIm = Array.Empty<float>();
    private bool _pending;
    private float _pRe, _pIm;

    /// <summary>The pair coefficients (g_1 .. g_K) of a halfband whose taps sum to 1: the centre is 0.5 and the
    /// pairs sum to 0.25 each side.</summary>
    public HalfbandDecimator(IReadOnlyList<double> pairs)
    {
        if (pairs == null || pairs.Count < 1) throw new ArgumentException("a halfband has at least one pair", nameof(pairs));
        _k = pairs.Count;
        _g = pairs.Select(v => (float)v).ToArray();
        _histE = _k - 1;
        _histO = 2 * _k - 1;
        Reset();
    }

    public int Pairs => _k;
    public int TapCount => 4 * _k - 1;

    /// <summary>The group delay in this stage's INPUT samples: the centre tap, (N - 1) / 2.</summary>
    public int GroupDelay => 2 * _k - 1;

    public void Reset()
    {
        Array.Clear(_eRe);
        Array.Clear(_eIm);
        Array.Clear(_oRe);
        Array.Clear(_oIm);
        _pending = false;
        _pRe = _pIm = 0;
    }

    // One capacity for all four: the odd history is the longer one.
    private void Ensure(int pairs)
    {
        int need = _histO + pairs;
        if (_oRe.Length >= need) return;
        int cap = Math.Max(need, 2 * _oRe.Length);
        Array.Resize(ref _eRe, cap);
        Array.Resize(ref _eIm, cap);
        Array.Resize(ref _oRe, cap);
        Array.Resize(ref _oIm, cap);
    }

    /// <summary>Push <paramref name="count"/> complex inputs; write one output per completed pair into outRe/outIm
    /// from index 0. Returns the outputs written: (count + an unpaired input carried from the last call) / 2.</summary>
    public int Process(ReadOnlySpan<float> re, ReadOnlySpan<float> im, int count, Span<float> outRe, Span<float> outIm)
    {
        int carried = _pending ? 1 : 0;
        int pairs = (carried + count) / 2;
        Ensure(pairs);
        int n = 0;
        int e = _histE, o = _histO;
        if (_pending && pairs > 0)
        {
            _eRe[e] = _pRe;
            _eIm[e] = _pIm;
            _oRe[o] = re[0];
            _oIm[o] = im[0];
            e++;
            o++;
            n = 1;
            _pending = false;
        }
        // The split: even inputs to E, odd to O. Two floats are one little-endian long, the even one in its low half,
        // so whole vectors split by a narrowing - no shuffle.
        int vw = Vector<float>.Count;
        int vecPairs = Vector.IsHardwareAccelerated ? (count - n) / 2 / vw * vw : 0;
        if (vecPairs > 0)
        {
            SplitPairs(re.Slice(n, 2 * vecPairs), _eRe.AsSpan(e), _oRe.AsSpan(o));
            SplitPairs(im.Slice(n, 2 * vecPairs), _eIm.AsSpan(e), _oIm.AsSpan(o));
            n += 2 * vecPairs;
            e += vecPairs;
            o += vecPairs;
        }
        for (; n + 1 < count && e < _histE + pairs; n += 2)
        {
            _eRe[e] = re[n];
            _eIm[e] = im[n];
            _oRe[o] = re[n + 1];
            _oIm[o] = im[n + 1];
            e++;
            o++;
        }
        if (n < count)
        {
            _pending = true;
            _pRe = re[n];
            _pIm = im[n];
        }

        int m = 0;
        var half = new Vector<float>(0.5f);
        for (; m + vw <= pairs; m += vw)
        {
            var accR = half * new Vector<float>(_eRe, m);
            var accI = half * new Vector<float>(_eIm, m);
            for (int i = 1; i <= _k; i++)
            {
                var g = new Vector<float>(_g[i - 1]);
                int a = m + _k - 1 + i, b = m + _k - i;
                accR += g * (new Vector<float>(_oRe, a) + new Vector<float>(_oRe, b));
                accI += g * (new Vector<float>(_oIm, a) + new Vector<float>(_oIm, b));
            }
            accR.CopyTo(outRe.Slice(m, vw));
            accI.CopyTo(outIm.Slice(m, vw));
        }
        for (; m < pairs; m++)
        {
            float r = 0.5f * _eRe[m], q = 0.5f * _eIm[m];
            for (int i = 1; i <= _k; i++)
            {
                int a = m + _k - 1 + i, b = m + _k - i;
                r += _g[i - 1] * (_oRe[a] + _oRe[b]);
                q += _g[i - 1] * (_oIm[a] + _oIm[b]);
            }
            outRe[m] = r;
            outIm[m] = q;
        }

        // Keep the history: the last K - 1 even and 2K - 1 odd samples move to the front.
        if (pairs > 0)
        {
            Array.Copy(_eRe, pairs, _eRe, 0, _histE);
            Array.Copy(_eIm, pairs, _eIm, 0, _histE);
            Array.Copy(_oRe, pairs, _oRe, 0, _histO);
            Array.Copy(_oIm, pairs, _oIm, 0, _histO);
        }
        return pairs;
    }

    // src holds 2 x (a multiple of the vector width) floats; its even ones go to even[], its odd ones to odd[].
    private static void SplitPairs(ReadOnlySpan<float> src, Span<float> even, Span<float> odd)
    {
        var longs = MemoryMarshal.Cast<float, long>(src);
        int vw = Vector<float>.Count, lw = Vector<long>.Count;
        for (int p = 0, l = 0; l < longs.Length; p += vw, l += 2 * lw)
        {
            var a = new Vector<long>(longs.Slice(l));
            var b = new Vector<long>(longs.Slice(l + lw));
            Vector.AsVectorSingle(Vector.Narrow(a, b)).CopyTo(even.Slice(p, vw));
            Vector.AsVectorSingle(Vector.Narrow(Vector.ShiftRightLogical(a, 32), Vector.ShiftRightLogical(b, 32))).CopyTo(odd.Slice(p, vw));
        }
    }

    // ------------------------------------------------------------------ design

    /// <summary>The zero-phase amplitude of a halfband at <paramref name="nu"/> cycles per input sample:
    /// <c>0.5 + 2 sum_i g_i cos(2 pi nu (2i - 1))</c>.</summary>
    public static double Amplitude(IReadOnlyList<double> pairs, double nu)
    {
        double a = 0.5;
        for (int i = 1; i <= pairs.Count; i++) a += 2 * pairs[i - 1] * Math.Cos(2 * Math.PI * nu * (2 * i - 1));
        return a;
    }

    /// <summary>The largest |amplitude| over the stopband [0.5 - pass, 0.5], dB. By the halfband's symmetry,
    /// A(nu) + A(0.5 - nu) = 1, the passband [0, pass] departs from 1 by the same amounts.</summary>
    public static double StopbandDb(IReadOnlyList<double> pairs, double pass)
    {
        double worst = 0;
        const int grid = 96;
        for (int s = 0; s <= grid; s++)
        {
            double nu = 0.5 - pass + pass * s / grid;
            worst = Math.Max(worst, Math.Abs(Amplitude(pairs, nu)));
        }
        return 20 * Math.Log10(Math.Max(worst, 1e-15));
    }

    /// <summary>The maximally flat halfband of K pairs: the odd taps are half the Lagrange weights that interpolate
    /// the midpoint from the 2K nearest odd samples, so the response has a zero of order 2K at the input Nyquist.
    /// Best where the band to protect is a sliver of the rate.</summary>
    public static double[] Lagrange(int k)
    {
        var x = new double[2 * k];
        for (int i = 0; i < k; i++)
        {
            x[2 * i] = (2 * i + 1) / 2.0;
            x[2 * i + 1] = -(2 * i + 1) / 2.0;
        }
        var g = new double[k];
        for (int i = 0; i < k; i++)
        {
            double w = 1;
            for (int j = 0; j < x.Length; j++)
                if (j != 2 * i) w *= (0 - x[j]) / (x[2 * i] - x[j]);
            g[i] = 0.5 * w;
        }
        return g;
    }

    /// <summary>A Kaiser-windowed halfband of K pairs, its pairs scaled to sum to 0.25 (unit DC gain, exact zero at
    /// the input Nyquist). Best where the band to protect is a sizeable part of the rate.</summary>
    public static double[] Kaiser(int k, double beta)
    {
        int half = 2 * k - 1;
        double i0b = BesselI0(beta);
        var g = new double[k];
        double sum = 0;
        for (int i = 1; i <= k; i++)
        {
            int n = 2 * i - 1;
            double r = (double)n / (half + 1);
            double w = BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0b;
            g[i - 1] = Math.Sin(Math.PI * n / 2) / (Math.PI * n) * w;
            sum += g[i - 1];
        }
        for (int i = 0; i < k; i++) g[i] *= 0.25 / sum;
        return g;
    }

    /// <summary>
    /// The shortest halfband whose stopband [0.5 - pass, 0.5] (cycles per input sample) is at or below
    /// <paramref name="targetDb"/>: the Lagrange design and Kaiser windows from beta 0 to 16 are tried at each K, and
    /// the deepest wins. Null when no K up to <paramref name="maxPairs"/> reaches the target.
    /// </summary>
    public static double[]? Design(double pass, double targetDb, int maxPairs = 24)
    {
        if (pass <= 0 || pass >= 0.25) return null;
        for (int k = 1; k <= maxPairs; k++)
        {
            double[]? best = null;
            double bestDb = double.MaxValue;
            void Try(double[] g)
            {
                double db = StopbandDb(g, pass);
                if (db < bestDb) { bestDb = db; best = g; }
            }
            Try(Lagrange(k));
            for (double beta = 0; beta <= 16.0001; beta += 0.25) Try(Kaiser(k, beta));
            if (bestDb <= targetDb) return best;
        }
        return null;
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
}
