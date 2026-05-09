from __future__ import annotations

import base64
import json
import mimetypes
import os
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Mapping

import requests

from src.spoken_text_sanitizer import sanitize_spoken_text


class MimoTTSError(RuntimeError):
    """Raised when the standalone MiMo TTS request fails."""


@dataclass(frozen=True)
class MimoTTSConfig:
    enabled: bool
    endpoint: str
    api_key: str
    model: str
    reference_audio_path: Path
    style_prompt: str
    output_format: str = "wav"
    timeout_sec: float = 120.0
    cache_dir: Path = Path(".tmp/mimo_tts_cache")

    @classmethod
    def from_mapping(cls, value: Mapping[str, Any], *, root: Path) -> "MimoTTSConfig":
        endpoint = str(value.get("Endpoint") or value.get("Url") or "https://token-plan-cn.xiaomimimo.com/v1/chat/completions")
        reference_audio = _resolve_path(str(value.get("ReferenceAudioPath") or ""), root)
        cache_dir = _resolve_path(str(value.get("CacheDir") or ".tmp/mimo_tts_cache"), root)
        return cls(
            enabled=bool(value.get("Enabled", True)),
            endpoint=_completion_url(endpoint),
            api_key=str(value.get("APIKey") or value.get("ApiKey") or os.environ.get("MIMO_API_KEY", "")),
            model=str(value.get("Model") or "mimo-v2.5-tts-voiceclone"),
            reference_audio_path=reference_audio,
            style_prompt=str(value.get("StylePrompt") or ""),
            output_format=str(value.get("OutputFormat") or "wav").lower(),
            timeout_sec=float(value.get("TimeoutSec", 120.0)),
            cache_dir=cache_dir,
        )

    def is_ready(self) -> bool:
        return self.enabled and bool(self.endpoint and self.api_key and self.reference_audio_path.exists())


class MimoTTSClient:
    """Standalone MiMo voice-clone TTS client used by Agent plugin mode."""

    def __init__(self, config: MimoTTSConfig) -> None:
        self.config = config

    def synthesize_to_file(self, text: str) -> Path:
        if not self.config.is_ready():
            raise MimoTTSError("MiMoTTS is not configured; fill APIKey and ReferenceAudioPath.")
        clean_text = sanitize_spoken_text(text)
        if not clean_text:
            raise MimoTTSError("TTS text is empty.")

        body = {
            "model": self.config.model,
            "messages": [
                {"role": "user", "content": self.config.style_prompt},
                {"role": "assistant", "content": clean_text},
            ],
            "audio": {
                "format": self.config.output_format,
                "voice": _reference_audio_data_url(self.config.reference_audio_path),
            },
        }
        response = requests.post(
            self.config.endpoint,
            headers={
                "Authorization": f"Bearer {self.config.api_key}",
                "api-key": self.config.api_key,
                "Content-Type": "application/json",
            },
            json=body,
            timeout=self.config.timeout_sec,
        )
        try:
            payload = response.json()
        except ValueError as exc:
            raise MimoTTSError(f"MiMo TTS response is not JSON: http_status={response.status_code}") from exc
        if response.status_code >= 400:
            raise MimoTTSError(f"MiMo TTS http error {response.status_code}: {_compact_error(payload)}")

        audio_b64 = _extract_audio_data(payload)
        if not audio_b64:
            raise MimoTTSError(f"MiMo TTS returned no audio data: {_compact_error(payload)}")

        audio_bytes = base64.b64decode(audio_b64)
        self.config.cache_dir.mkdir(parents=True, exist_ok=True)
        output_path = self.config.cache_dir / f"{uuid.uuid4()}.{self.config.output_format}"
        output_path.write_bytes(audio_bytes)
        return output_path


def _completion_url(value: str) -> str:
    raw = value.strip() or "https://token-plan-cn.xiaomimimo.com/v1/chat/completions"
    if raw.rstrip("/").endswith("/chat/completions"):
        return raw
    return raw.rstrip("/") + "/chat/completions"


def _reference_audio_data_url(path: Path) -> str:
    mime_type = mimetypes.guess_type(str(path))[0] or ""
    if path.suffix.lower() == ".wav":
        mime_type = "audio/wav"
    elif path.suffix.lower() == ".mp3":
        mime_type = "audio/mpeg"
    if mime_type not in {"audio/wav", "audio/mpeg", "audio/mp3"}:
        raise MimoTTSError(f"Unsupported MiMo reference audio type: {path.suffix}; use wav or mp3.")
    encoded = base64.b64encode(path.read_bytes()).decode("ascii")
    if len(encoded.encode("ascii")) > 10 * 1024 * 1024:
        raise MimoTTSError("MiMo reference audio is too large after base64 encoding; limit is 10 MB.")
    return f"data:{mime_type};base64,{encoded}"


def _extract_audio_data(payload: Mapping[str, Any]) -> str:
    try:
        audio = payload["choices"][0]["message"]["audio"]
        return audio["data"] if isinstance(audio, Mapping) and isinstance(audio.get("data"), str) else ""
    except (KeyError, IndexError, TypeError):
        return ""


def _compact_error(payload: Any) -> str:
    try:
        return json.dumps(_redact_audio(payload), ensure_ascii=False, separators=(",", ":"))[:800]
    except Exception:
        return str(payload)[:800]


def _redact_audio(value: Any) -> Any:
    if isinstance(value, Mapping):
        result: dict[str, Any] = {}
        for key, item in value.items():
            if str(key).lower() in {"data", "voice"} and isinstance(item, str) and len(item) > 200:
                result[str(key)] = f"<redacted:{len(item)} chars>"
            else:
                result[str(key)] = _redact_audio(item)
        return result
    if isinstance(value, list):
        return [_redact_audio(item) for item in value]
    return value


def _resolve_path(value: str, root: Path) -> Path:
    if value.startswith("res://"):
        return root / value.removeprefix("res://")
    if value.startswith("user://"):
        return root / ".tmp" / value.removeprefix("user://")
    path = Path(value)
    if path.is_absolute():
        return path
    return root / path
