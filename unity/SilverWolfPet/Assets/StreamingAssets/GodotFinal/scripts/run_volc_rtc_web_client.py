from __future__ import annotations

import argparse
import base64
import difflib
import hashlib
import json
import logging
import mimetypes
import os
import re
import socket
import subprocess
import sys
import time
import uuid
import webbrowser
from dataclasses import replace
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from threading import Event, Lock, RLock, Thread, Timer, current_thread
from typing import Any, Mapping
from urllib.parse import urlparse
from urllib.request import urlopen

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))
from src.pet_pose_bridge import create_presentation_client
from src.pet_memory import PetMemoryService, load_project_memory_config
from src.spoken_text_sanitizer import sanitize_spoken_text
from src.voice_backends.volc_rtc import (
    VolcPoseEventAdapter,
    VolcSessionCallbackBridge,
    check_start_voice_chat_config,
)
from src.voice_backends.volc_rtc.local_start_voice_chat_session import LocalStartVoiceChatSession
from src.voice_backends.volc_rtc.rtc_token_manager import ensure_fresh_rtc_token_file
from src.voice_backends.volc_rtc.volc_openapi_client import (
    VolcOpenAPIClient,
    build_update_voice_chat_function_result_request,
)
from src.voice_backends.omnivoice_gateway_tts_provider import (
    OmniVoiceGatewayTTSError,
    OmniVoiceGatewayTTSConfig,
    OmniVoiceGatewayTTSProvider,
)
from src.voice_backends.gpt_sovits_direct_tts_provider import (
    GPTSoVITSDirectTTSError,
    GPTSoVITSDirectTTSConfig,
    GPTSoVITSDirectTTSProvider,
)
from src.voice_backends.volc_rtc.volc_websearch_client import (
    VolcWebSearchClient,
    compact_search_result,
    format_search_answer_context,
)
import requests
from src.vision_observer import (
    S2SNarrationBridge,
    VisionApiClient,
    VisionEventCompressor,
    VisionFrameProvider,
    VisionObserver,
    VolcExternalTextS2SInjector,
    load_narration_router_config,
    load_vision_observer_config,
)
from src.vision_observer.narration_router import NarrationRouter
from src.vision_observer.s2s_bridge import SYSTEM_OBSERVATION_PREFIX


def _default_raw_event_log_path() -> Path:
    explicit = os.getenv("TRANSPARENT_PET_RAW_EVENT_LOG") or os.getenv("SILVERWOLF_RAW_EVENT_LOG")
    if explicit:
        return Path(explicit).expanduser()

    for env_key in ("LOCALAPPDATA", "APPDATA"):
        base = os.getenv(env_key)
        if base:
            return Path(base) / "voicechatpet" / "logs" / "volc_pose_raw_events.jsonl"

    return Path.home() / ".silverwolf_pet" / "logs" / "volc_pose_raw_events.jsonl"


DEFAULT_CONFIG_PATH = ROOT / "config" / "volc_start_voice_chat.local.json"
DEFAULT_RAW_LOG_PATH = _default_raw_event_log_path()
WELCOME_STATE_PATH = DEFAULT_RAW_LOG_PATH.parent / "welcome_state.json"
WEB_ROOT = ROOT / "tools" / "volc_rtc_web"
VENDOR_RTC_PATH = WEB_ROOT / "node_modules" / "@volcengine" / "rtc" / "index.esm.min.js"
FALLBACK_VENDOR_RTC_PATH = ROOT / ".tmp" / "volc-rtc-package" / "package" / "index.esm.min.js"
MEDIAPIPE_TASKS_PATH = WEB_ROOT / "node_modules" / "@mediapipe" / "tasks-vision" / "vision_bundle.mjs"
MEDIAPIPE_WASM_ROOT = WEB_ROOT / "node_modules" / "@mediapipe" / "tasks-vision" / "wasm"
MEDIAPIPE_FACE_MODEL_PATH = WEB_ROOT / "models" / "face_landmarker.task"
mimetypes.add_type("application/wasm", ".wasm")
mimetypes.add_type("text/javascript", ".mjs")
_SUPPORTED_TOOL_NAMES = {"set_pet_pose", "web_search"}
_REALTIME_VOICE_EVENT_TYPES = {
    "remote_audio_properties_report",
    "onremoteaudiopropertiesreport",
    "subtitle_message_received",
    "onsubtitlemessagereceived",
    "subtitle_event",
    "function_call_event",
    "tool_call_event",
    "function_call",
    "tool_call",
    "ai_state_event",
    "conversation_state",
    "task_state",
    "room_message",
    "user_message",
    "onroommessagereceived",
    "onusermessagereceived",
    "room_binary_message",
    "user_binary_message",
    "onroombinarymessagereceived",
    "onuserbinarymessagereceived",
}
_S2S_VISION_UNSUPPORTED_MESSAGE = (
    "Doubao S2S does not support vision; use a local Ark multimodal sidecar for screen understanding."
)
_VISION_CONFIG_MISSING_MESSAGE = "Current voice route has no VisionConfig; choose the traditional visual route first."
_DEFAULT_COMPANION_VISION_PROMPT = (
    "你会收到截图，但不要复述画面、读UI、不要像屏幕解说员。"
    "你只需要说一句提醒、吐槽或建议，搞怪，撒娇和回怼。"
    "不要和之前的内容重复，只输出会被直接念出来的台词，禁止括号动作、舞台说明"
)
_COMPANION_VISION_INTERVAL_PRESETS = (1.0, 2.0, 5.0, 8.0, 10.0, 15.0)
_EXTERNAL_TEXT_TO_LLM_MAX_CHARS = 200
_VOICE_OUTPUT_INTERFACE_VERSION = 1
_VOICE_OUTPUT_BUILTIN_PROVIDERS = ("volc_rtc", "omnivoice_gateway", "gpt_sovits_direct")
_VOICE_OUTPUT_LOCAL_TTS_PROVIDERS = {"omnivoice_gateway", "gpt_sovits_direct"}
_LOCAL_TTS_ERRORS = (OmniVoiceGatewayTTSError, GPTSoVITSDirectTTSError)


