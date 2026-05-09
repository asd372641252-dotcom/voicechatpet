from __future__ import annotations

import json
import socket
import time
from threading import Lock
from typing import Any, Mapping

from .pose_command import PoseCommand


class GodotPoseClient:
    """Small TCP client for the local Godot pose server.

    Connection errors are reported as False instead of raising by default, so
    the Volc voice session can continue even when Godot is closed.
    """

    backend = "godot"

    def __init__(
        self,
        host: str = "127.0.0.1",
        port: int = 17865,
        timeout_sec: float = 0.2,
        offline_cooldown_sec: float = 2.0,
    ) -> None:
        self.host = host
        self.port = port
        self.timeout_sec = timeout_sec
        self.offline_cooldown_sec = offline_cooldown_sec
        self._offline_until = 0.0
        self._state_lock = Lock()

    def send_pose(
        self,
        command: PoseCommand | Mapping[str, Any],
        *,
        raise_on_error: bool = False,
    ) -> bool:
        payload = command.to_godot_payload() if isinstance(command, PoseCommand) else dict(command)
        # Godot restores \uXXXX escapes during JSON.parse_string(). Keeping the
        # wire payload ASCII avoids Windows console/code-page or TCP decoding
        # edge cases turning CJK text into '?' before it reaches the bubble.
        message = json.dumps(payload, ensure_ascii=True, separators=(",", ":")) + "\n"
        now = time.monotonic()
        with self._state_lock:
            if not raise_on_error and now < self._offline_until:
                return False
        try:
            with socket.create_connection((self.host, self.port), self.timeout_sec) as sock:
                sock.settimeout(self.timeout_sec)
                sock.sendall(message.encode("utf-8"))
            with self._state_lock:
                self._offline_until = 0.0
            return True
        except OSError:
            with self._state_lock:
                self._offline_until = time.monotonic() + max(0.0, self.offline_cooldown_sec)
            if raise_on_error:
                raise
            return False
