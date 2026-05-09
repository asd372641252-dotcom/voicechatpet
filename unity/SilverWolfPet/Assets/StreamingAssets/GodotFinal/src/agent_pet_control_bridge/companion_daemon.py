from __future__ import annotations

import json
import threading
from dataclasses import dataclass
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, Mapping

from .client import DEFAULT_ENDPOINT
from .client import PetPerformTool
from .companion import AgentCompanionMode, CompanionEvent


DEFAULT_COMPANION_HOST = "127.0.0.1"
DEFAULT_COMPANION_PORT = 17343


@dataclass(frozen=True)
class CompanionDaemonSettings:
    host: str = DEFAULT_COMPANION_HOST
    port: int = DEFAULT_COMPANION_PORT
    pet_endpoint: str = DEFAULT_ENDPOINT
    heartbeat_sec: float = 10.0
    throttle_sec: float = 2.0
    emit_heartbeats: bool = True


class AgentCompanionDaemon:
    """HTTP daemon that keeps AgentCompanionMode alive between Agent calls."""

    def __init__(self, settings: CompanionDaemonSettings | None = None) -> None:
        self.settings = settings or CompanionDaemonSettings()
        self._lock = threading.RLock()
        self._companion: AgentCompanionMode | None = None
        self._task_id = ""
        self._objective = ""
        self._last_phase = ""
        self._last_text = ""
        self._last_heartbeat_text = ""
        self._heartbeat_texts: list[str] = []

    def serve_forever(self) -> None:
        server = ThreadingHTTPServer((self.settings.host, self.settings.port), _make_handler(self))
        server.app = self  # type: ignore[attr-defined]
        print(f"Agent companion daemon listening: http://{self.settings.host}:{self.settings.port}")
        print(f"Pet endpoint: {self.settings.pet_endpoint}")
        try:
            server.serve_forever()
        finally:
            with self._lock:
                if self._companion is not None:
                    self._companion.stop()
            server.server_close()

    def health(self) -> dict[str, Any]:
        with self._lock:
            return {
                "ok": True,
                "mode": "agent_companion_daemon",
                "task_id": self._task_id,
                "objective": self._objective,
                "last_phase": self._last_phase,
                "last_text": self._last_text,
                "heartbeat_sec": self.settings.heartbeat_sec,
                "emit_heartbeats": self.settings.emit_heartbeats,
                "pet_endpoint": self.settings.pet_endpoint,
                "running": self._companion is not None,
            }

    def start(self, payload: Mapping[str, Any]) -> dict[str, Any]:
        task_id = _text(payload.get("task_id")) or "agent-companion-task"
        objective = _text(payload.get("objective"))
        text = _text(payload.get("text")) or objective or "任务开始。"
        heartbeat_text = _text(payload.get("heartbeat_text"))
        heartbeat_texts = _text_list(payload.get("heartbeat_texts"))
        with self._lock:
            if self._companion is not None:
                self._companion.stop()
            self._task_id = task_id
            self._objective = objective
            self._last_phase = "task_start"
            self._last_text = text
            self._last_heartbeat_text = heartbeat_text
            self._heartbeat_texts = heartbeat_texts
            self._companion = self._new_companion_locked()
            result = self._companion.start_task(task_id, objective, text)
        return {"ok": result.accepted or result.sent or result.queued, "result": result.__dict__, **self.health()}

    def phase(self, payload: Mapping[str, Any]) -> dict[str, Any]:
        phase = _text(payload.get("phase")) or "operating"
        text = _text(payload.get("text")) or _text(payload.get("status_text")) or "还在处理。"
        heartbeat_text = _text(payload.get("heartbeat_text"))
        heartbeat_texts = _text_list(payload.get("heartbeat_texts"))
        force = bool(payload.get("force", False))
        with self._lock:
            companion = self._require_companion_locked()
            self._last_phase = phase
            self._last_text = text
            if heartbeat_text:
                self._last_heartbeat_text = heartbeat_text
            if heartbeat_texts:
                self._heartbeat_texts = heartbeat_texts
            result = companion.set_phase(phase, text, force=force)
        return {"ok": result.accepted or result.sent or result.queued or result.throttled, "result": result.__dict__, **self.health()}

    def waiting_user(self, payload: Mapping[str, Any]) -> dict[str, Any]:
        text = _text(payload.get("text")) or "这里需要你确认一下。"
        with self._lock:
            companion = self._require_companion_locked()
            self._last_phase = "waiting_user"
            self._last_text = text
            result = companion.waiting_user(text)
        return {"ok": result.accepted or result.sent or result.queued, "result": result.__dict__, **self.health()}

    def blocked(self, payload: Mapping[str, Any]) -> dict[str, Any]:
        text = _text(payload.get("text")) or "卡住了，我换个办法。"
        with self._lock:
            companion = self._require_companion_locked()
            self._last_phase = "blocked"
            self._last_text = text
            result = companion.blocked(text)
        return {"ok": result.accepted or result.sent or result.queued, "result": result.__dict__, **self.health()}

    def done(self, payload: Mapping[str, Any]) -> dict[str, Any]:
        text = _text(payload.get("text")) or "完成。"
        with self._lock:
            companion = self._require_companion_locked()
            self._last_phase = "done"
            self._last_text = text
            result = companion.done(text)
            self._companion = None
        return {"ok": result.accepted or result.sent or result.queued, "result": result.__dict__, **self.health()}

    def failed(self, payload: Mapping[str, Any]) -> dict[str, Any]:
        text = _text(payload.get("text")) or "失败。"
        with self._lock:
            companion = self._require_companion_locked()
            self._last_phase = "failed"
            self._last_text = text
            result = companion.failed(text)
            self._companion = None
        return {"ok": result.accepted or result.sent or result.queued, "result": result.__dict__, **self.health()}

    def stop(self) -> dict[str, Any]:
        result = None
        with self._lock:
            if self._companion is not None:
                self._companion.stop()
            self._companion = None
            self._last_phase = "stopped"
            result = self._send_stop_visual_locked()
        return {"ok": True, "result": result.__dict__ if result is not None else {}, **self.health()}

    def _new_companion_locked(self) -> AgentCompanionMode:
        return AgentCompanionMode(
            endpoint=self.settings.pet_endpoint,
            heartbeat_sec=self.settings.heartbeat_sec,
            throttle_sec=self.settings.throttle_sec,
            emit_heartbeats=self.settings.emit_heartbeats,
            line_builder=self._line_builder,
        )

    def _send_stop_visual_locked(self):
        tool = PetPerformTool(
            endpoint=self.settings.pet_endpoint,
            timeout_sec=0.8,
            throttle_sec=0.0,
        )
        task_id = self._task_id or "agent-companion-task"
        return tool.pet_perform(
            {
                "task_id": task_id,
                "phase": "done",
                "text": "",
                "emotion": "neutral",
                "pose": "idle",
                "bubble": True,
                "priority": 60,
            }
        )

    def _require_companion_locked(self) -> AgentCompanionMode:
        if self._companion is None:
            self._task_id = self._task_id or "agent-companion-task"
            self._objective = self._objective or ""
            self._companion = self._new_companion_locked()
            self._companion.start_task(self._task_id, self._objective, self._last_text or "任务开始。")
        return self._companion

    def _line_builder(self, event: CompanionEvent) -> str:
        if not event.heartbeat:
            return event.status_text
        with self._lock:
            heartbeat_text = self._last_heartbeat_text
            heartbeat_texts = list(self._heartbeat_texts)
        if heartbeat_texts:
            return heartbeat_texts[(event.heartbeat_index - 1) % len(heartbeat_texts)]
        base = heartbeat_text or event.status_text or "还在处理。"
        return _heartbeat_variant(base, event.heartbeat_index)


