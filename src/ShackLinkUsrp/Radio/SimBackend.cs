// ShackLinkUsrp - USRP B2xx streaming server. Copyright (C) 2026 Sorin Marghitoiu.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using ShackLinkUsrp.Host;
using ShackLinkUsrp.Wire;

namespace ShackLinkUsrp.Radio;

/// <summary>
/// The synthetic radio (docs/usrp_b210_support.md section 9.1 D8): signals at fixed ABSOLUTE RF
/// frequencies over a noise floor, generated in real time at the wide rate - so a retune moves them
/// across the panorama and a lease finds them where a radio would. It exists because the unit on the
/// bench has a stuck RX data line and three programs need an end-to-end source that tells the truth.
/// Its hello says <c>source = sim</c>, and its serial is <c>sim-&lt;name&gt;</c>, on every connection.
/// </summary>
internal sealed class SimBackend : RadioBackendBase
{
    /// <summary>One signal in the synthetic scene.</summary>
    public sealed record SimSignal(long FreqHz, double LevelDbfs, char Mod, double ModRateHz, double DepthOrDeviation);

    /// <summary>The default scene: something in the default 10 MHz window around 100 MHz, the 2 m
    /// FT8 spot, an NFM repeater, an AM airband voice, each at a level a panorama and a lease can
    /// both find and a gate can assert (the -20 dBFS carrier is the calibration line).</summary>
    public static readonly SimSignal[] DefaultScene =
    {
        new(100_100_000, -20, 'C', 0, 0),        // calibration carrier
        new(99_200_000, -45, 'F', 1000, 5000),    // NFM, 1 kHz tone, 5 kHz deviation
        new(101_700_000, -70, 'C', 0, 0),         // a weak carrier
        new(102_500_000, -35, 'A', 800, 0.5),     // AM, 800 Hz tone, 50 %
        new(144_174_000, -60, 'C', 0, 0),         // 2 m FT8 spot
        new(145_500_000, -30, 'F', 1000, 3000),   // 2 m simplex NFM
        new(118_100_000, -40, 'A', 400, 0.8),     // airband AM
    };

    /// <summary>What the generator produces at; a radio change replaces it at one exact sample.</summary>
    private readonly record struct Settings(long LoHz, double RateHz, double GainDb, bool Agc);

    private readonly SimSignal[] _scene;
    private readonly double _noiseDbfs;
    private readonly Settings _initial;
    private Thread? _generator;
    private volatile bool _stop;
    private readonly object _radioLock = new();
    // Under _radioLock: what was last asked for; the changes the generator has not reached yet, each
    // with the ring position it takes effect at; the position through which the generator has already
    // chosen its settings (a block it is building); and where the last change takes effect.
    private Settings _asked;
    private readonly Queue<(long At, Settings Then)> _changes = new();
    private long _committedThrough;
    private long _lastChangeAt;
    private long _behindMs;
    private long _blocks;

    /// <summary>A test hook (<c>--sim-fault-after</c>): fault this many milliseconds after the radio
    /// starts streaming, the way a real one dies - the samples stop and the state says why in words,
    /// until the process ends. 0 = never. It is how a gate reaches the fault paths (the idle rule
    /// among them, ShackLink docs/usrp_b210_support.md 9.11 finding 1) without a failing board.</summary>
    public int FaultAfterMs { get; init; }

    /// <summary>A test hook (<c>--sim-settle-ms</c>): a radio change - LO, rate, gain, AGC, antenna,
    /// bandwidth - takes effect this many milliseconds of samples AFTER it is asked for, and the samples
    /// in between still carry the old settings, the way a real radio's in-flight USB buffers do (UHD's
    /// is 5 ms). The backend announces exactly where the change took effect. 0 (the default) = at the
    /// next sample the generator produces. It is how a gate reaches the settle paths on a source whose
    /// own settle is 0 (ShackLink docs/usrp_b210_support.md 9.15: CRTU200's A2).</summary>
    public int SettleMs { get; init; }

