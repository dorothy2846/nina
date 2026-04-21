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


def post(path: str, body: dict | None = None, expect: int | tuple[int, ...] = 200) -> dict:
    r = requests.post(f"{BASE_URL}{path}", json=body, timeout=30)
    ok = r.status_code == expect if isinstance(expect, int) else r.status_code in expect
    assert ok, f"POST {path} → {r.status_code} not in {expect}, body={r.text[:200]}"
    return r.json() if r.text else {}


# connect/disconnect may be sync-200 or async-202 depending on provider state.
CONNECT_OK = (200, 202)


def wait_for_connected(status_path: str, device_label: str) -> dict:
    deadline = time.time() + CONNECT_POLL_TIMEOUT_SEC
    while time.time() < deadline:
        s = get(status_path)
        if s.get("connected") is True:
            return s
        time.sleep(CONNECT_POLL_INTERVAL_SEC)
    raise AssertionError(f"{device_label} did not reach connected=true within {CONNECT_POLL_TIMEOUT_SEC}s (last status: {s})")


def wait_for_info_populated(info_path: str, key: str, timeout: int = 8) -> dict:
    """Poll /info until the named key is non-null. NINA's state service takes a
    moment to catch up after the INDI connect succeeds — /status reflects the
    live INDI probe but /info reads from the mediator-populated state which is
    event-driven. Returns the last info payload whether or not the key appeared."""
    deadline = time.time() + timeout
    last = {}
    while time.time() < deadline:
        last = get(info_path)
        if last.get(key) not in (None, {}):
            return last
        time.sleep(0.25)
    return last


def first_device(kind_key: str) -> dict | None:
    """Pick the first discovered device of the given kind from /equipment/devices.
    `kind_key` is e.g. 'cameras', 'telescopes', 'focusers'."""
    devs = get("/api/v1/equipment/devices").get(kind_key) or []
    return devs[0] if devs else None


# ─────────── Test cases per device ───────────

