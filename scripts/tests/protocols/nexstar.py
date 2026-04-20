"""
Celestron NexStar protocol emulator. Covers every NexStar hand-controller
flavour (NexStar+, Evolution, CGE/CGEM, AVX, CPC, AstroFi) plus the
"NexStar AUX" direct-board protocol used by newer mounts.

Command set (subset — the slice drivers exercise during init + basic ops):
  'V'           → firmware version   returns 2 bytes + '#'
  'P'           → pass-through         auto-responses
  'E' / 'e'     → get RA/Dec (16 / 32-bit)
  'Z' / 'z'     → get Az/Alt (16 / 32-bit)
  'R'/'S'/'r'/'s' → goto/sync commands
  'M'           → cancel goto
  'K<ch>'       → echo one byte (connectivity check — driver sends 'Kx'
                  expects 'x' back)
  't'           → get tracking mode   1 byte + '#'
  'T<mode>'     → set tracking mode
  'J'           → get alignment state 1 byte + '#'
  'm'           → get model number    1 byte + '#'
  'w'           → get location        8 bytes + '#'
  'h'           → get time            8 bytes + '#'
"""

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from protocol_emulator import ProtocolEmulator


class NexStarEmulator(ProtocolEmulator):
    name = "nexstar"

    def __init__(self) -> None:
        self.ra_deg = 37.95      # Polaris RA
        self.dec_deg = 89.26
        self.az_deg = 180.0
        self.alt_deg = 45.0
        self.tracking_mode = 2    # 2 = EQ North
        self.alignment_done = 1   # 1 = aligned
        self.model = 10           # CGE Pro
        self._buf = b""

    def handle(self, data: bytes) -> bytes:
        self._buf += data
        out = b""
        # NexStar is single-character-command-led; many are fixed-length.
        while self._buf:
            cmd = self._buf[0:1]
            if cmd == b"K":
                # Echo next byte back. Variable length = 2 bytes (K + char).
                if len(self._buf) < 2:
                    break
                ch = self._buf[1:2]
                self._buf = self._buf[2:]
                out += ch + b"#"
            elif cmd == b"V":
                self._buf = self._buf[1:]
                out += b"\x04\x22#"  # version 4.34
            elif cmd == b"m":
                self._buf = self._buf[1:]
                out += bytes([self.model]) + b"#"
            elif cmd == b"t":
                self._buf = self._buf[1:]
                out += bytes([self.tracking_mode]) + b"#"
            elif cmd == b"T":
                # set tracking: 'T' + 1 byte
                if len(self._buf) < 2:
                    break
                self.tracking_mode = self._buf[1]
                self._buf = self._buf[2:]
                out += b"#"
            elif cmd == b"J":
                self._buf = self._buf[1:]
                out += bytes([self.alignment_done]) + b"#"
            elif cmd == b"E":
                self._buf = self._buf[1:]
                ra16 = int(self.ra_deg / 360.0 * 65536) & 0xFFFF
                de16 = int(self.dec_deg / 360.0 * 65536) & 0xFFFF
                out += f"{ra16:04X},{de16:04X}#".encode()
            elif cmd == b"e":
                self._buf = self._buf[1:]
                ra32 = int(self.ra_deg / 360.0 * (1 << 32)) & 0xFFFFFFFF
                de32 = int(self.dec_deg / 360.0 * (1 << 32)) & 0xFFFFFFFF
                out += f"{ra32:08X},{de32:08X}#".encode()
            elif cmd == b"Z":
                self._buf = self._buf[1:]
                az16 = int(self.az_deg / 360.0 * 65536) & 0xFFFF
                al16 = int(self.alt_deg / 360.0 * 65536) & 0xFFFF
                out += f"{az16:04X},{al16:04X}#".encode()
            elif cmd == b"z":
                self._buf = self._buf[1:]
                az32 = int(self.az_deg / 360.0 * (1 << 32)) & 0xFFFFFFFF
                al32 = int(self.alt_deg / 360.0 * (1 << 32)) & 0xFFFFFFFF
                out += f"{az32:08X},{al32:08X}#".encode()
            elif cmd in (b"R", b"S"):
                # Goto RA/Dec — 'R' + "HHHH,DDDD" (16-bit) or 'r' (32-bit).
                # Fixed len: 10 bytes for 16-bit, 18 for 32-bit.
                if len(self._buf) < 10:
                    break
                self._buf = self._buf[10:]
                out += b"#"
            elif cmd in (b"r", b"s"):
                if len(self._buf) < 18:
                    break
                self._buf = self._buf[18:]
                out += b"#"
            elif cmd == b"M":
                self._buf = self._buf[1:]
                out += b"#"
            elif cmd == b"L":
                # Is-slewing? returns '0#' if not slewing
                self._buf = self._buf[1:]
                out += b"0#"
            elif cmd == b"w":
                # location 8 bytes + '#'
                self._buf = self._buf[1:]
                out += bytes([37, 30, 0, 0, 127, 0, 0, 0]) + b"#"
            elif cmd == b"h":
                # time 8 bytes + '#'
                self._buf = self._buf[1:]
                out += bytes([12, 0, 0, 4, 20, 26, 0, 0]) + b"#"
            elif cmd == b"P":
                # pass-through — variable length. Min header is 8 bytes (P +
                # 7 subcommand bytes); reply is (msg_size) bytes + '#'.
                if len(self._buf) < 8:
                    break
                # For our purposes ack silently with '#' after consuming 8.
                resp_len = self._buf[1]  # ResponseBytes count
                self._buf = self._buf[8:]
                out += b"\x00" * max(0, resp_len) + b"#"
            else:
                # Unknown: consume 1 byte, silent
                self._buf = self._buf[1:]
        return out
