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

from src.agent_pet_control_bridge import PetPerformTool


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

    endpoint = f"http://127.0.0.1:{port}/pet/perform"
    tool = PetPerformTool(endpoint=endpoint, throttle_sec=8.0, timeout_sec=1.0)
    task_id = "probe-task"

    started = time.perf_counter()
    result = tool.pet_perform(
        {
            "task_id": task_id,
            "phase": "task_start",
            "text": "测试开始。",
            "emotion": "focused",
            "pose": "talk",
            "bubble": True,
            "blocking": False,
            "priority": 70,
        }
    )
    assert result.queued and result.accepted, result
    assert time.perf_counter() - started < 0.2, "blocking=false should return immediately"
    _wait_for_calls(1)

    result = tool.pet_perform(
        {
            "task_id": task_id,
            "phase": "waiting_user",
            "text": "需要确认。",
            "emotion": "confused",
            "pose": "think",
            "bubble": True,
            "blocking": True,
            "priority": 80,
        }
    )
    assert result.accepted and result.sent and not result.queued, result
    assert len(_ProbeHandler.calls) >= 2

    first_operating = tool.operating(task_id, "正在操作。")
    second_operating = tool.operating(task_id, "这条应该被节流。")
    assert first_operating.accepted and first_operating.queued, first_operating
    assert second_operating.throttled and not second_operating.sent, second_operating
    _wait_for_calls(3)

    done = tool.done(task_id, "完成。")
    assert done.accepted and done.queued, done
    _wait_for_calls(4)

    other_task = tool.operating("probe-task-2", "新任务不受旧任务节流。")
    assert other_task.accepted and other_task.queued, other_task
    _wait_for_calls(5)

    failing_tool = PetPerformTool(endpoint="http://127.0.0.1:1/pet/perform", timeout_sec=0.1)
    failed = failing_tool.waiting_user("failure-task", "桌宠不可用也不能中断桌面任务。")
    assert not failed.accepted and failed.error, failed

    server.shutdown()
    server.server_close()
    print("agent_pet_control_bridge probe passed")
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
