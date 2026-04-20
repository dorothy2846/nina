#!/usr/bin/env python3
"""
Per-driver smoke test (Level 2): iterate every installed INDI driver binary
and verify it launches + responds on the INDI protocol within a short timeout.

Complements integration_test.py (which exercises simulator drivers through
our server end-to-end). This one is the "did libindi install correctly,
and is every binary still viable?" smoke test — useful on CI after image
builds, and for answering "I have 40 drivers installed, which ones boot?".

What it does per driver:
  1. Launch `indiserver -v $driver` with a fresh port
  2. Wait up to N seconds for the server to bind
  3. Send a <getProperties/> over the socket
  4. Collect any <def*Vector/> responses for up to M seconds
  5. Tear down; record result

A driver is "healthy" if the server bound its port AND we received at least
one property definition. A driver is "broken" if it crashed, hung, or
responded with nothing — that's a signal to check installation.

This does NOT prove the driver works with real hardware. It only proves
the binary is runnable and speaks INDI.
"""

import argparse
import os
import platform
import socket
import subprocess
import sys
import time
from pathlib import Path

# macOS Homebrew path; override with --bin-dir on other systems.
DEFAULT_BIN_DIRS = [
    "/opt/homebrew/Cellar/libindi",   # macOS brew
    "/usr/bin",                         # Linux apt
    "/usr/local/bin",                   # Linux manual install
]

C_GREEN = "\033[32m"
C_RED = "\033[31m"
C_YELLOW = "\033[33m"
C_DIM = "\033[2m"
C_RESET = "\033[0m"


# INDI ships CLI utilities alongside device drivers under the same indi_* prefix.
# These binaries don't publish any property vectors — they're tools that USE a
# running indiserver. Excluding them removes false-negative noise from the smoke
# test.
CLI_UTILITIES = {
    "indi_eval", "indi_getprop", "indi_setprop", "indi_getdevice",
    "indi_hid_test", "indi_wait",
}


def discover_drivers(bin_dirs: list[str]) -> list[Path]:
    found: dict[str, Path] = {}
    for root in bin_dirs:
        root_path = Path(root)
        if not root_path.exists():
            continue
        # Some brew layouts keep binaries one level deeper under version dirs.
        for candidate in root_path.rglob("indi_*"):
            if not (candidate.is_file() and candidate.stat().st_mode & 0o111):
                continue
            # CLI utilities don't publish property vectors — always skip.
            if candidate.name in CLI_UTILITIES:
                continue
            # First definition wins — macOS Homebrew keeps the canonical binary
            # under the versioned Cellar dir; /opt/homebrew/bin holds a symlink
            # we'd otherwise discover as a duplicate.
            found.setdefault(candidate.name, candidate)
    return sorted(found.values(), key=lambda p: p.name)


def _drain(sock: socket.socket, timeout: float, max_bytes: int) -> bytes:
    """Collect bytes from sock until `timeout` of inactivity or `max_bytes`."""
    sock.settimeout(timeout)
    buf = b""
    try:
        while len(buf) < max_bytes:
            chunk = sock.recv(4096)
            if not chunk:
                break
            buf += chunk
    except socket.timeout:
        pass
    return buf


def _first_device_name(buf: bytes) -> str | None:
    """Extract the device='...' attribute from the first defVector in buf.
    Every INDI vector carries its device name, so the first one is enough."""
    import re
    m = re.search(rb'<def\w+Vector[^>]*device="([^"]+)"', buf)
    if not m:
        return None
    return m.group(1).decode("utf-8", errors="ignore")


def _driver_env(driver: Path) -> dict:
    """Build the env indiserver needs to actually run the driver. On macOS the
    Homebrew libindi ships without rpath, so we have to point DYLD_LIBRARY_PATH
    at the bottle's lib/ — mirrors what IndiServerManager does in production."""
    env = os.environ.copy()
    env["PATH"] = f"{driver.parent}:{env.get('PATH', '')}"
    if platform.system() == "Darwin":
        # Driver binary lives in .../bin/indi_xxx; the paired lib dir is a sibling.
        lib_dir = driver.parent.parent / "lib"
        if lib_dir.exists():
            env["DYLD_LIBRARY_PATH"] = str(lib_dir)
    return env


