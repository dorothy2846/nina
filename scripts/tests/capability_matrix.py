#!/usr/bin/env python3
"""
Driver installation report + capability probe verification.

What's technically knowable offline:

1. **Every driver's DRIVER_INTERFACE bitmask** — published immediately on getProperties.
   Tells us what KIND of device (telescope / camera / focuser / ...) each driver is.
   This lets us verify "driver X claims to be a focuser" vs "driver Y is a
   multi-interface powerbox". No hardware needed.

2. **Simulator drivers publish their full capability property set pre-connection**
   because the simulator fakes the hardware connection. Feeding simulator fixtures
   through our capability probes gives a real regression signal: "does our
   detection of hasDewHeater still trip on CCD_DEW_CONTROL for the ccd simulator?"

What's NOT knowable from a pre-connection fixture:

- Real vendor drivers' capability properties (CCD_COOLER, FOCUS_TEMPERATURE, etc.)
  are only published AFTER the driver successfully connects to hardware. A
  disconnected real driver shows only CONNECTION / DRIVER_INFO / generic.
  Capability regression for these requires either (a) live hardware, (b) an
  in-tree simulator counterpart, or (c) snapshot fixtures contributed by users
  via the /api/v1/diagnostics endpoint.

Usage:
  ./capability_matrix.py                       # classify all fixtures + probe simulators
  ./capability_matrix.py --csv > report.csv    # machine-readable
  ./capability_matrix.py --filter ccd          # subset
"""

import argparse
import sys
from pathlib import Path
from xml.etree import ElementTree as ET

IFACE = {
    "TELESCOPE":    0x0001,
    "CAMERA":       0x0002,
    "GUIDER":       0x0004,
    "FOCUSER":      0x0008,
    "FILTER":       0x0010,
    "DOME":         0x0020,
    "GPS":          0x0040,
    "WEATHER":      0x0080,
    "AO":           0x0100,
    "DUSTCAP":      0x0200,
    "LIGHTBOX":     0x0400,
    "DETECTOR":     0x1000,
    "ROTATOR":      0x0800,
    "SPECTROGRAPH": 0x2000,
    "CORRELATOR":   0x4000,
    "AUX":          0x8000,
}

PROBES = {
    "canAbort":            ["CCD_ABORT_EXPOSURE"],
    "hasDewHeater":        ["CCD_DEW_CONTROL", "AUX_HEATER_TOGGLE", "ANTI_DEW"],
    "hasOffset":           ["CCD_OFFSET"],
    "hasGain":             ["CCD_GAIN"],
    "canSetTemperature":   ["CCD_COOLER", "CCD_TEMPERATURE"],
    "canSetTrackRate":     ["TELESCOPE_TRACK_MODE"],
    "canSync":             ["ON_COORD_SET"],
    "canPark":             ["TELESCOPE_PARK"],
    "canFindHome":         ["TELESCOPE_HOME"],
    "canSetTracking":      ["TELESCOPE_TRACK_STATE"],
    "canPulseGuide":       ["TELESCOPE_TIMED_GUIDE_NS", "TELESCOPE_TIMED_GUIDE_WE"],
    "hasTemperature":      ["FOCUS_TEMPERATURE"],
    "hasTempCompensation": ["FOCUS_TEMPERATURE_COMPENSATION", "AUTO_FOCUS_COMP"],
    "hasBacklash":         ["FOCUS_BACKLASH_STEPS", "FOCUS_BACKLASH_TOGGLE"],
    "hasFlatLight":        ["FLAT_LIGHT_CONTROL"],
    "hasFlatIntensity":    ["FLAT_LIGHT_INTENSITY"],
}


def parse_dump(xml_bytes: bytes) -> tuple[int, set[str]]:
    wrapped = b"<root>" + xml_bytes + b"</root>"
    try:
        root = ET.fromstring(wrapped)
    except ET.ParseError:
        trimmed = xml_bytes[:xml_bytes.rfind(b"</def")]
        if not trimmed:
            return (0, set())
        last_close = trimmed.rfind(b">")
        if last_close <= 0:
            return (0, set())
        root = ET.fromstring(b"<root>" + trimmed[:last_close + 1] + b"</root>")

    properties: set[str] = set()
    interface_bitmask = 0
    for elem in root:
        if not (elem.tag.startswith("def") and elem.tag.endswith("Vector")):
            continue
        name = elem.attrib.get("name")
        if name:
            properties.add(name)
        if name == "DRIVER_INFO":
            for child in elem:
                if child.attrib.get("name") == "DRIVER_INTERFACE" and child.text:
                    try:
                        interface_bitmask = int(child.text.strip())
                    except ValueError:
                        pass
    return (interface_bitmask, properties)


def classify(bitmask: int) -> list[str]:
    return [label for label, bit in IFACE.items() if bitmask & bit]