class VolcRtcWebBridge:
    def __init__(
        self,
        *,
        config_path: Path,
        godot_host: str,
        godot_port: int,
        presentation_route: str,
        presentation_backend: str,
        presentation_host: str,
        presentation_port: int,
        bot_uid: str,
        raw_log_path: Path,
        logger: logging.Logger,
    ) -> None:
        self.logger = logger
        self.config_path = config_path
        token_result = ensure_fresh_rtc_token_file(_token_refresh_config_path(config_path))
        if token_result.refreshed:
            self.logger.info(
                "RTC token auto-refreshed reason=%s expires_at=%s path=%s",
                token_result.status.reason,
                token_result.status.expire_at,
                config_path,
            )
        else:
            self.logger.info(
                "RTC token ok expires_in_sec=%s room_id=%s user_id=%s",
                token_result.status.expires_in_sec,
                token_result.status.room_id,
                token_result.status.user_id,
            )
        self.config = _load_json_with_env(config_path)
        if _latency_mode_disables_function_calling(self.config):
            _strip_voice_tools_for_latency(self.config)
            self.logger.info("LocalLatencyMode disabled cloud tools for the low-latency voice route.")
        self._memory_service = PetMemoryService.open(
            root=ROOT,
            config=load_project_memory_config(ROOT, self.config),
            logger=self.logger,
        )
        if self._memory_service is not None:
            injected = self._memory_service.inject_startup_system_messages(self.config)
            self.logger.info("Pet memory startup injection count=%s status=%s", injected, self._memory_service.status())
        self.request = _extract_start_voice_chat_request(self.config)
        _ensure_direct_rtc_vision_uses_screen_stream(self.request, self.logger)
        self._base_task_id = str(self.request.get("TaskId") or "pet_voice_task")
        self._active_task_id = self._base_task_id
        self._active_request = self.request
        self._log_rtc_identity_consistency()
        self._voice_output_config = _voice_output_mapping(self.config)
        self._voice_output_requested_provider = _normalize_voice_output_provider(
            self._voice_output_config.get("Provider", self._voice_output_config.get("provider", "volc_rtc"))
        )
        self._voice_output_fallback_provider = _normalize_voice_output_provider(
            self._voice_output_config.get("FallbackProvider", self._voice_output_config.get("fallback_provider", "volc_rtc"))
        )
        self._voice_output_effective_provider = self._voice_output_requested_provider
        self._voice_output_last_error = ""
        self._voice_output_last_state = "idle"
        self._voice_output_last_job_id = ""
        self._voice_output_last_text_hash = ""
        self._omnivoice_queue_lock = RLock()
        self._omnivoice_pending_texts: list[str] = []
        self._omnivoice_active_job_text: dict[str, str] = {}
        self._omnivoice_caption_sent_jobs: set[str] = set()
        self._omnivoice_recent_ai_texts: list[tuple[float, str]] = []
        self._recent_ai_echo_texts: list[tuple[float, str]] = []
        self._recent_ai_echo_rounds: dict[str, float] = {}
        self._active_ai_playback_texts: list[tuple[float, str]] = []
        self._active_ai_playback_until = 0.0
        self._omnivoice_retry_count_by_hash: dict[str, int] = {}
        self._omnivoice_echo_window_seconds = max(
            2.0,
            _safe_float(
                self._voice_output_config.get(
                    "EchoFilterWindowSeconds",
                    self._voice_output_config.get("echoFilterWindowSeconds", 14.0),
                ),
                14.0,
            ),
        )
        self._omnivoice_echo_similarity_threshold = _clamp_float(
            self._voice_output_config.get(
                "EchoFilterSimilarity",
                self._voice_output_config.get("echoFilterSimilarity", 0.62),
            ),
            0.62,
            0.45,
            0.95,
        )
        self._omnivoice_retry_delay_seconds = max(
            0.25,
            _safe_float(
                self._voice_output_config.get(
                    "BusyRetryDelaySeconds",
                    self._voice_output_config.get("busyRetryDelaySeconds", 1.0),
                ),
                1.0,
            ),
        )
        self._omnivoice_max_busy_retries = max(
            1,
            int(
                _safe_float(
                    self._voice_output_config.get(
                        "MaxBusyRetries",
                        self._voice_output_config.get("maxBusyRetries", 8),
                    ),
                    8.0,
                )
            ),
        )
        self._omnivoice_queue_limit = max(
            1,
            int(
                _safe_float(
                    self._voice_output_config.get(
                        "QueueLimit",
                        self._voice_output_config.get("queueLimit", 12),
                    ),
                    12.0,
                )
            ),
        )
        self._omnivoice_inter_utterance_pause_seconds = _clamp_float(
            self._voice_output_config.get(
                "InterUtterancePauseSeconds",
                self._voice_output_config.get("interUtterancePauseSeconds", 0.45),
            ),
            0.45,
            0.0,
            1.5,
        )
        self._omnivoice_provider: OmniVoiceGatewayTTSProvider | GPTSoVITSDirectTTSProvider | None = None
        self.bot_uid = bot_uid or _find_bot_uid(self.request)
        presentation_client = create_presentation_client(
            root=ROOT,
            config=self.config,
            route_id=presentation_route,
            backend=presentation_backend,
            host=presentation_host,
            port=presentation_port,
            legacy_godot_host=godot_host,
            legacy_godot_port=godot_port,
            default_timeout_sec=0.05,
            offline_cooldown_sec=0.35,
        )
        self.logger.info(
            "Presentation route backend=%s host=%s port=%s",
            getattr(presentation_client, "backend", "unknown"),
            getattr(presentation_client, "host", ""),
            getattr(presentation_client, "port", ""),
        )
        self.adapter = VolcPoseEventAdapter(
            godot_client=presentation_client,
            bot_uids={self.bot_uid} if self.bot_uid else set(),
            raw_event_log_path=raw_log_path,
            on_send=self._on_pose_send,
            on_user_vision_intent=self._on_user_vision_intent,
            on_user_voice_stop_intent=self._on_user_voice_stop_intent,
            on_user_speech_activity=self._on_user_speech_activity,
        )
        self._omnivoice_provider = self._create_omnivoice_provider()
        self.callbacks = VolcSessionCallbackBridge(
            self.adapter,
            on_subtitle_event=self._on_rtc_subtitle_for_voice_output,
            on_ai_state_event=self._on_rtc_ai_state_for_voice_output,
        )
        self._openapi_client = VolcOpenAPIClient.from_config(self.config)
        self._websearch_client = VolcWebSearchClient.from_project_config(self.config)
        self._session: LocalStartVoiceChatSession | None = None
        self._session_lock = Lock()
        self._pending_tool_call_meta: list[dict[str, Any]] = []
        self._sent_records: list[dict[str, Any]] = []
        self._last_audio_pose_log_at = 0.0
        self._voice_active = False
        self._stopped_at = 0.0
        self._last_stale_event_log_at = 0.0
        self._welcome_subtitle_sent = False
        self._welcome_subtitle_ever_sent = False
        self._vision_lock = Lock()
        self._vision_desired = False
        self._vision_client_state: dict[str, Any] = {
            "screen_published": False,
            "updated_at": 0.0,
            "message": "",
        }
        self._camera_lock = Lock()
        self._camera_desired = False
        self._camera_client_state: dict[str, Any] = {
            "camera_published": False,
            "updated_at": 0.0,
            "message": "",
        }
        self._managed_camera_hub_process = None
        self._managed_camera_hub_last_start_at = 0.0
        self._camera_last_start_at = 0.0
        self._camera_stop_startup_grace_sec = 2.5
        self._screen_vision_settings = _stream_settings_from_config(
            self.config,
            (("ScreenVision",), ("VisionStream",), ("ScreenStream",), ("ClientRTC", "ScreenVision")),
            {
                "width": 1920,
                "height": 1080,
                "snapshotHeight": 1080,
                "fps": 3,
                "maxKbps": 3000,
                "cameraOverlayEnabled": False,
                "cameraOverlayWidth": 640,
                "cameraOverlayHeight": 360,
                "cameraOverlayPadding": 24,
                "cameraOverlayPosition": "bottomLeft",
                "cameraOverlaySourceUrl": "http://127.0.0.1:17863/stream.mjpg",
            },
        )
        self._camera_video_settings = _stream_settings_from_config(
            self.config,
            (("CameraVideo",), ("CameraStream",), ("ClientRTC", "CameraVideo")),
            {
                "width": 1280,
                "height": 720,
                "fps": 15,
                "maxKbps": 3000,
                "faceTrackingPacketFps": 15,
                "useCameraHub": False,
                "cameraHubUrl": "http://127.0.0.1:17863/stream.mjpg",
                "useVirtualCamera": True,
                "requireVirtualCamera": True,
                "sendFaceTrackingPackets": False,
                "deviceKeyword": "virtual,obs",
            },
        )
        self._face_tracking_lock = Lock()
        self._face_tracking_udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self._face_tracking_host = str(
            _pick_nested(self.config, ("FaceTracking", "Host"), ("FaceTracking", "host"))
            or os.environ.get("TRANSPARENT_PET_FACE_TRACKING_HOST")
            or "127.0.0.1"
        )
        self._face_tracking_port = int(
            _safe_float(
                _pick_nested(self.config, ("FaceTracking", "Port"), ("FaceTracking", "port"))
                or os.environ.get("TRANSPARENT_PET_FACE_TRACKING_PORT")
                or 5055,
                5055,
            )
        )
        self._face_tracking_client_state: dict[str, Any] = {
            "packet_count": 0,
            "last_packet_at": 0.0,
            "last_error": "",
            "host": self._face_tracking_host,
            "port": self._face_tracking_port,
        }
        self._last_vision_intent_at = 0.0
        self._last_vision_intent_text = ""
        speech_watchdog = self.config.get("SpeechTurnWatchdog", {})
        if not isinstance(speech_watchdog, Mapping):
            speech_watchdog = {}
        self._speech_watchdog_enabled = bool(speech_watchdog.get("Enabled", True))
        self._speech_watchdog_delay_sec = max(1.5, _safe_float(speech_watchdog.get("DelaySec", 1.5), 1.5))
        self._speech_watchdog_direct_fallback_enabled = _safe_bool(
            speech_watchdog.get("DirectFallbackEnabled", speech_watchdog.get("direct_fallback_enabled", True)), True
        )
        self._speech_watchdog_direct_fallback_sec = max(
            self._speech_watchdog_delay_sec,
            _safe_float(
                speech_watchdog.get("DirectFallbackSec", speech_watchdog.get("direct_fallback_sec", 3.5)),
                3.5,
            ),
        )
        self._speech_watchdog_direct_fallback_timeout_sec = max(
            3.0,
            _safe_float(
                speech_watchdog.get("DirectFallbackTimeoutSec", speech_watchdog.get("direct_fallback_timeout_sec", 8.0)),
                8.0,
            ),
        )
        self._speech_watchdog_busy_grace_sec = max(
            self._speech_watchdog_delay_sec + 4.0,
            _safe_float(speech_watchdog.get("BusyGraceSec", 10.0), 10.0),
        )
        self._speech_watchdog_stale_thinking_recover_sec = max(
            self._speech_watchdog_busy_grace_sec + 5.0,
            _safe_float(speech_watchdog.get("StaleThinkingRecoverSec", 28.0), 28.0),
        )
        self._speech_watchdog_busy_retry_interval_sec = max(
            0.8,
            _safe_float(speech_watchdog.get("BusyRetryIntervalSec", 1.5), 1.5),
        )
        self._speech_watchdog_busy_retry_max_sec = max(
            self._speech_watchdog_busy_grace_sec + self._speech_watchdog_delay_sec,
            _safe_float(speech_watchdog.get("BusyRetryMaxSec", 22.0), 22.0),
        )
        self._speech_watchdog_recovery_cooldown_sec = max(
            self._speech_watchdog_stale_thinking_recover_sec + 10.0,
            _safe_float(speech_watchdog.get("StaleThinkingRecoverCooldownSec", 45.0), 45.0),
        )
        self._speech_watchdog_lock = Lock()
        self._speech_watchdog_timer: Timer | None = None
        self._speech_watchdog_turn_id = 0
        self._speech_watchdog_user_text = ""
        self._speech_watchdog_user_at = 0.0
        self._speech_watchdog_ai_seen = False
        self._speech_watchdog_retry_turn_id = 0
        self._speech_watchdog_busy_retry_count = 0
        self._speech_watchdog_direct_fallback_turn_id = 0
        self._speech_watchdog_direct_fallback_in_flight = False
        self._speech_watchdog_direct_fallback_count = 0
        self._speech_watchdog_last_retry_at = 0.0
        self._speech_watchdog_last_skip_reason = ""
        self._speech_recovery_lock = Lock()
        self._speech_recovery_in_progress = False
        self._speech_recovery_last_at = 0.0
        self._speech_recovery_count = 0
        self._speech_recovery_last_reason = ""
        self._debug_text_lock = Lock()
        self._debug_text_next_id = 1
        self._debug_text_pending: list[dict[str, Any]] = []
        self._debug_text_results: list[dict[str, Any]] = []
        self._companion_vision_config = _companion_vision_config(self.config)
        self._companion_vision_lock = RLock()
        self._companion_vision_stop_event = Event()
        self._companion_vision_thread: Thread | None = None
        self._companion_vision_running = False
        self._companion_vision_last_prompt_at = 0.0
        self._companion_vision_pending = False
        self._companion_vision_pending_id = 0
        self._companion_vision_pending_until = 0.0
        self._companion_vision_pending_has_response = False
        self._companion_vision_pending_text = ""
        self._companion_vision_last_skip_reason = ""
        self._companion_vision_failure_count = 0
        self._companion_vision_next_allowed_at = 0.0
        self._companion_vision_last_success_at = 0.0
        self._companion_vision_response_active = False
        self._companion_vision_fallback_in_flight = False
        self._companion_vision_empty_fallback_count = 0
        self._companion_vision_last_fallback_error = ""
        self._companion_vision_recent_ai_texts: list[tuple[float, str]] = []
        self._companion_vision_output_history: list[tuple[float, str]] = []
        self._companion_vision_static_comment_index = 0
        self._companion_vision_waiting_for_welcome_done = False
        self._companion_vision_welcome_seen_speaking = False
        self._companion_vision_welcome_wait_started_at = 0.0
        self._last_ai_state = "idle"
        self._last_ai_state_at = 0.0
        self._voice_priority_user_until = 0.0
        self._voice_priority_waiting_for_answer = False
        self._voice_priority_waiting_until = 0.0
        self._voice_priority_answer_until = 0.0
        self._voice_priority_last_reason = ""
        self._vision_observer_config = load_vision_observer_config(self.config)
        self._narration_router_config = load_narration_router_config(self.config, self._vision_observer_config)
        self._vision_observer = self._create_vision_observer()

    def _log_rtc_identity_consistency(self) -> None:
        client_rtc = self.config.setdefault("ClientRTC", {})
        if not isinstance(client_rtc, dict):
            return
        user_id = _first_text(self.request.get("AgentConfig", {}).get("TargetUserId", []))
        if not user_id:
            user_id = str(client_rtc.get("UserId") or client_rtc.get("UserID") or "local_user_001")
        room_id = str(self.request.get("RoomId") or "")
        task_id = str(self.request.get("TaskId") or "")
        agent_config = self.request.get("AgentConfig", {})
        bot_uid = str(agent_config.get("UserId") or "") if isinstance(agent_config, Mapping) else ""
        self.logger.info(
            "Using fixed RTC identity room_id=%s task_id=%s bot_uid=%s target_user=%s token_present=%s",
            room_id,
            task_id,
            bot_uid,
            user_id,
            bool(client_rtc.get("Token") or client_rtc.get("token")),
        )

    def _create_vision_observer(self) -> VisionObserver | None:
        if not self._vision_observer_config.enabled:
            return None
        compressor = VisionEventCompressor(
            max_chars=self._vision_observer_config.max_context_chars,
            default_chars=min(120, self._vision_observer_config.max_context_chars),
            dedupe_window_ms=self._vision_observer_config.speak_cooldown_ms,
        )
        injector = VolcExternalTextS2SInjector(self._queue_external_text)
        self.logger.info("VisionObserver speech route: ExternalTextToLLM")
        narration_bridge = S2SNarrationBridge(
            injector=injector,
            compressor=compressor,
        )
        return VisionObserver(
            config=self._vision_observer_config,
            frame_provider=VisionFrameProvider(self._vision_observer_config),
            api_client=VisionApiClient(self._vision_observer_config),
            router=NarrationRouter(self._narration_router_config),
            narration_bridge=narration_bridge,
            runtime_status=self._runtime_status_for_vision,
            logger=self.logger,
        )

    def _runtime_status_for_vision(self) -> dict[str, Any]:
        status = dict(self.adapter.runtime_status())
        recent_user_monotonic = float(getattr(self.adapter, "_recent_user_subtitle_at", 0.0) or 0.0)
        if recent_user_monotonic > 0.0:
            age_ms = int(max(0.0, time.monotonic() - recent_user_monotonic) * 1000.0)
            status["last_user_speech_at_ms"] = int(time.time() * 1000) - age_ms
        status["last_active_speech_at_ms"] = int(time.time() * 1000) if status.get("audio_active") else 0
        ready_for_external_text = (
            self._voice_active
            and self._last_ai_state == "idle"
            and self._last_ai_state_at > 0.0
        )
        status["task_state"] = "voice_active" if ready_for_external_text else "voice_warming_up"
        return status

    def _create_omnivoice_provider(self) -> OmniVoiceGatewayTTSProvider | GPTSoVITSDirectTTSProvider | None:
        if self._voice_output_requested_provider not in _VOICE_OUTPUT_LOCAL_TTS_PROVIDERS:
            self._voice_output_effective_provider = "volc_rtc"
            return None

        if self._voice_output_requested_provider == "gpt_sovits_direct":
            config = GPTSoVITSDirectTTSConfig.from_mapping(self._voice_output_config, root=ROOT)
            if not config.is_ready():
                self._voice_output_last_error = "GPT-SoVITS config incomplete: base URL or ref audio path missing."
                self._voice_output_effective_provider = self._voice_output_fallback_provider or "volc_rtc"
                self.logger.warning(
                    "GPT-SoVITS disabled; config incomplete base_urls=%s ref_audio_path_present=%s fallback=%s",
                    list(config.base_urls),
                    bool(config.ref_audio_path),
                    self._voice_output_effective_provider,
                )
                return None

            self._voice_output_effective_provider = "gpt_sovits_direct"
            self._voice_output_last_error = ""
            self.logger.info(
                "VoiceOutput provider=gpt_sovits_direct base_urls=%s ref_audio=%s streaming_mode=%s media_type=%s",
                list(config.base_urls),
                config.ref_audio_path,
                config.streaming_mode,
                config.media_type,
            )
            return GPTSoVITSDirectTTSProvider(
                config,
                on_state_change=self._on_omnivoice_state_change,
                on_segment_ready=self._on_omnivoice_segment_ready,
            )

        config = OmniVoiceGatewayTTSConfig.from_mapping(self._voice_output_config, root=ROOT)
        if not config.is_ready():
            self._voice_output_last_error = "OmniVoice config incomplete: token, gateway URL, or voice id missing."
            self._voice_output_effective_provider = self._voice_output_fallback_provider or "volc_rtc"
            self.logger.warning(
                "OmniVoice disabled; config incomplete token_present=%s gateway=%s voice_id=%s fallback=%s",
                bool(config.api_token),
                config.gateway_url,
                config.voice_id,
                self._voice_output_effective_provider,
            )
            return None

        self._voice_output_effective_provider = "omnivoice_gateway"
        self._voice_output_last_error = ""
        self.logger.info(
            "VoiceOutput provider=omnivoice_gateway gateway=%s voice_id=%s pseudo_stream=%s",
            config.gateway_url,
            config.voice_id,
            config.pseudo_stream,
        )
        return OmniVoiceGatewayTTSProvider(
            config,
            on_state_change=self._on_omnivoice_state_change,
            on_segment_ready=self._on_omnivoice_segment_ready,
        )

    def _on_rtc_subtitle_for_voice_output(self, event: Mapping[str, Any]) -> bool | None:
        if self._omnivoice_provider is None or self._voice_output_effective_provider not in _VOICE_OUTPUT_LOCAL_TTS_PROVIDERS:
            return True

        text = _first_present_text(event, "text", "message", "content", "subtitle", "transcript", "utterance")
        if not text:
            return True

        speaker = _subtitle_speaker(event, self.bot_uid)
        is_final = _subtitle_is_final(event)
        if speaker == "user":
            self._stop_omnivoice("user_subtitle_final" if is_final else "user_subtitle")
            return True

        if speaker != "ai":
            return True
        if not is_final:
            return False

        normalized = _normalize_subtitle_plain_text(text)
        if not normalized:
            return False
        self._remember_omnivoice_ai_text(normalized)
        self._queue_omnivoice_caption_text(normalized)
        return False

    def _remember_omnivoice_ai_text(self, text: str) -> None:
        normalized = _normalize_for_echo_compare(text)
        if not normalized:
            return
        now = time.monotonic()
        with self._omnivoice_queue_lock:
            self._prune_omnivoice_echo_texts_locked(now)
            self._omnivoice_recent_ai_texts.append((now, normalized))
            if len(self._omnivoice_recent_ai_texts) > 16:
                del self._omnivoice_recent_ai_texts[: len(self._omnivoice_recent_ai_texts) - 16]

    def _remember_recent_ai_echo_text(self, text: str) -> None:
        normalized = _normalize_for_echo_compare(text)
        if not normalized:
            return
        now = time.monotonic()
        with self._omnivoice_queue_lock:
            self._prune_recent_ai_echo_texts_locked(now)
            self._recent_ai_echo_texts.append((now, normalized))
            if len(self._recent_ai_echo_texts) > 24:
                del self._recent_ai_echo_texts[: len(self._recent_ai_echo_texts) - 24]
            self._remember_active_ai_playback_text_locked(normalized, now)

    def _mark_ai_playback_state(self, state: str) -> None:
        normalized = _normalize_bridge_ai_state(state)
        now = time.monotonic()
        with self._omnivoice_queue_lock:
            self._prune_recent_ai_echo_texts_locked(now)
            if normalized == "speaking":
                self._active_ai_playback_until = max(self._active_ai_playback_until, now + 4.0)
            elif normalized == "idle":
                self._active_ai_playback_until = max(self._active_ai_playback_until, now + 2.5)
            elif normalized in {"listening", "interrupted"}:
                self._active_ai_playback_until = max(self._active_ai_playback_until, now + 1.2)

    def _remember_active_ai_playback_text_locked(self, normalized: str, now: float) -> None:
        if not normalized:
            return
        self._active_ai_playback_texts = [
            (timestamp, value)
            for timestamp, value in self._active_ai_playback_texts
            if now - timestamp <= max(2.0, self._omnivoice_echo_window_seconds)
        ]
        if self._active_ai_playback_texts and self._active_ai_playback_texts[-1][1] == normalized:
            self._active_ai_playback_texts[-1] = (now, normalized)
        else:
            self._active_ai_playback_texts.append((now, normalized))
        if len(self._active_ai_playback_texts) > 12:
            del self._active_ai_playback_texts[: len(self._active_ai_playback_texts) - 12]
        self._active_ai_playback_until = max(self._active_ai_playback_until, now + 4.0)

    def _filter_omnivoice_self_echo_subtitles(
        self,
        data: Any,
        *,
        trace_id: str,
        sender_uid: str = "",
    ) -> Any | None:
        if self._omnivoice_provider is None or self._voice_output_effective_provider not in _VOICE_OUTPUT_LOCAL_TTS_PROVIDERS:
            return data

        def should_drop(item: Mapping[str, Any]) -> bool:
            if not self._is_user_subtitle_item(item, sender_uid=sender_uid):
                return False
            text = _first_present_text(item, "text", "message", "content", "subtitle", "transcript", "utterance")
            if not text or not self._is_omnivoice_self_echo_text(text):
                return False
            self._write_omnivoice_self_echo_record(item, trace_id=trace_id, sender_uid=sender_uid, text=text)
            return True

        if isinstance(data, Mapping) and isinstance(data.get("data"), list):
            filtered = [item for item in data.get("data") or [] if not (isinstance(item, Mapping) and should_drop(item))]
            if not filtered:
                return None
            if len(filtered) == len(data.get("data") or []):
                return data
            updated = dict(data)
            updated["data"] = filtered
            return updated
        if isinstance(data, list):
            filtered = [item for item in data if not (isinstance(item, Mapping) and should_drop(item))]
            return filtered or None
        if isinstance(data, Mapping) and should_drop(data):
            return None
        return data

    def _is_user_subtitle_item(self, item: Mapping[str, Any], *, sender_uid: str = "") -> bool:
        uid = _first_present_text(item, "userId", "userID", "user_id", "uid")
        speaker = _first_present_text(item, "speaker", "role", "user_type", "source").strip().lower()
        if uid:
            return uid != self.bot_uid
        if sender_uid:
            return sender_uid != self.bot_uid
        return "user" in speaker and not any(token in speaker for token in ("ai", "bot", "assistant"))

    def _is_omnivoice_self_echo_text(self, text: str) -> bool:
        candidate = _normalize_for_echo_compare(text)
        if not candidate:
            return False
        state = self._omnivoice_provider.state if self._omnivoice_provider is not None else "idle"
        now = time.monotonic()
        with self._omnivoice_queue_lock:
            self._prune_omnivoice_echo_texts_locked(now)
            references = [_normalize_for_echo_compare(value) for value in self._omnivoice_active_job_text.values()]
            references.extend(value for _, value in self._omnivoice_recent_ai_texts)
        references = [value for value in references if value]
        if not references:
            return False
        echo_active = state in {"thinking", "speaking"} or bool(self._omnivoice_active_job_text)
        stop_words = {
            "\u505c",
            "\u505c\u4e0b",
            "\u95ed\u5634",
            "\u522b\u8bf4",
            "\u9000\u51fa",
            "\u7ed3\u675f",
            "\u6682\u505c",
        }
        if candidate in stop_words:
            return False
        for reference in references:
            if not reference:
                continue
            if len(candidate) < 4:
                if echo_active and candidate in reference and candidate not in stop_words:
                    return True
                continue
            if candidate in reference or reference in candidate:
                return True
            ratio = difflib.SequenceMatcher(None, candidate, reference).ratio()
            if ratio >= self._omnivoice_echo_similarity_threshold:
                return True
            if len(candidate) >= 8 and _longest_common_substring_len(candidate, reference) >= min(len(candidate) - 1, 10):
                return True
        return False

    def _prune_omnivoice_echo_texts_locked(self, now: float) -> None:
        cutoff = now - self._omnivoice_echo_window_seconds
        self._omnivoice_recent_ai_texts = [(ts, value) for ts, value in self._omnivoice_recent_ai_texts if ts >= cutoff]
        self._prune_recent_ai_echo_texts_locked(now)

    def _filter_recent_ai_self_echo_subtitles(
        self,
        data: Any,
        *,
        trace_id: str,
        sender_uid: str = "",
    ) -> Any | None:
        def should_drop(item: Mapping[str, Any]) -> bool:
            if not self._is_user_subtitle_item(item, sender_uid=sender_uid):
                return False
            text = _first_present_text(item, "text", "message", "content", "subtitle", "transcript", "utterance")
            if not text or not self._is_recent_ai_echo_text(text):
                return False
            self._write_recent_ai_self_echo_record(item, trace_id=trace_id, sender_uid=sender_uid, text=text)
            return True

        if isinstance(data, Mapping) and isinstance(data.get("data"), list):
            filtered = [item for item in data.get("data") or [] if not (isinstance(item, Mapping) and should_drop(item))]
            if not filtered:
                return None
            if len(filtered) == len(data.get("data") or []):
                return data
            updated = dict(data)
            updated["data"] = filtered
            return updated
        if isinstance(data, list):
            filtered = [item for item in data if not (isinstance(item, Mapping) and should_drop(item))]
            return filtered or None
        if isinstance(data, Mapping) and should_drop(data):
            return None
        return data

    def _is_recent_ai_echo_text(self, text: str) -> bool:
        candidate = _normalize_for_echo_compare(text)
        if not candidate:
            return False
        stop_words = {"停", "停下", "闭嘴", "别说", "退出", "结束", "暂停"}
        if candidate in stop_words:
            return False
        now = time.monotonic()
        with self._omnivoice_queue_lock:
            self._prune_recent_ai_echo_texts_locked(now)
            references = [value for _, value in self._recent_ai_echo_texts]
            playback_active = now <= self._active_ai_playback_until
            if playback_active:
                references.extend(value for _, value in self._active_ai_playback_texts)
        for reference in references:
            if not reference:
                continue
            if playback_active and len(candidate) < 4 and candidate in reference:
                return True
            if candidate in reference or reference in candidate:
                return True
            if len(candidate) >= 4 and difflib.SequenceMatcher(None, candidate, reference).ratio() >= self._omnivoice_echo_similarity_threshold:
                return True
            if len(candidate) >= 8 and _longest_common_substring_len(candidate, reference) >= min(len(candidate) - 1, 10):
                return True
        return False

    def _prune_recent_ai_echo_texts_locked(self, now: float) -> None:
        cutoff = now - self._omnivoice_echo_window_seconds
        self._recent_ai_echo_texts = [(ts, value) for ts, value in self._recent_ai_echo_texts if ts >= cutoff]
        self._active_ai_playback_texts = [(ts, value) for ts, value in self._active_ai_playback_texts if ts >= cutoff]
        if now > self._active_ai_playback_until and not self._active_ai_playback_texts:
            self._active_ai_playback_until = 0.0
        expired = [key for key, expires_at in self._recent_ai_echo_rounds.items() if expires_at < now]
        for key in expired:
            self._recent_ai_echo_rounds.pop(key, None)

    def _remember_recent_ai_echo_round_locked(self, item: Mapping[str, Any]) -> None:
        key = _echo_round_key(item)
        if not key:
            return
        now = time.monotonic()
        self._prune_recent_ai_echo_texts_locked(now)
        self._recent_ai_echo_rounds[key] = now + max(8.0, self._omnivoice_echo_window_seconds)

    def _is_recent_ai_echo_round_locked(self, event: Mapping[str, Any]) -> bool:
        key = _echo_round_key(event)
        if not key:
            return False
        now = time.monotonic()
        self._prune_recent_ai_echo_texts_locked(now)
        if key in self._recent_ai_echo_rounds:
            return True
        round_id = _first_present_text(event, "RoundID", "roundID", "roundId", "round_id")
        return bool(round_id and f"*:{round_id}" in self._recent_ai_echo_rounds)

    def _write_omnivoice_self_echo_record(
        self,
        item: Mapping[str, Any],
        *,
        trace_id: str,
        sender_uid: str,
        text: str,
    ) -> None:
        self.adapter.sender.write_event_record(
            trace_id=trace_id or _new_trace_id("echo"),
            event_type="subtitle_event",
            source="subtitle:self_echo_filtered",
            raw_payload={"item": item, "sender_uid": sender_uid},
            mapping_result={"ignored": True, "reason": "omnivoice_self_echo", "text": _clip_companion_context_line(text, max_chars=80)},
            send_to_godot=False,
            event_received_at=time.time(),
            pose_generated_at=time.time(),
        )

    def _write_recent_ai_self_echo_record(
        self,
        item: Mapping[str, Any],
        *,
        trace_id: str,
        sender_uid: str,
        text: str,
    ) -> None:
        with self._omnivoice_queue_lock:
            self._remember_recent_ai_echo_round_locked(item)
        self.adapter.sender.write_event_record(
            trace_id=trace_id or _new_trace_id("echo"),
            event_type="subtitle_event",
            source="subtitle:recent_ai_echo_filtered",
            raw_payload={"item": item, "sender_uid": sender_uid},
            mapping_result={"ignored": True, "reason": "recent_ai_self_echo", "text": _clip_companion_context_line(text, max_chars=80)},
            send_to_godot=False,
            event_received_at=time.time(),
            pose_generated_at=time.time(),
        )

    def _drop_recent_ai_echo_state_event(self, event: Mapping[str, Any], trace_id: str) -> bool:
        stage_code = _extract_stage_code(event)
        if stage_code not in {1, 2, 3, 4, 5}:
            return False
        with self._omnivoice_queue_lock:
            if not self._is_recent_ai_echo_round_locked(event):
                return False
        if stage_code == 5:
            with self._omnivoice_queue_lock:
                self._recent_ai_echo_rounds.pop(_echo_round_key(event), None)
                round_id = _first_present_text(event, "RoundID", "roundID", "roundId", "round_id")
                if round_id:
                    self._recent_ai_echo_rounds.pop(f"*:{round_id}", None)
        if stage_code in {2, 3, 5}:
            self._force_idle_presentation("recent_ai_echo_round")
        self.adapter.sender.write_event_record(
            trace_id=trace_id or _new_trace_id("echo-state"),
            event_type="ai_state_event",
            source="ai_state:recent_ai_echo_filtered",
            raw_payload=dict(event),
            mapping_result={"ignored": True, "reason": "recent_ai_echo_round", "stageCode": stage_code},
            send_to_godot=False,
            event_received_at=time.time(),
            pose_generated_at=time.time(),
        )
        return True

    def _queue_omnivoice_text(self, text: str, *, front: bool = False) -> None:
        if self._omnivoice_provider is None:
            return
        clean_text = _normalize_subtitle_plain_text(text)
        if not clean_text:
            return
        with self._omnivoice_queue_lock:
            if clean_text in self._omnivoice_active_job_text.values() or clean_text in self._omnivoice_pending_texts:
                return
            if not self._omnivoice_is_busy_locked():
                self._start_omnivoice_text_locked(clean_text)
                return
            if self._omnivoice_pending_texts and self._omnivoice_pending_texts[-1] == clean_text:
                return
            if front:
                self._omnivoice_pending_texts.insert(0, clean_text)
            else:
                self._omnivoice_pending_texts.append(clean_text)
            if len(self._omnivoice_pending_texts) > self._omnivoice_queue_limit:
                del self._omnivoice_pending_texts[: len(self._omnivoice_pending_texts) - self._omnivoice_queue_limit]
            self.logger.info(
                "OmniVoice text queued pending=%s chars=%s text_hash=%s",
                len(self._omnivoice_pending_texts),
                len(clean_text),
                text_hash_for_log(clean_text),
            )

    def _queue_omnivoice_caption_text(self, text: str) -> None:
        segments = _split_omnivoice_caption_segments(text)
        if not segments:
            return
        for segment in segments:
            self._queue_omnivoice_text(segment)

    def _omnivoice_is_busy_locked(self) -> bool:
        if self._omnivoice_provider is None:
            return False
        return bool(self._omnivoice_active_job_text or self._omnivoice_provider.state in {"thinking", "speaking"})

    def _start_omnivoice_text_locked(self, text: str) -> None:
        try:
            job_id = self._omnivoice_provider.speak(text)
            stats = self._omnivoice_provider.last_stats
            self._omnivoice_active_job_text[job_id] = text
            self._voice_output_last_job_id = job_id
            self._voice_output_last_text_hash = stats.text_hash if stats is not None else ""
            self._voice_output_last_error = ""
            self.logger.info(
                "OmniVoice speak queued job_id=%s chars=%s text_hash=%s",
                job_id,
                len(text),
                self._voice_output_last_text_hash,
            )
        except _LOCAL_TTS_ERRORS as exc:
            self._voice_output_last_error = str(exc)
            self.logger.warning("OmniVoice speak rejected: %s", exc)
            if _is_omnivoice_busy_error(str(exc)):
                self._schedule_omnivoice_retry_locked(text, str(exc))
                return
            self._fallback_voice_output_after_omnivoice_failure(str(exc))

    def _drain_omnivoice_queue(self) -> None:
        with self._omnivoice_queue_lock:
            if self._omnivoice_provider is None or self._omnivoice_is_busy_locked() or not self._omnivoice_pending_texts:
                return
            text = self._omnivoice_pending_texts.pop(0)
            self._start_omnivoice_text_locked(text)

    def _schedule_omnivoice_retry_locked(self, text: str, reason: str) -> None:
        retry_count = self._omnivoice_retry_count_by_hash.get(text_hash_for_log(text), 0) + 1
        self._omnivoice_retry_count_by_hash[text_hash_for_log(text)] = retry_count
        if retry_count > self._omnivoice_max_busy_retries:
            self.logger.warning("OmniVoice busy retry exhausted retries=%s reason=%s", retry_count - 1, reason)
            if self._voice_output_requested_provider == "gpt_sovits_direct":
                Timer(0.05, self._drain_omnivoice_queue).start()
                return
            self._fallback_voice_output_after_omnivoice_failure(reason)
            return
        if text not in self._omnivoice_pending_texts:
            self._omnivoice_pending_texts.insert(0, text)
        delay = self._omnivoice_retry_delay_seconds * min(3.0, float(retry_count))
        self.logger.info(
            "OmniVoice gateway busy; retry scheduled delay=%.2fs retry=%s pending=%s",
            delay,
            retry_count,
            len(self._omnivoice_pending_texts),
        )
        Timer(delay, self._drain_omnivoice_queue).start()

    def _on_rtc_ai_state_for_voice_output(self, event: Mapping[str, Any] | str) -> None:
        state = _bridge_ai_state_from_event(event)
        if state in {"listening", "interrupted"}:
            self._stop_omnivoice(state)

    def _on_omnivoice_state_change(self, state: str, payload: Mapping[str, Any]) -> None:
        self._voice_output_last_state = state
        if state == "error":
            self._voice_output_last_error = str(payload.get("error") or "OmniVoice playback error")
            if self._voice_output_requested_provider == "gpt_sovits_direct":
                job_id = str(payload.get("job_id") or "")
                with self._omnivoice_queue_lock:
                    text = self._omnivoice_active_job_text.pop(job_id, "")
                    if text:
                        self._schedule_omnivoice_retry_locked(text, self._voice_output_last_error)
                    elif self._omnivoice_pending_texts:
                        Timer(0.05, self._drain_omnivoice_queue).start()
                self._send_voice_output_pose(
                    {
                        "type": "pet_pose",
                        "state": "idle",
                        "mouth": "audio_volume",
                        "mouth_open": 0.0,
                        "audio_active": False,
                        "overlay_only": True,
                    },
                    event_type="gpt_sovits_retryable_error",
                    raw_payload=payload,
                )
                return
            if _is_omnivoice_busy_error(self._voice_output_last_error):
                job_id = str(payload.get("job_id") or "")
                with self._omnivoice_queue_lock:
                    text = self._omnivoice_active_job_text.pop(job_id, "")
                    if text:
                        self._schedule_omnivoice_retry_locked(text, self._voice_output_last_error)
                self._send_voice_output_pose(
                    {
                        "type": "pet_pose",
                        "state": "idle",
                        "mouth": "audio_volume",
                        "mouth_open": 0.0,
                        "audio_active": False,
                        "overlay_only": True,
                    },
                    event_type="omnivoice_busy_retry",
                    raw_payload=payload,
                )
                return
            self._send_voice_output_pose(
                {
                    "type": "pet_pose",
                    "state": "idle",
                    "mouth": "audio_volume",
                    "mouth_open": 0.0,
                    "audio_active": False,
                    "overlay_only": True,
                },
                event_type="omnivoice_error",
                raw_payload=payload,
            )
            self._fallback_voice_output_after_omnivoice_failure(self._voice_output_last_error)
            return

        if state == "thinking":
            self._send_voice_output_pose(
                {"type": "pet_pose", "state": "thinking", "overlay_only": True},
                event_type="omnivoice_thinking",
                raw_payload=payload,
            )
            return

        if state == "speaking":
            job_id = str(payload.get("job_id") or "")
            active_text = ""
            should_emit_caption = False
            with self._omnivoice_queue_lock:
                active_text = self._omnivoice_active_job_text.get(job_id, "")
                if active_text and job_id and job_id not in self._omnivoice_caption_sent_jobs:
                    self._omnivoice_caption_sent_jobs.add(job_id)
                    should_emit_caption = True
            pose_payload = {
                "type": "pet_pose",
                "state": "speaking",
                "mouth": "audio_volume",
                "mouth_open": 0.55,
                "audio_active": True,
                "overlay_only": True,
            }
            if should_emit_caption:
                pose_payload["bubble_text"] = active_text
                pose_payload["duration_ms"] = 0
                pose_payload["clear_bubble"] = True
            self._send_voice_output_pose(
                pose_payload,
                event_type="omnivoice_speaking",
                raw_payload=payload,
            )
            return

        if state == "idle":
            job_id = str(payload.get("job_id") or "")
            with self._omnivoice_queue_lock:
                if job_id:
                    active_text = self._omnivoice_active_job_text.pop(job_id, "")
                    self._omnivoice_caption_sent_jobs.discard(job_id)
                    if active_text:
                        self._omnivoice_retry_count_by_hash.pop(text_hash_for_log(active_text), None)
                has_pending = bool(self._omnivoice_pending_texts)
            if has_pending:
                Timer(self._omnivoice_inter_utterance_pause_seconds, self._drain_omnivoice_queue).start()
            self._send_voice_output_pose(
                {
                    "type": "pet_pose",
                    "state": "idle",
                    "mouth": "audio_volume",
                    "mouth_open": 0.0,
                    "audio_active": False,
                    "overlay_only": True,
                },
                event_type="omnivoice_idle",
                raw_payload=payload,
            )

    def _on_omnivoice_segment_ready(self, payload: Mapping[str, Any]) -> None:
        self.logger.info(
            "OmniVoice segment ready job_id=%s index=%s duration=%s elapsed=%s",
            payload.get("job_id"),
            payload.get("index"),
            payload.get("audio_duration_seconds"),
            payload.get("elapsed_seconds"),
        )

    def _stop_omnivoice(self, reason: str) -> None:
        if self._omnivoice_provider is None:
            return
        with self._omnivoice_queue_lock:
            self._omnivoice_pending_texts.clear()
            self._omnivoice_active_job_text.clear()
            self._omnivoice_caption_sent_jobs.clear()
            self._omnivoice_retry_count_by_hash.clear()
        try:
            self._omnivoice_provider.handle_interrupt(reason)
        except Exception:
            self.logger.exception("OmniVoice stop failed reason=%s", reason)

    def _fallback_voice_output_after_omnivoice_failure(self, reason: str) -> None:
        if self._voice_output_fallback_provider != "volc_rtc":
            return
        self._voice_output_effective_provider = "volc_rtc"
        self.logger.warning("VoiceOutput falling back to volc_rtc reason=%s", reason)

    def _send_voice_output_pose(
        self,
        payload: Mapping[str, Any],
        *,
        event_type: str,
        raw_payload: Mapping[str, Any],
        source: str = "local:omnivoice_gateway",
    ) -> None:
        sent = False
        error = ""
        try:
            sent = bool(self.adapter.sender.client.send_pose(dict(payload)))
        except Exception as exc:
            error = str(exc)
            self.logger.debug("VoiceOutput pose send failed event_type=%s error=%s", event_type, error)
        now = time.time()
        self.adapter.sender.write_event_record(
            trace_id=_new_trace_id("voice-output"),
            event_type=event_type,
            source=source,
            raw_payload=dict(raw_payload),
            mapping_result={"payload": dict(payload), "sent": sent, "error": error},
            send_to_godot=sent,
            error=None if sent else (error or "presentation_offline"),
            event_received_at=now,
            pose_generated_at=now,
        )

    def _force_idle_presentation(self, reason: str) -> None:
        now_mono = time.monotonic()
        with self._companion_vision_lock:
            self._last_ai_state = "idle"
            self._last_ai_state_at = now_mono
            self._voice_priority_waiting_for_answer = False
            self._voice_priority_waiting_until = 0.0
            self._voice_priority_answer_until = max(self._voice_priority_answer_until, now_mono + 1.0)
            self._voice_priority_last_reason = "forced_idle"
        self.adapter.reset_runtime_activity()
        self._send_voice_output_pose(
            {
                "type": "pet_pose",
                "state": "idle",
                "emotion": "neutral",
                "gesture": "none",
                "posture": "stand",
                "mouth": "audio_volume",
                "mouth_open": 0.0,
                "audio_active": False,
                "overlay_only": True,
            },
            event_type="local_idle_state_reset",
            raw_payload={"reason": reason},
            source="local:voice_state_reset",
        )

    def close(self) -> None:
        if self._vision_observer is not None:
            self._vision_observer.stop()
        self.companion_vision_stop()
        self._stop_omnivoice("bridge_close")
        self.stop_voice_chat()
        self.adapter.close()
        if self._memory_service is not None:
            self._memory_service.close()
        try:
            self._face_tracking_udp.close()
        except Exception:
            pass

    def client_config(self) -> dict[str, Any]:
        agent_config = self.request.get("AgentConfig", {})
        target_user_ids = agent_config.get("TargetUserId") if isinstance(agent_config, Mapping) else []
        user_id = _first_text(target_user_ids) or _pick_nested(
            self.config,
            ("ClientRTC", "UserId"),
            ("ClientRTC", "UserID"),
        )
        token = _pick_nested(self.config, ("ClientRTC", "Token"), ("ClientRTC", "token"))
        return {
            "rtc": {
                "appId": str(self.request.get("AppId") or ""),
                "roomId": str(self.request.get("RoomId") or ""),
                "userId": str(user_id or ""),
                "botUid": str(self.bot_uid or ""),
                "token": str(token or ""),
                "audioReportIntervalMs": 80,
                "enableClientSubtitle": False,
                "captureVolume": _safe_float(
                    _pick_nested(
                        self.config,
                        ("ClientRTC", "CaptureVolume"),
                        ("ClientRTC", "captureVolume"),
                    ),
                    150.0,
                ),
            },
            "startVoiceChat": {
                "taskId": self._active_task_id,
                "taskIdBase": self._base_task_id,
                "targetUserIds": target_user_ids if isinstance(target_user_ids, list) else [target_user_ids],
                "botUid": str(self.bot_uid or ""),
            },
            "voiceRoute": self.config.get("VoiceRoute", {}),
            "screenVision": self.screen_vision_settings(),
            "cameraVideo": self.camera_video_settings(),
            "vision": self.vision_status(),
            "camera": self.camera_status(),
            "faceTracking": self.face_tracking_status(),
            "companionVision": self.companion_vision_status(),
            "voiceOutput": self.voice_output_status(),
            "memory": self._memory_service.status() if self._memory_service is not None else {"enabled": False},
        }

    def voice_diagnostics(self) -> dict[str, Any]:
        try:
            runtime = dict(self.adapter.runtime_status())
        except Exception as exc:
            runtime = {"error": str(exc)}

        self._maybe_schedule_stale_thinking_recovery(runtime, trigger="diagnostics")
        now = time.monotonic()
        with self._speech_watchdog_lock:
            watchdog = {
                "enabled": self._speech_watchdog_enabled,
                "delaySec": self._speech_watchdog_delay_sec,
                "busyGraceSec": self._speech_watchdog_busy_grace_sec,
                "turnId": self._speech_watchdog_turn_id,
                "pending": self._speech_watchdog_timer is not None,
                "aiSeen": self._speech_watchdog_ai_seen,
                "retryTurnId": self._speech_watchdog_retry_turn_id,
                "busyRetryCount": self._speech_watchdog_busy_retry_count,
                "busyRetryIntervalSec": self._speech_watchdog_busy_retry_interval_sec,
                "busyRetryMaxSec": self._speech_watchdog_busy_retry_max_sec,
                "directFallbackEnabled": self._speech_watchdog_direct_fallback_enabled,
                "directFallbackSec": self._speech_watchdog_direct_fallback_sec,
                "directFallbackInFlight": self._speech_watchdog_direct_fallback_in_flight,
                "directFallbackTurnId": self._speech_watchdog_direct_fallback_turn_id,
                "directFallbackCount": self._speech_watchdog_direct_fallback_count,
                "lastRetryAgeSec": round(now - self._speech_watchdog_last_retry_at, 3) if self._speech_watchdog_last_retry_at else None,
                "lastSkipReason": self._speech_watchdog_last_skip_reason,
                "lastUserAgeSec": round(now - self._speech_watchdog_user_at, 3) if self._speech_watchdog_user_at else None,
                "lastUserTextPreview": _debug_preview_text(self._speech_watchdog_user_text),
            }
        with self._speech_recovery_lock:
            watchdog["staleThinkingRecoverSec"] = self._speech_watchdog_stale_thinking_recover_sec
            watchdog["recoveryInProgress"] = self._speech_recovery_in_progress
            watchdog["recoveryCount"] = self._speech_recovery_count
            watchdog["lastRecoveryAgeSec"] = round(now - self._speech_recovery_last_at, 3) if self._speech_recovery_last_at else None
            watchdog["lastRecoveryReason"] = self._speech_recovery_last_reason

        with self._debug_text_lock:
            pending = list(self._debug_text_pending)
            results = list(self._debug_text_results)

        last_result = dict(results[-1]) if results else {}
        return {
            "ok": True,
            "time": time.time(),
            "voiceActive": self._voice_active,
            "activeTaskId": self._active_task_id,
            "botUidPresent": bool(self.bot_uid),
            "runtime": runtime,
            "ai": {
                "lastState": self._last_ai_state,
                "lastStateAgeSec": round(now - self._last_ai_state_at, 3) if self._last_ai_state_at else None,
            },
            "speechWatchdog": watchdog,
            "externalText": {
                "pendingCount": len(pending),
                "resultCount": len(results),
                "lastResult": {
                    "ok": bool(last_result.get("ok", False)) if last_result else None,
                    "transport": str(last_result.get("transport") or ""),
                    "error": str(last_result.get("error") or ""),
                    "source": str(last_result.get("source") or ""),
                    "messageType": str(last_result.get("messageType") or ""),
                },
            },
            "voiceOutput": self.voice_output_status(),
        }

    def voice_output_status(self) -> dict[str, Any]:
        tts_config = OmniVoiceGatewayTTSConfig.from_mapping(self._voice_output_config, root=ROOT)
        gpt_sovits_config = GPTSoVITSDirectTTSConfig.from_mapping(self._voice_output_config, root=ROOT)
        local_tts_active = self._voice_output_effective_provider in _VOICE_OUTPUT_LOCAL_TTS_PROVIDERS
        mute_volc = (
            local_tts_active
            and _safe_bool(
                self._voice_output_config.get(
                    "MuteVolcRemoteAiAudio",
                    self._voice_output_config.get("muteVolcRemoteAiAudio", True),
                ),
                True,
            )
        )
        mute_microphone = (
            local_tts_active
            and _safe_bool(
                self._voice_output_config.get(
                    "MuteMicrophoneDuringLocalTts",
                    self._voice_output_config.get("muteMicrophoneDuringLocalTts", True),
                ),
                True,
            )
        )
        mute_remote_ai_microphone = _safe_bool(
            self._voice_output_config.get(
                "MuteMicrophoneDuringRemoteAiAudio",
                self._voice_output_config.get("muteMicrophoneDuringRemoteAiAudio", True),
            ),
            True,
        )
        remote_ai_release_ms = int(
            _clamp_float(
                self._voice_output_config.get(
                    "RemoteAiAudioEchoGuardReleaseMs",
                    self._voice_output_config.get("remoteAiAudioEchoGuardReleaseMs", 900),
                ),
                900.0,
                150.0,
                4000.0,
            )
        )
        remote_ai_capture_volume = int(
            _clamp_float(
                self._voice_output_config.get(
                    "RemoteAiAudioEchoGuardCaptureVolume",
                    self._voice_output_config.get("remoteAiAudioEchoGuardCaptureVolume", 35),
                ),
                35.0,
                0.0,
                400.0,
            )
        )
        return {
            "interfaceVersion": _VOICE_OUTPUT_INTERFACE_VERSION,
            "provider": self._voice_output_requested_provider,
            "effectiveProvider": self._voice_output_effective_provider,
            "fallbackProvider": self._voice_output_fallback_provider,
            "supportedProviders": list(_VOICE_OUTPUT_BUILTIN_PROVIDERS),
            "localTtsActive": local_tts_active,
            "muteVolcRemoteAiAudio": mute_volc,
            "muteMicrophoneDuringLocalTts": mute_microphone,
            "muteMicrophoneDuringRemoteAiAudio": mute_remote_ai_microphone,
            "remoteAiAudioEchoGuardReleaseMs": remote_ai_release_ms,
            "remoteAiAudioEchoGuardCaptureVolume": remote_ai_capture_volume,
            "gatewayURL": gpt_sovits_config.primary_url if self._voice_output_effective_provider == "gpt_sovits_direct" else tts_config.gateway_url,
            "baseURLs": list(gpt_sovits_config.base_urls) if self._voice_output_requested_provider == "gpt_sovits_direct" else [],
            "voiceId": "gpt_sovits_direct" if self._voice_output_effective_provider == "gpt_sovits_direct" else tts_config.voice_id,
            "lang": gpt_sovits_config.text_lang if self._voice_output_effective_provider == "gpt_sovits_direct" else tts_config.lang,
            "mediaType": gpt_sovits_config.media_type if self._voice_output_effective_provider == "gpt_sovits_direct" else "",
            "sampleRate": gpt_sovits_config.sample_rate if self._voice_output_effective_provider == "gpt_sovits_direct" else 0,
            "playbackBackend": gpt_sovits_config.playback_backend if self._voice_output_effective_provider == "gpt_sovits_direct" else "",
            "speedFactor": gpt_sovits_config.speed_factor if self._voice_output_effective_provider == "gpt_sovits_direct" else 0.0,
            "fragmentInterval": gpt_sovits_config.fragment_interval if self._voice_output_effective_provider == "gpt_sovits_direct" else 0.0,
            "interUtterancePauseSeconds": self._omnivoice_inter_utterance_pause_seconds,
            "pseudoStream": True if self._voice_output_effective_provider == "gpt_sovits_direct" else tts_config.pseudo_stream,
            "tokenPresent": True if self._voice_output_effective_provider == "gpt_sovits_direct" else bool(tts_config.api_token),
            "state": self._voice_output_last_state,
            "lastJobId": self._voice_output_last_job_id,
            "lastTextHash": self._voice_output_last_text_hash,
            "lastError": self._voice_output_last_error,
            "pendingTextCount": len(self._omnivoice_pending_texts),
        }

    def check_config(self) -> list[dict[str, str]]:
        return [
            {"key": issue.key, "message": issue.message, "severity": issue.severity}
            for issue in check_start_voice_chat_config(self.request)
        ]

    def queue_external_text_to_llm(self, payload: Mapping[str, Any]) -> dict[str, Any]:
        text = str(payload.get("text") or payload.get("message") or "").strip()
        if not text:
            return {"ok": False, "error": "empty_text"}
        if len(text) > 2000:
            return {"ok": False, "error": "text_too_long", "max_len": 2000}
        try:
            interrupt_mode = int(payload.get("interrupt_mode", payload.get("InterruptMode", 0)))
        except Exception:
            interrupt_mode = 0
        interrupt_mode = min(3, max(0, interrupt_mode))
        source = str(payload.get("source") or "external")
        return self._queue_external_text(text, interrupt_mode=interrupt_mode, source=source)

    def _queue_external_text(
        self,
        text: str,
        *,
        interrupt_mode: int,
        source: str,
        metadata: Mapping[str, Any] | None = None,
    ) -> dict[str, Any]:
        metadata = dict(metadata or {})
        source = str(source or "external")
        if self._memory_service is not None:
            text, memory_meta = self._memory_service.build_external_text(text)
            metadata.setdefault("memory", memory_meta)
        text = _normalize_external_text_to_llm_message(text, source=source)
        if not text:
            return {"ok": False, "error": "empty_text"}
        try:
            interrupt_mode = int(interrupt_mode)
        except Exception:
            interrupt_mode = 0
        if interrupt_mode <= 0:
            interrupt_mode = 1
        interrupt_mode = min(3, max(1, interrupt_mode))
        with self._debug_text_lock:
            message_id = self._debug_text_next_id
            self._debug_text_next_id += 1
            item = {
                "id": message_id,
                "text": text,
                "interruptMode": interrupt_mode,
                "botUid": str(self.bot_uid or ""),
                "source": source,
                "role": str(metadata.get("role") or "external"),
                "messageType": str(metadata.get("message_type") or metadata.get("messageType") or "external_text"),
                "metadata": metadata,
                "createdAt": time.time(),
            }
        self.adapter.sender.write_event_record(
            trace_id=f"{source}-text-queued-{message_id}",
            event_type="external_text_to_llm_queued",
            source=f"local:{source}",
            raw_payload={
                "id": message_id,
                "text": text,
                "interruptMode": interrupt_mode,
                "source": source,
                "role": item["role"],
                "messageType": item["messageType"],
                "metadata": metadata,
            },
            mapping_result={"queued": True, "botUid": str(self.bot_uid or "")},
            send_to_godot=False,
            event_received_at=time.time(),
            pose_generated_at=time.time(),
        )
        if self._external_text_should_use_rtc_binary(item):
            with self._debug_text_lock:
                self._debug_text_pending.append(item)
            return {"ok": True, "id": message_id, "queued": True, "transport": "rtc_user_binary"}

        if self._voice_active and isinstance(self._active_request, Mapping):
            Thread(
                target=self._send_external_text_or_fallback,
                args=(item,),
                name=f"external-text-{message_id}",
                daemon=True,
            ).start()
            return {"ok": True, "id": message_id, "queued": True, "transport": "openapi_async"}

        with self._debug_text_lock:
            self._debug_text_pending.append(item)
        return {"ok": True, "id": message_id, "queued": True, "transport": "web_binary"}

    def _external_text_should_use_rtc_binary(self, item: Mapping[str, Any]) -> bool:
        source = str(item.get("source") or "").strip().lower()
        metadata = item.get("metadata", {})
        if isinstance(metadata, Mapping):
            transport = str(
                metadata.get("transport")
                or metadata.get("Transport")
                or metadata.get("external_text_transport")
                or ""
            ).strip().lower()
            if transport in {"rtc", "rtc_binary", "user_binary"}:
                return True
        return False

    def _send_external_text_or_fallback(self, item: Mapping[str, Any]) -> None:
        result = self._send_external_text_via_update_voice_chat(item)
        if result.get("ok"):
            return
        with self._debug_text_lock:
            self._debug_text_pending.append(dict(item))
        self.adapter.sender.write_event_record(
            trace_id=f"external-text-fallback-{item.get('id')}",
            event_type="external_text_to_llm_fallback",
            source="openapi:UpdateVoiceChat",
            raw_payload={"id": item.get("id"), "error": result.get("error", "")},
            mapping_result={"fallback": "web_binary", "error": result.get("error", "")},
            send_to_godot=False,
            event_received_at=time.time(),
            pose_generated_at=time.time(),
        )

    def _send_external_text_via_update_voice_chat(self, item: Mapping[str, Any]) -> dict[str, Any]:
        if not self._voice_active or not isinstance(self._active_request, Mapping):
            return {"ok": False, "error": "voice_inactive"}
        try:
            interrupt_mode = int(item.get("interruptMode", 0))
        except Exception:
            interrupt_mode = 0
        # Volc OpenAPI rejects ExternalTextToLLM with InterruptMode=0. The local
        # scheduler keeps companion prompts non-interrupting by only sending them
        # while idle, then maps to the smallest OpenAPI-accepted value.
        interrupt_mode = min(3, max(1, interrupt_mode))
        request = {
            "AppId": self._active_request.get("AppId"),
            "RoomId": self._active_request.get("RoomId"),
            "TaskId": self._active_request.get("TaskId"),
            "Command": "ExternalTextToLLM",
            "Message": str(item.get("text") or ""),
            "InterruptMode": interrupt_mode,
        }
        try:
            response = self._openapi_client.update_voice_chat(request)
        except Exception as exc:
            self.logger.warning("UpdateVoiceChat ExternalTextToLLM failed id=%s error=%s", item.get("id"), exc)
            return {"ok": False, "error": str(exc)}

        result_item = {
            "id": item.get("id"),
            "ok": True,
            "error": "",
            "botUid": str(item.get("botUid") or self.bot_uid or ""),
            "sentAt": time.time(),
            "transport": "openapi",
            "source": str(item.get("source") or ""),
            "messageType": str(item.get("messageType") or ""),
        }
        self._note_external_text_result(result_item)
        with self._debug_text_lock:
            self._debug_text_results.append(result_item)
            self._debug_text_results = self._debug_text_results[-50:]
        self.adapter.sender.write_event_record(
            trace_id=f"external-text-update-{item.get('id')}",
            event_type="external_text_to_llm_result",
            source="openapi:UpdateVoiceChat",
            raw_payload={"id": item.get("id"), "ok": True, "response": _redact_for_http(response)},
            mapping_result={
                "sent_to_ai": True,
                "botUid": result_item["botUid"],
                "error": "",
                "transport": "openapi",
                "source": result_item["source"],
                "messageType": result_item["messageType"],
            },
            send_to_godot=False,
            event_received_at=time.time(),
            pose_generated_at=time.time(),
        )
        return {"ok": True, "response": response}

    def _send_external_text_to_speech_via_update_voice_chat(
        self,
        text: str,
        *,
        source: str,
        interrupt_mode: int = 1,
    ) -> dict[str, Any]:
        clean_text = _sanitize_direct_speech_text(text)
        if not clean_text:
            return {"ok": False, "error": "empty_tts_text"}
        if not self._voice_active or not isinstance(self._active_request, Mapping):
            return {"ok": False, "error": "voice_inactive"}
        try:
            mode = min(3, max(1, int(interrupt_mode)))
        except Exception:
            mode = 1
        request = {
            "AppId": self._active_request.get("AppId"),
            "RoomId": self._active_request.get("RoomId"),
            "TaskId": self._active_request.get("TaskId"),
            "Command": "ExternalTextToSpeech",
            "Message": clean_text,
            "InterruptMode": mode,
        }
        try:
            response = self._openapi_client.update_voice_chat(request)
        except Exception as exc:
            self.logger.warning("UpdateVoiceChat ExternalTextToSpeech failed source=%s error=%s", source, exc)
            result = {"ok": False, "error": str(exc), "text": clean_text}
        else:
            result = {"ok": True, "response": response, "text": clean_text}

        self.adapter.sender.write_event_record(
            trace_id=_new_trace_id("external-tts"),
            event_type="external_text_to_speech_result",
            source="openapi:UpdateVoiceChat",
            raw_payload={
                "ok": bool(result.get("ok")),
                "source": source,
                "text": clean_text,
                "response": _redact_for_http(result.get("response", {})),
                "error": str(result.get("error") or ""),
            },
            mapping_result={
                "sent_to_tts": bool(result.get("ok")),
                "source": source,
                "error": str(result.get("error") or ""),
                "textPreview": _debug_preview_text(clean_text, 80),
            },
            send_to_godot=False,
            event_received_at=time.time(),
            pose_generated_at=time.time(),
        )
        return result

    def _schedule_companion_empty_response_fallback_locked(self, reason: str, now: float) -> bool:
        if not bool(self._companion_vision_config.get("empty_fallback_enabled", True)):
            return False
        if self._companion_vision_fallback_in_flight:
            return False
        if not self._voice_active:
            return False
        if self._companion_vision_priority_block_reason_locked(now):
            return False
        prompt = self._companion_vision_pending_text or str(
            self._companion_vision_config.get("prompt") or _DEFAULT_COMPANION_VISION_PROMPT
        )
        pending_id = int(self._companion_vision_pending_id or 0)
        self._companion_vision_fallback_in_flight = True
        self._companion_vision_last_fallback_error = ""
        self._companion_vision_last_skip_reason = reason + "_fallback_queued"
        Thread(
            target=self._run_companion_empty_response_fallback,
            args=(pending_id, prompt, reason),
            name="companion-empty-response-fallback",
            daemon=True,
        ).start()
        return True

    def _run_companion_empty_response_fallback(self, pending_id: int, prompt: str, reason: str) -> None:
        text = ""
        error = ""
        result: dict[str, Any] = {"ok": False, "error": "not_started"}
        try:
            text = self._generate_companion_direct_fallback_text(prompt)
            if not text:
                text = "看不清，画面好像没把关键帧传稳。"
            with self._companion_vision_lock:
                if not self._voice_active or self._companion_vision_priority_block_reason_locked(time.monotonic()):
                    self._companion_vision_fallback_in_flight = False
                    self._companion_vision_last_fallback_error = "cancelled_by_priority"
                    return
                if pending_id and self._companion_vision_pending_id and pending_id != self._companion_vision_pending_id:
                    self._companion_vision_fallback_in_flight = False
                    self._companion_vision_last_fallback_error = "stale_pending_id"
                    return
            result = self._send_external_text_to_speech_via_update_voice_chat(
                text,
                source="companion_empty_response_fallback",
                interrupt_mode=1,
            )
            if not result.get("ok"):
                error = str(result.get("error") or "tts_failed")
        except Exception as exc:
            error = str(exc)
            self.logger.exception("companion empty-response fallback failed")

        clean_text = _sanitize_direct_speech_text(text)
        now = time.monotonic()
        with self._companion_vision_lock:
            self._companion_vision_fallback_in_flight = False
            if result.get("ok") and clean_text:
                self._companion_vision_empty_fallback_count += 1
                self._companion_vision_failure_count = 0
                self._companion_vision_pending = False
                self._companion_vision_pending_id = 0
                self._companion_vision_pending_until = 0.0
                self._companion_vision_pending_has_response = True
                self._companion_vision_response_active = True
                self._companion_vision_next_allowed_at = 0.0
                self._companion_vision_last_success_at = now
                self._companion_vision_last_skip_reason = "empty_response_fallback_spoken"
                self._companion_vision_last_fallback_error = ""
                self._remember_companion_ai_text_locked(clean_text, now)
            else:
                self._companion_vision_last_fallback_error = error or "empty_fallback_failed"
                self._mark_companion_vision_failure_locked("empty_fallback_failed", now)

        if result.get("ok") and clean_text:
            self._remember_recent_ai_echo_text(clean_text)
            self._send_voice_output_pose(
                {
                    "type": "pet_pose",
                    "state": "speaking",
                    "emotion": "neutral",
                    "gesture": "small_tease",
                    "posture": "stand",
                    "bubble_text": clean_text,
                    "mouth": "audio_volume",
                    "audio_active": True,
                    "overlay_only": False,
                    "duration_ms": 0,
                },
                event_type="companion_empty_response_fallback_speaking",
                raw_payload={"reason": reason, "text": clean_text},
                source="local:companion_fallback",
            )

    def _run_companion_vision_sidecar(self, pending_id: int, prompt: str, interrupt_mode: int) -> None:
        text = ""
        error = ""
        result: dict[str, Any] = {"ok": False, "error": "not_started"}
        try:
            text = self._generate_companion_sidecar_comment(prompt)
            if not text:
                raise RuntimeError("sidecar_empty_comment")
            with self._companion_vision_lock:
                text = self._dedupe_companion_sidecar_text_locked(text, time.monotonic())
                if not self._voice_active:
                    self._cancel_companion_vision_pending_locked("sidecar_cancelled_voice_inactive", time.monotonic())
                    return
                if pending_id != self._companion_vision_pending_id:
                    return
                priority_reason = self._companion_vision_priority_block_reason_locked(time.monotonic())
                if priority_reason:
                    self._cancel_companion_vision_pending_locked("sidecar_cancelled_" + priority_reason, time.monotonic())
                    return
            result = self._send_external_text_to_speech_via_update_voice_chat(
                text,
                source="companion_vision_sidecar",
                interrupt_mode=interrupt_mode,
            )
            if not result.get("ok"):
                error = str(result.get("error") or "tts_failed")
        except Exception as exc:
            error = str(exc)
            self.logger.exception("companion vision sidecar failed")

        clean_text = _sanitize_direct_speech_text(text)
        now = time.monotonic()
        with self._companion_vision_lock:
            stale = pending_id != self._companion_vision_pending_id
            if not stale and result.get("ok") and clean_text:
                self._companion_vision_failure_count = 0
                self._companion_vision_pending = False
                self._companion_vision_pending_id = 0
                self._companion_vision_pending_until = 0.0
                self._companion_vision_pending_has_response = True
                self._companion_vision_pending_text = ""
                self._companion_vision_response_active = True
                self._companion_vision_next_allowed_at = 0.0
                self._companion_vision_last_success_at = now
                self._companion_vision_last_skip_reason = "local_vision_sidecar_spoken"
                self._companion_vision_last_fallback_error = ""
                self._remember_companion_ai_text_locked(clean_text, now)
            elif not stale:
                self._companion_vision_last_fallback_error = error or "sidecar_failed"
                self._mark_companion_vision_failure_locked("local_vision_sidecar_failed", now)

        self.adapter.sender.write_event_record(
            trace_id=f"companion-sidecar-result-{pending_id}",
            event_type="companion_vision_sidecar_result",
            source="local:companion_vision_sidecar",
            raw_payload={
                "id": pending_id,
                "ok": bool(result.get("ok")),
                "text": clean_text,
                "error": error,
                "stale": stale,
            },
            mapping_result={
                "spoken": bool(result.get("ok") and clean_text and not stale),
                "error": error,
                "textPreview": _debug_preview_text(clean_text, 80),
            },
            send_to_godot=False,
            event_received_at=time.time(),
            pose_generated_at=time.time(),
        )
        if result.get("ok") and clean_text and not stale:
            self._remember_recent_ai_echo_text(clean_text)
            self._send_voice_output_pose(
                {
                    "type": "pet_pose",
                    "state": "speaking",
                    "emotion": "neutral",
                    "gesture": "small_tease",
                    "posture": "stand",
                    "bubble_text": clean_text,
                    "mouth": "audio_volume",
                    "audio_active": True,
                    "overlay_only": False,
                    "duration_ms": 0,
                },
                event_type="companion_vision_sidecar_speaking",
                raw_payload={"text": clean_text},
                source="local:companion_sidecar",
            )

    def _generate_companion_sidecar_comment(self, prompt: str) -> str:
        llm_config = self.request.get("Config", {}).get("LLMConfig", {})
        if not isinstance(llm_config, Mapping):
            raise RuntimeError("missing LLMConfig for companion sidecar")
        url = str(llm_config.get("Url") or llm_config.get("URL") or "").strip()
        api_key = str(llm_config.get("APIKey") or llm_config.get("ApiKey") or "").strip()
        model = str(llm_config.get("ModelName") or llm_config.get("Model") or "mimo-v2.5").strip()
        if not url or not api_key:
            raise RuntimeError("missing LLM endpoint or API key for companion sidecar")

        frame = self._capture_companion_fallback_frame()
        user_content: list[dict[str, Any]] = [{"type": "text", "text": str(prompt or _DEFAULT_COMPANION_VISION_PROMPT)}]
        if frame is not None:
            data_url = "data:image/jpeg;base64," + base64.b64encode(frame.image_jpeg).decode("ascii")
            user_content.append({"type": "image_url", "image_url": {"url": data_url, "detail": "high"}})

        timeout_sec = max(3.0, float(self._companion_vision_config.get("local_vision_sidecar_timeout_sec", 20.0)))
        thinking_type = str(llm_config.get("ThinkingType") or "auto").strip().lower()
        system_text = _system_messages_text(llm_config.get("SystemMessages", []))
        messages: list[dict[str, Any]] = []
        if system_text:
            messages.append({"role": "system", "content": system_text})
        messages.append({"role": "user", "content": user_content})
        body: dict[str, Any] = {
            "model": model,
            "messages": messages,
            "temperature": 0.2,
            "top_p": 0.5,
            "max_tokens": int(max(16, float(llm_config.get("MaxTokens", 512) or 512))),
            "stream": False,
        }
        if thinking_type:
            body["thinking"] = {"type": thinking_type}
        response = requests.post(
            _chat_completions_url(url),
            headers={"Authorization": "Bearer " + api_key, "Content-Type": "application/json"},
            json=body,
            timeout=timeout_sec,
        )
        try:
            payload = response.json()
        except ValueError as exc:
            raise RuntimeError(f"sidecar_llm_non_json:{response.status_code}") from exc
        if response.status_code >= 400:
            raise RuntimeError(f"sidecar_llm_http_{response.status_code}:{_debug_preview_text(payload, 160)}")
        choice = (payload.get("choices") or [{}])[0]
        message = choice.get("message") if isinstance(choice, Mapping) else {}
        text = ""
        if isinstance(message, Mapping):
            content = message.get("content")
            if isinstance(content, list):
                text = " ".join(
                    str(part.get("text") or "").strip()
                    for part in content
                    if isinstance(part, Mapping) and str(part.get("text") or "").strip()
                )
            else:
                text = _first_present_text(message, "content", "text", "message")
        if not text and isinstance(choice, Mapping):
            text = _first_present_text(choice, "text", "content")
        text = _extract_sidecar_speech_text(text)
        text = _sanitize_direct_speech_text(text)
        if not text:
            raise RuntimeError("sidecar_llm_empty_content")
        return text

    def _dedupe_companion_sidecar_text_locked(self, text: str, now: float) -> str:
        normalized = _sanitize_direct_speech_text(text)
        if not normalized:
            return ""
        window_sec = max(60.0, float(self._companion_vision_config.get("recent_context_window_sec", 180.0)))
        self._companion_vision_output_history = [
            (timestamp, item)
            for timestamp, item in self._companion_vision_output_history
            if now - timestamp <= window_sec
        ][-6:]
        recent = [item for _, item in self._companion_vision_output_history]
        if _companion_sidecar_text_too_similar(normalized, recent):
            normalized = self._next_companion_static_comment_locked(now, recent)
        self._companion_vision_output_history.append((now, normalized))
        self._companion_vision_output_history = self._companion_vision_output_history[-6:]
        return normalized

    def _next_companion_static_comment_locked(self, now: float, recent: list[str]) -> str:
        options = (
            "这一帧没新情报，先稳住。",
            "画面变化不大，继续推进吧。",
            "哼，先别急，节奏稳住。",
            "暂时没看到新东西。",
            "这个画面先过，别卡太久。",
        )
        for offset in range(len(options)):
            index = (self._companion_vision_static_comment_index + offset) % len(options)
            candidate = options[index]
            if not _companion_sidecar_text_too_similar(candidate, recent):
                self._companion_vision_static_comment_index = (index + 1) % len(options)
                return candidate
        self._companion_vision_static_comment_index = (self._companion_vision_static_comment_index + 1) % len(options)
        return options[self._companion_vision_static_comment_index]

    def _generate_companion_direct_fallback_text(self, prompt: str) -> str:
        llm_config = self.request.get("Config", {}).get("LLMConfig", {})
        if not isinstance(llm_config, Mapping):
            raise RuntimeError("missing LLMConfig for companion fallback")
        url = str(llm_config.get("Url") or llm_config.get("URL") or "").strip()
        api_key = str(llm_config.get("APIKey") or llm_config.get("ApiKey") or "").strip()
        model = str(llm_config.get("ModelName") or llm_config.get("Model") or "mimo-v2.5").strip()
        if not url or not api_key:
            raise RuntimeError("missing LLM endpoint or API key for companion fallback")

        user_content: list[dict[str, Any]] = [{"type": "text", "text": str(prompt or _DEFAULT_COMPANION_VISION_PROMPT)}]
        frame = self._capture_companion_fallback_frame()
        if frame is not None:
            data_url = "data:image/jpeg;base64," + base64.b64encode(frame.image_jpeg).decode("ascii")
            user_content.append({"type": "image_url", "image_url": {"url": data_url, "detail": "low"}})

        system_messages = llm_config.get("SystemMessages", [])
        if isinstance(system_messages, str):
            system_text = system_messages
        elif isinstance(system_messages, list):
            system_text = "\n".join(str(item) for item in system_messages if str(item).strip())
        else:
            system_text = ""
        system_text = system_text.strip()
        messages: list[dict[str, Any]] = []
        if system_text:
            messages.append({"role": "system", "content": system_text})
        messages.append({"role": "user", "content": user_content})
        body: dict[str, Any] = {
            "model": model,
            "messages": messages,
            "temperature": min(0.4, float(llm_config.get("Temperature", 0.2) or 0.2)),
            "top_p": min(0.7, float(llm_config.get("TopP", 0.5) or 0.5)),
            "max_tokens": int(max(16, float(llm_config.get("MaxTokens", 512) or 512))),
            "stream": False,
        }
        thinking_type = str(llm_config.get("ThinkingType") or "auto").strip().lower()
        if thinking_type:
            body["thinking"] = {"type": thinking_type}
        timeout_sec = max(2.0, float(self._companion_vision_config.get("empty_fallback_timeout_sec", 14.0)))
        response = requests.post(
            _chat_completions_url(url),
            headers={"Authorization": "Bearer " + api_key, "Content-Type": "application/json"},
            json=body,
            timeout=timeout_sec,
        )
        try:
            payload = response.json()
        except ValueError as exc:
            raise RuntimeError(f"fallback_llm_non_json:{response.status_code}") from exc
        if response.status_code >= 400:
            raise RuntimeError(f"fallback_llm_http_{response.status_code}:{_debug_preview_text(payload, 160)}")
        choice = (payload.get("choices") or [{}])[0]
        message = choice.get("message") if isinstance(choice, Mapping) else {}
        text = ""
        if isinstance(message, Mapping):
            text = _first_present_text(message, "content", "text", "message")
        text = _sanitize_direct_speech_text(text)
        if not text:
            raise RuntimeError("fallback_llm_empty_content")
        return text

    def _capture_companion_fallback_frame(self):
        try:
            max_width = int(float(self._companion_vision_config.get("empty_fallback_max_width", 1920) or 1920))
            jpeg_quality = int(float(self._companion_vision_config.get("empty_fallback_jpeg_quality", 82) or 82))
            config = replace(
                self._vision_observer_config,
                enabled=True,
                active_window_only=False,
                capture_interval_ms=1,
                min_diff_ratio=0.0,
                max_width=max(360, min(1920, max_width)),
                jpeg_quality=max(35, min(92, jpeg_quality)),
            )
            capture = VisionFrameProvider(config).poll(force=True, ignore_diff=True)
            return capture.frame
        except Exception:
            self.logger.exception("companion fallback screen capture failed")
            return None

    def take_pending_external_text(self) -> dict[str, Any]:
        with self._debug_text_lock:
            messages = self._debug_text_pending
            self._debug_text_pending = []
        return {"ok": True, "messages": messages}

    def record_external_text_result(self, payload: Mapping[str, Any]) -> dict[str, Any]:
        item = {
            "id": payload.get("id"),
            "ok": bool(payload.get("ok")),
            "error": str(payload.get("error") or ""),
            "botUid": str(payload.get("botUid") or self.bot_uid or ""),
            "sentAt": time.time(),
            "source": str(payload.get("source") or ""),
            "messageType": str(payload.get("messageType") or ""),
        }
        self._note_external_text_result(item)
        with self._debug_text_lock:
            self._debug_text_results.append(item)
            self._debug_text_results = self._debug_text_results[-50:]
        self.adapter.sender.write_event_record(
            trace_id=f"external-text-result-{item['id']}",
            event_type="external_text_to_llm_result",
            source="web:sendUserBinaryMessage",
            raw_payload=dict(payload),
            mapping_result={"sent_to_ai": item["ok"], "botUid": item["botUid"], "error": item["error"]},
            send_to_godot=False,
            event_received_at=time.time(),
            pose_generated_at=time.time(),
        )
        return {"ok": True}

    def external_text_results(self) -> dict[str, Any]:
        with self._debug_text_lock:
            return {"ok": True, "results": list(self._debug_text_results)}

    def start_voice_chat(self, *, force_restart: bool = False, suppress_welcome: bool = False) -> dict[str, Any]:
        if _vision_config_requested(self.request) and not _vision_supported_in_request(self.request):
            with self._vision_lock:
                self._vision_desired = False
                self._vision_client_state["message"] = _S2S_VISION_UNSUPPORTED_MESSAGE
            self.logger.warning(_S2S_VISION_UNSUPPORTED_MESSAGE)
            self._write_vision_record(
                "vision_config_ignored",
                {
                    "desired": False,
                    "reason": "s2s_vision_unsupported",
                    "message": _S2S_VISION_UNSUPPORTED_MESSAGE,
                },
            )
        with self._session_lock:
            if self._session is not None and self._session.started:
                if not force_restart:
                    self._voice_active = True
                    return {"ok": True, "already_started": True, "task_id": self._active_task_id}
                self.logger.info("Force restarting Volc VoiceChat task task_id=%s", self._active_task_id)
                self._session.stop()
                self._session = None
            suppress_cloud_welcome = bool(suppress_welcome or self._welcome_subtitle_ever_sent)
            request = _request_with_fresh_task_id(self.request, suppress_welcome=suppress_cloud_welcome)
            self._active_task_id = str(request.get("TaskId") or self._base_task_id)
            self._active_request = request
            now = time.monotonic()
            wait_for_welcome = bool(not suppress_cloud_welcome and _request_has_welcome_message(request))
            with self._companion_vision_lock:
                self._companion_vision_waiting_for_welcome_done = wait_for_welcome
                self._companion_vision_welcome_seen_speaking = False
                self._companion_vision_welcome_wait_started_at = now if wait_for_welcome else 0.0
                if not wait_for_welcome:
                    self._companion_vision_last_success_at = now
            self._session = LocalStartVoiceChatSession(config=self.config, request=request)
            self._session.start()
            self._voice_active = True
            self._stopped_at = 0.0
            self._welcome_subtitle_sent = suppress_cloud_welcome
            if self._vision_observer_config.enabled and self._vision_observer_config.companion_play_mode:
                with self._vision_lock:
                    self._vision_desired = True
                    self._vision_client_state["message"] = "MiMo companion play mode active"
            if self._vision_desired:
                self.companion_vision_start()
            if self._vision_observer is not None and self._vision_observer_config.enabled:
                self._vision_observer.start()
            return {
                "ok": True,
                "already_started": False,
                "task_id": self._active_task_id,
                "start_response": _redact_for_http(self._session.start_response or {}),
            }

    def stop_voice_chat(self) -> dict[str, Any]:
        self._voice_active = False
        self._stopped_at = time.time()
        self._stop_omnivoice("voice_chat_stop")
        self._force_idle_presentation("voice_chat_stop")
        if self._vision_observer is not None:
            self._vision_observer.stop()
        self.companion_vision_stop()
        self.adapter.reset_runtime_activity()
        self._cancel_speech_watchdog()
        with self._vision_lock:
            self._vision_desired = False
            self._vision_client_state["message"] = "voice chat stopping"
        with self._camera_lock:
            self._camera_desired = False
            self._camera_client_state["message"] = "voice chat stopping"
        with self._session_lock:
            if self._session is None:
                return {"ok": True, "already_stopped": True}
            self._session.stop()
            self._session = None
            self._active_task_id = self._base_task_id
            self._active_request = self.request
            return {"ok": True, "already_stopped": False}

    def _maybe_schedule_stale_thinking_recovery(self, runtime: Mapping[str, Any], *, trigger: str) -> bool:
        if not self._voice_active:
            return False
        current_state = str(runtime.get("current_state") or self._last_ai_state or "").strip().lower()
        if current_state != "thinking":
            return False
        if bool(runtime.get("audio_active")) or bool(runtime.get("subtitle_pending")):
            return False
        try:
            state_age_sec = float(runtime.get("current_state_age_sec") or 0.0)
        except (TypeError, ValueError):
            state_age_sec = 0.0
        threshold = self._speech_watchdog_stale_thinking_recover_sec
        if threshold <= 0.0 or state_age_sec < threshold:
            return False

        now = time.monotonic()
        if not self._should_restart_for_stale_thinking(trigger, now):
            reason = f"{trigger}:thinking_{state_age_sec:.1f}s_local_reset"
            with self._speech_recovery_lock:
                self._speech_recovery_last_at = now
                self._speech_recovery_last_reason = reason
            self._force_idle_presentation(reason)
            if trigger == "companion_vision":
                with self._companion_vision_lock:
                    self._mark_companion_vision_failure_locked("stale_thinking_local_reset", now)
            return True

        with self._speech_recovery_lock:
            if self._speech_recovery_in_progress:
                return False
            cooldown_left = self._speech_watchdog_recovery_cooldown_sec - (now - self._speech_recovery_last_at)
            if self._speech_recovery_last_at > 0.0 and cooldown_left > 0.0:
                return False
            self._speech_recovery_in_progress = True
            self._speech_recovery_last_at = now
            self._speech_recovery_last_reason = f"{trigger}:thinking_{state_age_sec:.1f}s"

        self._force_idle_presentation(self._speech_recovery_last_reason)
        Thread(
            target=self._recover_stale_thinking_session,
            args=(trigger, state_age_sec),
            daemon=True,
            name="volc-stale-thinking-recovery",
        ).start()
        return True

    def _should_restart_for_stale_thinking(self, trigger: str, now: float) -> bool:
        if trigger == "companion_vision":
            return False
        with self._speech_watchdog_lock:
            recent_user_turn = bool(
                self._speech_watchdog_user_text
                and self._speech_watchdog_user_at > 0.0
                and now - self._speech_watchdog_user_at <= max(12.0, self._speech_watchdog_delay_sec + 8.0)
            )
        return recent_user_turn

    def _recover_stale_thinking_session(self, trigger: str, state_age_sec: float) -> None:
        with self._vision_lock:
            restore_vision = bool(self._vision_desired)
        with self._camera_lock:
            restore_camera = bool(self._camera_desired)
        with self._companion_vision_lock:
            restore_companion = bool(self._companion_vision_running)
            restore_interval = float(self._companion_vision_config.get("interval_sec", 5.0))
            companion_was_pending = bool(self._companion_vision_pending or self._companion_vision_response_active)

        try:
            self.logger.warning(
                "Auto recovering stale VoiceChat thinking state trigger=%s age_sec=%.1f restore_vision=%s restore_camera=%s restore_companion=%s",
                trigger,
                state_age_sec,
                restore_vision,
                restore_camera,
                restore_companion,
            )
            self.stop_voice_chat()
            time.sleep(0.8)
            start_result = self.start_voice_chat(force_restart=True, suppress_welcome=True)
            if restore_vision:
                self.vision_start()
            if restore_camera:
                self.camera_start()
            if restore_companion:
                self.companion_vision_set_interval(restore_interval)
                self.companion_vision_start(force_enable=True)
                with self._companion_vision_lock:
                    if trigger == "companion_vision" or companion_was_pending:
                        now = time.monotonic()
                        self._mark_companion_vision_failure_locked("stale_thinking_recovery", now)
                        cooldown = max(
                            20.0,
                            restore_interval * 4.0,
                            float(self._companion_vision_config.get("max_failure_backoff_sec", 20.0)),
                        )
                        self._companion_vision_next_allowed_at = max(self._companion_vision_next_allowed_at, now + cooldown)
            self.logger.warning("Auto recovery restarted VoiceChat result=%s", start_result)
            with self._speech_recovery_lock:
                self._speech_recovery_count += 1
        except Exception:
            self.logger.exception("Auto recovery for stale VoiceChat thinking failed")
        finally:
            with self._speech_recovery_lock:
                self._speech_recovery_in_progress = False

    def route_web_event(self, envelope: Mapping[str, Any]) -> dict[str, Any]:
        event_type = str(envelope.get("event_type") or envelope.get("type") or "unknown")
        payload = envelope.get("payload", envelope)
        trace_id = str(envelope.get("trace_id") or _new_trace_id("web"))
        if self._should_drop_stale_voice_event(event_type):
            return {"ok": True, "handled": False, "dropped": True, "trace_id": trace_id}
        handled = self._route_payload(event_type, payload, trace_id)
        return {"ok": True, "handled": handled, "trace_id": trace_id}

    def _should_drop_stale_voice_event(self, event_type: str) -> bool:
        lowered = event_type.lower()
        if lowered not in _REALTIME_VOICE_EVENT_TYPES:
            return False
        if self._voice_active:
            return False
        if self._stopped_at == 0.0 and lowered in {"subtitle_event", "subtitle_message_received", "onsubtitlemessagereceived"}:
            return False
        now = time.time()
        if now - self._last_stale_event_log_at > 2.0:
            self._last_stale_event_log_at = now
            self.logger.info("drop stale voice event after stop event_type=%s stopped_ago=%.3fs", event_type, now - self._stopped_at if self._stopped_at else -1.0)
        return True

    def vision_start(self) -> dict[str, Any]:
        if _direct_rtc_vision_supported_in_request(self.request):
            with self._vision_lock:
                self._vision_desired = True
                self._vision_client_state["message"] = "screen vision requested"
            self._write_vision_record("vision_start_requested", {"desired": True, "mode": "direct_rtc_vision"})
            if self._vision_observer is not None and self._vision_observer_config.enabled:
                self._vision_observer.start()
            self.companion_vision_start()
            return self.vision_status()
        if self._sidecar_vision_available():
            with self._vision_lock:
                self._vision_desired = True
                self._vision_client_state["message"] = "vision API sidecar requested"
            self._vision_observer.start()
            self._write_vision_record(
                "vision_sidecar_start_requested",
                {"desired": True, "mode": "vision_api_sidecar"},
            )
            return self.vision_status()
        message = _S2S_VISION_UNSUPPORTED_MESSAGE if _is_s2s_request(self.request) else _VISION_CONFIG_MISSING_MESSAGE
        with self._vision_lock:
            self._vision_desired = False
            self._vision_client_state["message"] = message
        self._write_vision_record(
            "vision_start_rejected",
            {
                "desired": False,
                "reason": "s2s_vision_unsupported" if _is_s2s_request(self.request) else "vision_config_missing",
                "message": message,
            },
        )
        return self.vision_status()

    def vision_stop(self) -> dict[str, Any]:
        with self._vision_lock:
            self._vision_desired = False
            self._vision_client_state["message"] = "screen vision stop requested"
        if self._vision_observer is not None:
            self._vision_observer.stop()
        self.companion_vision_stop()
        self._write_vision_record("vision_stop_requested", {"desired": False})
        return self.vision_status()

    def vision_status(self) -> dict[str, Any]:
        direct_vision_supported = _direct_rtc_vision_supported_in_request(self.request)
        is_s2s = _is_s2s_request(self.request)
        sidecar_available = self._sidecar_vision_available()
        vision_supported = direct_vision_supported or sidecar_available
        mode = "direct_rtc_vision" if direct_vision_supported else (
            "vision_api_sidecar" if sidecar_available else (
                "ark_multimodal_sidecar_required" if is_s2s else "vision_config_missing"
            )
        )
        sidecar_status = self._vision_observer.status() if self._vision_observer is not None else None
        sidecar_payload = {
            "required": is_s2s,
            "implemented": bool(self._vision_observer_config.enabled),
            "enabled": bool(self._vision_observer_config.enabled),
            "provider": self._vision_observer_config.provider,
            "model": self._vision_observer_config.model,
            "thinkingType": self._vision_observer_config.thinking_type,
            "companionPlayMode": bool(self._vision_observer_config.companion_play_mode),
            "companionForceIntervalMs": self._vision_observer_config.companion_force_interval_ms,
            "running": bool(sidecar_status.running) if sidecar_status is not None else False,
            "inFlight": bool(sidecar_status.in_flight) if sidecar_status is not None else False,
            "lastSkipReason": sidecar_status.last_skip_reason if sidecar_status is not None else "",
            "lastRouteAction": sidecar_status.last_route_action if sidecar_status is not None else "",
            "lastEvent": sidecar_status.last_event if sidecar_status is not None else None,
            "lastInjection": sidecar_status.last_injection if sidecar_status is not None else None,
        }
        with self._vision_lock:
            status = {
                "ok": True,
                "desired": self._vision_desired,
                "screenPublished": bool(self._vision_client_state.get("screen_published")),
                "visionSupported": vision_supported,
                "directRtcVisionSupported": direct_vision_supported,
                "cloudVisionStreamType": _vision_snapshot_stream_type(self.request),
                "cloudVisionStreamName": _vision_snapshot_stream_name(self.request),
                "s2sVisionSupported": False if is_s2s else direct_vision_supported,
                "mode": mode,
                "sidecar": sidecar_payload,
                "updatedAt": self._vision_client_state.get("updated_at", 0.0),
                "message": str(self._vision_client_state.get("message") or ""),
                "settings": dict(self._screen_vision_settings),
            }
        status["companionVision"] = self.companion_vision_status()
        return status

    def vision_client_state(self, payload: Mapping[str, Any]) -> dict[str, Any]:
        with self._vision_lock:
            self._vision_client_state = {
                "screen_published": bool(payload.get("screenPublished", payload.get("screen_published", False))),
                "updated_at": time.time(),
                "message": str(payload.get("message") or ""),
            }
            screen_published = bool(self._vision_client_state.get("screen_published"))
        if screen_published:
            self.companion_vision_start()
        self._write_vision_record("vision_client_state", payload)
        return self.vision_status()

    def screen_vision_settings(self) -> dict[str, Any]:
        with self._vision_lock:
            return dict(self._screen_vision_settings)

    def screen_vision_update_settings(self, payload: Mapping[str, Any]) -> dict[str, Any]:
        restart_required = False
        with self._vision_lock:
            self._screen_vision_settings = _normalize_stream_settings(
                payload,
                self._screen_vision_settings,
            )
            snapshot_height = int(self._screen_vision_settings.get("snapshotHeight") or self._screen_vision_settings.get("height") or 720)
            _set_vision_snapshot_height(self.request, snapshot_height)
            if isinstance(self._active_request, dict):
                _set_vision_snapshot_height(self._active_request, snapshot_height)
            restart_required = bool(self._session is not None and self._session.started)
        self._ensure_camera_hub_if_needed("screen_vision_settings")
        self._write_vision_record("screen_vision_settings_updated", self.screen_vision_settings())
        return {
            "ok": True,
            "settings": self.screen_vision_settings(),
            "vision": self.vision_status(),
            "voiceRestartRequiredForSnapshotHeight": restart_required,
        }

    def camera_start(self) -> dict[str, Any]:
        self._ensure_camera_hub_if_needed("camera_start")
        with self._camera_lock:
            self._camera_desired = True
            self._camera_last_start_at = time.time()
            self._camera_client_state["message"] = "camera stream requested"
        self._write_vision_record("camera_start_requested", {"desired": True})
        return self.camera_status()

    def camera_stop(self, payload: Mapping[str, Any] | None = None) -> dict[str, Any]:
        payload = payload or {}
        force = bool(payload.get("force") or payload.get("manual") or payload.get("forceStop"))
        source = str(payload.get("source") or "")
        ignored = False
        age_sec = 999.0
        with self._camera_lock:
            age_sec = max(0.0, time.time() - self._camera_last_start_at)
            published = bool(self._camera_client_state.get("camera_published"))
            if (
                self._camera_desired
                and not published
                and not force
                and age_sec < self._camera_stop_startup_grace_sec
            ):
                ignored = True
                self._camera_client_state["message"] = "camera stop ignored during startup grace"
            else:
                self._camera_desired = False
                self._camera_client_state["message"] = "camera stream stop requested"
        self._write_vision_record(
            "camera_stop_requested",
            {
                "desired": False,
                "ignored": ignored,
                "force": force,
                "source": source,
                "ageSec": round(age_sec, 3),
            },
        )
        return self.camera_status()

    def camera_status(self) -> dict[str, Any]:
        with self._camera_lock:
            return {
                "ok": True,
                "desired": self._camera_desired,
                "cameraPublished": bool(self._camera_client_state.get("camera_published")),
                "updatedAt": self._camera_client_state.get("updated_at", 0.0),
                "message": str(self._camera_client_state.get("message") or ""),
                "settings": dict(self._camera_video_settings),
            }

    def camera_client_state(self, payload: Mapping[str, Any]) -> dict[str, Any]:
        with self._camera_lock:
            self._camera_client_state = {
                "camera_published": bool(payload.get("cameraPublished", payload.get("camera_published", False))),
                "updated_at": time.time(),
                "message": str(payload.get("message") or ""),
            }
        self._write_vision_record("camera_client_state", payload)
        return self.camera_status()

    def camera_video_settings(self) -> dict[str, Any]:
        with self._camera_lock:
            return dict(self._camera_video_settings)

    def camera_video_update_settings(self, payload: Mapping[str, Any]) -> dict[str, Any]:
        with self._camera_lock:
            self._camera_video_settings = _normalize_stream_settings(
                payload,
                self._camera_video_settings,
            )
        self._ensure_camera_hub_if_needed("camera_video_settings")
        self._write_vision_record("camera_video_settings_updated", self.camera_video_settings())
        return {"ok": True, "settings": self.camera_video_settings(), "camera": self.camera_status()}

    def _ensure_camera_hub_if_needed(self, reason: str) -> None:
        with self._vision_lock:
            screen_settings = dict(self._screen_vision_settings)
        with self._camera_lock:
            camera_settings = dict(self._camera_video_settings)

        screen_overlay_enabled = bool(screen_settings.get("cameraOverlayEnabled"))
        camera_uses_hub = bool(camera_settings.get("useCameraHub"))
        if not screen_overlay_enabled and not camera_uses_hub:
            return

        source_url = str(
            screen_settings.get("cameraOverlaySourceUrl")
            or camera_settings.get("cameraHubUrl")
            or "http://127.0.0.1:17863/stream.mjpg"
        )
        if not _is_local_camera_hub_url(source_url):
            return
        if _camera_hub_status_ready(source_url, timeout=0.45):
            return

        process = self._managed_camera_hub_process
        if process is not None and process.poll() is None:
            return

        now = time.time()
        if now - self._managed_camera_hub_last_start_at < 2.0:
            return

        tracker_root = _resolve_head_tracker_root()
        script_path = tracker_root / "head_tracker.py" if tracker_root else Path()
        if not tracker_root or not script_path.exists():
            self._write_vision_record(
                "camera_hub_start_failed",
                {"reason": "head_tracker_not_found", "sourceUrl": source_url, "trigger": reason},
            )
            return

        python_path = tracker_root / ".venv" / "Scripts" / "python.exe"
        executable = str(python_path if python_path.exists() else sys.executable)
        runtime_root = _silver_wolf_runtime_root()
        runtime_root.mkdir(parents=True, exist_ok=True)
        status_path = runtime_root / "head_tracker_bridge_hub_5055.json"
        stdout_path = runtime_root / "head_tracker_bridge_hub.out.log"
        stderr_path = runtime_root / "head_tracker_bridge_hub.err.log"
        width = int(_clamp_float(camera_settings.get("width"), 1280.0, 320.0, 1920.0))
        height = int(_clamp_float(camera_settings.get("height"), 720.0, 180.0, 1080.0))
        fps = int(_clamp_float(camera_settings.get("fps"), 10.0, 1.0, 30.0))
        frame_fps = min(15, max(1, fps))
        args = [
            str(script_path),
            "--camera-index",
            "0",
            "--host",
            "127.0.0.1",
            "--port",
            "5055",
            "--width",
            str(width),
            "--height",
            str(height),
            "--fps",
            str(fps),
            "--backend",
            "auto",
            "--center-mode",
            "bbox",
            "--status-file",
            str(status_path),
            "--no-mirror",
            "--print-every",
            "0",
            "--frame-host",
            "127.0.0.1",
            "--frame-port",
            "17863",
            "--frame-server-fps",
            str(frame_fps),
            "--frame-jpeg-quality",
            "82",
        ]

        creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        try:
            stdout_handle = stdout_path.open("ab")
            stderr_handle = stderr_path.open("ab")
            self._managed_camera_hub_process = subprocess.Popen(
                [executable, *args],
                cwd=str(tracker_root),
                stdout=stdout_handle,
                stderr=stderr_handle,
                creationflags=creationflags,
            )
            self._managed_camera_hub_last_start_at = now
            self._write_vision_record(
                "camera_hub_start_requested",
                {
                    "pid": self._managed_camera_hub_process.pid,
                    "sourceUrl": source_url,
                    "trigger": reason,
                    "width": width,
                    "height": height,
                    "fps": fps,
                },
            )
        except Exception as exc:
            self._managed_camera_hub_process = None
            self._managed_camera_hub_last_start_at = now
            self._write_vision_record(
                "camera_hub_start_failed",
                {"reason": str(exc), "sourceUrl": source_url, "trigger": reason},
            )

    def face_tracking_status(self) -> dict[str, Any]:
        with self._face_tracking_lock:
            packet_fps = int(self._camera_video_settings.get("faceTrackingPacketFps") or 15)
            return {
                "ok": not bool(self._face_tracking_client_state.get("last_error")),
                "host": self._face_tracking_host,
                "port": self._face_tracking_port,
                "packetCount": int(self._face_tracking_client_state.get("packet_count") or 0),
                "lastPacketAt": self._face_tracking_client_state.get("last_packet_at", 0.0),
                "lastError": str(self._face_tracking_client_state.get("last_error") or ""),
                "modelAvailable": MEDIAPIPE_FACE_MODEL_PATH.exists(),
                "gpuPreferred": True,
                "packetFps": packet_fps,
            }

    def face_tracking_packet(self, payload: Mapping[str, Any]) -> dict[str, Any]:
        packet = _sanitize_face_tracking_packet(payload)
        try:
            data = json.dumps(packet, separators=(",", ":")).encode("utf-8")
            self._face_tracking_udp.sendto(data, (self._face_tracking_host, self._face_tracking_port))
            with self._face_tracking_lock:
                self._face_tracking_client_state["packet_count"] = int(
                    self._face_tracking_client_state.get("packet_count") or 0
                ) + 1
                self._face_tracking_client_state["last_packet_at"] = time.time()
                self._face_tracking_client_state["last_error"] = ""
            return {"ok": True, "port": self._face_tracking_port}
        except Exception as exc:
            with self._face_tracking_lock:
                self._face_tracking_client_state["last_error"] = str(exc)
            self.logger.warning("face_tracking_udp_send_failed error=%s", exc)
            return {"ok": False, "error": str(exc), "port": self._face_tracking_port}

    def companion_vision_start(self, force_enable: bool = False) -> dict[str, Any]:
        if force_enable:
            with self._companion_vision_lock:
                self._companion_vision_config["enabled"] = True
        if not bool(self._companion_vision_config.get("enabled", True)):
            return self.companion_vision_status()
        if not (_direct_rtc_vision_supported_in_request(self.request) or self._sidecar_vision_available()):
            return self.companion_vision_status()
        started = False
        with self._companion_vision_lock:
            was_running = bool(self._companion_vision_running)
            self._companion_vision_running = True
            if not was_running:
                # A menu click should feel like it armed the mode immediately.
                # Once screen sharing is published, the first companion prompt
                # can fire without waiting for stale pacing timestamps.
                self._companion_vision_last_prompt_at = 0.0
                self._companion_vision_last_success_at = 0.0
                self._companion_vision_next_allowed_at = 0.0
                self._companion_vision_last_skip_reason = ""
            if self._companion_vision_thread is not None and self._companion_vision_thread.is_alive():
                return_status = True
            else:
                return_status = False
                self._companion_vision_stop_event.clear()
                self._companion_vision_thread = Thread(
                    target=self._run_companion_vision_loop,
                    name="volc-companion-vision-loop",
                    daemon=True,
                )
                self._companion_vision_thread.start()
                started = True
        if return_status:
            return self.companion_vision_status()
        if started:
            self._write_vision_record("companion_vision_start", self.companion_vision_status())
        return self.companion_vision_status()

    def companion_vision_stop(self) -> dict[str, Any]:
        thread: Thread | None = None
        with self._companion_vision_lock:
            self._companion_vision_config["enabled"] = False
            self._companion_vision_running = False
            self._companion_vision_pending = False
            self._companion_vision_pending_id = 0
            self._companion_vision_pending_until = 0.0
            self._companion_vision_response_active = False
            self._companion_vision_pending_text = ""
            self._companion_vision_fallback_in_flight = False
            self._companion_vision_stop_event.set()
            thread = self._companion_vision_thread
        if thread is not None and thread.is_alive() and thread is not current_thread():
            thread.join(timeout=0.4)
        with self._companion_vision_lock:
            if self._companion_vision_thread is thread:
                self._companion_vision_thread = None
        return self.companion_vision_status()

    def companion_vision_set_interval(self, interval_sec: Any) -> dict[str, Any]:
        interval = _snap_companion_vision_interval(interval_sec)
        with self._companion_vision_lock:
            self._companion_vision_config["interval_sec"] = interval
            self._companion_vision_next_allowed_at = 0.0
            self._companion_vision_last_skip_reason = ""
        status = self.companion_vision_status()
        self._write_vision_record(
            "companion_vision_interval",
            {
                "requestedIntervalSec": interval_sec,
                "intervalSec": interval,
                "status": status,
            },
        )
        return status

    def companion_vision_status(self) -> dict[str, Any]:
        with self._vision_lock:
            screen_published = bool(self._vision_client_state.get("screen_published"))
            vision_desired = bool(self._vision_desired)
        with self._companion_vision_lock:
            now = time.monotonic()
            priority = self._companion_vision_priority_snapshot_locked(now)
            return {
                "enabled": bool(self._companion_vision_config.get("enabled", True)),
                "running": bool(self._companion_vision_running),
                "intervalSec": float(self._companion_vision_config.get("interval_sec", 5.0)),
                "intervalPresetsSec": [int(value) for value in _COMPANION_VISION_INTERVAL_PRESETS],
                "priority": priority,
                "waitUntilSpeechDone": bool(self._companion_vision_config.get("wait_until_speech_done", True)),
                "pendingTimeoutSec": float(self._companion_vision_config.get("pending_timeout_sec", 12.0)),
                "screenPublished": screen_published,
                "visionDesired": vision_desired,
                "pending": bool(self._companion_vision_pending),
                "waitingForWelcome": bool(self._companion_vision_waiting_for_welcome_done),
                "recentContextCount": len(self._companion_vision_recent_ai_texts),
                "recentContext": [text for _, text in self._companion_vision_recent_ai_texts[-3:]],
                "lastPromptAt": self._companion_vision_last_prompt_at,
                "lastSkipReason": self._companion_vision_last_skip_reason,
                "failureCount": int(self._companion_vision_failure_count),
                "failureCircuitBreakCount": int(self._companion_vision_config.get("failure_circuit_break_count", 0) or 0),
                "nextAllowedInSec": round(max(0.0, self._companion_vision_next_allowed_at - time.monotonic()), 2),
                "emptyFallbackEnabled": bool(self._companion_vision_config.get("empty_fallback_enabled", True)),
                "emptyFallbackInFlight": bool(self._companion_vision_fallback_in_flight),
                "emptyFallbackCount": int(self._companion_vision_empty_fallback_count),
                "lastFallbackError": self._companion_vision_last_fallback_error,
                "localVisionSidecarEnabled": bool(self._companion_vision_config.get("local_vision_sidecar_enabled", True)),
                "localVisionSidecarTimeoutSec": float(self._companion_vision_config.get("local_vision_sidecar_timeout_sec", 20.0)),
            }

    def _run_companion_vision_loop(self) -> None:
        tick_sec = float(self._companion_vision_config.get("tick_sec", 0.5))
        tick_sec = max(0.2, min(tick_sec, 2.0))
        while not self._companion_vision_stop_event.wait(tick_sec):
            try:
                self._companion_vision_tick()
            except Exception:
                self.logger.exception("companion_vision_tick_failed")

    def _companion_vision_tick(self, *, force: bool = False) -> dict[str, Any]:
        now = time.monotonic()
        interval_sec = max(1.0, float(self._companion_vision_config.get("interval_sec", 5.0)))
        with self._vision_lock:
            screen_published = bool(self._vision_client_state.get("screen_published"))
            vision_desired = bool(self._vision_desired)
        with self._companion_vision_lock:
            if not self._companion_vision_running:
                return self._companion_vision_skip_locked("not_running")
            if not self._voice_active:
                return self._companion_vision_skip_locked("voice_not_active")
            if not vision_desired:
                return self._companion_vision_skip_locked("vision_not_desired")
            local_vision_sidecar = bool(self._companion_vision_config.get("local_vision_sidecar_enabled", True))
            if not screen_published and not local_vision_sidecar and not self._sidecar_vision_available():
                return self._companion_vision_skip_locked("screen_not_published")
            if self._sidecar_vision_available():
                return self._companion_vision_skip_locked("sidecar_vision_observer_managed")
            if self._companion_vision_waiting_for_welcome_done:
                max_wait_sec = max(3.0, float(self._companion_vision_config.get("welcome_max_wait_sec", 18.0)))
                started_at = float(self._companion_vision_welcome_wait_started_at or 0.0)
                if started_at > 0.0 and now - started_at >= max_wait_sec:
                    self._unlock_companion_vision_after_welcome_locked(now, "welcome_timeout")
                else:
                    return self._companion_vision_skip_locked("waiting_welcome")
            if now < self._companion_vision_next_allowed_at:
                return self._companion_vision_skip_locked("backoff")
            priority_block = self._companion_vision_priority_block_reason_locked(now)
            if priority_block:
                return self._companion_vision_skip_locked(priority_block)
            user_silence_sec = max(0.0, float(self._companion_vision_config.get("user_silence_sec", 0.0)))
            recent_user_at = float(getattr(self.adapter, "_recent_user_subtitle_at", 0.0) or 0.0)
            if user_silence_sec > 0.0 and recent_user_at > 0.0 and now - recent_user_at < user_silence_sec:
                return self._companion_vision_skip_locked("user_speaking_recently")
            if (
                user_silence_sec > 0.0
                and self._last_ai_state in {"listening", "interrupted"}
                and self._last_ai_state_at > 0.0
                and now - self._last_ai_state_at < user_silence_sec
            ):
                return self._companion_vision_skip_locked("user_state_recently")
            last_activity_at = max(self._companion_vision_last_prompt_at, self._companion_vision_last_success_at)
            if not force and now - last_activity_at < interval_sec:
                return self._companion_vision_skip_locked("interval_wait")
            if self._companion_vision_prompt_pending_locked(now):
                return self._companion_vision_skip_locked("prompt_pending")
            if now < self._companion_vision_next_allowed_at:
                return self._companion_vision_skip_locked("backoff")
            if self._companion_vision_ai_busy_locked(now):
                return self._companion_vision_skip_locked("ai_speaking_or_thinking")
            min_idle_sec = max(0.0, float(self._companion_vision_config.get("min_idle_sec", 0.0)))
            if min_idle_sec > 0.0 and self._last_ai_state == "idle" and self._last_ai_state_at > 0.0:
                if now - self._last_ai_state_at < min_idle_sec:
                    return self._companion_vision_skip_locked("idle_settle")

            interrupt_mode = int(self._companion_vision_config.get("interrupt_mode", 3))
            base_text = str(self._companion_vision_config.get("prompt") or _DEFAULT_COMPANION_VISION_PROMPT).strip()
            if local_vision_sidecar:
                pending_id = int(time.time() * 1000) % 2_000_000_000
                self._companion_vision_last_prompt_at = now
                self._companion_vision_pending = True
                self._companion_vision_pending_id = pending_id
                self._companion_vision_pending_until = now + max(
                    10.0,
                    float(self._companion_vision_config.get("pending_timeout_sec", 12.0))
                    + float(self._companion_vision_config.get("local_vision_sidecar_timeout_sec", 20.0)),
                )
                self._companion_vision_pending_has_response = False
                self._companion_vision_pending_text = base_text
                self._companion_vision_last_skip_reason = "local_vision_sidecar_queued"
                Thread(
                    target=self._run_companion_vision_sidecar,
                    args=(pending_id, base_text, max(1, min(3, interrupt_mode))),
                    name="companion-vision-sidecar",
                    daemon=True,
                ).start()
                self.adapter.sender.write_event_record(
                    trace_id=f"companion-sidecar-{pending_id}",
                    event_type="companion_vision_sidecar_queued",
                    source="local:companion_vision_sidecar",
                    raw_payload={"id": pending_id, "prompt": _debug_preview_text(base_text, 120)},
                    mapping_result={"queued": True, "transport": "local_vision_sidecar"},
                    send_to_godot=False,
                    event_received_at=time.time(),
                    pose_generated_at=time.time(),
                )
                return {"ok": True, "queued": True, "id": pending_id, "transport": "local_vision_sidecar"}

            text = self._companion_vision_prompt_with_context_locked(base_text, now)
            result = self._queue_external_text(
                text,
                interrupt_mode=max(0, min(3, interrupt_mode)),
                source="companion_vision",
                metadata={
                    "role": "external",
                    "message_type": "external_text",
                    "source": "companion_vision",
                    "transport": "rtc_binary",
                },
            )
            self._companion_vision_last_prompt_at = now
            self._companion_vision_pending = True
            self._companion_vision_pending_id = int(result.get("id") or 0)
            self._companion_vision_pending_until = now + max(3.0, float(self._companion_vision_config.get("pending_timeout_sec", 12.0)))
            self._companion_vision_pending_has_response = False
            self._companion_vision_pending_text = text
            self._companion_vision_last_skip_reason = ""
            return {"ok": True, "queued": True, "id": self._companion_vision_pending_id}

    def _sidecar_vision_available(self) -> bool:
        return self._vision_observer is not None and bool(self._vision_observer_config.enabled)

    def _companion_vision_skip_locked(self, reason: str) -> dict[str, Any]:
        self._companion_vision_last_skip_reason = reason
        return {"ok": True, "queued": False, "reason": reason}

    def _unlock_companion_vision_after_welcome_locked(self, now: float, reason: str) -> None:
        self._companion_vision_waiting_for_welcome_done = False
        self._companion_vision_welcome_seen_speaking = False
        self._companion_vision_welcome_wait_started_at = 0.0
        interval_sec = max(1.0, float(self._companion_vision_config.get("interval_sec", 5.0)))
        self._companion_vision_last_success_at = now
        self._companion_vision_next_allowed_at = max(self._companion_vision_next_allowed_at, now + interval_sec)
        self._companion_vision_last_skip_reason = reason

    def _companion_vision_prompt_pending_locked(self, now: float) -> bool:
        if not self._companion_vision_pending:
            return False
        if now <= self._companion_vision_pending_until:
            return True
        if self._companion_vision_pending_has_response:
            self._mark_companion_vision_success_locked(now)
        else:
            self._mark_companion_vision_failure_locked("pending_timeout", now)
            return False
        self._companion_vision_pending = False
        self._companion_vision_pending_id = 0
        self._companion_vision_pending_until = 0.0
        self._companion_vision_pending_has_response = False
        self._companion_vision_pending_text = ""
        return False

    def _companion_vision_ai_busy_locked(self, now: float) -> bool:
        if not bool(self._companion_vision_config.get("wait_until_speech_done", True)):
            return False
        status = self.adapter.runtime_status()
        if bool(status.get("audio_active")):
            return True
        current_state = str(status.get("current_state") or self._last_ai_state or "idle").strip().lower()
        state_age_sec = float(status.get("current_state_age_sec") or 0.0)
        max_busy_sec = max(0.0, float(self._companion_vision_config.get("max_busy_without_audio_sec", 0.0)))
        if current_state in {"speaking", "thinking", "listening", "interrupted"} and max_busy_sec > 0.0 and state_age_sec >= max_busy_sec:
            self.logger.warning(
                "companion vision holds stale ai state state=%s age_sec=%.1f max_busy_sec=%.1f",
                current_state,
                state_age_sec,
                max_busy_sec,
            )
            self._maybe_schedule_stale_thinking_recovery(status, trigger="companion_vision")
        if current_state in {"speaking", "thinking", "listening", "interrupted"}:
            return True
        return False

    def _companion_vision_priority_block_reason_locked(self, now: float) -> str:
        status = self.adapter.runtime_status()
        current_state = str(status.get("current_state") or self._last_ai_state or "idle")
        current_state = _normalize_bridge_ai_state(current_state) or current_state
        if current_state in {"listening", "interrupted"}:
            return "priority_user_speaking"

        user_silence_sec = max(0.0, float(self._companion_vision_config.get("user_silence_sec", 0.0)))
        recent_user_at = float(getattr(self.adapter, "_recent_user_subtitle_at", 0.0) or 0.0)
        if user_silence_sec > 0.0 and recent_user_at > 0.0 and now - recent_user_at < user_silence_sec:
            return "priority_user_recent"

        if self._voice_priority_user_until > now:
            return "priority_user_speaking"

        if self._voice_priority_waiting_for_answer:
            if self._voice_priority_waiting_until <= 0.0 or now <= self._voice_priority_waiting_until:
                return "priority_waiting_user_answer"
            self._voice_priority_waiting_for_answer = False
            self._voice_priority_last_reason = "user_answer_wait_expired"

        if self._voice_priority_answer_until > now:
            return "priority_answering_user"

        return ""

    def _companion_vision_priority_snapshot_locked(self, now: float) -> dict[str, Any]:
        reason = self._companion_vision_priority_block_reason_locked(now)
        if reason.startswith("priority_user"):
            level = 100
            owner = "user_speech"
        elif reason in {"priority_waiting_user_answer", "priority_answering_user"}:
            level = 80
            owner = "answer_user"
        else:
            level = 10 if self._companion_vision_running else 0
            owner = "screen_polling" if self._companion_vision_running else "idle"
        return {
            "level": level,
            "owner": owner,
            "blockReason": reason,
            "waitingForUserAnswer": bool(self._voice_priority_waiting_for_answer),
            "userHoldLeftSec": round(max(0.0, self._voice_priority_user_until - now), 2),
            "answerHoldLeftSec": round(max(0.0, self._voice_priority_answer_until - now), 2),
            "lastReason": self._voice_priority_last_reason,
        }

    def _mark_user_voice_priority_locked(self, now: float, reason: str, *, expect_answer: bool = True) -> None:
        user_hold = max(2.0, float(self._companion_vision_config.get("user_silence_sec", 0.0)))
        self._voice_priority_user_until = max(self._voice_priority_user_until, now + user_hold)
        if expect_answer:
            wait_hold = max(12.0, self._speech_watchdog_delay_sec + float(self._companion_vision_config.get("pending_timeout_sec", 8.0)) + 4.0)
            self._voice_priority_waiting_for_answer = True
            self._voice_priority_waiting_until = max(self._voice_priority_waiting_until, now + wait_hold)
        self._voice_priority_last_reason = reason
        if self._companion_vision_pending:
            self._cancel_companion_vision_pending_locked("cancelled_by_user_priority", now)

    def _mark_user_answer_priority_locked(self, now: float, reason: str) -> None:
        hold = max(6.0, float(self._companion_vision_config.get("max_busy_without_audio_sec", 0.0)))
        self._voice_priority_waiting_for_answer = False
        self._voice_priority_waiting_until = 0.0
        self._voice_priority_answer_until = max(self._voice_priority_answer_until, now + hold)
        self._voice_priority_last_reason = reason

    def _clear_user_answer_priority_locked(self, now: float) -> None:
        hold = max(1.0, float(self._companion_vision_config.get("interval_sec", 5.0)))
        self._voice_priority_waiting_for_answer = False
        self._voice_priority_waiting_until = 0.0
        self._voice_priority_answer_until = max(self._voice_priority_answer_until, now + hold)
        self._voice_priority_last_reason = "answer_idle_hold"

    def _note_ai_state_for_companion(self, state: str) -> None:
        normalized = _normalize_bridge_ai_state(state)
        if not normalized:
            return
        now = time.monotonic()
        with self._companion_vision_lock:
            self._last_ai_state = normalized
            self._last_ai_state_at = now
            if self._companion_vision_waiting_for_welcome_done:
                if normalized == "speaking":
                    self._companion_vision_welcome_seen_speaking = True
                elif normalized == "idle" and self._companion_vision_welcome_seen_speaking:
                    self._unlock_companion_vision_after_welcome_locked(now, "welcome_finished")
            if normalized in {"listening", "interrupted"}:
                self._mark_user_voice_priority_locked(now, normalized, expect_answer=True)
            elif normalized in {"thinking", "speaking"} and self._voice_priority_waiting_for_answer:
                if not self._companion_vision_pending and not self._companion_vision_response_active:
                    self._mark_user_answer_priority_locked(now, f"user_{normalized}")
            if normalized == "speaking" and self._companion_vision_pending:
                self._mark_companion_vision_success_locked(now)
                self._companion_vision_response_active = True
            if normalized == "idle" and self._companion_vision_pending:
                if self._companion_vision_pending_has_response:
                    self._mark_companion_vision_success_locked(now)
                else:
                    scheduled = self._schedule_companion_empty_response_fallback_locked("empty_response", now)
                    if not scheduled:
                        self._mark_companion_vision_failure_locked("empty_response", now)
            elif normalized == "idle" and self._companion_vision_response_active:
                self._companion_vision_last_success_at = now
                self._companion_vision_response_active = False
            elif normalized in {"listening", "interrupted"} and self._companion_vision_pending:
                self._cancel_companion_vision_pending_locked("cancelled_by_user_speech", now)
            if normalized == "idle" and (self._voice_priority_waiting_for_answer or self._voice_priority_answer_until > 0.0):
                self._clear_user_answer_priority_locked(now)
        self._mark_ai_playback_state(normalized)
        if normalized == "speaking":
            self._note_speech_watchdog_ai_response()

    def _note_external_text_result(self, item: Mapping[str, Any]) -> None:
        try:
            message_id = int(item.get("id") or 0)
        except (TypeError, ValueError):
            return
        with self._companion_vision_lock:
            if message_id != self._companion_vision_pending_id:
                return
            if not bool(item.get("ok")):
                self._mark_companion_vision_failure_locked("send_failed", time.monotonic())

    def _note_companion_ai_text(self, text: str, *, remember: bool = True) -> None:
        text = _normalize_subtitle_plain_text(text)
        if not text:
            return
        self._remember_recent_ai_echo_text(text)
        now = time.monotonic()
        with self._companion_vision_lock:
            from_companion_prompt = self._companion_vision_pending or self._companion_vision_response_active
            if not from_companion_prompt:
                late_window_sec = max(
                    30.0,
                    float(self._companion_vision_config.get("pending_timeout_sec", 12.0)) + 20.0,
                )
                from_companion_prompt = (
                    self._companion_vision_failure_count > 0
                    and self._companion_vision_last_prompt_at > 0.0
                    and now - self._companion_vision_last_prompt_at <= late_window_sec
                )
                if from_companion_prompt:
                    self._mark_companion_vision_success_locked(now)
                    self._companion_vision_response_active = True
            if self._companion_vision_pending:
                self._companion_vision_pending_has_response = True
                self._companion_vision_pending = False
                self._companion_vision_pending_id = 0
                self._companion_vision_pending_until = 0.0
                self._companion_vision_pending_text = ""
                self._companion_vision_fallback_in_flight = False
                self._companion_vision_failure_count = 0
                self._companion_vision_next_allowed_at = 0.0
                self._companion_vision_last_success_at = now
                self._companion_vision_last_skip_reason = ""
                self._companion_vision_response_active = True
            if remember and from_companion_prompt:
                self._remember_companion_ai_text_locked(text, now)
        if not from_companion_prompt:
            self._note_speech_watchdog_ai_response()

    def _remember_companion_ai_text_locked(self, text: str, now: float) -> None:
        window_sec = max(30.0, float(self._companion_vision_config.get("recent_context_window_sec", 300.0)))
        limit = max(0, int(self._companion_vision_config.get("recent_context_count", 0)))
        if limit <= 0:
            self._companion_vision_recent_ai_texts = []
            return
        normalized = _normalize_subtitle_plain_text(text)
        if not normalized:
            return
        kept = [
            (timestamp, item)
            for timestamp, item in self._companion_vision_recent_ai_texts
            if now - timestamp <= window_sec and _normalize_subtitle_plain_text(item) != normalized
        ]
        kept.append((now, normalized))
        self._companion_vision_recent_ai_texts = kept[-limit:]

    def _companion_vision_prompt_with_context_locked(self, base_text: str, now: float) -> str:
        window_sec = max(30.0, float(self._companion_vision_config.get("recent_context_window_sec", 300.0)))
        limit = max(0, int(self._companion_vision_config.get("recent_context_count", 0)))
        if limit <= 0:
            return base_text
        self._companion_vision_recent_ai_texts = [
            (timestamp, text)
            for timestamp, text in self._companion_vision_recent_ai_texts
            if now - timestamp <= window_sec
        ][-limit:]
        recent = [text for _, text in self._companion_vision_recent_ai_texts[-limit:]]
        if not recent:
            return base_text
        lines = "\n".join(f"- {_clip_companion_context_line(text)}" for text in recent)
        return (
            f"{base_text}\n\n"
            "Short-term context for this proactive screen check:\n"
            "You already said these recent companion comments:\n"
            f"{lines}\n"
            "Do not repeat the same observation, wording, joke, or advice. "
            "Use the current screen frame plus this memory. If the screen appears unchanged, "
            "say one short different angle or directly say there is no important change; do not invent unseen changes."
        )

    def _note_speech_watchdog_user_final(self, text: str) -> None:
        text = _normalize_subtitle_plain_text(text)
        if not text or not self._speech_watchdog_enabled:
            return
        if _has_voice_stop_intent(text):
            return
        with self._speech_watchdog_lock:
            self._speech_watchdog_turn_id += 1
            turn_id = self._speech_watchdog_turn_id
            self._speech_watchdog_user_text = text
            self._speech_watchdog_user_at = time.monotonic()
            self._speech_watchdog_ai_seen = False
            self._speech_watchdog_busy_retry_count = 0
            self._speech_watchdog_direct_fallback_in_flight = False
            if self._speech_watchdog_timer is not None:
                self._speech_watchdog_timer.cancel()
            self._speech_watchdog_timer = Timer(
                self._speech_watchdog_delay_sec,
                self._fire_speech_watchdog,
                args=(turn_id,),
            )
            self._speech_watchdog_timer.daemon = True
            self._speech_watchdog_timer.start()

    def _note_speech_watchdog_ai_response(self) -> None:
        with self._speech_watchdog_lock:
            self._speech_watchdog_ai_seen = True
            if self._speech_watchdog_timer is not None:
                self._speech_watchdog_timer.cancel()
                self._speech_watchdog_timer = None
            self._speech_watchdog_direct_fallback_in_flight = False

    def _cancel_speech_watchdog(self) -> None:
        with self._speech_watchdog_lock:
            if self._speech_watchdog_timer is not None:
                self._speech_watchdog_timer.cancel()
                self._speech_watchdog_timer = None
            self._speech_watchdog_ai_seen = False
            self._speech_watchdog_direct_fallback_in_flight = False

    def _fire_speech_watchdog(self, turn_id: int) -> None:
        with self._speech_watchdog_lock:
            if (
                turn_id != self._speech_watchdog_turn_id
                or self._speech_watchdog_ai_seen
                or self._speech_watchdog_retry_turn_id == turn_id
            ):
                return
            user_text = self._speech_watchdog_user_text
            self._speech_watchdog_timer = None
            user_at = self._speech_watchdog_user_at
        if not user_text:
            return
        if self._speech_watchdog_ai_busy():
            now = time.monotonic()
            elapsed_sec = max(0.0, now - user_at) if user_at > 0.0 else 0.0
            rescheduled = False
            direct_fallback_queued = False
            with self._speech_watchdog_lock:
                can_direct_fallback = (
                    self._speech_watchdog_direct_fallback_enabled
                    and turn_id == self._speech_watchdog_turn_id
                    and not self._speech_watchdog_ai_seen
                    and self._speech_watchdog_direct_fallback_turn_id != turn_id
                    and not self._speech_watchdog_direct_fallback_in_flight
                    and elapsed_sec >= self._speech_watchdog_direct_fallback_sec
                )
                can_retry = (
                    turn_id == self._speech_watchdog_turn_id
                    and not self._speech_watchdog_ai_seen
                    and self._speech_watchdog_retry_turn_id != turn_id
                    and user_at > 0.0
                    and now - user_at <= self._speech_watchdog_busy_retry_max_sec
                )
                if can_direct_fallback:
                    self._speech_watchdog_direct_fallback_in_flight = True
                    self._speech_watchdog_direct_fallback_turn_id = turn_id
                    self._speech_watchdog_last_skip_reason = "direct_fallback_queued"
                    direct_fallback_queued = True
                    Thread(
                        target=self._run_speech_watchdog_direct_fallback,
                        args=(turn_id, user_text),
                        daemon=True,
                        name="speech-watchdog-direct-fallback",
                    ).start()
                elif can_retry:
                    self._speech_watchdog_busy_retry_count += 1
                    self._speech_watchdog_last_skip_reason = "ai_busy_retrying"
                    self._speech_watchdog_timer = Timer(
                        self._speech_watchdog_busy_retry_interval_sec,
                        self._fire_speech_watchdog,
                        args=(turn_id,),
                    )
                    self._speech_watchdog_timer.daemon = True
                    self._speech_watchdog_timer.start()
                    rescheduled = True
                else:
                    self._speech_watchdog_last_skip_reason = "ai_busy_give_up"
            try:
                status_snapshot = dict(self.adapter.runtime_status())
            except Exception:
                status_snapshot = {}
            self.adapter.sender.write_event_record(
                trace_id=f"speech-watchdog-{turn_id}",
                event_type="speech_turn_watchdog_skip_busy",
                source="local:speech_watchdog",
                raw_payload={"turn_id": turn_id, "text": user_text, "delay_sec": self._speech_watchdog_delay_sec},
                mapping_result={
                    "skipped": True,
                    "reason": "ai_busy",
                    "rescheduled": rescheduled,
                    "directFallbackQueued": direct_fallback_queued,
                    "retryInSec": self._speech_watchdog_busy_retry_interval_sec if rescheduled else 0.0,
                    "busyRetryCount": self._speech_watchdog_busy_retry_count,
                    "elapsedSec": round(elapsed_sec, 3),
                    "status": status_snapshot,
                    "lastAiState": self._last_ai_state,
                },
                send_to_godot=False,
                event_received_at=time.time(),
                pose_generated_at=time.time(),
            )
            return
        result = self._queue_external_text(
            user_text,
            interrupt_mode=0,
            source="speech_watchdog",
            metadata={
                "role": "user",
                "message_type": "speech_watchdog_retry",
                "original_text": user_text,
                "turn_id": turn_id,
            },
        )
        with self._speech_watchdog_lock:
            self._speech_watchdog_retry_turn_id = turn_id
            self._speech_watchdog_last_retry_at = time.monotonic()
            self._speech_watchdog_last_skip_reason = ""
        self.adapter.sender.write_event_record(
            trace_id=f"speech-watchdog-{turn_id}",
            event_type="speech_turn_watchdog_retry",
            source="local:speech_watchdog",
            raw_payload={"turn_id": turn_id, "text": user_text, "delay_sec": self._speech_watchdog_delay_sec},
            mapping_result=result,
            send_to_godot=False,
            event_received_at=time.time(),
            pose_generated_at=time.time(),
        )

    def _run_speech_watchdog_direct_fallback(self, turn_id: int, user_text: str) -> None:
        text = ""
        error = ""
        result: dict[str, Any] = {"ok": False, "error": "not_started"}
        stale = False
        try:
            text = self._generate_speech_watchdog_direct_reply(user_text)
            if not text:
                raise RuntimeError("speech_direct_fallback_empty")
            with self._speech_watchdog_lock:
                stale = (
                    turn_id != self._speech_watchdog_turn_id
                    or self._speech_watchdog_ai_seen
                    or not self._voice_active
                )
                if stale:
                    self._speech_watchdog_direct_fallback_in_flight = False
                    return
            result = self._send_external_text_to_speech_via_update_voice_chat(
                text,
                source="speech_watchdog_direct_fallback",
                interrupt_mode=3,
            )
            if not result.get("ok"):
                error = str(result.get("error") or "tts_failed")
        except Exception as exc:
            error = str(exc)
            self.logger.exception("speech watchdog direct fallback failed")

        clean_text = _sanitize_direct_speech_text(text)
        now = time.monotonic()
        with self._speech_watchdog_lock:
            self._speech_watchdog_direct_fallback_in_flight = False
            if turn_id == self._speech_watchdog_turn_id and result.get("ok") and clean_text:
                self._speech_watchdog_ai_seen = True
                self._speech_watchdog_direct_fallback_count += 1
                self._speech_watchdog_last_skip_reason = "direct_fallback_spoken"
        if result.get("ok") and clean_text:
            self._remember_recent_ai_echo_text(clean_text)
            with self._companion_vision_lock:
                self._mark_user_answer_priority_locked(now, "speech_direct_fallback")
            self._send_voice_output_pose(
                {
                    "type": "pet_pose",
                    "state": "speaking",
                    "emotion": "neutral",
                    "gesture": "small_tease",
                    "posture": "stand",
                    "bubble_text": clean_text,
                    "mouth": "audio_volume",
                    "audio_active": True,
                    "overlay_only": False,
                    "duration_ms": 0,
                },
                event_type="speech_watchdog_direct_fallback_speaking",
                raw_payload={"turn_id": turn_id, "text": clean_text},
                source="local:speech_watchdog_direct_fallback",
            )
        self.adapter.sender.write_event_record(
            trace_id=f"speech-direct-fallback-{turn_id}",
            event_type="speech_turn_watchdog_direct_fallback_result",
            source="local:speech_watchdog",
            raw_payload={
                "turn_id": turn_id,
                "ok": bool(result.get("ok")),
                "text": clean_text,
                "error": error,
                "stale": stale,
            },
            mapping_result={
                "spoken": bool(result.get("ok") and clean_text and not stale),
                "error": error,
                "textPreview": _debug_preview_text(clean_text, 80),
            },
            send_to_godot=False,
            event_received_at=time.time(),
            pose_generated_at=time.time(),
        )

    def _generate_speech_watchdog_direct_reply(self, user_text: str) -> str:
        llm_config = self.request.get("Config", {}).get("LLMConfig", {})
        if not isinstance(llm_config, Mapping):
            raise RuntimeError("missing LLMConfig for speech direct fallback")
        url = str(llm_config.get("Url") or llm_config.get("URL") or "").strip()
        api_key = str(llm_config.get("APIKey") or llm_config.get("ApiKey") or "").strip()
        model = str(llm_config.get("ModelName") or llm_config.get("Model") or "mimo-v2.5").strip()
        if not url or not api_key:
            raise RuntimeError("missing LLM endpoint or API key for speech direct fallback")
        system_text = _system_messages_text(llm_config.get("SystemMessages", []))
        system_text = system_text.strip()
        messages: list[dict[str, Any]] = []
        if system_text:
            messages.append({"role": "system", "content": system_text})
        messages.append({"role": "user", "content": _trim_external_text_to_llm(user_text, max_chars=150, ensure_punctuation=False)})
        body: dict[str, Any] = {
            "model": model,
            "messages": messages,
            "temperature": 0.2,
            "top_p": 0.5,
            "max_tokens": int(max(16, float(llm_config.get("MaxTokens", 512) or 512))),
            "stream": False,
        }
        thinking_type = str(llm_config.get("ThinkingType") or "auto").strip().lower()
        if thinking_type:
            body["thinking"] = {"type": thinking_type}
        response = requests.post(
            _chat_completions_url(url),
            headers={"Authorization": "Bearer " + api_key, "Content-Type": "application/json"},
            json=body,
            timeout=self._speech_watchdog_direct_fallback_timeout_sec,
        )
        try:
            payload = response.json()
        except ValueError as exc:
            raise RuntimeError(f"speech_direct_llm_non_json:{response.status_code}") from exc
        if response.status_code >= 400:
            raise RuntimeError(f"speech_direct_llm_http_{response.status_code}:{_debug_preview_text(payload, 160)}")
        choice = (payload.get("choices") or [{}])[0]
        message = choice.get("message") if isinstance(choice, Mapping) else {}
        text = ""
        if isinstance(message, Mapping):
            text = _first_present_text(message, "content", "text", "message")
        if not text and isinstance(choice, Mapping):
            text = _first_present_text(choice, "text", "content")
        text = _extract_sidecar_speech_text(text)
        return _sanitize_direct_speech_text(text, max_chars=72)

    def _speech_watchdog_ai_busy(self) -> bool:
        try:
            status = self.adapter.runtime_status()
        except Exception:
            status = {}
        current_state = str(status.get("current_state") or self._last_ai_state or "").strip().lower()
        state_age_sec = _safe_float(status.get("current_state_age_sec"), 0.0)
        busy_grace_sec = self._speech_watchdog_busy_grace_sec
        if bool(status.get("subtitle_pending")) and (state_age_sec <= 0.0 or state_age_sec < busy_grace_sec):
            return True
        if current_state in {"thinking", "speaking", "interrupted"}:
            if state_age_sec <= 0.0 or state_age_sec < busy_grace_sec:
                return True
            self.logger.warning(
                "Speech watchdog treats stale busy state as idle state=%s age=%.1fs grace=%.1fs",
                current_state,
                state_age_sec,
                busy_grace_sec,
            )
        now = time.monotonic()
        if self._last_ai_state in {"thinking", "speaking", "interrupted"} and now - self._last_ai_state_at < self._speech_watchdog_delay_sec:
            return True
        return False

    def _mark_companion_vision_success_locked(self, now: float) -> None:
        self._companion_vision_pending = False
        self._companion_vision_pending_id = 0
        self._companion_vision_pending_until = 0.0
        self._companion_vision_pending_has_response = False
        self._companion_vision_pending_text = ""
        self._companion_vision_response_active = False
        self._companion_vision_failure_count = 0
        self._companion_vision_next_allowed_at = 0.0
        self._companion_vision_last_success_at = now
        self._companion_vision_last_skip_reason = ""

    def _cancel_companion_vision_pending_locked(self, reason: str, now: float) -> None:
        self._companion_vision_pending = False
        self._companion_vision_pending_id = 0
        self._companion_vision_pending_until = 0.0
        self._companion_vision_pending_has_response = False
        self._companion_vision_pending_text = ""
        self._companion_vision_response_active = False
        self._companion_vision_next_allowed_at = 0.0
        self._companion_vision_last_success_at = now
        self._companion_vision_last_skip_reason = reason

    def _mark_companion_vision_failure_locked(self, reason: str, now: float) -> None:
        self._companion_vision_pending = False
        self._companion_vision_pending_id = 0
        self._companion_vision_pending_until = 0.0
        self._companion_vision_pending_has_response = False
        self._companion_vision_pending_text = ""
        self._companion_vision_response_active = False
        self._companion_vision_failure_count += 1
        base_backoff = max(0.0, float(self._companion_vision_config.get("failure_backoff_sec", 0.0)))
        max_backoff = max(base_backoff, float(self._companion_vision_config.get("max_failure_backoff_sec", base_backoff)))
        circuit_count = int(self._companion_vision_config.get("failure_circuit_break_count", 0) or 0)
        circuit_sec = max(0.0, float(self._companion_vision_config.get("failure_circuit_break_sec", 0.0)))
        circuit_break_allowed = reason not in {"empty_response", "empty_subtitle"}
        if (
            circuit_break_allowed
            and circuit_count > 0
            and circuit_sec > 0.0
            and self._companion_vision_failure_count >= circuit_count
        ):
            backoff = circuit_sec
            reason = f"{reason}_circuit_break"
        elif reason in {"pending_timeout", "empty_response", "empty_subtitle"}:
            interval_sec = max(1.0, float(self._companion_vision_config.get("interval_sec", 5.0)))
            backoff = min(max_backoff, max(base_backoff, interval_sec))
        else:
            backoff = min(max_backoff, base_backoff * (2 ** max(0, self._companion_vision_failure_count - 1))) if base_backoff > 0.0 else 0.0
        self._companion_vision_next_allowed_at = now + backoff
        self._companion_vision_last_skip_reason = reason
        self.logger.warning(
            "companion vision prompt failed reason=%s failure_count=%s backoff_sec=%.1f",
            reason,
            self._companion_vision_failure_count,
            backoff,
        )

    def _write_vision_record(self, event_type: str, payload: Any) -> None:
        now = time.time()
        self.adapter.sender.write_event_record(
            trace_id=_new_trace_id("vision"),
            event_type=event_type,
            source="local:screen_vision",
            raw_payload=payload,
            mapping_result=self.vision_status(),
            send_to_godot=False,
            event_received_at=now,
            pose_generated_at=now,
        )

    def _route_payload(self, event_type: str, payload: Any, trace_id: str) -> bool:
        lowered = event_type.lower()
        try:
            if lowered in {"remote_audio_properties_report", "onremoteaudiopropertiesreport"}:
                self.callbacks.on_remote_audio_properties_report(payload)
                return True
            if lowered in {"client_log", "web_client_log"}:
                self.adapter.sender.write_event_record(
                    trace_id=trace_id,
                    event_type="client_log",
                    source="web:client_log",
                    raw_payload=payload,
                    mapping_result={"client_log": True},
                    send_to_godot=False,
                    event_received_at=time.time(),
                    pose_generated_at=time.time(),
                )
                return True
            if lowered in {"subtitle_message_received", "onsubtitlemessagereceived", "subtitle_event"}:
                payload = self._filter_omnivoice_self_echo_subtitles(payload, trace_id=trace_id)
                if payload is None:
                    return True
                payload = self._filter_recent_ai_self_echo_subtitles(payload, trace_id=trace_id)
                if payload is None:
                    return True
                self._note_memory_subtitle_payload(payload)
                self._note_companion_subtitle_payload(payload)
                self._note_voice_priority_subtitle_payload(payload)
                self._note_speech_watchdog_subtitle_payload(payload)
                self.callbacks.on_subtitle_messages(payload)
                return True
            if lowered in {"function_call_event", "tool_call_event", "function_call", "tool_call"}:
                routed = False
                for call in _iter_function_call_candidates(payload):
                    routed_call = self._attach_pending_tool_call_meta(call)
                    self._handle_function_call(routed_call)
                    routed = True
                if not routed and isinstance(payload, Mapping):
                    routed_payload = self._attach_pending_tool_call_meta(payload)
                    self._handle_function_call(routed_payload)
                    routed = True
                return routed
            if lowered in {"ai_state_event", "conversation_state", "task_state"}:
                if isinstance(payload, Mapping) and self._drop_recent_ai_echo_state_event(payload, trace_id):
                    return True
                self.callbacks.on_ai_state(payload)
                self._note_ai_state_for_companion(_bridge_ai_state_from_event(payload))
                self._maybe_emit_welcome_subtitle(payload, trace_id)
                return True
            if lowered in {"room_message", "user_message", "onroommessagereceived", "onusermessagereceived"}:
                return self._route_room_text(payload, trace_id)
            if lowered in {"room_binary_message", "user_binary_message", "onroombinarymessagereceived", "onuserbinarymessagereceived"}:
                return self._route_room_binary(payload, trace_id)

            self._write_unhandled(event_type, payload, trace_id)
            return False
        except Exception:
            self.logger.exception("web_event_route_failed event_type=%s trace_id=%s", event_type, trace_id)
            self._write_unhandled(event_type, payload, trace_id, error="route_exception")
            return False

    def _route_room_binary(self, payload: Any, trace_id: str) -> bool:
        data = payload
        sender_uid = ""
        tlv_type = ""
        if isinstance(payload, Mapping):
            sender_uid = _first_present_text(payload, "userId", "user_id", "uid")
            tlv_type = _first_present_text(payload, "tlvType", "tlv_type", "type")
            # The WebView runtime includes both the raw TLV text and a parsed
            # JSON helper. In real Volc subtitle/tool messages the helper can
            # arrive mojibaked, while the raw text still contains valid JSON
            # with escaped Unicode. Treat the TLV text as canonical.
            raw_text = _first_present_text(payload, "text", "message", "content")
            if raw_text:
                data = _json_or_text(raw_text)
            else:
                data = payload.get("json")
        else:
            data = _json_or_text(payload)

        self._write_decoded_room_binary(payload, data, trace_id, sender_uid=sender_uid, tlv_type=tlv_type)
        routed = self._route_decoded_room_payload(data, sender_uid=sender_uid, trace_id=trace_id)
        if not routed:
            self._write_unhandled("room_binary_message", payload, trace_id)
        return routed

    def _write_decoded_room_binary(
        self,
        payload: Any,
        decoded_payload: Any,
        trace_id: str,
        *,
        sender_uid: str,
        tlv_type: str,
    ) -> None:
        self.adapter.sender.write_event_record(
            trace_id=trace_id,
            event_type="room_binary_message_raw",
            source="web:room_binary_message",
            raw_payload={
                "sender_uid": sender_uid,
                "tlv_type": tlv_type,
                "payload": payload,
                "decoded": decoded_payload,
            },
            mapping_result={"decoded": True, "tlv_type": tlv_type, "sender_uid": sender_uid},
            send_to_godot=False,
            event_received_at=time.time(),
            pose_generated_at=time.time(),
        )

    def _route_room_text(self, payload: Any, trace_id: str) -> bool:
        sender_uid = ""
        data = payload
        if isinstance(payload, Mapping):
            sender_uid = _first_present_text(payload, "userId", "user_id", "uid")
            data = _json_or_text(_first_present_text(payload, "message", "text", "content") or payload.get("data"))
        else:
            data = _json_or_text(payload)
        routed = self._route_decoded_room_payload(data, sender_uid=sender_uid, trace_id=trace_id)
        if not routed:
            self._write_unhandled("room_message", payload, trace_id)
        return routed

    def _route_decoded_room_payload(self, data: Any, *, sender_uid: str = "", trace_id: str = "") -> bool:
        if isinstance(data, list):
            return any(self._route_decoded_room_payload(item, sender_uid=sender_uid, trace_id=trace_id) for item in data)
        if isinstance(data, str):
            state = data.strip()
            if state:
                normalized = state.lower().replace("-", "_")
                if normalized in {"user_speaking", "user_listening", "speech_start", "ai_processing", "ai_thinking", "llm_running", "ai_speaking", "tts_playing", "response_audio_start", "ai_idle", "response_finished", "tts_finished", "interrupted", "user_barge_in", "listening", "thinking", "speaking", "idle"}:
                    self.callbacks.on_ai_state(state)
                    self._note_ai_state_for_companion(state)
                    return True
                if sender_uid and sender_uid == self.bot_uid:
                    self._record_memory_subtitle(
                        role="assistant",
                        text=state,
                        is_final=True,
                        source="room_message",
                        metadata={"trace_id": trace_id, "sender_uid": sender_uid},
                    )
                    self._note_companion_ai_text(state)
                    self.callbacks.on_subtitle(
                        {
                            "trace_id": trace_id,
                            "speaker": "ai",
                            "uid": sender_uid,
                            "text": state,
                            "is_final": True,
                            "raw": {"message": state, "userId": sender_uid},
                        }
                    )
                    return True
            return False
        if not isinstance(data, Mapping):
            return False

        routed = False
        if _looks_like_function_call_meta(data):
            self._remember_tool_call_meta(data)
            routed = True
        for call in _iter_function_call_candidates(data):
            routed_call = self._attach_pending_tool_call_meta(call)
            self._handle_function_call(routed_call)
            routed = True
        if routed:
            return True

        if _looks_like_subtitle(data):
            filtered_data = self._filter_omnivoice_self_echo_subtitles(data, trace_id=trace_id, sender_uid=sender_uid)
            if filtered_data is None:
                return True
            filtered_data = self._filter_recent_ai_self_echo_subtitles(filtered_data, trace_id=trace_id, sender_uid=sender_uid)
            if filtered_data is None:
                return True
            data = filtered_data
            self._note_memory_subtitle_payload(data, sender_uid=sender_uid)
            self._note_companion_subtitle_payload(data, sender_uid=sender_uid)
            self._note_voice_priority_subtitle_payload(data, sender_uid=sender_uid)
            self._note_speech_watchdog_subtitle_payload(data, sender_uid=sender_uid)
            callback_data = self._subtitle_payload_with_sender(data, sender_uid=sender_uid)
            if isinstance(callback_data.get("data"), list):
                self.callbacks.on_subtitle_messages(callback_data.get("data"))
            else:
                self.callbacks.on_subtitle(callback_data)
            return True

        if sender_uid and sender_uid == self.bot_uid:
            text = _first_present_text(data, "message", "text", "content", "subtitle", "transcript", "utterance")
            if text:
                self._record_memory_subtitle(
                    role="assistant",
                    text=text,
                    is_final=True,
                    source="room_message",
                    metadata={"trace_id": trace_id, "sender_uid": sender_uid},
                )
                self._note_companion_ai_text(text)
                subtitle = dict(data)
                subtitle.setdefault("trace_id", trace_id)
                subtitle.setdefault("speaker", "ai")
                subtitle.setdefault("uid", sender_uid)
                subtitle.setdefault("text", text)
                subtitle.setdefault("is_final", True)
                self.callbacks.on_subtitle(subtitle)
                return True

        if _looks_like_ai_state(data):
            if self._drop_recent_ai_echo_state_event(data, trace_id):
                return True
            self.callbacks.on_ai_state(data)
            self._note_ai_state_for_companion(_bridge_ai_state_from_event(data))
            self._maybe_emit_welcome_subtitle(data, trace_id)
            return True

        return False

    def _subtitle_payload_with_sender(self, data: Mapping[str, Any], *, sender_uid: str = "") -> Mapping[str, Any]:
        if not sender_uid or sender_uid != self.bot_uid:
            return data

        def mark_ai_if_unattributed(item: Mapping[str, Any]) -> dict[str, Any]:
            marked = dict(item)
            uid = _first_present_text(marked, "userId", "userID", "user_id", "uid")
            if uid:
                return marked
            speaker = _first_present_text(marked, "speaker", "role", "user_type", "source").strip().lower()
            if "user" in speaker and not any(token in speaker for token in ("ai", "bot", "assistant")):
                return marked
            marked.setdefault("userId", sender_uid)
            marked.setdefault("uid", sender_uid)
            marked.setdefault("speaker", "ai")
            marked.setdefault("source", "ai")
            return marked

        if isinstance(data.get("data"), list):
            marked_data = dict(data)
            marked_data["data"] = [
                mark_ai_if_unattributed(item) if isinstance(item, Mapping) else item
                for item in data.get("data") or []
            ]
            return marked_data

        return mark_ai_if_unattributed(data)

    def _note_memory_subtitle_payload(self, data: Any, *, sender_uid: str = "") -> None:
        if self._memory_service is None:
            return
        candidates: list[Any]
        if isinstance(data, Mapping) and isinstance(data.get("data"), list):
            candidates = list(data.get("data") or [])
        else:
            candidates = [data]
        for item in candidates:
            if not isinstance(item, Mapping):
                continue
            text = _first_present_text(item, "text", "message", "content", "subtitle", "transcript", "utterance")
            if not text:
                continue
            uid = _first_present_text(item, "userId", "userID", "user_id", "uid")
            speaker = _first_present_text(item, "speaker", "role", "user_type", "source").strip().lower()
            has_final_key = any(key in item for key in ("definite", "is_final", "final", "completed"))
            is_final = bool(item.get("definite", item.get("is_final", item.get("final", item.get("completed", False)))))
            sender_bot_without_uid = bool(sender_uid and sender_uid == self.bot_uid and not uid)
            is_ai = (
                (uid and uid == self.bot_uid)
                or sender_bot_without_uid
                or ("ai" in speaker or "bot" in speaker or "assistant" in speaker)
            )
            is_user = (
                (uid and uid != self.bot_uid)
                or ("user" in speaker and not any(token in speaker for token in ("ai", "bot", "assistant")))
            )
            if is_ai and not has_final_key:
                is_final = True
            role = "assistant" if is_ai else ("user" if is_user else "")
            if not role:
                continue
            self._record_memory_subtitle(
                role=role,
                text=text,
                is_final=is_final,
                source="rtc_subtitle",
                metadata={"uid": uid, "speaker": speaker, "sender_uid": sender_uid},
            )

    def _record_memory_subtitle(
        self,
        *,
        role: str,
        text: str,
        is_final: bool,
        source: str,
        metadata: Mapping[str, Any] | None = None,
    ) -> None:
        if self._memory_service is None:
            return
        try:
            self._memory_service.record_subtitle(
                role=role,
                text=text,
                is_final=is_final,
                source=source,
                metadata=metadata,
            )
        except Exception:
            self.logger.exception("pet_memory_record_subtitle_failed role=%s source=%s", role, source)

    def _note_companion_subtitle_payload(self, data: Any, *, sender_uid: str = "") -> None:
        candidates: list[Any]
        if isinstance(data, Mapping) and isinstance(data.get("data"), list):
            candidates = list(data.get("data") or [])
        else:
            candidates = [data]
        should_force_idle = False
        for item in candidates:
            if not isinstance(item, Mapping):
                continue
            uid = _first_present_text(item, "userId", "userID", "user_id", "uid")
            speaker = _first_present_text(item, "speaker", "role", "user_type", "source").strip().lower()
            is_final = bool(
                item.get("is_final")
                or item.get("final")
                or item.get("completed")
                or item.get("definite")
                or item.get("paragraph")
            )
            sender_bot_without_uid = bool(sender_uid and sender_uid == self.bot_uid and not uid)
            is_ai = (
                (uid and uid == self.bot_uid)
                or (not uid and ("ai" in speaker or "bot" in speaker or "assistant" in speaker))
                or (sender_bot_without_uid and "user" not in speaker)
            )
            if not is_ai:
                continue
            text = _first_present_text(item, "text", "message", "content", "subtitle", "transcript", "utterance")
            if not text:
                if is_final:
                    now = time.monotonic()
                    with self._companion_vision_lock:
                        if self._companion_vision_pending and not self._companion_vision_pending_has_response:
                            self._companion_vision_last_skip_reason = "empty_subtitle_wait"
                continue
            self._note_companion_ai_text(text, remember=is_final)
        if should_force_idle:
            self._force_idle_presentation("companion_empty_subtitle")

    def _note_voice_priority_subtitle_payload(self, data: Any, *, sender_uid: str = "") -> None:
        candidates: list[Any]
        if isinstance(data, Mapping) and isinstance(data.get("data"), list):
            candidates = list(data.get("data") or [])
        else:
            candidates = [data]
        now = time.monotonic()
        for item in candidates:
            if not isinstance(item, Mapping):
                continue
            text = _first_present_text(item, "text", "message", "content", "subtitle", "transcript", "utterance")
            if not text:
                continue
            uid = _first_present_text(item, "userId", "userID", "user_id", "uid")
            speaker = _first_present_text(item, "speaker", "role", "user_type", "source").strip().lower()
            is_final = bool(item.get("definite", item.get("is_final", item.get("final", item.get("completed", False)))))
            sender_bot_without_uid = bool(sender_uid and sender_uid == self.bot_uid and not uid)
            is_ai = (
                (uid and uid == self.bot_uid)
                or (not uid and ("ai" in speaker or "bot" in speaker or "assistant" in speaker))
                or (sender_bot_without_uid and "user" not in speaker)
            )
            is_user = (
                (uid and uid != self.bot_uid)
                or (not uid and "user" in speaker and not any(token in speaker for token in ("ai", "bot", "assistant")))
            )
            with self._companion_vision_lock:
                if is_user:
                    if self._should_suppress_user_priority_echo_locked(text, now, is_final=is_final):
                        self._voice_priority_last_reason = "ai_echo_guard"
                        continue
                    self._mark_user_voice_priority_locked(
                        now,
                        "user_subtitle_final" if is_final else "user_subtitle",
                        expect_answer=is_final,
                    )
                elif is_ai and self._voice_priority_waiting_for_answer:
                    if not self._companion_vision_pending and not self._companion_vision_response_active:
                        self._mark_user_answer_priority_locked(now, "user_answer_subtitle")

    def _should_suppress_user_priority_echo_locked(self, text: str, now: float, *, is_final: bool) -> bool:
        if _has_barge_in_intent_text(text):
            return False
        try:
            status = self.adapter.runtime_status()
        except Exception:
            status = {}
        current_state = _normalize_bridge_ai_state(str(status.get("current_state") or self._last_ai_state or ""))
        audio_active = bool(status.get("audio_active"))
        state_age_sec = _safe_float(status.get("current_state_age_sec"), 0.0)
        recent_ai_state = (
            self._last_ai_state in {"speaking", "thinking"}
            and self._last_ai_state_at > 0.0
            and now - self._last_ai_state_at <= 2.8
        )
        cloud_ai_busy = current_state in {"speaking", "thinking"} and (state_age_sec <= 0.0 or state_age_sec <= 30.0)
        if not (audio_active or recent_ai_state or cloud_ai_busy):
            return False
        if self._is_recent_ai_echo_text(text):
            return True
        with self._omnivoice_queue_lock:
            self._prune_recent_ai_echo_texts_locked(now)
            playback_active = now <= self._active_ai_playback_until
        normalized = _normalize_for_echo_compare(text)
        if playback_active and not is_final and normalized:
            return True
        return playback_active and 0 < len(normalized) <= 18

    def _note_speech_watchdog_subtitle_payload(self, data: Any, *, sender_uid: str = "") -> None:
        candidates: list[Any]
        if isinstance(data, Mapping) and isinstance(data.get("data"), list):
            candidates = list(data.get("data") or [])
        else:
            candidates = [data]
        for item in candidates:
            if not isinstance(item, Mapping):
                continue
            text = _first_present_text(item, "text", "message", "content", "subtitle", "transcript", "utterance")
            if not text:
                continue
            uid = _first_present_text(item, "userId", "userID", "user_id", "uid")
            speaker = _first_present_text(item, "speaker", "role", "user_type", "source").strip().lower()
            is_final = bool(item.get("definite", item.get("is_final", item.get("final", item.get("completed", False)))))
            sender_bot_without_uid = bool(sender_uid and sender_uid == self.bot_uid and not uid)
            is_ai = (
                (uid and uid == self.bot_uid)
                or (not uid and ("ai" in speaker or "bot" in speaker or "assistant" in speaker))
                or (sender_bot_without_uid and "user" not in speaker)
            )
            if is_ai:
                with self._companion_vision_lock:
                    from_companion_prompt = self._companion_vision_pending or self._companion_vision_response_active
                if from_companion_prompt:
                    continue
                self._note_speech_watchdog_ai_response()
                continue
            is_user = (
                (uid and uid != self.bot_uid)
                or (not uid and "user" in speaker and not any(token in speaker for token in ("ai", "bot", "assistant")))
            )
            if is_user and is_final:
                self._note_speech_watchdog_user_final(text)

    def _maybe_emit_welcome_subtitle(self, payload: Any, trace_id: str) -> None:
        if self._welcome_subtitle_sent or not isinstance(payload, Mapping):
            return
        if _extract_stage_code(payload) != 3:
            return
        round_id = payload.get("RoundID", payload.get("roundID", payload.get("round_id", 0)))
        try:
            if int(round_id) != 0:
                return
        except (TypeError, ValueError):
            return
        agent_config = self.request.get("AgentConfig", {})
        welcome = ""
        if isinstance(agent_config, Mapping):
            welcome = str(agent_config.get("WelcomeMessage") or "").strip()
        if not welcome:
            return
        self._welcome_subtitle_sent = True
        self._welcome_subtitle_ever_sent = True
        self.callbacks.on_subtitle(
            {
                "trace_id": f"{trace_id}-welcome",
                "speaker": "ai",
                "uid": self.bot_uid,
                "text": welcome,
                "is_final": True,
                "raw": {
                    "source": "local_welcome_message_fallback",
                    "state_event": payload,
                },
            }
        )

    def _handle_function_call(self, call: Mapping[str, Any]) -> None:
        name = _extract_tool_function_name(call)
        if name == "web_search":
            self._queue_web_search_tool_result(call)
            return
        command = self.callbacks.on_function_call(call)
        self._queue_update_voice_chat_tool_result(call, command is not None)

    def _write_unhandled(self, event_type: str, payload: Any, trace_id: str, error: str = "unhandled") -> None:
        self.adapter.sender.write_event_record(
            trace_id=trace_id,
            event_type=event_type,
            source=f"web:{event_type}",
            raw_payload=payload,
            mapping_result={"ignored": True, "reason": error},
            send_to_godot=False,
            event_received_at=time.time(),
            pose_generated_at=time.time(),
        )

    def _remember_tool_call_meta(self, payload: Mapping[str, Any]) -> None:
        tool_call_id = _first_present_text(payload, "tool_call_id", "toolCallId", "ToolCallID", "id")
        function_name = _extract_tool_function_name(payload)
        response_id = _first_present_text(payload, "response_id", "responseId", "ResponseId")
        if not tool_call_id or function_name not in _SUPPORTED_TOOL_NAMES:
            return
        now = time.time()
        meta = {
            "tool_call_id": tool_call_id,
            "response_id": response_id,
            "name": function_name,
            "created_at": now,
        }
        self._pending_tool_call_meta = [
            item for item in self._pending_tool_call_meta if now - float(item.get("created_at", 0.0)) < 30.0
        ]
        self._pending_tool_call_meta.append(meta)
        self._pending_tool_call_meta = self._pending_tool_call_meta[-12:]
        self.adapter.sender.write_event_record(
            trace_id=_new_trace_id("tool-meta"),
            event_type="function_call_meta",
            source="function_call:meta",
            raw_payload=payload,
            mapping_result={"stored": True, "tool_call_id": tool_call_id, "response_id": response_id},
            send_to_godot=False,
            event_received_at=now,
            pose_generated_at=now,
        )

    def _attach_pending_tool_call_meta(self, call: Mapping[str, Any]) -> dict[str, Any]:
        merged = dict(call)
        if _first_present_text(merged, "tool_call_id", "toolCallId", "ToolCallID"):
            return merged
        name = _extract_tool_function_name(merged)
        now = time.time()
        self._pending_tool_call_meta = [
            item for item in self._pending_tool_call_meta if now - float(item.get("created_at", 0.0)) < 30.0
        ]
        for index in range(len(self._pending_tool_call_meta) - 1, -1, -1):
            meta = self._pending_tool_call_meta[index]
            if meta.get("name") == name:
                merged.setdefault("tool_call_id", meta.get("tool_call_id"))
                merged.setdefault("response_id", meta.get("response_id"))
                self._pending_tool_call_meta.pop(index)
                break
        return merged

    def _queue_update_voice_chat_tool_result(self, call: Mapping[str, Any], accepted: bool) -> None:
        tool_call_id = _first_present_text(call, "tool_call_id", "toolCallId", "ToolCallID", "id")
        if not tool_call_id:
            self.logger.warning("Skip UpdateVoiceChat tool result: missing tool_call_id call_keys=%s", sorted(call.keys()))
            return
        response_id = _first_present_text(call, "response_id", "responseId", "ResponseId")
        content = (
            "ok=true; pose command dispatched to the local desktop pet. Continue the voice reply naturally."
            if accepted
            else "ok=false; pose command was rejected locally. Continue the voice reply without pose control."
        )
        request = build_update_voice_chat_function_result_request(
            self._active_request,
            tool_call_id=tool_call_id,
            response_id=response_id,
            content=content,
        )
        Thread(
            target=self._send_update_voice_chat_tool_result,
            args=(request, tool_call_id, response_id, accepted),
            name=f"volc-tool-result-{tool_call_id[:12]}",
            daemon=True,
        ).start()

    def _queue_web_search_tool_result(self, call: Mapping[str, Any]) -> None:
        tool_call_id = _first_present_text(call, "tool_call_id", "toolCallId", "ToolCallID", "id")
        if not tool_call_id:
            self.logger.warning("Skip WebSearch tool result: missing tool_call_id call_keys=%s", sorted(call.keys()))
            return
        response_id = _first_present_text(call, "response_id", "responseId", "ResponseId")
        arguments = _extract_tool_arguments(call)
        query = str(arguments.get("query") or "").strip() if isinstance(arguments, Mapping) else ""
        original_query = query
        recent_user_text = str(getattr(self.adapter, "_recent_user_subtitle", "") or "")
        if not _should_allow_web_search(recent_user_text, query):
            self.logger.info(
                "WebSearch skipped: no allowed factual/search intent recent_user=%s query=%s",
                recent_user_text,
                query,
            )
            request = build_update_voice_chat_function_result_request(
                self._active_request,
                tool_call_id=tool_call_id,
                response_id=response_id,
                content=(
                    "ok=false; web_search was not executed by local policy. "
                    "If the answer depends on current, online, version, event, strategy, or uncertain facts, "
                    "say you are not sure and need online search; do not guess or invent names."
                ),
            )
            self.adapter.sender.write_event_record(
                trace_id=_new_trace_id("websearch-skip"),
                event_type="web_search_tool_result",
                source="function_call:web_search",
                raw_payload={"call": call, "query": query, "recent_user_text": recent_user_text},
                mapping_result={"accepted": False, "reason": "no_allowed_factual_or_search_intent"},
                send_to_godot=False,
                event_received_at=time.time(),
                pose_generated_at=time.time(),
            )
            Thread(
                target=self._send_update_voice_chat_tool_result,
                args=(request, tool_call_id, response_id, False),
                name=f"volc-websearch-skip-{tool_call_id[:12]}",
                daemon=True,
            ).start()
            return
        query = _sanitize_web_search_query(query, recent_user_text)
        if query != original_query:
            self.logger.info(
                "WebSearch query sanitized original=%s recent_user=%s sanitized=%s",
                original_query,
                recent_user_text,
                query,
            )
        Thread(
            target=self._send_web_search_tool_result,
            args=(call, tool_call_id, response_id, query),
            name=f"volc-websearch-{tool_call_id[:12]}",
            daemon=True,
        ).start()

    def _send_web_search_tool_result(
        self,
        call: Mapping[str, Any],
        tool_call_id: str,
        response_id: str,
        query: str,
    ) -> None:
        now = time.time()
        if self._websearch_client is None:
            content = "ok=false; web_search is not configured. Continue without online search."
            accepted = False
            result_payload: Any = {"error": "websearch_not_configured"}
        elif not query:
            content = "ok=false; web_search query is empty. Continue without online search."
            accepted = False
            result_payload = {"error": "empty_query"}
        else:
            try:
                if self._websearch_stream_tool_result_enabled():
                    accepted, result_payload, content = self._send_streaming_web_search_tool_result(
                        call=call,
                        tool_call_id=tool_call_id,
                        response_id=response_id,
                        query=query,
                        event_received_at=now,
                    )
                    return
                else:
                    result_payload = self._websearch_client.search(query)
                    answer_context = format_search_answer_context(result_payload)
                    content = "ok=true; 联网搜索结果：\n" + answer_context
                    accepted = True
            except Exception as exc:
                self.logger.exception("Volc WebSearch failed tool_call_id=%s query=%s", tool_call_id, query)
                content = f"ok=false; web_search failed: {exc}. Continue without online search."
                accepted = False
                result_payload = {"error": str(exc)}

        request = build_update_voice_chat_function_result_request(
            self._active_request,
            tool_call_id=tool_call_id,
            response_id=response_id,
            content=content,
        )
        self.adapter.sender.write_event_record(
            trace_id=_new_trace_id("websearch"),
            event_type="web_search_tool_result",
            source="function_call:web_search",
            raw_payload={"call": call, "query": query},
            mapping_result={"accepted": accepted, "result": result_payload},
            send_to_godot=False,
            event_received_at=now,
            pose_generated_at=time.time(),
        )
        self._send_update_voice_chat_tool_result(request, tool_call_id, response_id, accepted)

    def _send_streaming_web_search_tool_result(
        self,
        *,
        call: Mapping[str, Any],
        tool_call_id: str,
        response_id: str,
        query: str,
        event_received_at: float,
    ) -> tuple[bool, Any, str]:
        if self._websearch_client is None:
            return False, {"error": "websearch_not_configured"}, "ok=false; web_search is not configured."

        settings = self._websearch_settings()
        interval_sec = max(0.2, float(settings.get("StreamPartialIntervalMs", 450)) / 1000.0)
        min_chars = max(8, int(settings.get("StreamMinChars", 24)))
        max_updates = max(1, int(settings.get("StreamMaxUpdates", 6)))
        chunks: list[dict[str, Any]] = []
        sent_chars = 0
        sent_updates = 0
        last_send_at = 0.0
        final_payload: Any = {"error": "no_payload"}

        for payload in self._websearch_client.search_stream(query):
            chunks.append(payload)
            final_payload = payload
            summary_text = _extract_summary_text_from_websearch_chunks(chunks)
            now = time.time()
            if (
                summary_text
                and len(summary_text) - sent_chars >= min_chars
                and now - last_send_at >= interval_sec
                and sent_updates < max_updates
            ):
                partial_text = summary_text[: min(len(summary_text), sent_chars + 260)]
                sent_chars = len(partial_text)
                sent_updates += 1
                last_send_at = now
                content = (
                    "ok=true; partial=true; 以下是联网搜索总结的增量内容。"
                    "可以先用它简短回答，后续若有 final=true 再补充："
                    + partial_text
                )
                self._send_web_search_update(
                    call=call,
                    tool_call_id=tool_call_id,
                    response_id=response_id,
                    query=query,
                    content=content,
                    accepted=True,
                    result_payload={"partial": True, "update_index": sent_updates, "text": partial_text},
                    event_received_at=event_received_at,
                )

        aggregated = _aggregate_websearch_chunks(chunks) if chunks else final_payload
        final_text = _extract_summary_text_from_websearch_result(aggregated)
        content = (
            "ok=true; final=true; 联网搜索已完成。请基于以下最终总结自然回答用户，不要念出 JSON："
            + (final_text or compact_search_result(aggregated))
        )
        self._send_web_search_update(
            call=call,
            tool_call_id=tool_call_id,
            response_id=response_id,
            query=query,
            content=content,
            accepted=True,
            result_payload=aggregated,
            event_received_at=event_received_at,
            final=True,
        )
        return True, aggregated, content

    def _send_web_search_update(
        self,
        *,
        call: Mapping[str, Any],
        tool_call_id: str,
        response_id: str,
        query: str,
        content: str,
        accepted: bool,
        result_payload: Any,
        event_received_at: float,
        final: bool = False,
    ) -> None:
        request = build_update_voice_chat_function_result_request(
            self._active_request,
            tool_call_id=tool_call_id,
            response_id=response_id,
            content=content,
        )
        self.adapter.sender.write_event_record(
            trace_id=_new_trace_id("websearch"),
            event_type="web_search_tool_result",
            source="function_call:web_search",
            raw_payload={"call": call, "query": query, "final": final},
            mapping_result={"accepted": accepted, "streaming": True, "final": final, "result": result_payload},
            send_to_godot=False,
            event_received_at=event_received_at,
            pose_generated_at=time.time(),
        )
        self._send_update_voice_chat_tool_result(request, tool_call_id, response_id, accepted)

    def _websearch_settings(self) -> Mapping[str, Any]:
        settings = self.config.get("WebSearchOpenAPI") or {}
        return settings if isinstance(settings, Mapping) else {}

    def _websearch_stream_tool_result_enabled(self) -> bool:
        settings = self._websearch_settings()
        return bool(settings.get("StreamToolResult", False))

    def _send_update_voice_chat_tool_result(
        self,
        request: Mapping[str, Any],
        tool_call_id: str,
        response_id: str,
        accepted: bool,
    ) -> None:
        try:
            response = self._openapi_client.update_voice_chat(request)
            self.logger.info(
                "UpdateVoiceChat function result accepted tool_call_id=%s response_id=%s accepted=%s response_keys=%s",
                tool_call_id,
                response_id,
                accepted,
                sorted(response.keys()),
            )
        except Exception:
            self.logger.exception(
                "UpdateVoiceChat function result failed tool_call_id=%s response_id=%s accepted=%s",
                tool_call_id,
                response_id,
                accepted,
            )

    def _on_pose_send(self, item: Any, ok: bool) -> None:
        now = time.time()
        if item.source == "remote_audio_volume:ai":
            if now - self._last_audio_pose_log_at < 1.0:
                return
            self._last_audio_pose_log_at = now
        record = {
            "trace_id": item.trace_id,
            "source": item.source,
            "sent": ok,
            "event_to_pose_ms": _elapsed_ms(item.event_received_at, item.pose_generated_at),
            "pose_to_send_ms": _elapsed_ms(item.pose_generated_at, now),
            "event_to_send_ms": _elapsed_ms(item.event_received_at, now),
        }
        self._sent_records.append(record)
        self.logger.info("pose_send %s", json.dumps(record, ensure_ascii=False))

    def _on_user_vision_intent(self, text: str) -> None:
        normalized = " ".join(str(text or "").split())
        now = time.monotonic()
        if normalized == self._last_vision_intent_text and now - self._last_vision_intent_at < 5.0:
            self.logger.info("screen vision intent ignored by cooldown: %s", text)
            return
        if not self._voice_active:
            self.logger.info("screen vision intent ignored; voice chat is inactive: %s", text)
            return
        status = self.vision_status()
        self._last_vision_intent_at = now
        self._last_vision_intent_text = normalized
        self.logger.info("screen vision intent from user subtitle: %s", text)
        sidecar = status.get("sidecar") if isinstance(status.get("sidecar"), Mapping) else {}
        already_active = bool(status.get("desired")) and (
            bool(status.get("screenPublished")) or bool(sidecar.get("running")) or bool(sidecar.get("inFlight"))
        )
        if not already_active:
            self.vision_start()
        answer_result = self._queue_screen_query_context(normalized)
        if answer_result.get("pending"):
            self.logger.info("screen query waits for sidecar vision result: %s", answer_result)
        elif not answer_result.get("queued"):
            if bool(status.get("screenPublished")) and _direct_rtc_vision_supported_in_request(self.request):
                self._write_vision_record(
                    "screen_query_guard_skipped",
                    {
                        "query_text": normalized,
                        "reason": "direct_rtc_screen_published",
                    },
                )
            else:
                self._queue_screen_query_guard(normalized, reason=str(answer_result.get("reason") or "no_current_observation"))
        if self._vision_observer is not None and self._vision_observer_config.enabled:
            Thread(
                target=self._force_vision_tick_for_screen_query,
                args=(normalized,),
                name="screen-query-vision-tick",
                daemon=True,
            ).start()

    def _on_user_speech_activity(self, text: str, is_final: bool = False) -> None:
        self._stop_omnivoice("user_subtitle_final" if is_final else "user_speech")
        now = time.monotonic()
        with self._companion_vision_lock:
            self._mark_user_voice_priority_locked(
                now,
                "user_subtitle_final" if is_final else "user_subtitle",
                expect_answer=is_final,
            )
            if not is_final and self._companion_vision_response_active:
                self._companion_vision_response_active = False

    def _queue_screen_query_context(self, query_text: str) -> dict[str, Any]:
        if self._vision_observer is None or not self._vision_observer_config.enabled:
            return {"ok": False, "queued": False, "reason": "vision_observer_disabled"}
        try:
            return self._vision_observer.answer_user_screen_query(
                query_text,
                trace_id=_new_trace_id("screen-query"),
                ttl_ms=max(3000, self._vision_observer_config.event_ttl_ms),
            )
        except Exception:
            self.logger.exception("screen query context injection failed")
            return {"ok": False, "queued": False, "reason": "context_injection_failed"}

    def _queue_screen_query_guard(self, query_text: str, *, reason: str) -> dict[str, Any]:
        text = (
            f"{SYSTEM_OBSERVATION_PREFIX}"
            "用户正在询问屏幕内容，但当前还没有可用的视觉观察。"
            "请不要猜测屏幕细节，可以简短说明正在确认画面。"
        )
        result = self._queue_external_text(
            text,
            interrupt_mode=1,
            source="vision_observer",
            metadata={
                "role": "system_observation",
                "message_type": "screen_query_guard",
                "trace_id": _new_trace_id("screen-query-guard"),
                "query_text": query_text,
                "reason": reason,
            },
        )
        self.logger.info("screen query guard queued reason=%s result=%s", reason, result)
        return result

    def _force_vision_tick_for_screen_query(self, query_text: str) -> None:
        if self._vision_observer is None:
            return
        try:
            result = self._vision_observer.tick(force=True, ignore_diff=True)
            self.logger.info("screen query forced vision tick text=%s result=%s", query_text, result)
        except Exception:
            self.logger.exception("screen query forced vision tick failed text=%s", query_text)

    def _on_user_voice_stop_intent(self, text: str) -> None:
        self.logger.info("voice stop intent from user subtitle: %s", text)
        Thread(
            target=self._stop_voice_from_user_intent,
            args=(text,),
            name="volc-user-voice-stop",
            daemon=True,
        ).start()

    def _stop_voice_from_user_intent(self, text: str) -> None:
        should_quit_app = _has_app_exit_intent(text)
        try:
            self.vision_stop()
            self.camera_stop()
            result = self.stop_voice_chat()
            self.logger.info("voice stop intent handled text=%s result=%s", text, result)
            if should_quit_app:
                self._request_app_quit_from_user_intent(text)
        except Exception:
            self.logger.exception("voice stop intent failed text=%s", text)
        _stop_volc_voice_runtime_process()

    def _request_app_quit_from_user_intent(self, text: str) -> None:
        payload = {"quit_app": True, "voice_runtime": "stop"}
        ok = False
        error = ""
        try:
            ok = bool(self.adapter.sender.client.send_pose(payload))
        except Exception as exc:
            error = str(exc)
            self.logger.exception("app quit intent send failed text=%s", text)
        self.adapter.sender.write_event_record(
            trace_id=_new_trace_id("app-quit"),
            event_type="app_quit_intent",
            source="subtitle:user",
            raw_payload={"text": text},
            mapping_result={"quit_app": True, "sent": ok, "error": error},
            send_to_godot=ok,
            error=None if ok else (error or "presentation_offline"),
            event_received_at=time.time(),
            pose_generated_at=time.time(),
        )


