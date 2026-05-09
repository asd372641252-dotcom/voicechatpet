from __future__ import annotations

import sys
import time
from pathlib import Path
from threading import Lock, RLock

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from scripts.run_volc_rtc_web_client import VolcRtcWebBridge


class _FakeAdapter:
    def __init__(self) -> None:
        self.current_state = "idle"
        self.current_state_age_sec = 99.0
        self.audio_active = False
        self.sender = _FakeSender()

    def runtime_status(self) -> dict[str, object]:
        return {
            "current_state": self.current_state,
            "current_state_age_sec": self.current_state_age_sec,
            "audio_active": self.audio_active,
            "mouth_open": 0.0,
            "subtitle_pending": False,
        }


class _FakeSender:
    def write_event_record(self, **_kwargs: object) -> None:
        return


class _FakeLogger:
    def warning(self, *_args: object, **_kwargs: object) -> None:
        return

    def exception(self, *_args: object, **_kwargs: object) -> None:
        return


def _make_bridge() -> VolcRtcWebBridge:
    bridge = object.__new__(VolcRtcWebBridge)
    bridge.logger = _FakeLogger()
    bridge.request = {
        "Config": {
            "LLMConfig": {
                "VisionConfig": {
                    "Enable": True,
                }
            }
        }
    }
    bridge.config = {}
    bridge.bot_uid = "silver_wolf_bot"
    bridge.adapter = _FakeAdapter()
    bridge._memory_service = None
    bridge._voice_active = True
    bridge._active_request = None
    bridge._vision_lock = Lock()
    bridge._vision_desired = True
    bridge._vision_client_state = {
        "screen_published": True,
        "updated_at": time.time(),
        "message": "",
    }
    bridge._debug_text_lock = Lock()
    bridge._debug_text_next_id = 1
    bridge._debug_text_pending = []
    bridge._debug_text_results = []
    bridge._omnivoice_queue_lock = RLock()
    bridge._omnivoice_provider = None
    bridge._voice_output_effective_provider = "volc_rtc"
    bridge._omnivoice_active_job_text = {}
    bridge._omnivoice_recent_ai_texts = []
    bridge._recent_ai_echo_texts = []
    bridge._recent_ai_echo_rounds = {}
    bridge._active_ai_playback_texts = []
    bridge._active_ai_playback_until = 0.0
    bridge._omnivoice_echo_window_seconds = 14.0
    bridge._omnivoice_echo_similarity_threshold = 0.62
    bridge._companion_vision_config = {
        "enabled": True,
        "interval_sec": 8.0,
        "tick_sec": 0.5,
        "wait_until_speech_done": True,
        "pending_timeout_sec": 8.0,
        "max_busy_without_audio_sec": 18.0,
        "min_idle_sec": 1.0,
        "user_silence_sec": 0.0,
        "failure_backoff_sec": 0.0,
        "max_failure_backoff_sec": 0.0,
        "interrupt_mode": 0,
        "prompt": "陪玩模式测试：看屏幕说一句。",
    }
    bridge._companion_vision_lock = Lock()
    bridge._companion_vision_running = True
    bridge._companion_vision_last_prompt_at = 0.0
    bridge._companion_vision_pending = False
    bridge._companion_vision_pending_id = 0
    bridge._companion_vision_pending_until = 0.0
    bridge._companion_vision_pending_has_response = False
    bridge._companion_vision_last_skip_reason = ""
    bridge._companion_vision_failure_count = 0
    bridge._companion_vision_next_allowed_at = 0.0
    bridge._companion_vision_last_success_at = 0.0
    bridge._companion_vision_response_active = False
    bridge._companion_vision_recent_ai_texts = []
    bridge._companion_vision_waiting_for_welcome_done = False
    bridge._companion_vision_welcome_seen_speaking = False
    bridge._companion_vision_welcome_wait_started_at = 0.0
    bridge._last_ai_state = "idle"
    bridge._last_ai_state_at = time.monotonic() - 99.0
    bridge._voice_priority_user_until = 0.0
    bridge._voice_priority_waiting_for_answer = False
    bridge._voice_priority_waiting_until = 0.0
    bridge._voice_priority_answer_until = 0.0
    bridge._voice_priority_last_reason = ""
    bridge._vision_observer = None
    bridge._speech_watchdog_enabled = False
    bridge._speech_watchdog_delay_sec = 10.0
    bridge._speech_watchdog_lock = Lock()
    bridge._speech_watchdog_timer = None
    bridge._speech_watchdog_ai_seen = False
    return bridge


