from __future__ import annotations

import json
import os
import re
import threading
import time
import uuid
import winsound
from dataclasses import dataclass
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Mapping

from src.agent_pet_control_bridge import PetPerformCommand, PetPerformCommandError
from src.pet_pose_bridge import PoseCommand, PoseCommandError, create_presentation_client
from src.pet_pose_bridge.pose_command import FORBIDDEN_FIELDS
from src.spoken_text_sanitizer import sanitize_spoken_text
from .mimo_tts_client import MimoTTSClient, MimoTTSConfig, MimoTTSError
from .volc_tts_client import VolcTTSClient, VolcTTSConfig, VolcTTSError


@dataclass(frozen=True)
class AgentSpeakerSettings:
    host: str
    port: int
    require_auth: bool
    api_key: str
    max_text_chars: int
    default_emotion: str
    default_gesture: str
    default_posture: str
    default_priority: int


class AgentSpeakerServer:
    """HTTP server that lets any desktop Agent use the pet as voice/body output."""

    def __init__(
        self,
        *,
        config_path: Path,
        root: Path,
        godot_host: str = "",
        godot_port: int = 0,
        presentation_route: str = "",
        presentation_backend: str = "",
        presentation_host: str = "",
        presentation_port: int = 0,
    ) -> None:
        self.root = root
        self.config_path = config_path
        self.config = _load_json_with_env(config_path)
        agent_config = self.config.get("AgentPlugin", {})
        godot_config = self.config.get("Godot", {})
        tts_provider = str(self.config.get("TTSProvider") or "volc").strip().lower()
        tts_config = self.config.get("VolcTTS", {})
        mimo_tts_config = self.config.get("MimoTTS", {})
        if not isinstance(agent_config, Mapping):
            agent_config = {}
        if not isinstance(godot_config, Mapping):
            godot_config = {}
        if not isinstance(tts_config, Mapping):
            tts_config = {}
        if not isinstance(mimo_tts_config, Mapping):
            mimo_tts_config = {}
        tts_config = _with_inherited_tts_config(dict(tts_config), root)
        mimo_tts_config = _with_inherited_mimo_tts_config(dict(mimo_tts_config), root)
        if tts_provider in {"mimo", "mimo_tts", "mimo_voiceclone", "mimo-voiceclone"}:
            self.tts_provider = "mimo_voiceclone"
            self.tts = MimoTTSClient(MimoTTSConfig.from_mapping(mimo_tts_config, root=root))
        else:
            self.tts_provider = "volc"
            self.tts = VolcTTSClient(VolcTTSConfig.from_mapping(tts_config, root=root))

        self.settings = AgentSpeakerSettings(
            host=str(agent_config.get("Host") or "127.0.0.1"),
            port=int(agent_config.get("Port") or 17863),
            require_auth=bool(agent_config.get("RequireAuth", False)),
            api_key=str(agent_config.get("ApiKey") or ""),
            max_text_chars=int(agent_config.get("MaxTextChars") or 1200),
            default_emotion=str(agent_config.get("DefaultEmotion") or "neutral"),
            default_gesture=str(agent_config.get("DefaultGesture") or "none"),
            default_posture=str(agent_config.get("DefaultPosture") or "stand"),
            default_priority=int(agent_config.get("DefaultPriority") or 60),
        )
        self.godot = create_presentation_client(
            root=root,
            config=self.config,
            route_id=presentation_route,
            backend=presentation_backend,
            host=presentation_host,
            port=presentation_port,
            legacy_godot_host=godot_host,
            legacy_godot_port=godot_port,
            default_timeout_sec=float(godot_config.get("TimeoutSec") or 0.25),
        )
        self._audio_lock = threading.Lock()
        self._last_request: dict[str, Any] = {}

    def serve_forever(self) -> None:
        server = ThreadingHTTPServer((self.settings.host, self.settings.port), _make_handler(self))
        server.app = self  # type: ignore[attr-defined]
        print(f"Agent speaker plugin listening: http://{self.settings.host}:{self.settings.port}")
        print(f"Config: {self.config_path}")
        try:
            server.serve_forever()
        finally:
            server.server_close()

    def capabilities(self) -> dict[str, Any]:
        return {
            "ok": True,
            "mode": "agent_speaker",
            "description": "External Agent controls text, pose and mouth. This program only speaks and performs.",
            "routes_are_separate": True,
            "endpoints": {
                "perform": "POST /pet/perform",
                "say": "POST /v1/say",
                "pose": "POST /v1/pose",
                "mouth": "POST /v1/mouth",
                "stop": "POST /v1/stop",
                "health": "GET /health",
            },
            "tts": {
                "provider": self.tts_provider,
                "enabled": self.tts.config.enabled,
                "ready": self.tts.config.is_ready(),
                "voice": _tts_voice_label(self.tts),
                "encoding": _tts_encoding_label(self.tts),
            },
            "godot": {
                "host": self.godot.host,
                "port": self.godot.port,
            },
            "presentation": {
                "backend": getattr(self.godot, "backend", "unknown"),
                "host": self.godot.host,
                "port": self.godot.port,
            },
            "allowed_pose_fields": [
                "state",
                "emotion",
                "gesture",
                "posture",
                "bubble_text",
                "mouth",
                "face",
                "emotion_intensity",
                "eye_style",
                "overlay_only",
                "priority",
                "duration_ms",
                "interruptible",
            ],
            "forbidden_pose_fields": [
                "animation_name",
                "bone_name",
                "raw_transform",
                "file_path",
                "script",
                "code",
            ],
            "pet_perform": {
                "default_endpoint": f"http://{self.settings.host}:{self.settings.port}/pet/perform",
                "phases": ["task_start", "searching", "operating", "waiting_user", "blocked", "done", "failed"],
                "emotions": ["neutral", "focused", "smug", "annoyed", "confused", "happy"],
                "poses": ["idle", "think", "talk", "point", "annoyed", "smug"],
            },
        }

    def perform(self, payload: Mapping[str, Any]) -> dict[str, Any]:
        try:
            command = PetPerformCommand.from_mapping(payload)
        except PetPerformCommandError as exc:
            return {"ok": False, "accepted": False, "error": str(exc)}

        say_payload = _perform_to_say_payload(command)
        say_payload["voice"] = bool(command.text and self.tts.config.is_ready())
        if command.text:
            say_result = self.say(say_payload)
        else:
            godot_payload = self._build_speaking_payload(say_payload, "")
            godot_payload["force_bubble"] = False
            godot_payload["clear_bubble"] = True
            sent = self.godot.send_pose(godot_payload)
            say_result = {"ok": sent, "sent_to_godot": sent, "voice": False}
        accepted = bool(say_result.get("ok", False))
        return {
            "ok": accepted,
            "accepted": accepted,
            "task_id": command.task_id,
            "phase": command.phase,
            "blocking": command.blocking,
            "sent_to_godot": bool(say_result.get("sent_to_godot", False)),
            "voice": bool(say_result.get("voice", False)),
            "tts": say_result.get("tts", {}),
        }

    def say(self, payload: Mapping[str, Any]) -> dict[str, Any]:
        forbidden = FORBIDDEN_FIELDS.intersection(payload.keys())
        if forbidden:
            return {
                "ok": False,
                "error": "forbidden_fields",
                "fields": sorted(forbidden),
            }
        raw_text = str(payload.get("text") or payload.get("message") or "").strip()
        text = sanitize_spoken_text(raw_text)
        if not text:
            return {"ok": False, "error": "empty_text"}
        if len(text) > self.settings.max_text_chars:
            return {"ok": False, "error": "text_too_long", "maxTextChars": self.settings.max_text_chars}
        request_id = str(payload.get("request_id") or uuid.uuid4())
        voice = bool(payload.get("voice", True))
        command = self._build_speaking_payload(payload, text)
        sent = self.godot.send_pose(command)
        self._last_request = {"id": request_id, "text": text, "at": time.time(), "voice": voice}
        result = {"ok": True, "request_id": request_id, "sent_to_godot": sent, "voice": voice}
        if voice:
            thread = threading.Thread(target=self._speak_worker, args=(request_id, text), daemon=True)
            thread.start()
            result["tts"] = {"queued": True, "ready": self.tts.config.is_ready()}
        return result

    def pose(self, payload: Mapping[str, Any]) -> dict[str, Any]:
        try:
            command = PoseCommand.from_mapping({"type": "pet_pose", **dict(payload)})
        except PoseCommandError as exc:
            return {"ok": False, "error": str(exc)}
        sent = self.godot.send_pose(command)
        return {"ok": sent, "sent_to_godot": sent, "payload": command.to_godot_payload()}

    def mouth(self, payload: Mapping[str, Any]) -> dict[str, Any]:
        forbidden = FORBIDDEN_FIELDS.intersection(payload.keys())
        if forbidden:
            return {
                "ok": False,
                "error": "forbidden_fields",
                "fields": sorted(forbidden),
            }
        command = {
            "type": "pet_pose",
            "state": "speaking" if bool(payload.get("audio_active", False)) else "idle",
            "emotion": "neutral",
            "gesture": "none",
            "posture": "stand",
            "mouth": "audio_volume",
            "mouth_open": _clamp_float(payload.get("mouth_open", 0.0), 0.0, 1.0),
            "audio_active": bool(payload.get("audio_active", False)),
            "overlay_only": True,
        }
        sent = self.godot.send_pose(command)
        return {"ok": sent, "sent_to_godot": sent}

    def stop(self) -> dict[str, Any]:
        with self._audio_lock:
            winsound.PlaySound(None, winsound.SND_PURGE)
        self.godot.send_pose(
            {
                "type": "pet_pose",
                "state": "idle",
                "emotion": "neutral",
                "gesture": "none",
                "posture": "stand",
                "mouth": "audio_volume",
                "audio_active": False,
                "overlay_only": True,
            }
        )
        return {"ok": True}

    def _build_speaking_payload(self, payload: Mapping[str, Any], text: str) -> dict[str, Any]:
        bubble_enabled = bool(payload.get("bubble", True))
        if "bubble_text" in payload:
            bubble_text = sanitize_spoken_text(str(payload.get("bubble_text") or ""))
        else:
            bubble_text = text if bubble_enabled else ""
        return {
            "type": "pet_pose",
            "state": str(payload.get("state") or "speaking"),
            "emotion": str(payload.get("emotion") or self.settings.default_emotion),
            "gesture": str(payload.get("gesture") or self.settings.default_gesture),
            "posture": str(payload.get("posture") or self.settings.default_posture),
            "bubble_text": bubble_text,
            "mouth": "audio_volume",
            "mouth_open": 0.0,
            "audio_active": False,
            "face": str(payload.get("face") or ""),
            "emotion_intensity": float(payload.get("emotion_intensity") or 0.0),
            "overlay_only": bool(payload.get("overlay_only", False)),
            "force_bubble": bool(payload.get("force_bubble", False)),
            "clear_bubble": bool(payload.get("clear_bubble", False)),
            "priority": int(payload.get("priority") or self.settings.default_priority),
            "duration_ms": int(payload.get("duration_ms") or 0),
            "interruptible": bool(payload.get("interruptible", True)),
        }

    def _speak_worker(self, request_id: str, text: str) -> None:
        try:
            audio_path = self.tts.synthesize_to_file(text)
        except (VolcTTSError, MimoTTSError) as exc:
            # Keep cloud/provider diagnostics out of the user-facing bubble.
            # The original text has already been displayed before TTS starts;
            # leaking raw HTTP payloads here makes the pet look like it is
            # "speaking logs" instead of performing as the Agent body.
            self.godot.send_pose(
                {
                    "type": "pet_pose",
                    "state": "speaking",
                    "emotion": "neutral",
                    "gesture": "none",
                    "posture": "stand",
                    "mouth": "audio_volume",
                    "audio_active": False,
                    "overlay_only": True,
                }
            )
            print(f"[agent_speaker] TTS failed request_id={request_id}: {exc}")
            return

        with self._audio_lock:
            self.godot.send_pose(
                {
                    "type": "pet_pose",
                    "state": "speaking",
                    "emotion": "neutral",
                    "gesture": "none",
                    "posture": "stand",
                    "mouth": "audio_volume",
                    "mouth_open": 0.7,
                    "audio_active": True,
                    "overlay_only": True,
                }
            )
            try:
                winsound.PlaySound(str(audio_path), winsound.SND_FILENAME)
            finally:
                self.godot.send_pose(
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
                    }
                )