class BridgeHTTPServer(ThreadingHTTPServer):
    bridge: VolcRtcWebBridge


class BridgeRequestHandler(BaseHTTPRequestHandler):
    server: BridgeHTTPServer

    def do_OPTIONS(self) -> None:
        self._send_empty(HTTPStatus.NO_CONTENT)

    def do_GET(self) -> None:
        path = urlparse(self.path).path
        if path == "/api/config":
            self._send_json({"ok": True, "config": self.server.bridge.client_config()})
            return
        if path == "/api/check_config":
            self._send_json({"ok": True, "issues": self.server.bridge.check_config()})
            return
        if path == "/api/health":
            self._send_json({"ok": True, "time": time.time()})
            return
        if path in {"/api/diagnostics", "/api/voice_diagnostics"}:
            self._send_json(self.server.bridge.voice_diagnostics())
            return
        if path == "/api/vision/status":
            self._send_json(self.server.bridge.vision_status())
            return
        if path == "/api/camera/status":
            self._send_json(self.server.bridge.camera_status())
            return
        if path == "/api/face_tracking/status":
            self._send_json(self.server.bridge.face_tracking_status())
            return
        if path == "/api/companion_vision/status":
            self._send_json(self.server.bridge.companion_vision_status())
            return
        if path == "/api/voice_output/status":
            self._send_json(self.server.bridge.voice_output_status())
            return
        if path == "/api/external_text_to_llm/pending":
            self._send_json(self.server.bridge.take_pending_external_text())
            return
        if path == "/api/external_text_to_llm/results":
            self._send_json(self.server.bridge.external_text_results())
            return
        if path in {"/", "/index.html"}:
            self._send_file(WEB_ROOT / "index.html")
            return
        if path == "/main.js":
            self._send_file(WEB_ROOT / "main.js")
            return
        if path == "/vendor/volcengine-rtc.esm.min.js":
            vendor = VENDOR_RTC_PATH if VENDOR_RTC_PATH.exists() else FALLBACK_VENDOR_RTC_PATH
            self._send_file(vendor)
            return
        if path == "/vendor/mediapipe/tasks-vision.mjs":
            self._send_file(MEDIAPIPE_TASKS_PATH)
            return
        if path == "/vendor/mediapipe/face_landmarker.task":
            self._send_file(MEDIAPIPE_FACE_MODEL_PATH)
            return
        if path.startswith("/vendor/mediapipe/wasm/"):
            wasm_name = Path(path).name
            self._send_file(MEDIAPIPE_WASM_ROOT / wasm_name)
            return
        self._send_json({"ok": False, "error": "not_found"}, HTTPStatus.NOT_FOUND)

    def do_POST(self) -> None:
        path = urlparse(self.path).path
        try:
            if path == "/api/start_voice_chat":
                payload = self._read_json()
                force_restart = bool(payload.get("forceRestart") or payload.get("force_restart"))
                suppress_welcome = bool(payload.get("suppressWelcome") or payload.get("suppress_welcome"))
                self._send_json(
                    self.server.bridge.start_voice_chat(
                        force_restart=force_restart,
                        suppress_welcome=suppress_welcome,
                    )
                )
                return
            if path == "/api/stop_voice_chat":
                self._send_json(self.server.bridge.stop_voice_chat())
                return
            if path == "/api/event":
                self._send_json(self.server.bridge.route_web_event(self._read_json()))
                return
            if path == "/api/vision/start":
                self._send_json(self.server.bridge.vision_start())
                return
            if path == "/api/vision/stop":
                self._send_json(self.server.bridge.vision_stop())
                return
            if path == "/api/vision/settings":
                self._send_json(self.server.bridge.screen_vision_update_settings(self._read_json()))
                return
            if path == "/api/camera/start":
                self._send_json(self.server.bridge.camera_start())
                return
            if path == "/api/camera/stop":
                self._send_json(self.server.bridge.camera_stop(self._read_json()))
                return
            if path == "/api/camera/settings":
                self._send_json(self.server.bridge.camera_video_update_settings(self._read_json()))
                return
            if path == "/api/camera/client_state":
                self._send_json(self.server.bridge.camera_client_state(self._read_json()))
                return
            if path == "/api/face_tracking/packet":
                self._send_json(self.server.bridge.face_tracking_packet(self._read_json()))
                return
            if path == "/api/companion_vision/start":
                self._send_json(self.server.bridge.companion_vision_start(force_enable=True))
                return
            if path == "/api/companion_vision/interval":
                payload = self._read_json()
                interval_sec = payload.get(
                    "interval_sec",
                    payload.get("intervalSec", payload.get("IntervalSec", 10.0)),
                )
                self._send_json(self.server.bridge.companion_vision_set_interval(interval_sec))
                return
            if path == "/api/companion_vision/stop":
                self._send_json(self.server.bridge.companion_vision_stop())
                return
            if path == "/api/vision/client_state":
                self._send_json(self.server.bridge.vision_client_state(self._read_json()))
                return
            if path == "/api/external_text_to_llm":
                self._send_json(self.server.bridge.queue_external_text_to_llm(self._read_json()))
                return
            if path == "/api/external_text_to_llm/result":
                self._send_json(self.server.bridge.record_external_text_result(self._read_json()))
                return
            self._send_json({"ok": False, "error": "not_found"}, HTTPStatus.NOT_FOUND)
        except Exception as exc:
            logging.getLogger("volc_rtc_web").exception("request_failed path=%s", path)
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.INTERNAL_SERVER_ERROR)

    def log_message(self, fmt: str, *args: Any) -> None:
        logging.getLogger("volc_rtc_web.http").debug(fmt, *args)

    def _read_json(self) -> dict[str, Any]:
        length = int(self.headers.get("Content-Length") or "0")
        raw = self.rfile.read(min(length, 2_000_000))
        if not raw:
            return {}
        data = json.loads(raw.decode("utf-8"))
        if not isinstance(data, dict):
            raise ValueError("request JSON body must be an object")
        return data

    def _send_file(self, path: Path) -> None:
        if not path.exists():
            self._send_json(
                {
                    "ok": False,
                    "error": f"file missing: {path}",
                    "hint": "Run `npm install` inside D:\\pet\\tools\\volc_rtc_web if the RTC SDK file is missing.",
                },
                HTTPStatus.NOT_FOUND,
            )
            return
        mime_type = mimetypes.guess_type(str(path))[0] or "application/octet-stream"
        data = path.read_bytes()
        self.send_response(HTTPStatus.OK)
        self._send_common_headers(mime_type)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _send_json(self, payload: Mapping[str, Any], status: HTTPStatus = HTTPStatus.OK) -> None:
        data = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self._send_common_headers("application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _send_empty(self, status: HTTPStatus) -> None:
        self.send_response(status)
        self._send_common_headers("text/plain; charset=utf-8")
        self.end_headers()

    def _send_common_headers(self, content_type: str) -> None:
        self.send_header("Content-Type", content_type)
        self.send_header("Access-Control-Allow-Origin", "http://127.0.0.1:%s" % self.server.server_port)
        self.send_header("Access-Control-Allow-Methods", "GET,POST,OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")
        self.send_header("Cache-Control", "no-store")


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser(description="Run the local Volc Web RTC client bridge.")
    parser.add_argument("--config", default=str(DEFAULT_CONFIG_PATH))
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=17862)
    parser.add_argument("--godot-host", default="")
    parser.add_argument("--godot-port", type=int, default=0)
    parser.add_argument("--presentation-route", default="", help="Presentation route id, for example unity or godot.")
    parser.add_argument("--presentation-backend", default="", help="Presentation backend override: unity or godot.")
    parser.add_argument("--presentation-host", default="")
    parser.add_argument("--presentation-port", type=int, default=0)
    parser.add_argument("--bot-uid", default="")
    parser.add_argument("--raw-log", default=str(DEFAULT_RAW_LOG_PATH))
    parser.add_argument("--open-browser", action="store_true")
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    logger = logging.getLogger("volc_rtc_web")
    config_path = Path(args.config)
    if not config_path.exists():
        logger.error("Missing local config: %s", config_path)
        return 2

    bridge = VolcRtcWebBridge(
        config_path=config_path,
        godot_host=args.godot_host,
        godot_port=args.godot_port,
        presentation_route=args.presentation_route,
        presentation_backend=args.presentation_backend,
        presentation_host=args.presentation_host,
        presentation_port=args.presentation_port,
        bot_uid=args.bot_uid,
        raw_log_path=Path(args.raw_log),
        logger=logger,
    )
    issues = bridge.check_config()
    if issues:
        logger.warning("StartVoiceChat config check found %d issue(s).", len(issues))
        for issue in issues:
            logger.warning("[%s] %s: %s", issue["severity"], issue["key"], issue["message"])
    else:
        logger.info("StartVoiceChat config check passed.")

    server = BridgeHTTPServer((args.host, args.port), BridgeRequestHandler)
    server.bridge = bridge
    url = f"http://{args.host}:{args.port}/"
    logger.info("Volc RTC web bridge listening: %s", url)
    logger.info("Raw event log: %s", args.raw_log)
    if args.open_browser:
        webbrowser.open(url)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        logger.info("Stopping Volc RTC web bridge.")
    finally:
        bridge.close()
        server.server_close()
    return 0


