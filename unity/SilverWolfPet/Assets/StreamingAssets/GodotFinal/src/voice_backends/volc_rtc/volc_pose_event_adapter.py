from __future__ import annotations

import json
import logging
import queue
import re
import threading
import time
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Mapping

from src.pet_emotion import EmotionDirector
from src.pet_pose_bridge import GodotPoseClient, PoseCommand, PosePresentationClient, PoseRouter
from src.pet_pose_bridge.pose_command import PoseCommandError
from src.pet_pose_bridge.volc_function_call_adapter import VolcFunctionCallAdapter
from src.spoken_text_sanitizer import sanitize_spoken_text


LOGGER = logging.getLogger(__name__)
_RAW_EVENT_LOG_MAX_BYTES = 16 * 1024 * 1024
_AI_SUBTITLE_FRAGMENT_BUFFERED = object()
_AI_SUBTITLE_STABLE_END_CHARS = ("\u3002", "\uff01", "\uff1f", ".", "!", "?", "~")
_AI_SUBTITLE_DANGLING_STRIP_CHARS = " \t\r\n\uff0c,\u3001\uff1a:\uff1b;"
_AI_SUBTITLE_END_CHARS = ("。", "！", "？", ".", "!", "?", "…", "~")
_AI_SUBTITLE_DANGLING_CHARS = ("，", ",", "、", "：", ":", "；", ";")

STATE_ALIASES = {
    "user_speaking": "listening",
    "user speaking": "listening",
    "user_speech_start": "listening",
    "user speech start": "listening",
    "user_listening": "listening",
    "user listening": "listening",
    "speech_start": "listening",
    "listening": "listening",
    "listen": "listening",
    "processing": "thinking",
    "processed": "thinking",
    "ai_processing": "thinking",
    "ai processing": "thinking",
    "thinking": "thinking",
    "ai_thinking": "thinking",
    "ai thinking": "thinking",
    "llm_running": "thinking",
    "llm running": "thinking",
    "speaking": "speaking",
    "ai_speaking": "speaking",
    "ai speaking": "speaking",
    "answering": "speaking",
    "answer": "speaking",
    "tts_start": "speaking",
    "tts_playing": "speaking",
    "tts playing": "speaking",
    "answer_start": "speaking",
    "response_audio_start": "speaking",
    "response audio start": "speaking",
    "idle": "idle",
    "completed": "idle",
    "complete": "idle",
    "finished": "idle",
    "finish": "idle",
    "response_finished": "idle",
    "response finished": "idle",
    "answer_finish": "idle",
    "answerfinished": "idle",
    "answer_fin": "idle",
    "tts_end": "idle",
    "tts_finished": "idle",
    "tts finished": "idle",
    "interrupted": "interrupted",
    "interrupt": "interrupted",
    "barge_in": "interrupted",
    "user_barge_in": "interrupted",
    "user barge in": "interrupted",
}

STAGE_CODE_ALIASES = {
    # Volc AI state binary-message Stage.Code values.
    1: "listening",
    2: "thinking",
    3: "speaking",
    4: "interrupted",
    5: "idle",
}

POSE_BY_STATE = {
    "listening": {
        "state": "listening",
        "emotion": "neutral",
        "gesture": "none",
        "posture": "stand",
        "mouth": "none",
    },
    "thinking": {
        "state": "thinking",
        "emotion": "neutral",
        "gesture": "think",
        "posture": "stand",
        "mouth": "none",
    },
    "speaking": {
        "state": "speaking",
        "emotion": "neutral",
        "gesture": "none",
        "posture": "stand",
        "mouth": "audio_volume",
    },
    "idle": {
        "state": "idle",
        "emotion": "neutral",
        "gesture": "none",
        "posture": "stand",
        "mouth": "none",
    },
    "interrupted": {
        "state": "interrupted",
        "emotion": "surprised",
        "gesture": "none",
        "posture": "stand",
        "mouth": "none",
    },
}

SUBTITLE_SPEAKER_ALIASES = {
    "ai": "ai",
    "assistant": "ai",
    "bot": "ai",
    "agent": "ai",
    "llm": "ai",
    "user": "user",
    "human": "user",
    "local": "user",
}


@dataclass(frozen=True)
class StartVoiceChatConfigIssue:
    key: str
    message: str
    severity: str = "warning"


@dataclass(frozen=True)
class _QueuedPose:
    trace_id: str
    payload: dict[str, Any]
    source: str
    created_at: float
    event_type: str = ""
    raw_payload: Any = None
    mapping_result: Any = None
    event_received_at: float = 0.0
    pose_generated_at: float = 0.0
    attempted_send: bool = True
    error: str | None = None


class AsyncPresentationPoseSender:
    """Background TCP sender so SDK callback threads never block on the pet UI."""

    def __init__(
        self,
        client: PosePresentationClient | None = None,
        *,
        max_queue_size: int = 256,
        logger: logging.Logger | None = None,
        on_send: Callable[[_QueuedPose, bool], None] | None = None,
        raw_event_log_path: str | Path = "logs/volc_pose_raw_events.jsonl",
    ) -> None:
        self.client = client or GodotPoseClient()
        self.logger = logger or LOGGER
        self.on_send = on_send
        self.raw_event_log_path = Path(raw_event_log_path)
        self._raw_log_lock = threading.Lock()
        self._last_audio_raw_log_at = 0.0
        self._queue: queue.Queue[_QueuedPose | None] = queue.Queue(maxsize=max_queue_size)
        self._thread = threading.Thread(
            target=self._run,
            name="volc-presentation-pose-sender",
            daemon=True,
        )
        self._started = False
        self._closed = threading.Event()

    def start(self) -> None:
        if not self._started:
            self._thread.start()
            self._started = True

    def close(self, timeout_sec: float = 0.5) -> None:
        self._closed.set()
        try:
            self._queue.put_nowait(None)
        except queue.Full:
            pass
        if self._started:
            self._thread.join(timeout=timeout_sec)

    def enqueue(self, item: _QueuedPose) -> bool:
        self.start()
        try:
            self._queue.put_nowait(item)
            return True
        except queue.Full:
            self.logger.warning(
                "volc_pose_queue_full trace_id=%s source=%s dropped=1",
                item.trace_id,
                item.source,
            )
            return False

    def write_event_record(
        self,
        *,
        trace_id: str,
        event_type: str,
        source: str,
        raw_payload: Any,
        mapping_result: Any,
        send_to_godot: bool = False,
        error: str | None = None,
        event_received_at: float = 0.0,
        pose_generated_at: float = 0.0,
    ) -> None:
        self._write_raw_event_log(
            _QueuedPose(
                trace_id=trace_id,
                payload={},
                source=source,
                created_at=time.monotonic(),
                event_type=event_type,
                raw_payload=_json_safe(raw_payload),
                mapping_result=_json_safe(mapping_result),
                event_received_at=event_received_at,
                pose_generated_at=pose_generated_at,
                attempted_send=send_to_godot,
                error=error,
            ),
            send_to_godot,
        )

    def _run(self) -> None:
        while not self._closed.is_set():
            item = self._queue.get()
            if item is None:
                return
            ok = False
            try:
                ok = self.client.send_pose(item.payload)
            except Exception:
                self.logger.exception(
                    "presentation_pose_send_exception trace_id=%s source=%s",
                    item.trace_id,
                    item.source,
                )
            if not ok:
                self.logger.debug(
                    "presentation_pose_offline trace_id=%s source=%s dropped=1",
                    item.trace_id,
                    item.source,
                )
            if self.on_send is not None:
                self.on_send(item, ok)
            self._write_raw_event_log(item, ok)

    def _write_raw_event_log(self, item: _QueuedPose, ok: bool) -> None:
        if not self.raw_event_log_path:
            return
        now = time.time()
        if item.source == "remote_audio_volume:ai":
            if now - self._last_audio_raw_log_at < 0.75:
                return
            self._last_audio_raw_log_at = now
        record = {
            "timestamp": now,
            "trace_id": item.trace_id,
            "event_type": item.event_type or item.source,
            "source": item.source,
            "raw_event": item.raw_payload,
            "mapped_pose_command": item.mapping_result,
            "sent_to_godot": ok,
            "error": None if ok else (item.error if item.error is not None else ("godot_send_failed_or_offline" if item.attempted_send else None)),
            "raw_payload": item.raw_payload,
            "adapter_mapping_result": item.mapping_result,
            "send_to_godot": ok,
            "sent_to_presentation": ok,
            "timing": {
                "event_received_at": item.event_received_at,
                "pose_generated_at": item.pose_generated_at,
                "send_finished_at": now,
                "event_to_pose_ms": _elapsed_ms(item.event_received_at, item.pose_generated_at),
                "pose_to_send_ms": _elapsed_ms(item.pose_generated_at, now),
                "event_to_send_ms": _elapsed_ms(item.event_received_at, now),
            },
        }
        try:
            self.raw_event_log_path.parent.mkdir(parents=True, exist_ok=True)
            line = json.dumps(record, ensure_ascii=False, separators=(",", ":"))
            with self._raw_log_lock:
                self._rotate_raw_event_log_if_needed()
                with self.raw_event_log_path.open("a", encoding="utf-8") as file:
                    file.write(line + "\n")
        except OSError:
            self.logger.exception("volc_pose_raw_log_write_failed path=%s", self.raw_event_log_path)

    def _rotate_raw_event_log_if_needed(self) -> None:
        try:
            if (
                not self.raw_event_log_path.exists()
                or self.raw_event_log_path.stat().st_size < _RAW_EVENT_LOG_MAX_BYTES
            ):
                return
            backup_path = self.raw_event_log_path.with_name(self.raw_event_log_path.name + ".1")
            if backup_path.exists():
                backup_path.unlink()
            self.raw_event_log_path.replace(backup_path)
            self.logger.info(
                "volc_pose_raw_log_rotated path=%s backup=%s",
                self.raw_event_log_path,
                backup_path,
            )
        except OSError:
            self.logger.exception("volc_pose_raw_log_rotate_failed path=%s", self.raw_event_log_path)


