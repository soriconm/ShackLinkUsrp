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
ShackLinkUsrp.exe --sim [--sim-name <name>]
ShackLinkUsrp.exe --list
```

UHD is not bundled. By default the Ettus install (`C:\Program Files\UHD\bin`) is used; `--uhd <dir>`
loads `uhd.dll` from another directory.