class _AgentSpeakerHandler(BaseHTTPRequestHandler):
    server_version = "DesktopPetAgentSpeaker/0.1"

    def do_OPTIONS(self) -> None:
        self._send_json({"ok": True})

    def do_GET(self) -> None:
        if self.path in {"/health", "/v1/health"}:
            self._send_json({"ok": True, "mode": "agent_speaker", "time": time.time()})
            return
        if self.path in {"/", "/v1/capabilities"}:
            self._send_json(self._app.capabilities())
            return
        self._send_json({"ok": False, "error": "not_found"}, HTTPStatus.NOT_FOUND)

    def do_POST(self) -> None:
        if not self._authorized():
            self._send_json({"ok": False, "error": "unauthorized"}, HTTPStatus.UNAUTHORIZED)
            return
        payload = self._read_json()
        if payload is None:
            return
        if self.path == "/pet/perform":
            self._send_json(self._app.perform(payload))
            return
        if self.path == "/v1/say":
            self._send_json(self._app.say(payload))
            return
        if self.path == "/v1/pose":
            self._send_json(self._app.pose(payload))
            return
        if self.path == "/v1/mouth":
            self._send_json(self._app.mouth(payload))
            return
        if self.path in {"/v1/stop", "/api/stop", "/api/stop_voice_chat"}:
            self._send_json(self._app.stop())
            return
        self._send_json({"ok": False, "error": "not_found"}, HTTPStatus.NOT_FOUND)

    @property
    def _app(self) -> AgentSpeakerServer:
        return self.server.app  # type: ignore[attr-defined]

    def _authorized(self) -> bool:
        settings = self._app.settings
        if not settings.require_auth:
            return True
        header = self.headers.get("Authorization", "")
        token = header.removeprefix("Bearer").strip()
        return bool(settings.api_key and token == settings.api_key)

    def _read_json(self) -> Mapping[str, Any] | None:
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            length = 0
        raw = self.rfile.read(length) if length > 0 else b"{}"
        try:
            payload = json.loads(raw.decode("utf-8"))
        except json.JSONDecodeError:
            self._send_json({"ok": False, "error": "invalid_json"}, HTTPStatus.BAD_REQUEST)
            return None
        if not isinstance(payload, Mapping):
            self._send_json({"ok": False, "error": "json_root_must_be_object"}, HTTPStatus.BAD_REQUEST)
            return None
        return payload

    def _send_json(self, payload: Mapping[str, Any], status: HTTPStatus = HTTPStatus.OK) -> None:
        body = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self.send_response(int(status))
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Access-Control-Allow-Origin", "http://127.0.0.1")
        self.send_header("Access-Control-Allow-Methods", "GET,POST,OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type, Authorization")
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt: str, *args: Any) -> None:
        print("[agent_speaker] " + fmt % args)


