from __future__ import annotations

import logging
import threading
import time
from dataclasses import dataclass, field
from typing import Any, Callable, Mapping

from .api_client import VisionApiClient
from .frame_provider import VisionFrameProvider
from .logging_utils import log_structured
from .narration_router import NarrationRouter
from .s2s_bridge import S2SNarrationBridge
from .types import NarrationContext, ScreenFrame, VisionEvent, VisionModelOutput, VisionObserverConfig, new_id, now_ms, trim_text


RuntimeStatusFunc = Callable[[], Mapping[str, Any]]


@dataclass
class VisionObserverStatus:
    enabled: bool
    running: bool
    in_flight: bool
    last_event: Mapping[str, Any] | None = None
    last_injection: Mapping[str, Any] | None = None
    last_skip_reason: str = ""
    last_route_action: str = ""


@dataclass
class VisionObserver:
    config: VisionObserverConfig
    frame_provider: VisionFrameProvider
    api_client: VisionApiClient
    router: NarrationRouter
    narration_bridge: S2SNarrationBridge
    runtime_status: RuntimeStatusFunc | None = None
    logger: logging.Logger = field(default_factory=lambda: logging.getLogger(__name__))
    screen_state: dict[str, Any] = field(default_factory=dict)

    def __post_init__(self) -> None:
        self._stop_event = threading.Event()
        self._thread: threading.Thread | None = None
        self._lock = threading.Lock()
        self._in_flight = False
        self._api_call_starts: list[int] = []
        self._latest_frame_id = ""
        self._latest_window_title = ""
        self._last_skip_reason = ""
        self._last_route_action = ""
        self._last_event: VisionEvent | None = None
        self._pending_screen_query_text = ""
        self._pending_screen_query_trace_id = ""
        self._pending_screen_query_until_ms = 0
        self._pending_screen_query_last_event_id = ""
        self._last_companion_force_at_ms = 0

    def start(self) -> None:
        if not self.config.enabled:
            return
        with self._lock:
            if self._thread is not None and self._thread.is_alive():
                return
            self._stop_event.clear()
            self._thread = threading.Thread(target=self._run_loop, name="vision-observer", daemon=True)
            self._thread.start()

    def stop(self, timeout_sec: float = 0.5) -> None:
        self._stop_event.set()
        thread = self._thread
        if thread is not None and thread.is_alive() and thread is not threading.current_thread():
            thread.join(timeout=timeout_sec)

    def status(self) -> VisionObserverStatus:
        with self._lock:
            last = self._last_event
            return VisionObserverStatus(
                enabled=self.config.enabled,
                running=bool(self._thread is not None and self._thread.is_alive()),
                in_flight=self._in_flight,
                last_event=_event_status(last) if last is not None else None,
                last_injection=_injection_status(self.narration_bridge.records[-1]) if self.narration_bridge.records else None,
                last_skip_reason=self._last_skip_reason,
                last_route_action=self._last_route_action,
            )

    def answer_user_screen_query(self, query_text: str, *, trace_id: str | None = None, ttl_ms: int = 7000) -> dict[str, Any]:
        """Queue the latest visual observation when the user explicitly asks about the screen.

        If no safe current observation exists yet, keep a short-lived pending
        request so the next slow-path API result can be injected into S2S.
        """
        trace = trace_id or new_id("screen-query")
        current = now_ms()
        query = trim_text(query_text, 80)
        with self._lock:
            self._pending_screen_query_text = query
            self._pending_screen_query_trace_id = trace
            self._pending_screen_query_until_ms = current + max(1000, int(ttl_ms))
            last = self._last_event
        if last is not None and not last.is_expired(current) and not last.is_degraded and self._event_matches_live_window(last):
            record = self._inject_user_screen_query_answer(last, query, trace, reason="latest_event")
            if record is not None:
                with self._lock:
                    self._pending_screen_query_text = ""
                    self._pending_screen_query_trace_id = ""
                    self._pending_screen_query_until_ms = 0
                    self._pending_screen_query_last_event_id = last.event_id
                return {"ok": True, "queued": record.queued, "event_id": last.event_id, "reason": "latest_event"}
        return {"ok": True, "queued": False, "pending": True, "reason": "waiting_for_vision_result"}

    def tick(self, *, force: bool = False, ignore_diff: bool = False) -> dict[str, Any]:
        if not self.config.enabled:
            return {"ok": True, "skipped": True, "reason": "disabled"}
        self._flush_deferred_speech()
        if self._request_in_flight():
            self._set_skip("api_in_flight")
            return {"ok": True, "skipped": True, "reason": "api_in_flight"}
        companion_force = self._should_force_companion_observation() if not force and not ignore_diff else False
        capture = self.frame_provider.poll(force=force or companion_force, ignore_diff=ignore_diff or companion_force)
        frame = capture.frame
        if capture.skipped or frame is None:
            self._set_skip(capture.reason)
            event_name = "vision.diff.skip" if capture.reason == "diff_below_threshold" else "vision.capture"
            log_structured(
                self.logger,
                event_name,
                trace_id=capture.trace_id,
                frame_id="",
                latency_ms=capture.latency_ms,
                reason=capture.reason,
                priority=None,
                scene="",
            )
            return {"ok": True, "skipped": True, "reason": capture.reason}

        with self._lock:
            self._latest_frame_id = frame.frame_id
            self._latest_window_title = frame.window_title
        log_structured(
            self.logger,
            "vision.capture",
            trace_id=capture.trace_id,
            frame_id=frame.frame_id,
            latency_ms=capture.latency_ms,
            reason="captured",
            priority=None,
            scene="",
            diff_ratio=round(frame.diff_ratio, 5),
            window_title=frame.window_title,
            process_name=frame.process_name,
        )
        self._fast_path(frame, capture.trace_id)
        if not self._rate_limit_allows(frame.captured_at_ms):
            self._set_skip("rate_limited")
            return {"ok": True, "skipped": True, "reason": "rate_limited", "frame_id": frame.frame_id}
        self._start_slow_path(frame, capture.trace_id)
        return {"ok": True, "queued": True, "frame_id": frame.frame_id}

    def process_slow_result(self, frame: ScreenFrame, output: VisionModelOutput, *, trace_id: str | None = None) -> dict[str, Any]:
        trace = trace_id or new_id("vision")
        started_ms = now_ms()
        event = VisionEvent.from_model_output(output, frame, ttl_ms=self.config.event_ttl_ms, created_at_ms=started_ms)
        drop_reason = self._drop_reason_for_slow_result(frame, event)
        if drop_reason:
            log_structured(
                self.logger,
                "vision.event.dropped",
                trace_id=trace,
                frame_id=frame.frame_id,
                latency_ms=event.raw_latency_ms,
                route_action="drop",
                priority=event.priority,
                scene=event.scene,
                reason=drop_reason,
            )
            return {"ok": True, "dropped": True, "reason": drop_reason, "event_id": event.event_id}
        with self._lock:
            self._last_event = event
            self.screen_state.update(
                {
                    "scene": event.scene,
                    "summary": event.summary,
                    "window_title": event.window_title,
                    "process_name": event.process_name,
                    "last_event_at_ms": event.created_at_ms,
                    "priority": event.priority,
                    "speak_policy": event.speak_policy,
                }
            )
        log_structured(
            self.logger,
            "vision.event.created",
            trace_id=trace,
            frame_id=frame.frame_id,
            latency_ms=event.raw_latency_ms,
            priority=event.priority,
            scene=event.scene,
            reason="api_result",
        )
        pending_query = self._consume_pending_screen_query(event)
        if pending_query is not None:
            query_text, query_trace = pending_query
            record = self._inject_user_screen_query_answer(event, query_text, query_trace or trace, reason="slow_result")
            with self._lock:
                self._last_route_action = "answer_user_query"
            log_structured(
                self.logger,
                "narration.route",
                trace_id=query_trace or trace,
                frame_id=frame.frame_id,
                latency_ms=event.raw_latency_ms,
                route_action="answer_user_query",
                priority=event.priority,
                scene=event.scene,
                reason="user_screen_query",
            )
            return {
                "ok": True,
                "event_id": event.event_id,
                "route_action": "answer_user_query",
                "injected": record is not None,
            }
        companion_record = self._maybe_inject_companion_play_event(event, trace)
        if companion_record is not None:
            with self._lock:
                self._last_route_action = "companion_play_report"
            log_structured(
                self.logger,
                "narration.route",
                trace_id=companion_record.trace_id,
                frame_id=frame.frame_id,
                latency_ms=event.raw_latency_ms,
                route_action="companion_play_report",
                priority=event.priority,
                scene=event.scene,
                reason="companion_play_mode",
            )
            log_structured(
                self.logger,
                "s2s.speech.inject",
                trace_id=companion_record.trace_id,
                frame_id=frame.frame_id,
                latency_ms=event.raw_latency_ms,
                route_action="companion_play_report",
                priority=event.priority,
                scene=event.scene,
                reason="companion_play_mode",
                mode=companion_record.mode,
                queued=companion_record.queued,
            )
            return {
                "ok": True,
                "event_id": event.event_id,
                "route_action": "companion_play_report",
                "injected": True,
            }
        context = self._narration_context()
        decision = self.router.route(event, context)
        with self._lock:
            self._last_route_action = decision.action
        log_structured(
            self.logger,
            "narration.route",
            trace_id=decision.trace_id,
            frame_id=frame.frame_id,
            latency_ms=event.raw_latency_ms,
            route_action=decision.action,
            priority=event.priority,
            scene=event.scene,
            reason=decision.reason,
        )
        record = self.narration_bridge.handle(event, decision)
        if record is not None:
            log_structured(
                self.logger,
                "s2s.speech.inject" if record.mode != "context" else "s2s.context.inject",
                trace_id=record.trace_id,
                frame_id=frame.frame_id,
                latency_ms=event.raw_latency_ms,
                route_action=decision.action,
                priority=event.priority,
                scene=event.scene,
                reason=decision.reason,
                mode=record.mode,
                queued=record.queued,
            )
        return {
            "ok": True,
            "event_id": event.event_id,
            "route_action": decision.action,
            "injected": record is not None,
        }

    def _run_loop(self) -> None:
        tick_sec = max(0.05, self.config.loop_tick_ms / 1000.0)
        while not self._stop_event.wait(tick_sec):
            try:
                self.tick()
            except Exception:
                self.logger.exception("vision_observer_tick_failed")

    def _fast_path(self, frame: ScreenFrame, trace_id: str) -> None:
        with self._lock:
            previous_title = str(self.screen_state.get("window_title") or "")
            self.screen_state.update(
                {
                    "window_title": frame.window_title,
                    "process_name": frame.process_name,
                    "last_frame_id": frame.frame_id,
                    "last_diff_ratio": frame.diff_ratio,
                    "fast_path_at_ms": frame.captured_at_ms,
                }
            )
        reason = "window_changed" if previous_title and previous_title != frame.window_title else "frame_changed"
        log_structured(
            self.logger,
            "vision.fast.context",
            trace_id=trace_id,
            frame_id=frame.frame_id,
            latency_ms=0.0,
            route_action="update_context_only",
            priority=0.2,
            scene=str(self.screen_state.get("scene") or ""),
            reason=reason,
        )

    def _start_slow_path(self, frame: ScreenFrame, trace_id: str) -> None:
        with self._lock:
            if self._in_flight:
                return
            self._in_flight = True
        log_structured(
            self.logger,
            "vision.api.start",
            trace_id=trace_id,
            frame_id=frame.frame_id,
            latency_ms=0.0,
            reason="slow_path",
            priority=None,
            scene=str(self.screen_state.get("scene") or ""),
        )

        def worker() -> None:
            try:
                output = self.api_client.analyze(frame, dict(self.screen_state))
                log_structured(
                    self.logger,
                    "vision.api.timeout" if output.is_degraded and output.degraded_reason == "timeout" else "vision.api.success",
                    trace_id=trace_id,
                    frame_id=frame.frame_id,
                    latency_ms=output.raw_latency_ms,
                    priority=output.priority,
                    scene=output.scene,
                    reason=output.degraded_reason if output.is_degraded else "ok",
                )
                self.process_slow_result(frame, output, trace_id=trace_id)
            except Exception:
                self.logger.exception("vision_api_worker_failed frame_id=%s", frame.frame_id)
            finally:
                with self._lock:
                    self._in_flight = False

        thread = threading.Thread(target=worker, name=f"vision-api-{frame.frame_id}", daemon=True)
        thread.start()

    def _flush_deferred_speech(self) -> None:
        context = self._narration_context()
        records = self.narration_bridge.flush_deferred(context, min_pause_ms=self.config.user_pause_ms)
        for record in records:
            log_structured(
                self.logger,
                "s2s.speech.inject",
                trace_id=record.trace_id,
                frame_id="",
                latency_ms=0.0,
                route_action="deferred_speech",
                priority=None,
                scene=str(self.screen_state.get("scene") or ""),
                reason="user_pause_flush",
                mode=record.mode,
                queued=record.queued,
            )

    def _request_in_flight(self) -> bool:
        with self._lock:
            return self._in_flight

    def _should_force_companion_observation(self) -> bool:
        if not self.config.companion_play_mode:
            return False
        context = self._narration_context()
        if str(context.current_task_state or "").strip().lower() != "voice_active":
            return False
        current = context.now_ms or now_ms()
        interval = max(1000, int(self.config.companion_force_interval_ms))
        with self._lock:
            if self._last_companion_force_at_ms and current - self._last_companion_force_at_ms < interval:
                return False
            self._last_companion_force_at_ms = current
        return True

    def _maybe_inject_companion_play_event(self, event: VisionEvent, trace_id: str) -> Any:
        if not self.config.companion_play_mode or event.is_degraded:
            return None
        context = self._narration_context()
        decision = self.router.route_companion_report(
            event,
            context,
            min_priority=self.config.companion_min_priority,
            report_cooldown_ms=self.config.companion_report_cooldown_ms,
        )
        if decision.action not in {"speak_low_priority", "speak_now"}:
            return None
        text = _format_companion_play_context(event, max_chars=self.config.max_context_chars)
        if not text:
            return None
        mode = str(self.config.companion_inject_mode or "deferred_speech")
        if decision.mode == "immediate_speech":
            mode = "immediate_speech"
        if mode not in {"deferred_speech", "immediate_speech"}:
            mode = "deferred_speech"
        return self.narration_bridge.inject_text(event, text, mode=mode, trace_id=decision.trace_id or trace_id or new_id("companion"))

    def _consume_pending_screen_query(self, event: VisionEvent) -> tuple[str, str] | None:
        if event.is_degraded:
            return None
        current = now_ms()
        if event.is_expired(current):
            return None
        with self._lock:
            if not self._pending_screen_query_text or current > self._pending_screen_query_until_ms:
                self._pending_screen_query_text = ""
                self._pending_screen_query_trace_id = ""
                self._pending_screen_query_until_ms = 0
                return None
            if self._pending_screen_query_last_event_id == event.event_id:
                return None
            query_text = self._pending_screen_query_text
            query_trace = self._pending_screen_query_trace_id
            self._pending_screen_query_text = ""
            self._pending_screen_query_trace_id = ""
            self._pending_screen_query_until_ms = 0
            self._pending_screen_query_last_event_id = event.event_id
        return query_text, query_trace

    def _inject_user_screen_query_answer(self, event: VisionEvent, query_text: str, trace_id: str, *, reason: str) -> Any:
        text = _format_user_screen_query_context(event, query_text, max_chars=self.config.max_context_chars)
        record = self.narration_bridge.inject_text(event, text, mode="deferred_speech", trace_id=trace_id)
        if record is not None:
            log_structured(
                self.logger,
                "s2s.speech.inject",
                trace_id=record.trace_id,
                frame_id=event.frame_id,
                latency_ms=event.raw_latency_ms,
                route_action="answer_user_query",
                priority=event.priority,
                scene=event.scene,
                reason=f"user_screen_query_{reason}",
                mode=record.mode,
                queued=record.queued,
            )
        return record

    def _event_matches_live_window(self, event: VisionEvent) -> bool:
        try:
            live_window = self.frame_provider.current_window_info()
        except Exception:
            return True
        if live_window.title and event.window_title and live_window.title != event.window_title:
            return False
        return True

    def _rate_limit_allows(self, current_ms: int) -> bool:
        limit = max(1, int(self.config.max_calls_per_minute))
        cutoff = current_ms - 60_000
        self._api_call_starts = [item for item in self._api_call_starts if item >= cutoff]
        if len(self._api_call_starts) >= limit:
            return False
        self._api_call_starts.append(current_ms)
        return True

    def _drop_reason_for_slow_result(self, frame: ScreenFrame, event: VisionEvent) -> str:
        current = now_ms()
        if event.is_expired(current):
            return "event_expired"
        with self._lock:
            latest_frame_id = self._latest_frame_id
            latest_title = self._latest_window_title
        if latest_frame_id and frame.frame_id != latest_frame_id:
            return "stale_frame"
        if latest_title and frame.window_title and frame.window_title != latest_title:
            return "window_changed"
        live_window = self.frame_provider.current_window_info()
        if live_window.title and frame.window_title and live_window.title != frame.window_title:
            return "window_changed"
        return ""

    def _narration_context(self) -> NarrationContext:
        status = self.runtime_status() if self.runtime_status is not None else {}
        current = now_ms()
        s2s_state = str(status.get("current_state") or "idle")
        ai_speaking = bool(status.get("audio_active")) or s2s_state in {"speaking", "thinking"}
        recent_user_at = int(status.get("last_user_speech_at_ms") or 0)
        return NarrationContext(
            s2s_state=s2s_state,
            user_recently_speaking=s2s_state == "listening",
            ai_speaking=ai_speaking,
            last_user_speech_at_ms=recent_user_at,
            last_active_speech_at_ms=int(status.get("last_active_speech_at_ms") or 0),
            current_task_state=str(status.get("task_state") or ""),
            screen_state=dict(self.screen_state),
            now_ms=current,
        )

    def _set_skip(self, reason: str) -> None:
        with self._lock:
            self._last_skip_reason = reason


