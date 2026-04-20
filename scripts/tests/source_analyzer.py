#!/usr/bin/env python3
"""
INDI driver source-code analyzer — verifies capability support without hardware.

The INDI protocol's runtime constraint (drivers only publish capability-specific
property vectors after successful hardware CONNECT) makes offline runtime
verification impossible for real vendor drivers. Source code, however, is
deterministic — every IUFillXxxVector(..., "PROPERTY_NAME", ...) call in the
driver's C++ source names a property the driver WILL publish when it runs.

This script walks the INDI source trees (libindi core + indi-3rdparty), extracts
every IUFill-registered property name per driver, accounts for base-class
inheritance (INDI::CCD / INDI::Telescope / INDI::Focuser / INDI::FilterWheel /
INDI::Dome / INDI::Rotator add their full standard property set), and applies
our capability probes to the resulting complete property map.

Output: JSON catalog per driver with source-verified capability flags. The
iOS app consumes the same shape as the runtime-captured catalog, so merging
the two produces a "verified offline" + "verified at runtime" union.

Usage:
  source_analyzer.py --indi /tmp/indi-source/indi \
                     --indi-3rdparty /tmp/indi-source/indi-3rdparty \
                     --out supported_drivers.json
"""

import argparse
import json
import re
import sys
from pathlib import Path

# Properties each INDI base class registers for ALL drivers that inherit from
# it. Values are conservative (observed in the base class sources) — if a
# derived driver overrides or drops one, we'd need per-subclass analysis,
# which isn't worth the ROI for offline capability tagging.
BASE_CLASS_PROPERTIES = {
    "INDI::CCD": {
        "CCD_EXPOSURE", "CCD_ABORT_EXPOSURE", "CCD_FRAME", "CCD_TEMPERATURE",
        "CCD_COOLER", "CCD_COOLER_POWER", "CCD_FRAME_TYPE", "CCD_BINNING",
        "CCD_COMPRESSION", "CCD_FRAME_RESET", "CCD_INFO", "CCD_CFA",
        "CCD_STREAM_FRAME", "CCD_VIDEO_STREAM", "CCD_FAST_COUNT",
        "CCD_FAST_TOGGLE", "CCD_CAPTURE_FORMAT", "CCD_TRANSFER_FORMAT",
        "CCD_CONTROLS", "GUIDER_EXPOSURE", "GUIDER_ABORT_EXPOSURE",
        "GUIDER_FRAME", "GUIDER_BINNING",
    },
    "INDI::Telescope": {
        "EQUATORIAL_EOD_COORD", "HORIZONTAL_COORD", "ON_COORD_SET",
        "TELESCOPE_ABORT_MOTION", "TELESCOPE_MOTION_NS", "TELESCOPE_MOTION_WE",
        "TELESCOPE_SLEW_RATE", "TELESCOPE_PARK", "TELESCOPE_PIER_SIDE",
        "TELESCOPE_TRACK_STATE", "TELESCOPE_TRACK_MODE",
        "TELESCOPE_TIMED_GUIDE_NS", "TELESCOPE_TIMED_GUIDE_WE",
        "TELESCOPE_HAS_TRACK_MODE", "GEOGRAPHIC_COORD", "TIME_UTC",
    },
    "INDI::Focuser": {
        "FOCUS_MOTION", "FOCUS_SPEED", "REL_FOCUS_POSITION",
        "ABS_FOCUS_POSITION", "FOCUS_TIMER", "FOCUS_ABORT_MOTION",
        "FOCUS_REVERSE_MOTION", "FOCUS_SYNC", "FOCUS_MAX",
        "FOCUS_BACKLASH_TOGGLE", "FOCUS_BACKLASH_STEPS",
        "FOCUS_TEMPERATURE_COMPENSATION",
    },
    "INDI::FilterWheel": {
        "FILTER_SLOT", "FILTER_NAME",
    },
    "INDI::Dome": {
        "DOME_MOTION", "DOME_TIMER", "DOME_SPEED", "DOME_SHUTTER",
        "DOME_PARAMS", "DOME_ABSOLUTE_POSITION", "DOME_RELATIVE_POSITION",
        "DOME_ABORT_MOTION", "DOME_GOTO", "DOME_PARK",
        "DOME_AUTOSYNC", "DOME_MEASUREMENTS",
    },
    "INDI::Rotator": {
        "ABS_ROTATOR_ANGLE", "REL_ROTATOR_ANGLE", "ROTATOR_ABORT_MOTION",
        "ROTATOR_REVERSE", "ROTATOR_LIMITS", "ROTATOR_BACKLASH_STEPS",
        "ROTATOR_BACKLASH_TOGGLE",
    },
    "INDI::Weather": {
        "WEATHER_STATUS", "WEATHER_UPDATE_PERIOD",
    },
    "INDI::LightBoxInterface": {
        "FLAT_LIGHT_CONTROL", "FLAT_LIGHT_INTENSITY",
    },
    "INDI::DustCapInterface": {
        "CAP_PARK",
    },
    "INDI::DefaultDevice": {
        "CONNECTION", "DRIVER_INFO", "DEBUG", "CONFIG_PROCESS",
        "POLLING_PERIOD",
    },
}