    /// <summary>
    /// <c>--sim-tx-coupling</c>: how a transmission reaches this radio's own receiver (ShackLink
    /// docs/usrp_b210_support.md section 9.17 P4-b). A TX sample at L dBFS, sent at a TX gain of G dB,
    /// is heard at <c>L + G + TxCouplingDb</c> dBFS with the receiver at 30 dB (and the RX gain moves it
    /// with everything else) - so a -10 dBFS tone at a TX gain of 50 dB reads -40 dBFS by default. A
    /// stated number, in the telemetry as <c>tx_coupling_db</c>: no isolation was measured to choose it.
    /// </summary>
    public double TxCouplingDb { get; init; } = DefaultTxCouplingDb;

    public const double DefaultTxCouplingDb = -80;

    /// <summary>The B210's TX gain range and grid (UHD's get_tx_gain_range on the board, 0-89.75 in 0.25).</summary>
    public const double TxGainMaxDb = 89.75, TxGainStepDb = 0.25;

    private SimTransmission? _simTx;     // the generator's to mix; written by StartTx, cleared when it finishes
    private double _txGainDb;

    public SimBackend(SidecarLog log, string name, double wideRateHz, long loHz = 100_000_000, double noiseDbfs = -65)
        : base(log,
               new RadioInfo { Serial = "sim-" + name, Product = "Synthetic B210", Name = name, Source = RadioSources.Sim, KnownCodecFault = false },
               MakeCaps(),
               ringSamples: 1 << 23,
               initialLoHz: loHz,
               initialRateHz: wideRateHz)
    {
        _scene = DefaultScene;
        _noiseDbfs = noiseDbfs;
        _initial = _asked = new Settings(loHz, wideRateHz, 30, false);
    }

    private static RadioCaps MakeCaps() => new()
    {
        FreqMinHz = 50_000_000,
        FreqMaxHz = 6_000_000_000,
        GainMinDb = 0,
        GainMaxDb = 76,
        GainStepDb = 1,
        WideRatesHz = new[] { 2e6, 10e6, 20e6 },
        LeaseDecimMin = 2,
        LeaseDecimMax = 4096,
        MaxLeases = 8,
        MaxPanoramas = 4,
        MaxPanoramaBins = 1 << 18,
        Antennas = new[] { "TX/RX", "RX2" },
        Agc = true,
        BandwidthMinHz = 200e3,
        BandwidthMaxHz = 56e6,
    };

    protected override void OpenRadio()
    {
        // A synthetic radio opens at once; the short pause lets a client see "opening" honestly.
        Thread.Sleep(200);
        UpdateState(s => { s.Antenna = "RX2"; s.BandwidthHz = _initial.RateHz; }, announce: false);
        // The transmitter as a B210's comes up: 100 MHz, 0 dB, the LO on the carrier, and the lowest
        // admitted rate set (the UHD backend sets it too, before its TX chain is touched).
        double txRate = TxRates(_initial.RateHz).FirstOrDefault();
        InitTxState(s =>
        {
            s.CenterHz = 100_000_000;
            s.LoHz = 100_000_000;
            s.LoOffsetHz = 0;
            s.RateHz = txRate;
            s.GainDb = 0;
            s.Antenna = "TX/RX";
        }, txRate);
        _generator = new Thread(Generate) { IsBackground = true, Name = "SimRadio", Priority = ThreadPriority.AboveNormal };
        _generator.Start();
        Log($"synthetic radio: {_scene.Length} signals, noise {_noiseDbfs} dBFS, {_initial.RateHz:0} S/s" +
            (SettleMs > 0 ? $"; a change takes effect {SettleMs} ms after it is asked for (--sim-settle-ms)" : "") +
            $"; it transmits into itself with a coupling of {TxCouplingDb} dB");
    }

    // ------------------------------------------------------------ the transmitter (section 9.17 P4-b)

