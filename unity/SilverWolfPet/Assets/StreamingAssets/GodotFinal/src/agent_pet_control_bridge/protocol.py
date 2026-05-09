from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Any, Mapping


class PetPerformCommandError(ValueError):
    """Raised when an Agent tries to send an unsafe or malformed pet command."""


ALLOWED_PHASES = {
    "task_start",
    "searching",
    "operating",
    "waiting_user",
    "blocked",
    "done",
    "failed",
}
ALLOWED_EMOTIONS = {"neutral", "focused", "smug", "annoyed", "confused", "happy"}
ALLOWED_POSES = {"idle", "think", "talk", "point", "annoyed", "smug"}
ALLOWED_FIELDS = {
    "task_id",
    "phase",
    "text",
    "emotion",
    "pose",
    "bubble",
    "blocking",
    "priority",
}
FORBIDDEN_FIELDS = {"animation_name", "bone_name", "raw_transform", "file_path", "script", "code"}


@dataclass(frozen=True)
class PetPerformCommand:
    task_id: str
    phase: str
    text: str = ""
    emotion: str = "neutral"
    pose: str = "idle"
    bubble: bool = True
    blocking: bool = False
    priority: int = 50

    @classmethod
    def from_mapping(cls, payload: Mapping[str, Any]) -> "PetPerformCommand":
        forbidden = FORBIDDEN_FIELDS.intersection(payload.keys())
        if forbidden:
            raise PetPerformCommandError(f"Forbidden pet perform fields: {sorted(forbidden)}")

        unknown = set(payload.keys()) - ALLOWED_FIELDS
        if unknown:
            raise PetPerformCommandError(f"Unknown pet perform fields: {sorted(unknown)}")

        task_id = _as_str(payload.get("task_id", ""))
        if not task_id:
            raise PetPerformCommandError("task_id is required")

        phase = _enum(payload, "phase", "", ALLOWED_PHASES)
        emotion = _enum(payload, "emotion", "neutral", ALLOWED_EMOTIONS)
        pose = _enum(payload, "pose", "idle", ALLOWED_POSES)

        return cls(
            task_id=task_id,
            phase=phase,
            text=_as_str(payload.get("text", "")),
            emotion=emotion,
            pose=pose,
            bubble=bool(payload.get("bubble", True)),
            blocking=bool(payload.get("blocking", False)),
            priority=max(0, min(100, _as_int(payload.get("priority", 50), "priority"))),
        )

    def to_payload(self) -> dict[str, Any]:
        return asdict(self)


def _enum(payload: Mapping[str, Any], field_name: str, default: str, allowed: set[str]) -> str:
    value = _as_str(payload.get(field_name, default)).lower()
    if value not in allowed:
        raise PetPerformCommandError(f"Invalid {field_name}={value!r}; allowed values are {sorted(allowed)}")
    return value


def _as_str(value: Any) -> str:
    if value is None:
        return ""
    return str(value).strip()


def _as_int(value: Any, field_name: str) -> int:
    try:
        return int(value)
    except (TypeError, ValueError) as exc:
        raise PetPerformCommandError(f"{field_name} must be an integer") from exc
