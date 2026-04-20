#!/usr/bin/env python3
"""
End-to-end integration test for nina-headless against INDI simulator drivers.

Boots the server with NINA_INDI_DRIVERS set to the full simulator lineup,
exercises the /api/v1/* surface for every device kind, and reports pass/fail
per test case. No hardware needed — every driver under test is an INDI
simulator that fakes realistic property vectors.

Why this exists: we can't pre-buy every camera/mount/focuser amateurs use,
but we CAN verify our server speaks INDI correctly end-to-end by exercising
the simulator shims. Most regressions (capability probe drift, endpoint
wiring breaks, startup bugs) will show up here.

Prereqs:
  - libindi + simulator drivers installed (macOS: brew install libindi)
  - dotnet 10 SDK
  - python 3.8+
  - requests (auto-installed if missing)

Usage:
  ./integration_test.py              # run full suite
  ./integration_test.py --keep-alive  # leave server running after tests
"""

import argparse
import os
import signal
import subprocess
import sys
import time
from pathlib import Path

try:
    import requests
except ImportError:
    print("Installing 'requests' package...")
    subprocess.check_call([sys.executable, "-m", "pip", "install", "--quiet", "requests"])
    import requests

ROOT = Path(__file__).resolve().parent.parent.parent
SERVER_PROJECT = ROOT / "NINA.Headless" / "NINA.Headless.csproj"
BASE_URL = "http://localhost:1888"
STARTUP_TIMEOUT_SEC = 30
CONNECT_POLL_TIMEOUT_SEC = 15
CONNECT_POLL_INTERVAL_SEC = 0.5

# Simulator driver set — covers every device type we support (minus Switch and
# SafetyMonitor, which have no libindi simulator). Names match the binaries
# under /opt/homebrew/Cellar/libindi/*/bin/ and the linux apt package layout.
SIMULATOR_DRIVERS = " ".join([
    "indi_simulator_ccd",
    "indi_simulator_guide",
    "indi_simulator_telescope",
    "indi_simulator_focus",
    "indi_simulator_wheel",
    "indi_simulator_rotator",
    "indi_simulator_dome",
    "indi_simulator_weather",
    "indi_simulator_lightpanel",
])

# ANSI colors for terminal output.
C_GREEN = "\033[32m"
C_RED = "\033[31m"
C_YELLOW = "\033[33m"
C_DIM = "\033[2m"
C_RESET = "\033[0m"


class TestRunner:
    def __init__(self):
        self.passed = 0
        self.failed = 0
        self.skipped = 0
        self.failures: list[str] = []

    def case(self, name: str, fn):
        try:
            fn()
            print(f"  {C_GREEN}✓{C_RESET} {name}")
            self.passed += 1
        except AssertionError as e:
            print(f"  {C_RED}✗ {name}{C_RESET}")
            print(f"    {C_DIM}{e}{C_RESET}")
            self.failed += 1
            self.failures.append(f"{name}: {e}")
        except Exception as e:
            print(f"  {C_RED}✗ {name} (exception){C_RESET}")
            print(f"    {C_DIM}{type(e).__name__}: {e}{C_RESET}")
            self.failed += 1
            self.failures.append(f"{name}: {type(e).__name__}: {e}")

    def skip(self, name: str, reason: str):
        print(f"  {C_YELLOW}~{C_RESET} {name} {C_DIM}({reason}){C_RESET}")
        self.skipped += 1

    def summary(self) -> bool:
        total = self.passed + self.failed
        status = f"{C_GREEN}{self.passed}/{total} passed{C_RESET}" if self.failed == 0 \
            else f"{C_RED}{self.failed} failed{C_RESET}, {self.passed} passed"
        skip_str = f", {self.skipped} skipped" if self.skipped else ""
        print(f"\n{status}{skip_str}")
        if self.failures:
            print(f"\n{C_RED}Failures:{C_RESET}")
            for f in self.failures:
                print(f"  - {f}")
        return self.failed == 0


# ─────────── HTTP helpers ───────────