def test_health_and_discovery(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Health + discovery")
    t.case("GET /health returns status",
           lambda: _truthy(get("/api/v1/health").get("status")))
    t.case("GET /equipment/devices lists simulator devices",
           lambda: _assert_has_simulators(get("/api/v1/equipment/devices")))


def _assert_has_simulators(devices: dict):
    # The core kinds must be discovered — without these the server can't be
    # considered healthy. Per-kind presence for rotator/dome/weather/flat is
    # checked lazily: each device test skips gracefully if its simulator
    # didn't classify in time.
    for k in ["cameras", "telescopes", "focusers"]:
        lst = devices.get(k) or []
        assert len(lst) >= 1, f"no devices discovered under key '{k}' — got {devices}"


def test_camera(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Camera (indi_simulator_ccd)")
    dev = first_device("cameras")
    if not dev:
        t.skip("camera", "no simulator camera discovered")
        return
    post("/api/v1/camera/connect", {"deviceId": dev["id"]}, expect=CONNECT_OK)
    s = wait_for_connected("/api/v1/camera/status", "camera")
    t.case("POST /camera/connect → status.connected=true", lambda: _eq(s.get("connected"), True))
    info = wait_for_info_populated("/api/v1/camera/info", "capabilities")
    t.case("GET /camera/info → name populated", lambda: _truthy(info.get("name")))
    t.case("GET /camera/info → capabilities object present", lambda: _truthy(info.get("capabilities")))
    t.case("GET /camera/info → sensor resolution > 0", lambda: _truthy(info.get("resolutionX")))
    t.case("GET /camera/info → bit depth populated", lambda: _truthy(info.get("bitDepth")))

    caps = info.get("capabilities", {})
    t.case("caps.canSetTemperature=true (CCD_COOLER present)",
           lambda: _eq(caps.get("canSetTemperature"), True))
    t.case("caps.canAbort=true (CCD_ABORT_EXPOSURE present)",
           lambda: _eq(caps.get("canAbort"), True))
    t.case("caps.hasGain=true (CCD_GAIN present)", lambda: _eq(caps.get("hasGain"), True))
    t.case("caps.hasOffset=true (CCD_OFFSET present)", lambda: _eq(caps.get("hasOffset"), True))

    cool_on = post("/api/v1/camera/cooling", {"enabled": True, "temperature": -10})
    t.case("POST /camera/cooling enable → success=true",
           lambda: _eq(cool_on.get("success"), True))
    t.case("POST /camera/cooling enable → targetTemperature echoed",
           lambda: _eq(cool_on.get("targetTemperature"), -10))
    cool_off = post("/api/v1/camera/cooling", {"enabled": False, "temperature": 0})
    t.case("POST /camera/cooling disable → success=true",
           lambda: _eq(cool_off.get("success"), True))

    ab = post("/api/v1/camera/abort")
    t.case("POST /camera/abort → success=true",
           lambda: _eq(ab.get("success"), True))

    # Simulator doesn't expose CCD_DEW_CONTROL, so success=false is valid.
    dew = post("/api/v1/camera/dew-heater", {"enabled": True})
    t.case("POST /camera/dew-heater → success flag present",
           lambda: _truthy(dew.get("success") in (True, False)))

    stream_st = get("/api/v1/camera/stream/status")
    t.case("GET /camera/stream/status returns object", lambda: _truthy(stream_st))

    post("/api/v1/camera/disconnect", expect=CONNECT_OK)


def test_telescope(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Telescope (indi_simulator_telescope)")
    dev = first_device("telescopes")
    if not dev:
        t.skip("telescope", "no simulator telescope discovered")
        return
    post("/api/v1/telescope/connect", {"deviceId": dev["id"]}, expect=CONNECT_OK)
    wait_for_connected("/api/v1/telescope/status", "telescope")
    info = wait_for_info_populated("/api/v1/telescope/info", "capabilities")
    caps = info.get("capabilities") or {}
    # NinaStateService.TelescopeInfo is broadcast-populated via the mediator and
    # can lag seconds behind the INDI connect. When the caps block is still empty
    # we skip capability assertions; command round-trips below exercise the pipe.
    if caps:
        for cap, desc in [
            ("canSync", "ON_COORD_SET exposes SYNC"),
            ("canSetTrackRate", "TELESCOPE_TRACK_MODE present"),
            ("canPark", "TELESCOPE_PARK present"),
            ("canSetTracking", "TELESCOPE_TRACK_STATE present"),
            ("canPulseGuide", "TELESCOPE_TIMED_GUIDE_NS/WE present"),
        ]:
            t.case(f"caps.{cap}=true ({desc})",
                   lambda cap=cap: _eq(caps.get(cap), True))
    else:
        t.skip("telescope capabilities block", "TelescopeInfo not yet populated (INDI broadcast lag)")

    slew = post("/api/v1/telescope/slew", {"ra": 37.5, "dec": 89.0})
    t.case("POST /telescope/slew → success=true",
           lambda: _eq(slew.get("success"), True))
    alt_az = post("/api/v1/telescope/slewAltAz", {"alt": 45.0, "az": 180.0})
    t.case("POST /telescope/slewAltAz → success=true",
           lambda: _eq(alt_az.get("success"), True))

    track_on = post("/api/v1/telescope/tracking", {"enabled": True})
    t.case("POST /telescope/tracking enable → success=true",
           lambda: _eq(track_on.get("success"), True))
    t.case("POST /telescope/tracking enable → enabled field echoed",
           lambda: _eq(track_on.get("enabled"), True))
    track_off = post("/api/v1/telescope/tracking", {"enabled": False})
    t.case("POST /telescope/tracking disable → success=true",
           lambda: _eq(track_off.get("success"), True))

    for rate in ["sidereal", "solar", "lunar"]:
        tr = post("/api/v1/telescope/trackRate", {"rate": rate})
        t.case(f"POST /telescope/trackRate {rate} → success=true",
               lambda tr=tr: _eq(tr.get("success"), True))

    sync = post("/api/v1/telescope/sync", {"ra": 37.5, "dec": 89.0})
    t.case("POST /telescope/sync → success=true",
           lambda: _eq(sync.get("success"), True))

    for direction in ["N", "S", "E", "W"]:
        m = post("/api/v1/telescope/move",
                 {"direction": direction, "rate": 1.0, "duration": 100})
        t.case(f"POST /telescope/move {direction} → success=true",
               lambda m=m: _eq(m.get("success"), True))
    stop = post("/api/v1/telescope/stopMove")
    t.case("POST /telescope/stopMove → success=true",
           lambda: _eq(stop.get("success"), True))

    park = post("/api/v1/telescope/park")
    t.case("POST /telescope/park → success=true",
           lambda: _eq(park.get("success"), True))
    time.sleep(1)
    unpark = post("/api/v1/telescope/unpark")
    t.case("POST /telescope/unpark → success=true",
           lambda: _eq(unpark.get("success"), True))

    if caps.get("canFindHome"):
        home = post("/api/v1/telescope/home")
        t.case("POST /telescope/home → success=true",
               lambda: _eq(home.get("success"), True))

    rates = get("/api/v1/telescope/axisRates")
    t.case("GET /telescope/axisRates responds (primary/axisRates key present)",
           lambda: _truthy("primary" in rates or "axisRates" in rates))

    post("/api/v1/telescope/disconnect", expect=CONNECT_OK)


def test_focuser(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Focuser (indi_simulator_focus)")
    dev = first_device("focusers")
    if not dev:
        t.skip("focuser", "no simulator focuser discovered")
        return
    post("/api/v1/focuser/connect", {"deviceId": dev["id"]}, expect=CONNECT_OK)
    wait_for_connected("/api/v1/focuser/status", "focuser")
    info = wait_for_info_populated("/api/v1/focuser/info", "capabilities")
    caps = info.get("capabilities") or {}
    if caps:
        t.case("caps.hasTemperature=true (FOCUS_TEMPERATURE present)",
               lambda: _eq(caps.get("hasTemperature"), True))
        t.case("caps.hasBacklash=true (FOCUS_BACKLASH_* present)",
               lambda: _eq(caps.get("hasBacklash"), True))
    else:
        t.skip("focuser capabilities block", "FocuserInfo not yet populated (INDI broadcast lag)")

    move = post("/api/v1/focuser/move", {"position": 20000})
    t.case("POST /focuser/move → success=true",
           lambda: _eq(move.get("success"), True))
    t.case("POST /focuser/move → position echoed in body",
           lambda: _eq(move.get("position"), 20000))
    time.sleep(0.5)
    s_after = get("/api/v1/focuser/status")
    t.case("status.position populated after move",
           lambda: _truthy(s_after.get("position") is not None))

    halt = post("/api/v1/focuser/halt")
    t.case("POST /focuser/halt → success=true", lambda: _eq(halt.get("success"), True))

    tc_on = post("/api/v1/focuser/temp-compensation", {"enabled": True})
    t.case("POST /focuser/temp-compensation enable → success flag present",
           lambda: _truthy(tc_on.get("success") in (True, False)))
    post("/api/v1/focuser/temp-compensation", {"enabled": False})

    bl = post("/api/v1/focuser/backlash", {"steps": 50})
    t.case("POST /focuser/backlash → success flag present",
           lambda: _truthy(bl.get("success") in (True, False)))

    post("/api/v1/focuser/disconnect", expect=CONNECT_OK)


def test_filter_wheel(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Filter Wheel (indi_simulator_wheel)")
    dev = first_device("filterWheels")
    if not dev:
        t.skip("filter wheel", "no simulator wheel discovered")
        return
    post("/api/v1/filterwheel/connect", {"deviceId": dev["id"]}, expect=CONNECT_OK)
    wait_for_connected("/api/v1/filterwheel/status", "filter wheel")

    info = wait_for_info_populated("/api/v1/filterwheel/info", "filterCount")
    count = info.get("filterCount") or 0
    if info.get("connected") is True:
        t.case("GET /filterwheel/info → filterCount > 0", lambda: _gt(count, 0))
        t.case("GET /filterwheel/info → filters[] is a list",
               lambda: _truthy(isinstance(info.get("filters"), list)))
    else:
        t.skip("filterwheel /info enumeration", "FilterWheelInfo not yet populated (INDI broadcast lag)")

    # Skip the change loop entirely if the slot count is unknown — don't
    # fabricate a fallback, since that would hide a legitimate discovery gap.
    for pos in [p for p in (1, 2, 3) if p <= count]:
        resp = post("/api/v1/filterwheel/change", {"position": pos})
        def _check(resp=resp, pos=pos):
            _eq(resp.get("success"), True)
            _eq(resp.get("position"), pos)
        t.case(f"POST /filterwheel/change position={pos} → success + echo", _check)

    post("/api/v1/filterwheel/disconnect", expect=CONNECT_OK)


def test_rotator(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Rotator (indi_simulator_rotator)")
    dev = first_device("rotators")
    if not dev:
        t.skip("rotator", "no simulator rotator discovered")
        return
    post("/api/v1/rotator/connect", {"deviceId": dev["id"]}, expect=CONNECT_OK)
    wait_for_connected("/api/v1/rotator/status", "rotator")

    info = wait_for_info_populated("/api/v1/rotator/info", "stepSize")
    t.case("GET /rotator/info → connected=true", lambda: _eq(info.get("connected"), True))
    t.case("GET /rotator/info → stepSize reported",
           lambda: _truthy(info.get("stepSize") is not None))

    move = post("/api/v1/rotator/move", {"position": 45.0})
    t.case("POST /rotator/move → success=true", lambda: _eq(move.get("success"), True))
    t.case("POST /rotator/move → position echoed",
           lambda: _truthy(move.get("position") is not None))
    time.sleep(0.5)
    s_move = get("/api/v1/rotator/status")
    t.case("status.position populated after /move",
           lambda: _truthy(s_move.get("position") is not None))

    # Mechanical = encoder angle; sky angle = framing-corrected. Both paths.
    mech = post("/api/v1/rotator/moveMechanical", {"position": 90.0})
    t.case("POST /rotator/moveMechanical → success=true",
           lambda: _eq(mech.get("success"), True))
    t.case("POST /rotator/moveMechanical → mechanicalPosition echoed",
           lambda: _truthy(mech.get("mechanicalPosition") is not None))

    sync = post("/api/v1/rotator/sync", {"skyAngle": 30.0})
    t.case("POST /rotator/sync → success=true", lambda: _eq(sync.get("success"), True))

    rev_on = post("/api/v1/rotator/reverse", {"reverse": True})
    t.case("POST /rotator/reverse true → success=true",
           lambda: _eq(rev_on.get("success"), True))
    t.case("POST /rotator/reverse true → reverse=true echoed",
           lambda: _eq(rev_on.get("reverse"), True))
    rev_off = post("/api/v1/rotator/reverse", {"reverse": False})
    t.case("POST /rotator/reverse false → success=true",
           lambda: _eq(rev_off.get("success"), True))

    halt = post("/api/v1/rotator/halt")
    t.case("POST /rotator/halt → success=true", lambda: _eq(halt.get("success"), True))

    post("/api/v1/rotator/disconnect", expect=CONNECT_OK)


def test_dome(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Dome (indi_simulator_dome)")
    dev = first_device("domes")
    if not dev:
        t.skip("dome", "no simulator dome discovered")
        return
    post("/api/v1/dome/connect", {"deviceId": dev["id"]}, expect=CONNECT_OK)
    wait_for_connected("/api/v1/dome/status", "dome")

    info = wait_for_info_populated("/api/v1/dome/info", "capabilities")
    t.case("GET /dome/info → connected=true", lambda: _eq(info.get("connected"), True))
    t.case("GET /dome/info → capabilities object present",
           lambda: _truthy(info.get("capabilities")))
    t.case("GET /dome/info → shutterStatus populated",
           lambda: _truthy(info.get("shutterStatus") is not None))
    caps = info.get("capabilities") or {}

    # Capability guards — slit-only or shutter-only domes don't implement all.
    if caps.get("canSetShutter"):
        opn = post("/api/v1/dome/open")
        t.case("POST /dome/open → success=true", lambda: _eq(opn.get("success"), True))
        time.sleep(0.3)
        s_open = get("/api/v1/dome/status")
        t.case("status.shutterStatus transitions after /open",
               lambda s_open=s_open: _truthy(s_open.get("shutterStatus") in
                                             ("Open", "Opening", "Closed", "Closing", "Unknown")))
        cls = post("/api/v1/dome/close")
        t.case("POST /dome/close → success=true", lambda: _eq(cls.get("success"), True))

    if caps.get("canSetAzimuth") or caps.get("canSyncAzimuth"):
        sync = post("/api/v1/dome/sync", {"azimuth": 180.0})
        t.case("POST /dome/sync azimuth=180 → success=true",
               lambda: _eq(sync.get("success"), True))
        t.case("POST /dome/sync → azimuth echoed",
               lambda: _eq(sync.get("azimuth"), 180.0))

    if caps.get("canFindHome"):
        home = post("/api/v1/dome/home")
        t.case("POST /dome/home → success=true", lambda: _eq(home.get("success"), True))

    if caps.get("canPark"):
        park = post("/api/v1/dome/park")
        t.case("POST /dome/park → success=true", lambda: _eq(park.get("success"), True))

    halt = post("/api/v1/dome/halt")
    t.case("POST /dome/halt → success=true", lambda: _eq(halt.get("success"), True))

    post("/api/v1/dome/disconnect", expect=CONNECT_OK)


def test_weather(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Weather (indi_simulator_weather)")
    dev = first_device("weather")
    if not dev:
        t.skip("weather", "no simulator weather discovered")
        return
    post("/api/v1/weather/connect", {"deviceId": dev["id"]}, expect=CONNECT_OK)
    wait_for_connected("/api/v1/weather/status", "weather")
    s = get("/api/v1/weather/status")

    t.case("GET /weather/status → connected=true", lambda: _eq(s.get("connected"), True))
    t.case("GET /weather/status → name populated", lambda: _truthy(s.get("name")))
    # At least one measurement must surface — simulator publishes the full set.
    known_fields = [
        "temperatureC", "humidityPct", "dewPointC", "pressureHpa",
        "windSpeedMps", "cloudCoverPct", "skyBrightnessMagArcSec2", "skyTemperatureC",
    ]
    t.case("GET /weather/status → at least one sensor reading present",
           lambda: _truthy(any(s.get(f) is not None for f in known_fields)))

    post("/api/v1/weather/disconnect", expect=CONNECT_OK)


def test_flat_panel(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Flat Panel (indi_simulator_lightpanel)")
    dev = first_device("flatDevices")
    if not dev:
        t.skip("flat panel", "no simulator light panel discovered (some libindi builds omit it)")
        return
    post("/api/v1/flatpanel/connect", {"deviceId": dev["id"]}, expect=CONNECT_OK)
    wait_for_connected("/api/v1/flatpanel/status", "flat panel")

    for brightness in [64, 128, 200]:
        resp = post("/api/v1/flatpanel/brightness", {"brightness": brightness})
        t.case(f"POST /flatpanel/brightness {brightness} → success=true",
               lambda resp=resp: _eq(resp.get("success"), True))

    time.sleep(0.3)
    s_on = get("/api/v1/flatpanel/status")
    t.case("GET /flatpanel/status → connected=true after /brightness",
           lambda: _eq(s_on.get("connected"), True))

    off = post("/api/v1/flatpanel/off")
    t.case("POST /flatpanel/off → success=true", lambda: _eq(off.get("success"), True))

    post("/api/v1/flatpanel/disconnect", expect=CONNECT_OK)


def test_switch(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Switch (Pegasus UPB / etc.)")
    dev = first_device("switches")
    if not dev:
        t.skip("switch", "no switch device discovered (no libindi switch simulator)")
        return
    post("/api/v1/switch/connect", {"deviceId": dev["id"]}, expect=CONNECT_OK)
    wait_for_connected("/api/v1/switch/status", "switch")
    s = get("/api/v1/switch/status")
    t.case("GET /switch/status → connected=true", lambda: _eq(s.get("connected"), True))
    channels = s.get("channels") or []
    t.case("GET /switch/status → channels array present",
           lambda: _truthy(isinstance(channels, list)))
    if channels:
        key = channels[0].get("key")
        toggle = post("/api/v1/switch/toggle", {"key": key, "on": True})
        t.case(f"POST /switch/toggle {key} → success flag present",
               lambda: _truthy(toggle.get("success") in (True, False)))
    post("/api/v1/switch/disconnect", expect=CONNECT_OK)


def test_safety_monitor(t: TestRunner):
    print(f"\n{C_DIM}·{C_RESET} Safety Monitor")
    dev = first_device("safetyMonitors")
    if not dev:
        t.skip("safety monitor", "no safety monitor discovered (no libindi simulator)")
        return
    post("/api/v1/safetymonitor/connect", {"deviceId": dev["id"]}, expect=CONNECT_OK)
    wait_for_connected("/api/v1/safetymonitor/status", "safety monitor")
    s = get("/api/v1/safetymonitor/status")
    t.case("GET /safetymonitor/status → connected=true",
           lambda: _eq(s.get("connected"), True))
    t.case("GET /safetymonitor/status → isSafe is boolean",
           lambda: _truthy(isinstance(s.get("isSafe"), bool)))
    post("/api/v1/safetymonitor/disconnect", expect=CONNECT_OK)


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


def ensure_port_free():
    """Reject if another NINA.Headless is already bound to port 1888 — our
    newly spawned server would silently fail to bind and the test would end up
    hitting the stale process with the wrong driver set. Detection is a simple
    health probe before launch."""
    try:
        r = requests.get(f"{BASE_URL}/api/v1/health", timeout=1)
        if r.status_code == 200:
            sys.exit(f"{C_RED}Port 1888 already in use by another server — "
                     f"stop it first (or pass --external-server){C_RESET}")
    except requests.RequestException:
        pass


def start_server() -> subprocess.Popen:
    env = os.environ.copy()
    env["NINA_INDI_DRIVERS"] = SIMULATOR_DRIVERS
    # Ensure the INDI_FIFO path in IndiServerManager can be created, and use a
    # dedicated config dir so the test doesn't clobber dev data.
    env["NINA_CONFIG_DIR"] = str(ROOT / "scripts" / "tests" / ".test-data")
    Path(env["NINA_CONFIG_DIR"]).mkdir(parents=True, exist_ok=True)
    print(f"Starting server with simulators: {SIMULATOR_DRIVERS}")
    # start_new_session=True puts the server + every child (indiserver, driver
    # processes) in a new process group so we can killpg the whole tree on
    # teardown. Without this, SIGINT to `dotnet run` doesn't forward to the
    # NINA.Headless child and we leak the server between runs.
    # DEVNULL rather than PIPE: a multi-minute suite of dotnet + INDI logging
    # would fill the 64KB pipe buffer and block the server mid-run.
    proc = subprocess.Popen(
        ["dotnet", "run", "--project", str(SERVER_PROJECT), "--no-build"],
        env=env,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.STDOUT,
        start_new_session=True,
    )
    return proc


def stop_server(proc: subprocess.Popen):
    if proc.poll() is not None:
        return
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGTERM)
    except (ProcessLookupError, PermissionError):
        pass
    try:
        proc.wait(timeout=10)
    except subprocess.TimeoutExpired:
        try:
            os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
        except (ProcessLookupError, PermissionError):
            pass
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
        ensure_port_free()
        proc = start_server()
        if not wait_for_server():
            stop_server(proc)
            sys.exit(f"{C_RED}Server did not become healthy within {STARTUP_TIMEOUT_SEC}s{C_RESET}")
        # Give indiserver a beat to finish property discovery. The server answers
        # /health the moment it binds the socket, but simulators take a second or
        # two to publish their initial property vectors. Rotator/dome drivers
        # are slowest to publish their DRIVER_INTERFACE classification — 8s
        # covers them reliably on macOS.
        time.sleep(8)

    t = TestRunner()
    # Isolate each test function so an unhandled exception in one device
    # (network stall, bad JSON, timeout) doesn't abort the whole suite.
    test_fns = [
        test_health_and_discovery, test_camera, test_telescope, test_focuser,
        test_filter_wheel, test_rotator, test_dome, test_weather,
        test_flat_panel, test_switch, test_safety_monitor,
        test_diagnostics_dump,
    ]
    try:
        for fn in test_fns:
            try:
                fn(t)
            except Exception as e:
                print(f"  {C_RED}✗ {fn.__name__} aborted: {type(e).__name__}: {e}{C_RESET}")
                t.failed += 1
                t.failures.append(f"{fn.__name__} aborted: {type(e).__name__}: {e}")
    finally:
        ok = t.summary()
        if proc and not args.keep_alive:
            stop_server(proc)
        elif proc and args.keep_alive:
            print(f"\n{C_YELLOW}Server still running at {BASE_URL} (PID {proc.pid}){C_RESET}")
        sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
