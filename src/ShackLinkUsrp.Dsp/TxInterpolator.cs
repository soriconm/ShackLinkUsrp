// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace ShackLinkUsrp.Dsp;

/// <summary>
/// Raises a transmission's samples from the TX rate to the wide rate - what the synthetic radio needs to
/// put what it is sent into its own receive stream (ShackLink docs/usrp_b210_support.md section 9.17
/// P4-b), the job a B2xx's DUC and the air do for a real one. Two stages, because a transmission's
/// signal may reach +-0.4 of its rate (TxUsableFraction) and a plain interpolator's images of such a
/// signal would stand in the panorama as signals nobody sent:
/// <list type="number">
/// <item>a polyphase FIR upsampler by L = min(8, wide/TX) - a Kaiser-windowed sinc cut off at half the
/// TX rate, <see cref="TapsPerPhase"/> taps per phase, ~-75 dB images - so the signal occupies at most
/// +-0.05 of the intermediate rate;</item>
/// <item>cubic Lagrange interpolation from there to the wide rate, whose images of so oversampled a
/// signal are far below the first stage's.</item>
/// </list>
/// When the wide rate is an integer multiple of the TX rate under 8, the second stage lands exactly on
/// the first stage's samples. Input is pushed as it is consumed, so the caller - the synthetic radio's
/// clock - decides when a TX sample is used.
/// </summary>
public sealed class TxInterpolator
{
    public const int TapsPerPhase = 24;

    private readonly int _l;
    private readonly double[] _poly;     // [phase * TapsPerPhase + m] = L * h[phase + L m]
    private readonly double[] _xr = new double[TapsPerPhase];   // input history, newest at _xPos
    private readonly double[] _xi = new double[TapsPerPhase];
    private int _xPos;
    private readonly double _step;       // stage-1 samples per output sample
    private double[] _sr = new double[1024];                     // stage-1 output FIFO
    private double[] _si = new double[1024];
    private long _sBase;                 // absolute index of _sr[0]
    private int _sCount;
    private double _t = 1.0;             // absolute stage-1 position of the next output sample

    public double TxRateHz { get; }
    public double OutRateHz { get; }
    public int Upsampling => _l;

    public TxInterpolator(double txRateHz, double outRateHz)
    {
        if (!(txRateHz > 0) || !(outRateHz >= txRateHz))
            throw new ArgumentOutOfRangeException(nameof(outRateHz), "the output rate must be at least the TX rate");
        TxRateHz = txRateHz;
        OutRateHz = outRateHz;
        int ratio = (int)Math.Floor(outRateHz / txRateHz + 1e-9);
        _l = Math.Clamp(ratio, 1, 8);
        _step = _l * txRateHz / outRateHz;
        _poly = new double[_l * TapsPerPhase];
        if (_l == 1)
        {
            _poly[0] = 1.0;
        }
        else
        {
            var h = DecimatingFir.Design(_l * TapsPerPhase, 0.5 / _l);
            for (int p = 0; p < _l; p++)
                for (int m = 0; m < TapsPerPhase; m++)
                    _poly[p * TapsPerPhase + m] = _l * h[p + _l * m];
        }
    }

    /// <summary>How many TX samples must be <see cref="Push"/>ed before <paramref name="outputs"/> more
    /// output samples can be <see cref="Produce"/>d.</summary>
    public int InputNeeded(int outputs)
    {
        if (outputs <= 0) return 0;
        long last = (long)Math.Floor(_t + (outputs - 1) * _step) + 2;
        long have = _sBase + _sCount - 1;
        long missing = last - have;
        return missing <= 0 ? 0 : (int)((missing + _l - 1) / _l);
    }

    /// <summary>Take <paramref name="count"/> TX samples, interleaved sc16 I, Q.</summary>
    public void Push(ReadOnlySpan<short> iq, int count)
    {
        const double scale = 1.0 / 32768.0;
        EnsureRoom(count * _l);
        for (int n = 0; n < count; n++)
        {
            _xPos = _xPos + 1 == TapsPerPhase ? 0 : _xPos + 1;
            _xr[_xPos] = iq[2 * n] * scale;
            _xi[_xPos] = iq[2 * n + 1] * scale;
            for (int p = 0; p < _l; p++)
            {
                double ar = 0, ai = 0;
                int o = p * TapsPerPhase;
                int k = _xPos;
                for (int m = 0; m < TapsPerPhase; m++)
                {
                    double c = _poly[o + m];
                    ar += c * _xr[k];
                    ai += c * _xi[k];
                    k = k == 0 ? TapsPerPhase - 1 : k - 1;
                }
                _sr[_sCount] = ar;
                _si[_sCount] = ai;
                _sCount++;
            }
        }
    }

    /// <summary>Write <paramref name="count"/> output samples at the output rate, normalised to +-1 of
    /// full scale. The caller has pushed <see cref="InputNeeded"/> first.</summary>
    public void Produce(int count, Span<double> re, Span<double> im)
    {
        for (int j = 0; j < count; j++)
        {
            long i = (long)Math.Floor(_t);
            double f = _t - i;
            int b = (int)(i - 1 - _sBase);
            if (b < 0 || b + 3 >= _sCount) throw new InvalidOperationException("TxInterpolator.Produce ran past its input; push InputNeeded first");
            // Cubic Lagrange through the four samples at -1, 0, 1, 2 around the position.
            double wm1 = -f * (f - 1) * (f - 2) / 6.0;
            double w0 = (f + 1) * (f - 1) * (f - 2) / 2.0;
            double w1 = -(f + 1) * f * (f - 2) / 2.0;
            double w2 = (f + 1) * f * (f - 1) / 6.0;
            re[j] = wm1 * _sr[b] + w0 * _sr[b + 1] + w1 * _sr[b + 2] + w2 * _sr[b + 3];
            im[j] = wm1 * _si[b] + w0 * _si[b + 1] + w1 * _si[b + 2] + w2 * _si[b + 3];
            _t += _step;
        }
        // Keep one stage-1 sample behind the next position: the cubic reads index floor(t) - 1.
        long keepFrom = (long)Math.Floor(_t) - 1;
        int drop = (int)Math.Clamp(keepFrom - _sBase, 0, _sCount);
        if (drop > 0)
        {
            Array.Copy(_sr, drop, _sr, 0, _sCount - drop);
            Array.Copy(_si, drop, _si, 0, _sCount - drop);
            _sCount -= drop;
            _sBase += drop;
        }
    }

    private void EnsureRoom(int more)
    {
        if (_sCount + more <= _sr.Length) return;
        int size = _sr.Length;
        while (size < _sCount + more) size *= 2;
        Array.Resize(ref _sr, size);
        Array.Resize(ref _si, size);
    }
}