def get(path: str, expect: int = 200) -> dict:
    r = requests.get(f"{BASE_URL}{path}", timeout=30)
    assert r.status_code == expect, f"GET {path} → {r.status_code}, body={r.text[:200]}"
    return r.json() if r.text else {}


def post(path: str, body: dict | None = None, expect: int = 200) -> dict:
    r = requests.post(f"{BASE_URL}{path}", json=body, timeout=30)
    assert r.status_code == expect, f"POST {path} → {r.status_code}, body={r.text[:200]}"
    return r.json() if r.text else {}


def wait_for_connected(status_path: str, device_label: str) -> dict:
    deadline = time.time() + CONNECT_POLL_TIMEOUT_SEC
    while time.time() < deadline:
        s = get(status_path)
        if s.get("connected") is True:
            return s
        time.sleep(CONNECT_POLL_INTERVAL_SEC)
    raise AssertionError(f"{device_label} did not reach connected=true within {CONNECT_POLL_TIMEOUT_SEC}s (last status: {s})")


def first_device(kind_key: str) -> dict | None:
    """Pick the first discovered device of the given kind from /equipment/devices.
    `kind_key` is e.g. 'cameras', 'telescopes', 'focusers'."""
    devs = get("/api/v1/equipment/devices").get(kind_key) or []
    return devs[0] if devs else None


# ─────────── Test cases per device ───────────