def probe(properties: set[str]) -> dict[str, bool]:
    return {k: any(p in properties for p in names) for k, names in PROBES.items()}


def is_simulator(name: str) -> bool:
    return "_simulator_" in name or name.endswith("simulator")


def run(fixture_dir: Path, csv: bool, filter_str: str | None, include_sim: bool) -> int:
    fixtures = sorted(fixture_dir.glob("*.xml"))
    if filter_str:
        fixtures = [f for f in fixtures if filter_str in f.stem]
    if not fixtures:
        print(f"No fixtures under {fixture_dir}", file=sys.stderr)
        return 1

    rows = []
    for fx in fixtures:
        iface, props = parse_dump(fx.read_bytes())
        rows.append({
            "driver": fx.stem,
            "kinds": classify(iface),
            "interface": iface,
            "property_count": len(props),
            "is_simulator": is_simulator(fx.stem),
            "caps": probe(props),
        })

    real_rows = [r for r in rows if not r["is_simulator"]]
    sim_rows = [r for r in rows if r["is_simulator"]]

    if csv:
        fields = ["driver", "kinds", "interface", "property_count", "is_simulator"] + list(PROBES.keys())
        print(",".join(fields))
        for r in rows:
            vals = [r["driver"], "|".join(r["kinds"]), str(r["interface"]),
                    str(r["property_count"]), str(r["is_simulator"])]
            vals += [("1" if r["caps"][k] else "0") for k in PROBES]
            print(",".join(vals))
        return 0

    print("# Driver verification report\n")
    print(f"Generated from {len(rows)} driver fixture(s) captured via `driver_smoke_test.py --fixture-dir`.\n")

    print("## Installation health\n")
    print(f"- Drivers bundled: {len(rows)} ({len(real_rows)} vendor, {len(sim_rows)} simulator)")
    print(f"- Every listed driver launched + spoke INDI protocol successfully "
          f"(else it would be missing from the fixture set).\n")

    # Device-kind histogram from DRIVER_INTERFACE.
    print("## Device-kind coverage (from DRIVER_INTERFACE)\n")
    print("Counts drivers whose bitmask advertises each kind. Multi-kind drivers "
          "(e.g. powerboxes = FOCUSER + AUX + DUSTCAP) are counted once per kind.\n")
    print("| Kind | Drivers |")
    print("|---|---|")
    for label, bit in IFACE.items():
        count = sum(1 for r in real_rows if r["interface"] & bit)
        if count:
            print(f"| {label} | {count} |")
    print()

    if include_sim and sim_rows:
        print("## Simulator capability probe results\n")
        print("Simulator drivers publish their full capability property set pre-connection "
              "(no hardware to wait on), so these are the only fixtures where offline "
              "capability probing produces real signal. If a probe below says `—` for a "
              "simulator that obviously should support the feature, that's a regression "
              "in either the simulator or our probe names.\n")
        cap_keys = list(PROBES.keys())
        print("| Driver | " + " | ".join(cap_keys) + " |")
        print("|" + "---|" * (len(cap_keys) + 1))
        for r in sim_rows:
            cells = [r["driver"]] + ["✓" if r["caps"][k] else "—" for k in cap_keys]
            print("| " + " | ".join(cells) + " |")
        print()

    print("## Per-driver kind classification (vendor drivers)\n")
    print("For real drivers the pre-connection property set is generic "
          "(CONNECTION / DRIVER_INFO / POLLING_PERIOD / …) — capability-specific "
          "properties only show up after hardware connect. Real-gear capability "
          "regressions are caught via (a) simulator fixtures above, (b) user-"
          "submitted diagnostic dumps (/api/v1/diagnostics), or (c) live hardware tests.\n")
    # Group by first declared kind.
    by_kind: dict[str, list[dict]] = {}
    for r in real_rows:
        k = r["kinds"][0] if r["kinds"] else "UNCLASSIFIED"
        by_kind.setdefault(k, []).append(r)
    for kind in sorted(by_kind):
        drivers = by_kind[kind]
        print(f"### {kind} ({len(drivers)} drivers)\n")
        names = [d["driver"] for d in sorted(drivers, key=lambda d: d["driver"])]
        # Two-column layout for readability.
        col = (len(names) + 1) // 2
        left = names[:col]
        right = names[col:]
        for i in range(col):
            l = left[i] if i < len(left) else ""
            r = right[i] if i < len(right) else ""
            print(f"- {l:40s} - {r}" if r else f"- {l}")
        print()

    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--fixture-dir", default=str(Path(__file__).parent / "fixtures"))
    ap.add_argument("--csv", action="store_true")
    ap.add_argument("--filter", default=None)
    ap.add_argument("--no-simulators", action="store_true",
                    help="Skip the simulator capability section")
    args = ap.parse_args()
    sys.exit(run(Path(args.fixture_dir), args.csv, args.filter, not args.no_simulators))


if __name__ == "__main__":
    main()
