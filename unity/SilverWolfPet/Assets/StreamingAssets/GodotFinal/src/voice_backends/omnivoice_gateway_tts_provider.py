from __future__ import annotations

import hashlib
import json
import os
import queue
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import wave
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Iterable, Mapping


PROJECT_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_GATEWAY_URL = "http://117.50.176.5:9000"
DEFAULT_CACHE_DIR = PROJECT_ROOT / ".tmp" / "cache" / "tts"

TTSState = str
StateCallback = Callable[[TTSState, dict[str, Any]], None]
SegmentCallback = Callable[[dict[str, Any]], None]


class OmniVoiceGatewayTTSError(RuntimeError):
    """Raised when the OmniVoice gateway request, download, or playback fails."""


@dataclass(slots=True)
class OmniVoiceGatewayTTSConfig:
    gateway_url: str = DEFAULT_GATEWAY_URL
    api_token: str = ""
    voice_id: str = "role_001"
    lang: str = "zh"
    pseudo_stream: bool = True
    playback_enabled: bool = True
    cache_dir: Path = DEFAULT_CACHE_DIR
    speed: float = 1.0
    pause_multi: float = 0.6
    request_timeout_seconds: int = 900
    download_timeout_seconds: int = 300

    @classmethod
    def from_mapping(cls, value: Mapping[str, Any] | None, *, root: Path = PROJECT_ROOT) -> "OmniVoiceGatewayTTSConfig":
        data = value if isinstance(value, Mapping) else {}
        cache_dir = _resolve_path(
            _first_text(_pick(data, "CacheDir", "cache_dir"), str(DEFAULT_CACHE_DIR)),
            root,
        )
        return cls(
            gateway_url=normalize_url(_first_text(_pick(data, "GatewayURL", "GatewayUrl", "gateway_url"), DEFAULT_GATEWAY_URL)),
            api_token=_first_text(
                _pick(data, "APIToken", "ApiToken", "api_token", "Token", "token"),
                os.getenv("OMNIVOICE_API_TOKEN", ""),
                os.getenv("API_TOKEN", ""),
            ),
            voice_id=_first_text(_pick(data, "VoiceId", "VoiceID", "voice_id", "Voice", "voice"), "role_001"),
            lang=_first_text(_pick(data, "Lang", "Language", "lang", "language"), "zh"),
            pseudo_stream=_bool(_pick(data, "PseudoStream", "pseudo_stream"), True),
            playback_enabled=_bool(_pick(data, "PlaybackEnabled", "playback_enabled"), True),
            cache_dir=cache_dir,
            speed=_float(_pick(data, "Speed", "speed"), 1.0),
            pause_multi=_float(_pick(data, "PauseMulti", "pause_multi"), 0.6),
            request_timeout_seconds=max(1, int(_float(_pick(data, "RequestTimeoutSeconds", "request_timeout_seconds"), 900))),
            download_timeout_seconds=max(1, int(_float(_pick(data, "DownloadTimeoutSeconds", "download_timeout_seconds"), 300))),
        )

    @classmethod
    def from_project_config(
        cls,
        config: Mapping[str, Any],
        *,
        root: Path = PROJECT_ROOT,
    ) -> "OmniVoiceGatewayTTSConfig":
        return cls.from_mapping(_voice_output_mapping(config), root=root)

    def is_ready(self) -> bool:
        return bool(self.gateway_url and self.api_token and self.voice_id)


@dataclass(slots=True)
class PlaybackStats:
    job_id: str
    text_hash: str
    text_chars: int
    downloaded_segments: int = 0
    played_segments: int = 0
    first_segment_elapsed_seconds: float | None = None
    total_audio_duration_seconds: float = 0.0
    local_paths: list[str] = field(default_factory=list)
    status: str = "created"
    error: str = ""


