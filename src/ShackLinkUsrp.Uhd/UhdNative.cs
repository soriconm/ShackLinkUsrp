// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace ShackLinkUsrp.Uhd;

/// <summary>uhd_error (uhd/error.h). Every C API call returns one; only NONE is success.</summary>
public enum UhdError
{
    None = 0,
    InvalidDevice = 1,
    Index = 10,
    Key = 11,
    NotImplemented = 20,
    Usb = 21,
    Io = 30,
    Os = 31,
    Assertion = 40,
    Lookup = 41,
    Type = 42,
    Value = 43,
    Runtime = 44,
    Environment = 45,
    System = 46,
    Except = 47,
    BoostExcept = 60,
    StdExcept = 70,
    Unknown = 100,
}

/// <summary>uhd_rx_metadata_error_code_t (uhd/types/metadata.h).</summary>
public enum UhdRxErrorCode
{
    None = 0x0,
    Timeout = 0x1,
    LateCommand = 0x2,
    BrokenChain = 0x4,
    Overflow = 0x8,
    Alignment = 0xC,
    BadPacket = 0xF,
}

/// <summary>uhd_async_metadata_event_code_t (uhd/types/metadata.h): what a TX streamer's async
/// channel reports. A bit set, though UHD sends one code per message.</summary>
[Flags]
public enum UhdAsyncEventCode
{
    None = 0x0,
    /// <summary>The end-of-burst was transmitted: the last sample left the radio.</summary>
    BurstAck = 0x1,
    /// <summary>The radio ran out of samples mid-burst (it sends zeros until more arrive).</summary>
    Underflow = 0x2,
    /// <summary>Packets were lost between the host and the radio.</summary>
    SeqError = 0x4,
    TimeError = 0x8,
    UnderflowInPacket = 0x10,
    SeqErrorInBurst = 0x20,
    UserPayload = 0x40,
}

/// <summary>
/// Raw P/Invoke over UHD's C API (uhd.h). Every declaration here was read against the installed
/// UHD 4.11.0.0 headers (C:\Program Files\UHD\include\uhd\...) and the 4.10 source tree
/// (C:\Dev\USRP_B210\uhd-4.10-diagnostic\host\include\uhd\...); the nine C headers used are
/// byte-identical between the two (CR stripped), so one binding serves 4.8 through 4.11.
/// "uhd" resolves through <see cref="UhdLoader"/>'s import resolver to the one uhd.dll it loaded -
/// never to whatever copy the loader's default search would have found first.
/// </summary>
internal static unsafe class UhdNative
{
    internal const string Lib = "uhd";

    // ---------------------------------------------------------------- structs

    /// <summary>uhd/types/tune_request.h — uhd_tune_request_policy_t is ASCII, not 0/1/2. A zeroed
    /// policy field is an INVALID value: set_rx_freq then "succeeds" without tuning, which is the
    /// undiagnosed 2020 usrp-users C# report (docs/usrp_b210_support.md §2.2a, hazard 6).</summary>
    internal const int PolicyNone = 78;   // 'N'
    internal const int PolicyAuto = 65;   // 'A'
    internal const int PolicyManual = 77; // 'M'

    /// <summary>uhd/types/tune_request.h: { double target_freq; policy rf_freq_policy; double rf_freq;
    /// policy dsp_freq_policy; double dsp_freq; char* args; }. The enum is an MSVC int (4 bytes), so
    /// each policy is followed by 4 bytes of padding. <c>args</c> is a char* - marshalled as an
    /// IntPtr we own, never as a string field (tune_c.cpp accepts NULL for it).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct uhd_tune_request_t
    {
        public double target_freq;
        public int rf_freq_policy;
        public double rf_freq;
        public int dsp_freq_policy;
        public double dsp_freq;
        public IntPtr args;
    }

    /// <summary>uhd/types/tune_result.h: five doubles.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct uhd_tune_result_t
    {
        public double clipped_rf_freq;
        public double target_rf_freq;
        public double actual_rf_freq;
        public double target_dsp_freq;
        public double actual_dsp_freq;
    }

    /// <summary>uhd/types/usrp_info.h: eight char*, each strdup'd by usrp_c.cpp's COPY_INFO_FIELD
    /// and released by uhd_usrp_rx_info_free - which is why they stay IntPtr until read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct uhd_usrp_rx_info_t
    {
        public IntPtr mboard_id;
        public IntPtr mboard_name;
        public IntPtr mboard_serial;
        public IntPtr rx_id;
        public IntPtr rx_subdev_name;
        public IntPtr rx_subdev_spec;
        public IntPtr rx_serial;
        public IntPtr rx_antenna;
    }

