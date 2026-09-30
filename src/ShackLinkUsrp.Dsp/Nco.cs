// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace ShackLinkUsrp.Dsp;

/// <summary>
/// A numerically controlled oscillator for the frequency shift in front of a lease's decimator:
/// a unit phasor multiplied by a fixed step per sample, renormalised often enough that its
/// magnitude cannot drift. Double precision, because a lease at 4 MHz offset of a 20 MS/s stream
/// runs 10^7 multiplications a second and single precision would drift audibly within seconds.
/// </summary>
public sealed class Nco
{
    private double _re = 1.0, _im = 0.0;
    private double _stepRe = 1.0, _stepIm = 0.0;
    private int _sinceRenorm;

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
        const double scale = 1.0 / 32768.0;
        double pr = _re, pi = _im, sr = _stepRe, si = _stepIm;
        for (int n = 0; n < count; n++)
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
}
