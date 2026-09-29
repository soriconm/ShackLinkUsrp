// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using static ShackLinkUsrp.Uhd.UhdNative;

namespace ShackLinkUsrp.Uhd;

/// <summary>
/// One RX streamer (uhd_usrp_get_rx_stream) delivering interleaved sc16 I,Q on the host, plus the
/// metadata handle every recv writes into. Start, Recv and Stop belong to ONE thread - the radio
/// thread - which is how UHD's own examples drive it; the device's control calls may run elsewhere.
/// </summary>
public sealed unsafe class UhdRxStreamer : IDisposable
{
    private readonly UhdUsrp _owner;
    private IntPtr _h;
    private IntPtr _md;
    private bool _streaming;

    public string CpuFormat { get; }
    public string OtwFormat { get; }
    public int Channel { get; }

    /// <summary>uhd_rx_streamer_max_num_samps: the samples one transport packet carries - the
    /// device's own quantum (docs/usrp_b210_support.md §3.5), not a number to invent.</summary>
    public int MaxSamplesPerPacket { get; }

    public bool IsStreaming => _streaming;

    internal UhdRxStreamer(UhdUsrp owner, string cpuFormat, string otwFormat, string streamArgs, int chan)
    {
        _owner = owner;
        CpuFormat = cpuFormat;
        OtwFormat = otwFormat;
        Channel = chan;

        IntPtr h = IntPtr.Zero;
        Throw(uhd_rx_streamer_make(&h), "uhd_rx_streamer_make", IntPtr.Zero);
        _h = h;

        IntPtr cpu = AllocAnsi(cpuFormat);
        IntPtr otw = AllocAnsi(otwFormat);
        IntPtr args = AllocAnsi(streamArgs); // "" not NULL: stream_args_c_to_cpp does no null check
        nuint channel = (nuint)chan;
        try
        {
            var sa = new uhd_stream_args_t
            {
                cpu_format = cpu,
                otw_format = otw,
                args = args,
                channel_list = (IntPtr)(&channel),
                n_channels = 1,
            };
            UhdError e = uhd_usrp_get_rx_stream(owner.Handle, &sa, _h);
            if (e != UhdError.None)
            {
                string why = StreamerLastError();
                FreeHandles();
                throw new UhdException(e, "uhd_usrp_get_rx_stream", why);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(cpu);
            Marshal.FreeHGlobal(otw);
            Marshal.FreeHGlobal(args);
        }

        IntPtr md = IntPtr.Zero;
        UhdError me = uhd_rx_metadata_make(&md);
        if (me != UhdError.None)
        {
            FreeHandles();
            throw new UhdException(me, "uhd_rx_metadata_make", UhdUsrp.GlobalLastError());
        }
        _md = md;

        nuint max;
        Throw(uhd_rx_streamer_max_num_samps(_h, &max), "uhd_rx_streamer_max_num_samps", _h);
        MaxSamplesPerPacket = (int)max;
    }

    /// <summary>Continuous streaming, starting now.</summary>
    public void StartContinuous()
    {
        var cmd = new uhd_stream_cmd_t
        {
            stream_mode = StreamModeStartContinuous,
            num_samps = 0,
            stream_now = 1,
            time_spec_full_secs = 0,
            time_spec_frac_secs = 0.0,
        };
        Throw(uhd_rx_streamer_issue_stream_cmd(Live, &cmd), "uhd_rx_streamer_issue_stream_cmd(start)", _h);
        _streaming = true;
    }

    /// <summary>Stop continuous streaming, then drain what is already in flight so the next start
    /// does not begin with the tail of this one (UHD's examples do the same after a stop).</summary>
    public void Stop(double drainTimeoutSeconds = 0.1)
    {
        if (!_streaming) return;
        var cmd = new uhd_stream_cmd_t
        {
            stream_mode = StreamModeStopContinuous,
            num_samps = 0,
            stream_now = 1,
        };
        UhdError e = uhd_rx_streamer_issue_stream_cmd(Live, &cmd);
        _streaming = false;
        if (e != UhdError.None) throw new UhdException(e, "uhd_rx_streamer_issue_stream_cmd(stop)", StreamerLastError());

        var scratch = new short[Math.Max(2, MaxSamplesPerPacket * 2)];
        for (int i = 0; i < 256; i++)
        {
            int got = Recv(scratch, drainTimeoutSeconds, out UhdRxMetadata md, onePacket: true);
            if (got == 0 || md.ErrorCode == UhdRxErrorCode.Timeout) break;
        }
    }

    /// <summary>
    /// Receive up to <c>interleavedIq.Length / 2</c> complex samples. Returns the count delivered;
    /// <paramref name="metadata"/> carries the error code (TIMEOUT with 0 samples is a quiet wait,
    /// OVERFLOW means the host fell behind and the FPGA dropped samples). A call-level failure (a
    /// dead device) throws.
    /// </summary>
    public int Recv(Span<short> interleavedIq, double timeoutSeconds, out UhdRxMetadata metadata, bool onePacket = false)
    {
        IntPtr h = Live;
        int samps = interleavedIq.Length / 2;
        nuint got = 0;
        fixed (short* p = interleavedIq)
        {
            void* buf = p;
            IntPtr md = _md;
            UhdError e = uhd_rx_streamer_recv(h, &buf, (nuint)samps, &md, timeoutSeconds, onePacket ? (byte)1 : (byte)0, &got);
            if (e != UhdError.None) throw new UhdException(e, "uhd_rx_streamer_recv", StreamerLastError());
        }
        metadata = ReadMetadata();
        return (int)got;
    }

    private UhdRxMetadata ReadMetadata()
    {
        IntPtr md = _md;
        int code;
        byte hasTime, oos;
        long full;
        double frac;
        uhd_rx_metadata_error_code(md, &code);
        uhd_rx_metadata_has_time_spec(md, &hasTime);
        if (hasTime != 0) uhd_rx_metadata_time_spec(md, &full, &frac);
        else { full = 0; frac = 0.0; }
        uhd_rx_metadata_out_of_sequence(md, &oos);
        return new UhdRxMetadata((UhdRxErrorCode)code, hasTime != 0, full, frac, oos != 0);
    }

    /// <summary>uhd_rx_metadata_strerror for the last recv - UHD's own sentence for an error code.</summary>
    public string LastMetadataText() =>
        ReadString((b, l) => uhd_rx_metadata_strerror(_md, (byte*)b, l), out _);

    private IntPtr Live
    {
        get
        {
            IntPtr h = _h;
            if (h == IntPtr.Zero) throw new ObjectDisposedException(nameof(UhdRxStreamer));
            return h;
        }
    }

    private string StreamerLastError()
    {
        IntPtr h = _h;
        if (h != IntPtr.Zero)
        {
            string s = ReadString((b, l) => uhd_rx_streamer_last_error(h, (byte*)b, l), out UhdError e);
            if (e == UhdError.None && !string.IsNullOrWhiteSpace(s) && s != "None") return s;
        }
        return UhdUsrp.GlobalLastError();
    }

    private void Throw(UhdError e, string call, IntPtr streamer)
    {
        if (e == UhdError.None) return;
        string why = streamer != IntPtr.Zero ? StreamerLastError() : UhdUsrp.GlobalLastError();
        throw new UhdException(e, call, why);
    }

    private void FreeHandles()
    {
        if (_md != IntPtr.Zero)
        {
            IntPtr md = _md;
            uhd_rx_metadata_free(&md);
            _md = IntPtr.Zero;
        }
        if (_h != IntPtr.Zero)
        {
            IntPtr h = _h;
            uhd_rx_streamer_free(&h);
            _h = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_h == IntPtr.Zero) return;
        // Stop before free: a streamer freed mid-stream leaves the FPGA sending into nothing, and the
        // next open of the same device starts with an overflow.
        try { Stop(); } catch (UhdException) { }
        FreeHandles();
        _owner.Forget(this);
    }
}
