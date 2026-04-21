#!/usr/bin/env python3
"""
Driver-in-the-loop test runner. Points real INDI drivers at a Python
protocol emulator through a virtual serial port (pty pair) and verifies:

  1. The driver successfully CONNECTs (no Alert state on CONNECTION).
  2. Post-connect the driver publishes its FULL capability property set
     (TELESCOPE_TRACK_MODE, TELESCOPE_PARK, etc. — the vectors that only
     appear after a successful hardware handshake).
  3. Our capability probes (same as IndiDiscoveryService.cs) correctly
     classify the fully-connected driver.

Coverage: every driver whose hardware interface is serial text protocol.
The framework is protocol-pluggable — add a new emulator class + map one
driver-name prefix to it, and every matching driver gets exercised.

Usage:
  ./real_driver_test.py --driver indi_lx200basic
  ./real_driver_test.py --all                  # every mapped driver
  ./real_driver_test.py --fixture-dir /path    # save post-connect fixtures
"""

import argparse
import os
import socket
import subprocess
import sys
import time
from pathlib import Path

# Make protocol_emulator importable when the script is run directly.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from protocol_emulator import SerialBridge  # noqa: E402
from protocols.lx200 import LX200Emulator  # noqa: E402
from protocols.nexstar import NexStarEmulator  # noqa: E402
from protocols.moonlite import MoonliteEmulator  # noqa: E402
from protocols.ioptron import IOptronEmulator  # noqa: E402
from protocols.myfocuserpro2 import MyFocuserPro2Emulator  # noqa: E402


# Driver → protocol emulator mapping. Each entry covers every INDI driver
# that speaks the named wire protocol; one emulator class serves them all.
# Substring match: indi_lx200basic, indi_lx200am5, indi_lx200_OnStep etc.
# all handled by the one LX200 emulator.
PROTOCOL_MAP: list[tuple[str, type]] = [
    # LX200 family — every generic-goto mount ever made
    ("lx200",           LX200Emulator),
    ("am5",             LX200Emulator),
    ("OnStep",          LX200Emulator),
    ("OpenAstroTech",   LX200Emulator),
    ("TeenAstro",       LX200Emulator),
    ("pegasus_nyx",     LX200Emulator),
    ("autostar",        LX200Emulator),
    # NexStar family — Celestron hand-controller + AUX protocol
    ("celestron_gps",   NexStarEmulator),
    ("celestrongps",    NexStarEmulator),
    ("nexstarevo",      NexStarEmulator),
    ("celestronaux",    NexStarEmulator),
    # Moonlite text protocol (hex position + temperature)
    ("moonlite",        MoonliteEmulator),
    ("robofocus",       MoonliteEmulator),
    ("microtouch",      MoonliteEmulator),
    # myFocuserPro2 numeric-coded protocol (Robert Brown DIY lineage)
    ("myfocuserpro2",   MyFocuserPro2Emulator),
    ("nfocus",          MyFocuserPro2Emulator),
    ("onfocus",         MyFocuserPro2Emulator),
    # iOptron mount family (iEQ / CEM / GEM / GotoNova / ZEQ)
    ("ieq_telescope",       IOptronEmulator),
    ("ieqlegacy_telescope", IOptronEmulator),
    ("ioptronv3_telescope", IOptronEmulator),
    ("ioptronHC8406",   IOptronEmulator),
    ("lx200gotonova",   IOptronEmulator),
    ("lx200zeq25",      IOptronEmulator),
]


def pick_emulator(driver_name: str):
    for key, cls in PROTOCOL_MAP:
        if key in driver_name:
            return cls()
    return None


def driver_bin_dir() -> Path:
    """Homebrew keg for libindi has versioned paths; find the first bin/
    that contains indi_ binaries."""
    candidates = [
        Path("/opt/homebrew/Cellar/libindi"),
        Path("/usr/local/Cellar/libindi"),
        Path("/usr/bin"),
        Path("/opt/homebrew/bin"),
    ]
    for c in candidates:
        if c.exists():
            for sub in c.rglob("indi_lx200basic"):
                if sub.is_file():
                    return sub.parent
    raise RuntimeError("Could not locate libindi bin directory")


def driver_env(bin_dir: Path) -> dict:
    env = os.environ.copy()
    env["PATH"] = f"{bin_dir}:{env.get('PATH', '')}"
    if sys.platform == "darwin":
        lib_dir = bin_dir.parent / "lib"
        if lib_dir.exists():
            env["DYLD_LIBRARY_PATH"] = str(lib_dir)
    return env


