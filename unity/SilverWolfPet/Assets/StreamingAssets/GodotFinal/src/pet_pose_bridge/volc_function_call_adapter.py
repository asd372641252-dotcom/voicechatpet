from __future__ import annotations

import json
from typing import Any, Mapping

from .pose_command import (
    ALLOWED_EMOTIONS,
    ALLOWED_GESTURES,
    ALLOWED_POSTURES,
    ALLOWED_STATES,
    PoseCommand,
    PoseCommandError,
)


SET_PET_POSE_ALLOWED_ARGUMENTS = {
    "state",
    "emotion",
    "gesture",
    "posture",
    "duration_ms",
    "priority",
    "interruptible",
}
FORBIDDEN_TOOL_ARGUMENTS = {
    "animation_name",
    "bone_name",
    "raw_transform",
    "file_path",
    "script",
    "code",
}

SET_PET_POSE_TOOL_SCHEMA: dict[str, Any] = {
    "name": "set_pet_pose",
    "description": "Send a semantic desktop-pet pose request. Do not send animation names, bone names, transforms, or file paths.",
    "parameters": {
        "type": "object",
        "additionalProperties": False,
        "properties": {
            "state": {"type": "string", "enum": sorted(ALLOWED_STATES)},
            "emotion": {"type": "string", "enum": sorted(ALLOWED_EMOTIONS)},
            "gesture": {"type": "string", "enum": sorted(ALLOWED_GESTURES)},
            "posture": {"type": "string", "enum": sorted(ALLOWED_POSTURES)},
            "duration_ms": {"type": "integer", "minimum": 0},
            "priority": {"type": "integer"},
            "interruptible": {"type": "boolean"},
        },
        "required": [],
    },
}


class VolcFunctionCallAdapter:
    """Parses mixed-orchestration Function Calling output into safe pose commands."""

    tool_name = "set_pet_pose"

    def tool_schema(self) -> dict[str, Any]:
        return SET_PET_POSE_TOOL_SCHEMA

    def handle_tool_call(self, call: Mapping[str, Any]) -> PoseCommand:
        return self.from_function_call(call)

    def from_function_call(self, call: Mapping[str, Any]) -> PoseCommand:
        name = str(call.get("name", call.get("function_name", ""))).strip()
        if name != self.tool_name:
            raise PoseCommandError(f"Unsupported function call: {name}")

        arguments = call.get("arguments", {})
        if isinstance(arguments, str):
            arguments = json.loads(arguments or "{}")
        if not isinstance(arguments, Mapping):
            raise PoseCommandError("set_pet_pose arguments must be a JSON object")

        forbidden = FORBIDDEN_TOOL_ARGUMENTS.intersection(arguments.keys())
        if forbidden:
            raise PoseCommandError(
                f"Forbidden set_pet_pose arguments: {sorted(forbidden)}"
            )
        unknown = set(arguments.keys()) - SET_PET_POSE_ALLOWED_ARGUMENTS
        if unknown:
            raise PoseCommandError(f"Unknown set_pet_pose arguments: {sorted(unknown)}")

        payload = {
            "type": "pet_pose",
            "state": arguments.get("state", "acting"),
            "emotion": arguments.get("emotion", "neutral"),
            "gesture": arguments.get("gesture", "none"),
            "posture": arguments.get("posture", "stand"),
            "duration_ms": arguments.get("duration_ms", 0),
            "priority": arguments.get("priority", 0),
            "interruptible": arguments.get("interruptible", True),
            "mouth": "none",
            "bubble_text": "",
        }
        return PoseCommand.from_mapping(payload)