class VolcPoseEventAdapter:
    """Maps real Volc RTC voice events to existing local PoseBridge commands."""

    def __init__(
        self,
        *,
        godot_client: PosePresentationClient | None = None,
        pose_router: PoseRouter | None = None,
        function_call_adapter: VolcFunctionCallAdapter | None = None,
        emotion_director: EmotionDirector | None = None,
        bot_uids: set[str] | None = None,
        state_debounce_ms: int = 100,
        subtitle_throttle_ms: int = 150,
        volume_fps: float = 15.0,
        speaking_min_hold_ms: int = 400,
        thinking_min_hold_ms: int = 300,
        idle_delay_ms: int = 650,
        audio_start_threshold: float = 0.03,
        audio_stop_threshold: float = 0.016,
        audio_silence_timeout_ms: int = 180,
        volume_attack: float = 0.65,
        volume_release: float = 0.34,
        raw_event_log_path: str | Path = "logs/volc_pose_raw_events.jsonl",
        logger: logging.Logger | None = None,
        on_send: Callable[[_QueuedPose, bool], None] | None = None,
        on_user_vision_intent: Callable[[str], None] | None = None,
        on_user_voice_stop_intent: Callable[[str], None] | None = None,
        on_user_speech_activity: Callable[[str, bool], None] | None = None,
    ) -> None:
        self.logger = logger or LOGGER
        self.pose_router = pose_router or PoseRouter()
        self.function_call_adapter = function_call_adapter or VolcFunctionCallAdapter()
        self.emotion_director = emotion_director or EmotionDirector()
        self.sender = AsyncPresentationPoseSender(
            client=godot_client,
            logger=self.logger,
            on_send=on_send,
            raw_event_log_path=raw_event_log_path,
        )
        self.on_user_vision_intent = on_user_vision_intent
        self.on_user_voice_stop_intent = on_user_voice_stop_intent
        self.on_user_speech_activity = on_user_speech_activity
        self.bot_uids = {str(uid) for uid in (bot_uids or set())}
        self.state_debounce_sec = max(0.0, state_debounce_ms / 1000.0)
        self.subtitle_throttle_sec = max(0.05, subtitle_throttle_ms / 1000.0)
        self.volume_interval_sec = 1.0 / max(volume_fps, 1.0)
        self._last_state_key = ""
        self._last_state_sent_at = 0.0
        self._current_state_key = ""
        self._current_state_sent_at = 0.0
        self._state_min_holds = {
            "speaking": max(0.0, speaking_min_hold_ms / 1000.0),
            "thinking": max(0.0, thinking_min_hold_ms / 1000.0),
        }
        self._idle_delay_sec = max(0.0, idle_delay_ms / 1000.0)
        self._state_timer_lock = threading.Lock()
        self._state_timer: threading.Timer | None = None
        self._subtitle_lock = threading.Lock()
        self._subtitle_timer: threading.Timer | None = None
        self._subtitle_stream_timers: list[threading.Timer] = []
        self._subtitle_fragment_lock = threading.Lock()
        self._subtitle_fragment_buffers: dict[str, dict[str, Any]] = {}
        self._subtitle_fragment_timers: dict[str, threading.Timer] = {}
        self.subtitle_fragment_debounce_sec = 0.9
        self._pending_ai_subtitle = ""
        self._last_subtitle_sent_at = 0.0
        self._last_ai_subtitle_text = ""
        self._recent_user_subtitle = ""
        self._recent_user_subtitle_at = 0.0
        self._recent_user_final_subtitle_at = 0.0
        self._recent_user_pose_intent_until = 0.0
        self._recent_user_pose_intent_text = ""
        self._last_user_vision_intent_at = 0.0
        self._last_user_vision_intent_text = ""
        self._user_vision_intent_cooldown_sec = 5.0
        self._last_user_priority_sent_at = 0.0
        self._ignore_ai_subtitles_until = 0.0
        self._ignore_ai_audio_until = 0.0
        self._last_volume_sent_at = 0.0
        self._smoothed_mouth_open = 0.0
        self._volume_attack = max(0.0, min(1.0, volume_attack))
        self._volume_release = max(0.0, min(1.0, volume_release))
        self._audio_start_threshold = max(0.0, min(1.0, audio_start_threshold))
        self._audio_stop_threshold = max(0.0, min(1.0, audio_stop_threshold))
        self._audio_silence_timeout_sec = max(0.05, audio_silence_timeout_ms / 1000.0)
        self._audio_gate_active = False
        self._audio_gate_lock = threading.Lock()
        self._audio_stop_timer: threading.Timer | None = None

    def close(self) -> None:
        self.reset_runtime_activity()
        self.sender.close()

    def reset_runtime_activity(self) -> None:
        """Clear timers and transient subtitle/audio state when a voice session stops."""
        if self._state_timer is not None:
            self._state_timer.cancel()
            self._state_timer = None
        if self._subtitle_timer is not None:
            self._subtitle_timer.cancel()
            self._subtitle_timer = None
        for timer in self._subtitle_stream_timers:
            timer.cancel()
        self._subtitle_stream_timers.clear()
        with self._subtitle_fragment_lock:
            for timer in self._subtitle_fragment_timers.values():
                timer.cancel()
            self._subtitle_fragment_timers.clear()
            self._subtitle_fragment_buffers.clear()
        if self._audio_stop_timer is not None:
            self._audio_stop_timer.cancel()
            self._audio_stop_timer = None
        with self._subtitle_lock:
            self._pending_ai_subtitle = ""
            self._last_ai_subtitle_text = ""
        with self._audio_gate_lock:
            self._audio_gate_active = False
            self._smoothed_mouth_open = 0.0
        self._last_state_key = ""
        self._current_state_key = ""
        self._last_user_vision_intent_at = 0.0
        self._last_user_vision_intent_text = ""
        self._last_user_priority_sent_at = 0.0
        self._ignore_ai_subtitles_until = 0.0
        self._ignore_ai_audio_until = 0.0
        self._recent_user_final_subtitle_at = 0.0

    def runtime_status(self) -> dict[str, Any]:
        """Return lightweight runtime state for local schedulers.

        This is intentionally semantic-only. Callers use it to avoid asking the
        cloud model to speak while the current answer is still playing.
        """
        now = time.monotonic()
        with self._audio_gate_lock:
            audio_active = self._audio_gate_active
            mouth_open = _shape_mouth_open(self._smoothed_mouth_open)
        with self._subtitle_lock:
            subtitle_pending = self._subtitle_pending_locked()
        return {
            "current_state": self._current_state_key or "idle",
            "current_state_age_sec": max(0.0, now - self._current_state_sent_at) if self._current_state_sent_at else 0.0,
            "audio_active": audio_active,
            "mouth_open": round(mouth_open, 4),
            "subtitle_pending": subtitle_pending,
        }

    def _subtitle_pending_locked(self) -> bool:
        if self._subtitle_timer is not None and not self._subtitle_timer.is_alive():
            self._subtitle_timer = None
        if self._subtitle_stream_timers:
            self._subtitle_stream_timers = [
                timer for timer in self._subtitle_stream_timers if timer.is_alive()
            ]
        return bool(
            self._pending_ai_subtitle
            or self._subtitle_timer is not None
            or self._subtitle_stream_timers
        )

    def _cancel_ai_output_for_user_priority(self) -> None:
        with self._subtitle_lock:
            self._pending_ai_subtitle = ""
            if self._subtitle_timer is not None:
                self._subtitle_timer.cancel()
                self._subtitle_timer = None
            for timer in self._subtitle_stream_timers:
                timer.cancel()
            self._subtitle_stream_timers.clear()
        with self._audio_gate_lock:
            if self._audio_stop_timer is not None:
                self._audio_stop_timer.cancel()
                self._audio_stop_timer = None
            self._audio_gate_active = False
            self._smoothed_mouth_open = 0.0

    def _dispatch_user_speech_priority(
        self,
        text: str,
        *,
        is_final: bool,
        trace_id: str,
        raw_event: Mapping[str, Any],
        event_received_at: float,
    ) -> bool:
        now = time.monotonic()
        stale_hold_sec = 0.35 if is_final else 1.25
        if is_final:
            self._recent_user_final_subtitle_at = now
        else:
            self._recent_user_final_subtitle_at = 0.0
        self._ignore_ai_subtitles_until = max(self._ignore_ai_subtitles_until, now + stale_hold_sec)
        self._ignore_ai_audio_until = max(self._ignore_ai_audio_until, now + stale_hold_sec)
        if now - self._last_user_priority_sent_at < 0.25:
            return False
        self._last_user_priority_sent_at = now
        self._last_volume_sent_at = now
        payload = {
            "type": "pet_pose",
            "state": "listening",
            "emotion": "neutral",
            "gesture": "none",
            "posture": "stand",
            "mouth": "none",
            "mouth_open": 0.0,
            "audio_active": False,
            "overlay_only": True,
            "clear_bubble": True,
            "priority": 100,
            "interruptible": False,
        }
        return self._enqueue_payload(
            payload,
            trace_id=trace_id,
            source="subtitle:user_priority",
            event_type="subtitle_event",
            raw_payload=raw_event,
            mapping_result={
                "user_priority": True,
                "is_final": is_final,
                "text": _short_text(text, 80),
            },
            event_received_at=event_received_at,
        )

    def _notify_user_speech_activity(self, text: str, is_final: bool, trace_id: str) -> None:
        if self.on_user_speech_activity is None:
            return
        try:
            self.on_user_speech_activity(text, is_final)
        except Exception:
            self.logger.exception("user_speech_activity_callback_failed trace_id=%s", trace_id)

    def _ai_output_blocked_by_user_priority(self) -> bool:
        now = time.monotonic()
        return now < self._ignore_ai_subtitles_until or now < self._ignore_ai_audio_until

    def _release_user_priority_for_ai_answer(self) -> bool:
        now = time.monotonic()
        if self._recent_user_final_subtitle_at <= 0.0:
            return False
        if now - self._recent_user_final_subtitle_at > 20.0:
            return False
        self._ignore_ai_subtitles_until = 0.0
        self._ignore_ai_audio_until = 0.0
        return True

    def on_volc_ai_state_event(self, event: Mapping[str, Any] | str) -> PoseCommand | None:
        event_received_at = time.time()
        trace_id = _trace_id(event, "state")
        state = _normalize_ai_state(_extract_ai_state_value(event))
        if not state:
            if isinstance(event, Mapping) and _event_error_message(event):
                self._dispatch_cloud_error_event(
                    event,
                    trace_id=trace_id,
                    event_received_at=event_received_at,
                )
                return None
            self.logger.warning("volc_ai_state_unknown trace_id=%s event=%s", trace_id, event)
            self.sender.write_event_record(
                trace_id=trace_id,
                event_type="ai_state_event",
                source="ai_state:unknown",
                raw_payload=event,
                mapping_result={"error": "unknown_state"},
                send_to_godot=False,
                error="unknown_state",
                event_received_at=event_received_at,
                pose_generated_at=time.time(),
            )
            return None

        if (
            state == "speaking"
            and self._ai_output_blocked_by_user_priority()
            and not self._release_user_priority_for_ai_answer()
        ):
            self.sender.write_event_record(
                trace_id=trace_id,
                event_type="ai_state_event",
                source="ai_state:speaking_ignored_user_priority",
                raw_payload=event,
                mapping_result={"ignored": True, "reason": "user_speech_priority"},
                send_to_godot=False,
                event_received_at=event_received_at,
                pose_generated_at=time.time(),
            )
            return None

        return self._dispatch_state_event(
            state,
            trace_id=trace_id,
            raw_event=event,
            event_received_at=event_received_at,
        )

    def on_ai_state_event(self, event: Mapping[str, Any] | str) -> PoseCommand | None:
        return self.on_volc_ai_state_event(event)

    def on_volc_subtitle_event(self, event: Mapping[str, Any]) -> PoseCommand | None:
        event_received_at = time.time()
        trace_id = _trace_id(event, "subtitle")
        speaker = self._resolve_subtitle_speaker(event)
        text = str(_pick_value(event, "text", "content", "subtitle", "transcript", "utterance") or "")
        if not text:
            return None
        is_final = bool(_pick_value(event, "is_final", "final", "completed", "definite") or False)
        if speaker == "user":
            self._recent_user_subtitle = text
            self._recent_user_subtitle_at = time.monotonic()
            self._cancel_ai_output_for_user_priority()
            user_priority_sent = self._dispatch_user_speech_priority(
                text,
                is_final=is_final,
                trace_id=trace_id,
                raw_event=event,
                event_received_at=event_received_at,
            )
            self._notify_user_speech_activity(text, is_final, trace_id)
            if _has_explicit_pose_intent(text):
                self._recent_user_pose_intent_until = time.monotonic() + 15.0
                self._recent_user_pose_intent_text = text
            vision_intent = _has_screen_vision_intent(text)
            vision_intent_sent = False
            if (
                vision_intent
                and is_final
                and self.on_user_vision_intent is not None
                and self._should_emit_user_vision_intent(text)
            ):
                try:
                    self.on_user_vision_intent(text)
                    vision_intent_sent = True
                except Exception:
                    self.logger.exception("screen_vision_intent_callback_failed trace_id=%s", trace_id)
            voice_stop_intent = _has_voice_stop_intent(text)
            if voice_stop_intent and self.on_user_voice_stop_intent is not None:
                try:
                    self.on_user_voice_stop_intent(text)
                except Exception:
                    self.logger.exception("voice_stop_intent_callback_failed trace_id=%s", trace_id)
            self.logger.info("volc_user_subtitle trace_id=%s text=%s", trace_id, text)
            self.sender.write_event_record(
                trace_id=trace_id,
                event_type="subtitle_event",
                source="subtitle:user",
                raw_payload=event,
                mapping_result={
                    "ignored": True,
                    "reason": "user_subtitle",
                    "explicit_pose_intent": time.monotonic() <= self._recent_user_pose_intent_until,
                    "intent_text": self._recent_user_pose_intent_text,
                    "screen_vision_intent": vision_intent,
                    "screen_vision_intent_sent": vision_intent_sent,
                    "is_final": is_final,
                    "voice_stop_intent": voice_stop_intent,
                    "user_priority_sent": user_priority_sent,
                },
                send_to_godot=False,
                event_received_at=event_received_at,
                pose_generated_at=time.time(),
            )
            return None

        if self._ai_output_blocked_by_user_priority() and not self._release_user_priority_for_ai_answer():
            self.sender.write_event_record(
                trace_id=trace_id,
                event_type="subtitle_event",
                source="subtitle:ai_ignored_user_priority",
                raw_payload=event,
                mapping_result={"ignored": True, "reason": "user_speech_priority"},
                send_to_godot=False,
                event_received_at=event_received_at,
                pose_generated_at=time.time(),
            )
            return None

        clean_text = sanitize_spoken_text(text)
        if not clean_text:
            self.sender.write_event_record(
                trace_id=trace_id,
                event_type="subtitle_event",
                source="subtitle:ai",
                raw_payload=event,
                mapping_result={"ignored": True, "reason": "empty_after_spoken_text_sanitize"},
                send_to_godot=False,
                event_received_at=event_received_at,
                pose_generated_at=time.time(),
            )
            return None

        fragment_command = self._schedule_ai_subtitle_fragment(
            clean_text,
            trace_id=trace_id,
            is_final=is_final,
            raw_event=event,
            event_received_at=event_received_at,
        )
        if fragment_command is _AI_SUBTITLE_FRAGMENT_BUFFERED:
            return None
        if fragment_command is not None:
            return fragment_command

        return self._schedule_ai_subtitle(
            clean_text,
            trace_id=trace_id,
            is_final=is_final,
            raw_event=event,
            event_received_at=event_received_at,
        )

    def _should_emit_user_vision_intent(self, text: str) -> bool:
        normalized = _normalize_subtitle_text(text)
        now = time.monotonic()
        if now - self._last_user_vision_intent_at < self._user_vision_intent_cooldown_sec:
            return False
        self._last_user_vision_intent_text = normalized
        self._last_user_vision_intent_at = now
        return True

    def on_subtitle_event(self, event: Mapping[str, Any]) -> PoseCommand | None:
        return self.on_volc_subtitle_event(event)

    def _schedule_ai_subtitle_fragment(
        self,
        text: str,
        *,
        trace_id: str,
        is_final: bool,
        raw_event: Mapping[str, Any],
        event_received_at: float,
    ) -> PoseCommand | object | None:
        if bool(raw_event.get("_streamed_from_final_text")):
            return None
        raw_payload = raw_event.get("raw")
        raw = raw_payload if isinstance(raw_payload, Mapping) else raw_event
        round_id = str(_pick_value(raw, "roundId", "roundID", "round_id") or "").strip()
        first_pos = _coerce_int(_pick_value(raw, "firstCharPos", "first_char_pos", "start", "startPos"))
        last_pos = _coerce_int(_pick_value(raw, "lastCharPos", "last_char_pos", "end", "endPos"))
        if not round_id or first_pos is None or last_pos is None:
            return None

        uid = str(_pick_value(raw, "userId", "uid", "user_id", "sender_uid") or _pick_value(raw_event, "uid", "userId") or "")
        key = f"{uid}:{round_id}"
        paragraph = bool(_pick_value(raw, "paragraph", "isParagraphEnd", "paragraphEnd") or False)
        with self._subtitle_fragment_lock:
            entry = self._subtitle_fragment_buffers.setdefault(
                key,
                {
                    "pieces": {},
                    "trace_id": trace_id,
                    "raw_event": raw_event,
                    "event_received_at": event_received_at,
                    "is_final": is_final,
                },
            )
            entry["pieces"][first_pos] = text
            entry["trace_id"] = trace_id
            entry["raw_event"] = raw_event
            entry["event_received_at"] = event_received_at
            entry["is_final"] = bool(is_final or paragraph)
            combined_text = self._combine_subtitle_fragment_pieces(entry["pieces"])

            if paragraph:
                stable_text = self._stabilize_ai_subtitle_fragment(combined_text)
                if not stable_text:
                    return _AI_SUBTITLE_FRAGMENT_BUFFERED
                timer = self._subtitle_fragment_timers.pop(key, None)
                if timer is not None:
                    timer.cancel()
                self._subtitle_fragment_buffers.pop(key, None)
                combined_text = stable_text
                combined_event = self._subtitle_fragment_event(raw_event, combined_text, assembled=True)
            else:
                timer = self._subtitle_fragment_timers.pop(key, None)
                if timer is not None:
                    timer.cancel()
                timer = threading.Timer(self.subtitle_fragment_debounce_sec, self._flush_ai_subtitle_fragment_from_timer, args=(key,))
                timer.daemon = True
                self._subtitle_fragment_timers[key] = timer
                timer.start()
                return _AI_SUBTITLE_FRAGMENT_BUFFERED

        command = self._schedule_ai_subtitle(
            combined_text,
            trace_id=trace_id,
            is_final=True,
            raw_event=combined_event,
            event_received_at=event_received_at,
        )
        return command if command is not None else _AI_SUBTITLE_FRAGMENT_BUFFERED

    def _flush_ai_subtitle_fragment_from_timer(self, key: str) -> None:
        with self._subtitle_fragment_lock:
            entry = self._subtitle_fragment_buffers.pop(key, None)
            self._subtitle_fragment_timers.pop(key, None)
        if not entry:
            return
        combined_text = self._combine_subtitle_fragment_pieces(entry.get("pieces", {}))
        raw_event = entry.get("raw_event") if isinstance(entry.get("raw_event"), Mapping) else {}
        trace_id = str(entry.get("trace_id") or uuid.uuid4().hex)
        event_received_at = float(entry.get("event_received_at") or time.time())
        stable_text = self._stabilize_ai_subtitle_fragment(combined_text)
        if not stable_text:
            return
        if stable_text != combined_text:
            self.sender.write_event_record(
                trace_id=trace_id,
                event_type="subtitle_event",
                source="subtitle:ai_fragment_stabilized",
                raw_payload=raw_event,
                mapping_result={
                    "ignored": True,
                    "reason": "dangling_subtitle_fragment_trimmed",
                    "text": combined_text,
                    "stableText": stable_text,
                },
                send_to_godot=False,
                event_received_at=event_received_at,
                pose_generated_at=time.time(),
            )
        self._schedule_ai_subtitle(
            stable_text,
            trace_id=trace_id,
            is_final=True,
            raw_event=self._subtitle_fragment_event(raw_event, stable_text, assembled=True),
            event_received_at=event_received_at,
        )

    @staticmethod
    def _combine_subtitle_fragment_pieces(pieces: Mapping[Any, str]) -> str:
        combined: list[str] = []
        for _, piece in sorted(pieces.items(), key=lambda item: int(item[0])):
            text = str(piece or "").strip()
            if not text:
                continue
            if combined and text and combined[-1].endswith(text):
                continue
            combined.append(text)
        return sanitize_spoken_text("".join(combined))

    @staticmethod
    def _subtitle_fragment_event(raw_event: Mapping[str, Any], text: str, *, assembled: bool) -> dict[str, Any]:
        event = dict(raw_event)
        event["text"] = text
        event["is_final"] = True
        event["_assembled_subtitle_fragment"] = assembled
        return event

    @staticmethod
    def _ai_subtitle_fragment_ready(text: str) -> bool:
        cleaned = _normalize_subtitle_text(text)
        if not cleaned:
            return False
        if cleaned.endswith(_AI_SUBTITLE_DANGLING_CHARS):
            return False
        if cleaned.endswith(_AI_SUBTITLE_END_CHARS):
            return True
        return len(cleaned) >= 14

    @staticmethod
    def _ai_subtitle_fragment_is_dangling(text: str) -> bool:
        cleaned = _normalize_subtitle_text(text)
        if not cleaned:
            return True
        if cleaned.endswith(_AI_SUBTITLE_STABLE_END_CHARS):
            return False
        if cleaned.endswith(tuple(_AI_SUBTITLE_DANGLING_STRIP_CHARS)):
            return True
        complete = _trim_to_last_complete_subtitle_sentence(cleaned)
        if complete and len(cleaned) > len(complete):
            tail = cleaned[len(complete):].strip()
            if len(tail) <= 12 and any(mark in tail for mark in ("\uff0c", ",", "\u3001", "\uff1a", ":")):
                return True
        if cleaned.endswith(_AI_SUBTITLE_END_CHARS):
            return False
        if cleaned.endswith(_AI_SUBTITLE_DANGLING_CHARS):
            return True
        return len(cleaned) <= 4 and ("，" in cleaned or "," in cleaned)

    @staticmethod
    def _stabilize_ai_subtitle_fragment(text: str) -> str:
        cleaned = sanitize_spoken_text(_normalize_subtitle_text(text))
        if not cleaned:
            return ""
        if not VolcPoseEventAdapter._ai_subtitle_fragment_is_dangling(cleaned):
            return cleaned
        complete = _trim_to_last_complete_subtitle_sentence(cleaned)
        if complete:
            return complete
        trimmed = cleaned.rstrip(_AI_SUBTITLE_DANGLING_STRIP_CHARS).strip()
        if not trimmed:
            return ""
        return trimmed + "\u3002"

    def _resolve_subtitle_speaker(self, event: Mapping[str, Any]) -> str:
        uid_text = str(_pick_value(event, "uid", "userId", "user_id", "sender_uid") or "").strip()
        if uid_text and self.bot_uids:
            return "ai" if uid_text in self.bot_uids else "user"
        return _normalize_speaker(_pick_value(event, "speaker", "role", "user_type", "source"))

    def on_volc_remote_audio_volume(self, uid: str, volume: float | int) -> dict[str, Any] | None:
        event_received_at = time.time()
        uid_text = str(uid)
        if self.bot_uids and uid_text not in self.bot_uids:
            return None
        if time.monotonic() < self._ignore_ai_audio_until:
            return None

        raw_volume = _normalize_volume(volume)
        smoothing = self._volume_attack if raw_volume >= self._smoothed_mouth_open else self._volume_release
        self._smoothed_mouth_open = (
            self._smoothed_mouth_open * (1.0 - smoothing)
            + raw_volume * smoothing
        )
        now = time.monotonic()
        start_or_continue = (
            self._smoothed_mouth_open >= self._audio_start_threshold
            if not self._audio_gate_active
            else self._smoothed_mouth_open >= self._audio_stop_threshold
        )

        with self._audio_gate_lock:
            if start_or_continue:
                if self._audio_stop_timer is not None:
                    self._audio_stop_timer.cancel()
                    self._audio_stop_timer = None
            elif self._audio_gate_active:
                if self._audio_stop_timer is None:
                    self._audio_stop_timer = threading.Timer(
                        self._audio_silence_timeout_sec,
                        self._send_audio_gate_stop,
                    )
                    self._audio_stop_timer.daemon = True
                    self._audio_stop_timer.start()
                return None
            else:
                return None

            if not self._audio_gate_active:
                self._audio_gate_active = True
                force_send = True
            else:
                force_send = False

            if self._audio_stop_timer is None:
                self._audio_stop_timer = threading.Timer(
                    self._audio_silence_timeout_sec,
                    self._send_audio_gate_stop,
                )
                self._audio_stop_timer.daemon = True
                self._audio_stop_timer.start()

        if not force_send and now - self._last_volume_sent_at < self.volume_interval_sec:
            return None

        self._last_volume_sent_at = now
        payload = {
            "type": "pet_pose",
            "state": "speaking",
            "emotion": "neutral",
            "gesture": "none",
            "posture": "stand",
            "mouth": "audio_volume",
            "mouth_open": round(_shape_mouth_open(self._smoothed_mouth_open), 4),
            "audio_active": True,
            "overlay_only": True,
        }
        self._enqueue_payload(
            payload,
            trace_id=_new_trace_id("audio_volume"),
            source="remote_audio_volume:ai",
            event_type="remote_audio_volume_event",
            raw_payload={"uid": uid_text, "volume": volume, "normalized": raw_volume},
            mapping_result=payload,
            event_received_at=event_received_at,
        )
        return payload

    def on_remote_audio_volume(self, uid: str, volume: float | int) -> dict[str, Any] | None:
        return self.on_volc_remote_audio_volume(uid, volume)

    def _send_audio_gate_stop(self) -> None:
        with self._audio_gate_lock:
            if not self._audio_gate_active:
                return
            self._audio_gate_active = False
            self._smoothed_mouth_open = 0.0
            self._last_volume_sent_at = time.monotonic()
        payload = {
            "type": "pet_pose",
            "state": "speaking",
            "emotion": "neutral",
            "gesture": "none",
            "posture": "stand",
            "mouth": "audio_volume",
            "mouth_open": 0.0,
            "audio_active": False,
            "overlay_only": True,
        }
        self._enqueue_payload(
            payload,
            trace_id=_new_trace_id("audio_silence"),
            source="remote_audio_volume:silence",
            event_type="remote_audio_volume_event",
            raw_payload={"silence_timeout_sec": self._audio_silence_timeout_sec},
            mapping_result=payload,
            event_received_at=time.time(),
        )

    def on_volc_function_call(self, event: Mapping[str, Any]) -> PoseCommand | None:
        event_received_at = time.time()
        trace_id = _trace_id(event, "function_call")
        try:
            call = _extract_function_call(event)
            if not self._allow_function_call_from_recent_user_intent(call):
                raise PoseCommandError(
                    "set_pet_pose rejected locally because recent user text has no explicit pose intent"
                )
            command = self.function_call_adapter.handle_tool_call(call)
            routed = self.pose_router.route(command)
            self._enqueue_payload(
                routed.payload,
                trace_id=trace_id,
                source="function_call:set_pet_pose",
                event_type="function_call_event",
                raw_payload=event,
                mapping_result=_route_mapping_result(routed),
                event_received_at=event_received_at,
            )
            return command
        except (PoseCommandError, json.JSONDecodeError, TypeError, ValueError) as exc:
            self.logger.warning(
                "volc_function_call_rejected trace_id=%s reason=%s event=%s",
                trace_id,
                exc,
                event,
            )
            self.sender.write_event_record(
                trace_id=trace_id,
                event_type="function_call_event",
                source="function_call:rejected",
                raw_payload=event,
                mapping_result={"error": str(exc)},
                send_to_godot=False,
                error=str(exc),
                event_received_at=event_received_at,
                pose_generated_at=time.time(),
            )
            return None

    def _allow_function_call_from_recent_user_intent(self, call: Mapping[str, Any]) -> bool:
        name = str(call.get("name", call.get("function_name", ""))).strip()
        if name != "set_pet_pose":
            return True
        try:
            arguments = call.get("arguments", {})
            if isinstance(arguments, str):
                arguments = json.loads(arguments or "{}")
            if isinstance(arguments, Mapping) and int(arguments.get("priority", 0)) >= 50:
                return True
        except (TypeError, ValueError, json.JSONDecodeError):
            pass
        if time.monotonic() <= self._recent_user_pose_intent_until:
            return True
        if time.monotonic() - self._recent_user_subtitle_at > 15.0:
            # Some StartVoiceChat / RTC paths do not deliver user subtitles to
            # the local client. In that case the safety boundary is the
            # function schema plus PoseRouter; do not block an otherwise valid
            # semantic pose command just because the transcript callback is
            # absent.
            return True
        return _has_explicit_pose_intent(self._recent_user_subtitle)

    def on_function_call(self, event: Mapping[str, Any]) -> PoseCommand | None:
        return self.on_volc_function_call(event)

    def check_start_voice_chat_config(self, request: Mapping[str, Any]) -> list[StartVoiceChatConfigIssue]:
        return check_start_voice_chat_config(request)

    def _dispatch_state_event(
        self,
        state: str,
        *,
        trace_id: str,
        raw_event: Any,
        event_received_at: float,
        from_timer: bool = False,
    ) -> PoseCommand | None:
        now = time.monotonic()
        if state == self._last_state_key and now - self._last_state_sent_at < self.state_debounce_sec:
            self.logger.debug("volc_ai_state_debounced trace_id=%s state=%s", trace_id, state)
            return None
        if state == self._current_state_key and not from_timer:
            self.logger.debug("volc_ai_state_duplicate trace_id=%s state=%s", trace_id, state)
            return None

        delay = self._state_delay_for(state)
        if delay > 0.0 and not from_timer:
            self._schedule_state_event(
                state,
                trace_id=trace_id,
                raw_event=raw_event,
                event_received_at=event_received_at,
                delay=delay,
            )
            return None

        if self._state_timer is not None and not from_timer:
            self._state_timer.cancel()
        self._last_state_key = state
        self._last_state_sent_at = now
        self._current_state_key = state
        self._current_state_sent_at = now
        command = PoseCommand.from_mapping({"type": "pet_pose", **POSE_BY_STATE[state]})
        routed = self.pose_router.route(command)
        self._enqueue_payload(
            routed.payload,
            trace_id=trace_id,
            source=f"ai_state:{state}",
            event_type="ai_state_event",
            raw_payload=raw_event,
            mapping_result=_route_mapping_result(routed),
            event_received_at=event_received_at,
        )
        return command

    def _dispatch_cloud_error_event(
        self,
        event: Mapping[str, Any],
        *,
        trace_id: str,
        event_received_at: float,
    ) -> None:
        message = _event_error_message(event)
        short_message = _short_text(message, 96)
        payload = {
            "type": "pet_pose",
            "state": "idle",
            "emotion": "confused",
            "gesture": "none",
            "posture": "stand",
            "bubble_text": "云端会话错误：%s" % short_message,
            "priority": 90,
            "duration_ms": 7000,
            "interruptible": True,
        }
        self._enqueue_payload(
            payload,
            trace_id=trace_id,
            source="ai_state:error",
            event_type="ai_state_event",
            raw_payload=event,
            mapping_result={"error": "cloud_error", "message": short_message},
            event_received_at=event_received_at,
        )

    def _state_delay_for(self, target_state: str) -> float:
        now = time.monotonic()
        current_hold = self._state_min_holds.get(self._current_state_key, 0.0)
        remaining_hold = max(0.0, current_hold - (now - self._current_state_sent_at))
        if target_state == "idle":
            return max(self._idle_delay_sec, remaining_hold)
        if target_state == "speaking":
            return 0.0
        if target_state in {"listening", "interrupted"}:
            return 0.0
        return remaining_hold

    def _schedule_state_event(
        self,
        state: str,
        *,
        trace_id: str,
        raw_event: Any,
        event_received_at: float,
        delay: float,
    ) -> None:
        with self._state_timer_lock:
            if self._state_timer is not None:
                self._state_timer.cancel()
            self._state_timer = threading.Timer(
                delay,
                self._dispatch_state_event,
                kwargs={
                    "state": state,
                    "trace_id": trace_id,
                    "raw_event": raw_event,
                    "event_received_at": event_received_at,
                    "from_timer": True,
                },
            )
            self._state_timer.daemon = True
            self._state_timer.start()

    def _schedule_ai_subtitle(
        self,
        text: str,
        *,
        trace_id: str,
        is_final: bool,
        raw_event: Mapping[str, Any],
        event_received_at: float,
    ) -> PoseCommand | None:
        normalized_text = _normalize_subtitle_text(text)
        if is_final:
            normalized_text = self._stabilize_ai_subtitle_fragment(normalized_text)
        if not normalized_text:
            return None
        if normalized_text == self._last_ai_subtitle_text:
            return None
        self._last_ai_subtitle_text = normalized_text

        if is_final and _should_stream_subtitle(normalized_text):
            return self._schedule_ai_subtitle_chunks(
                normalized_text,
                trace_id=trace_id,
                raw_event=raw_event,
                event_received_at=event_received_at,
            )

        with self._subtitle_lock:
            self._pending_ai_subtitle = normalized_text
            now = time.monotonic()
            if is_final or now - self._last_subtitle_sent_at >= self.subtitle_throttle_sec:
                return self._flush_ai_subtitle_locked(trace_id, raw_event, event_received_at)

            if self._subtitle_timer is None or not self._subtitle_timer.is_alive():
                delay = self.subtitle_throttle_sec - (now - self._last_subtitle_sent_at)
                self._subtitle_timer = threading.Timer(
                    max(0.01, delay),
                    self._flush_ai_subtitle_from_timer,
                    args=(trace_id, raw_event, event_received_at),
                )
                self._subtitle_timer.daemon = True
                self._subtitle_timer.start()
            return None

    def _flush_ai_subtitle_from_timer(
        self,
        trace_id: str,
        raw_event: Mapping[str, Any],
        event_received_at: float,
    ) -> None:
        with self._subtitle_lock:
            self._flush_ai_subtitle_locked(trace_id, raw_event, event_received_at)
            self._subtitle_timer = None

    def _flush_ai_subtitle_locked(
        self,
        trace_id: str,
        raw_event: Mapping[str, Any],
        event_received_at: float,
    ) -> PoseCommand | None:
        text = self._pending_ai_subtitle
        if not text:
            return None
        self._pending_ai_subtitle = ""
        self._last_subtitle_sent_at = time.monotonic()
        return self._dispatch_ai_subtitle_text(
            text,
            trace_id=trace_id,
            raw_event=raw_event,
            event_received_at=event_received_at,
            overlay_only=True,
        )

    def _schedule_ai_subtitle_chunks(
        self,
        text: str,
        *,
        trace_id: str,
        raw_event: Mapping[str, Any],
        event_received_at: float,
    ) -> PoseCommand | None:
        chunks = _split_subtitle_chunks(text)
        if not chunks:
            return None
        with self._subtitle_lock:
            self._pending_ai_subtitle = ""
            if self._subtitle_timer is not None:
                self._subtitle_timer.cancel()
                self._subtitle_timer = None
            for timer in self._subtitle_stream_timers:
                timer.cancel()
            self._subtitle_stream_timers.clear()

        delay = 0.0
        first_command: PoseCommand | None = None
        for index, chunk in enumerate(chunks):
            chunk_trace_id = f"{trace_id}-chunk{index + 1}"
            chunk_event = dict(raw_event)
            chunk_event["text"] = chunk
            chunk_event["is_final"] = index == len(chunks) - 1
            chunk_event["_streamed_from_final_text"] = True
            chunk_event["_stream_index"] = index + 1
            chunk_event["_stream_total"] = len(chunks)
            if index == 0:
                first_command = self._dispatch_ai_subtitle_text(
                    chunk,
                    trace_id=chunk_trace_id,
                    raw_event=chunk_event,
                    event_received_at=event_received_at,
                    overlay_only=True,
                )
            else:
                timer = threading.Timer(
                    delay,
                    self._dispatch_ai_subtitle_text,
                    kwargs={
                        "text": chunk,
                        "trace_id": chunk_trace_id,
                        "raw_event": chunk_event,
                        "event_received_at": event_received_at,
                        "overlay_only": True,
                    },
                )
                timer.daemon = True
                self._subtitle_stream_timers.append(timer)
                timer.start()
            delay += _subtitle_chunk_delay(chunks[index])
        return first_command

    def _dispatch_ai_subtitle_text(
        self,
        text: str,
        *,
        trace_id: str,
        raw_event: Mapping[str, Any],
        event_received_at: float,
        overlay_only: bool,
    ) -> PoseCommand | None:
        if time.monotonic() < self._ignore_ai_subtitles_until and not self._release_user_priority_for_ai_answer():
            self.sender.write_event_record(
                trace_id=trace_id,
                event_type="subtitle_event",
                source="subtitle:ai_chunk_ignored_user_priority",
                raw_payload=raw_event,
                mapping_result={"ignored": True, "reason": "user_speech_priority"},
                send_to_godot=False,
                event_received_at=event_received_at,
                pose_generated_at=time.time(),
            )
            return None
        decision = self.emotion_director.process_text(
            text,
            voice_state="speaking",
            is_final=bool(_pick_value(raw_event, "is_final", "final", "completed", "definite") or False),
            source="subtitle:ai",
        )
        command = decision.pose_command
        routed = self.pose_router.route(command)
        subtitle_payload = dict(routed.payload)
        # Subtitle text should stay visible until replaced by the next subtitle.
        # Pose duration is useful for gestures, but the bubble should not fall back to "……".
        subtitle_payload["bubble_text"] = text
        subtitle_payload["duration_ms"] = 0
        if overlay_only:
            subtitle_payload["overlay_only"] = True
            subtitle_payload["gesture"] = "none"
        subtitle_mapping = _route_mapping_result(routed)
        subtitle_mapping["bubble_text"] = text
        subtitle_mapping["_local_emotion"] = _json_safe(decision.emotion_command.__dict__)
        subtitle_mapping["duration_ms"] = 0
        if overlay_only:
            subtitle_mapping["overlay_only"] = True
            subtitle_mapping["gesture"] = "none"
        self._enqueue_payload(
            subtitle_payload,
            trace_id=trace_id,
            source="subtitle:ai",
            event_type="subtitle_event",
            raw_payload=raw_event,
            mapping_result=subtitle_mapping,
            event_received_at=event_received_at,
        )
        return command

    def _enqueue_command(
        self,
        command: PoseCommand,
        *,
        trace_id: str,
        source: str,
        event_type: str,
        raw_payload: Any,
        mapping_result: Any,
        event_received_at: float,
    ) -> bool:
        return self._enqueue_payload(
            command.to_godot_payload(),
            trace_id=trace_id,
            source=source,
            event_type=event_type,
            raw_payload=raw_payload,
            mapping_result=mapping_result,
            event_received_at=event_received_at,
        )

    def _enqueue_payload(
        self,
        payload: Mapping[str, Any],
        *,
        trace_id: str,
        source: str,
        event_type: str,
        raw_payload: Any,
        mapping_result: Any,
        event_received_at: float,
    ) -> bool:
        pose_generated_at = time.time()
        self.logger.debug("volc_pose_enqueue trace_id=%s source=%s payload=%s", trace_id, source, payload)
        return self.sender.enqueue(
            _QueuedPose(
                trace_id=trace_id,
                payload=dict(payload),
                source=source,
                created_at=time.monotonic(),
                event_type=event_type,
                raw_payload=_json_safe(raw_payload),
                mapping_result=_json_safe(mapping_result),
                event_received_at=event_received_at,
                pose_generated_at=pose_generated_at,
                attempted_send=True,
            )
        )


