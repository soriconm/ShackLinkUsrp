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

    private readonly SimSignal[] _scene;
    private readonly double _noiseDbfs;
    private Thread? _generator;
    private volatile bool _stop;
    private readonly object _radioLock = new();
    private long _lo;
    private double _rate;
    private double _gainDb = 30;
    private bool _agc;
    private long _behindMs;
    private long _blocks;

    public SimBackend(SidecarLog log, string name, double wideRateHz, long loHz = 100_000_000, double noiseDbfs = -65)
        : base(log,
               new RadioInfo { Serial = "sim-" + name, Product = "Synthetic B210", Name = name, Source = RadioSources.Sim },
               MakeCaps(),
               ringSamples: 1 << 23,
               initialLoHz: loHz,
               initialRateHz: wideRateHz)
    {
        _scene = DefaultScene;
        _noiseDbfs = noiseDbfs;
        _lo = loHz;
        _rate = wideRateHz;
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
        UpdateState(s => { s.Antenna = "RX2"; s.BandwidthHz = _rate; }, announce: false);
        _generator = new Thread(Generate) { IsBackground = true, Name = "SimRadio", Priority = ThreadPriority.AboveNormal };
        _generator.Start();
        Log($"synthetic radio: {_scene.Length} signals, noise {_noiseDbfs} dBFS, {_rate:0} S/s");
    }

    protected override void CloseRadio()
    {
        _stop = true;
        _generator?.Join(2000);
    }

    protected override long ApplyLo(long hz) { lock (_radioLock) _lo = hz; return hz; }
    protected override double ApplyRate(double hz) { lock (_radioLock) _rate = hz; return hz; }
    protected override double ApplyGain(double db) { lock (_radioLock) { _gainDb = db; _agc = false; } return db; }
    protected override void ApplyAgc(bool on) { lock (_radioLock) _agc = on; }
    protected override string ApplyAntenna(string name) => name;
    protected override double ApplyBandwidth(double hz) => hz;
    protected override JsonArray RadioSettings() => new();

    protected override string ApplySetting(string key, string value) =>
        throw new ShackLinkUsrp.Server.RadioRefusal(ErrorCodes.NotFound, $"the synthetic radio has no setting \"{key}\"");

    protected override JsonObject RadioTelemetry() => new JsonObject()
        .Set("kind", "sim").Set("blocks", Interlocked.Read(ref _blocks)).Set("behind_ms", Interlocked.Read(ref _behindMs));

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
        long produced = 0;
        var clock = Stopwatch.StartNew();
        double rateAtStart = 0;
        long producedAtRateStart = 0;
        double tStart = 0;

        while (!_stop)
        {
            long lo;
            double rate, gainDb;
            lock (_radioLock)
            {
                lo = _lo;
                rate = _rate;
                gainDb = _agc ? 30 : _gainDb;
            }
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
            Array.Clear(acc);
            for (int k = 0; k < m; k++)
            {
                var sig = _scene[k];
                double off = sig.FreqHz - lo;
                if (Math.Abs(off) >= rate / 2) continue;
                double amp0 = Math.Pow(10, sig.LevelDbfs / 20.0) * gain * 32767.0;
                double mstep = 2 * Math.PI * sig.ModRateHz * Sub / rate;
                double r = pr[k], q = pi[k], mp = modPhase[k];
                for (int b = 0; b < Block; b += Sub)
                {
                    double f = off, a = amp0;
                    if (sig.Mod == 'F') f += sig.DepthOrDeviation * Math.Sin(mp);
                    else if (sig.Mod == 'A') a *= 1 + sig.DepthOrDeviation * Math.Sin(mp);
                    double w = 2 * Math.PI * f / rate;
                    double sr = Math.Cos(w), si = Math.Sin(w);
                    for (int n = b; n < b + Sub; n++)
                    {
                        acc[2 * n] += a * r;
                        acc[2 * n + 1] += a * q;
                        double nr = r * sr - q * si;
                        q = r * si + q * sr;
                        r = nr;
                    }
                    mp += mstep;
                }
                double mag = Math.Sqrt(r * r + q * q);
                pr[k] = r / mag;
                pi[k] = q / mag;
                modPhase[k] = mp % (2 * Math.PI);
            }
            int start = (int)(Uniform() * (NoiseLen - Block)) * 2;
            for (int n = 0; n < Block * 2; n++)
                iq[n] = (short)Math.Clamp(Math.Round(acc[n] + noise[start + n] * noiseAmp), -32768, 32767);
            Ring.Write(iq);
            produced += Block;
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
    }
}