class OmniVoiceGatewayTTSProvider:
    """Pseudo-streaming OmniVoice player for local desktop output.

    The provider owns a stream thread and a playback queue. Calling stop() marks
    the active job cancelled, purges queued audio, and stops current playback.
    Late segment_ready events from an old job are ignored.
    """

    def __init__(
        self,
        config: OmniVoiceGatewayTTSConfig,
        *,
        on_state_change: StateCallback | None = None,
        on_segment_ready: SegmentCallback | None = None,
    ) -> None:
        self.config = config
        self.config.cache_dir.mkdir(parents=True, exist_ok=True)
        self.on_state_change = on_state_change
        self.on_segment_ready = on_segment_ready

        self._queue: queue.Queue[tuple[str, str] | None] = queue.Queue()
        self._lock = threading.RLock()
        self._cancel_event = threading.Event()
        self._state: TTSState = "idle"
        self._current_job_id = ""
        self._playing = False
        self._stream_done = True
        self._last_stats: PlaybackStats | None = None
        self._stream_thread: threading.Thread | None = None
        self._playback_thread = threading.Thread(
            target=self._playback_loop,
            name="omnivoice-tts-playback",
            daemon=True,
        )
        self._playback_thread.start()

    @property
    def last_stats(self) -> PlaybackStats | None:
        with self._lock:
            return self._last_stats

    @property
    def state(self) -> TTSState:
        with self._lock:
            return self._state

    def speak(self, text: str) -> str:
        clean_text = str(text or "").strip()
        if not clean_text:
            raise OmniVoiceGatewayTTSError("text is empty")
        if not self.config.api_token:
            raise OmniVoiceGatewayTTSError("api_token is empty")

        self.stop()
        job_id = uuid.uuid4().hex
        stats = PlaybackStats(job_id=job_id, text_hash=text_hash(clean_text), text_chars=len(clean_text))
        with self._lock:
            self._current_job_id = job_id
            self._last_stats = stats
            self._cancel_event.clear()
            self._stream_done = False
            self._set_state_locked(
                "thinking",
                {"job_id": job_id, "text_chars": len(clean_text), "text_hash": stats.text_hash},
            )

        self._stream_thread = threading.Thread(
            target=self._stream_worker,
            args=(job_id, clean_text),
            name=f"omnivoice-tts-stream-{job_id[:8]}",
            daemon=True,
        )
        self._stream_thread.start()
        return job_id

    def stop(self) -> None:
        with self._lock:
            if self._current_job_id:
                self._cancel_event.set()
            self._stream_done = True
            self._playing = False
            self.clear_queue()
            self._stop_audio()
            self._set_state_locked("idle", {"reason": "stopped", "job_id": self._current_job_id})

    def clear_queue(self) -> None:
        while True:
            try:
                self._queue.get_nowait()
            except queue.Empty:
                return
            self._queue.task_done()

    def is_playing(self) -> bool:
        with self._lock:
            return self._playing

    def handle_interrupt(self, reason: str = "interrupt") -> None:
        self.stop()
        with self._lock:
            self._set_state_locked("idle", {"reason": reason, "job_id": self._current_job_id})

    def _stream_worker(self, job_id: str, text: str) -> None:
        started = time.perf_counter()
        try:
            if self.config.pseudo_stream:
                for event, data in self._post_stream(text):
                    if not self._is_current_job(job_id) or self._cancel_event.is_set():
                        continue
                    if event == "segment_ready":
                        local_path = self._download_segment(data, job_id)
                        self._handle_segment(job_id, data, local_path, time.perf_counter() - started)
                    elif event == "error":
                        raise OmniVoiceGatewayTTSError(str(data.get("error") or data))
            else:
                data = self._post_json(text)
                local_path = self._download_segment(data, job_id)
                self._handle_segment(job_id, data, local_path, time.perf_counter() - started)
        except Exception as exc:  # noqa: BLE001
            if self._is_current_job(job_id) and not self._cancel_event.is_set():
                with self._lock:
                    if self._last_stats and self._last_stats.job_id == job_id:
                        self._last_stats.status = "error"
                        self._last_stats.error = str(exc)
                    self._stream_done = True
                    self._set_state_locked("error", {"job_id": job_id, "error": str(exc)})
        finally:
            if self._is_current_job(job_id):
                with self._lock:
                    self._stream_done = True
                    self._maybe_idle_locked(job_id)

    def _handle_segment(self, job_id: str, data: dict[str, Any], local_path: Path, first_elapsed: float) -> None:
        duration = _float(data.get("audio_duration_seconds"), 0.0)
        segment_info = {
            "job_id": job_id,
            "index": int(_float(data.get("index"), 0.0)),
            "segment_count": int(_float(data.get("segment_count"), 1.0)),
            "local_audio_path": str(local_path),
            "audio_duration_seconds": duration,
            "elapsed_seconds": data.get("elapsed_seconds"),
            "rtf": data.get("rtf"),
            "worker_url": data.get("worker_url"),
            "filename": data.get("filename"),
        }
        with self._lock:
            if not self._is_current_job(job_id) or self._cancel_event.is_set():
                return
            if self._last_stats and self._last_stats.job_id == job_id:
                self._last_stats.downloaded_segments += 1
                self._last_stats.local_paths.append(str(local_path))
                self._last_stats.total_audio_duration_seconds += max(duration, 0.0)
                if self._last_stats.first_segment_elapsed_seconds is None:
                    self._last_stats.first_segment_elapsed_seconds = first_elapsed
        if self.on_segment_ready:
            self.on_segment_ready(segment_info)
        self._queue.put((job_id, str(local_path)))

    def _playback_loop(self) -> None:
        while True:
            item = self._queue.get()
            try:
                if item is None:
                    continue
                job_id, path = item
                if not self._is_current_job(job_id) or self._cancel_event.is_set():
                    continue
                with self._lock:
                    self._playing = True
                    self._set_state_locked("speaking", {"job_id": job_id, "path": path})
                if self.config.playback_enabled:
                    self._play_audio(Path(path))
                if self._is_current_job(job_id):
                    with self._lock:
                        if self._last_stats and self._last_stats.job_id == job_id:
                            self._last_stats.played_segments += 1
                        self._playing = False
                        self._maybe_idle_locked(job_id)
            finally:
                self._queue.task_done()

    def _maybe_idle_locked(self, job_id: str) -> None:
        if self._is_current_job(job_id) and self._stream_done and not self._playing and self._queue.empty():
            if self._last_stats and self._last_stats.job_id == job_id and not self._last_stats.error:
                self._last_stats.status = "done"
            self._set_state_locked("idle", {"job_id": job_id})

    def _post_stream(self, text: str) -> Iterable[tuple[str, dict[str, Any]]]:
        payload = self._request_payload(text)
        request = urllib.request.Request(
            _join_url(self.config.gateway_url, "/v1/tts/stream"),
            data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
            headers={
                "Authorization": f"Bearer {self.config.api_token}",
                "Content-Type": "application/json",
                "Accept": "text/event-stream",
            },
            method="POST",
        )
        try:
            with urllib.request.urlopen(request, timeout=self.config.request_timeout_seconds) as response:
                event = ""
                lines: list[str] = []
                for raw_line in response:
                    line = raw_line.decode("utf-8", errors="replace").rstrip("\r\n")
                    if not line:
                        if event and lines:
                            yield event, json.loads("\n".join(lines))
                        event = ""
                        lines = []
                        continue
                    if line.startswith("event:"):
                        event = line.split(":", 1)[1].strip()
                    elif line.startswith("data:"):
                        lines.append(line.split(":", 1)[1].strip())
        except urllib.error.HTTPError as exc:
            raise OmniVoiceGatewayTTSError(_http_error_message(exc)) from exc
        except (OSError, urllib.error.URLError) as exc:
            raise OmniVoiceGatewayTTSError(f"Gateway stream request failed: {exc}") from exc

    def _post_json(self, text: str) -> dict[str, Any]:
        request = urllib.request.Request(
            _join_url(self.config.gateway_url, "/v1/tts/json"),
            data=json.dumps(self._request_payload(text), ensure_ascii=False).encode("utf-8"),
            headers={
                "Authorization": f"Bearer {self.config.api_token}",
                "Content-Type": "application/json",
                "Accept": "application/json",
            },
            method="POST",
        )
        try:
            with urllib.request.urlopen(request, timeout=self.config.request_timeout_seconds) as response:
                return json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            raise OmniVoiceGatewayTTSError(_http_error_message(exc)) from exc
        except (OSError, urllib.error.URLError) as exc:
            raise OmniVoiceGatewayTTSError(f"Gateway json request failed: {exc}") from exc

    def _request_payload(self, text: str) -> dict[str, Any]:
        return {
            "text": text,
            "voice": self.config.voice_id,
            "lang": self.config.lang,
            "speed": self.config.speed,
            "pause_multi": self.config.pause_multi,
        }

    def _download_segment(self, data: dict[str, Any], job_id: str) -> Path:
        audio_url = str(data.get("audio_url") or "").strip()
        if not audio_url:
            raise OmniVoiceGatewayTTSError(f"missing audio_url in gateway response: {data!r}")
        filename = Path(str(data.get("filename") or Path(urllib.parse.urlparse(audio_url).path).name or "segment.wav")).name
        destination = self.config.cache_dir / f"{job_id[:8]}-{int(_float(data.get('index'), 0.0)):03d}-{filename}"
        request = urllib.request.Request(
            _join_url(self.config.gateway_url, audio_url),
            headers={"Authorization": f"Bearer {self.config.api_token}"},
            method="GET",
        )
        try:
            with urllib.request.urlopen(request, timeout=self.config.download_timeout_seconds) as response:
                destination.write_bytes(response.read())
        except urllib.error.HTTPError as exc:
            raise OmniVoiceGatewayTTSError(_http_error_message(exc)) from exc
        except (OSError, urllib.error.URLError) as exc:
            raise OmniVoiceGatewayTTSError(f"Gateway audio download failed: {exc}") from exc
        return destination

    def _play_audio(self, path: Path) -> None:
        if os.name == "nt":
            import winsound

            duration = wav_duration_seconds(path) or 0.0
            winsound.PlaySound(str(path), winsound.SND_FILENAME | winsound.SND_ASYNC)
            deadline = time.monotonic() + max(duration, 0.2)
            while time.monotonic() < deadline:
                if self._cancel_event.is_set():
                    break
                time.sleep(0.03)
            winsound.PlaySound(None, winsound.SND_PURGE)
            return

        import subprocess

        player = None
        for command in (["ffplay", "-nodisp", "-autoexit", "-loglevel", "quiet", str(path)], ["aplay", str(path)]):
            try:
                player = subprocess.Popen(command)
                break
            except OSError:
                continue
        if player is None:
            return
        while player.poll() is None:
            if self._cancel_event.is_set():
                player.terminate()
                break
            time.sleep(0.05)

    def _stop_audio(self) -> None:
        if os.name != "nt":
            return
        try:
            import winsound

            winsound.PlaySound(None, winsound.SND_PURGE)
        except Exception:
            pass

    def _is_current_job(self, job_id: str) -> bool:
        with self._lock:
            return self._current_job_id == job_id

    def _set_state_locked(self, state: TTSState, payload: dict[str, Any] | None = None) -> None:
        self._state = state
        if self.on_state_change:
            self.on_state_change(state, payload or {})


