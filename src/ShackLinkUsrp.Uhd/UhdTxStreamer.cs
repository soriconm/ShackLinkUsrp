// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using static ShackLinkUsrp.Uhd.UhdNative;

namespace ShackLinkUsrp.Uhd;

/// <summary>
/// One TX streamer (uhd_usrp_get_tx_stream) taking interleaved sc16 I,Q from the host, with the three
/// metadata handles a burst needs - start, middle, end - made once, because uhd_tx_metadata_make fixes
/// the flags and nothing sets them afterwards. ONE THREAD drives it from construction to disposal: the
/// C API writes the streamer's last_error string on every call, so a send on one thread and a
/// recv_async_msg on another race on that string (<see cref="UhdNative"/>'s TX streamer note). A burst
/// is: the first <see cref="Send"/> with <c>startOfBurst</c>, more sends, then <see cref="SendEndOfBurst"/>,
/// whose acknowledgement arrives through <see cref="PollAsync"/> as <see cref="UhdAsyncEventCode.BurstAck"/>.
/// </summary>
public sealed unsafe class UhdTxStreamer : IDisposable
{
    private IntPtr _h;
    private IntPtr _mdStart;
    private IntPtr _mdMiddle;
    private IntPtr _mdEnd;
    private IntPtr _async;

    public string CpuFormat { get; }
    public string OtwFormat { get; }
    public int Channel { get; }

    /// <summary>uhd_tx_streamer_max_num_samps: the samples one transport packet carries - the device's
    /// own quantum (caps.tx.quantum_samples).</summary>
    public int MaxSamplesPerPacket { get; }

    internal UhdTxStreamer(UhdUsrp owner, string cpuFormat, string otwFormat, string streamArgs, int chan)
    {
        CpuFormat = cpuFormat;
        OtwFormat = otwFormat;
        Channel = chan;

        IntPtr h = IntPtr.Zero;
        UhdError me = uhd_tx_streamer_make(&h);
        if (me != UhdError.None) throw new UhdException(me, "uhd_tx_streamer_make", UhdUsrp.GlobalLastError());
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
            UhdError e = uhd_usrp_get_tx_stream(owner.Handle, &sa, _h);
            if (e != UhdError.None)
            {
                string why = LastError();
                FreeHandles();
                throw new UhdException(e, "uhd_usrp_get_tx_stream", why);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(cpu);
            Marshal.FreeHGlobal(otw);
            Marshal.FreeHGlobal(args);
        }

        try
        {
            _mdStart = MakeMetadata(startOfBurst: true, endOfBurst: false);
            _mdMiddle = MakeMetadata(startOfBurst: false, endOfBurst: false);
            _mdEnd = MakeMetadata(startOfBurst: false, endOfBurst: true);
            IntPtr a = IntPtr.Zero;
            UhdError ae = uhd_async_metadata_make(&a);
            if (ae != UhdError.None) throw new UhdException(ae, "uhd_async_metadata_make", UhdUsrp.GlobalLastError());
            _async = a;
            nuint max;
            UhdError ne = uhd_tx_streamer_max_num_samps(_h, &max);
            if (ne != UhdError.None) throw new UhdException(ne, "uhd_tx_streamer_max_num_samps", LastError());
            MaxSamplesPerPacket = (int)max;
        }
        catch
        {
            FreeHandles();
            throw;
        }
    }

    private static IntPtr MakeMetadata(bool startOfBurst, bool endOfBurst)
    {
        IntPtr md = IntPtr.Zero;
        UhdError e = uhd_tx_metadata_make(&md, 0, 0, 0.0, startOfBurst ? (byte)1 : (byte)0, endOfBurst ? (byte)1 : (byte)0);
        if (e != UhdError.None) throw new UhdException(e, "uhd_tx_metadata_make", UhdUsrp.GlobalLastError());
        return md;
    }

