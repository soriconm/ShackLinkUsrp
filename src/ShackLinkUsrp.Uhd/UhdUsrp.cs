// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
using static ShackLinkUsrp.Uhd.UhdNative;

namespace ShackLinkUsrp.Uhd;

/// <summary>
/// One opened USRP (uhd_usrp_make). Every C API call is checked: anything but UHD_ERROR_NONE throws
/// <see cref="UhdException"/> with UHD's own words. Control calls are serialised on one gate so the
/// handle's last_error still belongs to the call that just failed when it is read.
/// Setters return the value READ BACK, because UHD coerces (rates to the clock's divisors, gain to its
/// step, frequency to the synthesizer) and the coerced value is the one every consumer must use.
/// </summary>
public sealed unsafe class UhdUsrp : IDisposable
{
    private IntPtr _h;
    private readonly object _gate = new();
    private readonly List<UhdRxStreamer> _streamers = new();

    public string Args { get; }

    private UhdUsrp(IntPtr handle, string args)
    {
        _h = handle;
        Args = args;
    }

    internal IntPtr Handle
    {
        get
        {
            IntPtr h = _h;
            if (h == IntPtr.Zero) throw new ObjectDisposedException(nameof(UhdUsrp));
            return h;
        }
    }

    // ------------------------------------------------------------------ discovery and open

    /// <summary>uhd_usrp_find: device address strings (e.g. "type=b200,name=…,serial=…,product=B210").
    /// Opens nothing - but it does enumerate USB, and a B2xx that another process holds may be missing
    /// from the answer.</summary>
    public static IReadOnlyList<string> Find(string args)
    {
        EnsureLoaded();
        IntPtr v = IntPtr.Zero;
        CheckGlobal(uhd_string_vector_make(&v), "uhd_string_vector_make");
        IntPtr a = AllocAnsi(args);
        try
        {
            CheckGlobal(uhd_usrp_find((byte*)a, &v), "uhd_usrp_find");
            return ReadStrings(v);
        }
        finally
        {
            Marshal.FreeHGlobal(a);
            uhd_string_vector_free(&v);
        }
    }

    /// <summary>uhd_usrp_make. On a cold B2xx this loads the FX3 firmware and the FPGA bitstream
    /// (~16 s measured, docs/usrp_b210_support.md §1.1) - the caller budgets for it; nothing here
    /// can make it shorter.</summary>
    public static UhdUsrp Open(string args)
    {
        EnsureLoaded();
        IntPtr h = IntPtr.Zero;
        IntPtr a = AllocAnsi(args);
        try
        {
            UhdError e = uhd_usrp_make(&h, (byte*)a);
            if (e != UhdError.None)
                throw new UhdException(e, "uhd_usrp_make", GlobalLastError());
        }
        finally
        {
            Marshal.FreeHGlobal(a);
        }
        if (h == IntPtr.Zero)
            throw new UhdException(UhdError.Unknown, "uhd_usrp_make", "returned success without a handle");
        return new UhdUsrp(h, args);
    }

    // ------------------------------------------------------------------ identity

    public string PrettyPrint => Locked(() => ReadDeviceString((b, l) => uhd_usrp_get_pp_string(Handle, (byte*)b, l), "uhd_usrp_get_pp_string", 16384));

    public string GetMboardName(int mboard = 0) =>
        Locked(() => ReadDeviceString((b, l) => uhd_usrp_get_mboard_name(Handle, (nuint)mboard, (byte*)b, l), "uhd_usrp_get_mboard_name"));

