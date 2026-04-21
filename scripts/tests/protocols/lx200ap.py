"""
Astro-Physics GTO protocol emulator. AP mounts speak LX200 plus vendor
extensions (status strings, pier side polls, "park off" unpark command).
The INDI driver's connect-time sequence:

  1. `#:GG#` — UTC offset, expects a '#'-terminated response
  2. `#:Gev#` — firmware version like "V1.23.4#"
  3. `#:GOS#` — status bitmap (1-char codes + '#')
  4. `#:KA#` — keepalive, periodic

Subclasses LX200Emulator to inherit standard LX200 command handling
(EQUATORIAL_EOD_COORD, slew, park, etc.) and adds AP-specific overrides.
"""

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from protocols.lx200 import LX200Emulator


class LX200APEmulator(LX200Emulator):
    name = "lx200ap"

    def _dispatch(self, cmd: bytes) -> bytes | None:
        # AP-specific first — so we override LX200 defaults where AP's
        # response format differs.
        # Driver does strstr(resp, "VCP4") etc. — needs a recognised token.
        if cmd == b"V":   return b"VCP4-P01-01#"
        if cmd == b"Gev": return b"VCP4-P01-01#"
        if cmd == b"GOS": return b"PNKQY#"          # status: Parked, Not-tracking, Known, North-polar-aligned
        if cmd == b"pS":  return b"East#"           # pier side
        if cmd == b"pR":  return b"+00*00:00#"      # park RA axis position
        if cmd == b"pD":  return b"+00*00:00#"      # park DEC axis position
        if cmd == b"PO":  return None               # park off (unpark) — silent
        if cmd == b"KA":  return None               # keepalive — silent
        if cmd == b"NS":  return b"1#"              # northern hemisphere
        if cmd == b"EW":  return b"0#"              # eastern longitude
        # AP uses ':GG#' as '+HH:MM.T#' not the '+HH.H#' LX200Basic uses.
        if cmd == b"GG":  return b"+00:00.0#"
        # Rate set commands (:RC0..:RS2, :RT0..:RT9) — silent ack.
        if cmd.startswith((b"RC", b"RS", b"RT", b"RG", b"RR", b"RD")):
            return None
        # Backlash + other set commands (:Br, :Bd, :BS etc.) — silent ack.
        if cmd.startswith((b"Br", b"Bd", b"BS")):
            return None
        # :Mexyz / :Mnxyz / :Msxyz / :Mwxyz — AP uses 3-digit suffix move commands
        if len(cmd) == 4 and cmd[:1] in (b"M",) and cmd[1:2] in (b"e", b"n", b"s", b"w"):
            return None
        # :Gp# — get polar alignment offset
        if cmd == b"Gp": return b"+00*00:00#"
        # Fall through to standard LX200 handling for :GR, :GD, :MS etc.
        return super()._dispatch(cmd)