def _load_json_with_env(path: Path) -> dict[str, Any]:
    text = path.read_text(encoding="utf-8")
    text = re.sub(r"\$\{([A-Z0-9_]+)\}", lambda match: os.getenv(match.group(1), ""), text)
    data = json.loads(text)
    if not isinstance(data, dict):
        raise ValueError(f"Config root must be a JSON object: {path}")
    base_ref = data.pop("Extends", data.pop("extends", None))
    if base_ref:
        base_path = _resolve_config_ref(path, str(base_ref))
        data = _deep_merge_config(_load_json_with_env(base_path), data)
    _normalize_start_voice_chat_request_numbers(data)
    return data


def _voice_output_mapping(config: Mapping[str, Any]) -> Mapping[str, Any]:
    for key in ("VoiceOutput", "voiceOutput", "voice_output"):
        value = config.get(key)
        if isinstance(value, Mapping):
            return value
    return {}


def _normalize_voice_output_provider(value: Any) -> str:
    provider = str(value or "volc_rtc").strip().lower().replace("-", "_")
    if provider in {"omnivoice", "omni_voice", "omnivoice_gateway"}:
        return "omnivoice_gateway"
    if provider in {"gpt_sovits", "gpt_sovits_direct", "gptsovits", "gpt_sovits_tts"}:
        return "gpt_sovits_direct"
    return "volc_rtc"


