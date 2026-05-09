from __future__ import annotations

import hashlib
import http.client
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
from typing import Any, Callable, Mapping


PROJECT_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_CACHE_DIR = PROJECT_ROOT / ".tmp" / "cache" / "gpt_sovits"
DEFAULT_BASE_URLS = ("http://127.0.0.1:19890",)

TTSState = str
StateCallback = Callable[[TTSState, dict[str, Any]], None]
SegmentCallback = Callable[[dict[str, Any]], None]


class GPTSoVITSDirectTTSError(RuntimeError):
    """Raised when the direct GPT-SoVITS stream, decode, or playback fails."""


@dataclass(slots=True)
class GPTSoVITSDirectTTSConfig:
    base_urls: tuple[str, ...] = DEFAULT_BASE_URLS
    ref_audio_path: str = ""
    prompt_text: str = ""
    text_lang: str = "zh"
    prompt_lang: str = "zh"
    cache_dir: Path = DEFAULT_CACHE_DIR
    playback_enabled: bool = True
    playback_backend: str = "sounddevice"
    request_timeout_seconds: int = 120
    media_type: str = "raw"
    sample_rate: int = 32000
    channels: int = 1
    sample_width: int = 2
    read_chunk_bytes: int = 8192
    min_segment_bytes: int = 16384
    streaming_mode: int = 2
    fragment_interval: float = 0.3
    batch_size: int = 1
    batch_threshold: float = 0.75
    split_bucket: bool = True
    parallel_infer: bool = True
    top_k: int = 15
    top_p: float = 1.0
    temperature: float = 1.0
    speed_factor: float = 1.0
    repetition_penalty: float = 1.35
    sample_steps: int = 32
    super_sampling: bool = False
    overlap_length: int = 2
    min_chunk_length: int = 16
    text_split_method: str = "cut5"

    @classmethod
    def from_mapping(cls, value: Mapping[str, Any] | None, *, root: Path = PROJECT_ROOT) -> "GPTSoVITSDirectTTSConfig":
        data = value if isinstance(value, Mapping) else {}
        base_urls = _urls_from_mapping(data)
        cache_dir = _resolve_path(
            _first_text(_pick(data, "CacheDir", "cache_dir"), str(DEFAULT_CACHE_DIR)),
            root,
        )
        return cls(
            base_urls=base_urls,
            ref_audio_path=_first_text(_pick(data, "RefAudioPath", "ref_audio_path"), ""),
            prompt_text=_first_text(_pick(data, "PromptText", "prompt_text"), ""),
            text_lang=_first_text(_pick(data, "TextLang", "text_lang", "Lang", "lang"), "zh"),
            prompt_lang=_first_text(_pick(data, "PromptLang", "prompt_lang"), "zh"),
            cache_dir=cache_dir,
            playback_enabled=_bool(_pick(data, "PlaybackEnabled", "playback_enabled"), True),
            playback_backend=_first_text(_pick(data, "PlaybackBackend", "playback_backend"), "sounddevice").lower(),
            request_timeout_seconds=max(1, int(_float(_pick(data, "RequestTimeoutSeconds", "request_timeout_seconds"), 120))),
            media_type=_first_text(_pick(data, "MediaType", "media_type"), "raw").lower(),
            sample_rate=max(8000, int(_float(_pick(data, "SampleRate", "sample_rate"), 32000))),
            channels=max(1, int(_float(_pick(data, "Channels", "channels"), 1))),
            sample_width=max(1, int(_float(_pick(data, "SampleWidth", "sample_width"), 2))),
            read_chunk_bytes=max(1024, int(_float(_pick(data, "ReadChunkBytes", "read_chunk_bytes"), 8192))),
            min_segment_bytes=max(1024, int(_float(_pick(data, "MinSegmentBytes", "min_segment_bytes"), 16384))),
            streaming_mode=max(0, int(_float(_pick(data, "StreamingMode", "streaming_mode"), 2))),
            fragment_interval=max(0.05, _float(_pick(data, "FragmentInterval", "fragment_interval"), 0.3)),
            batch_size=max(1, int(_float(_pick(data, "BatchSize", "batch_size"), 1))),
            batch_threshold=_float(_pick(data, "BatchThreshold", "batch_threshold"), 0.75),
            split_bucket=_bool(_pick(data, "SplitBucket", "split_bucket"), True),
            parallel_infer=_bool(_pick(data, "ParallelInfer", "parallel_infer"), True),
            top_k=max(1, int(_float(_pick(data, "TopK", "top_k"), 15))),
            top_p=_float(_pick(data, "TopP", "top_p"), 1.0),
            temperature=_float(_pick(data, "Temperature", "temperature"), 1.0),
            speed_factor=max(0.2, _float(_pick(data, "SpeedFactor", "speed_factor", "Speed", "speed"), 1.0)),
            repetition_penalty=_float(_pick(data, "RepetitionPenalty", "repetition_penalty"), 1.35),
            sample_steps=max(1, int(_float(_pick(data, "SampleSteps", "sample_steps"), 32))),
            super_sampling=_bool(_pick(data, "SuperSampling", "super_sampling"), False),
            overlap_length=max(0, int(_float(_pick(data, "OverlapLength", "overlap_length"), 2))),
            min_chunk_length=max(1, int(_float(_pick(data, "MinChunkLength", "min_chunk_length"), 16))),
            text_split_method=_first_text(_pick(data, "TextSplitMethod", "text_split_method"), "cut5"),
        )

    def is_ready(self) -> bool:
        return bool(self.base_urls and self.ref_audio_path)

    @property
    def primary_url(self) -> str:
        return self.base_urls[0] if self.base_urls else ""


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


