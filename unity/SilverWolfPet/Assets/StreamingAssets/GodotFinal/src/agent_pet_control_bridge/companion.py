from __future__ import annotations

import threading
import time
from contextlib import contextmanager
from dataclasses import dataclass
from typing import Callable, Iterator

from .client import DEFAULT_ENDPOINT, PetPerformResult, PetPerformTool


IMPORTANT_PHASES = {"task_start", "waiting_user", "blocked", "done", "failed"}
ACTIVE_PHASES = {"searching", "operating"}


@dataclass(frozen=True)
class CompanionEvent:
    task_id: str
    phase: str
    status_text: str
    objective: str = ""
    elapsed_sec: float = 0.0
    heartbeat: bool = False
    heartbeat_index: int = 0


LineBuilder = Callable[[CompanionEvent], str]


PHASE_DEFAULTS: dict[str, tuple[str, str, int]] = {
    "task_start": ("focused", "talk", 75),
    "searching": ("focused", "think", 55),
    "operating": ("focused", "point", 55),
    "waiting_user": ("confused", "think", 85),
    "blocked": ("annoyed", "annoyed", 90),
    "done": ("happy", "smug", 95),
    "failed": ("confused", "annoyed", 95),
}


DEFAULT_LINES: dict[str, str] = {
    "task_start": "收到，开始处理。",
    "searching": "我在找入口。",
    "operating": "还在处理，稍等。",
    "waiting_user": "这里需要你确认一下。",
    "blocked": "卡住了，我换个办法。",
    "done": "搞定。",
    "failed": "这次没跑通，我先停一下。",
}


