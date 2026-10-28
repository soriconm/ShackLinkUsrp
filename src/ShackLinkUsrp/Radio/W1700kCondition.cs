// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// The roll-off tables and the FIR design are ported from h0m3us3r/w1700k-sdr host/stream/src/flatten.cpp (MIT, Copyright (c)
// the w1700k-sdr contributors), at upstream commit 0b0413e.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace ShackLinkUsrp.Radio;

/// <summary>
/// What the W1700K's samples need before the ring, both on by default and each a setting (C:\Dev\SDRW1700K docs/05; the
/// SDRW1700K session's measurements of 2026-10-20):
/// <list type="bullet">
/// <item><b>Flatten</b>: each rate's decimation rolls the band's edges off, signal and noise alike - -2 dB at 62 % of Nyquist,
/// -13 at 87 %, -21 at the edge at 40 MS/s. Upstream's correction, as upstream designs it: a 129-tap linear-phase FIR whose
/// response is the inverse of the roll-off measured on its unit A (band 1), capped at +24 dB, applied ZERO-PHASE (output t from
/// inputs t-64..t+64). In the TIME domain, so a lease near an edge is corrected as well as the panorama - which is why it is here and
/// not a per-bin correction of the rows.</item>
/// <item><b>DC removal</b>: the LO's leak is a spike at the centre (-37 to -42 dBFS measured, and WANDERING). A one-pole DC blocker
/// with a corner of <see cref="DcCornerTargetHz"/> takes it out and leaves a signal a kilohertz from the LO alone.</item>
/// </list>
/// One instance per stream; it is fed the receive thread's slices in order and keeps 128 samples of history across them. A gap
/// (lost completions) is filtered across as if contiguous - the samples were lost anyway, and the ring says where.
/// </summary>
internal sealed class W1700kCondition
{
    // dB at |f| = k/64 of Nyquist, k = 0..64 (upstream flatten.cpp kRolloff40..320).
    private static readonly float[] Rolloff40 =
    {
          0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,
          0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,
          0.0f,   0.0f,   0.0f,  -0.6f,  -1.1f,  -1.3f,  -1.5f,  -1.8f,  -2.1f,  -2.4f,  -2.7f,  -3.1f,  -3.5f,
         -3.9f,  -4.4f,  -4.9f,  -5.4f,  -5.9f,  -6.5f,  -7.2f,  -7.8f,  -8.5f,  -9.2f, -10.0f, -10.7f, -11.6f,
        -12.4f, -13.3f, -14.2f, -15.1f, -16.0f, -16.9f, -17.7f, -18.5f, -19.2f, -19.8f, -20.2f, -20.5f, -20.7f,
    };
    private static readonly float[] Rolloff80 =
    {
          0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,
          0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,
          0.0f,   0.0f,   0.0f,  -0.4f,  -0.7f,  -0.9f,  -1.1f,  -1.3f,  -1.6f,  -1.9f,  -2.2f,  -2.6f,  -3.0f,
         -3.4f,  -3.8f,  -4.3f,  -4.8f,  -5.3f,  -5.9f,  -6.5f,  -7.1f,  -7.8f,  -8.5f,  -9.3f, -10.1f, -11.0f,
        -11.8f, -12.8f, -13.8f, -14.8f, -15.9f, -17.0f, -18.2f, -19.3f, -20.5f, -21.5f, -22.4f, -23.1f, -23.4f,
    };
    private static readonly float[] Rolloff160 =
    {
          0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,
          0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,
          0.0f,   0.0f,   0.0f,  -0.3f,  -0.6f,  -0.8f,  -1.0f,  -1.2f,  -1.5f,  -1.7f,  -2.1f,  -2.4f,  -2.8f,
         -3.1f,  -3.6f,  -4.1f,  -4.6f,  -5.1f,  -5.7f,  -6.3f,  -6.9f,  -7.6f,  -8.4f,  -9.1f,  -9.9f, -10.8f,
        -11.7f, -12.7f, -13.7f, -14.8f, -15.9f, -17.1f, -18.4f, -19.7f, -21.0f, -22.3f, -23.4f, -24.2f, -24.7f,
    };
    private static readonly float[] Rolloff320 =
    {
          0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,
          0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,   0.0f,
          0.0f,   0.0f,   0.0f,  -0.1f,  -0.2f,  -0.3f,  -0.5f,  -0.6f,  -0.8f,  -1.1f,  -1.4f,  -1.7f,  -2.0f,
         -2.4f,  -2.8f,  -3.2f,  -3.6f,  -4.1f,  -4.6f,  -5.1f,  -5.7f,  -6.3f,  -6.9f,  -7.6f,  -8.4f,  -9.2f,
        -10.1f, -11.0f, -12.0f, -13.0f, -14.0f, -15.2f, -16.4f, -17.8f, -19.1f, -20.5f, -21.7f, -22.6f, -23.1f,
    };

