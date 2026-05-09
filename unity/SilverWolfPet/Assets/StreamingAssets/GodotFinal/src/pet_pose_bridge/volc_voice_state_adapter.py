from __future__ import annotations

from typing import Any, Mapping

from .pose_command import PoseCommand


VOLC_STATE_TO_POSE = {
    "listening": {"state": "listening", "emotion": "neutral", "mouth": "none"},
    "thinking": {"state": "thinking", "emotion": "confused", "gesture": "think"},
    "speaking": {"state": "speaking", "emotion": "neutral", "mouth": "audio_volume"},
    "idle": {"state": "idle", "emotion": "neutral", "mouth": "none"},
    "interrupted": {"state": "listening", "emotion": "surprised", "mouth": "none"},
}


class VolcVoiceStateAdapter:
    """Converts Volc RTC AI state callbacks into local semantic pet commands."""

    def from_ai_state(
        self,
        state: str,
        *,
        bubble_text: str = "",
        priority: int = 0,
    ) -> PoseCommand:
        normalized = str(state).strip().lower()
        base = dict(VOLC_STATE_TO_POSE.get(normalized, VOLC_STATE_TO_POSE["idle"]))
        base.update(
            {
                "type": "pet_pose",
                "bubble_text": bubble_text,
                "priority": priority,
            }
        )
        return PoseCommand.from_mapping(base)

    def from_event(self, event: Mapping[str, Any]) -> PoseCommand:
        state = (
            event.get("state")
            or event.get("ai_state")
            or event.get("status")
            or event.get("event")
            or "idle"
        )
        bubble_text = str(event.get("bubble_text", event.get("text", "")))
        priority = int(event.get("priority", 0))
        return self.from_ai_state(str(state), bubble_text=bubble_text, priority=priority)

    def from_subtitle(
        self,
        text: str,
        *,
        speaker: str = "ai",
        is_final: bool = False,
    ) -> PoseCommand:
        normalized_speaker = speaker.strip().lower()
        if normalized_speaker in {"user", "human"}:
            return PoseCommand.from_mapping(
                {
                    "type": "pet_pose",
                    "state": "listening",
                    "emotion": "neutral",
                    "gesture": "none",
                    "posture": "stand",
                    "bubble_text": "",
                    "mouth": "none",
                    "priority": 0,
                    "duration_ms": 0,
                }
            )

        return PoseCommand.from_mapping(
            {
                "type": "pet_pose",
                "state": "speaking",
                "emotion": analyze_caption_emotion(text),
                "gesture": "none",
                "posture": "stand",
                "bubble_text": text,
                "mouth": "audio_volume",
                "priority": 1 if is_final else 0,
                "duration_ms": 0,
            }
        )


def analyze_caption_emotion(text: str) -> str:
    lowered = text.lower()
    if any(token in lowered for token in ["哈哈", "开心", "太好了", "nice", "great", "happy"]):
        return "happy"
    if any(token in lowered for token in ["哼", "笨", "太迟", "不算太迟", "mock", "tease"]):
        return "mocking"
    if any(token in lowered for token in ["生气", "烦", "别吵", "angry"]):
        return "angry"
    if any(token in lowered for token in ["什么", "为什么", "不懂", "confused", "?"]):
        return "confused"
    if any(token in lowered for token in ["困", "睡", "晚安", "sleep"]):
        return "sleepy"
    return "neutral"