def check_start_voice_chat_config(request: Mapping[str, Any]) -> list[StartVoiceChatConfigIssue]:
    issues: list[StartVoiceChatConfigIssue] = []
    flat = _flatten_keys(request)

    def has_any(*needles: str) -> bool:
        lowered = {key.lower() for key in flat}
        return any(any(needle.lower() in key for key in lowered) for needle in needles)

    if not has_any("subtitle", "transcript", "asr", "caption"):
        issues.append(StartVoiceChatConfigIssue("subtitle", "实时字幕/对话记录配置未检测到，请启用 AI 字幕回调。"))
    if not has_any("statecallback", "aistate", "taskevent", "statuscallback", "enableconversationstatecallback"):
        issues.append(StartVoiceChatConfigIssue("ai_state", "AI 状态或任务事件回调配置未检测到。"))
    if has_any("functioncallingconfig", "tools", "toolconfig") and not _has_set_pet_pose_tool(request):
        issues.append(StartVoiceChatConfigIssue("set_pet_pose", "Function Calling 工具 set_pet_pose 未检测到。"))
    has_s2s = has_any("s2sconfig", "speech2speech", "s2s")
    has_asr_tts = has_any("asrconfig") and has_any("ttsconfig")
    if not has_any("llmconfig"):
        issues.append(StartVoiceChatConfigIssue("LLMConfig", "端到端语音混合编排 LLMConfig 未检测到。", "error"))
    if not has_s2s and not has_asr_tts:
        issues.append(StartVoiceChatConfigIssue("voice_route", "未检测到 S2SConfig 或 ASRConfig+TTSConfig 语音链路。", "error"))
    llm_config = _get_nested(request, "Config", "LLMConfig")
    if isinstance(llm_config, Mapping) and str(llm_config.get("Mode") or "").lower() == "customllm":
        custom_url = str(
            llm_config.get("URL")
            or llm_config.get("Url")
            or llm_config.get("url")
            or llm_config.get("Endpoint")
            or ""
        ).strip()
        if not custom_url or custom_url.startswith("${"):
            issues.append(
                StartVoiceChatConfigIssue(
                    "CustomLLM.URL",
                    "第三方 Agent LLM 路线已启用，但 LLMConfig.URL 为空。请填写 OpenAI-compatible/Agent HTTP endpoint。",
                    "error",
                )
            )
    websearch_extra = _get_nested(
        request,
        "Config",
        "S2SConfig",
        "ProviderParams",
        "dialog",
        "extra",
    )
    if isinstance(websearch_extra, Mapping) and bool(websearch_extra.get("enable_volc_websearch")):
        api_key = str(websearch_extra.get("volc_websearch_api_key") or "").strip()
        if not api_key or api_key.startswith("${"):
            issues.append(
                StartVoiceChatConfigIssue(
                    "volc_websearch_api_key",
                    "已开启 S2S 内置联网搜索，但 volc_websearch_api_key 为空。若使用 TOP 网关 AK/SK，请关闭该内置开关并使用本地 web_search 工具。",
                )
            )
        search_type = str(websearch_extra.get("volc_websearch_type") or "").strip()
        if search_type and search_type not in {"web", "web_summary"}:
            issues.append(
                StartVoiceChatConfigIssue(
                    "volc_websearch_type",
                    "火山联网搜索类型建议使用 web 或 web_summary。",
                )
            )
    if has_any("jsoninreply", "rawjson", "returnjson"):
        issues.append(StartVoiceChatConfigIssue("json_voice_reply", "请确认 JSON 不会混入 AI 语音回复文本。"))

    return issues