def _token_refresh_config_path(path: Path) -> Path:
    current = path
    seen: set[Path] = set()
    while True:
        resolved = current.resolve()
        if resolved in seen:
            return current
        seen.add(resolved)
        try:
            data = json.loads(current.read_text(encoding="utf-8"))
        except Exception:
            return current
        if not isinstance(data, dict):
            return current
        base_ref = data.get("Extends", data.get("extends"))
        if not base_ref:
            return current
        current = _resolve_config_ref(current, str(base_ref))


def _resolve_config_ref(current_path: Path, ref: str) -> Path:
    if ref.startswith("res://"):
        return ROOT / ref.removeprefix("res://")
    ref_path = Path(ref)
    if ref_path.is_absolute():
        return ref_path
    root_relative = ROOT / ref_path
    if root_relative.exists():
        return root_relative
    return current_path.parent / ref_path


def _deep_merge_config(base: Mapping[str, Any], override: Mapping[str, Any]) -> dict[str, Any]:
    merged: dict[str, Any] = dict(base)
    for key, value in override.items():
        if value is None:
            merged.pop(key, None)
            continue
        if isinstance(value, Mapping) and isinstance(merged.get(key), Mapping):
            merged[key] = _deep_merge_config(merged[key], value)  # type: ignore[arg-type]
        else:
            merged[key] = value
    return merged