class AgentCompanionMode:
    """Task-scoped companion broadcaster for Agent-driven work.

    This layer emits high-level progress lines through pet_perform. The Agent
    remains responsible for persona and wording; the desktop pet only performs
    the final line. Heartbeats are enabled by default so long tasks can keep
    pushing status, but repeated heartbeat text is rewritten into a nearby
    variant instead of being sent verbatim over and over.
    """

    def __init__(
        self,
        *,
        endpoint: str = DEFAULT_ENDPOINT,
        api_key: str = "",
        timeout_sec: float = 1.2,
        throttle_sec: float = 8.0,
        heartbeat_sec: float = 10.0,
        emit_heartbeats: bool = True,
        line_builder: LineBuilder | None = None,
        pet_tool: PetPerformTool | None = None,
    ) -> None:
        self.pet = pet_tool or PetPerformTool(
            endpoint=endpoint,
            api_key=api_key,
            timeout_sec=timeout_sec,
            throttle_sec=throttle_sec,
        )
        self.heartbeat_sec = max(1.0, heartbeat_sec)
        self.emit_heartbeats = emit_heartbeats
        self.line_builder = line_builder
        self._lock = threading.RLock()
        self._task_id = ""
        self._objective = ""
        self._phase = "operating"
        self._status_text = DEFAULT_LINES["operating"]
        self._started_at = 0.0
        self._last_emit_at = 0.0
        self._last_heartbeat_line = ""
        self._heartbeat_index = 0
        self._running = False
        self._thread: threading.Thread | None = None

    def start_task(self, task_id: str, objective: str = "", text: str = "") -> PetPerformResult:
        with self._lock:
            self._task_id = task_id
            self._objective = objective
            self._phase = "operating"
            self._status_text = text or objective or DEFAULT_LINES["operating"]
            self._started_at = time.monotonic()
            self._last_heartbeat_line = ""
            self._heartbeat_index = 0
            self._running = True
            self._ensure_thread_locked()
        return self._emit("task_start", text or objective or DEFAULT_LINES["task_start"], force=True)

    def set_phase(self, phase: str, status_text: str = "", *, force: bool = False) -> PetPerformResult:
        phase = _normalize_phase(phase)
        with self._lock:
            self._phase = phase if phase in ACTIVE_PHASES else self._phase
            if status_text:
                self._status_text = status_text
                self._last_heartbeat_line = ""
                self._heartbeat_index = 0
        return self._emit(phase, status_text or DEFAULT_LINES.get(phase, self._status_text), force=force)

    def say_working(self, status_text: str = "") -> PetPerformResult:
        return self.set_phase("operating", status_text or self._status_text, force=True)

    def waiting_user(self, text: str) -> PetPerformResult:
        return self.set_phase("waiting_user", text, force=True)

    def blocked(self, text: str) -> PetPerformResult:
        return self.set_phase("blocked", text, force=True)

    def done(self, text: str = "") -> PetPerformResult:
        try:
            return self._emit("done", text or DEFAULT_LINES["done"], force=True)
        finally:
            self.stop()

    def failed(self, text: str = "") -> PetPerformResult:
        try:
            return self._emit("failed", text or DEFAULT_LINES["failed"], force=True)
        finally:
            self.stop()

    def stop(self) -> None:
        with self._lock:
            self._running = False

    @contextmanager
    def step(self, phase: str, status_text: str) -> Iterator[None]:
        self.set_phase(phase, status_text)
        try:
            yield
        except Exception:
            self.failed("这一步炸了，我先把现场留住。")
            raise

    def heartbeat(self) -> PetPerformResult | None:
        if not self.emit_heartbeats:
            return None
        with self._lock:
            if not self._running or not self._task_id:
                return None
            now = time.monotonic()
            if now - self._last_emit_at < self.heartbeat_sec:
                return None
            phase = self._phase if self._phase in ACTIVE_PHASES else "operating"
            text = self._status_text or DEFAULT_LINES[phase]
        return self._emit(phase, text, heartbeat=True)

    def _emit(
        self,
        phase: str,
        text: str,
        *,
        force: bool = False,
        heartbeat: bool = False,
    ) -> PetPerformResult:
        phase = _normalize_phase(phase)
        with self._lock:
            if not self._task_id:
                return PetPerformResult(accepted=False, sent=False, error="companion task is not started")
            now = time.monotonic()
            if not force and phase not in IMPORTANT_PHASES and now - self._last_emit_at < self.heartbeat_sec:
                return PetPerformResult(accepted=False, sent=False, throttled=True, error="companion throttled")
            heartbeat_index = self._heartbeat_index + 1 if heartbeat else self._heartbeat_index
            event = CompanionEvent(
                task_id=self._task_id,
                phase=phase,
                status_text=text,
                objective=self._objective,
                elapsed_sec=now - self._started_at if self._started_at else 0.0,
                heartbeat=heartbeat,
                heartbeat_index=heartbeat_index,
            )
            line = self._build_line(event)
            if heartbeat and line == self._last_heartbeat_line:
                line = _fallback_heartbeat_line(event, line)
            emotion, pose, priority = PHASE_DEFAULTS[phase]
            self._last_emit_at = now
            if heartbeat:
                self._heartbeat_index = heartbeat_index
                self._last_heartbeat_line = line

        return self.pet.pet_perform(
            {
                "task_id": event.task_id,
                "phase": phase,
                "text": line,
                "emotion": emotion,
                "pose": pose,
                "bubble": True,
                "blocking": phase == "waiting_user",
                "priority": priority,
            }
        )

    def _build_line(self, event: CompanionEvent) -> str:
        if self.line_builder is not None:
            line = self.line_builder(event)
            if line:
                return str(line).strip()
        return event.status_text or DEFAULT_LINES.get(event.phase, DEFAULT_LINES["operating"])

    def _ensure_thread_locked(self) -> None:
        if self._thread is not None and self._thread.is_alive():
            return
        self._thread = threading.Thread(target=self._heartbeat_loop, daemon=True)
        self._thread.start()

    def _heartbeat_loop(self) -> None:
        while True:
            with self._lock:
                running = self._running
            if not running:
                return
            time.sleep(min(self.heartbeat_sec, 1.0))
            self.heartbeat()


def _normalize_phase(phase: str) -> str:
    value = str(phase).strip().lower()
    if value not in PHASE_DEFAULTS:
        raise ValueError(f"Invalid companion phase {value!r}; allowed values are {sorted(PHASE_DEFAULTS)}")
    return value


def _fallback_heartbeat_line(event: CompanionEvent, base_line: str) -> str:
    base = base_line.strip() or event.status_text.strip() or DEFAULT_LINES["operating"]
    variants = [
        "还在推进：%s",
        "这一步还没结束：%s",
        "进度在线：%s",
        "继续处理中：%s",
        "没掉线，仍在跑：%s",
    ]
    template = variants[event.heartbeat_index % len(variants)]
    return template % base
