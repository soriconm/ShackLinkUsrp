// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Both receive sides at once (ShackLink docs/usrp_b210_support.md section 9.23, DS2): the rings in lockstep,
// the synthetic radio's second side - the same absolute-RF scene through a stated gain and phase, with its own
// noise - a dual lease and origin-aligned leases measured against those stated numbers, a centre per side with
// the RF LO placed, and the side switch told to the side that stays on.

using System.Numerics;
using ShackLinkUsrp.Client;
using ShackLinkUsrp.Dsp;
using ShackLinkUsrp.Radio;
using ShackLinkUsrp.Server;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Tests;

internal static partial class Program
{
    private static void RingAlignCases()
    {
        var ring = new WideRing(1024);
        ring.Write(new short[200]);
        ring.AlignTo(5000);
        Check(ring.Written == 5000, "AlignTo moves the write position forward without writing");
        ring.AlignTo(10);
        Check(ring.Written == 5000, "...and never backwards");
        ring.Write(new short[20]);
        Check(ring.Written == 5010, "writing continues from the aligned position");
    }

    private static void NcoAnchorCases()
    {
        foreach (long shift in new long[] { 150_000, -2_345_678, 4_999_999 })
        {
            // One NCO runs from position 0 through 3 x 8,192 samples; another is anchored at the position the
            // first reached: the next sample each mixes must be the same phasor.
            var run = new Nco();
            run.Set(shift, 10e6);
            run.AnchorAt(0);
            var ones = new short[8192 * 2];
            for (int i = 0; i < 8192; i++) ones[2 * i] = 16384;
            var re = new float[8192];
            var im = new float[8192];
            for (int k = 0; k < 3; k++) run.Mix(ones, 8192, re, im);
            var jump = new Nco();
            jump.Set(shift, 10e6);
            jump.AnchorAt(3 * 8192);
            var re1 = new float[1];
            var im1 = new float[1];
            run.Mix(ones, 1, re1, im1);
            var re2 = new float[1];
            var im2 = new float[1];
            jump.Mix(ones, 1, re2, im2);
            Check(Math.Abs(re1[0] - re2[0]) < 1e-5 && Math.Abs(im1[0] - im2[0]) < 1e-5,
                  $"an NCO anchored at a position turns where one that ran to it does ({shift} Hz: {F(re1[0], "0.00000")},{F(im1[0], "0.00000")} vs {F(re2[0], "0.00000")},{F(im2[0], "0.00000")})");
        }
        var far = new Nco();
        far.Set(123_456, 10e6);
        far.AnchorAt(86_400L * 10_000_000L);   // a day into a 10 MS/s stream: exact, not a double's rounding
        var r = new float[1];
        var q = new float[1];
        far.Mix(new short[] { 16384, 0 }, 1, r, q);
        double want = -2 * Math.PI * ((123_456L * 86_400L * 10_000_000L) % 10_000_000L) / 10e6;
        Check(Math.Abs(r[0] - 0.5 * Math.Cos(want)) < 1e-5 && Math.Abs(q[0] - 0.5 * Math.Sin(want)) < 1e-5,
              "a day into the stream the anchored phase is still exact (integer arithmetic)");
    }

    /// <summary>Complex samples of a lease block: one side of a dual block (<paramref name="side"/> 0 = A, 1 = B), or a single one.</summary>
    private static Complex[] SideOf(IqHeader h, byte[] s, int side)
    {
        int stride = h.Format == IqFormat.Sc16Dual ? 8 : 4;
        var c = new Complex[h.Count];
        for (int i = 0; i < h.Count; i++)
        {
            int o = i * stride + side * 4;
            c[i] = new Complex(BitConverter.ToInt16(s, o), BitConverter.ToInt16(s, o + 2));
        }
        return c;
    }

    /// <summary>The mean of B/A over every instant, as (dB, degrees) - the relative level and phase a measurement reads.</summary>
    private static (double Db, double Deg, double DegSpread) Relative(IEnumerable<(Complex a, Complex b)> pairs)
    {
        var ratios = pairs.Where(p => p.a.Magnitude > 100).Select(p => p.b / p.a).ToList();
        if (ratios.Count == 0) return (double.NaN, double.NaN, double.NaN);
        var mean = new Complex(ratios.Average(r => r.Real), ratios.Average(r => r.Imaginary));
        double deg = mean.Phase * 180 / Math.PI;
        double spread = ratios.Max(r => Math.Abs(Math.IEEERemainder(r.Phase * 180 / Math.PI - deg, 360)));
        return (20 * Math.Log10(ratios.Average(r => r.Magnitude)), deg, spread);
    }