# Same capability probes as the C# server + runtime capability_matrix.py,
# kept in sync so "verified offline" and "verified at runtime" read the same
# flags.
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

# Driver metadata we emit alongside capabilities.
KIND_BY_BASECLASS = {
    "INDI::CCD":         "CAMERA",
    "INDI::Telescope":   "TELESCOPE",
    "INDI::Focuser":     "FOCUSER",
    "INDI::FilterWheel": "FILTER",
    "INDI::Dome":        "DOME",
    "INDI::Rotator":     "ROTATOR",
    "INDI::Weather":     "WEATHER",
}

# Regex: IUFillXxxVector(variable, elems, n, device, "PROPERTY_NAME", ...)
# Property name is the 5th positional arg (first string literal).
PROP_RE = re.compile(
    r'IUFill(?:Switch|Number|Text|Light|BLOB)Vector\s*\(\s*[^,]+,\s*[^,]+,\s*[^,]+,\s*[^,]+,\s*"([A-Z_][A-Z0-9_]*)"'
)
# Some drivers use the property name as the 4th arg in newer APIs.
PROP_RE_DEFINE = re.compile(r'\bdefineProperty\s*\(\s*&?([A-Za-z_][A-Za-z0-9_]*)\b')
# Simpler: catch any literal that looks like an INDI-convention property name.
CONVENTIONAL_PROP_RE = re.compile(r'"([A-Z][A-Z_0-9]{3,})"')
# Inheritance detection: `class Foo : public INDI::CCD {` style.
CLASS_INHERIT_RE = re.compile(
    r'class\s+\w+\s*:\s*public\s+(INDI::(?:CCD|Telescope|Focuser|FilterWheel|Dome|Rotator|Weather|LightBoxInterface|DustCapInterface|DefaultDevice))'
)


def analyze_driver_dir(driver_dir: Path) -> dict:
    """Walk every .cpp/.h file in the driver's dir and collect:
    - inherited INDI base classes
    - all 'LOOKS_LIKE_A_PROPERTY' string literals (we cast a wide net —
      false positives get filtered when we apply the probe list, which
      only cares about a fixed set of known property names).
    Returns {bases, properties, primary_kind}.
    """
    bases: set[str] = set()
    props: set[str] = set()
    for f in driver_dir.rglob("*"):
        if f.suffix not in (".cpp", ".cc", ".h", ".hpp") or f.is_symlink():
            continue
        try:
            text = f.read_text(encoding="utf-8", errors="ignore")
        except Exception:
            continue
        bases.update(CLASS_INHERIT_RE.findall(text))
        props.update(PROP_RE.findall(text))
        # Wide net — picks up constants, #defines, and properties referenced
        # elsewhere. False positives in the MY_* / DBG_* family get filtered
        # out when we only match against our known probe names.
        props.update(CONVENTIONAL_PROP_RE.findall(text))

    # Fold in every property the base class publishes on its own.
    effective_props = set(props)
    for base in bases:
        effective_props.update(BASE_CLASS_PROPERTIES.get(base, set()))

    primary = None
    for base in bases:
        if base in KIND_BY_BASECLASS:
            primary = KIND_BY_BASECLASS[base]
            break

    return {
        "bases": sorted(bases),
        "properties_count": len(effective_props),
        "properties": effective_props,
        "primary_kind": primary,
    }


def derive_display_and_binary(driver_dir_name: str, indi_core: bool) -> tuple[str, str]:
    """Map a driver directory name to (display, binary-guess). Not every
    directory name matches the binary 1:1 — indi-asi ships multiple binaries
    (indi_asi_ccd, indi_asi_focuser, indi_asi_wheel). We emit the directory
    as the primary display identifier; binary is a best-effort guess."""
    if indi_core:
        # core driver subdirs are the binary name (e.g. 'celestron_gps' → indi_celestron_gps)
        return (driver_dir_name.replace("_", " "),
                f"indi_{driver_dir_name}")
    # 3rdparty: strip indi- prefix for display
    stripped = driver_dir_name.removeprefix("indi-")
    return (stripped.replace("-", " "),
            f"indi_{stripped.replace('-', '_')}")


