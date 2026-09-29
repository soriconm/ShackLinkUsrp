// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace ShackLinkUsrp.Uhd;

/// <summary>A UHD C API call returned something other than UHD_ERROR_NONE. Carries the call that
/// failed and UHD's own message (the handle's last_error, else the global one), because the code
/// alone - "RUNTIME" - is what an operator can do nothing with.</summary>
public sealed class UhdException : Exception
{
    public UhdError Error { get; }
    public string Call { get; }
    public string UhdMessage { get; }

    public UhdException(UhdError error, string call, string uhdMessage)
        : base($"{call} failed ({error}): {(string.IsNullOrWhiteSpace(uhdMessage) ? "UHD gave no message" : uhdMessage.Trim())}")
    {
        Error = error;
        Call = call;
        UhdMessage = uhdMessage ?? "";
    }
}
