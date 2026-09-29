// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace ShackLinkUsrp.Dsp;

public enum WindowShape
{
    Rectangular = 0,
    Hann = 1,
    BlackmanHarris = 2,
}

/// <summary>
/// An analysis window and the two numbers every row states about it (W3): the coherent sum, which
/// sets 0 dBFS for a full-scale tone, and the ENBW in bins, which turns a bin width into an RBW.
/// PERIODIC windows (denominator N), the spectral-analysis convention.
/// </summary>
public sealed class SpectralWindow
{
    public WindowShape Shape { get; }
    public int Size { get; }
    public float[] Coefficients { get; }

    /// <summary>Sum of the coefficients: the magnitude of a unit complex tone centred in a bin.</summary>
    public double CoherentSum { get; }

    /// <summary>Equivalent noise bandwidth in bins: N * sum(w^2) / sum(w)^2.</summary>
    public double EnbwBins { get; }

    public SpectralWindow(WindowShape shape, int size)
    {
        Shape = shape;
        Size = size;
        Coefficients = new float[size];
        double sum = 0, sum2 = 0;
        for (int n = 0; n < size; n++)
        {
            double x = 2.0 * Math.PI * n / size;
            double w = shape switch
            {
                WindowShape.Hann => 0.5 - 0.5 * Math.Cos(x),
                WindowShape.BlackmanHarris => 0.35875 - 0.48829 * Math.Cos(x) + 0.14128 * Math.Cos(2 * x) - 0.01168 * Math.Cos(3 * x),
                _ => 1.0,
            };
            Coefficients[n] = (float)w;
            sum += w;
            sum2 += w * w;
        }
        CoherentSum = sum;
        EnbwBins = size * sum2 / (sum * sum);
    }
}