def _normalize_start_voice_chat_request_numbers(config: dict[str, Any]) -> None:
    """Godot rewrites JSON numbers as floats; Volc StartVoiceChat rejects int fields like 160.0."""
    int_paths = [
        ("StartVoiceChat", "Config", "ASRConfig", "ProviderParams", "StreamMode"),
        ("StartVoiceChat", "Config", "ASRConfig", "VADConfig", "SilenceTime"),
        ("StartVoiceChat", "Config", "ASRConfig", "TurnDetectionMode"),
        ("StartVoiceChat", "Config", "ASRConfig", "InterruptConfig", "InterruptSpeechDuration"),
        ("StartVoiceChat", "Config", "LLMConfig", "MaxTokens"),
        ("StartVoiceChat", "Config", "LLMConfig", "HistoryLength"),
        ("StartVoiceChat", "Config", "LLMConfig", "VisionConfig", "SnapshotConfig", "StreamType"),
        ("StartVoiceChat", "Config", "LLMConfig", "VisionConfig", "SnapshotConfig", "Height"),
        ("StartVoiceChat", "Config", "LLMConfig", "VisionConfig", "SnapshotConfig", "Interval"),
        ("StartVoiceChat", "Config", "LLMConfig", "VisionConfig", "SnapshotConfig", "ImagesLimit"),
        ("StartVoiceChat", "Config", "LLMConfig", "VisionConfig", "Height"),
        ("StartVoiceChat", "Config", "LLMConfig", "VisionConfig", "Interval"),
        ("StartVoiceChat", "Config", "LLMConfig", "VisionConfig", "ImagesLimit"),
        ("StartVoiceChat", "Config", "SubtitleConfig", "SubtitleMode"),
        ("StartVoiceChat", "Config", "InterruptMode"),
        ("StartVoiceChat", "Config", "MusicAgentConfig", "Mode"),
        ("CompanionVision", "IntervalSec"),
        ("CompanionVision", "PendingTimeoutSec"),
        ("CompanionVision", "LocalVisionSidecarTimeoutSec"),
        ("CompanionVision", "MaxBusyWithoutAudioSec"),
        ("CompanionVision", "InterruptMode"),
    ]
    for path in int_paths:
        _coerce_nested_int(config, path)
    _normalize_openai_compatible_url(config)
    _remove_unsupported_start_voice_chat_fields(config)


