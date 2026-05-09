from __future__ import annotations

from dataclasses import dataclass

from src.pet_pose_bridge import PoseCommand

from .emotion_state import EmotionState
from .emotion_to_pose import emotion_to_pose
from .emotion_types import EmotionCommand, EmotionType
from .tone_analyzer import ToneAnalyzer


@dataclass(frozen=True)
class EmotionDecision:
    emotion_command: EmotionCommand
    pose_command: PoseCommand


class EmotionDirector:
    """Turns AI text into stable local emotion and pose commands."""

    def __init__(
        self,
        *,
        analyzer: ToneAnalyzer | None = None,
        state: EmotionState | None = None,
    ) -> None:
        self.analyzer = analyzer or ToneAnalyzer()
        self.state = state or EmotionState()

    def process_text(
        self,
        text: str,
        *,
        voice_state: str = "speaking",
        is_final: bool = False,
        source: str = "subtitle:ai",
    ) -> EmotionDecision:
        analyzed = self.analyzer.analyze(
            text,
            voice_state=voice_state,
            is_final=is_final,
            source=source,
        )
        directed = self._apply_context(self.state.apply(analyzed), voice_state=voice_state)
        pose = emotion_to_pose(directed, voice_state=voice_state)
        return EmotionDecision(emotion_command=directed, pose_command=pose)

    def _apply_context(self, command: EmotionCommand, *, voice_state: str) -> EmotionCommand:
        if voice_state == "thinking" and command.emotion == EmotionType.NEUTRAL:
            return EmotionCommand(
                emotion=EmotionType.FOCUSED,
                valence=0.05,
                arousal=0.35,
                dominance=0.6,
                intensity=0.34,
                face="focused",
                gesture="none",
                duration_ms=900,
                decay_ms=900,
                priority=25,
                source=command.source,
                text_excerpt=command.text_excerpt,
            ).normalized()
        if voice_state == "speaking" and command.emotion in {EmotionType.SLEEPY, EmotionType.BORED}:
            return command
        if voice_state == "idle" and command.emotion == EmotionType.NEUTRAL:
            return command
        return command