def _make_handler(app: AgentSpeakerServer) -> type[_AgentSpeakerHandler]:
    class Handler(_AgentSpeakerHandler):
        pass

    Handler.app = app  # type: ignore[attr-defined]
    return Handler


def _load_json_with_env(path: Path) -> dict[str, Any]:
    text = path.read_text(encoding="utf-8")
    text = re.sub(r"\$\{([A-Z0-9_]+)\}", lambda match: os.getenv(match.group(1), ""), text)
    data = json.loads(text)
    if not isinstance(data, dict):
        raise ValueError(f"Config root must be a JSON object: {path}")
    return data


def _with_inherited_tts_config(tts_config: dict[str, Any], root: Path) -> dict[str, Any]:
    """Fill standalone Agent TTS config from existing local voice configs.

    The Agent plugin route is separate from StartVoiceChat, but it should not
    force users to duplicate the same cloned-voice credentials.
    """

    if _tts_config_ready(tts_config):
        return tts_config

    candidates = [
        root / "config" / "volc_start_voice_chat.local.json",
        root / "config" / "volc_traditional_voice_chat.local.json",
    ]
    for candidate in candidates:
        inherited = _extract_tts_config_from_voice_config(candidate)
        if not inherited:
            continue
        merged = dict(tts_config)
        for key, value in inherited.items():
            if value and not str(merged.get(key, "")).strip():
                merged[key] = value
        if _tts_config_ready(merged):
            return merged
        tts_config = merged
    return tts_config


