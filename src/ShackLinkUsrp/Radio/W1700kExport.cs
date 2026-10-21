// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;

namespace ShackLinkUsrp.Radio;

/// <summary>
/// The W1700K's UDP export (h0m3us3r/w1700k-sdr, <c>kernel/w1700k_sdr/README.md</c>; C:\Dev\SDRW1700K docs/01 section 4): one
/// datagram per completion of the unit's DMA ring - a 16-byte header (le32 magic "SDR1", le16 offset, le16 slice_bytes,
/// le64 seq) and the slice. A raw slice is 2048 little-endian u32 words, I in bits 29:16 and Q in 13:0, each 14-bit two's
/// complement; a packed ("half") slice carries the same 2048 samples in 1792 words, sample 7 of every 8 in the spare bits of
/// words 0-6. A slice under 4096 bytes is a firmware event: no samples and no sample time. <c>seq</c> counts EVERY completion,
/// events included, so a jump is completions lost anywhere between the unit's ring and this socket.
/// <para>Converted to sc16 at HALF scale: the 14-bit value x2, so the radio's 14-bit full scale reads -6 dBFS here. The 6 dB are the
/// roll-off correction's headroom (<see cref="W1700kCondition"/>): at x4, 14-bit full scale was sc16's, and the flatten clipped 0.04 %
/// of a real 40 MS/s capture's samples (measured on the replay, 2026-10-20). Upstream keeps 12 dB (its ring holds the 14-bit values,
/// +-8192 in an int16); x2 keeps 6 dB and one bit of the filter's fraction. No level here is calibrated: this is a stated convention.</para>
/// </summary>
internal static class W1700kExport
{
    public const uint Magic = 0x31524453;   // "SDR1"
    public const int HeaderBytes = 16;
    public const int SamplesPerSlice = 2048;
    public const int RawSliceBytes = SamplesPerSlice * 4;           // 8192
    public const int PackedSliceBytes = SamplesPerSlice / 8 * 7 * 4; // 7168
    /// <summary>Under this a slice is a firmware event.</summary>
    public const int EventBelowBytes = 4096;
    /// <summary>The spare bits of a raw word: 31:30 and 15:14.</summary>
    public const uint SpareMask = 0xC000C000;

    public enum Kind { Malformed, Event, Raw, Packed, Unknown }

