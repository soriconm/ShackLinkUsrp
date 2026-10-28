// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// The W1700K conditioner as it ran on the unit before it was batched (ShackLinkUsrp 5c7f1fe, verified live 2026-10-20 and -27): one
// slice at a time on one thread. FROZEN as the gate's reference - the batched, shared conditioner must give these samples bit for bit
// (W1700kConditionBatchCases). Never optimise or "fix" this copy: it is the definition of right.

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using ShackLinkUsrp.Radio;

namespace ShackLinkUsrp.Tests;

internal sealed class W1700kConditionReference
{
    private const int Delay = W1700kCondition.Delay;
    private readonly int _block;
    private readonly float[] _work;
    private readonly float[] _out;
    private float[]? _taps;
    private double _rateHz;
    private double _dcI, _dcQ;
    private bool _primed;

    public W1700kConditionReference(int blockSamples = W1700kExport.SamplesPerSlice)
    {
        _block = blockSamples;
        _work = new float[2 * (2 * Delay + _block) + 32];
        _out = new float[2 * _block + 32];
    }

    public bool Flatten = true;
    public bool RemoveDc = true;
    public long Clipped;

    public double RateHz
    {
        get => _rateHz;
        set
        {
            if (value == _rateHz) return;
            _rateHz = value;
            _taps = W1700kCondition.TapsFor(value);
            _primed = false;
        }
    }

    public void Reset() => _primed = false;

    public void Process(Span<short> iq)
    {
        bool flatten = Flatten && _taps != null, dc = RemoveDc;
        if (!flatten && !dc) return;
        int n2 = 2 * _block;
        int hist = 4 * Delay;
        if (flatten)
        {
            if (!_primed)
            {
                Array.Clear(_work, 0, hist);
                _primed = true;
            }
            ToFloat(iq, _work.AsSpan(hist, n2));
            Fir(_taps!, _work, _out, n2);
            Array.Copy(_work, n2, _work, 0, hist);
        }
        else ToFloat(iq, _out.AsSpan(0, n2));
        long clipped = 0;
        if (!dc) clipped = ToShort(_out, 0, iq, 0, 0, out _, out _);
        else
        {
            double share = W1700kCondition.DcShare(_rateHz, W1700kCondition.DcSubSamples);
            int step = 2 * W1700kCondition.DcSubSamples;
            for (int at = 0; at < n2; at += step)
            {
                int len = Math.Min(step, n2 - at);
                clipped += ToShort(_out, at, iq.Slice(at, len), (float)_dcI, (float)_dcQ, out double sumI, out double sumQ);
                _dcI += share * (sumI / (len / 2) - _dcI);
                _dcQ += share * (sumQ / (len / 2) - _dcQ);
            }
        }
        Clipped += clipped;
    }