    /// <summary>uhd/types/ranges.h: { double start; double stop; double step; }.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct uhd_range_t
    {
        public double start;
        public double stop;
        public double step;
    }

    /// <summary>uhd/usrp/usrp.h: { char* cpu_format; char* otw_format; char* args; size_t*
    /// channel_list; int n_channels; }. NOTE the int beside a size_t* (hazard 6). usrp_c.cpp's
    /// stream_args_c_to_cpp builds std::string from all three char* WITHOUT a null check, so each
    /// must point at a real string - "" for no args, never NULL.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct uhd_stream_args_t
    {
        public IntPtr cpu_format;
        public IntPtr otw_format;
        public IntPtr args;
        public IntPtr channel_list;
        public int n_channels;
    }

    /// <summary>uhd_stream_mode_t (uhd/usrp/usrp.h) - ASCII again: 'a' start, 'o' stop, 'd'/'m'
    /// num-samps-and-done/more.</summary>
    internal const int StreamModeStartContinuous = 97;
    internal const int StreamModeStopContinuous = 111;
    internal const int StreamModeNumSampsAndDone = 100;
    internal const int StreamModeNumSampsAndMore = 109;

    /// <summary>uhd/usrp/usrp.h: { uhd_stream_mode_t stream_mode; size_t num_samps; bool stream_now;
    /// int64_t time_spec_full_secs; double time_spec_frac_secs; }. <c>stream_now</c> is a ONE-byte
    /// C/C++ bool, so it is a byte here: a C# bool field would marshal as a 4-byte Win32 BOOL and
    /// shift everything after it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct uhd_stream_cmd_t
    {
        public int stream_mode;
        public nuint num_samps;
        public byte stream_now;
        public long time_spec_full_secs;
        public double time_spec_frac_secs;
    }

    /// <summary>The sizes and offsets the headers imply for x64 MSVC (natural alignment, 4-byte
    /// enums, 1-byte bool). Checked at load, before any call can hand UHD a mis-laid struct - the
    /// rade_native rule: a layout is asserted, never assumed.</summary>
    internal static string LayoutProblem()
    {
        var problems = new List<string>();
        void Size<T>(int expected) where T : struct
        {
            int actual = Marshal.SizeOf<T>();
            if (actual != expected) problems.Add($"sizeof({typeof(T).Name}) = {actual}, header implies {expected}");
        }
        void Off<T>(string field, int expected) where T : struct
        {
            int actual = (int)Marshal.OffsetOf<T>(field);
            if (actual != expected) problems.Add($"offsetof({typeof(T).Name}.{field}) = {actual}, header implies {expected}");
        }
        if (IntPtr.Size != 8) problems.Add("this binding is x64-only");

        Size<uhd_tune_request_t>(48);
        Off<uhd_tune_request_t>(nameof(uhd_tune_request_t.target_freq), 0);
        Off<uhd_tune_request_t>(nameof(uhd_tune_request_t.rf_freq_policy), 8);
        Off<uhd_tune_request_t>(nameof(uhd_tune_request_t.rf_freq), 16);
        Off<uhd_tune_request_t>(nameof(uhd_tune_request_t.dsp_freq_policy), 24);
        Off<uhd_tune_request_t>(nameof(uhd_tune_request_t.dsp_freq), 32);
        Off<uhd_tune_request_t>(nameof(uhd_tune_request_t.args), 40);

        Size<uhd_tune_result_t>(40);
        Off<uhd_tune_result_t>(nameof(uhd_tune_result_t.actual_dsp_freq), 32);

        Size<uhd_usrp_rx_info_t>(64);
        Off<uhd_usrp_rx_info_t>(nameof(uhd_usrp_rx_info_t.rx_antenna), 56);

        Size<uhd_range_t>(24);

        Size<uhd_stream_args_t>(40);
        Off<uhd_stream_args_t>(nameof(uhd_stream_args_t.channel_list), 24);
        Off<uhd_stream_args_t>(nameof(uhd_stream_args_t.n_channels), 32);

        Size<uhd_stream_cmd_t>(40);
        Off<uhd_stream_cmd_t>(nameof(uhd_stream_cmd_t.num_samps), 8);
        Off<uhd_stream_cmd_t>(nameof(uhd_stream_cmd_t.stream_now), 16);
        Off<uhd_stream_cmd_t>(nameof(uhd_stream_cmd_t.time_spec_full_secs), 24);
        Off<uhd_stream_cmd_t>(nameof(uhd_stream_cmd_t.time_spec_frac_secs), 32);

        return problems.Count == 0 ? "" : string.Join("; ", problems);
    }