def _coerce_nested_int(root: dict[str, Any], path: tuple[str, ...]) -> None:
    node: Any = root
    for key in path[:-1]:
        if not isinstance(node, dict):
            return
        node = node.get(key)
    if not isinstance(node, dict):
        return
    key = path[-1]
    value = node.get(key)
    if isinstance(value, bool):
        return
    if isinstance(value, (int, float)):
        node[key] = int(round(float(value)))


def _normalize_openai_compatible_url(config: dict[str, Any]) -> None:
    llm = config.get("StartVoiceChat", {}).get("Config", {}).get("LLMConfig", {})
    if not isinstance(llm, dict):
        return
    mode = str(llm.get("Mode") or "").lower()
    if mode != "customllm":
        return
    url = str(llm.get("Url") or llm.get("URL") or "").strip().rstrip("/")
    if not url:
        return
    lowered = url.lower()
    if lowered.endswith("/chat/completions"):
        llm["Url"] = url
    elif lowered.endswith("/v1") or lowered.endswith("/compatible-mode/v1"):
        llm["Url"] = f"{url}/chat/completions"
    else:
        parsed = urlparse(url)
        path = parsed.path.rstrip("/")
        if not path or path == "/" or path.lower() == "/beta" or re.fullmatch(r"/v[0-9][A-Za-z0-9_./-]*", path, re.IGNORECASE):
            llm["Url"] = f"{url}/chat/completions"


def _remove_unsupported_start_voice_chat_fields(config: dict[str, Any]) -> None:
    """Keep direct-LLM test fields out of the Volc StartVoiceChat request."""
    voice_config = config.get("StartVoiceChat", {}).get("Config", {})
    if not isinstance(voice_config, dict):
        return
    asr = voice_config.get("ASRConfig", {})
    if isinstance(asr, dict):
        provider_params = asr.get("ProviderParams", {})
        if isinstance(provider_params, dict):
            provider_params.pop("context", None)
    llm = voice_config.get("LLMConfig", {})
    if not isinstance(llm, dict):
        return
    llm.pop("Stream", None)
    vision = llm.get("VisionConfig")
    if isinstance(vision, dict):
        for key in ("ImageDetail", "Height", "Interval", "ImagesLimit"):
            vision.pop(key, None)


def _latency_mode_disables_function_calling(config: Mapping[str, Any]) -> bool:
    latency_mode = config.get("LocalLatencyMode", config.get("local_latency_mode", {}))
    if not isinstance(latency_mode, Mapping):
        return False
    return bool(latency_mode.get("DisableFunctionCalling", latency_mode.get("disable_function_calling", False)))


def _strip_voice_tools_for_latency(config: dict[str, Any]) -> None:
    request = config.get("StartVoiceChat", config.get("start_voice_chat", config))
    if not isinstance(request, dict):
        return
    voice_config = request.get("Config", {})
    if isinstance(voice_config, dict):
        llm_config = voice_config.get("LLMConfig", {})
        if isinstance(llm_config, dict):
            llm_config.pop("Tools", None)
        voice_config.pop("FunctionCallingConfig", None)
        voice_config.pop("WebSearchAgentConfig", None)
        s2s_config = voice_config.get("S2SConfig", {})
        if isinstance(s2s_config, dict):
            provider_params = s2s_config.get("ProviderParams", {})
            if isinstance(provider_params, dict):
                dialog = provider_params.get("dialog", {})
                if isinstance(dialog, dict):
                    extra = dialog.get("extra", {})
                    if isinstance(extra, dict):
                        extra["enable_volc_websearch"] = False
    websearch_config = config.get("WebSearchOpenAPI", config.get("web_search_openapi", {}))
    if isinstance(websearch_config, dict):
        websearch_config["Enabled"] = False


def _redact_for_http(value: Any) -> Any:
    if isinstance(value, Mapping):
        redacted: dict[str, Any] = {}
        for key, item in value.items():
            key_text = str(key).lower()
            if any(part in key_text for part in ("token", "secret", "apikey", "api_key", "accesskey", "authorization")):
                redacted[str(key)] = "***"
            else:
                redacted[str(key)] = _redact_for_http(item)
        return redacted
    if isinstance(value, list):
        return [_redact_for_http(item) for item in value]
    return value


def _extract_start_voice_chat_request(config: Mapping[str, Any]) -> dict[str, Any]:
    request = config.get("StartVoiceChat", config.get("start_voice_chat", config))
    if not isinstance(request, dict):
        raise ValueError("StartVoiceChat config must be a JSON object")
    return request


def _request_with_fresh_task_id(request: Mapping[str, Any], *, suppress_welcome: bool = False) -> dict[str, Any]:
    cloned = json.loads(json.dumps(request, ensure_ascii=False))
    if not isinstance(cloned, dict):
        raise ValueError("StartVoiceChat request must be a JSON object")
    base = re.sub(r"[^A-Za-z0-9_-]+", "_", str(cloned.get("TaskId") or "pet_voice_task")).strip("_")
    if not base:
        base = "pet_voice_task"
    suffix = time.strftime("%Y%m%d_%H%M%S", time.localtime()) + "_" + uuid.uuid4().hex[:6]
    max_base_len = max(1, 64 - len(suffix) - 1)
    cloned["TaskId"] = f"{base[:max_base_len]}_{suffix}"
    if suppress_welcome:
        _suppress_start_voice_chat_welcome(cloned)
    return cloned


def _suppress_start_voice_chat_welcome(request: dict[str, Any]) -> None:
    agent_config = request.get("AgentConfig")
    if isinstance(agent_config, dict):
        for key in ("WelcomeMessage", "welcomeMessage", "welcome_message", "WelcomeText", "welcomeText"):
            if key in agent_config:
                agent_config[key] = ""


def _request_has_welcome_message(request: Mapping[str, Any]) -> bool:
    agent_config = request.get("AgentConfig")
    if not isinstance(agent_config, Mapping):
        return False
    for key in ("WelcomeMessage", "welcomeMessage", "welcome_message", "WelcomeText", "welcomeText"):
        if str(agent_config.get(key) or "").strip():
            return True
    return False


def _vision_config_requested(request: Mapping[str, Any]) -> bool:
    config = request.get("Config", {})
    if not isinstance(config, Mapping):
        return False
    llm = config.get("LLMConfig", {})
    if not isinstance(llm, Mapping):
        return False
    vision = llm.get("VisionConfig", {})
    if not isinstance(vision, Mapping):
        return False
    return bool(vision.get("Enable", vision.get("enable", False)))


def _vision_snapshot_config(request: Mapping[str, Any]) -> Mapping[str, Any]:
    config = request.get("Config", {})
    if not isinstance(config, Mapping):
        return {}
    llm = config.get("LLMConfig", {})
    if not isinstance(llm, Mapping):
        return {}
    vision = llm.get("VisionConfig", {})
    if not isinstance(vision, Mapping):
        return {}
    snapshot = vision.get("SnapshotConfig", vision.get("snapshot_config", {}))
    return snapshot if isinstance(snapshot, Mapping) else {}


def _vision_snapshot_stream_type(request: Mapping[str, Any]) -> int:
    value = _vision_snapshot_config(request).get("StreamType")
    if isinstance(value, bool):
        return -1
    try:
        return int(value)
    except (TypeError, ValueError):
        return -1


def _vision_snapshot_stream_name(request: Mapping[str, Any]) -> str:
    return {0: "main_video", 1: "screen"}.get(_vision_snapshot_stream_type(request), "unknown")


def _ensure_direct_rtc_vision_uses_screen_stream(request: Mapping[str, Any], logger: logging.Logger) -> None:
    if not _vision_config_requested(request) or _is_s2s_request(request):
        return
    config = request.get("Config", {})
    if not isinstance(config, dict):
        return
    llm = config.get("LLMConfig", {})
    if not isinstance(llm, dict):
        return
    vision = llm.get("VisionConfig", {})
    if not isinstance(vision, dict):
        return
    snapshot = vision.get("SnapshotConfig")
    if not isinstance(snapshot, dict):
        snapshot = {}
        vision["SnapshotConfig"] = snapshot
    previous = snapshot.get("StreamType")
    if previous != 1:
        snapshot["StreamType"] = 1
        logger.warning(
            "VisionConfig SnapshotConfig.StreamType corrected from %s to 1 (screen stream).",
            previous,
        )


def _set_vision_snapshot_height(request: Mapping[str, Any], height: int) -> None:
    if _is_s2s_request(request) or not isinstance(request, dict):
        return
    config = request.get("Config", {})
    if not isinstance(config, dict):
        return
    llm = config.get("LLMConfig", {})
    if not isinstance(llm, dict):
        return
    vision = llm.get("VisionConfig", {})
    if not isinstance(vision, dict) or not bool(vision.get("Enable", vision.get("enable", False))):
        return
    snapshot = vision.get("SnapshotConfig")
    if not isinstance(snapshot, dict):
        snapshot = {}
        vision["SnapshotConfig"] = snapshot
    snapshot["Height"] = int(max(120, min(2160, height)))


def _is_s2s_request(request: Mapping[str, Any]) -> bool:
    config = request.get("Config", {})
    return isinstance(config, Mapping) and isinstance(config.get("S2SConfig"), Mapping)


def _direct_rtc_vision_supported_in_request(request: Mapping[str, Any]) -> bool:
    return _vision_config_requested(request) and not _is_s2s_request(request)


def _vision_supported_in_request(request: Mapping[str, Any]) -> bool:
    return _direct_rtc_vision_supported_in_request(request)


def _companion_vision_config(config: Mapping[str, Any]) -> dict[str, Any]:
    raw = config.get("CompanionVision", config.get("companion_vision", {}))
    if not isinstance(raw, Mapping):
        raw = {}
    return {
        "enabled": bool(raw.get("Enabled", raw.get("enabled", False))),
        "interval_sec": _snap_companion_vision_interval(raw.get("IntervalSec", raw.get("interval_sec", 8.0))),
        "tick_sec": _safe_float(raw.get("TickSec", raw.get("tick_sec", 0.5)), 0.5),
        "wait_until_speech_done": bool(raw.get("WaitUntilSpeechDone", raw.get("wait_until_speech_done", True))),
        "pending_timeout_sec": _safe_float(raw.get("PendingTimeoutSec", raw.get("pending_timeout_sec", 12.0)), 12.0),
        "max_busy_without_audio_sec": _safe_float(raw.get("MaxBusyWithoutAudioSec", raw.get("max_busy_without_audio_sec", 10.0)), 10.0),
        "min_idle_sec": _safe_float(raw.get("MinIdleSec", raw.get("min_idle_sec", 0.0)), 0.0),
        "user_silence_sec": _safe_float(raw.get("UserSilenceSec", raw.get("user_silence_sec", 0.0)), 0.0),
        "failure_backoff_sec": _safe_float(raw.get("FailureBackoffSec", raw.get("failure_backoff_sec", 0.0)), 0.0),
        "max_failure_backoff_sec": _safe_float(raw.get("MaxFailureBackoffSec", raw.get("max_failure_backoff_sec", 0.0)), 0.0),
        "failure_circuit_break_count": int(
            max(0.0, _safe_float(raw.get("FailureCircuitBreakCount", raw.get("failure_circuit_break_count", 3)), 3.0))
        ),
        "failure_circuit_break_sec": _safe_float(
            raw.get("FailureCircuitBreakSec", raw.get("failure_circuit_break_sec", 120.0)),
            120.0,
        ),
        "empty_fallback_enabled": bool(raw.get("EmptyFallbackEnabled", raw.get("empty_fallback_enabled", False))),
        "empty_fallback_timeout_sec": _safe_float(
            raw.get("EmptyFallbackTimeoutSec", raw.get("empty_fallback_timeout_sec", 14.0)),
            14.0,
        ),
        "empty_fallback_max_width": int(
            max(360.0, _safe_float(raw.get("EmptyFallbackMaxWidth", raw.get("empty_fallback_max_width", 1920)), 1920.0))
        ),
        "empty_fallback_jpeg_quality": int(
            max(35.0, _safe_float(raw.get("EmptyFallbackJpegQuality", raw.get("empty_fallback_jpeg_quality", 82)), 82.0))
        ),
        "local_vision_sidecar_enabled": _safe_bool(
            raw.get("LocalVisionSidecarEnabled", raw.get("local_vision_sidecar_enabled", True)), True
        ),
        "local_vision_sidecar_timeout_sec": _safe_float(
            raw.get("LocalVisionSidecarTimeoutSec", raw.get("local_vision_sidecar_timeout_sec", 20.0)), 20.0
        ),
        "welcome_max_wait_sec": _safe_float(raw.get("WelcomeMaxWaitSec", raw.get("welcome_max_wait_sec", 18.0)), 18.0),
        "recent_context_count": int(max(0.0, _safe_float(raw.get("RecentContextCount", raw.get("recent_context_count", 0)), 0.0))),
        "recent_context_window_sec": _safe_float(raw.get("RecentContextWindowSec", raw.get("recent_context_window_sec", 180.0)), 180.0),
        "interrupt_mode": int(_safe_float(raw.get("InterruptMode", raw.get("interrupt_mode", 1)), 1.0)),
        "prompt": str(raw.get("Prompt", raw.get("prompt", _DEFAULT_COMPANION_VISION_PROMPT)) or _DEFAULT_COMPANION_VISION_PROMPT),
    }


def _snap_companion_vision_interval(value: Any) -> float:
    requested = _safe_float(value, 10.0)
    return min(_COMPANION_VISION_INTERVAL_PRESETS, key=lambda preset: abs(preset - requested))


def _bridge_ai_state_from_event(event: Any) -> str:
    if isinstance(event, str):
        return _normalize_bridge_ai_state(event)
    if not isinstance(event, Mapping):
        return ""
    stage_code = _extract_stage_code(event)
    if stage_code is not None:
        return {
            1: "listening",
            2: "thinking",
            3: "speaking",
            4: "interrupted",
            5: "idle",
        }.get(stage_code, "")
    for key in ("state", "ai_state", "aiState", "status", "event", "type", "description", "Description"):
        value = event.get(key)
        if value:
            normalized = _normalize_bridge_ai_state(str(value))
            if normalized:
                return normalized
    return ""


def _normalize_bridge_ai_state(value: str) -> str:
    normalized = str(value or "").strip().lower().replace("-", "_").replace(" ", "_")
    return {
        "user_speaking": "listening",
        "user_listening": "listening",
        "speech_start": "listening",
        "listening": "listening",
        "ai_processing": "thinking",
        "ai_thinking": "thinking",
        "llm_running": "thinking",
        "processing": "thinking",
        "thinking": "thinking",
        "ai_speaking": "speaking",
        "tts_playing": "speaking",
        "response_audio_start": "speaking",
        "speaking": "speaking",
        "answering": "speaking",
        "ai_idle": "idle",
        "response_finished": "idle",
        "tts_finished": "idle",
        "completed": "idle",
        "finished": "idle",
        "idle": "idle",
        "interrupted": "interrupted",
        "user_barge_in": "interrupted",
        "barge_in": "interrupted",
    }.get(normalized, "")


def _normalize_subtitle_plain_text(text: str) -> str:
    return re.sub(r"\s+", " ", str(text or "").strip())


def _compact_similarity_key(text: str) -> str:
    return re.sub(r"[\s，。！？、,.!?;；:：\"'“”‘’]+", "", _normalize_subtitle_plain_text(text))


def _companion_sidecar_text_too_similar(text: str, recent: list[str]) -> bool:
    candidate = _compact_similarity_key(text)
    if len(candidate) < 4:
        return False
    for item in recent:
        reference = _compact_similarity_key(item)
        if not reference:
            continue
        if candidate == reference:
            return True
        if len(candidate) >= 8 and len(reference) >= 8:
            if difflib.SequenceMatcher(None, candidate, reference).ratio() >= 0.82:
                return True
    return False