def _extract_function_call(event: Mapping[str, Any]) -> Mapping[str, Any]:
    if "name" in event or "function_name" in event:
        return event
    for key in ("function_call", "tool_call", "toolCall", "call"):
        value = event.get(key)
        if isinstance(value, Mapping):
            return value
    raise PoseCommandError("Function call event does not contain a tool call object")


def _normalize_ai_state(value: Any) -> str:
    if isinstance(value, int):
        return STAGE_CODE_ALIASES.get(value, "")
    if isinstance(value, float):
        return STAGE_CODE_ALIASES.get(int(value), "")
    state = str(value or "").strip().lower().replace("-", "_")
    return STATE_ALIASES.get(state, "")


def _event_error_message(event: Mapping[str, Any]) -> str:
    error_info = event.get("ErrorInfo", event.get("errorInfo", event.get("error", {})))
    if isinstance(error_info, Mapping):
        code = error_info.get("ErrorCode", error_info.get("code", ""))
        reason = str(error_info.get("Reason", error_info.get("message", error_info.get("Message", ""))) or "")
        if code or reason:
            return ("%s %s" % (code, reason)).strip()
    stage = event.get("Stage", event.get("stage", {}))
    if isinstance(stage, Mapping):
        description = str(stage.get("Description", stage.get("description", "")) or "")
        if "error" in description.lower():
            return description
    return ""


