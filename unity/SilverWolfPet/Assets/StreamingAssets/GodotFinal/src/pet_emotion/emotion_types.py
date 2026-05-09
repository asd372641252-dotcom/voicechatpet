from __future__ import annotations

from dataclasses import dataclass, replace
from enum import StrEnum


class EmotionType(StrEnum):
    NEUTRAL = "neutral"
    FOCUSED = "focused"
    THINKING = "thinking"
    BORED = "bored"
    SLEEPY = "sleepy"
    AMUSED = "amused"
    SMUG = "smug"
    MOCKING_LIGHT = "mocking_light"
    MOCKING_HEAVY = "mocking_heavy"
    ANNOYED = "annoyed"
    IMPATIENT = "impatient"
    CONFUSED = "confused"
    SURPRISED = "surprised"
    SERIOUS = "serious"
    PROUD = "proud"
    COMFORTING = "comforting"
    VICTORY = "victory"
    FAIL_TEASE = "fail_tease"


@dataclass(frozen=True)
class EmotionCommand:
    emotion: EmotionType = EmotionType.NEUTRAL
    valence: float = 0.0
    arousal: float = 0.2
    dominance: float = 0.5
    intensity: float = 0.0
    face: str = "neutral"
    gesture: str = "none"
    posture_hint: str = "stand"
    duration_ms: int = 0
    decay_ms: int = 1200
    priority: int = 0
    source: str = "local"
    text_excerpt: str = ""

    def normalized(self) -> "EmotionCommand":
        return replace(
            self,
            valence=_clamp(self.valence, -1.0, 1.0),
            arousal=_clamp(self.arousal, 0.0, 1.0),
            dominance=_clamp(self.dominance, 0.0, 1.0),
            intensity=_clamp(self.intensity, 0.0, 1.0),
            duration_ms=max(0, int(self.duration_ms)),
            decay_ms=max(0, int(self.decay_ms)),
            priority=int(self.priority),
            text_excerpt=self.text_excerpt[:80],
        )


def _clamp(value: float, minimum: float, maximum: float) -> float:
    return max(minimum, min(maximum, float(value)))
