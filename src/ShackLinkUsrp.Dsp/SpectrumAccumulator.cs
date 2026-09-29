// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace ShackLinkUsrp.Dsp;

public enum Integration
{
    Sample = 0,
    Average = 1,
    Peak = 2,
}

/// <summary>
/// FFT frames in, rows out: windows each frame of sc16, transforms it, and integrates the power
/// of every bin over the row interval (average, peak, or the last frame for "sample"). A row is
/// the fft-shifted, CROPPED run of bins [cropStart, cropStart + bins) in dB x 100 relative to
/// digital full scale: a full-scale complex tone centred in a bin reads 0 (the window's coherent
/// sum is divided out), which is ShackLink's Short() convention (section 9.3).
/// </summary>
public sealed class SpectrumAccumulator
{
    private readonly ComplexFft _fft;
    private readonly SpectralWindow _window;
    private readonly float[] _re;
    private readonly float[] _im;
    private readonly double[] _acc;
    private readonly double _normalise;

    public int FftSize { get; }
    public Integration Integration { get; }
    public int FramesIntegrated { get; private set; }
    public SpectralWindow Window => _window;

    public SpectrumAccumulator(int fftSize, WindowShape window, Integration integration)
    {
        FftSize = fftSize;
        Integration = integration;
        _fft = new ComplexFft(fftSize);
        _window = new SpectralWindow(window, fftSize);
        _re = new float[fftSize];
        _im = new float[fftSize];
        _acc = new double[fftSize];
        _normalise = 1.0 / (_window.CoherentSum * _window.CoherentSum);
    }

    /// <summary>Transform one frame of <see cref="FftSize"/> interleaved sc16 samples and integrate it.</summary>
    public void AddFrame(ReadOnlySpan<short> iq)
    {
        const float scale = 1f / 32768f;
        var w = _window.Coefficients;
        for (int n = 0; n < FftSize; n++)
        {
            float c = w[n] * scale;
            _re[n] = iq[2 * n] * c;
            _im[n] = iq[2 * n + 1] * c;
        }
        _fft.Forward(_re, _im);
        switch (Integration)
        {
            case Integration.Peak:
                for (int k = 0; k < FftSize; k++)
                {
                    double p = (double)_re[k] * _re[k] + (double)_im[k] * _im[k];
                    if (FramesIntegrated == 0 || p > _acc[k]) _acc[k] = p;
                }
                break;
            case Integration.Sample:
                for (int k = 0; k < FftSize; k++) _acc[k] = (double)_re[k] * _re[k] + (double)_im[k] * _im[k];
                break;
            default:
                for (int k = 0; k < FftSize; k++) _acc[k] += (double)_re[k] * _re[k] + (double)_im[k] * _im[k];
                break;
        }
        FramesIntegrated++;
    }

    /// <summary>Write bins [cropStart, cropStart + bins) of the SHIFTED spectrum (index 0 = the most
    /// negative frequency) into <paramref name="dest"/> as dB x 100, then reset the integration.
    /// A row with no frame reads the floor (-32768), which no real measurement produces.</summary>
    public void TakeRow(int cropStart, int bins, short[] dest)
    {
        int half = FftSize / 2;
        double div = Integration == Integration.Average && FramesIntegrated > 0 ? 1.0 / FramesIntegrated : 1.0;
        for (int i = 0; i < bins; i++)
        {
            int s = cropStart + i;
            int k = (s + half) & (FftSize - 1);
            if (FramesIntegrated == 0)
            {
                dest[i] = short.MinValue;
                continue;
            }
            double p = _acc[k] * div * _normalise;
            double db100 = 1000.0 * Math.Log10(p + 1e-30);
            dest[i] = (short)Math.Clamp(Math.Round(db100), short.MinValue + 1, short.MaxValue);
        }
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_acc);
        FramesIntegrated = 0;
    }
}