    private static void ToFloat(ReadOnlySpan<short> s, Span<float> d)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 16 <= s.Length; i += 16)
            {
                var (lo, hi) = Vector256.Widen(Vector256.Create(s.Slice(i, 16)));
                Vector256.ConvertToSingle(lo).CopyTo(d.Slice(i, 8));
                Vector256.ConvertToSingle(hi).CopyTo(d.Slice(i + 8, 8));
            }
        }
        for (; i < s.Length; i++) d[i] = s[i];
    }

    private static long ToShort(float[] src, int at, Span<short> dst, float subI, float subQ, out double sumI, out double sumQ)
    {
        int n = dst.Length, i = 0;
        long clipped = 0;
        sumI = 0;
        sumQ = 0;
        if (Vector256.IsHardwareAccelerated && Avx.IsSupported)
        {
            ref float s = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), at);
            var sub = Vector256.Create(subI, subQ, subI, subQ, subI, subQ, subI, subQ);
            var max = Vector256.Create(32767f);
            var min = Vector256.Create(-32768f);
            var sum = Vector256<float>.Zero;
            for (; i + 16 <= n; i += 16)
            {
                var a = Vector256.LoadUnsafe(ref s, (nuint)i);
                var b = Vector256.LoadUnsafe(ref s, (nuint)(i + 8));
                sum += a + b;
                a -= sub;
                b -= sub;
                uint over = Vector256.ExtractMostSignificantBits(Vector256.GreaterThan(a, max) | Vector256.LessThan(a, min)) |
                            Vector256.ExtractMostSignificantBits(Vector256.GreaterThan(b, max) | Vector256.LessThan(b, min)) << 8;
                clipped += BitOperations.PopCount(over);
                var ia = Vector256.ConvertToInt32(Avx.RoundToNearestInteger(Vector256.Min(Vector256.Max(a, min), max)));
                var ib = Vector256.ConvertToInt32(Avx.RoundToNearestInteger(Vector256.Min(Vector256.Max(b, min), max)));
                Vector256.Narrow(ia, ib).CopyTo(dst.Slice(i, 16));
            }
            for (int k = 0; k < 8; k += 2)
            {
                sumI += sum.GetElement(k);
                sumQ += sum.GetElement(k + 1);
            }
        }
        for (; i < n; i += 2)
        {
            sumI += src[at + i];
            sumQ += src[at + i + 1];
            dst[i] = Sat(MathF.Round(src[at + i] - subI), ref clipped);
            dst[i + 1] = Sat(MathF.Round(src[at + i + 1] - subQ), ref clipped);
        }
        return clipped;
    }

    private static short Sat(float v, ref long clipped)
    {
        if (v > short.MaxValue) { clipped++; return short.MaxValue; }
        if (v < short.MinValue) { clipped++; return short.MinValue; }
        return (short)v;
    }

    private static void Fir(float[] taps, float[] work, float[] output, int n2)
    {
        const int Taps = W1700kCondition.Taps;
        ref float x = ref MemoryMarshal.GetArrayDataReference(work);
        ref float o = ref MemoryMarshal.GetArrayDataReference(output);
        ref float t = ref MemoryMarshal.GetArrayDataReference(taps);
        int f = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; f + 32 <= n2; f += 32)
            {
                var c = Vector256.Create(Unsafe.Add(ref t, Delay));
                nuint m = (nuint)(f + 2 * Delay);
                var a0 = c * Vector256.LoadUnsafe(ref x, m);
                var a1 = c * Vector256.LoadUnsafe(ref x, m + 8);
                var a2 = c * Vector256.LoadUnsafe(ref x, m + 16);
                var a3 = c * Vector256.LoadUnsafe(ref x, m + 24);
                for (int k = 0; k < Delay; k++)
                {
                    var h = Vector256.Create(Unsafe.Add(ref t, k));
                    nuint lo = (nuint)(f + 2 * k), hi = (nuint)(f + 2 * (Taps - 1 - k));
                    a0 = Vector256.FusedMultiplyAdd(h, Vector256.LoadUnsafe(ref x, lo) + Vector256.LoadUnsafe(ref x, hi), a0);
                    a1 = Vector256.FusedMultiplyAdd(h, Vector256.LoadUnsafe(ref x, lo + 8) + Vector256.LoadUnsafe(ref x, hi + 8), a1);
                    a2 = Vector256.FusedMultiplyAdd(h, Vector256.LoadUnsafe(ref x, lo + 16) + Vector256.LoadUnsafe(ref x, hi + 16), a2);
                    a3 = Vector256.FusedMultiplyAdd(h, Vector256.LoadUnsafe(ref x, lo + 24) + Vector256.LoadUnsafe(ref x, hi + 24), a3);
                }
                a0.StoreUnsafe(ref o, (nuint)f);
                a1.StoreUnsafe(ref o, (nuint)f + 8);
                a2.StoreUnsafe(ref o, (nuint)f + 16);
                a3.StoreUnsafe(ref o, (nuint)f + 24);
            }
        }
        for (; f < n2; f++)
        {
            float acc = taps[Delay] * work[f + 2 * Delay];
            for (int k = 0; k < Delay; k++) acc += taps[k] * (work[f + 2 * k] + work[f + 2 * (Taps - 1 - k)]);
            output[f] = acc;
        }
    }
}
