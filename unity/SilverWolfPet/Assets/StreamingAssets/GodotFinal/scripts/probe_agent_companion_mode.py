from __future__ import annotations

import json
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from src.agent_pet_control_bridge import AgentCompanionMode, CompanionEvent


class _ProbeHandler(BaseHTTPRequestHandler):
    calls: list[dict[str, Any]] = []

    def do_POST(self) -> None:
        if self.path != "/pet/perform":
            self.send_response(404)
            self.end_headers()
            return
        length = int(self.headers.get("Content-Length", "0"))
        raw = self.rfile.read(length) if length > 0 else b"{}"
        payload = json.loads(raw.decode("utf-8"))
        self.calls.append(payload)
        body = json.dumps({"ok": True, "accepted": True}, ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt: str, *args: Any) -> None:
        return


def main() -> int:
    server = ThreadingHTTPServer(("127.0.0.1", 0), _ProbeHandler)
    port = int(server.server_address[1])
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()

    def silver_wolf_line(event: CompanionEvent) -> str:
        if event.phase == "task_start":
            return f"开局了。{event.status_text}"
        if event.heartbeat:
            return f"还在跑，别急。{event.status_text}"
        if event.phase == "done":
            return f"通关。{event.status_text}"
        return event.status_text

    companion = AgentCompanionMode(
        endpoint=f"http://127.0.0.1:{port}/pet/perform",
        heartbeat_sec=1.0,
        throttle_sec=0.05,
        line_builder=silver_wolf_line,
    )

    companion.start_task("companion-probe", "检查陪伴模式。")
    _wait_for_calls(1)

    companion.set_phase("operating", "正在执行桌面操作。", force=True)
    _wait_for_calls(2)

    time.sleep(1.25)
    _wait_for_calls(3)
    assert _ProbeHandler.calls[-1]["phase"] == "operating"
    assert "还在跑" in _ProbeHandler.calls[-1]["text"]

    companion.waiting_user("这里需要你确认。")
    _wait_for_calls(4)
    assert _ProbeHandler.calls[-1]["blocking"] is True

    companion.done("陪伴模式验收完成。")
    _wait_for_calls(5)
    time.sleep(1.2)
    assert len(_ProbeHandler.calls) == 5, "done should stop heartbeat loop"

    server.shutdown()
    server.server_close()

    print("agent_companion_mode probe passed")
    print(json.dumps({"calls": _ProbeHandler.calls}, ensure_ascii=False, indent=2))
    return 0


def _wait_for_calls(count: int, timeout_sec: float = 2.0) -> None:
    deadline = time.time() + timeout_sec
    while time.time() < deadline:
        if len(_ProbeHandler.calls) >= count:
            return
        time.sleep(0.02)
    raise AssertionError(f"Expected at least {count} pet calls, got {len(_ProbeHandler.calls)}")


if __name__ == "__main__":
    raise SystemExit(main())