    private static List<(IqHeader h, byte[] s)> Collect(LeaseStream lease, int blocks, int timeoutMs)
    {
        var got = new List<(IqHeader, byte[])>();
        lease.Connection.IqBlockReceived += (c, h, s) => { lock (got) got.Add((h, s)); };
        lease.Connection.StartReader();
        WaitFor(() => { lock (got) return got.Count >= blocks; }, timeoutMs);
        lock (got) return got.ToList();
    }

    private static void SimTwoSides()
    {
        var (backend, server, log) = StartSim("sides");
        try
        {
            using var s = Connect(server, "sides-test");
            Check(s.WaitReady(5000) == RadioStates.Ready, "the synthetic radio opens");
            Check(s.Caps.RxSides != null && s.Caps.RxSides.SequenceEqual(new[] { "A", "B" }), "it has RX A and RX B");
            Check(s.Caps.DualRatesHz != null && s.Caps.DualRatesHz.SequenceEqual(new[] { 2e6, 10e6 }),
                  $"two sides at the rates a two-side clock serves: {string.Join(", ", s.Caps.DualRatesHz ?? new double[0])}");
            var st = s.GetState();
            Check(st.RxSides.SequenceEqual(new[] { "A" }) && st.Side("B") is { Streaming: false } && st.Side("B")!.Antenna == "RX2",
                  "it opens streaming RX A alone; RX B is stated, on RX2, not streaming");
            long writtenB0 = backend.RingOfSide(RxSides.B).Written;

            s.ClaimLo();
            // RX B must come on MID-STREAM: switched on before the radio's first block, both rings would start at
            // position 0 together and lockstep would hold with no alignment at all (the mutation sweep found this).
            Check(WaitFor(() => backend.RingOfSide(RxSides.A).Written > 1_000_000, 3000), "RX A has streamed a million samples before RX B comes on");
            st = s.SetRxSides(new[] { "A", "B" });
            Check(st.RxSides.SequenceEqual(new[] { "A", "B" }) && st.MasterClockHz == 20e6 && st.MaxApartHz == 8_000_000,
                  $"both sides stream: the clock is 20 MHz (10 MS/s, /2) and the centres may be 8 MHz apart (clock {st.MasterClockHz}, apart {st.MaxApartHz})");
            Check(WaitFor(() => backend.RingOfSide(RxSides.B).Written > writtenB0 + 100_000, 3000) &&
                  Math.Abs(backend.RingOfSide(RxSides.A).Written - backend.RingOfSide(RxSides.B).Written) <= 4096,
                  $"RX B's ring runs in lockstep with RX A's (A {backend.RingOfSide(RxSides.A).Written}, B {backend.RingOfSide(RxSides.B).Written})");

            // A dual lease 50 kHz above the calibration carrier: the carrier turns at -50 kHz in both halves, so two
            // halves from DIFFERENT instants would disagree in phase by 1.8 degrees per wide sample.
            using var dual = s.OpenLease(new LeaseRequest { CenterHz = 100_150_000, Decimation = 50, Sides = new[] { "A", "B" } });
            Check(dual.Grant.IsDual && dual.Grant.Format == IqFormat.Sc16Dual && dual.Origin.HasValue, $"a dual lease, format 3, origin {dual.Origin}");
            var blocks = Collect(dual, 30, 4000);
            Check(blocks.Count >= 30 && blocks.All(b => b.h.Format == IqFormat.Sc16Dual && b.h.Count <= ProtocolConstants.MaxIqDualBlockSamples),
                  $"{blocks.Count} dual blocks, every one format 3 and at most 8,192 instants");
            var tail = blocks.Skip(10).SelectMany(b => SideOf(b.h, b.s, 0).Zip(SideOf(b.h, b.s, 1))).ToList();
            var rel = Relative(tail);
            Check(Math.Abs(rel.Db - SimBackend.DefaultSideBGainDb) < 0.3 && Math.Abs(rel.Deg - SimBackend.DefaultSideBPhaseDeg) < 1.0,
                  $"RX B reads the stated {SimBackend.DefaultSideBGainDb} dB and {SimBackend.DefaultSideBPhaseDeg} degrees from RX A, instant by instant ({F(rel.Db)} dB, {F(rel.Deg)} deg)");
            Check(rel.DegSpread < 3, $"...steady in every instant (the widest departure {F(rel.DegSpread)} deg): the halves come from one instant");

            // Two ORDINARY leases, one per side, opened at different moments: their origins align them.
            using var la = s.OpenLease(new LeaseRequest { CenterHz = 100_150_000, Decimation = 50, Side = "A" });
            Thread.Sleep(137);
            using var lb = s.OpenLease(new LeaseRequest { CenterHz = 100_150_000, Decimation = 50, Side = "B" });
            Check(la.Origin.HasValue && lb.Origin.HasValue && lb.Origin > la.Origin, $"each lease states its origin (A {la.Origin}, B {lb.Origin})");
            var ba = Collect(la, 25, 4000);
            var bb = Collect(lb, 25, 4000);
            double fs = 10e6, fbb = 100_100_000 - 100_150_000;
            var byPos = new Dictionary<long, Complex>();
            foreach (var (h, bytes) in ba.Skip(5))
            {
                var c = SideOf(h, bytes, 0);
                for (int i = 0; i < c.Length; i++) byPos[la.Origin!.Value + (long)(h.FirstSample + (ulong)i) * 50] = c[i];
            }
            var aligned = new List<(Complex a, Complex b)>();
            long residual = ((lb.Origin!.Value - la.Origin!.Value) % 50 + 50) % 50;
            foreach (var (h, bytes) in bb.Skip(5))
            {
                var c = SideOf(h, bytes, 0);
                for (int i = 0; i < c.Length; i++)
                {
                    long pb = lb.Origin.Value + (long)(h.FirstSample + (ulong)i) * 50;
                    if (byPos.TryGetValue(pb - residual, out var a)) aligned.Add((a, c[i]));
                }
            }
            // Sample pairs residual wide samples apart: the carrier turned 2 pi fbb residual / fs between them.
            var relOrigin = Relative(aligned);
            double expect = SimBackend.DefaultSideBPhaseDeg + 360.0 * fbb * residual / fs;
            Check(aligned.Count > 1000 && Math.Abs(Math.IEEERemainder(relOrigin.Deg - expect, 360)) < 1.0,
                  $"aligned by their origins ({aligned.Count} pairs, residual {residual} wide samples), the two leases read {F(relOrigin.Deg)} deg where {F(expect)} was due");

            // Each side is its own receiver.
            s.SetGain(40, "B");
            s.SetAgc(true, "A");
            st = s.GetState();
            Check(st.Side("A")!.Agc && !st.Side("B")!.Agc && Math.Abs(st.Side("B")!.GainDb - 40) < 1e-9 && st.Agc,
                  "RX A on AGC, RX B at 40 dB - each side its own");
            var gainLease = s.OpenLease(new LeaseRequest { CenterHz = 100_150_000, Decimation = 50, Sides = new[] { "A", "B" } });
            var afterGain = Collect(gainLease, 20, 4000);
            var relGain = Relative(afterGain.Skip(8).SelectMany(b => SideOf(b.h, b.s, 0).Zip(SideOf(b.h, b.s, 1))));
            Check(Math.Abs(relGain.Db - (SimBackend.DefaultSideBGainDb + 10)) < 0.4,
                  $"RX B at 40 dB (A's AGC holds 30) reads 10 dB higher relative to RX A: {F(relGain.Db)} dB");
            s.SetGain(30, "B");
            s.SetAgc(false, "A");

            // A centre per side: RX B to 107 MHz. At a 20 MHz clock a window 7 MHz off the LO does not fit: the LO is placed.
            var aBlocks = new List<IqHeader>();
            using var watchA = s.OpenLease(new LeaseRequest { CenterHz = 100_100_000, Decimation = 50, Side = "A" });
            watchA.Connection.IqBlockReceived += (c, h, b) => { lock (aBlocks) aBlocks.Add(h); };
            watchA.Connection.StartReader();
            dual.Dispose();
            lb.Dispose();
            gainLease.Dispose();
            Thread.Sleep(200);
            st = s.SetLo(107_000_000, false, "B");
            Check(st.CenterOf("B") == 107_000_000 && st.CenterOf("A") == 100_000_000 && st.RfLoHz == 103_500_000,
                  $"RX B moves to 107 MHz, RX A stays at 100, and the RF LO is placed between them ({st.RfLoHz})");
            Check(WaitFor(() => { lock (aBlocks) return aBlocks.Any(h => (h.Flags & IqFlags.RadioReconfigured) != 0 && h.CenterHz == 100_100_000); }, 2000),
                  "RX A's lease is told it was reconfigured under an unmoved centre");
            // An FFT here lasts 0.4 ms, a third of the AM's 800 Hz period: a row of a few FFTs reads the ENVELOPE of the
            // moment. 100 ms of integration averages 80 periods, and the carrier's bin reads its level (+0.5 dB of sidebands).
            var rowB = s.Sweep(new PanoramaRequest { Bins = 4096, Side = "B" });
            Thread.Sleep(100);
            rowB = s.Sweep(new PanoramaRequest { Bins = 4096, Side = "B" });
            double am = LevelAt(rowB.Header, rowB.Bins, 102_500_000, 10e6);
            double cal = LevelAt(rowB.Header, rowB.Bins, 100_100_000, 10e6);
            Check(rowB.Header.CenterHz == 107_000_000 && Math.Abs(am - (-35 + SimBackend.DefaultSideBGainDb)) < 1.0,
                  $"RX B's own sweep is centred on 107 MHz and hears the AM station at 102.5 MHz ({F(am)} dBFS, due {F(-35 + SimBackend.DefaultSideBGainDb)})");
            Check(double.IsNaN(cal), "...and not the carrier at 100.1 MHz, outside ITS window");
            var rowA = s.Sweep(new PanoramaRequest { Bins = 4096 });
            Thread.Sleep(100);
            rowA = s.Sweep(new PanoramaRequest { Bins = 4096 });
            double amA = LevelAt(rowA.Header, rowA.Bins, 102_500_000, 10e6);
            Check(Math.Abs(am - amA - SimBackend.DefaultSideBGainDb) < 0.5,
                  $"the same station read on both sides differs by RX B's stated {SimBackend.DefaultSideBGainDb} dB (A {F(amA)}, B {F(am)} dBFS)");
            Check(rowA.Header.CenterHz == 100_000_000 && Math.Abs(LevelAt(rowA.Header, rowA.Bins, 100_100_000, 10e6) + 20) < 1.5,
                  "RX A's sweep still reads its -20 dBFS carrier at 100.1 MHz");
            try
            {
                s.SetLo(109_000_000, false, "B");
                Check(false, "a centre 9 MHz from RX A's is refused");
            }
            catch (UsrpRequestException ex)
            {
                Check(ex.Code == ErrorCodes.OutOfRange, $"a centre 9 MHz from RX A's is refused ({ex.Code})");
            }

            // The side switch: RX B off, RX A goes on with a reconfiguration, never a lost lease.
            int reconfBefore;
            lock (aBlocks) reconfBefore = aBlocks.Count(h => (h.Flags & IqFlags.RadioReconfigured) != 0);
            st = s.SetRxSides(new[] { "A" });
            long bStopped = backend.RingOfSide(RxSides.B).Written;
            Check(st.RxSides.SequenceEqual(new[] { "A" }) && st.MaxApartHz == null, "RX B switched off");
            Check(WaitFor(() => { lock (aBlocks) return aBlocks.Count(h => (h.Flags & IqFlags.RadioReconfigured) != 0) > reconfBefore; }, 2000),
                  "RX A's lease goes on through the switch, flagged reconfigured");
            Thread.Sleep(300);
            Check(backend.RingOfSide(RxSides.B).Written == bStopped, "RX B's ring stands still");

            // RX B alone.
            watchA.Dispose();
            la.Dispose();
            Thread.Sleep(100);
            st = s.SetRxSides(new[] { "B" });
            long aStopped = backend.RingOfSide(RxSides.A).Written;
            Check(st.RxSides.SequenceEqual(new[] { "B" }) && st.CenterOf("B") == 107_000_000, "RX B alone, still at 107 MHz");
            using var onlyB = s.OpenLease(new LeaseRequest { CenterHz = 104_000_000, Decimation = 50, Side = "B" });
            var ob = Collect(onlyB, 10, 3000);
            Check(ob.Count >= 10 && WaitFor(() => backend.RingOfSide(RxSides.B).Written > aStopped + 100_000, 2000) &&
                  backend.RingOfSide(RxSides.A).Written == aStopped,
                  "a lease on RX B alone streams; RX A's ring stands still");
            onlyB.Dispose();

            // Back to both, with a whole-radio retune: equal centres keep their relative phase through it.
            s.SetRxSides(new[] { "A", "B" });
            st = s.SetLo(100_000_000, false, "B");
            using var d2 = s.OpenLease(new LeaseRequest { CenterHz = 100_150_000, Decimation = 50, Sides = new[] { "A", "B" } });
            var before = Relative(Collect(d2, 20, 4000).Skip(8).SelectMany(b => SideOf(b.h, b.s, 0).Zip(SideOf(b.h, b.s, 1))));
            s.SetLo(100_050_000);
            using var d3 = s.OpenLease(new LeaseRequest { CenterHz = 100_150_000, Decimation = 50, Sides = new[] { "A", "B" } });
            var after = Relative(Collect(d3, 20, 4000).Skip(8).SelectMany(b => SideOf(b.h, b.s, 0).Zip(SideOf(b.h, b.s, 1))));
            Check(Math.Abs(Math.IEEERemainder(after.Deg - before.Deg, 360)) < 1.0 && Math.Abs(after.Db - before.Db) < 0.3,
                  $"a whole-radio retune moves both centres and keeps their relative phase ({F(before.Deg)} -> {F(after.Deg)} deg)");
            var tel = s.GetTelemetry().GetObject("radio");
            Check(tel?.GetObject("source")?.GetDouble("side_b_gain_db", double.NaN) == SimBackend.DefaultSideBGainDb &&
                  tel.GetObject("source")?.GetDouble("side_b_phase_deg", double.NaN) == SimBackend.DefaultSideBPhaseDeg,
                  "the synthetic radio states the gain and phase its RX B hears at");
        }
        finally
        {
            server.Dispose();
            backend.Dispose();
            log.Dispose();
        }
    }

