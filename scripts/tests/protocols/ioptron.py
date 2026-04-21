"""
iOptron protocol emulator. Covers iEQ / iEQ30 / iEQ45 / CEM / GEM / ZEQ25 /
GotoNova mounts — iOptron's command set is text over serial but distinct
from Meade's LX200. Identifier + version queries (MountInfo, FW1, FW2)
must respond correctly or the INDI driver bails before publishing
capabilities.

Spec: indi_ioptronv3_telescope + indi_ieq_telescope sources in libindi.
"""

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from protocol_emulator import ProtocolEmulator


class IOptronEmulator(ProtocolEmulator):
    """iEQ-style mount. We answer the identifier + status + position
    queries the driver runs during init, plus the motion commands iOS
    would trigger later."""

    name = "ioptron"

    def __init__(self) -> None:
        # MountInfo code "0026" = iEQ45 Pro GoTo — a widely-supported model
        # the driver will happily fall through to standard handling for.
        self.mount_info = b"0026"
        self.ra_ms = int(2.53 * 15 * 3600 * 1000)  # msec in 15°-granular RA
        self.dec_ms = int(89.26 * 3600 * 1000)
        self._buf = b""

    def handle(self, data: bytes) -> bytes:
        self._buf += data
        out = b""
        while self._buf:
            if self._buf[:1] != b":":
                self._buf = self._buf[1:]
                continue
            end = self._buf.find(b"#")
            if end < 0:
                break
            cmd = self._buf[1:end]
            self._buf = self._buf[end + 1:]
            resp = self._dispatch(cmd)
            if resp is not None:
                out += resp
        return out

    def _dispatch(self, cmd: bytes) -> bytes | None:
        # Identifier/version queries — these must succeed for driver init
        if cmd == b"MountInfo":     return self.mount_info
        if cmd == b"FW1":           return b"160610160610"      # FW date MMDDYY × 2
        if cmd == b"FW2":           return b"160610160610"
        if cmd == b"V":             return b"V1.00#"

        # Position
        if cmd == b"GEP":
            # :GEP# → DEC(9)+RA(9)+PierSide(1)+Counter(1)+# — standard iEQ response
            return f"{self.dec_ms:+010d}{self.ra_ms:09d}10#".encode()
        if cmd == b"GR":            return _ra_hms(self.ra_ms) + b"#"
        if cmd == b"GD":            return _dec_dms(self.dec_ms) + b"#"
        if cmd == b"GGS":           return b"+00:00:00#"
        if cmd == b"GLS":           return b"+3700127000#"     # lat/long
        if cmd == b"GMT":           return b"+0003601#"        # UTC offset
        if cmd == b"GUT":           return b"04202600:00:00#"  # date + time
        if cmd == b"GPE":           return b"00#"               # position error
        if cmd == b"GPR":           return b"00#"

        # Set-target / slew / sync — all accepted silently with '1' ACK
        if cmd.startswith(b"Sr"):   return b"1"
        if cmd.startswith(b"Sd"):   return b"1"
        if cmd.startswith(b"Sg"):   return b"1"
        if cmd.startswith(b"St"):   return b"1"
        if cmd.startswith(b"SG"):   return b"1"
        if cmd.startswith(b"SDS"):  return b"1"
        if cmd.startswith(b"SLA") or cmd.startswith(b"SLO"):
            return b"1"
        if cmd == b"MS1" or cmd == b"MS2" or cmd == b"MS":
            return b"1"
        if cmd == b"MSH":           return b"1"              # home
        if cmd == b"CM":            return b" M31 EX GAL#"
        if cmd == b"CMR":           return b"Coordinates matched.#"

        # Motion / parking
        if cmd == b"MH":            return b"1"              # home
        if cmd == b"MP0" or cmd == b"MP1":
            return b"1"
        if cmd == b"q":             return None              # abort silent
        if cmd.startswith(b"AG"):   return None
        if cmd.startswith(b"RR") or cmd.startswith(b"RT") or cmd.startswith(b"RG"):
            return None
        if cmd == b"pS":            return b"0"

        # Fallback silent for unknown commands
        return None


def _ra_hms(ms: int) -> bytes:
    sec = ms // 1000
    h = sec // 3600 % 24
    m = (sec // 60) % 60
    s = sec % 60
    return f"{h:02d}:{m:02d}:{s:02d}".encode()


def _dec_dms(ms: int) -> bytes:
    sign = "+" if ms >= 0 else "-"
    ms = abs(ms)
    sec = ms // 1000
    d = sec // 3600
    m = (sec // 60) % 60
    s = sec % 60
    return f"{sign}{d:02d}*{m:02d}:{s:02d}".encode()
