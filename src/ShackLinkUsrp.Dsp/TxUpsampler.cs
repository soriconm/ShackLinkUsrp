// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;

namespace ShackLinkUsrp.Dsp;

/// <summary>
/// Raises sc16 IQ by an EXACT integer factor (ShackLink docs/plutosdr_support.md section 9.21, the owner's Q4 of section 9.20): a
/// Pluto-class board's TX DMA runs right only at the wide rate, so a transmission a client sends at wide/8 is interpolated x8 HERE -
/// the job the board's HDL interpolator says it does and, on the owner's board, does not. A Kaiser-windowed NYQUIST FIR, polyphase:
/// <list type="bullet">
/// <item><b>Every <see cref="Factor"/>-th output IS an input sample, exactly</b>: the filter's zeros fall on every L-th tap from its
/// centre, so the zero-phase branch is one tap of exactly 1 - the input passes through bit for bit, <see cref="DelayInput"/> input
/// samples late.</item>
/// <item><b>Images are the stopband</b>: a signal reaching 0.4 of the input rate puts its first image at 0.6, the stopband's edge,
/// computed at -86 dB there (<see cref="HalfLength"/> 14, beta 8.5) - below the 12-bit DAC's own ~74 dB. The image rejection was
/// the HDL interpolator's job; it is ours now, and the sidecar's gate MEASURES it in what the board was sent.</item>
/// <item><b>Phase-continuous</b>: the history is carried from call to call for the transmission's life, so a block boundary is
/// invisible in the output.</item>
/// </list>
/// An output that would exceed sc16 full scale (an inter-sample peak of a full-scale input) is clipped and COUNTED
/// (<see cref="Clipped"/>), never silently.
/// </summary>
public sealed class TxUpsampler
{
    /// <summary>Input samples either side of the filter's centre: its taps span 2 x 14 x L + 1 output samples.</summary>
    public const int HalfLength = 14;

    /// <summary>The Kaiser window's beta: with <see cref="HalfLength"/> 14 at L = 8, -86 dB at 0.6 of the input rate (computed).</summary>
    public const double KaiserBeta = 8.5;

    /// <summary>Taps per branch, padded to a multiple of 16 so every vector width divides it.</summary>
    private const int Padded = 32;

    private readonly float[] _taps;     // [branch * Padded + j], j over the history window, oldest first
    private readonly float[] _hi = new float[2 * Padded];
    private readonly float[] _hq = new float[2 * Padded];
    private int _pos;
    private int _quietRun;
    private long _clipped;

    public TxUpsampler(int factor)
    {
        if (factor < 2 || factor > 64) throw new ArgumentOutOfRangeException(nameof(factor), "an interpolation factor of 2 to 64");
        Factor = factor;
        int L = factor, c = HalfLength * L, n = 2 * c + 1;
        var h = new double[n];
        double i0b = BesselI0(KaiserBeta);
        for (int i = 0; i < n; i++)
        {
            double x = (i - c) / (double)L;
            double sinc = x == 0 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
            double r = (i - c) / (double)c;
            h[i] = sinc * BesselI0(KaiserBeta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0b;
        }
        // NYQUIST: exactly 1 at the centre and exactly 0 at every other L-th tap - the zero-phase branch is the input itself.
        for (int k = -HalfLength; k <= HalfLength; k++) h[c + k * L] = k == 0 ? 1.0 : 0.0;
        // Branch p's output n*L + p is sum_k h[p + k L] x[n - k]: in the window (oldest first, the newest last) x[n - k] sits at
        // Padded - 1 - k.
        _taps = new float[L * Padded];
        for (int p = 0; p < L; p++)
            for (int k = 0; k <= 2 * HalfLength; k++)
            {
                int i = p + k * L;
                if (i < n) _taps[p * Padded + Padded - 1 - k] = (float)h[i];
            }
    }

    /// <summary>L: output samples per input sample.</summary>
    public int Factor { get; }

    /// <summary>Output sample m*L is input sample m - <see cref="DelayInput"/>, exactly.</summary>
    public static int DelayInput => HalfLength;

    /// <summary>How many input samples it takes for a sample's response to leave the output entirely.</summary>
    public static int TapsPerPhase => 2 * HalfLength + 1;

    /// <summary>The last <see cref="TapsPerPhase"/> inputs were all zero: nothing of an earlier sample is still to come out. A
    /// transmission's end, and a tuned step's LO move, wait for this - the tail of what went before leaves at ITS LO.</summary>
    public bool Settled => _quietRun >= TapsPerPhase;

    /// <summary>Outputs clipped to sc16 full scale, over the transmission's life.</summary>
    public long Clipped => Interlocked.Read(ref _clipped);

    /// <summary>
    /// Interpolate <paramref name="count"/> interleaved sc16 input samples into <paramref name="count"/> x <see cref="Factor"/>
    /// output samples, continuing from the previous call.
    /// </summary>
    public void Process(ReadOnlySpan<short> input, int count, Span<short> output)
    {
        if (input.Length < 2 * count) throw new ArgumentException("the input holds fewer samples than asked", nameof(input));
        if (output.Length < 2 * count * Factor) throw new ArgumentException("the output cannot hold count x factor samples", nameof(output));
        int L = Factor;
        var taps = _taps.AsSpan();
        long clipped = 0;
        for (int n = 0; n < count; n++)
        {
            short xi = input[2 * n], xq = input[2 * n + 1];
            _hi[_pos] = _hi[_pos + Padded] = xi;
            _hq[_pos] = _hq[_pos + Padded] = xq;
            _pos = _pos + 1 == Padded ? 0 : _pos + 1;
            _quietRun = xi == 0 && xq == 0 ? _quietRun + 1 : 0;
            var wi = new ReadOnlySpan<float>(_hi, _pos, Padded);
            var wq = new ReadOnlySpan<float>(_hq, _pos, Padded);
            int o = 2 * n * L;
            // Branch 0 IS the input, HalfLength samples late: copied, never computed, so it is exact by construction.
            output[o] = (short)wi[Padded - 1 - HalfLength];
            output[o + 1] = (short)wq[Padded - 1 - HalfLength];
            for (int p = 1; p < L; p++)
            {
                var t = taps.Slice(p * Padded, Padded);
                output[o + 2 * p] = ToShort(Dot(t, wi), ref clipped);
                output[o + 2 * p + 1] = ToShort(Dot(t, wq), ref clipped);
            }
        }
        if (clipped > 0) Interlocked.Add(ref _clipped, clipped);
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        var acc = Vector<float>.Zero;
        for (int i = 0; i < va.Length; i++) acc += va[i] * vb[i];
        float s = Vector.Dot(acc, Vector<float>.One);
        for (int i = va.Length * Vector<float>.Count; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    private static short ToShort(float v, ref long clipped)
    {
        float r = MathF.Round(v);
        if (r > short.MaxValue)
        {
            clipped++;
            return short.MaxValue;
        }
        if (r < short.MinValue)
        {
            clipped++;
            return short.MinValue;
        }
        return (short)r;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 200; k++)
        {
            term *= q / (k * (double)k);
            sum += term;
            if (term < sum * 1e-17) break;
        }
        return sum;
    }
}
