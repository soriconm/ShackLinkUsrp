# ShackLinkUsrp

A small streaming server for USRP B2xx radios (B200/B210 and compatible clones). It owns the radio
through UHD and serves derived products over TCP to any number of clients:

- **panoramas** — the full instantaneous span as integrated power-spectrum rows, per-client geometry;
- **leases** — narrow IQ channels cut out of the wide stream by a per-lease DDC;
- **events** — state changes, dropped leases, radio faults.

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
| `src/ShackLinkUsrp.Dsp` | the wide-stream ring, panorama FFT and DDC leases |
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
ShackLinkUsrp.exe --sim [--sim-name <name>] [--sim-fault-after <ms>] [--sim-settle-ms <ms>]
ShackLinkUsrp.exe --list
```

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
