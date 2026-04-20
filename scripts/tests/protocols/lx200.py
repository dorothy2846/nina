"""
LX200 protocol emulator. LX200 is the Meade-originated text command set
used (with minor variations) by every cheap-to-moderate goto mount ever
made: LX200 classic/GPS, Autostar II, Celestron NexStar (mostly-compat),
Losmandy Gemini, AstroPhysics, iOptron, SkyWatcher AllStar, ZWO AM5
(over TCP), Pegasus NYX-101, OnStep, OpenAstroTech, Avalon, Paramount
(one mode), …

Spec references:
  * http://www.meade.com/support/LX200CommandSet.pdf (Meade's own)
  * INDI's libindi/drivers/telescope/lx200driver.h — exact command list
    INDI drivers issue
  * OnStep wiki — modern additions (extensions beyond classic Meade)

Commands are text, '#'-terminated except for one-character set-mode
commands. Coordinate format differs between "long" and "short" mode
(high-precision vs low-precision); we implement long which every modern
driver requests on connect with `:U#`.

State kept:
  RA (hours), Dec (degrees), target RA/Dec, tracking state, park state,
  site lat/long, current date/time.
"""

from __future__ import annotations

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from protocol_emulator import ProtocolEmulator


def _fmt_ra(hours: float) -> str:
    """HH:MM:SS.SS format — high-precision long mode."""
    hours = hours % 24
    h = int(hours)
    m_full = (hours - h) * 60
    m = int(m_full)
    s = (m_full - m) * 60
    return f"{h:02d}:{m:02d}:{s:05.2f}"


def _fmt_dec(deg: float) -> str:
    """sDD*MM:SS format."""
    sign = "+" if deg >= 0 else "-"
    deg = abs(deg)
    d = int(deg)
    m_full = (deg - d) * 60
    m = int(m_full)
    s = int((m_full - m) * 60)
    return f"{sign}{d:02d}*{m:02d}:{s:02d}"


def _fmt_lat(deg: float) -> str:
    """sDD*MM# latitude format."""
    sign = "+" if deg >= 0 else "-"
    deg = abs(deg)
    d = int(deg)
    m = int((deg - d) * 60)
    return f"{sign}{d:02d}*{m:02d}"


