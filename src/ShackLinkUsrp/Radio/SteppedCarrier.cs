// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using ShackLinkUsrp.Dsp;

namespace ShackLinkUsrp.Radio;

/// <summary>
/// The <c>step</c> source's samples (ShackLink docs/usrp_b210_support.md section 9.18 P4b2). In a baseband
/// plan it is a carrier at <c>f - centre</c> that moves at each step's exact first sample
/// (<see cref="TxStepPlan.StartSample"/>), phase-continuous - so the dwell is counted by the DAC that
/// takes the samples, however the host stutters. In a tuned plan it is baseband 0 Hz throughout: the
/// frequency writes move the carrier, and the samples say nothing about it.
/// </summary>
internal sealed class SteppedCarrier : IIqGenerator
{
    private readonly TxStepPlan _plan;
    private readonly ToneGenerator _tone;
    private long _generated;     // TX samples written so far, counted from the key-up
    private long _nextStep = 1;  // the next step whose first sample the generator has not reached

    public SteppedCarrier(TxStepPlan plan, double levelDbfs)
    {
        _plan = plan;
        _tone = new ToneGenerator(OffsetOf(0), plan.RateHz, levelDbfs);
    }

    /// <summary>TX samples written so far.</summary>
    public long Generated => _generated;

    private double OffsetOf(long k) => _plan.Baseband ? _plan.FrequencyHz[_plan.IndexOf(k)] - _plan.CenterHz : 0;

    public void Fill(Span<short> iq, int count)
    {
        int done = 0;
        while (done < count)
        {
            int n = count - done;
            if (_plan.Baseband && _nextStep < _plan.StepCount)
            {
                long edge = _plan.StartSample(_nextStep);
                if (_generated >= edge)
                {
                    _tone.Retune(OffsetOf(_nextStep));
                    _nextStep++;
                    continue;
                }
                n = (int)Math.Min(n, edge - _generated);
            }
            _tone.Fill(iq.Slice(2 * done), n);
            done += n;
            _generated += n;
        }
    }
}
