"""
Debug-mode emulator that logs every inbound byte sequence from the driver
and responds with a best-effort generic ACK ('#' for framed protocols,
'\\x06' ACK for byte protocols). Purpose: run this against a failing
driver, inspect the captured command log, and use what the driver actually
sends to implement a real emulator for that protocol family.
"""

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from protocol_emulator import ProtocolEmulator


class DebugLoggerEmulator(ProtocolEmulator):
    name = "debug"

    def __init__(self, log_path: Path | None = None):
        self._buf = b""
        self.log: list[bytes] = []
        self.log_path = log_path

    def handle(self, data: bytes) -> bytes:
        self.log.append(data)
        if self.log_path:
            self.log_path.write_bytes(b"\n".join(self.log))
        self._buf += data
        out = b""
        # Try to parse as ':...#' framed protocol (most common)
        while b":" in self._buf and b"#" in self._buf:
            i = self._buf.find(b":")
            self._buf = self._buf[i:]
            end = self._buf.find(b"#")
            if end < 0: break
            cmd = self._buf[1:end]
            self._buf = self._buf[end + 1:]
            # Dumb-but-safe: positive ACK '1' for set commands, '0' for queries
            if cmd.startswith((b"S", b"M", b"Q", b"T", b"R", b"G", b"h", b"p")):
                out += b"1"
            else:
                out += b"#"
        return out