def _system_messages_text(value: Any) -> str:
    if isinstance(value, str):
        return value.strip()
    if isinstance(value, list):
        return "\n".join(str(item).strip() for item in value if str(item).strip())
    return ""


def _sanitize_direct_speech_text(text: str, *, max_chars: int = 80) -> str:
    cleaned = sanitize_spoken_text(_normalize_subtitle_plain_text(text))
    if not cleaned:
        return ""
    cleaned = re.sub(r"[\(（【\[].*?[\)）】\]]", "", cleaned)
    cleaned = re.sub(r"[\U00010000-\U0010ffff]", "", cleaned)
    cleaned = re.sub(r"\s+", " ", cleaned).strip()
    if not cleaned:
        return ""
    parts = _split_omnivoice_caption_segments(cleaned, max_chars=max_chars)
    return (parts[0] if parts else cleaned[:max_chars]).strip()


def _extract_sidecar_speech_text(text: str) -> str:
    raw = str(text or "").strip()
    if not raw:
        return ""
    parsed: Any = None
    try:
        parsed = json.loads(raw)
    except Exception:
        match = re.search(r"\{.*\}", raw, flags=re.DOTALL)
        if match:
            try:
                parsed = json.loads(match.group(0))
            except Exception:
                parsed = None
    if isinstance(parsed, Mapping):
        for key in ("speech", "text", "reply", "message", "content"):
            value = parsed.get(key)
            if value is not None and str(value).strip():
                return str(value).strip()
    return raw


def _chat_completions_url(value: str) -> str:
    raw = str(value or "").strip().rstrip("/")
    if not raw:
        return raw
    lowered = raw.lower()
    if lowered.endswith("/chat/completions"):
        return raw
    if lowered.endswith("/v1") or lowered.endswith("/compatible-mode/v1"):
        return raw + "/chat/completions"
    parsed = urlparse(raw)
    path = parsed.path.rstrip("/")
    if not path or path == "/" or path.lower() == "/beta" or re.fullmatch(r"/v[0-9][A-Za-z0-9_./-]*", path, re.IGNORECASE):
        return raw + "/chat/completions"
    return raw


def _normalize_external_text_to_llm_message(text: str, *, source: str = "") -> str:
    normalized = _normalize_subtitle_plain_text(text)
    normalized = re.sub(r"\s+", " ", normalized).strip()
    return _trim_external_text_to_llm(normalized, max_chars=_EXTERNAL_TEXT_TO_LLM_MAX_CHARS)


def _trim_external_text_to_llm(text: str, *, max_chars: int, ensure_punctuation: bool = True) -> str:
    normalized = re.sub(r"\s+", " ", str(text or "")).strip()
    if not normalized:
        return ""
    normalized = normalized.strip("\ufeff")
    end_punct = "\u3002\uff01\uff1f!?;；,，."
    if ensure_punctuation and normalized[-1] not in end_punct:
        normalized += "\u3002"
    if len(normalized) <= max_chars:
        return normalized
    if max_chars <= 1:
        return normalized[:max_chars]
    clipped = normalized[: max_chars - 1].rstrip(" \t\r\n,，、;；:：")
    if ensure_punctuation:
        clipped = clipped.rstrip(end_punct) + "\u3002"
    return clipped[:max_chars]


def _split_omnivoice_caption_segments(text: str, *, max_chars: int = 44) -> list[str]:
    normalized = _normalize_subtitle_plain_text(text)
    if not normalized:
        return []

    parts: list[str] = []
    start = 0
    for match in re.finditer(r"[\u3002\uff01\uff1f!?\uff1b;]+", normalized):
        end = match.end()
        part = normalized[start:end].strip()
        if part:
            parts.extend(_split_long_omnivoice_caption_part(part, max_chars=max_chars))
        start = end

    tail = normalized[start:].strip()
    if tail:
        parts.extend(_split_long_omnivoice_caption_part(tail, max_chars=max_chars))

    if not parts:
        parts = _split_long_omnivoice_caption_part(normalized, max_chars=max_chars)
    return [part for part in parts if part]


def _split_long_omnivoice_caption_part(text: str, *, max_chars: int) -> list[str]:
    if len(text) <= max_chars:
        return [text]

    output: list[str] = []
    current = ""
    for token in re.split(r"([\uff0c\u3001,\uff1a:])", text):
        if not token:
            continue
        candidate = current + token
        if current and len(candidate) > max_chars:
            output.append(current.strip())
            current = token.lstrip("\uff0c\u3001,\uff1a:").strip()
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
            chunk = item[index : index + max_chars].strip()
            if chunk:
                final.append(chunk)
    return final


def _debug_preview_text(text: str, limit: int = 96) -> str:
    normalized = _normalize_subtitle_plain_text(text)
    if len(normalized) <= limit:
        return normalized
    return normalized[: max(0, limit - 3)] + "..."


def _normalize_for_echo_compare(text: str) -> str:
    return re.sub(r"[\W_]+", "", str(text or "").strip().lower(), flags=re.UNICODE)


def _longest_common_substring_len(left: str, right: str) -> int:
    if not left or not right:
        return 0
    previous = [0] * (len(right) + 1)
    best = 0
    for left_char in left:
        current = [0] * (len(right) + 1)
        for idx, right_char in enumerate(right, start=1):
            if left_char == right_char:
                current[idx] = previous[idx - 1] + 1
                if current[idx] > best:
                    best = current[idx]
        previous = current
    return best


def text_hash_for_log(text: str) -> str:
    return hashlib.sha256(str(text or "").encode("utf-8")).hexdigest()


def _is_omnivoice_busy_error(error: str) -> bool:
    normalized = str(error or "").strip().lower()
    return "another tts task" in normalized or "already running" in normalized


def _clip_companion_context_line(text: str, *, max_chars: int = 80) -> str:
    normalized = _normalize_subtitle_plain_text(text)
    if len(normalized) <= max_chars:
        return normalized
    return normalized[:max_chars].rstrip() + "..."


def _has_voice_stop_intent(text: str) -> bool:
    normalized = _normalize_subtitle_plain_text(text)
    if not normalized:
        return False
    if _has_app_exit_intent(normalized):
        return True
    return any(
        token in normalized
        for token in (
            "停止语音",
            "关闭语音",
            "结束通话",
            "退出通话",
            "别说话了",
            "先别说",
            "闭麦",
            "停一下语音",
        )
    )


def _has_barge_in_intent_text(text: str) -> bool:
    normalized = _normalize_subtitle_plain_text(text)
    if not normalized:
        return False
    compact = re.sub(r"[\s,.;:!?，。！？、；：]+", "", normalized)
    tokens = (
        "\u505c",
        "\u505c\u4e00\u4e0b",
        "\u522b\u8bf4",
        "\u95ed\u5634",
        "\u6253\u65ad",
        "\u6682\u505c",
        "\u7b49\u4e00\u4e0b",
        "\u5148\u522b\u8bf4",
        "\u7ed3\u675f\u8bed\u97f3",
        "\u9000\u51fa\u8bed\u97f3",
        "stop",
        "pause",
    )
    return any(token in normalized or token in compact for token in tokens)


def _has_app_exit_intent(text: str) -> bool:
    normalized = _normalize_subtitle_plain_text(text)
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


def _safe_float(value: Any, default: float) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


def _clamp_float(value: Any, default: float, low: float, high: float) -> float:
    return max(low, min(high, _safe_float(value, default)))


def _safe_bool(value: Any, default: bool) -> bool:
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        return bool(value)
    text = str(value).strip().lower()
    if text in {"1", "true", "yes", "on", "enable", "enabled"}:
        return True
    if text in {"0", "false", "no", "off", "disable", "disabled"}:
        return False
    return default


def _silver_wolf_runtime_root() -> Path:
    for env_key in ("LOCALAPPDATA", "APPDATA"):
        base = os.getenv(env_key)
        if base:
            return Path(base) / "voicechatpet"
    return Path.home() / ".silverwolf_pet"


def _resolve_head_tracker_root() -> Path | None:
    candidates = [
        ROOT / "head_tracker",
        ROOT.parent.parent / "head_tracker",
        ROOT.parent.parent.parent / "head_tracker",
        ROOT.parent.parent.parent.parent / "head_tracker",
    ]
    for candidate in candidates:
        try:
            resolved = candidate.resolve()
        except OSError:
            continue
        if (resolved / "head_tracker.py").exists():
            return resolved
    return None


def _is_local_camera_hub_url(source_url: str) -> bool:
    try:
        parsed = urlparse(source_url)
    except Exception:
        return False
    if parsed.scheme not in {"http", "https"}:
        return False
    host = (parsed.hostname or "").lower()
    return host in {"127.0.0.1", "localhost", "::1"}


def _camera_hub_status_url(source_url: str) -> str:
    parsed = urlparse(source_url)
    return parsed._replace(path="/status", params="", query="", fragment="").geturl()


def _camera_hub_status_ready(source_url: str, *, timeout: float) -> bool:
    try:
        with urlopen(_camera_hub_status_url(source_url), timeout=timeout) as response:
            payload = json.loads(response.read().decode("utf-8", errors="replace"))
        return bool(payload.get("ok")) and float(payload.get("lastFrameAgeSec") or 999.0) < 2.5
    except Exception:
        return False


def _stream_settings_from_config(
    config: Mapping[str, Any],
    paths: tuple[tuple[str, ...], ...],
    defaults: Mapping[str, Any],
) -> dict[str, Any]:
    payload: Mapping[str, Any] = {}
    for path in paths:
        candidate = _pick_nested(config, path)
        if isinstance(candidate, Mapping):
            payload = candidate
            break
    return _normalize_stream_settings(payload, defaults)


def _normalize_stream_settings(payload: Mapping[str, Any], defaults: Mapping[str, Any]) -> dict[str, Any]:
    values = dict(defaults)
    width = _first_present_value(payload, "width", "Width")
    height = _first_present_value(payload, "height", "Height")
    snapshot_height = _first_present_value(payload, "snapshotHeight", "SnapshotHeight", "visionSnapshotHeight", "VisionSnapshotHeight")
    fps = _first_present_value(payload, "fps", "Fps", "frameRate", "FrameRate")
    max_kbps = _first_present_value(payload, "maxKbps", "MaxKbps", "bitrateKbps", "BitrateKbps")
    values["width"] = int(_clamp_float(width, float(values.get("width", 640)), 160.0, 3840.0))
    values["height"] = int(_clamp_float(height, float(values.get("height", 360)), 120.0, 2160.0))
    if "snapshotHeight" in values or snapshot_height is not None:
        values["snapshotHeight"] = int(_clamp_float(snapshot_height, float(values.get("snapshotHeight", values["height"])), 120.0, 2160.0))
    values["fps"] = int(_clamp_float(fps, float(values.get("fps", 15)), 1.0, 60.0))
    values["maxKbps"] = int(_clamp_float(max_kbps, float(values.get("maxKbps", 700)), 100.0, 12000.0))
    if "cameraOverlayEnabled" in values or _first_present_value(payload, "cameraOverlayEnabled", "CameraOverlayEnabled") is not None:
        values["cameraOverlayEnabled"] = _safe_bool(
            _first_present_value(payload, "cameraOverlayEnabled", "CameraOverlayEnabled"),
            bool(values.get("cameraOverlayEnabled", False)),
        )
    if "cameraOverlayWidth" in values or _first_present_value(payload, "cameraOverlayWidth", "CameraOverlayWidth") is not None:
        overlay_width = _first_present_value(payload, "cameraOverlayWidth", "CameraOverlayWidth")
        values["cameraOverlayWidth"] = int(_clamp_float(overlay_width, float(values.get("cameraOverlayWidth", 640)), 160.0, 1280.0))
    if "cameraOverlayHeight" in values or _first_present_value(payload, "cameraOverlayHeight", "CameraOverlayHeight") is not None:
        overlay_height = _first_present_value(payload, "cameraOverlayHeight", "CameraOverlayHeight")
        values["cameraOverlayHeight"] = int(_clamp_float(overlay_height, float(values.get("cameraOverlayHeight", 360)), 90.0, 720.0))
    if "cameraOverlayPadding" in values or _first_present_value(payload, "cameraOverlayPadding", "CameraOverlayPadding") is not None:
        overlay_padding = _first_present_value(payload, "cameraOverlayPadding", "CameraOverlayPadding")
        values["cameraOverlayPadding"] = int(_clamp_float(overlay_padding, float(values.get("cameraOverlayPadding", 24)), 0.0, 200.0))
    overlay_position = _first_present_value(payload, "cameraOverlayPosition", "CameraOverlayPosition")
    if overlay_position is not None or "cameraOverlayPosition" in values:
        cleaned_position = str(overlay_position if overlay_position is not None else values.get("cameraOverlayPosition", "")).strip()
        values["cameraOverlayPosition"] = cleaned_position or "bottomLeft"
    overlay_source_url = _first_present_value(payload, "cameraOverlaySourceUrl", "CameraOverlaySourceUrl")
    if overlay_source_url is not None or "cameraOverlaySourceUrl" in values:
        cleaned_overlay_url = str(overlay_source_url if overlay_source_url is not None else values.get("cameraOverlaySourceUrl", "")).strip()
        values["cameraOverlaySourceUrl"] = cleaned_overlay_url or "http://127.0.0.1:17863/stream.mjpg"
    if "faceTrackingPacketFps" in values:
        packet_fps = _first_present_value(
            payload,
            "faceTrackingPacketFps",
            "FaceTrackingPacketFps",
            "packetFps",
            "PacketFps",
        )
        values["faceTrackingPacketFps"] = int(
            _clamp_float(packet_fps, float(values.get("faceTrackingPacketFps", 15)), 2.0, 30.0)
        )
    if "useCameraHub" in values or _first_present_value(payload, "useCameraHub", "UseCameraHub") is not None:
        values["useCameraHub"] = _safe_bool(
            _first_present_value(payload, "useCameraHub", "UseCameraHub", "cameraHub", "CameraHub"),
            bool(values.get("useCameraHub", True)),
        )
    camera_hub_url = _first_present_value(
        payload,
        "cameraHubUrl",
        "CameraHubUrl",
        "cameraHubStreamUrl",
        "CameraHubStreamUrl",
    )
    if camera_hub_url is not None or "cameraHubUrl" in values:
        cleaned_hub_url = str(camera_hub_url if camera_hub_url is not None else values.get("cameraHubUrl", "")).strip()
        values["cameraHubUrl"] = cleaned_hub_url or "http://127.0.0.1:17863/stream.mjpg"
    if "useVirtualCamera" in values or _first_present_value(payload, "useVirtualCamera", "UseVirtualCamera") is not None:
        values["useVirtualCamera"] = _safe_bool(
            _first_present_value(payload, "useVirtualCamera", "UseVirtualCamera", "virtualCamera", "VirtualCamera"),
            bool(values.get("useVirtualCamera", False)),
        )
    if "requireVirtualCamera" in values or _first_present_value(payload, "requireVirtualCamera", "RequireVirtualCamera") is not None:
        values["requireVirtualCamera"] = _safe_bool(
            _first_present_value(payload, "requireVirtualCamera", "RequireVirtualCamera"),
            bool(values.get("requireVirtualCamera", False)),
        )
    if "sendFaceTrackingPackets" in values or _first_present_value(payload, "sendFaceTrackingPackets", "SendFaceTrackingPackets") is not None:
        values["sendFaceTrackingPackets"] = _safe_bool(
            _first_present_value(payload, "sendFaceTrackingPackets", "SendFaceTrackingPackets"),
            bool(values.get("sendFaceTrackingPackets", False)),
        )
    device_keyword = _first_present_value(
        payload,
        "deviceKeyword",
        "DeviceKeyword",
        "virtualCameraKeyword",
        "VirtualCameraKeyword",
        "cameraDeviceKeyword",
        "CameraDeviceKeyword",
    )
    if device_keyword is not None or "deviceKeyword" in values:
        cleaned = str(device_keyword if device_keyword is not None else values.get("deviceKeyword", "")).strip()
        values["deviceKeyword"] = cleaned or "virtual,obs"
    return values


def _first_present_value(value: Mapping[str, Any], *keys: str) -> Any:
    for key in keys:
        if key in value and value[key] is not None:
            return value[key]
    return None


def _sanitize_face_tracking_packet(payload: Mapping[str, Any]) -> dict[str, Any]:
    return {
        "face_found": bool(payload.get("face_found", payload.get("faceFound", False))),
        "face_center_x": _clamp_float(payload.get("face_center_x", payload.get("faceCenterX")), 0.0, -1.0, 1.0),
        "face_center_y": _clamp_float(payload.get("face_center_y", payload.get("faceCenterY")), 0.0, -1.0, 1.0),
        "face_width_px": max(0.0, _safe_float(payload.get("face_width_px", payload.get("faceWidthPx")), 0.0)),
        "yaw": _clamp_float(payload.get("yaw"), 0.0, -90.0, 90.0),
        "pitch": _clamp_float(payload.get("pitch"), 0.0, -60.0, 60.0),
        "roll": _clamp_float(payload.get("roll"), 0.0, -90.0, 90.0),
        "z_cm": max(0.0, _safe_float(payload.get("z_cm", payload.get("zCm")), 0.0)),
        "z_offset": _clamp_float(payload.get("z_offset", payload.get("zOffset")), 0.0, -1.0, 1.0),
        "timestamp": _safe_float(payload.get("timestamp"), time.time()),
    }


def _find_bot_uid(request: Mapping[str, Any]) -> str:
    agent = request.get("AgentConfig", {})
    if isinstance(agent, Mapping) and agent.get("UserId"):
        return str(agent["UserId"])
    for value in request.values():
        if isinstance(value, Mapping):
            found = _find_bot_uid(value)
            if found:
                return found
    return ""


def _pick_nested(root: Mapping[str, Any], *paths: tuple[str, ...]) -> Any:
    for path in paths:
        current: Any = root
        for part in path:
            if not isinstance(current, Mapping) or part not in current:
                current = None
                break
            current = current[part]
        if current:
            return current
    return None


def _first_text(value: Any) -> str:
    if isinstance(value, list) and value:
        return str(value[0])
    if isinstance(value, str):
        return value
    return ""


def _first_present_text(value: Mapping[str, Any], *keys: str) -> str:
    for key in keys:
        item = value.get(key)
        if item is not None and str(item).strip():
            return str(item).strip()
    return ""


def _echo_round_key(value: Mapping[str, Any]) -> str:
    round_id = _first_present_text(value, "RoundID", "roundID", "roundId", "round_id")
    if not round_id:
        return ""
    task_id = _first_present_text(value, "TaskId", "taskId", "task_id")
    return f"{task_id}:{round_id}" if task_id else f"*:{round_id}"


def _subtitle_speaker(event: Mapping[str, Any], bot_uid: str = "") -> str:
    speaker = str(event.get("speaker") or event.get("role") or event.get("user_type") or event.get("source") or "").strip().lower()
    if speaker in {"ai", "assistant", "bot", "agent"}:
        return "ai"
    if speaker in {"user", "human", "local"}:
        return "user"
    uid = _first_present_text(event, "uid", "userId", "user_id", "sender_uid")
    if uid and bot_uid:
        return "ai" if uid == bot_uid else "user"
    return ""


def _subtitle_is_final(event: Mapping[str, Any]) -> bool:
    for key in ("is_final", "final", "completed", "definite"):
        if key in event:
            return _safe_bool(event.get(key), False)
    return False


def _json_or_text(value: Any) -> Any:
    if not isinstance(value, str):
        return value
    try:
        return json.loads(value)
    except json.JSONDecodeError:
        return value


def _iter_function_call_candidates(value: Any) -> list[Mapping[str, Any]]:
    candidates: list[Mapping[str, Any]] = []
    if isinstance(value, list):
        for item in value:
            candidates.extend(_iter_function_call_candidates(item))
        return candidates
    if not isinstance(value, Mapping):
        return candidates

    name = value.get("name") or value.get("function_name")
    if name in _SUPPORTED_TOOL_NAMES:
        candidates.append(value)
    function = value.get("function")
    if isinstance(function, Mapping) and function.get("name") in _SUPPORTED_TOOL_NAMES:
        candidates.append(function)
    for key in ("function_call", "tool_call", "toolCall", "call"):
        child = value.get(key)
        if isinstance(child, Mapping):
            candidates.extend(_iter_function_call_candidates(child))
    for key in ("function_calls", "tool_calls", "toolCalls", "calls"):
        child_list = value.get(key)
        if isinstance(child_list, list):
            candidates.extend(_iter_function_call_candidates(child_list))
    return candidates


def _looks_like_function_call_meta(value: Mapping[str, Any]) -> bool:
    event_type = str(value.get("event_type", value.get("type", ""))).strip().lower()
    if event_type not in {"function_calling", "function_call", "tool_call", "tool_calling"}:
        return False
    return bool(_first_present_text(value, "tool_call_id", "toolCallId", "ToolCallID", "id"))


def _extract_tool_function_name(value: Mapping[str, Any]) -> str:
    name = value.get("name") or value.get("function_name")
    if isinstance(name, str) and name.strip():
        return name.strip()
    function = value.get("function")
    if isinstance(function, str):
        return function.strip()
    if isinstance(function, Mapping):
        child_name = function.get("name") or function.get("function_name")
        if isinstance(child_name, str):
            return child_name.strip()
    for key in ("function_call", "tool_call", "toolCall", "call"):
        child = value.get(key)
        if isinstance(child, Mapping):
            found = _extract_tool_function_name(child)
            if found:
                return found
    return ""


def _extract_tool_arguments(value: Mapping[str, Any]) -> Mapping[str, Any]:
    arguments = value.get("arguments", value.get("Arguments", value.get("args", {})))
    if isinstance(arguments, str):
        try:
            parsed = json.loads(arguments)
            return parsed if isinstance(parsed, Mapping) else {}
        except json.JSONDecodeError:
            return {}
    if isinstance(arguments, Mapping):
        return arguments
    function = value.get("function")
    if isinstance(function, Mapping):
        return _extract_tool_arguments(function)
    for key in ("function_call", "tool_call", "toolCall", "call"):
        child = value.get(key)
        if isinstance(child, Mapping):
            found = _extract_tool_arguments(child)
            if found:
                return found
    return {}


def _sanitize_web_search_query(query: str, recent_user_text: str) -> str:
    original = _compact_search_query(query)
    user_query = _clean_web_search_user_text(recent_user_text)
    if user_query and (_has_web_search_intent(user_query) or _looks_like_query_drift(original, user_query)):
        cleaned = user_query
    else:
        cleaned = original

    if not cleaned:
        return original
    if "今天" in cleaned and "2026" not in cleaned:
        cleaned = cleaned.replace("今天", "2026年4月27日")
    if "现在" in cleaned and "2026" not in cleaned and any(token in cleaned for token in ("新闻", "发生", "几点", "时间")):
        cleaned = cleaned.replace("现在", "2026年4月27日")
    if "崩坏星穹铁道" in original and "崩坏星穹铁道" not in cleaned:
        cleaned = "崩坏星穹铁道 " + cleaned
    if "虚构" in cleaned and not any(token in cleaned for token in ("崩坏", "星穹", "铁道")):
        cleaned = "崩坏星穹铁道 " + cleaned
    return _compact_search_query(cleaned)[:80] or original


def _clean_web_search_user_text(text: str) -> str:
    cleaned = _compact_search_query(text)
    cleaned = re.sub(r"^(银狼|小狼|助手)[，,。.\s]*", "", cleaned)
    cleaned = re.sub(r"(帮我|麻烦你|请|给我|一下|查一下|搜一下|搜索一下|联网查|联网搜)", "", cleaned)
    cleaned = re.sub(r"(你知道|告诉我|看看|看一下)", "", cleaned)
    return _compact_search_query(cleaned)


def _has_web_search_intent(text: str) -> bool:
    return _has_explicit_web_search_intent(text) or _has_factual_web_search_intent(text)


def _has_explicit_web_search_intent(text: str) -> bool:
    compact = _compact_search_query(text)
    return any(
        token in text
        for token in (
            "搜",
            "查",
            "联网",
            "攻略",
            "隐藏成就",
            "当前版本",
            "最新",
            "实时",
            "官方",
            "新闻",
            "价格",
            "虚构",
            "版本活动",
            "复刻",
        )
    ) or "websearch" in compact.lower() or "web search" in compact.lower()


def _has_factual_web_search_intent(text: str) -> bool:
    compact = _compact_search_query(text)
    if not compact:
        return False
    lower = compact.lower()
    if re.search(r"\b\d+(?:\.\d+)+\b", lower) and any(token in compact for token in ("版本", "名称", "名字", "活动", "更新")):
        return True
    if any(token in compact for token in ("当前版本", "现在版本", "版本名", "版本名称", "最新活动", "版本活动")):
        return True
    if any(token in compact for token in ("星穹铁道", "崩坏星穹铁道", "原神", "绝区零")) and any(
        token in compact for token in ("版本", "活动", "名称", "名字", "攻略", "隐藏成就", "角色", "复刻")
    ):
        return True
    return False


def _should_allow_web_search(recent_user_text: str, query: str) -> bool:
    return (
        _has_explicit_web_search_intent(recent_user_text)
        or _has_explicit_web_search_intent(query)
        or _has_factual_web_search_intent(recent_user_text)
        or _has_factual_web_search_intent(query)
    )


def _looks_like_query_drift(query: str, user_query: str) -> bool:
    if not query or not user_query:
        return False
    if len(query) > len(user_query) + 16:
        return True
    return any(token in query and token not in user_query for token in ("巡海游侠", "某角色", "示例", "假设"))


def _stop_volc_voice_runtime_process() -> None:
    if os.name != "nt":
        return
    try:
        subprocess.run(
            [
                "powershell.exe",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                "Get-Process -Name VolcVoiceRuntime -ErrorAction SilentlyContinue | Stop-Process -Force",
            ],
            check=False,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=3.0,
        )
    except Exception:
        logging.getLogger(__name__).exception("failed to stop VolcVoiceRuntime process")


def _compact_search_query(text: str) -> str:
    text = str(text or "").strip()
    text = re.sub(r"[\r\n\t]+", " ", text)
    text = re.sub(r"[“”\"'`]+", "", text)
    text = re.sub(r"\s+", " ", text)
    text = re.sub(r"^[，,。.!！?？\s]+|[，,。.!！?？\s]+$", "", text)
    return text.strip()


def _extract_summary_text_from_websearch_chunks(chunks: list[Mapping[str, Any]]) -> str:
    return "".join(_iter_websearch_delta_content(chunks)).strip()


def _extract_summary_text_from_websearch_result(payload: Any) -> str:
    if not isinstance(payload, Mapping):
        return ""
    result = payload.get("Result")
    if isinstance(result, Mapping):
        summary_text = result.get("SummaryText")
        if isinstance(summary_text, str) and summary_text.strip():
            return summary_text.strip()
        web_results = result.get("WebResults")
        if isinstance(web_results, list):
            parts: list[str] = []
            for item in web_results[:3]:
                if not isinstance(item, Mapping):
                    continue
                title = str(item.get("Title") or "").strip()
                summary = str(item.get("Summary") or item.get("Snippet") or "").strip()
                if title or summary:
                    parts.append(f"{title}\n{summary}".strip())
            return "\n\n".join(parts).strip()
    return ""


def _iter_websearch_delta_content(chunks: list[Mapping[str, Any]]) -> list[str]:
    parts: list[str] = []
    for payload in chunks:
        result = payload.get("Result") if isinstance(payload, Mapping) else None
        choices = result.get("Choices") if isinstance(result, Mapping) else None
        if choices is None and isinstance(payload, Mapping):
            choices = payload.get("Choices")
        if not isinstance(choices, list):
            continue
        for choice in choices:
            if not isinstance(choice, Mapping):
                continue
            delta = choice.get("Delta")
            if isinstance(delta, Mapping) and isinstance(delta.get("Content"), str):
                parts.append(delta["Content"])
            message = choice.get("Message")
            if isinstance(message, Mapping) and isinstance(message.get("Content"), str):
                parts.append(message["Content"])
    return parts


def _aggregate_websearch_chunks(chunks: list[dict[str, Any]]) -> dict[str, Any]:
    if not chunks:
        return {}
    first = chunks[0]
    last = chunks[-1]
    result = last.get("Result") if isinstance(last.get("Result"), Mapping) else {}
    first_result = first.get("Result") if isinstance(first.get("Result"), Mapping) else {}
    summary_text = _extract_summary_text_from_websearch_chunks(chunks)
    return {
        "ResponseMetadata": first.get("ResponseMetadata") or last.get("ResponseMetadata"),
        "Result": {
            "ResultCount": first_result.get("ResultCount", result.get("ResultCount")),
            "WebResults": first_result.get("WebResults", result.get("WebResults")),
            "SummaryText": summary_text,
            "SearchContext": first_result.get("SearchContext", result.get("SearchContext")),
            "Usage": result.get("Usage"),
            "ChunkCount": len(chunks),
            "LogId": result.get("LogId"),
        },
    }


def _looks_like_ai_state(value: Mapping[str, Any]) -> bool:
    event_type = str(value.get("type", value.get("event_type", ""))).strip().lower()
    if event_type in {"subtitle", "transcript", "caption"}:
        return False
    if "Stage" in value or "stage" in value:
        return True
    state_keys = {"state", "ai_state", "aiState", "status", "event", "Code", "code"}
    return any(key in value for key in state_keys)


def _extract_stage_code(value: Mapping[str, Any]) -> int | None:
    for stage_key in ("Stage", "stage"):
        stage = value.get(stage_key)
        if isinstance(stage, Mapping):
            code = stage.get("Code", stage.get("code"))
            try:
                return int(code)
            except (TypeError, ValueError):
                return None
    for key in ("Code", "code"):
        if key in value:
            try:
                return int(value[key])
            except (TypeError, ValueError):
                return None
    return None


def _looks_like_subtitle(value: Mapping[str, Any]) -> bool:
    event_type = str(value.get("type", value.get("event_type", ""))).strip().lower()
    if event_type in {"subtitle", "transcript", "caption"}:
        return True
    if not any(key in value for key in ("text", "content", "subtitle", "transcript", "utterance")):
        return False
    return any(key in value for key in ("speaker", "role", "user_type", "source", "uid", "userId", "user_id"))


def _elapsed_ms(start: float, end: float) -> float | None:
    if not start or not end:
        return None
    return round((end - start) * 1000.0, 3)


def _new_trace_id(prefix: str) -> str:
    return f"{prefix}-{int(time.time() * 1000)}-{uuid.uuid4().hex[:8]}"


if __name__ == "__main__":
    raise SystemExit(main())
