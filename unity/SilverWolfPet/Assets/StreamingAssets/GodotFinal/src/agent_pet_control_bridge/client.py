from __future__ import annotations

import json
import logging
import threading
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from typing import Any, Mapping

from .protocol import PetPerformCommand, PetPerformCommandError


LOGGER = logging.getLogger(__name__)
DEFAULT_ENDPOINT = "http://127.0.0.1:17342/pet/perform"
IMPORTANT_PHASES = {"task_start", "waiting_user", "blocked", "done", "failed"}
THROTTLED_PHASES = {"searching", "operating"}


@dataclass(frozen=True)
class PetPerformResult:
    accepted: bool
    sent: bool
    queued: bool = False
    throttled: bool = False
    status_code: int = 0
    error: str = ""
    response: dict[str, Any] = field(default_factory=dict)


class PetPerformTool:
    """Agent-side high-level performance tool.

    The Agent remains the main controller. This tool only sends deliberate
    semantic performance commands to the local desktop pet. It never reads OCR
    logs, coordinates, or internal tool traces.
    """

    def __init__(
        self,
        *,
        endpoint: str = DEFAULT_ENDPOINT,
        api_key: str = "",
        timeout_sec: float = 1.2,
        throttle_sec: float = 8.0,
        logger: logging.Logger | None = None,
    ) -> None:
        self.endpoint = endpoint
        self.api_key = api_key
        self.timeout_sec = timeout_sec
        self.throttle_sec = throttle_sec
        self.logger = logger or LOGGER
        self._last_sent_at: dict[tuple[str, str], float] = {}
        self._lock = threading.Lock()

    def pet_perform(self, command: PetPerformCommand | Mapping[str, Any]) -> PetPerformResult:
        try:
            parsed = command if isinstance(command, PetPerformCommand) else PetPerformCommand.from_mapping(command)
        except PetPerformCommandError as exc:
            self.logger.warning("pet_perform rejected locally: %s", exc)
            return PetPerformResult(accepted=False, sent=False, error=str(exc))

        if self._is_throttled(parsed):
            return PetPerformResult(accepted=False, sent=False, throttled=True, error="throttled")

        if parsed.blocking:
            return self._send(parsed)

        thread = threading.Thread(target=self._send_fire_and_forget, args=(parsed,), daemon=True)
        thread.start()
        return PetPerformResult(accepted=True, sent=True, queued=True)

    def task_start(self, task_id: str, text: str = "收到，任务开始。") -> PetPerformResult:
        return self.pet_perform(
            {
                "task_id": task_id,
                "phase": "task_start",
                "text": text,
                "emotion": "focused",
                "pose": "talk",
                "bubble": True,
                "priority": 70,
            }
        )

    def searching(self, task_id: str, text: str = "我在找目标。") -> PetPerformResult:
        return self.pet_perform(
            {
                "task_id": task_id,
                "phase": "searching",
                "text": text,
                "emotion": "focused",
                "pose": "think",
                "bubble": True,
                "priority": 45,
            }
        )

    def operating(self, task_id: str, text: str = "正在操作。") -> PetPerformResult:
        return self.pet_perform(
            {
                "task_id": task_id,
                "phase": "operating",
                "text": text,
                "emotion": "focused",
                "pose": "point",
                "bubble": True,
                "priority": 45,
            }
        )

    def waiting_user(self, task_id: str, text: str) -> PetPerformResult:
        return self.pet_perform(
            {
                "task_id": task_id,
                "phase": "waiting_user",
                "text": text,
                "emotion": "confused",
                "pose": "think",
                "bubble": True,
                "blocking": True,
                "priority": 80,
            }
        )

    def blocked(self, task_id: str, text: str) -> PetPerformResult:
        return self.pet_perform(
            {
                "task_id": task_id,
                "phase": "blocked",
                "text": text,
                "emotion": "annoyed",
                "pose": "annoyed",
                "bubble": True,
                "priority": 85,
            }
        )

    def done(self, task_id: str, text: str = "搞定。") -> PetPerformResult:
        return self.pet_perform(
            {
                "task_id": task_id,
                "phase": "done",
                "text": text,
                "emotion": "happy",
                "pose": "smug",
                "bubble": True,
                "priority": 90,
            }
        )

    def failed(self, task_id: str, text: str) -> PetPerformResult:
        return self.pet_perform(
            {
                "task_id": task_id,
                "phase": "failed",
                "text": text,
                "emotion": "confused",
                "pose": "annoyed",
                "bubble": True,
                "priority": 90,
            }
        )

    def _is_throttled(self, command: PetPerformCommand) -> bool:
        if command.phase in IMPORTANT_PHASES:
            with self._lock:
                self._last_sent_at[(command.task_id, command.phase)] = time.monotonic()
            return False
        if command.phase not in THROTTLED_PHASES:
            return False

        key = (command.task_id, command.phase)
        now = time.monotonic()
        with self._lock:
            last = self._last_sent_at.get(key, 0.0)
            if now - last < self.throttle_sec:
                return True
            self._last_sent_at[key] = now
        return False

    def _send_fire_and_forget(self, command: PetPerformCommand) -> None:
        result = self._send(command)
        if not result.accepted:
            self.logger.warning("pet_perform fire-and-forget failed: %s", result.error or result.status_code)

    def _send(self, command: PetPerformCommand) -> PetPerformResult:
        data = json.dumps(command.to_payload(), ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        request = urllib.request.Request(
            self.endpoint,
            data=data,
            headers=self._headers(),
            method="POST",
        )
        try:
            with urllib.request.urlopen(request, timeout=self.timeout_sec) as response:
                raw = response.read().decode("utf-8")
                payload = json.loads(raw) if raw else {}
                accepted = bool(payload.get("accepted", payload.get("ok", False)))
                return PetPerformResult(
                    accepted=accepted,
                    sent=True,
                    status_code=int(response.status),
                    response=payload if isinstance(payload, dict) else {},
                )
        except (OSError, urllib.error.URLError, TimeoutError, json.JSONDecodeError) as exc:
            self.logger.warning("pet_perform request failed: %s", exc)
            return PetPerformResult(accepted=False, sent=False, error=str(exc))

    def _headers(self) -> dict[str, str]:
        headers = {"Content-Type": "application/json; charset=utf-8"}
        if self.api_key:
            headers["Authorization"] = f"Bearer {self.api_key}"
        return headers


_DEFAULT_TOOL = PetPerformTool()


def pet_perform(command: PetPerformCommand | Mapping[str, Any]) -> PetPerformResult:
    return _DEFAULT_TOOL.pet_perform(command)
