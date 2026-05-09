from __future__ import annotations

import base64
import json
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Mapping

import requests

from src.spoken_text_sanitizer import sanitize_spoken_text


class VolcTTSError(RuntimeError):
    """Raised when the standalone Volc cloned-voice TTS request fails."""


@dataclass(frozen=True)
class VolcTTSConfig:
    enabled: bool
    endpoint: str
    app_id: str
    access_token: str
    cluster: str
    speaker_id: str
    encoding: str = "wav"
    speed_ratio: float = 1.0
    volume_ratio: float = 1.0
    pitch_ratio: float = 1.0
    timeout_sec: float = 20.0
    cache_dir: Path = Path(".tmp/agent_tts_cache")

    @classmethod
    def from_mapping(cls, value: Mapping[str, Any], *, root: Path) -> "VolcTTSConfig":
        cache_dir = _resolve_path(str(value.get("CacheDir") or ".tmp/agent_tts_cache"), root)
        return cls(
            enabled=bool(value.get("Enabled", True)),
            endpoint=str(value.get("Endpoint") or "https://openspeech.bytedance.com/api/v1/tts"),
            app_id=str(value.get("AppId") or ""),
            access_token=str(value.get("AccessToken") or ""),
            cluster=str(value.get("Cluster") or "volcano_icl"),
            speaker_id=str(value.get("SpeakerId") or ""),
            encoding=str(value.get("Encoding") or "wav").lower(),
            speed_ratio=float(value.get("SpeedRatio", 1.0)),
            volume_ratio=float(value.get("VolumeRatio", 1.0)),
            pitch_ratio=float(value.get("PitchRatio", 1.0)),
            timeout_sec=float(value.get("TimeoutSec", 20.0)),
            cache_dir=cache_dir,
        )

    def is_ready(self) -> bool:
        return self.enabled and bool(self.endpoint and self.app_id and self.access_token and self.speaker_id)


class VolcTTSClient:
    """Direct standalone TTS client used only by Agent plugin mode.

    This path is intentionally separate from StartVoiceChat. It does not create
    an RTC room, does not call an LLM, and does not listen to the user.
    """

    def __init__(self, config: VolcTTSConfig) -> None:
        self.config = config

    def synthesize_to_file(self, text: str) -> Path:
        if not self.config.is_ready():
            raise VolcTTSError("VolcTTS is not configured; fill VolcTTS AppId, AccessToken and SpeakerId.")
        clean_text = sanitize_spoken_text(text)
        if not clean_text:
            raise VolcTTSError("TTS text is empty.")

        req_id = str(uuid.uuid4())
        body = {
            "app": {
                "appid": self.config.app_id,
                "token": self.config.access_token,
                "cluster": self.config.cluster,
            },
            "user": {
                "uid": "desktop_pet_agent_plugin",
            },
            "audio": {
                "voice_type": self.config.speaker_id,
                "encoding": self.config.encoding,
                "speed_ratio": self.config.speed_ratio,
                "volume_ratio": self.config.volume_ratio,
                "pitch_ratio": self.config.pitch_ratio,
            },
            "request": {
                "reqid": req_id,
                "text": clean_text,
                "text_type": "plain",
                "operation": "query",
            },
        }
        response = requests.post(
            self.config.endpoint,
            data=json.dumps(body, ensure_ascii=False).encode("utf-8"),
            headers={
                "Authorization": f"Bearer;{self.config.access_token}",
                "Content-Type": "application/json; charset=utf-8",
            },
            timeout=self.config.timeout_sec,
        )
        try:
            payload = response.json()
        except ValueError as exc:
            raise VolcTTSError(f"TTS response is not JSON: http_status={response.status_code}") from exc
        if response.status_code >= 400:
            raise VolcTTSError(f"TTS http error {response.status_code}: {payload}")
        if int(payload.get("code", 0)) != 3000:
            raise VolcTTSError(f"TTS failed: {payload}")
        audio_b64 = payload.get("data")
        if not isinstance(audio_b64, str) or not audio_b64:
            raise VolcTTSError(f"TTS returned no audio data: {payload}")

        audio_bytes = base64.b64decode(audio_b64)
        self.config.cache_dir.mkdir(parents=True, exist_ok=True)
        output_path = self.config.cache_dir / f"{req_id}.{self.config.encoding}"
        output_path.write_bytes(audio_bytes)
        return output_path


def _resolve_path(value: str, root: Path) -> Path:
    if value.startswith("res://"):
        return root / value.removeprefix("res://")
    if value.startswith("user://"):
        return root / ".tmp" / value.removeprefix("user://")
    path = Path(value)
    if path.is_absolute():
        return path
    return root / path