def _short_text(text: str, limit: int) -> str:
    cleaned = re.sub(r"\s+", " ", str(text or "")).strip()
    if len(cleaned) > limit:
        return cleaned[:limit] + "…"
    return cleaned


def _normalize_speaker(value: Any) -> str:
    speaker = str(value or "ai").strip().lower()
    return SUBTITLE_SPEAKER_ALIASES.get(speaker, "ai")


def _normalize_volume(volume: float | int) -> float:
    numeric = float(volume)
    if numeric > 100.0:
        numeric = numeric / 255.0
    elif numeric > 1.0:
        numeric = numeric / 100.0
    return max(0.0, min(1.0, numeric))


def _coerce_int(value: Any) -> int | None:
    if value is None:
        return None
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def _shape_mouth_open(value: float) -> float:
    normalized = max(0.0, min(1.0, float(value)))
    if normalized <= 0.035:
        return 0.0
    lifted = (normalized - 0.035) / 0.965
    return max(0.0, min(0.58, (lifted ** 0.75) * 0.58))


def _normalize_subtitle_text(text: str) -> str:
    return re.sub(r"\s+", " ", str(text or "").strip())


def _trim_to_last_complete_subtitle_sentence(text: str) -> str:
    normalized = _normalize_subtitle_text(text).rstrip(_AI_SUBTITLE_DANGLING_STRIP_CHARS)
    if not normalized:
        return ""
    last_index = -1
    for mark in _AI_SUBTITLE_STABLE_END_CHARS:
        last_index = max(last_index, normalized.rfind(mark))
    if last_index < 0:
        return ""
    return normalized[: last_index + 1].strip()