    /// <summary>What one datagram is. <paramref name="payload"/> is where its slice starts (the header's offset is 0 on the
    /// 10G path, which always sends whole slices; anything else is not a slice this reader knows).</summary>
    public static Kind Classify(ReadOnlySpan<byte> datagram, out ulong seq, out int sliceBytes)
    {
        seq = 0;
        sliceBytes = 0;
        if (datagram.Length < HeaderBytes) return Kind.Malformed;
        if (BinaryPrimitives.ReadUInt32LittleEndian(datagram) != Magic) return Kind.Malformed;
        ushort offset = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(4));
        sliceBytes = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(6));
        seq = BinaryPrimitives.ReadUInt64LittleEndian(datagram.Slice(8));
        if (offset != 0 || datagram.Length != HeaderBytes + sliceBytes) return Kind.Malformed;
        if (sliceBytes < EventBelowBytes) return Kind.Event;
        if (sliceBytes == RawSliceBytes) return Kind.Raw;
        if (sliceBytes == PackedSliceBytes) return Kind.Packed;
        return Kind.Unknown;
    }

    /// <summary>One raw word to sc16: the 14-bit field moved to the top of an int32 and shifted back arithmetically, so it is
    /// sign-extended and x2 (upstream's <c>publish.cpp</c> shifts by 18 and keeps 14 bits; 17 keeps one bit of headroom's worth, see
    /// the class). I's shift leaves Q's spare bits 15:14 at 17:16, under the sample: masked, or a packed slice's spare bits would land
    /// in I's lowest bit.</summary>
    public static short I(uint w) => (short)((int)((w << 2) & 0xFFFC_0000) >> 17);

    public static short Q(uint w) => (short)((int)(w << 18) >> 17);

    /// <summary>What an sc16 sample here is, in the radio's 14-bit counts.</summary>
    public const int Scale = 2;

    /// <summary>A raw slice's 2048 samples into <paramref name="iq"/> (interleaved I, Q: 4096 shorts).</summary>
    public static void ConvertRaw(ReadOnlySpan<byte> slice, Span<short> iq)
    {
        for (int k = 0, j = 0; k < SamplesPerSlice; k++, j += 2)
        {
            uint w = BinaryPrimitives.ReadUInt32LittleEndian(slice.Slice(4 * k));
            iq[j] = I(w);
            iq[j + 1] = Q(w);
        }
    }

    /// <summary>
    /// A packed slice's 2048 samples into <paramref name="iq"/>: each 7 words carry 8 samples. Words 0-6 are samples 0-6 with
    /// their spare bits cleared; sample 7 is the OR over k of <c>(o_k &amp; 0xC000C000) &gt;&gt; (14 - 2k)</c> (upstream's
    /// pack: <c>o_k = w_k | ((w7 &lt;&lt; (14 - 2k)) &amp; 0xC000C000)</c>, docs/01 section 4).
    /// </summary>
    public static void ConvertPacked(ReadOnlySpan<byte> slice, Span<short> iq)
    {
        int j = 0;
        for (int g = 0; g < SamplesPerSlice / 8; g++)
        {
            uint w7 = 0;
            for (int k = 0; k < 7; k++)
            {
                uint o = BinaryPrimitives.ReadUInt32LittleEndian(slice.Slice(4 * (7 * g + k)));
                uint w = o & ~SpareMask;
                w7 |= (o & SpareMask) >> (14 - 2 * k);
                iq[j++] = I(w);
                iq[j++] = Q(w);
            }
            iq[j++] = I(w7);
            iq[j++] = Q(w7);
        }
    }

    // ------------------------------------------------------------ the far end, for a gate and a replay

    /// <summary>One sc16 sample (at this file's x2) as the unit's raw word, I in 29:16 and Q in 13:0: a value past the 14 bits' range
    /// is clamped to it, as the radio's own output is.</summary>
    public static uint Word(short i, short q) => (Field(i) << 16) | Field(q);

    private static uint Field(short v) => (uint)Math.Clamp(v >> 1, -8192, 8191) & 0x3FFF;

    /// <summary>A datagram as the unit sends it: the header, then the slice.</summary>
    public static int Compose(Span<byte> datagram, ulong seq, ReadOnlySpan<byte> slice)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(datagram, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(datagram.Slice(4), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(datagram.Slice(6), (ushort)slice.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(datagram.Slice(8), seq);
        slice.CopyTo(datagram.Slice(HeaderBytes));
        return HeaderBytes + slice.Length;
    }

    /// <summary>2048 sc16 samples as a raw slice.</summary>
    public static void PackRaw(ReadOnlySpan<short> iq, Span<byte> slice)
    {
        for (int k = 0; k < SamplesPerSlice; k++)
            BinaryPrimitives.WriteUInt32LittleEndian(slice.Slice(4 * k), Word(iq[2 * k], iq[2 * k + 1]));
    }

    /// <summary>2048 sc16 samples as a packed slice, by upstream's rule.</summary>
    public static void PackHalf(ReadOnlySpan<short> iq, Span<byte> slice)
    {
        for (int g = 0; g < SamplesPerSlice / 8; g++)
        {
            int s = 8 * g;
            uint w7 = Word(iq[2 * (s + 7)], iq[2 * (s + 7) + 1]);
            for (int k = 0; k < 7; k++)
            {
                uint w = Word(iq[2 * (s + k)], iq[2 * (s + k) + 1]);
                BinaryPrimitives.WriteUInt32LittleEndian(slice.Slice(4 * (7 * g + k)), w | ((w7 << (14 - 2 * k)) & SpareMask));
            }
        }
    }
}
