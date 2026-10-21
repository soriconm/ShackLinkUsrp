// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;

namespace ShackLinkUsrp.Dsp;

/// <summary>
/// A lease's channel filter: a Kaiser-windowed sinc low-pass evaluated only at every D-th input
/// (polyphase in effect - the cost is <see cref="TapsPerPhase"/> complex x real MACs per INPUT
/// sample whatever D is). The input is copied in a block after the last L - 1 samples, so the window a dot
/// product reads is always contiguous, and the dot products run as <see cref="Vector{T}"/>; the tail moves to the
/// front once per call, not a sample at a time.
/// <para>Design (docs/usrp_b210_support.md section 5.4): the -6 dB edge sits at the output
/// Nyquist, Kaiser beta 8. MEASURED (ShackLink docs/skimmer_support.md 26.13-26.14): at 16 taps a phase nothing folds
/// in above -80 dB over the central 66 % of the output band (-71 dB at 69 %, -42 at 75 %); at 24, over 78 %. A lease
/// runs this as the last stage of a <see cref="LeaseDecimator"/>, at 24.</para>
/// </summary>
public sealed class DecimatingFir
{
    public const int TapsPerPhase = 16;
    private const double KaiserBeta = 8.0;
    // The design's own length, before the padding to a SIMD width (which adds zeros at the OLDEST end and so moves
    // nothing in time).
    private readonly int _designTaps;

    private readonly float[] _taps;     // time-reversed, padded to a multiple of the SIMD width
    private float[] _hre;               // the last L - 1 inputs, then the block being processed
    private float[] _him;
    private readonly int _len;          // L = padded tap count
    private int _phase;                 // inputs since the last output

    public int Decimation { get; }
    public int TapCount => _len;
    public int TapsPerPhaseUsed { get; }

    /// <summary>The group delay in INPUT samples: the centre of the symmetric design, tapsPerPhase x D / 2.
    /// Output j is completed by input (j + 1) D - 1 and is centred on input (j + 1) D - 1 - GroupDelay.</summary>
    public int GroupDelay => (_designTaps - 1) / 2;

    public DecimatingFir(int decimation) : this(decimation, TapsPerPhase) { }

    /// <summary>A filter of <paramref name="tapsPerPhase"/> taps a phase (even, 16 or more): the same Kaiser
    /// beta and cutoff, so a longer one has the same -6 dB edge and a narrower transition.</summary>
    public DecimatingFir(int decimation, int tapsPerPhase)
    {
        if (decimation < 1) throw new ArgumentOutOfRangeException(nameof(decimation));
        if (tapsPerPhase < 2 || tapsPerPhase % 2 != 0) throw new ArgumentOutOfRangeException(nameof(tapsPerPhase), "an even number of taps a phase");
        Decimation = decimation;
        TapsPerPhaseUsed = tapsPerPhase;
        int designTaps = decimation == 1 ? 1 : tapsPerPhase * decimation + 1;
        _designTaps = designTaps;
        // Padded to a multiple of TWO vectors: the dot product runs two accumulators per component (see Process).
        int vw = 2 * Vector<float>.Count;
        _len = (designTaps + vw - 1) / vw * vw;
        var h = decimation == 1 ? new[] { 1.0 } : Design(designTaps, 0.5 / decimation);
        _taps = new float[_len];
        // Reverse into the tail of the padded array: leading zeros multiply the oldest history.
        for (int i = 0; i < h.Length; i++) _taps[_len - 1 - i] = (float)h[i];
        _hre = new float[2 * _len];
        _him = new float[2 * _len];
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
        if (count <= 0) return 0;
        int hist = _len - 1;
        if (_hre.Length < hist + count)
        {
            int cap = Math.Max(hist + count, 2 * _hre.Length);
            Array.Resize(ref _hre, cap);
            Array.Resize(ref _him, cap);
        }
        re.Slice(0, count).CopyTo(_hre.AsSpan(hist));
        im.Slice(0, count).CopyTo(_him.AsSpan(hist));
        int produced = 0;
        int vw = Vector<float>.Count;
        // Input n of this block is at hist + n; an output completes on every D-th input, the first one D - phase - 1
        // into the block. Its window, oldest first, is the L samples ending there: [n, n + L - 1].
        for (int n = Decimation - _phase - 1; n < count; n += Decimation)
        {
            // Two independent accumulators per component: one alone makes every multiply-add wait for the last
            // (a latency chain the length of the filter).
            var r0 = Vector<float>.Zero;
            var r1 = Vector<float>.Zero;
            var i0 = Vector<float>.Zero;
            var i1 = Vector<float>.Zero;
            for (int k = 0; k < _len; k += 2 * vw)
            {
                var t0 = new Vector<float>(_taps, k);
                var t1 = new Vector<float>(_taps, k + vw);
                r0 = Vector.FusedMultiplyAdd(t0, new Vector<float>(_hre, n + k), r0);
                i0 = Vector.FusedMultiplyAdd(t0, new Vector<float>(_him, n + k), i0);
                r1 = Vector.FusedMultiplyAdd(t1, new Vector<float>(_hre, n + k + vw), r1);
                i1 = Vector.FusedMultiplyAdd(t1, new Vector<float>(_him, n + k + vw), i1);
            }
            outRe[outOffset + produced] = Vector.Sum(r0 + r1);
            outIm[outOffset + produced] = Vector.Sum(i0 + i1);
            produced++;
        }
        _phase = (int)((_phase + (long)count) % Decimation);
        // The newest L - 1 inputs become the history.
        Array.Copy(_hre, count, _hre, 0, hist);
        Array.Copy(_him, count, _him, 0, hist);
        return produced;
    }

    public void Reset()
    {
        Array.Clear(_hre);
        Array.Clear(_him);
        _phase = 0;
    }
}