def _should_stream_subtitle(text: str) -> bool:
    if len(text) >= 36:
        return True
    return sum(1 for ch in text if ch in "。！？!?；;") >= 2


def _split_subtitle_chunks(text: str, *, max_chars: int = 30) -> list[str]:
    normalized = _normalize_subtitle_text(text)
    if not normalized:
        return []

    parts: list[str] = []
    start = 0
    for match in re.finditer(r"[。！？!?；;]+|(?<=）)", normalized):
        end = match.end()
        part = normalized[start:end].strip()
        if part:
            parts.extend(_split_long_subtitle_part(part, max_chars=max_chars))
        start = end
    tail = normalized[start:].strip()
    if tail:
        parts.extend(_split_long_subtitle_part(tail, max_chars=max_chars))

    if not parts:
        parts = _split_long_subtitle_part(normalized, max_chars=max_chars)
    return parts


def _split_long_subtitle_part(text: str, *, max_chars: int) -> list[str]:
    if len(text) <= max_chars:
        return [text]
    output: list[str] = []
    current = ""
    for token in re.split(r"([，、,——：:])", text):
        if not token:
            continue
        candidate = current + token
        if current and len(candidate) > max_chars:
            output.append(current.strip())
            current = token.lstrip("，、,：:")
        else:
            current = candidate
    if current.strip():
        output.append(current.strip())

    final: list[str] = []
    for item in output:
        if len(item) <= max_chars:
            final.append(item)
            continue
        for index in range(0, len(item), max_chars):
            final.append(item[index : index + max_chars].strip())
    return [item for item in final if item]


