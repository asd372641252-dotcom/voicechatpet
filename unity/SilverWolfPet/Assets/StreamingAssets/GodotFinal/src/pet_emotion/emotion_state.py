from __future__ import annotations

import time
from dataclasses import replace

from .emotion_types import EmotionCommand, EmotionType


class EmotionState:
    """Maintains base emotion plus a short-lived reaction layer."""

    def __init__(self, *, cooldown_sec: float = 1.6) -> None:
        self.base = EmotionCommand()
        self.reaction: EmotionCommand | None = None
        self.reaction_until = 0.0
        self.cooldown_sec = max(0.0, cooldown_sec)
        self._last_emotion_at: dict[EmotionType, float] = {}

    def apply(self, command: EmotionCommand) -> EmotionCommand:
        now = time.monotonic()
        command = command.normalized()

        if command.emotion == EmotionType.NEUTRAL:
            decayed = self.current(now=now)
            if decayed.emotion != EmotionType.NEUTRAL and decayed.intensity >= 0.2:
                return replace(
                    decayed,
                    text_excerpt=command.text_excerpt,
                    source=command.source,
                    priority=min(decayed.priority, command.priority),
                ).normalized()
            return command

        last_at = self._last_emotion_at.get(command.emotion, 0.0)
        if command.priority < 70 and now - last_at < self.cooldown_sec:
            command = replace(
                command,
                intensity=command.intensity * 0.45,
                gesture="none",
                priority=max(0, command.priority - 10),
            ).normalized()
        else:
            self._last_emotion_at[command.emotion] = now

        self.reaction = command
        self.reaction_until = now + max(command.duration_ms, command.decay_ms, 1) / 1000.0
        return command

    def current(self, *, now: float | None = None) -> EmotionCommand:
        now = time.monotonic() if now is None else now
        if self.reaction is None:
            return self.base
        if now >= self.reaction_until:
            self.reaction = None
            return self.base

        total = max(self.reaction.decay_ms / 1000.0, 0.001)
        remaining = max(0.0, self.reaction_until - now)
        decay_factor = min(1.0, remaining / total)
        return replace(
            self.reaction,
            intensity=self.reaction.intensity * decay_factor,
            priority=max(0, int(self.reaction.priority * decay_factor)),
        ).normalized()