def test_health_and_discovery(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Health + discovery")
    t.case("GET /health returns status", lambda: (
        get("/api/v1/health").get("status") is not None or
        (_ for _ in ()).throw(AssertionError("no status field"))
    ))
    t.case("GET /equipment/devices lists simulator devices", lambda: (
        _assert_has_simulators(get("/api/v1/equipment/devices"))
    ))


def _assert_has_simulators(devices: dict):
    # Every kind in SIMULATOR_DRIVERS should show at least one discovered entry.
    # (Guide camera registers as a camera too, which is fine.)
    expected_kinds = ["cameras", "telescopes", "focusers", "filterWheels",
                      "rotators", "domes", "weather"]
    for k in expected_kinds:
        lst = devices.get(k) or []
        assert len(lst) >= 1, f"no devices discovered under key '{k}' — got {devices}"


def test_camera(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Camera (indi_simulator_ccd)")
    dev = first_device("cameras")
    if not dev:
        t.skip("camera", "no simulator camera discovered")
        return
    post("/api/v1/camera/connect", {"deviceId": dev["id"]}, expect=202)
    s = wait_for_connected("/api/v1/camera/status", "camera")
    t.case("camera status.connected=true", lambda: _eq(s.get("connected"), True))
    info = get("/api/v1/camera/info")
    t.case("camera /info returns name", lambda: _truthy(info.get("name")))
    t.case("camera /info returns capabilities object", lambda: _truthy(info.get("capabilities")))
    t.case("camera sensor resolution populated", lambda: _truthy(info.get("resolutionX")))
    # Cooling: simulator supports it.
    cool = post("/api/v1/camera/cooling", {"enabled": True, "temperature": -10})
    t.case("cooling enable returns success", lambda: _eq(cool.get("success"), True))
    post("/api/v1/camera/disconnect")


def test_telescope(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Telescope (indi_simulator_telescope)")
    dev = first_device("telescopes")
    if not dev:
        t.skip("telescope", "no simulator telescope discovered")
        return
    post("/api/v1/telescope/connect", {"deviceId": dev["id"]}, expect=202)
    wait_for_connected("/api/v1/telescope/status", "telescope")
    info = get("/api/v1/telescope/info")
    t.case("telescope /info returns capabilities", lambda: _truthy(info.get("capabilities")))
    caps = info.get("capabilities", {})
    t.case("caps.canSync is true (every goto mount)", lambda: _eq(caps.get("canSync"), True))
    # Tracking rate — simulator advertises TELESCOPE_TRACK_MODE, so canSetTrackRate should be true.
    t.case("caps.canSetTrackRate detected", lambda: _eq(caps.get("canSetTrackRate"), True))
    # Slew to Polaris-ish coords (RA 2.5h = 37.5°, Dec +89°).
    slew = post("/api/v1/telescope/slew", {"ra": 37.5, "dec": 89.0})
    t.case("slew returns success", lambda: _eq(slew.get("success"), True))
    track = post("/api/v1/telescope/trackRate", {"rate": "sidereal"})
    t.case("trackRate sidereal accepted", lambda: _eq(track.get("success"), True))
    post("/api/v1/telescope/disconnect")


def test_focuser(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Focuser (indi_simulator_focus)")
    dev = first_device("focusers")
    if not dev:
        t.skip("focuser", "no simulator focuser discovered")
        return
    post("/api/v1/focuser/connect", {"deviceId": dev["id"]}, expect=202)
    wait_for_connected("/api/v1/focuser/status", "focuser")
    info = get("/api/v1/focuser/info")
    caps = info.get("capabilities", {})
    t.case("focuser /info returns capabilities", lambda: _truthy(caps))
    t.case("hasTemperature detected (simulator publishes FOCUS_TEMPERATURE)",
           lambda: _eq(caps.get("hasTemperature"), True))
    move = post("/api/v1/focuser/move", {"position": 20000})
    t.case("focuser move accepted", lambda: _eq(move.get("success"), True))
    post("/api/v1/focuser/disconnect")


def test_filter_wheel(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Filter Wheel (indi_simulator_wheel)")
    dev = first_device("filterWheels")
    if not dev:
        t.skip("filter wheel", "no simulator wheel discovered")
        return
    post("/api/v1/filterwheel/connect", {"deviceId": dev["id"]}, expect=202)
    wait_for_connected("/api/v1/filterwheel/status", "filter wheel")
    info = get("/api/v1/filterwheel/info")
    t.case("wheel reports filterCount > 0", lambda: _gt(info.get("filterCount"), 0))
    change = post("/api/v1/filterwheel/change", {"position": 2})
    t.case("filter change accepted", lambda: _eq(change.get("success"), True))
    post("/api/v1/filterwheel/disconnect")


def test_rotator(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Rotator (indi_simulator_rotator)")
    dev = first_device("rotators")
    if not dev:
        t.skip("rotator", "no simulator rotator discovered")
        return
    post("/api/v1/rotator/connect", {"deviceId": dev["id"]}, expect=202)
    wait_for_connected("/api/v1/rotator/status", "rotator")
    move = post("/api/v1/rotator/move", {"position": 45.0})
    t.case("rotator move accepted", lambda: _eq(move.get("success"), True))
    post("/api/v1/rotator/disconnect")


def test_dome(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Dome (indi_simulator_dome)")
    dev = first_device("domes")
    if not dev:
        t.skip("dome", "no simulator dome discovered")
        return
    post("/api/v1/dome/connect", {"deviceId": dev["id"]}, expect=202)
    wait_for_connected("/api/v1/dome/status", "dome")
    info = get("/api/v1/dome/info")
    t.case("dome /info returns capabilities", lambda: _truthy(info.get("capabilities")))
    opn = post("/api/v1/dome/open")
    t.case("dome open accepted", lambda: _eq(opn.get("success"), True))
    post("/api/v1/dome/close")
    post("/api/v1/dome/disconnect")


def test_weather(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Weather (indi_simulator_weather)")
    dev = first_device("weather")
    if not dev:
        t.skip("weather", "no simulator weather discovered")
        return
    post("/api/v1/weather/connect", {"deviceId": dev["id"]}, expect=202)
    wait_for_connected("/api/v1/weather/status", "weather")
    s = get("/api/v1/weather/status")
    t.case("weather returns readings",
           lambda: _truthy(s.get("temperatureC") is not None or s.get("humidityPct") is not None))
    post("/api/v1/weather/disconnect")


def test_flat_panel(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Flat Panel (indi_simulator_lightpanel)")
    dev = first_device("flatDevices")
    if not dev:
        t.skip("flat panel", "no simulator light panel discovered (some libindi builds omit it)")
        return
    post("/api/v1/flatpanel/connect", {"deviceId": dev["id"]}, expect=202)
    wait_for_connected("/api/v1/flatpanel/status", "flat panel")
    bright = post("/api/v1/flatpanel/brightness", {"brightness": 128})
    t.case("flat brightness accepted", lambda: _eq(bright.get("success"), True))
    post("/api/v1/flatpanel/off")
    post("/api/v1/flatpanel/disconnect")


def test_diagnostics_dump(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Diagnostics")
    dump = get("/api/v1/diagnostics")
    t.case("/diagnostics returns indiConnected=true", lambda: _eq(dump.get("indiConnected"), True))
    t.case("/diagnostics lists devices", lambda: _gt(len(dump.get("devices") or []), 0))


# ─────────── Assertion helpers ───────────

def _eq(actual, expected):
    assert actual == expected, f"expected {expected!r}, got {actual!r}"


def _truthy(v):
    assert v, f"expected truthy, got {v!r}"


def _gt(v, min_val):
    assert v is not None and v > min_val, f"expected > {min_val}, got {v!r}"


# ─────────── Server lifecycle ───────────

def wait_for_server():
    deadline = time.time() + STARTUP_TIMEOUT_SEC
    while time.time() < deadline:
        try:
            r = requests.get(f"{BASE_URL}/api/v1/health", timeout=2)
            if r.status_code == 200:
                return True
        except requests.RequestException:
            pass
        time.sleep(0.5)
    return False


def start_server() -> subprocess.Popen:
    env = os.environ.copy()
    env["NINA_INDI_DRIVERS"] = SIMULATOR_DRIVERS
    # Ensure the INDI_FIFO path in IndiServerManager can be created, and use a
    # dedicated config dir so the test doesn't clobber dev data.
    env["NINA_CONFIG_DIR"] = str(ROOT / "scripts" / "tests" / ".test-data")
    Path(env["NINA_CONFIG_DIR"]).mkdir(parents=True, exist_ok=True)
    print(f"Starting server with simulators: {SIMULATOR_DRIVERS}")
    proc = subprocess.Popen(
        ["dotnet", "run", "--project", str(SERVER_PROJECT), "--no-build"],
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
    )
    return proc


def stop_server(proc: subprocess.Popen):
    if proc.poll() is not None:
        return
    proc.send_signal(signal.SIGINT)
    try:
        proc.wait(timeout=10)
    except subprocess.TimeoutExpired:
        proc.kill()


# ─────────── Main ───────────

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--keep-alive", action="store_true",
                    help="Leave the server running after tests finish")
    ap.add_argument("--external-server", action="store_true",
                    help="Assume a server is already running at localhost:1888; skip lifecycle")
    args = ap.parse_args()

    print("Building nina-headless...")
    build = subprocess.run(
        ["dotnet", "build", str(SERVER_PROJECT), "-c", "Debug", "--nologo"],
        capture_output=True, text=True,
    )
    if build.returncode != 0:
        print(build.stdout[-2000:])
        print(build.stderr[-2000:])
        sys.exit(f"{C_RED}Build failed{C_RESET}")

    proc = None
    if not args.external_server:
        proc = start_server()
        if not wait_for_server():
            stop_server(proc)
            sys.exit(f"{C_RED}Server did not become healthy within {STARTUP_TIMEOUT_SEC}s{C_RESET}")
        # Give indiserver a beat to finish property discovery. The server answers
        # /health the moment it binds the socket, but simulators take a second or
        # two to publish their initial property vectors.
        time.sleep(5)

    t = TestRunner()
    try:
        test_health_and_discovery(t)
        test_camera(t)
        test_telescope(t)
        test_focuser(t)
        test_filter_wheel(t)
        test_rotator(t)
        test_dome(t)
        test_weather(t)
        test_flat_panel(t)
        test_diagnostics_dump(t)
    finally:
        ok = t.summary()
        if proc and not args.keep_alive:
            stop_server(proc)
        elif proc and args.keep_alive:
            print(f"\n{C_YELLOW}Server still running at {BASE_URL} (PID {proc.pid}){C_RESET}")
        sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