def _subtitle_chunk_delay(text: str) -> float:
    # Final AI subtitles often arrive as one full paragraph. Replay them as
    # readable sentence-sized bubbles without waiting for perfect token events.
    base = 0.34 + min(len(text), 42) * 0.028
    if text.endswith(("。", "！", "？", "!", "?")):
        base += 0.22
    return max(0.48, min(base, 1.45))


def _extract_ai_state_value(event: Mapping[str, Any] | str) -> Any:
    if isinstance(event, str):
        return event
    for stage_key in ("Stage", "stage"):
        stage = event.get(stage_key)
        if isinstance(stage, Mapping):
            code = stage.get("Code", stage.get("code"))
            if code is not None:
                try:
                    return int(code)
                except (TypeError, ValueError):
                    return code
            description = stage.get("Description", stage.get("description"))
            if description:
                return description
    for key in (
        "state",
        "ai_state",
        "aiState",
        "status",
        "event",
        "type",
        "stage",
        "description",
        "Description",
        "Code",
        "code",
    ):
        if key in event:
            value = event[key]
            if key.lower() == "code":
                try:
                    return int(value)
                except (TypeError, ValueError):
                    return value
            return value
    return None


def _pick_value(event: Mapping[str, Any] | str, *keys: str) -> Any:
    if isinstance(event, str):
        return event
    for key in keys:
        if key in event:
            return event[key]
    return None


