# ShackLinkUsrp

A streaming server for software-defined radios. It owns one radio and serves derived products over
TCP to any number of clients:

- **panoramas**: the full instantaneous span as integrated power-spectrum rows, per-client geometry;
- **leases**: narrow IQ channels cut out of the wide stream by a per-lease DDC;
- **events**: state changes, dropped leases, radio faults;
- **transmit**: IQ a client streams to it, or a signal it generates itself (a tone, AM, FM, PM, DSB,
  SSB, a pulse, a stepped sweep), under a TX owner separate from the LO owner, with a cap, a
  starvation limit and a drain that ends on the radio's own acknowledgement.

It serves four kinds of radio:

| Radio | Reached through | Command line |
|---|---|---|
| **USRP B2xx** (B200, B210 and compatible clones) | UHD's C API (`uhd.dll`, the operator's install) | `--serial <serial>` |
| **Pluto-class boards** (ADALM-Pluto and AD936x boards like it) | iiod, over the network or the board's own USB; no UHD | `--serial <hw_serial> --iiod <host[:port]\|mdns\|usb:<hw_serial>>` |
| **Gemtek W1700K** running [h0m3us3r/w1700k-sdr](https://github.com/h0m3us3r/w1700k-sdr) | the router's UDP sample export, upstream's controller, and its transmit service | `--w1700k` |
| **A synthetic radio**, for tests and demonstrations | nothing: it is generated here | `--sim` |

Its clients are ShackLink, an SDR server, and **RF Bench Analyser**, a spectrum analyser and RF bench
application for Windows whose Microsoft Store package carries a build of this program beside it. Both
speak to it through `ShackLinkUsrp.Protocol`, an MIT-licensed library.

## Licence

**GPL-3.0-or-later** (`LICENSE`), because it links libuhd, which is GPL-3.0-or-later. It is a separate
program: its clients start it and talk to it over a socket, and link none of it.

It also contains:

- the W1700K's roll-off tables and filter design, ported from w1700k-sdr (MIT, Copyright (c) 2026
  h0m3us3r): `LICENSES/MIT-w1700k-sdr.txt`;
- `ShackLinkUsrp.Protocol` and `ShackLinkIio`, compiled in from the ShackLink repository (MIT,
  Copyright (c) 2026 Sorin Marghitoiu), each with its own `LICENSE.txt`;
- in `patches/`, changes to UHD's source, GPL-3.0-or-later like UHD.

UHD itself is not included and is never shipped with this program: install it from Ettus Research.

## The source of a build you were given

