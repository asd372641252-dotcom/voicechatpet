from __future__ import annotations

from dataclasses import asdict, dataclass, field
from typing import Any, Mapping


class PoseCommandError(ValueError):
    """Raised when a cloud or RTC payload is not a safe semantic pose command."""


ALLOWED_STATES = {"idle", "listening", "thinking", "speaking", "interrupted", "acting", "sleep"}
ALLOWED_EMOTIONS = {
    "neutral",
    "happy",
    "angry",
    "mocking",
    "sleepy",
    "surprised",
    "confused",
}
ALLOWED_GESTURES = {
    "none",
    "small_tease",
    "point",
    "arms_crossed",
    "nod",
    "shake_head",
    "think",
    "smug",
}
ALLOWED_POSTURES = {"stand", "sit", "lie", "air"}
ALLOWED_MOUTH = {"none", "audio_volume", "viseme"}
ALLOWED_FIELDS = {
    "type",
    "state",
    "emotion",
    "gesture",
    "posture",
    "bubble_text",
    "mouth",
    "mouth_open",
    "audio_active",
    "face",
    "emotion_intensity",
    "eye_style",
    "overlay_only",
    "clear_bubble",
    "priority",
    "duration_ms",
    "interruptible",
}
FORBIDDEN_FIELDS = {"animation_name", "bone_name", "raw_transform", "file_path", "script", "code"}


@dataclass(frozen=True)
class PoseCommand:
    type: str = "pet_pose"
    state: str = "idle"
    emotion: str = "neutral"
    gesture: str = "none"
    posture: str = "stand"
    bubble_text: str = ""
    mouth: str = "none"
    face: str = ""
    emotion_intensity: float = 0.0
    eye_style: str = ""
    overlay_only: bool = False
    clear_bubble: bool = False
    priority: int = 0
    duration_ms: int = 0
    interruptible: bool = True
    metadata: dict[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_mapping(cls, payload: Mapping[str, Any]) -> "PoseCommand":
        _reject_forbidden_fields(payload)
        unknown = set(payload.keys()) - ALLOWED_FIELDS
        if unknown:
            raise PoseCommandError(f"Unknown pet pose fields: {sorted(unknown)}")

        command_type = _as_str(payload.get("type", "pet_pose"))
        if command_type != "pet_pose":
            raise PoseCommandError(f"Unsupported command type: {command_type}")

        state = _enum(payload, "state", "idle", ALLOWED_STATES)
        emotion = _enum(payload, "emotion", "neutral", ALLOWED_EMOTIONS)
        gesture = _enum(payload, "gesture", "none", ALLOWED_GESTURES)
        posture = _enum(payload, "posture", "stand", ALLOWED_POSTURES)
        mouth = _enum(payload, "mouth", "none", ALLOWED_MOUTH)

        return cls(
            type=command_type,
            state=state,
            emotion=emotion,
            gesture=gesture,
            posture=posture,
            bubble_text=_as_str(payload.get("bubble_text", "")),
            mouth=mouth,
            face=_as_str(payload.get("face", "")),
            emotion_intensity=_as_float(payload.get("emotion_intensity", 0.0), "emotion_intensity"),
            eye_style=_as_str(payload.get("eye_style", "")),
            overlay_only=bool(payload.get("overlay_only", False)),
            clear_bubble=bool(payload.get("clear_bubble", False)),
            priority=_as_int(payload.get("priority", 0), "priority"),
            duration_ms=max(0, _as_int(payload.get("duration_ms", 0), "duration_ms")),
            interruptible=bool(payload.get("interruptible", True)),
        )

    def to_godot_payload(self) -> dict[str, Any]:
        payload = asdict(self)
        payload.pop("metadata", None)
        return payload


def _reject_forbidden_fields(payload: Mapping[str, Any]) -> None:
    forbidden = FORBIDDEN_FIELDS.intersection(payload.keys())
    if forbidden:
        raise PoseCommandError(
            "Cloud pose commands must stay semantic; forbidden fields: "
            f"{sorted(forbidden)}"
        )


def _enum(
    payload: Mapping[str, Any],
    field_name: str,
    default: str,
    allowed: set[str],
) -> str:
    value = _as_str(payload.get(field_name, default)).lower()
    if value not in allowed:
        raise PoseCommandError(
            f"Invalid {field_name}={value!r}; allowed values are {sorted(allowed)}"
        )
    return value


def _as_str(value: Any) -> str:
    if value is None:
        return ""
    return str(value).strip()


def _as_int(value: Any, field_name: str) -> int:
    try:
        return int(value)
    except (TypeError, ValueError) as exc:
        raise PoseCommandError(f"{field_name} must be an integer") from exc


def _as_float(value: Any, field_name: str) -> float:
    try:
        return max(0.0, min(1.0, float(value)))
    except (TypeError, ValueError) as exc:
        raise PoseCommandError(f"{field_name} must be a number") from exc