def _event_status(event: VisionEvent) -> Mapping[str, Any]:
    return {
        "event_id": event.event_id,
        "frame_id": event.frame_id,
        "scene": event.scene,
        "summary": event.summary,
        "important_text": list(event.important_text),
        "ui_targets": [
            {"name": item.name, "reason": item.reason, "confidence": item.confidence}
            for item in event.ui_targets
        ],
        "user_state_guess": event.user_state_guess,
        "risk": event.risk,
        "priority": event.priority,
        "speak_policy": event.speak_policy,
        "suggested_speech": event.suggested_speech,
        "confidence": event.confidence,
        "created_at_ms": event.created_at_ms,
        "window_title": event.window_title,
        "process_name": event.process_name,
        "is_degraded": event.is_degraded,
    }


def _injection_status(record: Any) -> Mapping[str, Any]:
    raw = record.raw_result if isinstance(record.raw_result, Mapping) else {}
    result: dict[str, Any] = {
        "trace_id": record.trace_id,
        "event_id": record.event_id,
        "mode": record.mode,
        "role": record.role,
        "queued": bool(record.queued),
        "text": trim_text(record.text, 80),
    }
    for key in ("ok", "accepted", "sent_to_godot", "voice", "error"):
        if key in raw:
            result[key] = raw.get(key)
    result["tts_provider"] = raw.get("tts_provider", "")
    tts = raw.get("tts") if isinstance(raw, Mapping) else None
    if isinstance(tts, Mapping):
        result["tts"] = {
            key: tts.get(key)
            for key in ("queued", "ready")
            if key in tts
        }
    return result


