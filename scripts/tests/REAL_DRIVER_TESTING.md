# Driver-in-the-loop test framework

## Problem

INDI drivers publish capability-specific property vectors (`CCD_COOLER`,
`TELESCOPE_PARK`, `FOCUS_TEMPERATURE_COMPENSATION`, …) only **after a
successful hardware CONNECT**. Pre-connect runs see only
`CONNECTION` + `DRIVER_INFO` + generic stubs, so offline
`getProperties`-based smoke testing can't verify whether our server
correctly handles each driver's actual property surface.

Buying every piece of amateur astro gear is infeasible.

## Solution

Most amateur astro hardware speaks a **text protocol over RS-232 /
USB-serial**: LX200 (Meade + everyone who copied them), NexStar
(Celestron), Moonlite (most stepper focusers), iEQ (iOptron), SkyWatcher
MCMT. These protocols are public, simple, and stateful. Python can
emulate them in ~100-150 lines each.

If we stand up a virtual serial port (pty pair) with a Python emulator
on the master end, and point an INDI driver at the slave end, the
driver doesn't know it's not real hardware — it completes the
handshake, publishes its full property set, accepts slew / park /
track / goto / capture commands, and emits telemetry. That's end-to-end
verification without buying the hardware.

## Architecture

```
┌────────────────────────┐      ┌──────────────────────┐
│ indiserver + driver    │─pty─▶│ SerialBridge         │
│ (e.g. indi_lx200basic) │◀────│ + ProtocolEmulator    │
└────────────────────────┘      │ (LX200 / NexStar /   │
         ▲                       │  Moonlite / …)       │
         │ INDI XML over TCP     └──────────────────────┘
         │
┌────────────────────────┐
│ real_driver_test.py    │  connects via socket, issues
│ (pytest-style asserts) │  CONNECT, captures post-connect
└────────────────────────┘  property stream, verifies caps
```

- `protocol_emulator.py` — abstract `ProtocolEmulator`, pty-backed
  `SerialBridge` with background reader thread.
- `protocols/lx200.py` — Meade LX200 implementation.
- `protocols/nexstar.py` — Celestron NexStar.
- `protocols/moonlite.py` — Moonlite focuser.
- `real_driver_test.py` — runner: maps driver-name substrings to
  emulators, exercises each, captures fixture, runs capability probes.

## Coverage (current mapping)

| Emulator | Maps to drivers | Families |
|---|---|---|
| LX200 | `lx200*`, `am5`, `OnStep`, `OpenAstroTech`, `TeenAstro`, `pegasus_nyx`, `autostar` | 20+ mount drivers |
| NexStar | `celestron*`, `nexstarevo`, `celestronaux` | 5+ Celestron variants |
| Moonlite | `moonlite`, `nstep`, `myfocuserpro2`, `nfocus`, `microtouch`, `lakeside`, `smartfocus`, `robofocus`, `perfectstar`, `rbfocus`, `onfocus`, `aaf2` | 12+ focuser drivers |

That's ~35 drivers exercised via real connect handshakes today, with
trivial effort per additional protocol (each emulator ≈ 100 lines).

## Adding a new protocol

1. Create `protocols/<name>.py`, subclass `ProtocolEmulator`, implement
   `handle(bytes) -> bytes` with enough state to answer the driver's
   init queries + basic operations.
2. Add substring-to-class mapping in `real_driver_test.py`'s
   `PROTOCOL_MAP`.
3. Run `real_driver_test.py --driver indi_<target>` to verify.

## What's NOT covered

**USB bulk cameras** (ZWO ASI, QHY, Player One, Altair, SVBONY, SBIG,
Atik): these talk to vendor SDKs which call libusb directly. Emulating
is either kernel-module territory (`vhci-hcd` + `usbip`) or reverse-
engineering undocumented USB protocols. Out of scope. These drivers
remain covered by:

- Static source-code capability analysis (`source_analyzer.py`)
- User-submitted diagnostic dumps via `/api/v1/diagnostics`
- Eventual real-hardware field tests

## Running

```bash
# Single driver
./real_driver_test.py --driver indi_lx200basic

# Every driver with a mapped protocol
./real_driver_test.py --all

# Capture post-connect property dumps for regression
./real_driver_test.py --all --fixture-dir ./fixtures/connected
```