class _SoundDevicePCMStreamPlayer:
    """Continuous raw PCM playback queue backed by PortAudio/sounddevice."""

    def __init__(self, *, sample_rate: int, channels: int, sample_width: int) -> None:
        if sample_width != 2:
            raise GPTSoVITSDirectTTSError("sounddevice playback supports 16-bit PCM only")
        self.sample_rate = sample_rate
        self.channels = channels
        self.sample_width = sample_width
        self._queue: queue.Queue[bytes | None] = queue.Queue()
        self._stop_event = threading.Event()
        self._done_event = threading.Event()
        self._thread = threading.Thread(target=self._run, name="gpt-sovits-sounddevice-playback", daemon=True)
        self.error = ""

    def start(self) -> None:
        self._thread.start()

    def write(self, raw_audio: bytes) -> None:
        if raw_audio and not self._stop_event.is_set():
            self._queue.put(_even_pcm_bytes(raw_audio))

    def finish(self) -> None:
        self._queue.put(None)

    def stop(self) -> None:
        self._stop_event.set()
        self._drain_queue()
        self._queue.put(None)
        self._thread.join(timeout=2.0)

    def wait(self, *, cancel_event: threading.Event, timeout_seconds: float) -> bool:
        deadline = time.monotonic() + max(1.0, timeout_seconds)
        while not self._done_event.is_set():
            if cancel_event.is_set():
                self.stop()
                return False
            if time.monotonic() >= deadline:
                self.stop()
                return False
            time.sleep(0.02)
        return True

    def _drain_queue(self) -> None:
        while True:
            try:
                self._queue.get_nowait()
            except queue.Empty:
                return
            self._queue.task_done()

    def _run(self) -> None:
        try:
            import sounddevice as sd

            with sd.RawOutputStream(
                samplerate=self.sample_rate,
                channels=self.channels,
                dtype="int16",
                blocksize=0,
                latency="low",
            ) as stream:
                while not self._stop_event.is_set():
                    item = self._queue.get()
                    try:
                        if item is None:
                            break
                        if item:
                            stream.write(item)
                    finally:
                        self._queue.task_done()
        except Exception as exc:  # noqa: BLE001
            self.error = str(exc)
        finally:
            self._done_event.set()


