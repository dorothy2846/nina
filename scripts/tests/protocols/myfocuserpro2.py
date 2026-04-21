"""
myFocuserPro2 protocol emulator. myFocuserPro2 is a popular DIY-class
focuser (Robert Brown design, widely reimplemented) with its own numeric
command protocol: `:NN#` where NN is a two-digit code, response is
`<letter><number>#`.

Based on the INDI driver's sendCommand + sscanf("%*c%d#") parse pattern —
the driver ignores the leading byte and reads an integer until the `#`.
That's lenient enough that we can respond with any letter prefix + a
sensible integer and the driver is happy.

Commands seen in init (libindi source):
  :00# position   → "P<n>#"
  :01# target     → "T<n>#"
  :03# move       → fire-and-forget
  :06# max step   → "M<n>#"
  :08# is-moving  → "I<n>#" (0/1)
  :11# temperature → "Z<n>#" (×10, e.g. 200 = 20.0°C)
  :13# coil power → "?<n>#"
  :16# home        → silent
  :24# step-mode  → "S<n>#"
  :26# temp-comp  → "C<n>#"
  :27-29# coeffs etc.
  :37# / :43# / :74# / :76# / :78# / :80# various config reads
"""

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from protocol_emulator import ProtocolEmulator


class MyFocuserPro2Emulator(ProtocolEmulator):
    name = "myfocuserpro2"

    # Map each query code to the letter prefix + an integer value. The
    # specific prefix letter doesn't matter to the driver (sscanf skips it),
    # but we emit the "canonical" ones for readability of a captured fixture.
    RESPONSES = {
        # :03# is the handshake firmware-version query. Driver reads exactly
        # 5 bytes; we emit 5-byte "F242#" (firmware 2.42).
        b"03": (b"F",     242),
        b"00": (b"P",   42000),    # current position (mid-travel)
        b"01": (b"T",   42000),    # target position
        b"06": (b"M",   50000),    # max step
        b"08": (b"I",       0),    # is-moving (0 = stopped)
        b"11": (b"Z",     150),    # temperature ×10 (= 15.0°C)
        b"13": (b"?",       1),    # coil power on
        b"24": (b"S",       2),    # step mode (half step)
        b"26": (b"C",       0),    # temp comp on/off
        b"27": (b"B",       1),    # temp comp coeff sign
        b"28": (b"D",      30),    # temp comp coeff
        b"29": (b"A",      50),    # backlash steps
        b"37": (b"F",       1),    # LCD backlight
        b"43": (b"G",     100),    # motor speed
        b"74": (b"H",       1),    # display
        b"76": (b"J",       1),    # park position
        b"78": (b"K",       1),    # rotary encoder
        b"80": (b"L",       1),    # temp probe present
    }

    def __init__(self) -> None:
        self._buf = b""
        self.position = self.RESPONSES[b"00"][1]
        self.target = self.position

    def handle(self, data: bytes) -> bytes:
        self._buf += data
        out = b""
        while b":" in self._buf and b"#" in self._buf:
            i = self._buf.find(b":")
            self._buf = self._buf[i:]
            end = self._buf.find(b"#")
            if end < 0: break
            cmd = self._buf[1:end]  # strip ':' and '#'
            self._buf = self._buf[end + 1:]
            if cmd in self.RESPONSES:
                letter, value = self.RESPONSES[cmd]
                out += letter + str(value).encode() + b"#"
            # write commands (:03, :05, :SN*) are silent
        return out
