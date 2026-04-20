"""
Serial-protocol emulator framework for driver-in-the-loop testing.

The INDI runtime constraint (drivers only publish capability vectors after
successful CONNECT) defeats offline testing — but "successful CONNECT" just
means the driver's serial-open + protocol-handshake returned OK. If we
satisfy that by standing up a virtual serial port (pty) on the other end
and speaking the driver's wire protocol, the driver doesn't know it's
talking to Python — it publishes its full property set, accepts every
control command, emits telemetry. That's end-to-end verification without
hardware.

Coverage scope: any driver whose hardware interface is RS-232 / USB-serial
text protocol (~70% of amateur astro gear — mounts, most focusers, filter
wheels, weather stations, basic relay boards). USB bulk cameras (ZWO/QHY/
Player One) require vendor SDK emulation and are out of this framework's
scope; they stay covered by static analysis + Diagnostics endpoint feedback.

Design:
  * `ProtocolEmulator` (abstract): one `handle(data: bytes) -> bytes` method.
    Subclass per protocol (LX200, NexStar, SkyWatcher, Moonlite, …).
  * `SerialBridge`: opens a pty pair, hands the driver the slave path, feeds
    bytes the driver writes to the emulator's `handle`, writes the response
    back to the master fd. Background thread, torn down on stop().
"""

from __future__ import annotations

import os
import pty
import select
import threading
from abc import ABC, abstractmethod


class ProtocolEmulator(ABC):
    """Stateful responder for one wire protocol. Implementations keep enough
    state (current RA/Dec, focuser position, filter slot, etc.) to answer
    follow-up queries consistently — real drivers often slew, then poll
    position, then check slewing=false."""

    name: str = "abstract"

    @abstractmethod
    def handle(self, data: bytes) -> bytes:
        """Return the response bytes this protocol would generate for the
        given inbound data. Empty bytes means no response (common for
        fire-and-forget commands like :Q#). Called on every byte arrival;
        implementations buffer internally if the protocol is line-oriented."""


class SerialBridge:
    """pty-backed serial port that runs a protocol emulator on the master end.
    The slave path (/dev/pts/N) is what the INDI driver opens as if it were
    a real /dev/ttyUSB0 — stream reads/writes go through unmodified."""

    def __init__(self, emulator: ProtocolEmulator):
        self.emulator = emulator
        self.master_fd, slave_fd = pty.openpty()
        self.slave_path = os.ttyname(slave_fd)
        # We keep the slave open ourselves — if we close it, the driver would
        # see EOF on first read. pty pairs persist only while at least one end
        # has an open fd.
        self._slave_fd = slave_fd
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._loop, daemon=True)

    def start(self) -> None:
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        # Join before closing — otherwise the reader thread hits EBADF.
        self._thread.join(timeout=2)
        try: os.close(self.master_fd)
        except OSError: pass
        try: os.close(self._slave_fd)
        except OSError: pass

    def _loop(self) -> None:
        while not self._stop.is_set():
            try:
                r, _, _ = select.select([self.master_fd], [], [], 0.2)
            except (ValueError, OSError):
                return
            if self.master_fd in r:
                try:
                    data = os.read(self.master_fd, 4096)
                except OSError:
                    return
                if not data:
                    return
                response = self.emulator.handle(data)
                if response:
                    try:
                        os.write(self.master_fd, response)
                    except OSError:
                        return