A program that ships a build of ShackLinkUsrp tags the commit it was built from, and the
[release](https://github.com/soriconm/ShackLinkUsrp/releases) on that tag carries its complete source:
this repository at that commit, and `ShackLinkUsrp.Protocol` and `ShackLinkIio` at the ShackLink commit
it was built against, with the command that builds it. RF Bench Analyser's tags are `rfba-<version>`.

## Build

Requires the .NET 10 SDK, and the two MIT libraries above. They live in the ShackLink repository,
which is not public: take them from a release's source archive (`ShackLink-libraries.zip`) and unzip
them beside this checkout as `ShackLink\`, which is where the build looks by default, or point
`-p:ShackLinkDir=<folder>\` at them. Then:

```
dotnet publish src\ShackLinkUsrp\ShackLinkUsrp.csproj -c Release -r win-x64 --self-contained true
```

or `dotnet build ShackLinkUsrp.sln -c Release` for a development build. **Run the Release build**: the
W1700K's conditioning cannot keep up with 40 MS/s in Debug.

The W1700K needs upstream's controller, `w1700k.exe`. The build stages one where the sidecar looks for
it, a `W1700K` folder beside the sidecar's own, when it is given one (`-p:W1700kControllerDist=<folder>`),
and otherwise says it did not. RF Bench Analyser's package carries one; `--w1700k-serve` names any
other, such as a WSL checkout of w1700k-sdr.

The test gate, `tests\ShackLinkUsrp.Tests` (exit 0 or 1), also compiles one file of ShackLink's own
tests (`IiodWireTest\FakeIiod.cs`, a fake iiod on loopback), so it builds only beside a ShackLink
checkout.

| Project | What it is |
|---|---|
| `src/ShackLinkUsrp` | the executable: protocol server, the radio backends and the DSP pipelines |
| `src/ShackLinkUsrp.Uhd` | the UHD C API binding (`uhd.h`) |
| `src/ShackLinkUsrp.Dsp` | the wide-stream ring, panorama FFT, DDC leases, and the transmitter's generators and interpolator |
| `src/ShackLinkUsrp.UhdProbe` | an instrument: drives the binding against a real radio and prints what it measured |
| `tests/ShackLinkUsrp.Tests` | the gate |

## Run

```
ShackLinkUsrp.exe --serial <serial> [--uhd <dir>] [--rate 10e6] [--otw sc16|sc8] [--linger 10]
ShackLinkUsrp.exe --serial <hw_serial> --iiod <host[:port]|mdns|usb:<hw_serial>>
ShackLinkUsrp.exe --w1700k --port 5555 --persist [--w1700k-serve none] [--w1700k-tx-serve none] [--w1700k-bind 10.77.0.10:7700] [--w1700k-rates 40e6]
ShackLinkUsrp.exe --sim [--sim-name <name>] [--sim-fault-after <ms>] [--sim-settle-ms <ms>] [--sim-tx-coupling <dB>]
ShackLinkUsrp.exe --list [--json <file>]
```

`ShackLinkUsrp.exe --help` lists every option. A client normally starts the sidecar itself and finds
it through a rendezvous file under `%LocalAppData%\ShackLinkUsrp` (`--state-dir`); `--port` and
`--persist` serve one by hand, and `--listen` binds an address other than loopback.

**A Pluto-class board** is found by its `hw_serial`: tried first at the `--iiod` address, which is a
hint, then by an `_iio._tcp` browse matched on the serial (`mdns` browses at once), or opened over its
own USB pipes through WinUSB (`usb:<hw_serial>`). It loads no UHD and takes none of UHD's options.

**A W1700K**'s samples arrive as UDP at `--w1700k-bind` (10.77.0.10:7700 by default, the unit's fixed
destination; Windows Firewall needs an inbound UDP rule for this program). The sidecar runs upstream's
controller to configure the unit (`--w1700k-serve`, `none` to change nothing on it), offers the wide
rates the link can carry (`--w1700k-rates`: 40 MS/s needs 2.5 GbE, 80 5 GbE, 320 10 GbE), and corrects
the decimation's roll-off and the LO's leak in the samples (`--w1700k-flatten`, `--w1700k-dc`). Its
three front ends, 2.4, 5 and 6 GHz, follow the frequency. **Its transmitter** is the controller's
transmit service, run as this sidecar's child (`--w1700k-tx-serve`: built in when the controller is the
bundled `w1700k.exe`, `none` for no transmitter); taking it stops the unit's reception until it is
released. `--w1700k-tx-check` says whether a transmitter would be offered and why not, and touches
nothing.

**Transmitting is the client's and the operator's decision.** Every radio takes `--tx-max-ms <ms>`
(the ceiling on any transmission, default one hour) and `--tx-starve-ms <ms>` (a keyed IQ transmission
fed nothing that long is ended, default 500). The sidecar never transmits on its own: nothing on its
command line or in any file starts a transmission, and what is radiated, where and at what level, is
the licensed operator's responsibility.

**The synthetic radio transmits into itself.** Its generator thread is its DAC: it takes a
transmission's samples at the TX rate against the same clock that paces its receive stream, and adds
them to what it receives at the TX frequency, at `L + G + coupling` dBFS for a sample at L dBFS sent at
a TX gain of G (coupling -80 dB unless `--sim-tx-coupling` says otherwise; its telemetry states it). A
gate sees the transmission in the panorama, and nothing anywhere radiated.

**On a real B2xx** the TX chain is set up as the radio opens, before the receiver streams: an admitted
TX rate (one that divides the wide rate, so a transmission never moves the master clock under the
receiver) is set first, and one TX streamer is made and freed to prove the chain and read UHD's
quantum. Nothing is sent. Each transmission then gets its own streamer, driven by ONE thread: UHD's C
API writes a streamer's error string on every call, so its sends and its acknowledgement poll must not
be split across threads.

**A generated source runs at the lowest admitted TX rate that holds it** and puts the client's rate
back when it ends. FM's phase is the exact integral of its deviation, so its modulation index is the
same at every rate. A **stepped sweep** runs in one of two regimes, and each `tx_step` event says which
through `timing`:

- **Baseband**: when every point fits the usable band of one admitted rate (at most 1.6 MHz at
  2 MS/s), the LO parks at the sweep's centre and the carrier steps inside the generated samples, at
  exact sample counts, on either radio (`timing: sample`).
- **Tuned**: wider, every step is a frequency write, the DUC's CORDIC inside its reach of a parked LO
  and the synthesizer beyond it. UHD applies a write when it arrives, not at a sample, so on a real
  radio these steps say `timing: bound`, stamped when the write returned. Timed commands
  (`set_command_time`) are deliberately not used: a pending one holds the radio's whole command
  processor, and a late one runs at once with no word to the host.

A sweep that does not repeat ends by itself, acknowledged like a drain, and puts back the centre and
LO the client had set. The synthetic radio steps in both regimes at the exact sample: it owns its
clock.

A sidecar lives as long as its clients: it exits `--linger` seconds after the last one leaves, at once
when its radio is in a fault (a fault is terminal; only a fresh sidecar opens the radio again), and
never with `--persist`. `--sim-fault-after` makes the synthetic radio fault on cue, for tests of those
paths. `--sim-settle-ms` makes each synthetic radio change take effect that long after it is asked for
(the samples between still carry the old settings, like a real radio's in-flight USB buffers) and
announces exactly where: it is how the gate reaches the settle paths, which UHD's 5 ms hides.

A row never spans a radio change: every change the LO owner makes (LO, rate, gain, AGC, antenna,
bandwidth, a setting) restarts the panoramas and sweeps at its settle boundary, a pipeline created
inside a settle starts at its boundary, a `sweep` sent after a change waits for it, and a row's gain is
the gain all its FFTs were taken at.

**UHD is not bundled.** By default the Ettus install (`C:\Program Files\UHD\bin`) is used; `--uhd <dir>`
loads `uhd.dll` from another directory.

## The protocol

Every frame starts with a 16-byte header. A client's control connection carries requests, replies,
errors and events, their bodies JSON and a request's verb in its `op`; panorama rows and IQ blocks
stream on data connections that attach to it. `ShackLinkUsrp.Protocol` implements the client side.
The specification, and the design record behind every rule above, are in the ShackLink repository's
`docs/usrp_b210_support.md` (section 9), `docs/plutosdr_support.md` and `docs/w1700k_support.md`,
which are not public; comments and commit messages here cite them by section. This README is the
public description.

## A diagnostic UHD for a board with a known CODEC fault

Stock UHD refuses a B2xx whose AD936x CODEC loopback self-test fails, as it should. The
OpenSourceSDRLab clone this was developed on has a defective RX D8 line and fails that test, so its
bring-up used a UHD v4.8.0.0 built from source with `patches/uhd-4.8-b210-codec-loopback.patch`: the
loopback failure becomes a warning when `UHD_B200_IGNORE_CODEC_LOOPBACK=1`, plus the build fixes a
modern MSVC, libusb and Boost toolchain needs. `--allow-known-codec-fault` sets that variable in the
sidecar's own environment before `uhd.dll` loads, and `--uhd <dir>` points at the patched build:

```
ShackLinkUsrp.exe --serial <serial> --uhd <patched UHD's bin folder> --allow-known-codec-fault
```

A stock UHD ignores the variable. **A board opened this way proves plumbing only: its samples are not
trustworthy for any level or spectral measurement.** `patches/uhd-4.10-txqc-diagnostic.patch` is a
diagnostic build for reading the AD9361's TX quadrature calibration, used by `ShackLinkUsrp.UhdProbe
--txqc`. Both patches are GPL-3.0-or-later like the UHD source they modify.