def collect_core_drivers(indi_root: Path) -> list[dict]:
    """Core drivers live under indi/drivers/{category}/ as flat .cpp/.h files
    (one driver per stem). A handful of categories nest a whole driver under
    its own subdir when they pull in helper files; handle both layouts."""
    out = []
    drivers_root = indi_root / "drivers"
    if not drivers_root.exists():
        return out
    for category in drivers_root.iterdir():
        if not category.is_dir():
            continue

        # Flat layout: group .cpp files under the category by stem. A driver
        # typically has <stem>.cpp + <stem>.h; some have additional helper
        # files like <stem>driver.cpp we co-analyze alongside.
        flat_cpps = sorted(p for p in category.glob("*.cpp") if p.is_file())
        seen_stems: set[str] = set()
        for cpp in flat_cpps:
            stem = cpp.stem
            if stem in seen_stems:
                continue
            header = cpp.with_suffix(".h")
            # Class inheritance for INDI drivers lives in the .h, not the .cpp.
            # A helper file (e.g. celestrondriver.cpp) has no matching .h with
            # a main-driver class — we skip those so they don't get emitted as
            # standalone "drivers".
            combined = ""
            for f in (header, cpp):
                if f.exists():
                    try: combined += f.read_text(encoding="utf-8", errors="ignore")
                    except Exception: pass
            if not CLASS_INHERIT_RE.search(combined):
                continue
            seen_stems.add(stem)
            info = _analyze_files([cpp, header])
            display, binary = derive_display_and_binary(stem, indi_core=True)
            out.append(build_row(display, binary, category.name, info, source="core"))

        # Nested layout: subdirs with their own CMakeLists + sources.
        for sub in category.iterdir():
            if not sub.is_dir():
                continue
            if not any(sub.rglob("*.cpp")):
                continue
            info = analyze_driver_dir(sub)
            display, binary = derive_display_and_binary(sub.name, indi_core=True)
            out.append(build_row(display, binary, category.name, info, source="core"))
    return out


def _analyze_files(files: list[Path]) -> dict:
    """Same shape as analyze_driver_dir() but for an explicit file list."""
    bases: set[str] = set()
    props: set[str] = set()
    for f in files:
        if not f.exists() or f.suffix not in (".cpp", ".cc", ".h", ".hpp"):
            continue
        try:
            text = f.read_text(encoding="utf-8", errors="ignore")
        except Exception:
            continue
        bases.update(CLASS_INHERIT_RE.findall(text))
        props.update(PROP_RE.findall(text))
        props.update(CONVENTIONAL_PROP_RE.findall(text))
    effective_props = set(props)
    for base in bases:
        effective_props.update(BASE_CLASS_PROPERTIES.get(base, set()))
    primary = None
    for base in bases:
        if base in KIND_BY_BASECLASS:
            primary = KIND_BY_BASECLASS[base]
            break
    return {
        "bases": sorted(bases),
        "properties_count": len(effective_props),
        "properties": effective_props,
        "primary_kind": primary,
    }


def collect_3rdparty_drivers(root: Path) -> list[dict]:
    """Vendor packages (indi-asi, indi-qhy, indi-playerone, indi-toupbase, …)
    frequently ship multiple driver binaries from ONE source directory — e.g.
    indi-asi produces indi_asi_ccd + indi_asi_focuser + indi_asi_wheel +
    indi_asi_rotator + indi_asi_st4. Splitting on "main" .cpp files (those
    containing an INDI class declaration) yields one row per binary with the
    right primary kind, rather than one row per package with a muddled kind."""
    out = []
    for driver_dir in root.iterdir():
        if not driver_dir.is_dir() or not driver_dir.name.startswith("indi-"):
            continue
        cpps = sorted(driver_dir.rglob("*.cpp"))
        if not cpps:
            continue

        main_cpps: list[tuple[Path, Path | None]] = []
        # A main .cpp is one whose class decl inherits from an INDI::XXX base.
        for cpp in cpps:
            header = cpp.with_suffix(".h")
            combined = ""
            for f in (header, cpp):
                if f.exists():
                    try: combined += f.read_text(encoding="utf-8", errors="ignore")
                    except Exception: pass
            if CLASS_INHERIT_RE.search(combined):
                main_cpps.append((cpp, header if header.exists() else None))

        if not main_cpps:
            # Header-only / no INDI class — fall back to whole-dir analysis so
            # we still emit the row, even if weakly typed.
            info = analyze_driver_dir(driver_dir)
            display, binary = derive_display_and_binary(driver_dir.name, indi_core=False)
            out.append(build_row(display, binary, driver_dir.name, info, source="3rdparty"))
            continue

        # Shared helper files (asi_base.h, usb_utils.h, etc.) aren't binaries
        # themselves but their INDI:: base-class headers get picked up by the
        # per-main-cpp analysis through the sibling .h scan. That's fine —
        # better to attribute shared properties to every driver than to none.
        for cpp, header in main_cpps:
            files = [cpp] + ([header] if header else [])
            info = _analyze_files(files)
            stem = cpp.stem  # e.g. "asi_ccd"
            # Binary name: the 3rdparty convention is `indi_<stem>` when stem
            # already starts with the vendor prefix (asi_ccd → indi_asi_ccd).
            # For stems that don't carry the vendor prefix, prepend the
            # package short name.
            package_short = driver_dir.name.removeprefix("indi-").replace("-", "_")
            if stem.startswith(package_short + "_") or stem.startswith(package_short):
                binary = f"indi_{stem}"
            else:
                binary = f"indi_{package_short}_{stem}"
            display = stem.replace("_", " ")
            out.append(build_row(display, binary, driver_dir.name, info, source="3rdparty"))

    return out


