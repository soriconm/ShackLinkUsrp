// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace ShackLinkUsrp.Uhd;

/// <summary>One uhd_range_t. A step of 0 means continuous.</summary>
public readonly record struct UhdRange(double Start, double Stop, double Step);

/// <summary>A uhd::meta_range_t copied out of its handle: the overall start/stop/step UHD reports
/// plus every constituent range (a B2xx rate list is several ranges, one per clock regime).</summary>
public sealed record UhdMetaRange(double Start, double Stop, double Step, IReadOnlyList<UhdRange> Ranges)
{
    public override string ToString() =>
        Ranges.Count <= 1 ? $"{Start:G9}..{Stop:G9} step {Step:G6}" : $"{Start:G9}..{Stop:G9} in {Ranges.Count} ranges";
}

/// <summary>uhd_usrp_rx_info_t, copied into managed strings (the native ones are freed at once).</summary>
public sealed record UhdRxInfo(string MboardId, string MboardName, string MboardSerial, string RxId,
                               string RxSubdevName, string RxSubdevSpec, string RxSerial, string RxAntenna);

/// <summary>uhd_tune_result_t plus the frequency get_rx_freq read back afterwards - the readback is
/// the truth (hazard 6: a tune that "succeeds" is not evidence that it tuned).</summary>
public readonly record struct UhdTuneResult(double ClippedRfHz, double TargetRfHz, double ActualRfHz,
                                            double TargetDspHz, double ActualDspHz, double ReadbackHz);

/// <summary>How a TX tune places the synthesizer (docs/usrp_b210_support.md section 9.17 P4-3), each a
/// uhd::tune_request_t shape UHD's own tune_xx_subdev_and_dsp resolves.</summary>
public enum UhdTxTunePolicy
{
    /// <summary>rf AUTO, dsp AUTO: the LO on the target, the DUC taking only the synthesizer's residual.</summary>
    LoOnTarget,
    /// <summary>rf MANUAL at target + offset, dsp AUTO: the LO a stated distance from the carrier.</summary>
    LoOffset,
    /// <summary>rf NONE, dsp AUTO: the LO stays where it is and the DUC's CORDIC moves the carrier - a park.</summary>
    KeepLo,
}

/// <summary>One message from a TX streamer's async channel.</summary>
public readonly record struct UhdAsyncEvent(UhdAsyncEventCode Code, bool HasTimeSpec, long FullSecs, double FracSecs);

/// <summary>What one recv call reported. <see cref="Samples"/> can be non-zero alongside an error
/// code: UHD delivers what it has, then reports the overflow that ended the fragment.</summary>
public readonly record struct UhdRxMetadata(UhdRxErrorCode ErrorCode, bool HasTimeSpec, long FullSecs,
                                            double FracSecs, bool OutOfSequence);