def _format_user_screen_query_context(event: VisionEvent, query_text: str, *, max_chars: int) -> str:
    limit = min(max(120, int(max_chars or 180)), 220)
    if event.suggested_speech:
        return trim_text(event.suggested_speech, 48)
    parts: list[str] = []
    scene = _scene_label(event.scene)
    if scene:
        parts.append(f"{scene}：")
    summary = event.summary or event.user_state_guess or "视觉模型暂时没有判断出主要内容"
    parts.append(trim_text(summary, 90))
    important = [trim_text(item, 18) for item in event.important_text[:3] if str(item).strip()]
    if important:
        parts.append("。关键文字：" + "、".join(important))
    target = next((item for item in event.ui_targets if item.name and item.confidence >= 0.45), None)
    if target is not None:
        parts.append(f"。重点：{trim_text(target.name, 24)}")
    return trim_text("".join(parts), limit)


def _scene_label(scene: str) -> str:
    mapping = {
        "game": "游戏画面",
        "document": "文档窗口",
        "browser": "浏览器页面",
        "chat": "聊天窗口",
        "video": "视频画面",
        "desktop": "桌面",
        "code": "代码窗口",
    }
    return mapping.get(str(scene or "").lower(), "")


def _format_companion_play_context(event: VisionEvent, *, max_chars: int) -> str:
    if event.suggested_speech:
        return trim_text(event.suggested_speech, 48)
    limit = min(max(60, int(max_chars or 120)), 120)
    scene = _scene_label(event.scene)
    parts: list[str] = []
    if scene:
        parts.append(f"{scene}，")
    summary = event.summary or event.user_state_guess
    if not summary:
        return ""
    parts.append(trim_text(summary, 56))
    target = next((item for item in event.ui_targets if item.name and item.confidence >= 0.45), None)
    if target is not None:
        parts.append(f"，盯一下{trim_text(target.name, 12)}")
    return trim_text("".join(parts), limit)
