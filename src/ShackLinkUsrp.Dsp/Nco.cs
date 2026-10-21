// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;

namespace ShackLinkUsrp.Dsp;

/// <summary>
/// A numerically controlled oscillator for the frequency shift in front of a lease's decimator:
/// a unit phasor multiplied by a fixed step per sample, renormalised often enough that its
/// magnitude cannot drift. Double precision, because a lease at 4 MHz offset of a 20 MS/s stream
/// runs 10^7 multiplications a second and single precision would drift audibly within seconds.
/// <para><b>Vectorised, and still a double-precision oscillator</b> (ShackLink docs/skimmer_support.md 26.14): the
/// recurrence runs once per block of <see cref="Vector{T}"/> samples, in double, by the block's step; each lane's
/// phasor is that base times its own offset step^i, formed in float and never fed back. So nothing accumulates
/// in float - every sample's phasor is one float rounding from the exact one - and the serial chain that made a
/// per-sample recurrence latency-bound (a complex multiply's latency per sample) is a block long instead.</para>
/// </summary>
public sealed class Nco
{
    private double _re = 1.0, _im = 0.0;
    private double _stepRe = 1.0, _stepIm = 0.0;
    private int _sinceRenorm;
    // The block step, step^W, and each lane's offset step^i over 32768 (the sc16 scale folded in), i < W.
    private double _blockRe = 1.0, _blockIm = 0.0;
    private float[] _laneRe = Array.Empty<float>(), _laneIm = Array.Empty<float>();

    public double FrequencyHz { get; private set; }
    public double SampleRateHz { get; private set; }

    /// <summary>Set the shift. Positive = the signal at +f moves to 0 Hz (the phasor turns at -f).
    /// The phase is kept, so a retune does not click - unless the caller anchors it (<see cref="AnchorAt"/>).</summary>
    public void Set(double shiftHz, double sampleRateHz)
    {
        FrequencyHz = shiftHz;
        SampleRateHz = sampleRateHz;
        double w = -2.0 * Math.PI * shiftHz / sampleRateHz;
        _stepRe = Math.Cos(w);
        _stepIm = Math.Sin(w);
        int lanes = Vector<float>.Count;
        _blockRe = Math.Cos(w * lanes);
        _blockIm = Math.Sin(w * lanes);
        _laneRe = new float[lanes];
        _laneIm = new float[lanes];
        for (int i = 0; i < lanes; i++)
        {
            _laneRe[i] = (float)(Math.Cos(w * i) / 32768.0);
            _laneIm[i] = (float)(Math.Sin(w * i) / 32768.0);
        }
    }

    /// <summary>
    /// Put the phasor where it is at radio position <paramref name="position"/> on a clock that started at 0:
    /// <c>exp(-j 2 pi shift position / rate)</c>. A lease anchors its NCO before every chunk it mixes, so its phase
    /// is a function of the radio position ALONE - two leases at one centre agree in phase at one position,
    /// whenever each was opened and whatever either dropped (ShackLink docs/usrp_b210_support.md section 9.23 Q4:
    /// what makes aligning two leases by their origins a phase measurement, not only a time one). Exact integer
    /// arithmetic when the shift and the rate are whole hertz, as every offered rate and every centre is.
    /// </summary>
    public void AnchorAt(long position)
    {
        double cycles;
        double shift = FrequencyHz, rate = SampleRateHz;
        if (rate > 0 && shift == Math.Round(shift) && rate == Math.Round(rate) && rate < long.MaxValue)
        {
            long r = (long)rate;
            Int128 prod = (Int128)(long)shift * position;
            Int128 rem = prod % r;
            if (rem < 0) rem += r;
            cycles = (double)(long)rem / r;
        }
        else
        {
            double c = shift * (double)position / rate;
            cycles = c - Math.Floor(c);
        }
        double w = -2.0 * Math.PI * cycles;
        _re = Math.Cos(w);
        _im = Math.Sin(w);
        _sinceRenorm = 0;
    }

    /// <summary>Mix <paramref name="count"/> sc16 samples (normalised to +/-1) into re/im.</summary>
    public void Mix(ReadOnlySpan<short> iq, int count, Span<float> re, Span<float> im)
    {
        int n = 0;
        int lanes = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && count >= lanes && _laneRe.Length == lanes)
            n = MixBlocks(iq, count / lanes * lanes, re, im);
        const double scale = 1.0 / 32768.0;
        double pr = _re, pi = _im, sr = _stepRe, si = _stepIm;
        for (; n < count; n++)
        {
            double xr = iq[2 * n] * scale;
            double xi = iq[2 * n + 1] * scale;
            re[n] = (float)(xr * pr - xi * pi);
            im[n] = (float)(xr * pi + xi * pr);
            double npr = pr * sr - pi * si;
            pi = pr * si + pi * sr;
            pr = npr;
            if (++_sinceRenorm >= 1024)
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

    // Whole blocks of W samples. A sample's I and Q are the low and high halves of one little-endian int, so a
    // Vector<int> of samples splits into I and Q by shifts - no shuffle.
    private int MixBlocks(ReadOnlySpan<short> iq, int count, Span<float> re, Span<float> im)
    {
        int lanes = Vector<float>.Count;
        var samples = MemoryMarshal.Cast<short, int>(iq.Slice(0, 2 * count));
        var laneRe = new Vector<float>(_laneRe);
        var laneIm = new Vector<float>(_laneIm);
        double br = _re, bi = _im, sr = _blockRe, si = _blockIm;
        for (int n = 0; n < count; n += lanes)
        {
            var v = new Vector<int>(samples.Slice(n));
            var xr = Vector.ConvertToSingle(Vector.ShiftRightArithmetic(Vector.ShiftLeft(v, 16), 16));
            var xi = Vector.ConvertToSingle(Vector.ShiftRightArithmetic(v, 16));
            var fr = new Vector<float>((float)br);
            var fi = new Vector<float>((float)bi);
            var pr = fr * laneRe - fi * laneIm;
            var pi = fr * laneIm + fi * laneRe;
            (xr * pr - xi * pi).CopyTo(re.Slice(n, lanes));
            (xr * pi + xi * pr).CopyTo(im.Slice(n, lanes));
            double nbr = br * sr - bi * si;
            bi = br * si + bi * sr;
            br = nbr;
            _sinceRenorm += lanes;
            if (_sinceRenorm >= 1024)
            {
                double mag = Math.Sqrt(br * br + bi * bi);
                br /= mag;
                bi /= mag;
                _sinceRenorm = 0;
            }
        }
        _re = br;
        _im = bi;
        return count;
    }
}