    /// <summary>
    /// Queue up to <c>interleavedIq.Length / 2</c> samples for the radio; returns how many it took before
    /// <paramref name="timeoutSeconds"/> passed. BLOCKS while UHD's send buffers are full - the radio
    /// consuming at the TX rate is what paces the caller. <paramref name="startOfBurst"/> marks the
    /// first packet of a burst: pass it on every call until one returns non-zero.
    /// </summary>
    public int Send(ReadOnlySpan<short> interleavedIq, bool startOfBurst, double timeoutSeconds)
    {
        IntPtr h = Live;
        int samps = interleavedIq.Length / 2;
        nuint sent = 0;
        fixed (short* p = interleavedIq)
        {
            void* buf = p;
            IntPtr md = startOfBurst ? _mdStart : _mdMiddle;
            UhdError e = uhd_tx_streamer_send(h, &buf, (nuint)samps, &md, timeoutSeconds, &sent);
            if (e != UhdError.None) throw new UhdException(e, "uhd_tx_streamer_send", LastError());
        }
        return (int)sent;
    }

    /// <summary>End the burst: an empty packet flagged end-of-burst, after everything already sent.
    /// The radio acknowledges it on the async channel once the last sample has left.</summary>
    public void SendEndOfBurst(double timeoutSeconds)
    {
        IntPtr h = Live;
        nuint sent = 0;
        short dummy = 0;
        void* buf = &dummy;
        IntPtr md = _mdEnd;
        UhdError e = uhd_tx_streamer_send(h, &buf, 0, &md, timeoutSeconds, &sent);
        if (e != UhdError.None) throw new UhdException(e, "uhd_tx_streamer_send(end of burst)", LastError());
    }

    /// <summary>One message from the async channel, waiting up to <paramref name="timeoutSeconds"/>;
    /// false when none came.</summary>
    public bool PollAsync(double timeoutSeconds, out UhdAsyncEvent ev)
    {
        IntPtr h = Live;
        IntPtr md = _async;
        byte valid = 0;
        UhdError e = uhd_tx_streamer_recv_async_msg(h, &md, timeoutSeconds, &valid);
        if (e != UhdError.None) throw new UhdException(e, "uhd_tx_streamer_recv_async_msg", LastError());
        if (valid == 0)
        {
            ev = default;
            return false;
        }
        int code = 0;
        byte has = 0;
        long full = 0;
        double frac = 0;
        uhd_async_metadata_event_code(md, &code);
        uhd_async_metadata_has_time_spec(md, &has);
        if (has != 0) uhd_async_metadata_time_spec(md, &full, &frac);
        ev = new UhdAsyncEvent((UhdAsyncEventCode)code, has != 0, full, frac);
        return true;
    }

    private IntPtr Live
    {
        get
        {
            IntPtr h = _h;
            if (h == IntPtr.Zero) throw new ObjectDisposedException(nameof(UhdTxStreamer));
            return h;
        }
    }

    private string LastError()
    {
        IntPtr h = _h;
        if (h != IntPtr.Zero)
        {
            string s = ReadString((b, l) => uhd_tx_streamer_last_error(h, (byte*)b, l), out UhdError e);
            if (e == UhdError.None && !string.IsNullOrWhiteSpace(s) && s != "None") return s;
        }
        return UhdUsrp.GlobalLastError();
    }

    private void FreeHandles()
    {
        FreeMetadata(ref _mdStart);
        FreeMetadata(ref _mdMiddle);
        FreeMetadata(ref _mdEnd);
        if (_async != IntPtr.Zero)
        {
            IntPtr a = _async;
            uhd_async_metadata_free(&a);
            _async = IntPtr.Zero;
        }
        if (_h != IntPtr.Zero)
        {
            IntPtr h = _h;
            uhd_tx_streamer_free(&h);
            _h = IntPtr.Zero;
        }
    }

    private static void FreeMetadata(ref IntPtr md)
    {
        if (md == IntPtr.Zero) return;
        IntPtr m = md;
        uhd_tx_metadata_free(&m);
        md = IntPtr.Zero;
    }

    /// <summary>Free the streamer, on the thread that drove it. The caller has ended the burst first:
    /// a streamer freed mid-burst leaves the radio keyed on its last packet's zeros until UHD notices.</summary>
    public void Dispose() => FreeHandles();
}
