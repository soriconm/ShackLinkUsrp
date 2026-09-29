// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace ShackLinkUsrp.Dsp;

/// <summary>
/// A transmit source the sidecar generates itself (ShackLink docs/usrp_b210_support.md section 9.17
/// P4-a'): interleaved sc16 at the TX rate, phase-continuous across calls, its level the CARRIER's
/// against the DAC's full scale. The radio side takes as many samples as its clock needs and never
/// asks why.
/// </summary>
public interface IIqGenerator
{
    /// <summary>Write the next <paramref name="count"/> samples, interleaved I, Q.</summary>
    void Fill(Span<short> iq, int count);
}