def normalize_url(value: str) -> str:
    url = (value or DEFAULT_GATEWAY_URL).strip()
    if not url.startswith(("http://", "https://")):
        url = "http://" + url
    return url.rstrip("/")


def wav_duration_seconds(path: Path) -> float | None:
    try:
        with wave.open(str(path), "rb") as audio:
            rate = audio.getframerate()
            if rate <= 0:
                return None
            return audio.getnframes() / float(rate)
    except Exception:
        return None


def text_hash(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def _voice_output_mapping(config: Mapping[str, Any]) -> Mapping[str, Any]:
    for key in ("VoiceOutput", "voice_output", "voiceOutput"):
        value = config.get(key)
        if isinstance(value, Mapping):
            return value
    return {}


def _join_url(base: str, path: str) -> str:
    return urllib.parse.urljoin(base.rstrip("/") + "/", path.lstrip("/"))


def _resolve_path(value: str, root: Path) -> Path:
    path = Path(value)
    if path.is_absolute():
        return path
    return root / path


def _pick(data: Mapping[str, Any], *keys: str) -> Any:
    for key in keys:
        if key in data:
            return data[key]
    return None


def _first_text(*values: Any) -> str:
    for value in values:
        if value is not None and str(value).strip():
            return str(value).strip()
    return ""


def _bool(value: Any, default: bool) -> bool:
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    return str(value).strip().lower() not in {"0", "false", "no", "off", ""}


def _float(value: Any, default: float) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


def _http_error_message(exc: urllib.error.HTTPError) -> str:
    body = exc.read().decode("utf-8", errors="replace")
    if exc.code == 401:
        return "HTTP 401: OmniVoice API token is invalid"
    if exc.code in {502, 503, 504}:
        return f"HTTP {exc.code}: Gateway unavailable; check the 9000 port and gateway service"
    return f"HTTP {exc.code}: {body}"