    public const int Taps = 129;
    public const int Delay = (Taps - 1) / 2;
    public const double MaxGainDb = 24;
    private const int DesignPoints = 8192;
    private const double KaiserBeta = 4;

    /// <summary>
    /// The DC blocker's corner, the same at every rate. MEASURED on the unit (2026-10-20, band 0, AGC, 610 Hz bins): the leak is a narrow
    /// line at -42 dBFS that WANDERS - a 6 Hz corner took 16 dB off it and left -58 dBFS in the same narrow shape, 18 dB over the floor
    /// in ShackLink's 1.2 kHz bins, because what it follows faster than 6 Hz (the AGC's steps among it) passed straight through. 200 Hz
    /// follows that and stays far narrower than a display bin; the price, said where the setting is offered, is a notch of a few
    /// hundred hertz at the LO (a sub-receiver tuned exactly there loses it). It settles a retune's new leak in about 4 ms.
    /// </summary>
    public const double DcCornerTargetHz = 200;
    public static double DcCornerHz(double rateHz) => DcCornerTargetHz;

    /// <summary>The estimate's step per block: the block's share of the one-pole's per-sample coefficient 2 pi fc / rate, capped at 1.</summary>
    internal static double DcShare(double rateHz, int blockSamples) =>
        rateHz > 0 ? Math.Min(1.0, 2 * Math.PI * DcCornerTargetHz / rateHz * blockSamples) : 1.0;

    private static readonly Dictionary<long, float[]> Designed = new();

    /// <summary>The measured roll-off for a rate, or null for a rate upstream has none for.</summary>
    internal static float[]? RolloffFor(double rateHz) => Math.Round(rateHz) switch
    {
        40e6 => Rolloff40,
        80e6 => Rolloff80,
        160e6 => Rolloff160,
        320e6 => Rolloff320,
        _ => null,
    };

    /// <summary>The roll-off at |f| = x Nyquist, dB, linearly interpolated (upstream's <c>interpolate</c>).</summary>
    internal static double RolloffDb(float[] table, double x)
    {
        x = Math.Clamp(x, 0, 1) * (table.Length - 1);
        int k = Math.Min((int)x, table.Length - 2);
        return table[k] + (x - k) * (table[k + 1] - table[k]);
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1;
        for (int k = 1; k < 50; k++)
        {
            term *= (x / (2 * k)) * (x / (2 * k));
            sum += term;
        }
        return sum;
    }