def _with_inherited_mimo_tts_config(tts_config: dict[str, Any], root: Path) -> dict[str, Any]:
    """Fill standalone MiMo TTS config from the current MiMo voice route."""

    if _mimo_tts_config_ready(tts_config):
        return tts_config

    candidates = [
        root / "config" / "volc_traditional_voice_chat.local.json",
        root / "config" / "volc_traditional_mimo25.example.json",
    ]
    for candidate in candidates:
        inherited = _extract_mimo_tts_config_from_voice_config(candidate)
        if not inherited:
            continue
        merged = dict(tts_config)
        for key, value in inherited.items():
            if value and not str(merged.get(key, "")).strip():
                merged[key] = value
        if _mimo_tts_config_ready(merged):
            return merged
        tts_config = merged
    return tts_config


def _tts_config_ready(value: Mapping[str, Any]) -> bool:
    return bool(
        str(value.get("AppId", "")).strip()
        and str(value.get("AccessToken", "")).strip()
        and str(value.get("SpeakerId", "")).strip()
    )


def _mimo_tts_config_ready(value: Mapping[str, Any]) -> bool:
    return bool(
        str(value.get("APIKey", value.get("ApiKey", ""))).strip()
        and str(value.get("ReferenceAudioPath", "")).strip()
    )


