from __future__ import annotations

from src.pet_pose_bridge import PoseCommand

from .emotion_types import EmotionCommand, EmotionType


FACE_BY_EMOTION = {
    EmotionType.NEUTRAL: "neutral",
    EmotionType.FOCUSED: "thinking",
    EmotionType.THINKING: "thinking",
    EmotionType.BORED: "sleepy",
    EmotionType.SLEEPY: "sleepy",
    EmotionType.AMUSED: "happy",
    EmotionType.SMUG: "smug",
    EmotionType.MOCKING_LIGHT: "mocking",
    EmotionType.MOCKING_HEAVY: "mocking",
    EmotionType.ANNOYED: "angry",
    EmotionType.IMPATIENT: "angry",
    EmotionType.CONFUSED: "confused",
    EmotionType.SURPRISED: "surprised",
    EmotionType.SERIOUS: "serious",
    EmotionType.PROUD: "smug",
    EmotionType.COMFORTING: "neutral",
    EmotionType.VICTORY: "happy",
    EmotionType.FAIL_TEASE: "mocking",
}

POSE_BY_EMOTION = {
    EmotionType.MOCKING_LIGHT: ("speaking", "mocking", "small_tease", "stand", 55, 2500),
    EmotionType.MOCKING_HEAVY: ("speaking", "mocking", "small_tease", "stand", 65, 2800),
    EmotionType.ANNOYED: ("speaking", "angry", "shake_head", "stand", 60, 2200),
    EmotionType.IMPATIENT: ("speaking", "angry", "shake_head", "stand", 50, 1800),
    EmotionType.FOCUSED: ("thinking", "neutral", "think", "stand", 50, 2200),
    EmotionType.THINKING: ("thinking", "confused", "think", "stand", 45, 1800),
    EmotionType.CONFUSED: ("thinking", "confused", "think", "stand", 50, 2000),
    EmotionType.VICTORY: ("acting", "happy", "smug", "stand", 70, 2500),
    EmotionType.PROUD: ("speaking", "happy", "smug", "stand", 55, 2000),
    EmotionType.AMUSED: ("speaking", "happy", "small_tease", "stand", 50, 1800),
    EmotionType.SLEEPY: ("idle", "sleepy", "none", "sit", 30, 2600),
    EmotionType.BORED: ("idle", "sleepy", "none", "stand", 30, 2200),
    EmotionType.SURPRISED: ("speaking", "surprised", "none", "stand", 50, 1600),
    EmotionType.SERIOUS: ("speaking", "neutral", "arms_crossed", "stand", 45, 1800),
    EmotionType.COMFORTING: ("speaking", "neutral", "nod", "stand", 45, 2200),
    EmotionType.FAIL_TEASE: ("speaking", "mocking", "small_tease", "stand", 50, 2000),
}


def emotion_to_pose(command: EmotionCommand, *, voice_state: str = "speaking") -> PoseCommand:
    command = command.normalized()
    state, emotion, gesture, posture, priority, duration_ms = POSE_BY_EMOTION.get(
        command.emotion,
        (_safe_voice_state(voice_state), "neutral", "none", "stand", 10, 0),
    )

    if voice_state == "speaking" and state not in {"speaking", "acting"}:
        state = "speaking"
    elif voice_state == "thinking" and command.emotion in {EmotionType.FOCUSED, EmotionType.THINKING, EmotionType.CONFUSED}:
        state = "thinking"
    elif voice_state == "idle" and command.emotion in {EmotionType.SLEEPY, EmotionType.BORED}:
        state = "idle"

    overlay_only = command.intensity < 0.35
    if command.intensity < 0.35:
        gesture = "none"
    elif command.intensity < 0.7 and state == "acting":
        state = "speaking"

    if voice_state == "speaking" and posture not in {"stand", "sit"}:
        posture = "stand"

    face = command.face or FACE_BY_EMOTION.get(command.emotion, "neutral")
    return PoseCommand.from_mapping(
        {
            "type": "pet_pose",
            "state": state,
            "emotion": emotion,
            "gesture": gesture,
            "posture": posture,
            "bubble_text": command.text_excerpt,
            "mouth": "audio_volume" if state == "speaking" else "none",
            "priority": max(priority, command.priority),
            "duration_ms": duration_ms if not overlay_only else min(duration_ms, 900),
            "interruptible": True,
            "face": face,
            "emotion_intensity": command.intensity,
            "eye_style": face,
            "overlay_only": overlay_only,
        }
    )


def _safe_voice_state(state: str) -> str:
    normalized = str(state or "speaking").strip().lower()
    if normalized in {"speaking", "thinking", "idle", "listening"}:
        return normalized
    return "speaking"