    public UhdRxInfo GetRxInfo(int chan = 0) => Locked(() =>
    {
        uhd_usrp_rx_info_t info = default;
        Check(uhd_usrp_get_rx_info(Handle, (nuint)chan, &info), "uhd_usrp_get_rx_info");
        try
        {
            static string S(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(p) ?? "";
            return new UhdRxInfo(S(info.mboard_id), S(info.mboard_name), S(info.mboard_serial), S(info.rx_id),
                                 S(info.rx_subdev_name), S(info.rx_subdev_spec), S(info.rx_serial), S(info.rx_antenna));
        }
        finally
        {
            // The strings were strdup'd by the C API; only it may free them.
            uhd_usrp_rx_info_free(&info);
        }
    });

    // ------------------------------------------------------------------ clock

    public double GetMasterClockRate(int mboard = 0) => Locked(() =>
    {
        double v;
        Check(uhd_usrp_get_master_clock_rate(Handle, (nuint)mboard, &v), "uhd_usrp_get_master_clock_rate");
        return v;
    });

    /// <summary>Pins the master clock. Setting it switches UHD's automatic clock selection OFF for
    /// the life of the device - a later rate that needs another clock then fails instead of moving it.</summary>
    public double SetMasterClockRate(double hz, int mboard = 0) => Locked(() =>
    {
        Check(uhd_usrp_set_master_clock_rate(Handle, hz, (nuint)mboard), "uhd_usrp_set_master_clock_rate");
        double v;
        Check(uhd_usrp_get_master_clock_rate(Handle, (nuint)mboard, &v), "uhd_usrp_get_master_clock_rate");
        return v;
    });

    public string GetClockSource(int mboard = 0) =>
        Locked(() => ReadDeviceString((b, l) => uhd_usrp_get_clock_source(Handle, (nuint)mboard, (byte*)b, l), "uhd_usrp_get_clock_source"));

    public IReadOnlyList<string> GetClockSources(int mboard = 0) =>
        Locked(() => ReadDeviceStrings(v => uhd_usrp_get_clock_sources(Handle, (nuint)mboard, (IntPtr*)v), "uhd_usrp_get_clock_sources"));

    public void SetClockSource(string source, int mboard = 0) => Locked(() =>
    {
        IntPtr s = AllocAnsi(source);
        try { Check(uhd_usrp_set_clock_source(Handle, (byte*)s, (nuint)mboard), "uhd_usrp_set_clock_source"); }
        finally { Marshal.FreeHGlobal(s); }
        return 0;
    });

    // ------------------------------------------------------------------ RX: rate and frequency

    public int RxChannelCount => Locked(() =>
    {
        nuint n;
        Check(uhd_usrp_get_rx_num_channels(Handle, &n), "uhd_usrp_get_rx_num_channels");
        return (int)n;
    });

    public double SetRxRate(double hz, int chan = 0) => Locked(() =>
    {
        Check(uhd_usrp_set_rx_rate(Handle, hz, (nuint)chan), "uhd_usrp_set_rx_rate");
        double v;
        Check(uhd_usrp_get_rx_rate(Handle, (nuint)chan, &v), "uhd_usrp_get_rx_rate");
        return v;
    });

    public double GetRxRate(int chan = 0) => Locked(() =>
    {
        double v;
        Check(uhd_usrp_get_rx_rate(Handle, (nuint)chan, &v), "uhd_usrp_get_rx_rate");
        return v;
    });

    /// <summary>The rates UHD offers FOR THE CURRENT MASTER CLOCK - on a B2xx with automatic clock
    /// selection this is not every rate the device can reach (ShackLink's Soapy §19 trap: it tops at
    /// 16 MSPS while a 56 MSPS request is honoured by moving the clock).</summary>
    public UhdMetaRange GetRxRates(int chan = 0) =>
        Locked(() => ReadMetaRange(r => uhd_usrp_get_rx_rates(Handle, (nuint)chan, r), "uhd_usrp_get_rx_rates"));

    /// <summary>Tune channel <paramref name="chan"/> so its samples are centred on
    /// <paramref name="targetHz"/>. With a non-zero <paramref name="loOffsetHz"/> the RF LO is placed
    /// at target + offset (policy MANUAL) and the FPGA DDC shifts the difference (policy AUTO) -
    /// uhd::tune_request_t(target, lo_off) - which moves the DC spike off the centre.</summary>
    public UhdTuneResult TuneRx(double targetHz, double loOffsetHz = 0.0, int chan = 0) => Locked(() =>
    {
        var req = new uhd_tune_request_t
        {
            target_freq = targetHz,
            rf_freq_policy = loOffsetHz == 0.0 ? PolicyAuto : PolicyManual,
            rf_freq = targetHz + loOffsetHz,
            dsp_freq_policy = PolicyAuto,
            dsp_freq = 0.0,
            args = IntPtr.Zero,
        };
        uhd_tune_result_t res;
        Check(uhd_usrp_set_rx_freq(Handle, &req, (nuint)chan, &res), "uhd_usrp_set_rx_freq");
        double readback;
        Check(uhd_usrp_get_rx_freq(Handle, (nuint)chan, &readback), "uhd_usrp_get_rx_freq");
        return new UhdTuneResult(res.clipped_rf_freq, res.target_rf_freq, res.actual_rf_freq,
                                 res.target_dsp_freq, res.actual_dsp_freq, readback);
    });

    /// <summary>
    /// Move ONLY channel <paramref name="chan"/>'s DDC so its samples are centred on <paramref name="targetHz"/>,
    /// the RF LO left where it is (rf policy NONE, dsp AUTO) - how the second side of a B210 is tuned, because its
    /// one RX synthesizer is the first side's too (ShackLink docs/usrp_b210_support.md section 9.23 S1). The DDC
    /// shift is what reaches the target from the LO now; the read-back is the centre the channel is actually on.
    /// </summary>
    public UhdTuneResult TuneRxDspOnly(double targetHz, int chan) => Locked(() =>
    {
        var req = new uhd_tune_request_t
        {
            target_freq = targetHz,
            rf_freq_policy = PolicyNone,
            rf_freq = 0.0,
            dsp_freq_policy = PolicyAuto,
            dsp_freq = 0.0,
            args = IntPtr.Zero,
        };
        uhd_tune_result_t res;
        Check(uhd_usrp_set_rx_freq(Handle, &req, (nuint)chan, &res), "uhd_usrp_set_rx_freq");
        double readback;
        Check(uhd_usrp_get_rx_freq(Handle, (nuint)chan, &readback), "uhd_usrp_get_rx_freq");
        return new UhdTuneResult(res.clipped_rf_freq, res.target_rf_freq, res.actual_rf_freq,
                                 res.target_dsp_freq, res.actual_dsp_freq, readback);
    });

    public double GetRxFrequency(int chan = 0) => Locked(() =>
    {
        double v;
        Check(uhd_usrp_get_rx_freq(Handle, (nuint)chan, &v), "uhd_usrp_get_rx_freq");
        return v;
    });

    public UhdMetaRange GetRxFrequencyRange(int chan = 0) =>
        Locked(() => ReadMetaRange(r => uhd_usrp_get_rx_freq_range(Handle, (nuint)chan, r), "uhd_usrp_get_rx_freq_range"));

    public UhdMetaRange GetFrontEndRxFrequencyRange(int chan = 0) =>
        Locked(() => ReadMetaRange(r => uhd_usrp_get_fe_rx_freq_range(Handle, (nuint)chan, r), "uhd_usrp_get_fe_rx_freq_range"));

    // ------------------------------------------------------------------ RX: gain, AGC, bandwidth, antenna

    /// <summary>Gain name "" = the overall gain UHD distributes over the chain.</summary>
    public double SetRxGain(double db, int chan = 0, string name = "") => Locked(() =>
    {
        IntPtr n = AllocAnsi(name);
        try
        {
            Check(uhd_usrp_set_rx_gain(Handle, db, (nuint)chan, (byte*)n), "uhd_usrp_set_rx_gain");
            double v;
            Check(uhd_usrp_get_rx_gain(Handle, (nuint)chan, (byte*)n, &v), "uhd_usrp_get_rx_gain");
            return v;
        }
        finally { Marshal.FreeHGlobal(n); }
    });

    public double GetRxGain(int chan = 0, string name = "") => Locked(() =>
    {
        IntPtr n = AllocAnsi(name);
        try
        {
            double v;
            Check(uhd_usrp_get_rx_gain(Handle, (nuint)chan, (byte*)n, &v), "uhd_usrp_get_rx_gain");
            return v;
        }
        finally { Marshal.FreeHGlobal(n); }
    });

    public UhdMetaRange GetRxGainRange(int chan = 0, string name = "") => Locked(() =>
    {
        IntPtr n = AllocAnsi(name);
        try { return ReadMetaRange(r => uhd_usrp_get_rx_gain_range(Handle, (byte*)n, (nuint)chan, r), "uhd_usrp_get_rx_gain_range"); }
        finally { Marshal.FreeHGlobal(n); }
    });

    public IReadOnlyList<string> GetRxGainNames(int chan = 0) =>
        Locked(() => ReadDeviceStrings(v => uhd_usrp_get_rx_gain_names(Handle, (nuint)chan, (IntPtr*)v), "uhd_usrp_get_rx_gain_names"));

    public void SetRxAgc(bool enable, int chan = 0) => Locked(() =>
    {
        Check(uhd_usrp_set_rx_agc(Handle, enable ? (byte)1 : (byte)0, (nuint)chan), "uhd_usrp_set_rx_agc");
        return 0;
    });

    public double SetRxBandwidth(double hz, int chan = 0) => Locked(() =>
    {
        Check(uhd_usrp_set_rx_bandwidth(Handle, hz, (nuint)chan), "uhd_usrp_set_rx_bandwidth");
        double v;
        Check(uhd_usrp_get_rx_bandwidth(Handle, (nuint)chan, &v), "uhd_usrp_get_rx_bandwidth");
        return v;
    });

    public double GetRxBandwidth(int chan = 0) => Locked(() =>
    {
        double v;
        Check(uhd_usrp_get_rx_bandwidth(Handle, (nuint)chan, &v), "uhd_usrp_get_rx_bandwidth");
        return v;
    });

    public UhdMetaRange GetRxBandwidthRange(int chan = 0) =>
        Locked(() => ReadMetaRange(r => uhd_usrp_get_rx_bandwidth_range(Handle, (nuint)chan, r), "uhd_usrp_get_rx_bandwidth_range"));

    public string GetRxAntenna(int chan = 0) =>
        Locked(() => ReadDeviceString((b, l) => uhd_usrp_get_rx_antenna(Handle, (nuint)chan, (byte*)b, l), "uhd_usrp_get_rx_antenna"));

    public IReadOnlyList<string> GetRxAntennas(int chan = 0) =>
        Locked(() => ReadDeviceStrings(v => uhd_usrp_get_rx_antennas(Handle, (nuint)chan, (IntPtr*)v), "uhd_usrp_get_rx_antennas"));

    public string SetRxAntenna(string antenna, int chan = 0) => Locked(() =>
    {
        IntPtr a = AllocAnsi(antenna);
        try { Check(uhd_usrp_set_rx_antenna(Handle, (byte*)a, (nuint)chan), "uhd_usrp_set_rx_antenna"); }
        finally { Marshal.FreeHGlobal(a); }
        return ReadDeviceString((b, l) => uhd_usrp_get_rx_antenna(Handle, (nuint)chan, (byte*)b, l), "uhd_usrp_get_rx_antenna");
    });

    public void SetRxDcOffsetEnabled(bool enable, int chan = 0) => Locked(() =>
    {
        Check(uhd_usrp_set_rx_dc_offset_enabled(Handle, enable ? (byte)1 : (byte)0, (nuint)chan), "uhd_usrp_set_rx_dc_offset_enabled");
        return 0;
    });

    public void SetRxIqBalanceEnabled(bool enable, int chan = 0) => Locked(() =>
    {
        Check(uhd_usrp_set_rx_iq_balance_enabled(Handle, enable ? (byte)1 : (byte)0, (nuint)chan), "uhd_usrp_set_rx_iq_balance_enabled");
        return 0;
    });

    // ------------------------------------------------------------------ TX (docs/usrp_b210_support.md section 9.17 P4-b)

    public int TxChannelCount => Locked(() =>
    {
        nuint n;
        Check(uhd_usrp_get_tx_num_channels(Handle, &n), "uhd_usrp_get_tx_num_channels");
        return (int)n;
    });

    /// <summary>Set the DUC's host rate and return what it READ BACK. On a B2xx with automatic clock
    /// selection this joins the LCM the master clock is chosen from (b200_io_impl.cpp
    /// set_auto_tick_rate), so the caller admits only rates that divide the wide rate.</summary>
    public double SetTxRate(double hz, int chan = 0) => Locked(() =>
    {
        Check(uhd_usrp_set_tx_rate(Handle, hz, (nuint)chan), "uhd_usrp_set_tx_rate");
        double v;
        Check(uhd_usrp_get_tx_rate(Handle, (nuint)chan, &v), "uhd_usrp_get_tx_rate");
        return v;
    });

    public double GetTxRate(int chan = 0) => Locked(() =>
    {
        double v;
        Check(uhd_usrp_get_tx_rate(Handle, (nuint)chan, &v), "uhd_usrp_get_tx_rate");
        return v;
    });

    /// <summary>The DUC's host rates at the CURRENT master clock: tick / n for each interpolation the
    /// core offers (tx_dsp_core_3000.cpp get_host_rates) - UHD's answer, never a datasheet's.</summary>
    public UhdMetaRange GetTxRates(int chan = 0) =>
        Locked(() => ReadMetaRange(r => uhd_usrp_get_tx_rates(Handle, (nuint)chan, r), "uhd_usrp_get_tx_rates"));

    /// <summary>Tune the transmitter so baseband 0 Hz leaves at <paramref name="targetHz"/>, the
    /// synthesizer placed by <paramref name="policy"/> (<see cref="UhdTxTunePolicy"/>). UHD's own tune
    /// request resolves it; the frequency is read back afterwards (hazard 6: a tune that returns OK is
    /// not evidence that it tuned). The DUC's CORDIC takes the difference with the TX sign UHD applies.</summary>
    public UhdTuneResult TuneTx(double targetHz, UhdTxTunePolicy policy, double loOffsetHz = 0.0, int chan = 0) => Locked(() =>
    {
        var req = new uhd_tune_request_t
        {
            target_freq = targetHz,
            rf_freq_policy = policy switch
            {
                UhdTxTunePolicy.LoOffset => PolicyManual,
                UhdTxTunePolicy.KeepLo => PolicyNone,
                _ => PolicyAuto,
            },
            rf_freq = policy == UhdTxTunePolicy.LoOffset ? targetHz + loOffsetHz : 0.0,
            dsp_freq_policy = PolicyAuto,
            dsp_freq = 0.0,
            args = IntPtr.Zero,
        };
        uhd_tune_result_t res;
        Check(uhd_usrp_set_tx_freq(Handle, &req, (nuint)chan, &res), "uhd_usrp_set_tx_freq");
        double readback;
        Check(uhd_usrp_get_tx_freq(Handle, (nuint)chan, &readback), "uhd_usrp_get_tx_freq");
        return new UhdTuneResult(res.clipped_rf_freq, res.target_rf_freq, res.actual_rf_freq,
                                 res.target_dsp_freq, res.actual_dsp_freq, readback);
    });

    public double GetTxFrequency(int chan = 0) => Locked(() =>
    {
        double v;
        Check(uhd_usrp_get_tx_freq(Handle, (nuint)chan, &v), "uhd_usrp_get_tx_freq");
        return v;
    });

    public UhdMetaRange GetTxFrequencyRange(int chan = 0) =>
        Locked(() => ReadMetaRange(r => uhd_usrp_get_tx_freq_range(Handle, (nuint)chan, r), "uhd_usrp_get_tx_freq_range"));

    /// <summary>Gain name "" = the overall gain. Returns the value READ BACK (the B2xx's grid is 0.25 dB).</summary>
    public double SetTxGain(double db, int chan = 0, string name = "") => Locked(() =>
    {
        IntPtr n = AllocAnsi(name);
        try
        {
            Check(uhd_usrp_set_tx_gain(Handle, db, (nuint)chan, (byte*)n), "uhd_usrp_set_tx_gain");
            double v;
            Check(uhd_usrp_get_tx_gain(Handle, (nuint)chan, (byte*)n, &v), "uhd_usrp_get_tx_gain");
            return v;
        }
        finally { Marshal.FreeHGlobal(n); }
    });

    public double GetTxGain(int chan = 0, string name = "") => Locked(() =>
    {
        IntPtr n = AllocAnsi(name);
        try
        {
            double v;
            Check(uhd_usrp_get_tx_gain(Handle, (nuint)chan, (byte*)n, &v), "uhd_usrp_get_tx_gain");
            return v;
        }
        finally { Marshal.FreeHGlobal(n); }
    });

    public UhdMetaRange GetTxGainRange(int chan = 0, string name = "") => Locked(() =>
    {
        IntPtr n = AllocAnsi(name);
        try { return ReadMetaRange(r => uhd_usrp_get_tx_gain_range(Handle, (byte*)n, (nuint)chan, r), "uhd_usrp_get_tx_gain_range"); }
        finally { Marshal.FreeHGlobal(n); }
    });

    public string GetTxAntenna(int chan = 0) =>
        Locked(() => ReadDeviceString((b, l) => uhd_usrp_get_tx_antenna(Handle, (nuint)chan, (byte*)b, l), "uhd_usrp_get_tx_antenna"));

    public IReadOnlyList<string> GetTxAntennas(int chan = 0) =>
        Locked(() => ReadDeviceStrings(v => uhd_usrp_get_tx_antennas(Handle, (nuint)chan, (IntPtr*)v), "uhd_usrp_get_tx_antennas"));

    /// <summary>A TX streamer taking interleaved sc16 from the host, sc16 over USB. Its EXISTENCE enables
    /// the AD9361's TX chain (b200_impl.cpp update_enables) and so disturbs the receiver once; and
    /// creating one re-applies whatever TX rate UHD holds (get_tx_stream's rate update), so the caller
    /// sets an admitted rate FIRST. It is NOT registered for <see cref="Dispose"/>: it belongs to the one
    /// thread that drives it, and its owner ends that thread before the device goes.</summary>
    public UhdTxStreamer CreateTxStreamer(int chan = 0) => Locked(() => new UhdTxStreamer(this, "sc16", "sc16", "", chan));

    // ------------------------------------------------------------------ streaming

    /// <summary>An RX streamer delivering interleaved sc16 on the host. <paramref name="otwFormat"/> is
    /// what crosses USB: "sc16" (4 B per sample) or "sc8" (2 B, the §2.4 throughput lever).</summary>
    public UhdRxStreamer CreateRxStreamer(string otwFormat = "sc16", string streamArgs = "", int chan = 0) =>
        CreateRxStreamer(otwFormat, streamArgs, new[] { chan });

    /// <summary>ONE streamer over <paramref name="channels"/> - UHD's "MIMO time" when there are two: an overflow
    /// stops, flushes and restarts both together, so the channels stay sample-aligned (b200_io_impl.cpp
    /// handle_overflow; ShackLink docs/usrp_b210_support.md section 9.23 S5).</summary>
    public UhdRxStreamer CreateRxStreamer(string otwFormat, string streamArgs, int[] channels)
    {
        lock (_gate)
        {
            var s = new UhdRxStreamer(this, "sc16", otwFormat, streamArgs, channels);
            _streamers.Add(s);
            return s;
        }
    }

    internal void Forget(UhdRxStreamer s)
    {
        lock (_gate) _streamers.Remove(s);
    }

    /// <summary>Real-time priority for the calling thread (uhd_set_thread_priority) - what UHD's own
    /// examples do on their recv thread. Returns the error rather than throwing: an unprivileged
    /// process may be refused, and the stream still runs.</summary>
    public static UhdError TrySetThreadPriority(float priority = 0.5f, bool realtime = true)
    {
        EnsureLoaded();
        return uhd_set_thread_priority(priority, realtime ? (byte)1 : (byte)0);
    }

    public void Dispose()
    {
        UhdRxStreamer[] live;
        lock (_gate) live = _streamers.ToArray();
        // A streamer must go before its device: freeing the usrp under a live rx_streamer leaves the
        // streamer holding a device the C API's registry no longer knows.
        foreach (var s in live) s.Dispose();
        lock (_gate)
        {
            if (_h != IntPtr.Zero)
            {
                IntPtr h = _h;
                uhd_usrp_free(&h);
                _h = IntPtr.Zero;
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private T Locked<T>(Func<T> body)
    {
        lock (_gate) return body();
    }

    private static void EnsureLoaded()
    {
        if (!UhdLoader.IsLoaded)
            throw new InvalidOperationException("UHD is not loaded - call UhdLoader.Load first.");
    }

    internal static string GlobalLastError() =>
        ReadString((b, l) => uhd_get_last_error((byte*)b, l), out _);

    private string DeviceLastError()
    {
        IntPtr h = _h;
        if (h != IntPtr.Zero)
        {
            string s = ReadString((b, l) => uhd_usrp_last_error(h, (byte*)b, l), out UhdError e);
            if (e == UhdError.None && !string.IsNullOrWhiteSpace(s) && s != "None") return s;
        }
        return GlobalLastError();
    }

    private void Check(UhdError e, string call)
    {
        if (e != UhdError.None) throw new UhdException(e, call, DeviceLastError());
    }

    private static void CheckGlobal(UhdError e, string call)
    {
        if (e != UhdError.None) throw new UhdException(e, call, GlobalLastError());
    }

    private string ReadDeviceString(Func<IntPtr, nuint, UhdError> fill, string call, int bytes = StringBufferBytes)
    {
        string s = ReadString(fill, out UhdError e, bytes);
        Check(e, call);
        return s;
    }

    /// <summary><paramref name="fillPtrToHandle"/> receives the ADDRESS of the vector handle (the C
    /// API's uhd_string_vector_handle* out-parameter), as an IntPtr the lambda casts back.</summary>
    private IReadOnlyList<string> ReadDeviceStrings(Func<IntPtr, UhdError> fillPtrToHandle, string call)
    {
        IntPtr v = IntPtr.Zero;
        CheckGlobal(uhd_string_vector_make(&v), "uhd_string_vector_make");
        try
        {
            IntPtr* pv = &v;
            Check(fillPtrToHandle((IntPtr)pv), call);
            return ReadStrings(v);
        }
        finally
        {
            uhd_string_vector_free(&v);
        }
    }

    private static List<string> ReadStrings(IntPtr v)
    {
        nuint n;
        CheckGlobal(uhd_string_vector_size(v, &n), "uhd_string_vector_size");
        var list = new List<string>((int)n);
        for (nuint i = 0; i < n; i++)
        {
            nuint idx = i;
            string s = ReadString((b, l) => uhd_string_vector_at(v, idx, (byte*)b, l), out UhdError e);
            CheckGlobal(e, "uhd_string_vector_at");
            list.Add(s);
        }
        return list;
    }

    private UhdMetaRange ReadMetaRange(Func<IntPtr, UhdError> fill, string call)
    {
        IntPtr r = IntPtr.Zero;
        CheckGlobal(uhd_meta_range_make(&r), "uhd_meta_range_make");
        try
        {
            Check(fill(r), call);
            nuint n;
            CheckGlobal(uhd_meta_range_size(r, &n), "uhd_meta_range_size");
            var ranges = new List<UhdRange>((int)n);
            for (nuint i = 0; i < n; i++)
            {
                uhd_range_t one;
                CheckGlobal(uhd_meta_range_at(r, i, &one), "uhd_meta_range_at");
                ranges.Add(new UhdRange(one.start, one.stop, one.step));
            }
            // start/stop/step throw inside UHD on an EMPTY meta range; report empty instead.
            if (n == 0) return new UhdMetaRange(double.NaN, double.NaN, double.NaN, ranges);
            double start, stop, step;
            CheckGlobal(uhd_meta_range_start(r, &start), "uhd_meta_range_start");
            CheckGlobal(uhd_meta_range_stop(r, &stop), "uhd_meta_range_stop");
            CheckGlobal(uhd_meta_range_step(r, &step), "uhd_meta_range_step");
            return new UhdMetaRange(start, stop, step, ranges);
        }
        finally
        {
            uhd_meta_range_free(&r);
        }
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"UhdUsrp({Args})");
}