    /// <summary>
    /// Upstream's design (<c>Flatten::Flatten</c>): the inverse roll-off, capped, sampled on an 8192-point grid; its inverse DFT (real
    /// and even, so real symmetric taps); the 129 around zero under a Kaiser window. Null for a rate with no table.
    /// </summary>
    internal static float[]? TapsFor(double rateHz)
    {
        var table = RolloffFor(rateHz);
        if (table == null) return null;
        long key = (long)Math.Round(rateHz);
        lock (Designed)
        {
            if (Designed.TryGetValue(key, out var made)) return made;
            double cap = Math.Pow(10, MaxGainDb / 20);
            var gain = new double[DesignPoints];
            for (int k = 0; k < DesignPoints; k++)
            {
                double f = k < DesignPoints / 2 ? (double)k / DesignPoints : (double)k / DesignPoints - 1;
                gain[k] = Math.Min(Math.Pow(10, -RolloffDb(table, Math.Abs(f) / 0.5) / 20), cap);
            }
            var impulse = new double[Delay + 1];
            for (int n = 0; n <= Delay; n++)
            {
                double sum = 0;
                for (int k = 0; k < DesignPoints; k++) sum += gain[k] * Math.Cos(2 * Math.PI * k * n / DesignPoints);
                impulse[n] = sum / DesignPoints;
            }
            double i0 = BesselI0(KaiserBeta);
            var taps = new float[Taps];
            for (int t = 0; t < Taps; t++)
            {
                int n = t - Delay;
                double r = (double)n / Delay;
                taps[t] = (float)(impulse[Math.Abs(n)] * BesselI0(KaiserBeta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0);
            }
            Designed[key] = taps;
            return taps;
        }
    }

    /// <summary>The FIR's response at |f| = x Nyquist, dB (upstream's <c>gain_db</c>).</summary>
    internal static double ResponseDb(float[] taps, double x)
    {
        double w = Math.PI * Math.Clamp(x, 0, 1), re = 0;
        for (int t = 0; t < taps.Length; t++) re += taps[t] * Math.Cos(w * (t - Delay));
        return 20 * Math.Log10(Math.Abs(re));
    }

    // ------------------------------------------------------------ the running filter

    /// <summary>The most slices one <see cref="ProcessBatch"/> takes (64 Ki samples).</summary>
    public const int MaxBatch = 32;

    private const int Hist = 4 * Delay;  // the filter's history, as floats: 2 Delay complex samples
    private readonly int _block;
    private readonly int _n2;            // floats a slice
    private readonly int _subs;          // DC stretches a slice
    // Per slice of a batch, each touched by that slice's job alone: [history: Hist][this slice: _n2][SIMD tail], the filter's output,
    // and the slice before it (its history); per stretch the sums before the DC comes off (I, Q), and the DC that comes off (I, Q).
    private readonly float[]?[] _in = new float[MaxBatch][];
    private readonly float[]?[] _out = new float[MaxBatch][];
    private readonly short[]?[] _before = new short[MaxBatch][];
    private readonly double[] _sums;
    private readonly float[] _dc;
    private readonly float[] _hist = new float[Hist];   // the last slice's input tail: the next batch's first history
    private float[]? _taps;
    private double _rateHz;
    private double _dcI, _dcQ;           // the DC estimate, in sc16 units
    private bool _primed;
    private short[]? _single;
    private short[][]? _singleSet;
    private Action<int, short[]>? _singleFill;

    public W1700kCondition(int blockSamples = W1700kExport.SamplesPerSlice)
    {
        _block = blockSamples;
        _n2 = 2 * _block;
        _subs = (_n2 + 2 * DcSubSamples - 1) / (2 * DcSubSamples);
        _sums = new double[MaxBatch * 2 * _subs];
        _dc = new float[MaxBatch * 2 * _subs];
    }

    /// <summary>Flatten the stream (when its rate has a table).</summary>
    public volatile bool Flatten = true;

    /// <summary>Remove the stream's DC.</summary>
    public volatile bool RemoveDc = true;

    /// <summary>Samples clipped at sc16 full scale by the correction's gain: an edge signal the roll-off had brought under it.</summary>
    public long Clipped;

    /// <summary>The rate whose roll-off is corrected; a change starts the filter afresh.</summary>
    public double RateHz
    {
        get => _rateHz;
        set
        {
            if (value == _rateHz) return;
            _rateHz = value;
            _taps = TapsFor(value);
            _primed = false;
        }
    }

    /// <summary>Whether this rate is flattened now.</summary>
    public bool Flattening => Flatten && _taps != null;

    /// <summary>A new run (the stream restarted): no history to filter across.</summary>
    public void Reset() => _primed = false;

    /// <summary>
    /// Condition one block in place (<paramref name="iq"/>: <c>_block</c> interleaved sc16 samples). With the flatten on, the output is
    /// the input <see cref="Delay"/> samples earlier (zero-phase as computed, so the stream is 64 samples late - 1.6 us at 40 MS/s);
    /// the first block after a reset is filtered against zeros before it.
    /// <para>The DC estimate is a one-pole of corner <see cref="DcCornerTargetHz"/>, moved once every <see cref="DcSubSamples"/> samples by
    /// their mean (<see cref="DcShare"/>) - the per-sample recursion is a serial chain no vector unit can share. NOT once a 2048-sample
    /// block: the subtracted estimate is a STAIRCASE, and stepping at 19.5 kHz it put images of a signal f from the LO at 19.5 kHz +- f,
    /// about -30 dBc for one 5 kHz away (the gate caught it as that tone reading 0.23 dB high). Stepping every 16 samples (2.5 MHz at
    /// 40 MS/s) puts them near (corner / step rate) = -82 dBc of the signal, and the gate measures them (64 was -70).</para>
    /// </summary>
    public void Process(Span<short> iq)
    {
        _single ??= new short[_n2];
        _singleSet ??= new[] { _single };
        _singleFill ??= (_, _) => { };              // the slice is in place already, and a batch of one never asks for the one before
        iq.Slice(0, _n2).CopyTo(_single);
        ProcessBatch(1, _singleFill, _singleSet, null);
        _single.CopyTo(iq);
    }

    /// <summary>
    /// Condition <paramref name="count"/> CONSECUTIVE slices (at most <see cref="MaxBatch"/>, no reset among them) into
    /// <paramref name="iqs"/>[0 .. count), the same samples <see cref="Process"/> gives them one at a time - bit for bit
    /// (W1700kConditionBatchCases, against the conditioner as it ran before). <paramref name="fill"/>(j, dst) writes slice j's sc16
    /// samples; it may be called for a slice twice, for the slice after it needs its tail as the filter's history, so it must not
    /// consume anything.
    /// <para>What each slice needs only of itself is shared by <paramref name="team"/> (null: this thread): the fill, the slice before's
    /// tail, the filter, and each stretch's sums - 95 % of the work. In order, on this thread: the DC estimate's recursion over those
    /// sums (128 steps a slice), then the DC off, rounding and clipping.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void ProcessBatch(int count, Action<int, short[]> fill, short[][] iqs, WorkTeam? team)
    {
        if (count <= 0) return;
        if (count > MaxBatch) throw new ArgumentOutOfRangeException(nameof(count), $"at most {MaxBatch} slices a batch");
        bool flatten = Flattening, dc = RemoveDc;
        var taps = _taps;
        if (flatten && !_primed)
        {
            Array.Clear(_hist);
            _primed = true;
        }
        int step = 2 * DcSubSamples;
        void One(int j)
        {
            var iq = iqs[j];
            fill(j, iq);
            if (!flatten && !dc) return;
            var o = _out[j] ??= new float[_n2 + 32];
            if (flatten)
            {
                var x = _in[j] ??= new float[Hist + _n2 + 32];
                if (j == 0) _hist.CopyTo(x, 0);
                else
                {
                    // The slice before, read again: its job is writing its own buffer at this moment.
                    var b = _before[j] ??= new short[_n2];
                    fill(j - 1, b);
                    ToFloat(b.AsSpan(_n2 - Hist, Hist), x.AsSpan(0, Hist));
                }
                ToFloat(iq.AsSpan(0, _n2), x.AsSpan(Hist, _n2));
                Fir(taps!, x, o, _n2);
            }
            else ToFloat(iq.AsSpan(0, _n2), o.AsSpan(0, _n2));
            if (dc) Sums(o, _n2, step, _sums.AsSpan(j * 2 * _subs, 2 * _subs));
        }
        if (team == null || count == 1)
            for (int j = 0; j < count; j++) One(j);
        else team.Run(count, One);
        if (!flatten && !dc) return;
        if (flatten) Array.Copy(_in[count - 1]!, _n2, _hist, 0, Hist);   // the last slice's input tail, for the next batch
        long clipped = 0;
        double share = DcShare(_rateHz, DcSubSamples);
        for (int j = 0; j < count; j++)
        {
            if (!dc)
            {
                clipped += ToShort(_out[j]!, _n2, step, ReadOnlySpan<float>.Empty, iqs[j]);
                continue;
            }
            // The estimate each stretch has taken off it, and then moved by that stretch's mean: the serial chain, in order.
            var sums = _sums.AsSpan(j * 2 * _subs, 2 * _subs);
            var sub = _dc.AsSpan(j * 2 * _subs, 2 * _subs);
            for (int at = 0, s = 0; at < _n2; at += step, s += 2)
            {
                int half = Math.Min(step, _n2 - at) / 2;
                sub[s] = (float)_dcI;
                sub[s + 1] = (float)_dcQ;
                _dcI += share * (sums[s] / half - _dcI);
                _dcQ += share * (sums[s + 1] / half - _dcQ);
            }
            clipped += ToShort(_out[j]!, _n2, step, sub, iqs[j]);
        }
        if (clipped > 0) Interlocked.Add(ref Clipped, clipped);
    }

    /// <summary>How many samples the DC estimate stands for before it moves: small enough that its staircase's images are negligible
    /// (<see cref="Process"/>), large enough that the vector loop between moves is not all overhead.</summary>
    public const int DcSubSamples = 16;

    /// <summary>sc16 to float, widened eight at a time.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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

    /// <summary>
    /// Each stretch of <paramref name="step"/> floats of <paramref name="src"/>: its sums BEFORE the DC is taken off, I and Q - what
    /// the estimate moves by. The same additions in the same order as when they were taken inside the conversion to sc16 (eight lanes
    /// summed sixteen floats at a time, then the lanes into a double, I from the even ones), so the estimate is the same to the bit.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Sums(float[] src, int n2, int step, Span<double> sums)
    {
        bool vector = Vector256.IsHardwareAccelerated && Avx.IsSupported;
        ref float s0 = ref MemoryMarshal.GetArrayDataReference(src);
        for (int at = 0, k = 0; at < n2; at += step, k += 2)
        {
            int len = Math.Min(step, n2 - at), i = 0;
            double sumI = 0, sumQ = 0;
            if (vector)
            {
                ref float s = ref Unsafe.Add(ref s0, at);
                var sum = Vector256<float>.Zero;
                for (; i + 16 <= len; i += 16) sum += Vector256.LoadUnsafe(ref s, (nuint)i) + Vector256.LoadUnsafe(ref s, (nuint)(i + 8));
                for (int e = 0; e < 8; e += 2)
                {
                    sumI += sum.GetElement(e);
                    sumQ += sum.GetElement(e + 1);
                }
            }
            for (; i < len; i += 2)
            {
                sumI += src[at + i];
                sumQ += src[at + i + 1];
            }
            sums[k] = sumI;
            sums[k + 1] = sumQ;
        }
    }

    /// <summary>
    /// float to sc16: <paramref name="src"/>'s <paramref name="n2"/> floats into <paramref name="dst"/>, each stretch of
    /// <paramref name="step"/> with its own DC taken off (<paramref name="dc"/>: I, Q a stretch; empty = none), rounded to nearest,
    /// saturated at full scale with every saturated sample counted.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long ToShort(float[] src, int n2, int step, ReadOnlySpan<float> dc, Span<short> dst)
    {
        long clipped = 0;
        bool vector = Vector256.IsHardwareAccelerated && Avx.IsSupported;
        ref float s0 = ref MemoryMarshal.GetArrayDataReference(src);
        var max = Vector256.Create(32767f);
        var min = Vector256.Create(-32768f);
        if (dc.IsEmpty) step = n2;
        for (int at = 0, k = 0; at < n2; at += step, k += 2)
        {
            int len = Math.Min(step, n2 - at), i = 0;
            float subI = dc.IsEmpty ? 0 : dc[k], subQ = dc.IsEmpty ? 0 : dc[k + 1];
            var d = dst.Slice(at, len);
            if (vector)
            {
                ref float s = ref Unsafe.Add(ref s0, at);
                var sub = Vector256.Create(subI, subQ, subI, subQ, subI, subQ, subI, subQ);
                for (; i + 16 <= len; i += 16)
                {
                    var a = Vector256.LoadUnsafe(ref s, (nuint)i) - sub;
                    var b = Vector256.LoadUnsafe(ref s, (nuint)(i + 8)) - sub;
                    uint over = Vector256.ExtractMostSignificantBits(Vector256.GreaterThan(a, max) | Vector256.LessThan(a, min)) |
                                Vector256.ExtractMostSignificantBits(Vector256.GreaterThan(b, max) | Vector256.LessThan(b, min)) << 8;
                    clipped += BitOperations.PopCount(over);
                    var ia = Vector256.ConvertToInt32(Avx.RoundToNearestInteger(Vector256.Min(Vector256.Max(a, min), max)));
                    var ib = Vector256.ConvertToInt32(Avx.RoundToNearestInteger(Vector256.Min(Vector256.Max(b, min), max)));
                    Vector256.Narrow(ia, ib).CopyTo(d.Slice(i, 16));
                }
            }
            for (; i < len; i += 2)
            {
                d[i] = Sat(MathF.Round(src[at + i] - subI), ref clipped);
                d[i + 1] = Sat(MathF.Round(src[at + i + 1] - subQ), ref clipped);
            }
        }
        return clipped;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short Sat(float v, ref long clipped)
    {
        if (v > short.MaxValue) { clipped++; return short.MaxValue; }
        if (v < short.MinValue) { clipped++; return short.MinValue; }
        return (short)v;
    }

    /// <summary>
    /// out[f] = sum over k of taps[k] x work[f + 2k], for float index f of the interleaved block: the real taps act on I and Q alike, at a
    /// stride of one complex sample. Symmetric taps fold the sum in two (65 multiplies, not 129); vectorised across f, FOUR output
    /// vectors at a time - four independent sums, so the multiply-adds overlap instead of each waiting on the last (one sum was
    /// latency-bound: 35 us a slice, measured).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Fir(float[] taps, float[] work, float[] output, int n2)
    {
        ref float x = ref MemoryMarshal.GetArrayDataReference(work);
        ref float o = ref MemoryMarshal.GetArrayDataReference(output);
        ref float t = ref MemoryMarshal.GetArrayDataReference(taps);
        int f = 0;
        // 512 bits where the processor runs them at full width (Zen 5: the filter 9.1 -> see docs/w1700k_support.md): the same
        // multiply-adds in the same order for every output, so the same result to the bit as the 256-bit loop below.
        if (Vector512.IsHardwareAccelerated)
        {
            for (; f + 64 <= n2; f += 64)
            {
                var c = Vector512.Create(Unsafe.Add(ref t, Delay));
                nuint m = (nuint)(f + 2 * Delay);
                var a0 = c * Vector512.LoadUnsafe(ref x, m);
                var a1 = c * Vector512.LoadUnsafe(ref x, m + 16);
                var a2 = c * Vector512.LoadUnsafe(ref x, m + 32);
                var a3 = c * Vector512.LoadUnsafe(ref x, m + 48);
                for (int k = 0; k < Delay; k++)
                {
                    var h = Vector512.Create(Unsafe.Add(ref t, k));
                    nuint lo = (nuint)(f + 2 * k), hi = (nuint)(f + 2 * (Taps - 1 - k));
                    a0 = Vector512.FusedMultiplyAdd(h, Vector512.LoadUnsafe(ref x, lo) + Vector512.LoadUnsafe(ref x, hi), a0);
                    a1 = Vector512.FusedMultiplyAdd(h, Vector512.LoadUnsafe(ref x, lo + 16) + Vector512.LoadUnsafe(ref x, hi + 16), a1);
                    a2 = Vector512.FusedMultiplyAdd(h, Vector512.LoadUnsafe(ref x, lo + 32) + Vector512.LoadUnsafe(ref x, hi + 32), a2);
                    a3 = Vector512.FusedMultiplyAdd(h, Vector512.LoadUnsafe(ref x, lo + 48) + Vector512.LoadUnsafe(ref x, hi + 48), a3);
                }
                a0.StoreUnsafe(ref o, (nuint)f);
                a1.StoreUnsafe(ref o, (nuint)f + 16);
                a2.StoreUnsafe(ref o, (nuint)f + 32);
                a3.StoreUnsafe(ref o, (nuint)f + 48);
            }
        }
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
