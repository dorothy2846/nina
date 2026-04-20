#!/usr/bin/env python3
"""
Merge source-analyzed and runtime-verified driver catalogs into one bundle for
the iOS app. Source analysis covers every INDI driver in the libindi +
indi-3rdparty repos (including vendors not installable on macOS like ZWO ASI
and Player One). Runtime fixtures prove each driver binary actually launches
on the bundled libindi build.

Union strategy:
  - Primary: source catalog (widest coverage, capability accuracy)
  - Overlay: mark drivers also present in runtime fixtures with installed=True
  - Runtime-only drivers (those the source analyzer missed — e.g. INDI
    simulators, or drivers we couldn't map from directory → binary name) are
    appended so we don't lose them.
"""

import argparse
import json
from pathlib import Path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", type=Path, required=True)
    ap.add_argument("--runtime", type=Path, required=True)
    ap.add_argument("--out", type=Path, required=True)
    args = ap.parse_args()

    source = json.loads(args.source.read_text())["drivers"]
    runtime = json.loads(args.runtime.read_text())["drivers"]

    by_binary: dict[str, dict] = {}
    for row in source:
        row["installed"] = False
        row["verifiedAtRuntime"] = False
        by_binary[row["binary"]] = row

    for r in runtime:
        existing = by_binary.get(r["binary"])
        if existing:
            existing["installed"] = True
            existing["verifiedAtRuntime"] = True
            # Runtime might catch caps the static analyzer missed (e.g. when
            # a driver uses a macro for the property name). Union both sides.
            existing_caps = existing.get("capabilities", {})
            for k, v in r.get("capabilities", {}).items():
                if v:
                    existing_caps[k] = True
            existing["capabilities"] = existing_caps
        else:
            # Runtime-only (usually simulators or drivers whose dir name didn't
            # match our binary-guess heuristic). Carry them through unchanged.
            r["installed"] = True
            r["verifiedAtRuntime"] = True
            r["source"] = r.get("source", "runtime-only")
            by_binary[r["binary"]] = r

    drivers = sorted(by_binary.values(), key=lambda d: (d.get("primary") or "ZZ", d["display"]))
    args.out.write_text(json.dumps({"drivers": drivers}, indent=2))

    total = len(drivers)
    installed = sum(1 for d in drivers if d.get("installed"))
    sim = sum(1 for d in drivers if d.get("isSimulator"))
    verified_cap = sum(1 for d in drivers if d.get("capabilities"))
    print(f"Merged catalog: {total} drivers")
    print(f"  installed on this host: {installed}")
    print(f"  simulator drivers:      {sim}")
    print(f"  with verified caps:     {verified_cap}")
    print(f"Output: {args.out}")


if __name__ == "__main__":
    main()
