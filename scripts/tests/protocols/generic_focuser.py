"""
Generic focuser protocol catch-all. Many INDI focuser drivers share a
small set of idioms — `:GD#` / `:GP#` hex-value queries (Microtouch,
Moonlite-lite variants), `V#` / `C#` / `Q#` single-letter (AAF2, rbFocus,
SmartFocus), `:TI`/`:RT`/`:RO`/`:RF`/`:RA`/`:RP` nSTEP family (nSTEP,
nFocus). Rather than write a bespoke emulator per driver, this class
recognises each common idiom and emits a safe default response so the
driver's init handshake completes and post-connect properties publish.

Per-driver precision (correct slew targets, homing, etc.) isn't the
goal — capability-publish verification is. The driver needs *some*
response to proceed past CONNECT; the exact payload only matters for
features beyond what we probe.
"""

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from protocol_emulator import ProtocolEmulator


class GenericFocuserEmulator(ProtocolEmulator):
    name = "generic_focuser"

    def __init__(self) -> None:
        self._buf = b""
        self.position = 0x4000

    def handle(self, data: bytes) -> bytes:
        self._buf += data
        out = b""
        # Two parse modes: ':...#' framed commands, and bare single-letter
        # commands like 'V#'/'C#'/'Q#' without leading colon.
        progress = True
        while progress:
            progress = False
            # Mode 1: ':xxx#'
            if b":" in self._buf and b"#" in self._buf:
                ci = self._buf.find(b":")
                hi = self._buf.find(b"#", ci)
                if hi > ci:
                    # Also skip any bare 'x#' before the ':'.
                    pre = self._buf[:ci]
                    for b in pre:
                        out += self._bare(bytes([b]))
                    cmd = self._buf[ci + 1:hi]
                    self._buf = self._buf[hi + 1:]
                    resp = self._framed(cmd)
                    if resp is not None:
                        out += resp
                    progress = True
                    continue
            # Mode 2: bare 'X#' byte-pair (no leading ':')
            if b"#" in self._buf and b":" not in self._buf:
                hi = self._buf.find(b"#")
                if hi == 0:
                    self._buf = self._buf[1:]
                    progress = True
                    continue
                if hi > 0:
                    cmd = self._buf[:hi]
                    self._buf = self._buf[hi + 1:]
                    out += self._bare(cmd)
                    progress = True
                    continue
        return out

    def _framed(self, cmd: bytes) -> bytes | None:
        """':cmd#' framed commands."""
        # Hex position/target queries — Moonlite / Microtouch / Lakeside
        if cmd == b"GP" or cmd == b"GN":
            return f"{self.position:04X}#".encode()
        if cmd == b"GT":
            return b"0100#"          # temperature ×½ °C (Moonlite format)
        if cmd == b"GD":
            # Microtouch speed query: returns `%hX#` (hex byte)
            return b"02#"
        if cmd == b"GV":
            return b"10#"            # firmware 1.0
        if cmd == b"GH" or cmd == b"GI" or cmd == b"GC" or cmd == b"GB":
            return b"00#"
        # nSTEP / nFocus family — rate/period queries
        if cmd.startswith(b"RA") or cmd.startswith(b"RB") or cmd.startswith(b"RC") \
                or cmd.startswith(b"RE") or cmd.startswith(b"RG") or cmd.startswith(b"RH") \
                or cmd.startswith(b"RO") or cmd.startswith(b"RP") or cmd.startswith(b"RS") \
                or cmd.startswith(b"RT") or cmd.startswith(b"RW") or cmd.startswith(b"RF"):
            # 3-digit numeric response like "050#"
            return b"050#"
        if cmd == b"TI":
            return b"+25.0#"          # temperature string
        if cmd == b"IP":
            # onfocus firmware/info
            return b"Focuser OK#"
        # Set commands — silent
        if cmd.startswith((b"SP", b"SN", b"SF", b"SD", b"SC", b"SO")):
            return None
        # :F<5digit># — nSTEP move
        if cmd.startswith(b"F"):
            return None
        if cmd == b"FG" or cmd == b"FQ":
            return None
        return None

    def _bare(self, cmd: bytes) -> bytes:
        """Single-letter or bareword commands outside ':..#' framing.
        Emits short text responses ending in '#' which is what most
        C#/V#/Q#-style focusers expect."""
        if cmd == b"":      return b""
        if cmd == b"V":     return b"V1.0#"
        if cmd == b"Q":     return b"000000#"     # idle / position zero
        if cmd == b"C":     return b"#"
        return b"#"