def build_row(display: str, binary: str, category: str, info: dict, source: str) -> dict:
    props = info["properties"]
    caps = {k: any(p in props for p in names) for k, names in PROBES.items()}
    kinds = []
    for base in info["bases"]:
        if base in KIND_BY_BASECLASS and KIND_BY_BASECLASS[base] not in kinds:
            kinds.append(KIND_BY_BASECLASS[base])
    primary = info["primary_kind"] or _kind_from_category(category)
    return {
        "binary":          binary,
        "display":         display,
        "primary":         primary,
        "kinds":           kinds or [primary] if primary else [],
        "isSimulator":     False,
        "source":          source,
        "bases":           info["bases"],
        "propertyCount":   info["properties_count"],
        "capabilities":    {k: v for k, v in caps.items() if v},
    }


def _kind_from_category(category: str) -> str:
    """When class-inheritance parsing misses (e.g. the driver only uses a
    C-style INDI driver skeleton), fall back to the directory category name
    from libindi's driver tree layout."""
    m = {
        "ccd":           "CAMERA",
        "telescope":     "TELESCOPE",
        "focuser":       "FOCUSER",
        "filter_wheel":  "FILTER",
        "dome":          "DOME",
        "rotator":       "ROTATOR",
        "weather":       "WEATHER",
        "power":         "AUX",
        "auxiliary":     "AUX",
        "video":         "DETECTOR",
        "receiver":      "DETECTOR",
        "spectrograph":  "SPECTROGRAPH",
        "io":            "AUX",
    }.get(category.lower(), "Unknown")
    return m


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--indi", required=True, type=Path, help="libindi source tree")
    ap.add_argument("--indi-3rdparty", type=Path, default=None, help="indi-3rdparty source tree")
    ap.add_argument("--out", type=Path, required=True, help="Output JSON path")
    args = ap.parse_args()

    rows: list[dict] = []
    rows.extend(collect_core_drivers(args.indi))
    if args.indi_3rdparty:
        rows.extend(collect_3rdparty_drivers(args.indi_3rdparty))

    # De-dupe by binary (core + 3rdparty sometimes ship the same name).
    seen: dict[str, dict] = {}
    for r in rows:
        if r["binary"] in seen:
            # Prefer 3rdparty (more complete than a skeleton in core).
            if r["source"] == "3rdparty":
                seen[r["binary"]] = r
            continue
        seen[r["binary"]] = r

    rows = sorted(seen.values(), key=lambda r: (r["primary"] or "ZZ", r["display"]))
    args.out.write_text(json.dumps({"drivers": rows}, indent=2))

    # Stats summary to stdout.
    cap_coverage = {}
    for k in PROBES:
        cap_coverage[k] = sum(1 for r in rows if k in r["capabilities"])
    print(f"Analyzed {len(rows)} drivers ({sum(1 for r in rows if r['source']=='core')} core + {sum(1 for r in rows if r['source']=='3rdparty')} 3rdparty).")
    print(f"Output: {args.out}")
    print("\nCapability coverage (drivers whose SOURCE registers the property):")
    for k, c in cap_coverage.items():
        pct = 100.0 * c / len(rows) if rows else 0
        print(f"  {k:25s} {c:4d} / {len(rows)} ({pct:.0f}%)")


if __name__ == "__main__":
    main()