    /// <summary>What only a board can prove, pinned in the source: the two-channel streamer, the tune through
    /// channel 0 alone, the bandwidth to both chains, and the explicit clock (section 9.23.2 items 1, 8, 9; S3).</summary>
    private static void SidesSourceRules()
    {
        string root = RepoRoot();
        string uhd = File.ReadAllText(Path.Combine(root, "src", "ShackLinkUsrp", "Radio", "UhdBackend.cs"));
        string? start = MemberBody(uhd, "private void StartStreaming()");
        Check(start != null && start.Contains("_streamSides.Select(Chan)"), "the UHD backend makes ONE streamer over every streaming side");
        string? recv = MemberBody(uhd, "private void RecvLoop()");
        Check(recv != null && recv.Contains("for (int i = 0; i < rings.Length; i++) rings[i].Write(bufs[i].AsSpan(0, got * 2));"),
              "...and writes the same count into every streaming side's ring");
        string? tune = MemberBody(uhd, "protected override (long RfLoHz, IReadOnlyDictionary<string, long> Centers) ApplyTuning(");
        Check(tune != null && tune.Contains("u.TuneRxDspOnly(cB, 1)") && !tune.Contains("TuneRx(cB"),
              "two sides are tuned on channel 0, and channel 1's DDC alone - never a tune on channel 1");
        string? bw = MemberBody(uhd, "private double SetBandwidthBothChains(");
        Check(bw != null && bw.Contains("for (int ch = 1; ch < _rxChannels; ch++)"), "the analog bandwidth is written to every chain's node");
        string? sides = MemberBody(uhd, "protected override (long RfLoHz, IReadOnlyDictionary<string, long> Centers, double ClockHz) ApplyRxSides(");
        Check(sides != null && sides.IndexOf("StopStreaming()", StringComparison.Ordinal) < sides.IndexOf("AlignRings(", StringComparison.Ordinal) &&
              sides.IndexOf("AlignRings(", StringComparison.Ordinal) < sides.IndexOf("StartStreaming()", StringComparison.Ordinal),
              "a side switch aligns the rings between the old stream's stop and the new one's start");
        string? clock = MemberBody(uhd, "private void SetClockFor(");
        Check(clock != null && clock.Contains("_explicitClock = true;") && clock.Contains("u.SetRxRate(rateHz, ch)"),
              "once set, the clock is the sidecar's, and every channel's rate is set again after it");
    }
}