    /// <summary>
    /// The TX metadata is the one TX type that is not a struct: <c>uhd_tx_metadata_make</c> takes its
    /// flags BY VALUE as C++ <c>bool</c>s and there is no setter afterwards (metadata_c.cpp), so a
    /// mis-marshalled flag would silently send a burst with no end - the amplifier held on. Asserted at
    /// load the only way a by-value parameter can be: make one of each shape and read every flag back
    /// through UHD's own getters. "" = sound.
    /// </summary>
    internal static string TxMetadataProblem()
    {
        var problems = new List<string>();
        foreach (var (sob, eob) in new[] { (true, false), (false, true), (false, false), (true, true) })
        {
            IntPtr md = IntPtr.Zero;
            UhdError e = uhd_tx_metadata_make(&md, 0, 0, 0.0, sob ? (byte)1 : (byte)0, eob ? (byte)1 : (byte)0);
            if (e != UhdError.None || md == IntPtr.Zero)
            {
                problems.Add($"uhd_tx_metadata_make(sob={sob}, eob={eob}) failed: {e}");
                continue;
            }
            try
            {
                byte has, s, en;
                uhd_tx_metadata_has_time_spec(md, &has);
                uhd_tx_metadata_start_of_burst(md, &s);
                uhd_tx_metadata_end_of_burst(md, &en);
                if (has != 0 || (s != 0) != sob || (en != 0) != eob)
                    problems.Add($"a TX metadata made with sob={sob}, eob={eob}, no time spec reads back sob={s}, eob={en}, has_time_spec={has}");
            }
            finally
            {
                uhd_tx_metadata_free(&md);
            }
        }
        return problems.Count == 0 ? "" : string.Join("; ", problems);
    }

