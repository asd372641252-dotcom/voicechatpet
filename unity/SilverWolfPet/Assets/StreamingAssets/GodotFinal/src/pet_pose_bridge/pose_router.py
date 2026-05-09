from __future__ import annotations

import time
from dataclasses import dataclass
from typing import Any, Mapping

from .pose_command import PoseCommand


@dataclass(frozen=True)
class RoutedPose:
    command: PoseCommand
    action: str
    route: tuple[str, ...]
    payload: dict[str, Any]


STATE_ACTIONS = {
    "idle": "KA_Idle01_breathing",
    "listening": "KA_Idle02_LookLeftAndRight",
    "thinking": "KA_Idle08_ComeUpWithAnIdea",
    "speaking": "KA_Idle50_StandingTalk1_1",
    "interrupted": "KA_Idle29_Surprised",
    "acting": "KA_Idle45_WaveHandSlightly",
    "sleep": "KA_Idle09_Waiting",
}

EMOTION_ACTIONS = {
    "happy": "KA_Idle28_Laugh",
    "angry": "KA_Idle27_Angry",
    "mocking": "KA_Idle42_Taunt",
    "sleepy": "KA_Idle09_Waiting",
    "surprised": "KA_Idle29_Surprised",
    "confused": "KA_Idle08_ComeUpWithAnIdea",
}

GESTURE_ACTIONS = {
    "small_tease": "KA_Idle42_Taunt",
    "point": "KA_Idle39_CuteArmUp",
    "arms_crossed": "KA_Idle37_Tsundere",
    "nod": "KA_Idle44_GreetingBow",
    "shake_head": "KA_Idle02_LookLeftAndRight",
    "think": "KA_Idle08_ComeUpWithAnIdea",
    "smug": "KA_Idle43_HandOnHip",
}

POSTURE_DEFAULTS = {
    "stand": "KA_Idle01_breathing",
    "sit": "KA_Idle10_Sit",
    "lie": "KA_Idle01_breathing",
    "air": "KA_Idle01_breathing",
}

POSTURE_ROUTES = {
    ("stand", "sit"): ("KA_Idle10_Sit",),
    ("sit", "stand"): ("KA_Idle01_breathing",),
}


class PoseRouter:
    """Local-only semantic pose router.

    Cloud function calls never choose animation files. This router is the trust
    boundary that maps semantic state/emotion/gesture/posture to local Godot
    actions and transition routes.
    """

    def __init__(
        self,
        available_actions: set[str] | None = None,
        *,
        semantic_cooldown_sec: float = 1.2,
        priority_bypass_cooldown: int = 2,
    ) -> None:
        self.available_actions = available_actions or set()
        self.current_posture = "stand"
        self.semantic_cooldown_sec = max(0.0, semantic_cooldown_sec)
        self.priority_bypass_cooldown = priority_bypass_cooldown
        self._last_semantic_at: dict[tuple[str, str, str], float] = {}

    def route(self, command: PoseCommand) -> RoutedPose:
        command = self._apply_semantic_cooldown(command)
        action = self._select_action(command)
        route = self._route_for_posture(command.posture, action)
        payload = command.to_godot_payload()
        return RoutedPose(command=command, action=action, route=route, payload=payload)

    def update_current_posture(self, posture: str) -> None:
        if posture in POSTURE_DEFAULTS:
            self.current_posture = posture

    def _select_action(self, command: PoseCommand) -> str:
        candidates = [
            GESTURE_ACTIONS.get(command.gesture, ""),
            EMOTION_ACTIONS.get(command.emotion, ""),
        ]
        if command.posture != "stand":
            candidates.append(POSTURE_DEFAULTS.get(command.posture, ""))
        candidates.extend(
            [
                STATE_ACTIONS.get(command.state, ""),
                POSTURE_DEFAULTS.get(command.posture, ""),
                STATE_ACTIONS["idle"],
            ]
        )
        for action in candidates:
            if self._is_available(action):
                return action
        return STATE_ACTIONS["idle"]

    def _route_for_posture(self, target_posture: str, target_action: str) -> tuple[str, ...]:
        if target_posture == self.current_posture:
            self.current_posture = target_posture
            return (target_action,)
        route = list(POSTURE_ROUTES.get((self.current_posture, target_posture), ()))
        route.append(target_action)
        safe_actions: list[str] = []
        for action in route:
            if self._is_available(action) and (not safe_actions or safe_actions[-1] != action):
                safe_actions.append(action)
        safe_route = tuple(safe_actions)
        self.current_posture = target_posture
        return safe_route or (target_action,)

    def _is_available(self, action: str) -> bool:
        return bool(action) and (not self.available_actions or action in self.available_actions)

    def _apply_semantic_cooldown(self, command: PoseCommand) -> PoseCommand:
        if command.priority >= self.priority_bypass_cooldown:
            return command
        if command.emotion == "neutral" and command.gesture == "none":
            return command

        now = time.monotonic()
        key = (command.state, command.emotion, command.gesture)
        last = self._last_semantic_at.get(key, 0.0)
        if now - last >= self.semantic_cooldown_sec:
            self._last_semantic_at[key] = now
            return command

        return PoseCommand(
            type=command.type,
            state=command.state,
            emotion="neutral",
            gesture="none",
            posture=command.posture,
            bubble_text=command.bubble_text,
            mouth=command.mouth,
            priority=command.priority,
            duration_ms=command.duration_ms,
            interruptible=command.interruptible,
        )


def route_payload(
    payload: Mapping[str, Any],
    *,
    available_actions: set[str] | None = None,
) -> RoutedPose:
    return PoseRouter(available_actions=available_actions).route(PoseCommand.from_mapping(payload))