def smoke_test(driver: Path, port: int, startup_timeout: float = 5.0,
               property_timeout: float = 2.0, capture_stderr: bool = False,
               fixture_dir: Path | None = None) -> tuple[bool, str, int]:
    """Returns (healthy, message, property_count). When fixture_dir is set, the
    raw XML buffer from a successful run is saved to <fixture_dir>/<driver>.xml
    for offline capability-probe regression tests."""
    stderr_target = subprocess.PIPE if capture_stderr else subprocess.DEVNULL
    proc = subprocess.Popen(
        ["indiserver", "-p", str(port), "-m", "200", driver.name],
        env=_driver_env(driver),
        stdout=subprocess.DEVNULL,
        stderr=stderr_target,
    )
    try:
        # Wait for socket bind.
        deadline = time.time() + startup_timeout
        sock = None
        while time.time() < deadline:
            try:
                sock = socket.create_connection(("localhost", port), timeout=0.5)
                break
            except (ConnectionRefusedError, socket.timeout, OSError):
                time.sleep(0.1)
        if sock is None:
            # Surface the first line of indiserver's stderr so the failure mode
            # is visible (missing .so, driver crashed on startup, etc.) instead of
            # a generic timeout.
            hint = ""
            if capture_stderr and proc.stderr:
                try:
                    # Drain whatever's buffered without blocking forever.
                    proc.terminate()
                    proc.wait(timeout=2)
                    err = proc.stderr.read(2048).decode("utf-8", errors="replace")
                    first = err.strip().split("\n")[0] if err.strip() else ""
                    if first:
                        hint = f" — {first[:150]}"
                except Exception:
                    pass
            return (False, f"socket never bound within {startup_timeout}s{hint}", 0)

        # Ask for all properties. INDI is XML-over-plaintext.
        sock.sendall(b"<getProperties version='1.7'/>\n")

        # Collect the initial (pre-connection) property set. Real drivers stop
        # here — they advertise only CONNECTION / DRIVER_INFO / generic until
        # hardware is actually wired up. Simulators keep publishing because they
        # fake the connection state; we'll drive them further below.
        sock.settimeout(property_timeout)
        buf = _drain(sock, property_timeout, max_bytes=200_000)

        # Fire a CONNECT command — simulators respond with their full capability
        # vector set; real drivers without hardware time out or fail gracefully
        # (we just don't block waiting on them). This is what lets the capability
        # matrix see CCD_COOLER / FOCUS_TEMPERATURE / TELESCOPE_TRACK_MODE for
        # drivers that have those features.
        device = _first_device_name(buf)
        if device:
            connect_xml = (
                f"<newSwitchVector device='{device}' name='CONNECTION'>"
                f"<oneSwitch name='CONNECT'>On</oneSwitch>"
                f"<oneSwitch name='DISCONNECT'>Off</oneSwitch>"
                f"</newSwitchVector>\n"
            ).encode()
            try:
                sock.sendall(connect_xml)
                # Wait a bit longer for post-connect properties; real drivers
                # either respond quickly with an error or hang on hardware —
                # we give up either way after the timeout.
                buf += _drain(sock, 3.0, max_bytes=500_000)
            except (BrokenPipeError, OSError):
                pass

        sock.close()

        # Count defined property vectors. Crude but sufficient — any driver
        # that published at least one vector is functional.
        defs = buf.count(b"<def")
        if defs == 0:
            return (False, "no property definitions received", 0)
        # Persist the initial property XML so offline capability-matrix tests can
        # feed it back through our IndiClient parser without needing libindi on
        # the CI host. Naming mirrors the driver binary so the regression tool
        # can look up "what does our probe say for indi_efa_focus?".
        if fixture_dir is not None:
            fixture_dir.mkdir(parents=True, exist_ok=True)
            (fixture_dir / f"{driver.name}.xml").write_bytes(buf)
        return (True, f"{defs} property vectors", defs)
    finally:
        if proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=2)
            except subprocess.TimeoutExpired:
                proc.kill()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bin-dir", action="append", default=None,
                    help="Override driver search path (can be repeated)")
    ap.add_argument("--port-base", type=int, default=17624,
                    help="Starting TCP port for isolated indiserver instances")
    ap.add_argument("--filter", default=None,
                    help="Only test drivers whose name contains this substring")
    ap.add_argument("--verbose", action="store_true",
                    help="Capture indiserver stderr and include first line in failure messages")
    ap.add_argument("--startup-timeout", type=float, default=5.0,
                    help="Seconds to wait for indiserver to bind its socket (default: 5)")
    ap.add_argument("--fixture-dir", default=None,
                    help="Save each driver's property dump to this directory for offline tests")
    args = ap.parse_args()

    # Fail fast if indiserver isn't available — every driver would fail anyway.
    if subprocess.run(["which", "indiserver"], capture_output=True).returncode != 0:
        sys.exit(f"{C_RED}indiserver not found on PATH. Install libindi first.{C_RESET}")

    bin_dirs = args.bin_dir or DEFAULT_BIN_DIRS
    drivers = discover_drivers(bin_dirs)
    if args.filter:
        drivers = [d for d in drivers if args.filter in d.name]

    if not drivers:
        sys.exit(f"{C_RED}No INDI drivers discovered under {bin_dirs}{C_RESET}")

    print(f"Discovered {len(drivers)} driver(s). Running smoke test...\n")

    passed: list[Path] = []
    failed: list[tuple[Path, str]] = []
    for i, drv in enumerate(drivers):
        print(f"  [{i+1}/{len(drivers)}] {drv.name:40s}", end=" ", flush=True)
        fixture_dir = Path(args.fixture_dir) if args.fixture_dir else None
        healthy, msg, _ = smoke_test(drv, args.port_base + i,
                                      startup_timeout=args.startup_timeout,
                                      capture_stderr=args.verbose,
                                      fixture_dir=fixture_dir)
        if healthy:
            print(f"{C_GREEN}✓{C_RESET} {C_DIM}{msg}{C_RESET}")
            passed.append(drv)
        else:
            print(f"{C_RED}✗{C_RESET} {C_DIM}{msg}{C_RESET}")
            failed.append((drv, msg))

    total = len(drivers)
    print(f"\n{C_GREEN}{len(passed)}/{total} healthy{C_RESET}", end="")
    if failed:
        print(f", {C_RED}{len(failed)} unhealthy{C_RESET}")
        print(f"\n{C_YELLOW}Unhealthy drivers:{C_RESET}")
        for drv, msg in failed:
            print(f"  - {drv.name}: {msg}")
    else:
        print()
    sys.exit(0 if not failed else 1)


if __name__ == "__main__":
    main()