def main() -> int:
    bridge = _make_bridge()

    first = bridge._companion_vision_tick(force=True)
    assert first.get("queued") is True, first
    assert first.get("id") == 1, first
    assert len(bridge._debug_text_pending) == 1, bridge._debug_text_pending
    assert bridge._debug_text_pending[0]["source"] == "companion_vision", bridge._debug_text_pending
    assert bridge._debug_text_pending[0]["interruptMode"] == 0, bridge._debug_text_pending

    pending_skip = bridge._companion_vision_tick(force=True)
    assert pending_skip.get("reason") == "prompt_pending", pending_skip

    bridge._note_companion_ai_text("看到画面了，先稳住。")
    text_success_at = bridge._companion_vision_last_success_at
    assert bridge._companion_vision_pending is False
    assert bridge._companion_vision_response_active is True

    bridge._companion_vision_last_prompt_at = time.monotonic() - 99.0
    interval_skip = bridge._companion_vision_tick(force=False)
    assert interval_skip.get("reason") == "interval_wait", interval_skip

    bridge._note_ai_state_for_companion("speaking")
    assert bridge._companion_vision_response_active is True
    bridge._note_ai_state_for_companion("idle")
    bridge._last_ai_state_at = time.monotonic() - 2.0
    assert bridge._companion_vision_pending is False
    assert bridge._companion_vision_response_active is False
    assert bridge._companion_vision_last_success_at >= text_success_at

    bridge._companion_vision_last_prompt_at = time.monotonic() - 9.0
    bridge._companion_vision_last_success_at = time.monotonic() - 9.0
    second = bridge._companion_vision_tick(force=False)
    assert second.get("queued") is True, second
    assert second.get("id") == 2, second

    bridge._note_companion_ai_text("第二次也正常。")
    bridge._note_ai_state_for_companion("idle")
    bridge._last_ai_state_at = time.monotonic() - 2.0
    bridge._companion_vision_last_prompt_at = time.monotonic() - 9.0
    bridge.adapter.current_state = "speaking"
    bridge.adapter.current_state_age_sec = 1.0
    busy_skip = bridge._companion_vision_tick(force=True)
    assert busy_skip.get("reason") == "ai_speaking_or_thinking", busy_skip

    bridge.adapter.audio_active = True
    bridge.adapter.current_state = "idle"
    bridge._companion_vision_pending = False
    audio_skip = bridge._companion_vision_tick(force=True)
    assert audio_skip.get("reason") == "ai_speaking_or_thinking", audio_skip

    bridge.adapter.audio_active = False
    bridge._companion_vision_config["user_silence_sec"] = 8.0
    bridge._last_ai_state = "listening"
    bridge._last_ai_state_at = time.monotonic()
    user_state_skip = bridge._companion_vision_tick(force=True)
    assert user_state_skip.get("reason") == "user_state_recently", user_state_skip

    priority_bridge = _make_bridge()
    priority_bridge._companion_vision_config["user_silence_sec"] = 8.0
    priority_bridge._note_voice_priority_subtitle_payload(
        {"data": [{"userId": "local_user_001", "text": "银狼，先回答我这个。", "definite": True}]}
    )
    user_priority_skip = priority_bridge._companion_vision_tick(force=True)
    assert user_priority_skip.get("reason") == "priority_user_speaking", user_priority_skip
    priority_bridge._voice_priority_user_until = 0.0
    priority_bridge._note_ai_state_for_companion("thinking")
    answer_priority_skip = priority_bridge._companion_vision_tick(force=True)
    assert answer_priority_skip.get("reason") == "priority_answering_user", answer_priority_skip
    priority_bridge._note_ai_state_for_companion("idle")
    assert priority_bridge._voice_priority_waiting_for_answer is False
    assert priority_bridge._voice_priority_answer_until > time.monotonic()
    idle_hold_skip = priority_bridge._companion_vision_tick(force=True)
    assert idle_hold_skip.get("reason") == "priority_answering_user", idle_hold_skip

    interrupt_bridge = _make_bridge()
    queued = interrupt_bridge._companion_vision_tick(force=True)
    assert queued.get("queued") is True, queued
    interrupt_bridge._note_ai_state_for_companion("listening")
    assert interrupt_bridge._companion_vision_pending is False
    assert interrupt_bridge._companion_vision_failure_count == 0
    assert interrupt_bridge._companion_vision_last_skip_reason == "cancelled_by_user_priority"
    assert interrupt_bridge._companion_vision_next_allowed_at == 0.0

    timeout_bridge = _make_bridge()
    timeout_first = timeout_bridge._companion_vision_tick(force=True)
    assert timeout_first.get("queued") is True, timeout_first
    timeout_bridge._companion_vision_pending_until = time.monotonic() - 1.0
    timeout_retry = timeout_bridge._companion_vision_tick(force=True)
    assert timeout_retry.get("queued") is True, timeout_retry
    assert timeout_retry.get("id") == 2, timeout_retry
    assert timeout_bridge._companion_vision_pending is True
    assert timeout_bridge._companion_vision_failure_count == 1
    assert timeout_bridge._companion_vision_next_allowed_at <= time.monotonic()
    assert len(timeout_bridge._debug_text_pending) == 2, timeout_bridge._debug_text_pending

    print("companion vision pacer probe passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