    /// <summary>The TX rates admitted at <paramref name="wideRateHz"/>: the rules the UHD backend applies
    /// to UHD's own answers, fed the ports of those answers (the DUC's host rates at the clock UHD's
    /// automatic selection would choose).</summary>
    private static double[] TxRates(double wideRateHz)
    {
        double tick = TxRatePlan.ModelTickHz(wideRateHz);
        return TxRatePlan.Admitted(TxRatePlan.DucHostRates(tick), tick, wideRateHz);
    }

    protected override TxCaps? RadioTxCaps()
    {
        double wide = WideRateHz;
        double[] rates = TxRates(wide);
        if (rates.Length == 0) return null;
        return new TxCaps
        {
            FreqMinHz = 50_000_000,
            FreqMaxHz = 6_000_000_000,
            GainMinDb = 0,
            GainMaxDb = TxGainMaxDb,
            GainStepDb = TxGainStepDb,
            RatesHz = rates,
            Antennas = new[] { "TX/RX" },
            MaxMs = 0,              // the server's ceiling
            QuantumSamples = 0,     // not stated: the synthetic radio has no transport packet
            MaxLoOffsetHz = TxRatePlan.MaxLoOffsetHz(TxRatePlan.ModelTickHz(wide), rates),
            Sources = new[] { TxSourceKinds.Iq, TxSourceKinds.Tone },
            Modulations = Array.Empty<string>(),
            ModulatingWaveforms = Array.Empty<string>(),
        };
    }

    // The synthetic DUC is exact: an admitted rate is read back as asked.
    protected override double ApplyTxRate(double hz) => hz;

    protected override double ApplyTxGain(double db)
    {
        double eff = Math.Clamp(Math.Round(db / TxGainStepDb) * TxGainStepDb, 0, TxGainMaxDb);
        Volatile.Write(ref _txGainDb, eff);
        return eff;
    }

    protected override (long CenterHz, long LoHz) ApplyTxTune(long centerHz, long loOffsetHz) => (centerHz, centerHz + loOffsetHz);

    protected override (long CenterHz, long LoHz, string Retune) ApplyTxPark(long centerHz, long loOffsetHz, RadioTransmission open)
    {
        long lo = GetTxState().LoHz;
        double reach = TxRatePlan.DspReachHz(TxRatePlan.ModelTickHz(WideRateHz), open.Grant.RateHz);
        return Math.Abs(centerHz - lo) <= reach
            ? (centerHz, lo, TxWords.RetuneDsp)
            : (centerHz, centerHz + loOffsetHz, TxWords.RetuneRf);
    }

    protected override RadioTransmission OpenTransmission(TxStartRequest request, TxGrant grant)
    {
        if (!TxRules.DividesWideRate(grant.RateHz, WideRateHz))
            throw new ShackLinkUsrp.Server.RadioRefusal(ErrorCodes.OutOfRange,
                $"the TX rate {grant.RateHz:0} S/s does not divide the wide rate {WideRateHz:0} S/s");
        var t = new SimTransmission(this, request, grant);
        Volatile.Write(ref _simTx, t);
        return t;
    }

    protected override void CloseRadio()
    {
        _stop = true;
        _generator?.Join(2000);
    }

    protected override long ApplyLo(long hz) { Schedule(s => s with { LoHz = hz }); return hz; }
    protected override double ApplyRate(double hz)
    {
        // As the UHD backend does: a TX rate that no longer divides the wide rate is moved to the lowest
        // one the new rate admits (no transmission is open - the server refuses set_rate under one).
        double txRate = GetTxState().RateHz;
        if (txRate > 0 && !TxRules.DividesWideRate(txRate, hz))
        {
            double moved = TxRates(hz).FirstOrDefault();
            if (moved > 0)
            {
                InitTxState(s => s.RateHz = moved, moved);
                Log($"TX rate moved from {txRate:0} to {moved:0} S/s: {txRate:0} does not divide the new wide rate {hz:0} S/s");
            }
        }
        Schedule(s => s with { RateHz = hz });
        return hz;
    }
    protected override double ApplyGain(double db) { Schedule(s => s with { GainDb = db, Agc = false }); return db; }
    protected override void ApplyAgc(bool on) => Schedule(s => s with { Agc = on });
    // Antenna and bandwidth change nothing the synthetic scene models, but they are radio changes all
    // the same: each has a position where it took effect, and that is what gets announced.
    protected override string ApplyAntenna(string name) { Schedule(s => s); return name; }
    protected override double ApplyBandwidth(double hz) { Schedule(s => s); return hz; }

