# ShackLinkUsrp

A small streaming server for USRP B2xx radios (B200/B210 and compatible clones). It owns the radio
through UHD and serves derived products over TCP to any number of clients:

- **panoramas** — the full instantaneous span as integrated power-spectrum rows, per-client geometry;
- **leases** — narrow IQ channels cut out of the wide stream by a per-lease DDC;
- **events** — state changes, dropped leases, radio faults;
- **transmit** — IQ a client streams to it, or a signal it generates itself (a tone, an AM or FM carrier,
  a stepped sweep), under a TX owner separate from the LO owner, with a cap, a starvation limit and a
  drain that ends on the radio's own acknowledgement (ShackLink `docs/usrp_b210_support.md` §9.17–§9.18).

Clients today are ShackLink (the SDR server) and the RF Bench Analyser. They speak the protocol
through the MIT-licensed `ShackLinkUsrp.Protocol` library, which lives in the ShackLink repository
(`..\ShackLink\ShackLinkUsrp.Protocol`, a sibling checkout). **The wire is specified in ShackLink's
`docs/usrp_b210_support.md` §9**; this repository implements the far end of it.

## Licence

GPL-3.0-or-later (see `LICENSE`), because it links libuhd, which is GPL-3.0-or-later. It is a
separate program: clients talk to it over a socket and link none of it.

## Layout

| Project | What it is |
|---|---|
| `src/ShackLinkUsrp` | the executable: protocol server + radio backend + DSP pipelines |
| `src/ShackLinkUsrp.Uhd` | the UHD C API binding (`uhd.h`) |
| `src/ShackLinkUsrp.Dsp` | the wide-stream ring, panorama FFT, DDC leases, and the transmitter's generators and interpolator |
| `src/ShackLinkUsrp.UhdProbe` | an instrument: drives the binding against a real radio and prints what it measured |
| `tests/ShackLinkUsrp.Tests` | the gate (exit 0/1) |

## Build

Requires the .NET 9 SDK and a sibling checkout of ShackLink (`C:\Dev\ShackLink`), or
`-p:ShackLinkDir=<path>\`.

```
dotnet build ShackLinkUsrp.sln -c Release
```

## Run

```
ShackLinkUsrp.exe --serial <serial> [--uhd <dir>] [--rate 10e6] [--linger 10]
ShackLinkUsrp.exe --sim [--sim-name <name>] [--sim-fault-after <ms>] [--sim-settle-ms <ms>] [--sim-tx-coupling <dB>]
ShackLinkUsrp.exe --list
```

Either radio also takes `--tx-max-ms <ms>` (the ceiling on any transmission, default one hour) and
`--tx-starve-ms <ms>` (a keyed IQ transmission fed nothing that long is ended, default 500). The sidecar
never transmits on its own: nothing on its command line or in any file starts a transmission.

**The synthetic radio transmits into itself.** Its generator thread is its DAC: it takes a transmission's
samples at the TX rate against the same clock that paces its receive stream, and adds them to what it
receives at the TX frequency, at `L + G + coupling` dBFS for a sample at L dBFS sent at a TX gain of G
(coupling −80 dB unless `--sim-tx-coupling` says otherwise; its telemetry states it). A gate sees the
transmission in the panorama, and nothing anywhere radiated.

**On a real B2xx** the TX chain is set up as the radio opens, before the receiver streams: an admitted TX
rate (one that divides the wide rate, so a transmission never moves the master clock under the receiver)
is set first, and one TX streamer is made and freed to prove the chain and read UHD's quantum. Nothing is
sent. Each transmission then gets its own streamer, driven by ONE thread — UHD's C API writes a streamer's
error string on every call, so its sends and its acknowledgement poll must not be split across threads.

**A generated source runs at the lowest admitted TX rate that holds it** and puts the client's rate back
when it ends. AM and FM are modulated by one sine (FM's phase is the exact integral of its deviation, so
its modulation index is the same at every rate). A **stepped sweep** runs in one of two regimes, and each
`tx_step` event says which through `timing`:

- **Baseband** — when every point fits the usable band of one admitted rate (at most 1.6 MHz at 2 MS/s):
  the LO parks at the sweep's centre and the carrier steps inside the generated samples, at exact sample
  counts, on either radio (`timing: sample`).
- **Tuned** — wider: every step is a frequency write, the DUC's CORDIC inside its reach of a parked LO and
  the synthesizer beyond it. UHD applies a write when it arrives, not at a sample, so on a real radio these
  steps say `timing: bound`, stamped when the write returned. Timed commands (`set_command_time`) are
  deliberately not used yet: a pending one holds the radio's whole command processor, and a late one runs
  at once with no word to the host (ShackLink `docs/usrp_b210_support.md` §9.18 P4b2).

A sweep that does not repeat ends by itself, acknowledged like a drain, and puts back the centre and LO
the client had set. The synthetic radio steps in both regimes at the exact sample: it owns its clock.

A sidecar lives as long as its clients: it exits `--linger` seconds after the last one leaves, at once
when its radio is in a fault (a fault is terminal; only a fresh sidecar opens the radio again), and
never with `--persist`. `--sim-fault-after` makes the synthetic radio fault on cue, for tests of those
paths. `--sim-settle-ms` makes each synthetic radio change take effect that long after it is asked for
(the samples between still carry the old settings, like a real radio's in-flight USB buffers) and
announces exactly where: it is how the gate reaches the settle paths, which UHD's 5 ms hides.

A row never spans a radio change: every change the LO owner makes (LO, rate, gain, AGC, antenna,
bandwidth, a setting) restarts the panoramas and sweeps at its settle boundary, a pipeline created
inside a settle starts at its boundary, a `sweep` sent after a change waits for it, and a row's gain is
the gain all its FFTs were taken at (ShackLink `docs/usrp_b210_support.md` §9.15).

UHD is not bundled. By default the Ettus install (`C:\Program Files\UHD\bin`) is used; `--uhd <dir>`
loads `uhd.dll` from another directory.

## A diagnostic UHD for a board with a known CODEC fault

Stock UHD refuses a B2xx whose AD936x CODEC loopback self-test fails, as it should. The
OpenSourceSDRLab clone this was developed on has a defective RX D8 line and fails that test, so its
bring-up used a UHD v4.8.0.0 built from source with `patches/uhd-4.8-b210-codec-loopback.patch`:
the loopback failure becomes a warning when `UHD_B200_IGNORE_CODEC_LOOPBACK=1`, plus the build fixes
a modern MSVC / libusb / Boost toolchain needs. `--allow-known-codec-fault` sets that variable in the
sidecar's own environment before `uhd.dll` loads, and `--uhd <dir>` points at the patched build:

```
ShackLinkUsrp.exe --serial <serial> --uhd C:\Dev\USRP_B210\uhd-4.8-soapy-diagnostic --allow-known-codec-fault
```

A stock UHD ignores the variable. **A board opened this way proves plumbing only — its samples are
not trustworthy for any level or spectral measurement.** The patch is GPL-3.0-or-later like the UHD
source it modifies; it lived in the ShackLink repository's `tools\` while ShackLink still had an
in-process SoapyUHD bench path, and moved here when that path was deleted (2026-09-28).
