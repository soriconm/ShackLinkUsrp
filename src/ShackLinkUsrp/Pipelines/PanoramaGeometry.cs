// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Pipelines;

/// <summary>
/// What a panorama request becomes against the radio as it is now (W2): the sidecar picks the FFT
/// (the smallest power of two giving at least the requested bins across the span), CROPS the bins
/// that fall in the span and states their count - it never resamples. A centre the span cannot
/// reach is moved to the nearest one that fits, and the grant says where it landed.
/// </summary>
internal sealed record PanoramaGeometry(
    int FftSize,
    int Bins,
    int CropStart,
    long CenterHz,
    long SpanHz,
    bool FollowsLo,
    WindowShape Window,
    Integration Integration,
    double EnbwBins,
    double Fps)
{
    public static PanoramaGeometry Compute(PanoramaRequest req, long loHz, double wideRateHz, int maxFft)
    {
        long span = req.SpanHz > 0 ? (long)Math.Min(req.SpanHz, wideRateHz) : (long)wideRateHz;
        int n = SpanRules.PanoramaFftSize(req.Bins, span, wideRateHz, maxFft);
        if (n == 0)
            throw new RadioRefusal(ErrorCodes.OutOfRange,
                $"{req.Bins} bins across {span} Hz of a {wideRateHz:0} S/s stream needs an FFT above {maxFft} points",
                new JsonObject().Set("max_fft", maxFft));
        int bins = SpanRules.BinsInSpan(n, span, wideRateHz);
        double binHz = wideRateHz / n;
        bool follows = req.CenterHz == 0;
        long want = follows ? loHz : req.CenterHz;
        int crop = n / 2 + (int)Math.Round((want - loHz) / binHz) - bins / 2;
        crop = Math.Clamp(crop, 0, n - bins);
        long center = loHz + (long)Math.Round((crop + bins / 2 - n / 2) * binHz);
        var window = (WindowShape)(int)req.Window;
        var enbw = new SpectralWindow(window, 64).EnbwBins; // ENBW is a property of the shape, not of N
        return new PanoramaGeometry(n, bins, crop, center, (long)Math.Round(bins * binHz), follows,
                                    window, (Integration)(int)req.Integration, enbw, req.Fps);
    }

    public PanoramaGrant ToGrant() => new()
    {
        Bins = Bins,
        FftSize = FftSize,
        SpanHz = SpanHz,
        CenterHz = CenterHz,
        EnbwBins = EnbwBins,
        Fps = Fps,
        Integration = (IntegrationKind)(int)Integration,
        Window = (WindowKind)(int)Window,
    };
}
