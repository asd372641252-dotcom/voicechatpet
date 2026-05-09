from __future__ import annotations

import re
from dataclasses import dataclass, field

from .types import NarrationContext, NarrationRouterConfig, RouteDecision, VisionEvent, new_id, now_ms


@dataclass
class NarrationRouter:
    config: NarrationRouterConfig = field(default_factory=NarrationRouterConfig)
    _last_spoken_by_signature: dict[str, int] = field(default_factory=dict)
    _last_companion_report_at_ms: int = 0

    def route(self, event: VisionEvent, context: NarrationContext | None = None) -> RouteDecision:
        ctx = context or NarrationContext(now_ms=now_ms())
        trace_id = new_id("route")
        if not self.config.enabled:
            return RouteDecision("update_context_only", "router_disabled", trace_id=trace_id, mode="context")
        if event.is_expired(ctx.now_ms):
            return RouteDecision("drop", "event_expired", trace_id=trace_id, mode="drop")
        if event.is_degraded:
            return RouteDecision("update_context_only", "degraded_event", trace_id=trace_id, mode="context")
        if str(ctx.current_task_state or "").strip().lower() == "voice_inactive":
            return RouteDecision("update_context_only", "voice_inactive", trace_id=trace_id, mode="context")

        priority = float(event.priority)
        if priority < 0.45 or event.speak_policy == "silent":
            return RouteDecision("update_context_only", "priority_below_0_45_or_silent", trace_id=trace_id, mode="context")
        if priority < 0.70 or event.speak_policy == "speak_if_asked":
            return RouteDecision("update_context_only", "context_until_user_asks", trace_id=trace_id, mode="context")

        if self._speech_recently_used(event, ctx.now_ms):
            return RouteDecision("drop", "dedup_speak_cooldown", trace_id=trace_id, mode="drop")

        user_busy = self._user_is_busy(ctx)
        ai_busy = self._ai_is_busy(ctx)
        high_risk_interrupt = event.risk == "high" and self.config.high_risk_can_interrupt and priority >= 0.90
        can_interrupt_user = self.config.allow_interrupt_user or high_risk_interrupt
        can_interrupt_ai = self.config.allow_interrupt_ai or priority >= self.config.interrupt_priority

        if user_busy and not can_interrupt_user:
            return RouteDecision("defer_until_user_pause", "user_speaking_or_listening", trace_id=trace_id, mode="deferred_speech")
        if ai_busy and not can_interrupt_ai:
            return RouteDecision("defer_until_user_pause", "ai_speaking", trace_id=trace_id, mode="deferred_speech")

        if priority < 0.90:
            if event.scene in {"document", "browser", "code", "chat"} and ctx.user_pause_ms() < self.config.user_pause_ms:
                return RouteDecision("defer_until_user_pause", "non_game_wait_for_pause", trace_id=trace_id, mode="deferred_speech")
            if ctx.user_pause_ms() >= self.config.user_pause_ms:
                self._mark_spoken(event, ctx.now_ms)
                return RouteDecision("speak_low_priority", "user_paused", trace_id=trace_id, mode="deferred_speech")
            return RouteDecision("defer_until_user_pause", "waiting_for_user_pause", trace_id=trace_id, mode="deferred_speech")

        if event.scene in {"document", "browser", "code", "chat"} and event.risk != "high":
            if ctx.user_pause_ms() < self.config.user_pause_ms:
                return RouteDecision("defer_until_user_pause", "quiet_scene_wait_for_pause", trace_id=trace_id, mode="deferred_speech")

        self._mark_spoken(event, ctx.now_ms)
        return RouteDecision("speak_now", "priority_high", trace_id=trace_id, mode="immediate_speech")

    def route_companion_report(
        self,
        event: VisionEvent,
        context: NarrationContext | None = None,
        *,
        min_priority: float = 0.35,
        report_cooldown_ms: int = 6000,
    ) -> RouteDecision:
        ctx = context or NarrationContext(now_ms=now_ms())
        trace_id = new_id("route")
        if not self.config.enabled:
            return RouteDecision("update_context_only", "router_disabled", trace_id=trace_id, mode="context")
        if event.is_expired(ctx.now_ms):
            return RouteDecision("drop", "event_expired", trace_id=trace_id, mode="drop")
        if event.is_degraded:
            return RouteDecision("update_context_only", "degraded_event", trace_id=trace_id, mode="context")
        if str(ctx.current_task_state or "").strip().lower() != "voice_active":
            return RouteDecision("update_context_only", "voice_inactive", trace_id=trace_id, mode="context")
        if event.priority < max(0.0, float(min_priority)) or event.speak_policy == "silent":
            return RouteDecision("update_context_only", "companion_priority_below_min_or_silent", trace_id=trace_id, mode="context")
        current_ms = ctx.now_ms or now_ms()
        if self._speech_recently_used(event, current_ms):
            return RouteDecision("drop", "dedup_speak_cooldown", trace_id=trace_id, mode="drop")
        cooldown = max(1000, int(report_cooldown_ms))
        if self._last_companion_report_at_ms and current_ms - self._last_companion_report_at_ms < cooldown:
            return RouteDecision("drop", "companion_report_cooldown", trace_id=trace_id, mode="drop")

        user_busy = self._user_is_busy(ctx)
        ai_busy = self._ai_is_busy(ctx)
        high_risk_interrupt = event.risk == "high" and self.config.high_risk_can_interrupt and event.priority >= 0.90
        can_interrupt_user = self.config.allow_interrupt_user or high_risk_interrupt
        can_interrupt_ai = self.config.allow_interrupt_ai or event.priority >= self.config.interrupt_priority
        if user_busy and not can_interrupt_user:
            return RouteDecision("defer_until_user_pause", "user_speaking_or_listening", trace_id=trace_id, mode="deferred_speech")
        if ai_busy and not can_interrupt_ai:
            return RouteDecision("defer_until_user_pause", "ai_speaking", trace_id=trace_id, mode="deferred_speech")

        self._last_companion_report_at_ms = current_ms
        self._mark_spoken(event, current_ms)
        if event.priority >= self.config.interrupt_priority and event.risk == "high":
            return RouteDecision("speak_now", "companion_high_risk", trace_id=trace_id, mode="immediate_speech")
        return RouteDecision("speak_low_priority", "companion_play_report", trace_id=trace_id, mode="deferred_speech")

    def _speech_recently_used(self, event: VisionEvent, current_ms: int) -> bool:
        signature = self.signature(event)
        self._last_spoken_by_signature = {
            key: value for key, value in self._last_spoken_by_signature.items() if current_ms - value <= self.config.speak_cooldown_ms
        }
        last = self._last_spoken_by_signature.get(signature, 0)
        return bool(last and current_ms - last < self.config.speak_cooldown_ms)

    def _mark_spoken(self, event: VisionEvent, current_ms: int) -> None:
        self._last_spoken_by_signature[self.signature(event)] = current_ms

    def signature(self, event: VisionEvent) -> str:
        text = f"{event.scene}|{event.summary}|{event.suggested_speech}"
        return re.sub(r"\s+", "", text.lower())[:120]

    def _user_is_busy(self, context: NarrationContext) -> bool:
        state = str(context.s2s_state or "").strip().lower()
        return context.user_recently_speaking or state in {"listening", "user_speaking", "speaking_user"}

    def _ai_is_busy(self, context: NarrationContext) -> bool:
        state = str(context.s2s_state or "").strip().lower()
        return context.ai_speaking or state in {"speaking", "thinking"}