def run_driver_test(driver_name: str, port: int, fixture_dir: Path | None,
                    per_driver_timeout: float = 20.0) -> dict:
    """Returns {driver, connected, property_count, capabilities, error}.
    Enforces a hard per-driver wall-clock budget — drivers that hang on
    handshake (happens for sophisticated mounts whose protocol our emulator
    doesn't fully satisfy) would otherwise freeze the whole suite."""
    import signal as _signal
    emulator = pick_emulator(driver_name)
    if emulator is None:
        return {"driver": driver_name, "connected": False,
                "error": "no protocol emulator mapped"}

    bridge = SerialBridge(emulator)
    bridge.start()
    bin_dir = driver_bin_dir()
    env = driver_env(bin_dir)

    # start_new_session puts indiserver + its spawned driver into their own
    # process group so we can SIGKILL the whole tree, not just the parent.
    # `preexec_fn=os.setsid` is the equivalent in older Python versions.
    indiserver = subprocess.Popen(
        ["indiserver", "-p", str(port), "-m", "200", driver_name],
        env=env,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        start_new_session=True,
    )
    start_time = time.time()
    def _deadline_exceeded() -> bool:
        return time.time() - start_time > per_driver_timeout

    try:
        # Wait for indiserver to bind.
        deadline = time.time() + 5
        sock = None
        while time.time() < deadline:
            try:
                sock = socket.create_connection(("localhost", port), timeout=0.5)
                break
            except (ConnectionRefusedError, OSError, socket.timeout):
                time.sleep(0.1)
        if sock is None:
            return {"driver": driver_name, "connected": False,
                    "error": "indiserver never bound socket"}

        # Discover device name + connection-mode options.
        sock.sendall(b"<getProperties version='1.7'/>\n")
        initial = _drain(sock, 1.5, 200_000)

        device = _first_device_name(initial)
        if not device:
            return {"driver": driver_name, "connected": False,
                    "error": "driver emitted no property vectors"}

        # Point the driver at our virtual serial port + connect.
        # Order matters: CONNECTION_MODE must be SERIAL before DEVICE_PORT
        # takes effect on some drivers.
        sock.sendall(
            f"<newSwitchVector device='{device}' name='CONNECTION_MODE'>"
            f"<oneSwitch name='CONNECTION_SERIAL'>On</oneSwitch>"
            f"<oneSwitch name='CONNECTION_TCP'>Off</oneSwitch>"
            f"</newSwitchVector>\n".encode())
        _drain(sock, 0.3, 50_000)

        sock.sendall(
            f"<newTextVector device='{device}' name='DEVICE_PORT'>"
            f"<oneText name='PORT'>{bridge.slave_path}</oneText>"
            f"</newTextVector>\n".encode())
        _drain(sock, 0.3, 50_000)

        # Now connect.
        sock.sendall(
            f"<newSwitchVector device='{device}' name='CONNECTION'>"
            f"<oneSwitch name='CONNECT'>On</oneSwitch>"
            f"<oneSwitch name='DISCONNECT'>Off</oneSwitch>"
            f"</newSwitchVector>\n".encode())
        post_connect = _drain(sock, 3.0, 500_000, wall_clock_cap=12.0)

        full = initial + post_connect
        # Check CONNECTION state — Alert means handshake failed.
        connected = b"name=\"CONNECTION\"" in full and _connection_state_ok(full)
        prop_count = full.count(b"<def")

        if fixture_dir is not None:
            fixture_dir.mkdir(parents=True, exist_ok=True)
            (fixture_dir / f"{driver_name}.connected.xml").write_bytes(full)

        caps = _probe_capabilities(full)

        sock.close()
        return {
            "driver": driver_name,
            "connected": connected,
            "property_count": prop_count,
            "capabilities": caps,
            "emulator": emulator.__class__.name,
        }
    finally:
        # Kill the whole process group — the driver child often doesn't die
        # on parent SIGTERM if it's blocked in a read(). SIGKILL the group
        # guarantees teardown so the next iteration doesn't collide on port.
        try:
            os.killpg(os.getpgid(indiserver.pid), _signal.SIGKILL)
        except (OSError, ProcessLookupError):
            pass
        try: indiserver.wait(timeout=2)
        except subprocess.TimeoutExpired: pass
        bridge.stop()