def _extract_tts_config_from_voice_config(path: Path) -> dict[str, str]:
    if not path.exists():
        return {}
    try:
        data = _load_json_with_env(path)
    except (OSError, json.JSONDecodeError, ValueError):
        return {}

    start = data.get("StartVoiceChat", {})
    config = start.get("Config", {}) if isinstance(start, Mapping) else {}
    if not isinstance(config, Mapping):
        return {}

    s2s = config.get("S2SConfig", {})
    if isinstance(s2s, Mapping):
        provider = s2s.get("ProviderParams", {})
        if isinstance(provider, Mapping):
            app = provider.get("app", {})
            tts = provider.get("tts", {})
            if isinstance(app, Mapping) and isinstance(tts, Mapping):
                result = {
                    "AppId": str(app.get("appid", "")),
                    "AccessToken": str(app.get("token", "")),
                    "SpeakerId": str(tts.get("speaker", "")),
                }
                if _tts_config_ready(result):
                    return result

    tts_config = config.get("TTSConfig", {})
    if isinstance(tts_config, Mapping):
        provider = tts_config.get("ProviderParams", {})
        if isinstance(provider, Mapping):
            credential = provider.get("Credential", {})
            params_text = str(provider.get("VolcanoTTSParameters", "") or "")
            speaker = _extract_speaker_from_tts_parameters(params_text)
            if isinstance(credential, Mapping):
                result = {
                    "AppId": str(credential.get("AppId", "")),
                    "AccessToken": str(credential.get("Token", "") or credential.get("AccessToken", "")),
                    "SpeakerId": speaker,
                }
                if _tts_config_ready(result):
                    return result

    return {}


def _extract_mimo_tts_config_from_voice_config(path: Path) -> dict[str, str]:
    if not path.exists():
        return {}
    try:
        data = _load_json_with_env(path)
    except (OSError, json.JSONDecodeError, ValueError):
        return {}

    start = data.get("StartVoiceChat", {})
    config = start.get("Config", {}) if isinstance(start, Mapping) else {}
    if not isinstance(config, Mapping):
        return {}
    llm_config = config.get("LLMConfig", {})
    if not isinstance(llm_config, Mapping):
        return {}

    endpoint = str(llm_config.get("Url") or llm_config.get("URL") or llm_config.get("Endpoint") or "")
    api_key = str(llm_config.get("APIKey") or llm_config.get("ApiKey") or "")
    style = _persona_prompt(llm_config)
    result = {
        "Endpoint": endpoint,
        "APIKey": api_key,
        "StylePrompt": style,
    }
    return {key: value for key, value in result.items() if value}