    /// <summary>Queue a change for the generator. It takes effect at one exact ring position: SettleMs
    /// of samples (at the rate being generated) from now - never inside a block the generator has
    /// already chosen its settings for, never before an earlier change - and <see cref="ChangeBoundary"/>
    /// announces that position.</summary>
    private void Schedule(Func<Settings, Settings> change)
    {
        lock (_radioLock)
        {
            long settle = (long)(_asked.RateHz * SettleMs / 1000.0);
            _asked = change(_asked);
            long at = Math.Max(Math.Max(Ring.Written + settle, _committedThrough), _lastChangeAt);
            _changes.Enqueue((at, _asked));
            _lastChangeAt = at;
        }
    }

    /// <summary>Exactly where the change just scheduled takes effect - not an estimate: the generator
    /// switches at this sample.</summary>
    protected override long ChangeBoundary()
    {
        lock (_radioLock) return _lastChangeAt;
    }

    protected override JsonArray RadioSettings() => new();

    protected override string ApplySetting(string key, string value) =>
        throw new ShackLinkUsrp.Server.RadioRefusal(ErrorCodes.NotFound, $"the synthetic radio has no setting \"{key}\"");

    protected override JsonObject RadioTelemetry() => new JsonObject()
        .Set("kind", "sim").Set("blocks", Interlocked.Read(ref _blocks)).Set("behind_ms", Interlocked.Read(ref _behindMs))
        .Set("settle_ms", SettleMs)
        .Set("tx_coupling_db", TxCouplingDb)
        .Set("tx_gain_db", Volatile.Read(ref _txGainDb));