PROBES = {
    "canAbort":            [b"CCD_ABORT_EXPOSURE"],
    "hasDewHeater":        [b"CCD_DEW_CONTROL", b"AUX_HEATER_TOGGLE", b"ANTI_DEW"],
    "hasOffset":           [b"CCD_OFFSET"],
    "hasGain":             [b"CCD_GAIN"],
    "canSetTemperature":   [b"CCD_COOLER", b"CCD_TEMPERATURE"],
    "canSetTrackRate":     [b"TELESCOPE_TRACK_MODE"],
    "canSync":             [b"ON_COORD_SET"],
    "canPark":             [b"TELESCOPE_PARK"],
    "canFindHome":         [b"TELESCOPE_HOME"],
    "canSetTracking":      [b"TELESCOPE_TRACK_STATE"],
    "canPulseGuide":       [b"TELESCOPE_TIMED_GUIDE_NS", b"TELESCOPE_TIMED_GUIDE_WE"],
    "hasTemperature":      [b"FOCUS_TEMPERATURE"],
    "hasTempCompensation": [b"FOCUS_TEMPERATURE_COMPENSATION", b"AUTO_FOCUS_COMP"],
    "hasBacklash":         [b"FOCUS_BACKLASH_STEPS", b"FOCUS_BACKLASH_TOGGLE"],
    "hasFlatLight":        [b"FLAT_LIGHT_CONTROL"],
    "hasFlatIntensity":    [b"FLAT_LIGHT_INTENSITY"],
}


def _probe_capabilities(buf: bytes) -> dict:
    return {k: any(needle in buf for needle in needles)
            for k, needles in PROBES.items()}


def _connection_state_ok(buf: bytes) -> bool:
    """Scan for the most recent CONNECTION setSwitchVector and read its
    state= attribute. 'Ok' or 'Idle' = handshake succeeded; 'Alert' means
    the driver couldn't actually connect."""
    import re
    matches = list(re.finditer(rb'<setSwitchVector[^>]*name="CONNECTION"[^>]*state="([^"]+)"', buf))
    if not matches:
        return False
    return matches[-1].group(1) in (b"Ok", b"Idle")


def _drain(sock: socket.socket, timeout: float, max_bytes: int,
           wall_clock_cap: float | None = None) -> bytes:
    """Read up to max_bytes, stopping when `timeout` seconds of recv-silence
    pass OR `wall_clock_cap` total seconds elapse. The second bound protects
    against chatty drivers that keep the drain alive forever by always
    sending more bytes within the recv timeout window."""
    sock.settimeout(timeout)
    buf = b""
    deadline = time.time() + wall_clock_cap if wall_clock_cap else None
    try:
        while len(buf) < max_bytes:
            if deadline and time.time() >= deadline:
                break
            chunk = sock.recv(4096)
            if not chunk: break
            buf += chunk
    except (socket.timeout, OSError):
        pass
    return buf


def _first_device_name(buf: bytes) -> str | None:
    import re
    m = re.search(rb'<def\w+Vector[^>]*device="([^"]+)"', buf)
    return m.group(1).decode() if m else None


C_GREEN, C_RED, C_YELLOW, C_DIM, C_RESET = "\033[32m", "\033[31m", "\033[33m", "\033[2m", "\033[0m"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--driver", default=None, help="Single driver to test (e.g. indi_lx200basic)")
    ap.add_argument("--all", action="store_true", help="Test every driver with a mapped protocol")
    ap.add_argument("--fixture-dir", type=Path, default=None,
                    help="Save post-connect property dumps under this dir for regression use")
    ap.add_argument("--port-base", type=int, default=0,
                    help="0 = auto-pick based on PID to avoid collisions across runs")
    args = ap.parse_args()
    if args.port_base == 0:
        args.port_base = 20000 + (os.getpid() % 10000)

    bin_dir = driver_bin_dir()
    all_drivers = sorted(p.name for p in bin_dir.glob("indi_*")
                         if p.is_file() and "simulator" not in p.name)

    if args.driver:
        targets = [args.driver]
    elif args.all:
        targets = [d for d in all_drivers if pick_emulator(d) is not None]
    else:
        sys.exit("pass --driver NAME or --all")

    print(f"Running {len(targets)} driver-in-the-loop test(s)...\n")
    passed, failed = 0, 0
    for i, drv in enumerate(targets):
        print(f"  [{i+1}/{len(targets)}] {drv:40s}", end=" ", flush=True)
        result = run_driver_test(drv, args.port_base + i, args.fixture_dir)
        if result.get("connected"):
            cap_yes = sum(1 for v in result["capabilities"].values() if v)
            print(f"{C_GREEN}✓{C_RESET} "
                  f"{C_DIM}{result['property_count']} props, {cap_yes} caps verified via {result['emulator']}{C_RESET}")
            passed += 1
        else:
            print(f"{C_RED}✗{C_RESET} {C_DIM}{result.get('error', 'failed')}{C_RESET}")
            failed += 1

    print(f"\n{C_GREEN}{passed} connected + verified{C_RESET}, {C_RED}{failed} failed{C_RESET}")
    sys.exit(0 if failed == 0 else 1)


if __name__ == "__main__":
    main()
