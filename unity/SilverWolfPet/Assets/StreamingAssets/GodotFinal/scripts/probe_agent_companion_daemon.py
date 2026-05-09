from __future__ import annotations

import json
import sys
import threading
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from src.agent_pet_control_bridge import AgentCompanionDaemon, CompanionDaemonSettings


class _PetProbeHandler(BaseHTTPRequestHandler):
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
        body = json.dumps({"ok": True, "accepted": True}, ensure_ascii=True).encode("ascii")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt: str, *args: Any) -> None:
        return


def main() -> int:
    pet_server = ThreadingHTTPServer(("127.0.0.1", 0), _PetProbeHandler)
    pet_port = int(pet_server.server_address[1])
    threading.Thread(target=pet_server.serve_forever, daemon=True).start()

    companion = AgentCompanionDaemon(
        CompanionDaemonSettings(
            host="127.0.0.1",
            port=0,
            pet_endpoint=f"http://127.0.0.1:{pet_port}/pet/perform",
            heartbeat_sec=1.0,
            throttle_sec=0.05,
        )
    )
    companion_server = ThreadingHTTPServer(("127.0.0.1", 0), _handler_for_probe(companion))
    companion_port = int(companion_server.server_address[1])
    threading.Thread(target=companion_server.serve_forever, daemon=True).start()

    base = f"http://127.0.0.1:{companion_port}"
    post(base + "/companion/start", {
        "task_id": "daemon-probe",
        "text": "任务开始。",
        "heartbeat_text": "还在跑。",
    })
    wait_for_calls(1)

    post(base + "/companion/phase", {
        "phase": "operating",
        "text": "正在操作。",
        "heartbeat_text": "还在操作。",
        "force": True,
    })
    wait_for_calls(2)

    time.sleep(1.25)
    wait_for_calls(3)
    assert _PetProbeHandler.calls[-1]["text"] != "正在操作。"
    assert _PetProbeHandler.calls[-1]["text"] != _PetProbeHandler.calls[-2]["text"]

    post(base + "/companion/done", {"text": "完成。"})
    wait_for_calls(4)
    time.sleep(1.2)
    assert len(_PetProbeHandler.calls) == 4, "done should stop daemon heartbeat"

    companion_server.shutdown()
    companion_server.server_close()
    pet_server.shutdown()
    pet_server.server_close()

    print("agent_companion_daemon probe passed")
    print(json.dumps({"calls": _PetProbeHandler.calls}, ensure_ascii=False, indent=2))
    return 0


def _handler_for_probe(app: AgentCompanionDaemon) -> type[BaseHTTPRequestHandler]:
    from src.agent_pet_control_bridge.companion_daemon import _make_handler

    return _make_handler(app)


def post(url: str, payload: dict[str, Any]) -> dict[str, Any]:
    req = urllib.request.Request(
        url,
        data=json.dumps(payload, ensure_ascii=True).encode("ascii"),
        headers={"Content-Type": "application/json; charset=utf-8"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=3) as resp:
        return json.loads(resp.read().decode("utf-8"))


def wait_for_calls(count: int, timeout_sec: float = 2.0) -> None:
    deadline = time.time() + timeout_sec
    while time.time() < deadline:
        if len(_PetProbeHandler.calls) >= count:
            return
        time.sleep(0.02)
    raise AssertionError(f"Expected {count} calls, got {len(_PetProbeHandler.calls)}")


if __name__ == "__main__":
    raise SystemExit(main())