def _trace_id(event: Mapping[str, Any] | str, prefix: str) -> str:
    if isinstance(event, Mapping):
        for key in ("trace_id", "traceId", "request_id", "requestId", "task_id", "taskId"):
            if key in event and event[key]:
                return str(event[key])
    return _new_trace_id(prefix)


def _new_trace_id(prefix: str) -> str:
    return f"{prefix}-{int(time.time() * 1000)}-{uuid.uuid4().hex[:8]}"


def _elapsed_ms(start: float, end: float) -> float | None:
    if not start or not end:
        return None
    return round((end - start) * 1000.0, 3)


def _json_safe(value: Any) -> Any:
    try:
        json.dumps(value, ensure_ascii=False)
        return value
    except TypeError:
        return repr(value)


def _route_mapping_result(routed: Any) -> dict[str, Any]:
    return {
        **dict(routed.payload),
        "_local_route": {
            "action": routed.action,
            "route": list(routed.route),
        },
    }


def _flatten_keys(value: Any, prefix: str = "") -> set[str]:
    output: set[str] = set()
    if isinstance(value, Mapping):
        for key, item in value.items():
            path = f"{prefix}.{key}" if prefix else str(key)
            output.add(path)
            output.update(_flatten_keys(item, path))
    elif isinstance(value, list):
        for index, item in enumerate(value):
            output.update(_flatten_keys(item, f"{prefix}[{index}]"))
    return output


def _get_nested(value: Mapping[str, Any], *keys: str) -> Any:
    current: Any = value
    for key in keys:
        if not isinstance(current, Mapping):
            return None
        current = current.get(key)
    return current


def _has_set_pet_pose_tool(value: Any) -> bool:
    if isinstance(value, Mapping):
        if value.get("name") == "set_pet_pose" or value.get("function_name") == "set_pet_pose":
            return True
        return any(_has_set_pet_pose_tool(item) for item in value.values())
    if isinstance(value, list):
        return any(_has_set_pet_pose_tool(item) for item in value)
    return False


def _has_explicit_pose_intent(text: str) -> bool:
    normalized = str(text or "").strip().lower()
    if not normalized:
        return False
    return any(
        token in normalized
        for token in (
            "姿势",
            "动作",
            "摆",
            "坐下",
            "坐着",
            "飞起",
            "飞一下",
            "趴下",
            "躺下",
            "得意",
            "展示",
            "变身",
            "挥手",
            "指一下",
            "指着",
            "指",
            "叉腰",
            "开心动作",
            "得意动作",
            "得意姿势",
            "吐槽一下",
            "吐槽动作",
            "嘲讽",
            "做一下",
            "做个动作",
            "来个",
            "切换",
            "切个动作",
            "换个动作",
            "表演",
            "展示一下",
            "站着",
            "坐",
            "躺",
            "趴",
            "飞",
        )
    )


def _has_screen_vision_intent(text: str) -> bool:
    normalized = str(text or "").strip().lower()
    if not normalized:
        return False
    if any(token in normalized for token in ("版本", "活动", "攻略", "隐藏成就", "联网", "搜索", "查一下", "搜一下")):
        return False
    if any(token in normalized for token in ("屏幕", "画面", "直播", "游戏画面")) and any(
        token in normalized for token in ("什么", "名字", "叫", "看", "识别", "哪里", "在哪", "怎么")
    ):
        return True
    if any(token in normalized for token in ("这个", "那个", "哪个")) and any(
        token in normalized for token in ("名字", "叫什么", "是谁", "是什么")
    ):
        return True
    return any(
        token in normalized
        for token in (
            "看屏幕",
            "看看屏幕",
            "看一下屏幕",
            "屏幕上有什么",
            "画面上有什么",
            "看得到",
            "看得见",
            "能看到",
            "能看见",
            "你能看",
            "你看得到",
            "你看得见",
            "帮我看",
            "看一下这个",
            "看看这个",
            "识别屏幕",
            "屏幕识别",
            "读一下",
            "读这个",
            "这是什么",
            "怎么回事",
            "哪里不对",
            "陪我玩",
            "帮我打",
            "看游戏",
            "看画面",
        )
    )


def _has_voice_stop_intent(text: str) -> bool:
    normalized = str(text or "").strip().lower()
    if not normalized:
        return False
    compact = re.sub(r"[\s，。！？!?,.;；、]+", "", normalized)
    if _has_app_exit_intent(normalized):
        return True
    exact_intents = {
        "关闭",
        "关了",
        "停",
        "停止",
        "退出",
        "结束",
        "别听了",
        "不用听了",
        "先关了",
        "先退了",
    }
    if compact in exact_intents:
        return True
    stop_tokens = ("关闭", "关掉", "停止", "结束", "退出", "断开", "停掉")
    voice_tokens = ("语音", "通话", "对话", "聊天", "会话", "麦克风", "麦")
    if any(stop in compact for stop in stop_tokens) and any(voice in compact for voice in voice_tokens):
        return True
    return any(
        token in compact
        for token in (
            "停止语音",
            "关闭语音",
            "结束通话",
            "退出通话",
            "停止对话",
            "关闭对话",
            "别听我说话了",
            "不用继续听了",
        )
    )


def _has_app_exit_intent(text: str) -> bool:
    normalized = str(text or "").strip().lower()
    compact = re.sub(r"[\s，。！？、,.!?;；:：]+", "", normalized)
    if not compact:
        return False
    exact_intents = {
        "退出",
        "关闭",
        "关掉",
        "退了",
        "关闭程序",
        "退出程序",
        "关闭桌宠",
        "退出桌宠",
        "关掉程序",
        "关掉桌宠",
        "结束程序",
        "结束桌宠",
    }
    if compact in exact_intents:
        return True
    return any(
        token in compact
        for token in (
            "退出程序",
            "关闭程序",
            "退出桌宠",
            "关闭桌宠",
            "关掉程序",
            "关掉桌宠",
        )
    )
