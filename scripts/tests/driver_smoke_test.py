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


def discover_drivers(bin_dirs: list[str]) -> list[Path]:
    found: set[Path] = set()
    for root in bin_dirs:
        root_path = Path(root)
        if not root_path.exists():
            continue
        # Some brew layouts keep binaries one level deeper under version dirs.
        for candidate in root_path.rglob("indi_*"):
            if candidate.is_file() and candidate.stat().st_mode & 0o111:
                # Skip simulator drivers — they're exercised by integration_test.py
                # and always pass, diluting the signal here.
                if "_simulator_" in candidate.name:
                    continue
                found.add(candidate)
    return sorted(found, key=lambda p: p.name)


def smoke_test(driver: Path, port: int, startup_timeout: float = 3.0,
               property_timeout: float = 2.0) -> tuple[bool, str, int]:
    """Returns (healthy, message, property_count)."""
    proc = subprocess.Popen(
        ["indiserver", "-p", str(port), "-m", "200", driver.name],
        env={**__import__("os").environ, "PATH": f"{driver.parent}:{__import__('os').environ.get('PATH', '')}"},
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
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
            return (False, f"socket never bound within {startup_timeout}s", 0)

        # Ask for all properties. INDI is XML-over-plaintext.
        sock.sendall(b"<getProperties version='1.7'/>\n")

        # Collect whatever we get back within the property window.
        sock.settimeout(property_timeout)
        buf = b""
        try:
            while True:
                chunk = sock.recv(4096)
                if not chunk:
                    break
                buf += chunk
                # Bail early once we've seen enough — some drivers stream forever.
                if len(buf) > 200_000:
                    break
        except socket.timeout:
            pass
        sock.close()

        # Count defined property vectors. Crude but sufficient — any driver
        # that published at least one vector is functional.
        defs = buf.count(b"<def")
        if defs == 0:
            return (False, "no property definitions received", 0)
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
        healthy, msg, _ = smoke_test(drv, args.port_base + i)
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