    // ---------------------------------------------------------------- version.h / error.h

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_get_version_string(byte* version_out, nuint buffer_len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_get_abi_string(byte* abi_string_out, nuint buffer_len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_get_last_error(byte* error_out, nuint strbuffer_len);

    // ---------------------------------------------------------------- utils/thread_priority.h

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_set_thread_priority(float priority, byte realtime);

    // ---------------------------------------------------------------- types/string_vector.h
    // A vector must be MADE before a call fills it: usrp_c.cpp writes (*strings_out)->... .

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_string_vector_make(IntPtr* h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_string_vector_free(IntPtr* h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_string_vector_size(IntPtr h, nuint* size_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_string_vector_at(IntPtr h, nuint index, byte* value_out, nuint strbuffer_len);

    // ---------------------------------------------------------------- types/ranges.h
    // Likewise pre-made: uhd_usrp_get_rx_rates writes rates_out->meta_range_cpp.

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_meta_range_make(IntPtr* h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_meta_range_free(IntPtr* h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_meta_range_start(IntPtr h, double* start_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_meta_range_stop(IntPtr h, double* stop_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_meta_range_step(IntPtr h, double* step_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_meta_range_size(IntPtr h, nuint* size_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_meta_range_at(IntPtr h, nuint num, uhd_range_t* range_out);

    // ---------------------------------------------------------------- usrp/usrp.h: device

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_find(byte* args, IntPtr* strings_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_make(IntPtr* h, byte* args);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_free(IntPtr* h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_last_error(IntPtr h, byte* error_out, nuint strbuffer_len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_pp_string(IntPtr h, byte* pp_string_out, nuint strbuffer_len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_mboard_name(IntPtr h, nuint mboard, byte* mboard_name_out, nuint strbuffer_len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_rx_info(IntPtr h, nuint chan, uhd_usrp_rx_info_t* info_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_rx_info_free(uhd_usrp_rx_info_t* rx_info);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_set_master_clock_rate(IntPtr h, double rate, nuint mboard);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_master_clock_rate(IntPtr h, nuint mboard, double* clock_rate_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_set_clock_source(IntPtr h, byte* clock_source, nuint mboard);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_clock_source(IntPtr h, nuint mboard, byte* clock_source_out, nuint strbuffer_len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_clock_sources(IntPtr h, nuint mboard, IntPtr* clock_sources_out);

    // ---------------------------------------------------------------- usrp/usrp.h: RX chain
    // Parameter ORDER differs between siblings (set_rx_gain: gain, chan, name; get_rx_gain: chan,
    // name, out; get_rx_gain_range: name, chan, out) - each line below is copied from the header.

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_rx_num_channels(IntPtr h, nuint* num_channels_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_set_rx_rate(IntPtr h, double rate, nuint chan);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_rx_rate(IntPtr h, nuint chan, double* rate_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_rx_rates(IntPtr h, nuint chan, IntPtr rates_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_set_rx_freq(IntPtr h, uhd_tune_request_t* tune_request, nuint chan, uhd_tune_result_t* tune_result);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_rx_freq(IntPtr h, nuint chan, double* freq_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_rx_freq_range(IntPtr h, nuint chan, IntPtr freq_range_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_fe_rx_freq_range(IntPtr h, nuint chan, IntPtr freq_range_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_set_rx_gain(IntPtr h, double gain, nuint chan, byte* gain_name);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_rx_gain(IntPtr h, nuint chan, byte* gain_name, double* gain_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_rx_gain_range(IntPtr h, byte* name, nuint chan, IntPtr gain_range_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_rx_gain_names(IntPtr h, nuint chan, IntPtr* gain_names_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_set_rx_agc(IntPtr h, byte enable, nuint chan);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_set_rx_bandwidth(IntPtr h, double bandwidth, nuint chan);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_rx_bandwidth(IntPtr h, nuint chan, double* bandwidth_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_rx_bandwidth_range(IntPtr h, nuint chan, IntPtr bandwidth_range_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_set_rx_antenna(IntPtr h, byte* ant, nuint chan);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_rx_antenna(IntPtr h, nuint chan, byte* ant_out, nuint strbuffer_len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_rx_antennas(IntPtr h, nuint chan, IntPtr* antennas_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_set_rx_dc_offset_enabled(IntPtr h, byte enb, nuint chan);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_set_rx_iq_balance_enabled(IntPtr h, byte enb, nuint chan);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_rx_stream(IntPtr h, uhd_stream_args_t* stream_args, IntPtr h_out);

    // ---------------------------------------------------------------- usrp/usrp.h: RX streamer

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_rx_streamer_make(IntPtr* h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_rx_streamer_free(IntPtr* h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_rx_streamer_num_channels(IntPtr h, nuint* num_channels_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_rx_streamer_max_num_samps(IntPtr h, nuint* max_num_samps_out);

    /// <summary><c>buffs</c> is void** (one pointer per channel) and <c>md</c> is a POINTER TO the
    /// metadata handle - both straight from usrp.h.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_rx_streamer_recv(IntPtr h, void** buffs, nuint samps_per_buff, IntPtr* md, double timeout, byte one_packet, nuint* items_recvd);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_rx_streamer_issue_stream_cmd(IntPtr h, uhd_stream_cmd_t* stream_cmd);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_rx_streamer_last_error(IntPtr h, byte* error_out, nuint strbuffer_len);

    // ---------------------------------------------------------------- types/metadata.h: RX

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_rx_metadata_make(IntPtr* handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_rx_metadata_free(IntPtr* handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_rx_metadata_has_time_spec(IntPtr h, byte* result_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_rx_metadata_time_spec(IntPtr h, long* full_secs_out, double* frac_secs_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_rx_metadata_out_of_sequence(IntPtr h, byte* result_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_rx_metadata_error_code(IntPtr h, int* error_code_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_rx_metadata_strerror(IntPtr h, byte* strerror_out, nuint strbuffer_len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_rx_metadata_last_error(IntPtr h, byte* error_out, nuint strbuffer_len);

    // ---------------------------------------------------------------- usrp/usrp.h: TX chain
    // docs/usrp_b210_support.md section 9.17 P4-b. Same parameter-order trap as the RX siblings: each
    // line is copied from usrp.h (set_tx_gain: gain, chan, name; get_tx_gain: chan, name, out;
    // get_tx_gain_range: name, chan, out).

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_tx_num_channels(IntPtr h, nuint* num_channels_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_set_tx_rate(IntPtr h, double rate, nuint chan);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_tx_rate(IntPtr h, nuint chan, double* rate_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_tx_rates(IntPtr h, nuint chan, IntPtr rates_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_set_tx_freq(IntPtr h, uhd_tune_request_t* tune_request, nuint chan, uhd_tune_result_t* tune_result);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_tx_freq(IntPtr h, nuint chan, double* freq_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_tx_freq_range(IntPtr h, nuint chan, IntPtr freq_range_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_set_tx_gain(IntPtr h, double gain, nuint chan, byte* gain_name);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_tx_gain(IntPtr h, nuint chan, byte* gain_name, double* gain_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_tx_gain_range(IntPtr h, byte* name, nuint chan, IntPtr gain_range_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_tx_antenna(IntPtr h, nuint chan, byte* ant_out, nuint strbuffer_len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_tx_antennas(IntPtr h, nuint chan, IntPtr* antennas_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_usrp_get_tx_stream(IntPtr h, uhd_stream_args_t* stream_args, IntPtr h_out);

    // ---------------------------------------------------------------- usrp/usrp.h: TX streamer
    // usrp_c.cpp wraps every call below in UHD_SAFE_C_SAVE_ERROR(h, ...), which WRITES the handle's
    // std::string last_error on every call, success included - so two threads calling into ONE TX
    // streamer race on that string. UhdTxStreamer is therefore driven by one thread only: its send
    // and its async-message poll are the same thread's calls (UHD's own examples use a second thread
    // for recv_async_msg; through the C API that is a data race).

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_tx_streamer_make(IntPtr* h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_tx_streamer_free(IntPtr* h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_tx_streamer_max_num_samps(IntPtr h, nuint* max_num_samps_out);

    /// <summary><c>buffs</c> is const void** (one pointer per channel) and <c>md</c> a POINTER TO the
    /// metadata handle - both straight from usrp.h, as for recv.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_tx_streamer_send(IntPtr h, void** buffs, nuint samps_per_buff, IntPtr* md, double timeout, nuint* items_sent);

    /// <summary><c>valid</c> is a C++ <c>bool*</c>: one byte.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_tx_streamer_recv_async_msg(IntPtr h, IntPtr* md, double timeout, byte* valid);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_tx_streamer_last_error(IntPtr h, byte* error_out, nuint strbuffer_len);

    // ---------------------------------------------------------------- types/metadata.h: TX and async

    /// <summary>The flags are C++ <c>bool</c>s passed BY VALUE (one byte each; x64 passes them in a
    /// register and the callee reads the low byte) - asserted at load by <see cref="TxMetadataProblem"/>.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_tx_metadata_make(IntPtr* handle, byte has_time_spec, long full_secs, double frac_secs, byte start_of_burst, byte end_of_burst);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_tx_metadata_free(IntPtr* handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_tx_metadata_has_time_spec(IntPtr h, byte* result_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_tx_metadata_start_of_burst(IntPtr h, byte* result_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_tx_metadata_end_of_burst(IntPtr h, byte* result_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_async_metadata_make(IntPtr* handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_async_metadata_free(IntPtr* handle);

    /// <summary>The out-parameter is uhd_async_metadata_event_code_t, an MSVC enum: 4 bytes.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_async_metadata_event_code(IntPtr h, int* event_code_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_async_metadata_has_time_spec(IntPtr h, byte* result_out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern UhdError uhd_async_metadata_time_spec(IntPtr h, long* full_secs_out, double* frac_secs_out);

    // ---------------------------------------------------------------- string helpers

    internal const int StringBufferBytes = 4096;

    /// <summary>Call a UHD "fill this char buffer" function and read the NUL-terminated result.
    /// UHD strings are narrow and in practice ASCII (identities, antenna names, messages).</summary>
    internal static string ReadString(Func<IntPtr, nuint, UhdError> fill, out UhdError error, int bytes = StringBufferBytes)
    {
        IntPtr buf = Marshal.AllocHGlobal(bytes);
        try
        {
            *(byte*)buf = 0;
            error = fill(buf, (nuint)bytes);
            return Marshal.PtrToStringAnsi(buf) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>Marshal a managed string as a NUL-terminated narrow string we own (free with
    /// Marshal.FreeHGlobal). Null becomes "" because several C API paths build std::string from
    /// the pointer without a null check.</summary>
    internal static IntPtr AllocAnsi(string? s) => Marshal.StringToHGlobalAnsi(s ?? "");
}