class GPTSoVITSDirectTTSProvider:
    """Direct GPT-SoVITS streaming player.

    The remote API returns one audio stream. Locally we split raw PCM chunks into
    short WAV files so the existing Windows playback and pet mouth state can react
    as soon as chunks arrive.
    """

    def __init__(
        self,
        config: GPTSoVITSDirectTTSConfig,
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
        self._pcm_player: _SoundDevicePCMStreamPlayer | None = None
        self._next_url_index = 0
        self._opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        self._playback_thread = threading.Thread(
            target=self._playback_loop,
            name="gpt-sovits-tts-playback",
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
            raise GPTSoVITSDirectTTSError("text is empty")
        if not self.config.is_ready():
            raise GPTSoVITSDirectTTSError("GPT-SoVITS config incomplete: base URL or ref audio path missing")

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
            name=f"gpt-sovits-tts-stream-{job_id[:8]}",
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
        stream_error: Exception | None = None
        segment_index = 0
        attempts = max(1, len(self.config.base_urls or DEFAULT_BASE_URLS))

        for attempt in range(attempts):
            if not self._is_current_job(job_id) or self._cancel_event.is_set():
                stream_error = None
                break
            pcm_player: _SoundDevicePCMStreamPlayer | None = None
            attempt_start_index = segment_index
            retry_this_attempt = False
            try:
                base_url = self._select_base_url()
                pcm_player = self._create_pcm_stream_player()
                for raw_audio in self._post_stream(text, base_url):
                    if not raw_audio or not self._is_current_job(job_id) or self._cancel_event.is_set():
                        continue
                    local_path, duration = self._write_segment(job_id, segment_index, raw_audio)
                    queue_file_playback = True
                    if pcm_player is not None:
                        self._feed_pcm_stream_player(job_id, pcm_player, raw_audio, local_path)
                        queue_file_playback = False
                    self._handle_segment(
                        job_id,
                        {
                            "index": segment_index,
                            "segment_count": 0,
                            "audio_duration_seconds": duration,
                            "elapsed_seconds": round(time.perf_counter() - started, 3),
                            "worker_url": base_url,
                            "filename": local_path.name,
                        },
                        local_path,
                        time.perf_counter() - started,
                        queue_playback=queue_file_playback,
                    )
                    segment_index += 1
                if segment_index == 0 and self._is_current_job(job_id) and not self._cancel_event.is_set():
                    raise GPTSoVITSDirectTTSError("GPT-SoVITS stream returned no audio")
                stream_error = None
            except Exception as exc:  # noqa: BLE001
                if segment_index > attempt_start_index and _is_transient_stream_error(exc):
                    # The service can close chunked responses abruptly after
                    # yielding usable audio. Treat the partial audio as done so
                    # one bad trailer does not kill the whole conversation.
                    stream_error = None
                else:
                    stream_error = exc
                    retry_this_attempt = (
                        attempt + 1 < attempts
                        and segment_index == attempt_start_index
                        and _is_transient_stream_error(exc)
                    )
            finally:
                if pcm_player is not None:
                    try:
                        if retry_this_attempt or stream_error is not None or self._cancel_event.is_set():
                            pcm_player.stop()
                        else:
                            pcm_player.finish()
                            expected_duration = 0.0
                            with self._lock:
                                if self._last_stats and self._last_stats.job_id == job_id:
                                    expected_duration = self._last_stats.total_audio_duration_seconds
                            if not pcm_player.wait(
                                cancel_event=self._cancel_event,
                                timeout_seconds=max(5.0, expected_duration + 5.0),
                            ):
                                stream_error = GPTSoVITSDirectTTSError("sounddevice playback did not finish")
                            elif pcm_player.error:
                                stream_error = GPTSoVITSDirectTTSError(f"sounddevice playback failed: {pcm_player.error}")
                    finally:
                        with self._lock:
                            if self._pcm_player is pcm_player:
                                self._pcm_player = None
                            if self._is_current_job(job_id):
                                self._playing = False
                                if self._last_stats and self._last_stats.job_id == job_id:
                                    self._last_stats.played_segments = max(
                                        self._last_stats.played_segments,
                                        self._last_stats.downloaded_segments,
                                    )
            if retry_this_attempt:
                continue
            break

        if self._is_current_job(job_id):
            with self._lock:
                self._stream_done = True
                if stream_error is not None and not self._cancel_event.is_set():
                    if self._last_stats and self._last_stats.job_id == job_id:
                        self._last_stats.status = "error"
                        self._last_stats.error = str(stream_error)
                    self._set_state_locked("error", {"job_id": job_id, "error": str(stream_error)})
                else:
                    self._maybe_idle_locked(job_id)

    def _handle_segment(
        self,
        job_id: str,
        data: dict[str, Any],
        local_path: Path,
        first_elapsed: float,
        *,
        queue_playback: bool = True,
    ) -> None:
        duration = _float(data.get("audio_duration_seconds"), 0.0)
        segment_info = {
            "job_id": job_id,
            "index": int(_float(data.get("index"), 0.0)),
            "segment_count": int(_float(data.get("segment_count"), 0.0)),
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
        if queue_playback:
            self._queue.put((job_id, str(local_path)))

    def _create_pcm_stream_player(self) -> _SoundDevicePCMStreamPlayer | None:
        if not self.config.playback_enabled:
            return None
        backend = (self.config.playback_backend or "sounddevice").lower()
        if backend in {"file", "wav", "winsound"}:
            return None
        if backend not in {"auto", "sounddevice", "pcm", "pcm_stream", "stream"}:
            return None
        try:
            __import__("sounddevice")
        except Exception:
            if backend == "sounddevice":
                raise GPTSoVITSDirectTTSError("sounddevice playback backend is not available")
            return None
        player = _SoundDevicePCMStreamPlayer(
            sample_rate=self.config.sample_rate,
            channels=self.config.channels,
            sample_width=self.config.sample_width,
        )
        player.start()
        return player

    def _feed_pcm_stream_player(
        self,
        job_id: str,
        player: _SoundDevicePCMStreamPlayer,
        raw_audio: bytes,
        local_path: Path,
    ) -> None:
        with self._lock:
            if not self._is_current_job(job_id) or self._cancel_event.is_set():
                return
            self._pcm_player = player
            if not self._playing:
                self._playing = True
                self._set_state_locked(
                    "speaking",
                    {"job_id": job_id, "path": str(local_path), "backend": "sounddevice"},
                )
        player.write(raw_audio)

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

    def _post_stream(self, text: str, base_url: str):
        payload = self._request_payload(text)
        request = urllib.request.Request(
            _join_url(base_url, "/v1/tts/stream"),
            data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
            headers={"Content-Type": "application/json", "Accept": "audio/*"},
            method="POST",
        )
        try:
            with self._opener.open(request, timeout=self.config.request_timeout_seconds) as response:
                pending = bytearray()
                wav_header_stripped = self.config.media_type != "wav"
                wav_header_pending = bytearray()
                while not self._cancel_event.is_set():
                    chunk = response.read(self.config.read_chunk_bytes)
                    if not chunk:
                        break
                    if not wav_header_stripped:
                        wav_header_pending.extend(chunk)
                        if len(wav_header_pending) < 44:
                            continue
                        if wav_header_pending.startswith(b"RIFF"):
                            chunk = bytes(wav_header_pending[44:])
                        else:
                            chunk = bytes(wav_header_pending)
                        wav_header_pending.clear()
                        wav_header_stripped = True
                    if not chunk:
                        continue
                    pending.extend(chunk)
                    while len(pending) >= self.config.min_segment_bytes:
                        emit = bytes(pending[: self.config.min_segment_bytes])
                        del pending[: self.config.min_segment_bytes]
                        yield _even_pcm_bytes(emit)
                if pending and not self._cancel_event.is_set():
                    yield _even_pcm_bytes(bytes(pending))
        except urllib.error.HTTPError as exc:
            raise GPTSoVITSDirectTTSError(_http_error_message(exc)) from exc
        except (OSError, urllib.error.URLError) as exc:
            raise GPTSoVITSDirectTTSError(f"GPT-SoVITS stream request failed: {exc}") from exc

    def _request_payload(self, text: str) -> dict[str, Any]:
        return {
            "text": text,
            "text_lang": self.config.text_lang,
            "ref_audio_path": self.config.ref_audio_path,
            "prompt_text": self.config.prompt_text,
            "prompt_lang": self.config.prompt_lang,
            "top_k": self.config.top_k,
            "top_p": self.config.top_p,
            "temperature": self.config.temperature,
            "text_split_method": self.config.text_split_method,
            "batch_size": self.config.batch_size,
            "batch_threshold": self.config.batch_threshold,
            "split_bucket": self.config.split_bucket,
            "speed_factor": self.config.speed_factor,
            "fragment_interval": self.config.fragment_interval,
            "media_type": self.config.media_type,
            "streaming_mode": self.config.streaming_mode,
            "parallel_infer": self.config.parallel_infer,
            "repetition_penalty": self.config.repetition_penalty,
            "sample_steps": self.config.sample_steps,
            "super_sampling": self.config.super_sampling,
            "overlap_length": self.config.overlap_length,
            "min_chunk_length": self.config.min_chunk_length,
        }

    def _write_segment(self, job_id: str, index: int, raw_audio: bytes) -> tuple[Path, float]:
        filename = f"{job_id[:8]}-{index:03d}.wav"
        destination = self.config.cache_dir / filename
        with wave.open(str(destination), "wb") as wav_file:
            wav_file.setnchannels(self.config.channels)
            wav_file.setsampwidth(self.config.sample_width)
            wav_file.setframerate(self.config.sample_rate)
            wav_file.writeframes(raw_audio)
        duration = len(raw_audio) / float(self.config.sample_rate * self.config.sample_width * self.config.channels)
        return destination, duration

    def _play_audio(self, path: Path) -> None:
        if os.name == "nt":
            import winsound

            duration = wav_duration_seconds(path) or 0.0
            winsound.PlaySound(str(path), winsound.SND_FILENAME | winsound.SND_ASYNC)
            deadline = time.monotonic() + max(duration, 0.08)
            while time.monotonic() < deadline:
                if self._cancel_event.is_set():
                    break
                time.sleep(0.02)
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
        player = self._pcm_player
        if player is not None:
            try:
                player.stop()
            except Exception:
                pass
            finally:
                self._pcm_player = None
        if os.name != "nt":
            return
        try:
            import winsound

            winsound.PlaySound(None, winsound.SND_PURGE)
        except Exception:
            pass

    def _select_base_url(self) -> str:
        with self._lock:
            urls = self.config.base_urls or DEFAULT_BASE_URLS
            url = urls[self._next_url_index % len(urls)]
            self._next_url_index += 1
            return url

    def _is_current_job(self, job_id: str) -> bool:
        with self._lock:
            return self._current_job_id == job_id

    def _set_state_locked(self, state: TTSState, payload: dict[str, Any] | None = None) -> None:
        self._state = state
        if self.on_state_change:
            self.on_state_change(state, payload or {})


def normalize_url(value: str) -> str:
    url = str(value or "").strip()
    if not url:
        return ""
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


def _urls_from_mapping(data: Mapping[str, Any]) -> tuple[str, ...]:
    raw = _pick(data, "BaseURLs", "BaseUrls", "base_urls", "baseUrls", "GatewayURLs", "gateway_urls")
    values: list[str] = []
    if isinstance(raw, (list, tuple)):
        values.extend(str(item) for item in raw)
    elif raw:
        values.extend(part.strip() for part in str(raw).split(","))
    else:
        single = _first_text(_pick(data, "GatewayURL", "GatewayUrl", "gateway_url", "BaseURL", "BaseUrl", "base_url"), "")
        if single:
            values.append(single)
    urls = tuple(url for url in (normalize_url(value) for value in values) if url)
    return urls or DEFAULT_BASE_URLS


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
        if value is None:
            continue
        text = str(value).strip()
        if text:
            return text
    return ""


def _bool(value: Any, default: bool) -> bool:
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        return bool(value)
    return str(value).strip().lower() in {"1", "true", "yes", "on"}


def _float(value: Any, default: float) -> float:
    try:
        if value is None or value == "":
            return default
        return float(value)
    except (TypeError, ValueError):
        return default


def _even_pcm_bytes(data: bytes) -> bytes:
    if len(data) % 2:
        return data[:-1]
    return data


def _is_transient_stream_error(exc: BaseException) -> bool:
    if isinstance(exc, http.client.IncompleteRead):
        return True
    if isinstance(exc, (ConnectionError, TimeoutError, OSError, urllib.error.URLError)):
        return True
    text = str(exc).lower()
    return any(
        marker in text
        for marker in (
            "incompleteread",
            "response ended prematurely",
            "remote end closed",
            "connection reset",
            "connection aborted",
            "timed out",
        )
    )


def _http_error_message(exc: urllib.error.HTTPError) -> str:
    try:
        body = exc.read().decode("utf-8", errors="replace")
    except Exception:
        body = ""
    return f"HTTP {exc.code}: {body[:500] or exc.reason}"
