from __future__ import annotations

import json
import socket
import time
from threading import Lock
from typing import Any, Mapping

from .pose_command import ALLOWED_FIELDS, PoseCommand
from .pose_router import PoseRouter


class UnityPoseClient:
    """TCP client for the Unity PetDesktop presentation route.

    Unity's ``PetControlServer`` accepts compact commands such as
    ``state/action/text/mouth``. The rest of the project speaks the safer
    semantic ``pet_pose`` protocol, so this client performs the local
    translation at the presentation boundary.
    """

    backend = "unity"

    def __init__(
        self,
        host: str = "127.0.0.1",
        port: int = 17861,
        timeout_sec: float = 0.2,
        offline_cooldown_sec: float = 2.0,
    ) -> None:
        self.host = host
        self.port = port
        self.timeout_sec = timeout_sec
        self.offline_cooldown_sec = offline_cooldown_sec
        self._offline_until = 0.0
        self._state_lock = Lock()
        self._router = PoseRouter()

    def send_pose(
        self,
        command: PoseCommand | Mapping[str, Any],
        *,
        raise_on_error: bool = False,
    ) -> bool:
        payload = command.to_godot_payload() if isinstance(command, PoseCommand) else dict(command)
        unity_payload = self.to_unity_payload(payload)
        message = json.dumps(unity_payload, ensure_ascii=True, separators=(",", ":")) + "\n"
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

    def to_unity_payload(self, payload: Mapping[str, Any]) -> dict[str, Any]:
        overlay_only = bool(payload.get("overlay_only", False))
        result: dict[str, Any] = {}

        for key in ("quit_app", "voice_runtime", "screen_vision", "camera_video", "voice_route", "companion_interval_sec"):
            if key in payload:
                result[key] = payload[key]

        text = _first_text(payload, "text", "bubble_text")
        if text:
            result["text"] = text
            duration_ms = _as_int(payload.get("duration_ms"), 0)
            if duration_ms > 0 or (overlay_only and "duration_ms" in payload):
                result["duration_ms"] = duration_ms
        if bool(payload.get("clear_bubble", False)):
            result["clear_bubble"] = True

        result.update(_resolve_mouth_payload(payload))

        if overlay_only:
            _apply_overlay_voice_state(result, payload)
            return result

        has_pose_content = any(key in payload for key in ALLOWED_FIELDS) or any(
            key in payload for key in ("state", "emotion", "gesture", "action")
        )
        if result and not has_pose_content:
            return result

        semantic = _semantic_pose_payload(payload)
        command = PoseCommand.from_mapping(semantic)
        routed = self._router.route(command)

        result["state"] = _unity_state(command.state, command.emotion, command.gesture)
        result["emotion"] = _unity_emotion(command)
        explicit_action = str(payload.get("action") or "").strip()
        routed_action = str(routed.action or "").strip()
        if explicit_action:
            result["action"] = explicit_action
        elif result["state"] != "speaking":
            result["action"] = routed_action
        result["priority"] = _as_int(payload.get("priority"), command.priority)
        return {key: value for key, value in result.items() if value != ""}


def _semantic_pose_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    semantic = {key: payload[key] for key in ALLOWED_FIELDS if key in payload}
    semantic.setdefault("type", "pet_pose")
    return semantic


def _first_text(payload: Mapping[str, Any], *keys: str) -> str:
    for key in keys:
        value = payload.get(key)
        if value is not None and str(value):
            return str(value)
    return ""


def _resolve_mouth_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    mouth_value = payload.get("mouth")
    mouth = str(mouth_value or "").strip().lower()
    if mouth in {"audio_volume", "viseme"}:
        result["mouth"] = mouth
    elif mouth_value not in (None, ""):
        try:
            result["mouth"] = _clamp_float(mouth_value, 0.0, 1.0)
        except (TypeError, ValueError):
            pass

    if "mouth_open" in payload:
        result["mouth_open"] = _clamp_float(payload.get("mouth_open"), 0.0, 1.0)
    if "audio_active" in payload:
        result["audio_active"] = bool(payload.get("audio_active"))
    return result


def _apply_overlay_voice_state(result: dict[str, Any], payload: Mapping[str, Any]) -> None:
    mouth = str(payload.get("mouth") or "").strip().lower()
    has_audio_mouth = mouth in {"audio_volume", "viseme"} or "audio_active" in payload
    if not has_audio_mouth:
        return

    audio_active = bool(payload.get("audio_active", True))
    result["state"] = "speaking" if audio_active else "idle"


def _unity_state(state: str, emotion: str, gesture: str) -> str:
    normalized = str(state or "idle").strip().lower()
    if normalized == "listening":
        return "idle"
    if normalized == "interrupted":
        return "clicked"
    if normalized == "acting":
        if gesture in {"point", "smug"}:
            return "happy"
        if gesture == "shake_head":
            return "angry"
        return "idle"
    if normalized == "sleep":
        return "sleepy"
    if emotion in {"happy", "angry", "sleepy", "mocking", "surprised"} and normalized == "idle":
        return "angry" if emotion == "mocking" else emotion
    return normalized


def _unity_emotion(command: PoseCommand) -> str:
    if command.face:
        return command.face
    if command.emotion != "neutral":
        return command.emotion
    if command.state == "interrupted":
        return "surprised"
    return "neutral"


def _as_int(value: Any, default: int) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def _clamp_float(value: Any, minimum: float, maximum: float) -> float:
    try:
        number = float(value)
    except (TypeError, ValueError):
        number = minimum
    return max(minimum, min(maximum, number))