    private void Generate()
    {
        const int Block = 4096;
        const int Sub = 64;              // modulation is re-evaluated every 64 samples
        const int NoiseLen = 1 << 20;    // complex Gaussian table, walked from a random start per block
        var noise = new float[NoiseLen * 2];
        ulong rng = 0x9E3779B97F4A7C15UL;
        double Uniform()
        {
            rng ^= rng >> 12; rng ^= rng << 25; rng ^= rng >> 27;
            return (rng * 0x2545F4914F6CDD1DUL >> 11) * (1.0 / (1UL << 53));
        }
        for (int i = 0; i < noise.Length; i++)
        {
            // Box-Muller once, at start-up: unit variance per component.
            double u1 = Math.Max(Uniform(), 1e-300), u2 = Uniform();
            noise[i] = (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }

        var iq = new short[Block * 2];
        var acc = new double[Block * 2];
        int m = _scene.Length;
        var pr = new double[m];
        var pi = new double[m];
        var modPhase = new double[m];
        for (int k = 0; k < m; k++) pr[k] = 1.0;
        long produced = 0;               // = Ring.Written: the generator is the ring's only writer
        var clock = Stopwatch.StartNew();
        double rateAtStart = 0;
        long producedAtRateStart = 0;
        double tStart = 0;
        Settings current = _initial;

        while (!_stop)
        {
            if (FaultAfterMs > 0 && clock.ElapsedMilliseconds >= FaultAfterMs)
            {
                string why = $"the synthetic radio faulted on purpose {FaultAfterMs} ms after it started streaming (--sim-fault-after)";
                // The transmitter lets go first, on this thread - nothing else will ever consume its samples.
                Volatile.Read(ref _simTx)?.RadioStopped(why, produced);
                Fault(why);
                return;
            }
            int n = Block;
            lock (_radioLock)
            {
                // A change whose position has come takes effect HERE, at its exact sample, and a block
                // ends early where the next one begins: no block mixes two settings, so the position
                // ChangeBoundary announced is the truth to the sample.
                while (_changes.Count > 0 && _changes.Peek().At <= produced) current = _changes.Dequeue().Then;
                if (_changes.Count > 0) n = (int)Math.Min(Block, _changes.Peek().At - produced);
                _committedThrough = produced + n;
            }
            long lo = current.LoHz;
            double rate = current.RateHz;
            double gainDb = current.Agc ? 30 : current.GainDb;
            if (rate != rateAtStart)
            {
                // A new rate restarts the pacing reference; the signals keep their phases.
                rateAtStart = rate;
                producedAtRateStart = produced;
                tStart = clock.Elapsed.TotalSeconds;
            }

            // The RF gain moves every signal and the noise together, and clips at full scale like an ADC.
            double gain = Math.Pow(10, (gainDb - 30) / 20.0);
            double noiseAmp = Math.Pow(10, _noiseDbfs / 20.0) * gain * 32767.0 / Math.Sqrt(2);
            Array.Clear(acc, 0, n * 2);
            for (int k = 0; k < m; k++)
            {
                var sig = _scene[k];
                double off = sig.FreqHz - lo;
                if (Math.Abs(off) >= rate / 2) continue;
                double amp0 = Math.Pow(10, sig.LevelDbfs / 20.0) * gain * 32767.0;
                double mstep = 2 * Math.PI * sig.ModRateHz * Sub / rate;
                double r = pr[k], q = pi[k], mp = modPhase[k];
                for (int b = 0; b < n; b += Sub)
                {
                    int e = Math.Min(b + Sub, n);   // a block cut short at a change ends mid-step
                    double f = off, a = amp0;
                    if (sig.Mod == 'F') f += sig.DepthOrDeviation * Math.Sin(mp);
                    else if (sig.Mod == 'A') a *= 1 + sig.DepthOrDeviation * Math.Sin(mp);
                    double w = 2 * Math.PI * f / rate;
                    double sr = Math.Cos(w), si = Math.Sin(w);
                    for (int t = b; t < e; t++)
                    {
                        acc[2 * t] += a * r;
                        acc[2 * t + 1] += a * q;
                        double nr = r * sr - q * si;
                        q = r * si + q * sr;
                        r = nr;
                    }
                    mp += mstep * (e - b) / Sub;   // exactly mstep for a whole step
                }
                double mag = Math.Sqrt(r * r + q * q);
                pr[k] = r / mag;
                pi[k] = q / mag;
                modPhase[k] = mp % (2 * Math.PI);
            }
            // What this radio is transmitting, heard by its own receiver (section 9.17 P4-b): its samples
            // are taken here, at the TX rate against this clock, so this thread is its DAC.
            var tx = Volatile.Read(ref _simTx);
            if (tx != null)
            {
                if (tx.IsFinished)
                    Interlocked.CompareExchange(ref _simTx, null, tx);
                else
                    tx.Mix(acc, n, lo, rate, 32767.0 * Math.Pow(10, (Volatile.Read(ref _txGainDb) + TxCouplingDb) / 20.0) * gain, produced);
            }

            int start = (int)(Uniform() * (NoiseLen - Block)) * 2;
            for (int i = 0; i < n * 2; i++)
                iq[i] = (short)Math.Clamp(Math.Round(acc[i] + noise[start + i] * noiseAmp), -32768, 32767);
            Ring.Write(iq.AsSpan(0, n * 2));
            produced += n;
            Interlocked.Increment(ref _blocks);

            // Real time: sleep until the signal time of what was produced catches up with the clock.
            double due = tStart + (produced - producedAtRateStart) / rate;
            double now = clock.Elapsed.TotalSeconds;
            if (due > now)
            {
                int ms = (int)((due - now) * 1000);
                if (ms > 0) Thread.Sleep(ms);
                Interlocked.Exchange(ref _behindMs, 0);
            }
            else
            {
                Interlocked.Exchange(ref _behindMs, (long)((now - due) * 1000));
            }
        }
        Volatile.Read(ref _simTx)?.RadioStopped("the synthetic radio closed under the transmission", produced);
    }
}