class _CompanionDaemonHandler(BaseHTTPRequestHandler):
    app: AgentCompanionDaemon

    def do_OPTIONS(self) -> None:
        self._send_json({"ok": True})

    def do_GET(self) -> None:
        if self.path in {"/health", "/v1/health"}:
            self._send_json(self.app.health())
            return
        self._send_json({"ok": False, "error": "not_found"}, HTTPStatus.NOT_FOUND)

    def do_POST(self) -> None:
        payload = self._read_json()
        if payload is None:
            return
        try:
            if self.path in {"/companion/start", "/v1/companion/start"}:
                self._send_json(self.app.start(payload))
                return
            if self.path in {"/companion/phase", "/v1/companion/phase"}:
                self._send_json(self.app.phase(payload))
                return
            if self.path in {"/companion/waiting_user", "/v1/companion/waiting_user"}:
                self._send_json(self.app.waiting_user(payload))
                return
            if self.path in {"/companion/blocked", "/v1/companion/blocked"}:
                self._send_json(self.app.blocked(payload))
                return
            if self.path in {"/companion/done", "/v1/companion/done"}:
                self._send_json(self.app.done(payload))
                return
            if self.path in {"/companion/failed", "/v1/companion/failed"}:
                self._send_json(self.app.failed(payload))
                return
            if self.path in {"/companion/stop", "/v1/companion/stop"}:
                self._send_json(self.app.stop())
                return
        except ValueError as exc:
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.BAD_REQUEST)
            return
        self._send_json({"ok": False, "error": "not_found"}, HTTPStatus.NOT_FOUND)

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
        body = json.dumps(payload, ensure_ascii=True, separators=(",", ":")).encode("ascii")
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
        print("[agent_companion] " + fmt % args)


def _make_handler(app: AgentCompanionDaemon) -> type[_CompanionDaemonHandler]:
    class Handler(_CompanionDaemonHandler):
        pass

    Handler.app = app
    return Handler


def _text(value: Any) -> str:
    if value is None:
        return ""
    return str(value).strip()


def _text_list(value: Any) -> list[str]:
    if not isinstance(value, list):
        return []
    result = []
    for item in value:
        text = _text(item)
        if text:
            result.append(text)
    return result


def _heartbeat_variant(base: str, index: int) -> str:
    clean = base.strip() or "还在处理。"
    variants = [
        "还在推进：%s",
        "这一步还没结束：%s",
        "继续处理中：%s",
        "进度在线：%s",
        "没掉线，仍在跑：%s",
    ]
    return variants[(index - 1) % len(variants)] % clean