class LX200Emulator(ProtocolEmulator):
    """Minimal viable LX200-compatible mount. Responds to the command set
    that initProperties() on INDI::Telescope + lx200 subclasses exercises
    during connect, plus the slew/park/sync operations the driver runs
    when the user clicks a button in iOS."""

    name = "lx200"

    def __init__(self) -> None:
        # Default target: Polaris-ish so the driver reports sensible coords.
        self.ra_hours = 2.53
        self.dec_deg = 89.26
        self.target_ra = 0.0
        self.target_dec = 0.0
        self.tracking = True
        self.slewing = False
        self.parked = False
        self.site_lat = 37.5  # Seoul-ish — just non-zero default.
        self.site_long = 127.0
        # Line buffer — driver sometimes writes a multi-byte command in
        # several recv chunks.
        self._buf = b""

    def handle(self, data: bytes) -> bytes:
        self._buf += data
        out = b""
        # LX200 commands are either single-char (very old) or ':...#' framed.
        while self._buf:
            if self._buf[:1] == b"\x06":
                # ACK — driver asking "are you there?". Return 'A' (Alt-Az),
                # 'P' (Polar aligned), or 'L' (Land). 'P' keeps the driver
                # happy without triggering alignment prompts.
                out += b"P"
                self._buf = self._buf[1:]
                continue
            if self._buf[:1] != b":":
                # Skip garbage byte (shouldn't happen but be robust).
                self._buf = self._buf[1:]
                continue
            end = self._buf.find(b"#")
            if end == -1:
                break  # wait for more bytes
            cmd = self._buf[1:end]  # strip ':' prefix + '#' terminator
            self._buf = self._buf[end + 1:]
            resp = self._dispatch(cmd)
            if resp is not None:
                out += resp
        return out

    def _dispatch(self, cmd: bytes) -> bytes | None:
        # Queries — no trailing ACK, just response.
        if cmd == b"GR": return (_fmt_ra(self.ra_hours) + "#").encode()
        if cmd == b"GD": return (_fmt_dec(self.dec_deg) + "#").encode()
        if cmd == b"Gr": return (_fmt_ra(self.target_ra) + "#").encode()
        if cmd == b"Gd": return (_fmt_dec(self.target_dec) + "#").encode()
        if cmd == b"Gt": return (_fmt_lat(self.site_lat) + "#").encode()
        if cmd == b"Gg":
            # Longitude — INDI convention is 0–360 going west, drivers often
            # expect west-positive. We return our stored value directly.
            return (_fmt_lat(-self.site_long) + "#").encode()
        if cmd == b"GVP": return b"astellar-emu#"              # product
        if cmd == b"GVN": return b"1.0#"                        # firmware
        if cmd == b"GVT": return b"01:02:03#"                   # firmware time
        if cmd == b"GVD": return b"Apr 20 2026#"                # firmware date
        if cmd == b"GVF": return b"astellar-emu Apr 20 2026 01:02:03#"
        if cmd == b"GW":
            # alignment-status string: AT2 = alt/az, high, 2-star done (safe default)
            return b"AT2#"
        if cmd == b"GG": return b"+00.0#"                       # UTC offset
        if cmd == b"GL": return b"12:00:00#"                    # local time
        if cmd == b"GS": return b"12:00:00#"                    # sidereal
        if cmd == b"GC": return b"04/20/26#"                    # date
        if cmd == b"GU":
            # Status mask — N bit set = tracking, bit depends on mount.
            # Empty string '#' is "all default" for drivers that only check
            # specific bits.
            flags = "N" if self.tracking else "n"
            if self.parked: flags += "P"
            return (flags + "#").encode()

        # Slew rate queries (0=guide, 1=center, 2=find, 3=max typically)
        if cmd == b"RG" or cmd == b"RC" or cmd == b"RM" or cmd == b"RS":
            return None  # no response, just acknowledged internally

        # Movement / motion
        if cmd == b"Q":
            self.slewing = False
            return None
        if cmd in (b"Mn", b"Ms", b"Me", b"Mw"):
            return None  # start-motion — no response
        if cmd in (b"Qn", b"Qs", b"Qe", b"Qw"):
            return None  # stop-motion — no response

        # Set target coords. Format: :Sr HH:MM:SS# or :Sd sDD*MM:SS#
        if cmd.startswith(b"Sr"):
            self.target_ra = _parse_hms(cmd[2:].lstrip().decode())
            return b"1"  # 1 = valid
        if cmd.startswith(b"Sd"):
            self.target_dec = _parse_dms(cmd[2:].lstrip().decode())
            return b"1"
        if cmd.startswith(b"St"):
            # set latitude
            self.site_lat = _parse_dms(cmd[2:].lstrip().decode())
            return b"1"
        if cmd.startswith(b"Sg"):
            # set longitude
            self.site_long = -_parse_dms(cmd[2:].lstrip().decode())
            return b"1"
        if cmd.startswith((b"SC", b"SL", b"SG")):
            # set date / local time / UTC offset — accept them
            return b"1Updating Planetary Data                                 #"

        # Slew start
        if cmd == b"MS":
            self.ra_hours = self.target_ra
            self.dec_deg = self.target_dec
            self.slewing = False  # instant-slew in our emulator
            return b"0"  # 0 = slew started OK
        if cmd == b"CM":
            # Sync to target
            self.ra_hours = self.target_ra
            self.dec_deg = self.target_dec
            return b" M31 EX GAL MAG 3.5 SZ178.0'#"

        # Park / unpark (Meade extension)
        if cmd == b"hP":
            self.parked = True
            self.slewing = False
            return None
        if cmd == b"hR":
            self.parked = False
            return None
        if cmd == b"hQ":
            # home
            return None

        # Tracking rate set (Te=enable tracking, Td=disable, Q=stop)
        if cmd == b"TQ" or cmd == b"TS" or cmd == b"TL":
            return None  # sidereal / lunar / solar — ack silently

        # Long / short format mode. Drivers request :U# once on connect.
        if cmd == b"U":
            return None

        # Pulse guide :Mgdxxxx# where d=n/s/e/w, xxxx=ms. Ack silently.
        if cmd.startswith(b"Mg"):
            return None

        # Unknown — return 0 (error) for commands that expect a response,
        # otherwise stay silent. We err on silence so unexpected byte
        # sequences don't confuse the driver.
        return None


def _parse_hms(s: str) -> float:
    """Parse 'HH:MM:SS[.ss]' into hours."""
    parts = s.replace(" ", "").split(":")
    try:
        h = int(parts[0])
        m = int(parts[1])
        sec = float(parts[2]) if len(parts) > 2 else 0
        return h + m / 60 + sec / 3600
    except (ValueError, IndexError):
        return 0.0


def _parse_dms(s: str) -> float:
    """Parse '+DD*MM:SS' or similar into degrees."""
    s = s.replace(" ", "").replace("*", ":").replace("\xdf", ":")
    sign = 1
    if s.startswith("-"):
        sign = -1
        s = s[1:]
    elif s.startswith("+"):
        s = s[1:]
    parts = s.split(":")
    try:
        d = int(parts[0])
        m = int(parts[1]) if len(parts) > 1 else 0
        sec = float(parts[2]) if len(parts) > 2 else 0
        return sign * (d + m / 60 + sec / 3600)
    except (ValueError, IndexError):
        return 0.0