def _persona_prompt(llm_config: Mapping[str, Any]) -> str:
    messages = llm_config.get("SystemMessages", llm_config.get("system_messages", []))
    if isinstance(messages, str):
        return messages.strip()
    if isinstance(messages, list):
        parts = [str(item).strip() for item in messages if str(item).strip()]
        return "\n".join(parts)
    return ""


def _extract_speaker_from_tts_parameters(params_text: str) -> str:
    if not params_text:
        return ""
    try:
        params = json.loads(params_text)
    except json.JSONDecodeError:
        return ""
    if not isinstance(params, Mapping):
        return ""
    req_params = params.get("req_params", {})
    if not isinstance(req_params, Mapping):
        return ""
    return str(req_params.get("speaker", ""))


def _tts_voice_label(tts: Any) -> str:
    config = getattr(tts, "config", None)
    if config is None:
        return ""
    if hasattr(config, "speaker_id"):
        return str(config.speaker_id)
    if hasattr(config, "reference_audio_path"):
        return str(config.reference_audio_path)
    return ""


def _tts_encoding_label(tts: Any) -> str:
    config = getattr(tts, "config", None)
    if config is None:
        return ""
    if hasattr(config, "encoding"):
        return str(config.encoding)
    if hasattr(config, "output_format"):
        return str(config.output_format)
    return ""


def _clamp_float(value: Any, minimum: float, maximum: float) -> float:
    try:
        number = float(value)
    except (TypeError, ValueError):
        number = minimum
    return max(minimum, min(maximum, number))


def _perform_to_say_payload(command: PetPerformCommand) -> dict[str, Any]:
    emotion, face, intensity = _map_perform_emotion(command.emotion)
    state, gesture = _map_perform_pose(command.pose, command.phase)
    if command.phase == "waiting_user":
        state = "listening"
    elif command.phase == "searching":
        state = "thinking"
        gesture = "think"
    elif command.phase == "done":
        state = "acting"
        gesture = "smug"
    elif command.phase == "failed":
        state = "acting"
        gesture = "shake_head"
    elif command.phase == "blocked":
        state = "thinking"
        gesture = "shake_head"

    return {
        "text": command.text,
        "voice": bool(command.text),
        "bubble": command.bubble,
        "force_bubble": command.bubble,
        "state": state,
        "emotion": emotion,
        "gesture": gesture,
        "posture": "stand",
        "face": face,
        "emotion_intensity": intensity,
        "priority": command.priority,
        "duration_ms": _phase_duration_ms(command.phase),
        "interruptible": command.phase not in {"waiting_user", "blocked"},
    }


def _map_perform_emotion(emotion: str) -> tuple[str, str, float]:
    mapping = {
        "neutral": ("neutral", "neutral", 0.35),
        "focused": ("neutral", "focused", 0.55),
        "smug": ("mocking", "smug", 0.72),
        "annoyed": ("angry", "annoyed", 0.68),
        "confused": ("confused", "confused", 0.62),
        "happy": ("happy", "happy", 0.72),
    }
    return mapping.get(emotion, ("neutral", "neutral", 0.35))


def _map_perform_pose(pose: str, phase: str) -> tuple[str, str]:
    mapping = {
        "idle": ("idle", "none"),
        "think": ("thinking", "think"),
        "talk": ("speaking", "none"),
        "point": ("acting", "point"),
        "annoyed": ("acting", "shake_head"),
        "smug": ("acting", "smug"),
    }
    if phase == "task_start" and pose == "idle":
        return ("speaking", "none")
    return mapping.get(pose, ("idle", "none"))


def _phase_duration_ms(phase: str) -> int:
    # Agent progress bubbles are status lines, not transient subtitles. Keep
    # them visible until the next meaningful line replaces them; this removes
    # the need for noisy heartbeat repeats just to keep the bubble alive.
    return 0
