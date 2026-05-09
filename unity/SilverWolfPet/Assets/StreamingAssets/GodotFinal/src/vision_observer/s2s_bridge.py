from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Callable, Mapping

from .compressor import VisionEventCompressor, ensure_sentence_end
from .types import InjectionRecord, NarrationContext, RouteDecision, VisionEvent, new_id, now_ms


QueueExternalTextWithOptions = Callable[..., Mapping[str, Any]]


SYSTEM_OBSERVATION_PREFIX = "【系统观察事件，不是用户发言】"


class S2SContextInjector:
    def inject(self, text: str, *, mode: str, event: VisionEvent, trace_id: str) -> InjectionRecord:
        raise NotImplementedError


@dataclass
class MockS2SContextInjector(S2SContextInjector):
    records: list[InjectionRecord] = field(default_factory=list)

    def inject(self, text: str, *, mode: str, event: VisionEvent, trace_id: str) -> InjectionRecord:
        record = InjectionRecord(
            trace_id=trace_id,
            mode=mode,
            text=_format_system_observation(text),
            event_id=event.event_id,
            role="system_observation",
            queued=mode != "context",
            raw_result={"ok": True, "mock": True},
        )
        self.records.append(record)
        return record


class VolcExternalTextS2SInjector(S2SContextInjector):
    """Adapter over the existing WebRTC ExternalTextToLLM queue.

    Context-only events are stored locally by the narration bridge. Speech
    events are queued as external text with an explicit system-observation
    marker, so downstream code can distinguish them from user messages.
    """

    def __init__(self, queue_external_text: QueueExternalTextWithOptions) -> None:
        self.queue_external_text = queue_external_text
        self.context_records: list[InjectionRecord] = []

    def inject(self, text: str, *, mode: str, event: VisionEvent, trace_id: str) -> InjectionRecord:
        if mode == "context":
            formatted = _format_system_observation(text)
            record = InjectionRecord(
                trace_id=trace_id,
                mode=mode,
                text=formatted,
                event_id=event.event_id,
                role="system_observation",
                queued=False,
                raw_result={"ok": True, "context_only": True},
            )
            self.context_records.append(record)
            self.context_records = self.context_records[-50:]
            return record

        interrupt_mode = 2 if mode == "immediate_speech" else 1
        formatted = str(text or "").strip()
        result = self.queue_external_text(
            formatted,
            interrupt_mode=interrupt_mode,
            source="vision_observer",
            metadata={
                "role": "external",
                "message_type": "external_text",
                "trace_id": trace_id,
                "event_id": event.event_id,
                "mode": mode,
            },
        )
        return InjectionRecord(
            trace_id=trace_id,
            mode=mode,
            text=formatted,
            event_id=event.event_id,
            role="external",
            queued=bool(result.get("queued") or result.get("ok")),
            raw_result=result,
        )


@dataclass
class S2SNarrationBridge:
    injector: S2SContextInjector
    compressor: VisionEventCompressor
    records: list[InjectionRecord] = field(default_factory=list)
    pending_deferred: list[tuple[VisionEvent, str, str]] = field(default_factory=list)

    def handle(self, event: VisionEvent, decision: RouteDecision) -> InjectionRecord | None:
        if decision.action == "drop" or decision.mode == "drop":
            return None
        compressed = self.compressor.compress(event)
        if not compressed:
            return None
        mode = _mode_from_decision(decision)
        trace_id = decision.trace_id or new_id("s2s")
        if decision.action == "defer_until_user_pause":
            self.pending_deferred.append((event, compressed, trace_id))
            self.pending_deferred = self.pending_deferred[-20:]
            record = InjectionRecord(
                trace_id=trace_id,
                mode=mode,
                text=_format_system_observation(compressed),
                event_id=event.event_id,
                role="system_observation",
                queued=False,
                raw_result={"ok": True, "deferred": True},
            )
            self.records.append(record)
            self.records = self.records[-100:]
            return record
        record = self.injector.inject(compressed, mode=mode, event=event, trace_id=trace_id)
        self.records.append(record)
        self.records = self.records[-100:]
        return record

    def inject_text(self, event: VisionEvent, text: str, *, mode: str, trace_id: str) -> InjectionRecord | None:
        body = ensure_sentence_end(str(text or "").strip())
        if not body:
            return None
        record = self.injector.inject(body, mode=mode, event=event, trace_id=trace_id)
        self.records.append(record)
        self.records = self.records[-100:]
        return record

    def flush_deferred(self, context: NarrationContext, *, min_pause_ms: int) -> list[InjectionRecord]:
        if context.user_recently_speaking or context.ai_speaking:
            return []
        if str(context.s2s_state or "").lower() in {"listening", "speaking", "thinking"}:
            return []
        if context.user_pause_ms() < min_pause_ms:
            return []
        current = context.now_ms or now_ms()
        flushed: list[InjectionRecord] = []
        remaining: list[tuple[VisionEvent, str, str]] = []
        for event, text, trace_id in self.pending_deferred:
            if event.is_expired(current):
                continue
            record = self.injector.inject(text, mode="deferred_speech", event=event, trace_id=trace_id)
            flushed.append(record)
            self.records.append(record)
        self.pending_deferred = remaining
        self.records = self.records[-100:]
        return flushed


def _mode_from_decision(decision: RouteDecision) -> str:
    if decision.action == "speak_now" or decision.mode == "immediate_speech":
        return "immediate_speech"
    if decision.action in {"defer_until_user_pause", "speak_low_priority"} or decision.mode == "deferred_speech":
        return "deferred_speech"
    return "context"


def _format_system_observation(text: str) -> str:
    body = ensure_sentence_end(str(text or "").strip())
    if body.startswith(SYSTEM_OBSERVATION_PREFIX):
        return ensure_sentence_end(body)
    return ensure_sentence_end(SYSTEM_OBSERVATION_PREFIX + body)
