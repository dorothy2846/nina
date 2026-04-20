"""
Moonlite focuser protocol emulator. Moonlite's text protocol is the
lingua franca for cheap stepper focusers — Moonlite itself, nSTEP,
myFocuserPro2, Lakeside, RobotFocus and many DIY builds all speak a
subset of it.

Commands (all framed as ':XX#' or ':XXyyyy#'):
  :GP#   → get current position (HEX, 4 chars + '#')
  :GN#   → get new/target position
  :GT#   → get temperature (HEX, 4 chars)
  :GV#   → get firmware version
  :GH#   → half-step mode? (00# / FF#)
  :GI#   → is-moving? (00# = idle, 01# = moving)
  :GC#   → get temp-comp coefficient
  :SPxxxx# → set current position (no response)
  :SNxxxx# → set new/target position
  :FG#   → go to target position
  :FQ#   → halt
  :+/-/C/D/Y/Z/+  → various movement + config commands (driver-specific,
                    ack silently)
"""

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from protocol_emulator import ProtocolEmulator


class MoonliteEmulator(ProtocolEmulator):
    name = "moonlite"

    def __init__(self) -> None:
        self.position = 0x4000       # mid-travel
        self.target = self.position
        self.temperature_raw = 0x0100  # ~16°C in Moonlite's 1/2 C units
        self.moving = False
        self._buf = b""

    def handle(self, data: bytes) -> bytes:
        self._buf += data
        out = b""
        while True:
            if not self._buf.startswith(b":"):
                # drop until next ':'
                i = self._buf.find(b":")
                if i < 0:
                    self._buf = b""
                    break
                self._buf = self._buf[i:]
            end = self._buf.find(b"#")
            if end < 0:
                break
            cmd = self._buf[1:end]  # strip ':' + '#'
            self._buf = self._buf[end + 1:]
            resp = self._dispatch(cmd)
            if resp is not None:
                out += resp
        return out

    def _dispatch(self, cmd: bytes) -> bytes | None:
        if cmd == b"GP": return f"{self.position:04X}#".encode()
        if cmd == b"GN": return f"{self.target:04X}#".encode()
        if cmd == b"GT": return f"{self.temperature_raw:04X}#".encode()
        if cmd == b"GV": return b"10#"  # firmware 1.0
        if cmd == b"GH": return b"00#"
        if cmd == b"GI": return (b"01#" if self.moving else b"00#")
        if cmd == b"GC": return b"02#"
        if cmd == b"GD": return b"02#"  # motor speed
        if cmd == b"GB": return b"00#"  # backlash
        if cmd == b"FG":
            # go to target — instant-complete
            self.position = self.target
            return None
        if cmd == b"FQ":
            self.moving = False
            return None
        if cmd.startswith(b"SP"):
            try: self.position = int(cmd[2:], 16)
            except ValueError: pass
            return None
        if cmd.startswith(b"SN"):
            try: self.target = int(cmd[2:], 16)
            except ValueError: pass
            return None
        if cmd.startswith(b"SF") or cmd.startswith(b"SD") or cmd.startswith(b"SC"):
            return None  # config ack silent
        # Unknown command — ack silently to avoid confusing the driver.
        return None
