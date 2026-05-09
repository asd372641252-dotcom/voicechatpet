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

from src.agent_pet_control_bridge import AgentPetSession


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

    reminders: list[dict[str, object]] = []
    endpoint = f"http://127.0.0.1:{port}/pet/perform"

    session = AgentPetSession(
        "guard-task",
        endpoint=endpoint,
        reminder_grace_sec=0.05,
        reminder_callback=lambda reminder: reminders.append(reminder.to_dict()),
    )

    session.expect("task_start", "测试开始。")
    session.pet.task_start("guard-task", "测试开始。")
    time.sleep(0.08)
    assert session.check_reminders() == [], "A real pet_perform call should satisfy the expectation"

    session.expect("operating", "这一步故意忘记播。")
    time.sleep(0.08)
    missing = session.check_reminders()
    assert len(missing) == 1, missing
    assert missing[0].reason == "missing_pet_perform", missing[0]
    assert "pet_perform" in missing[0].to_agent_message()

    session.expect("done", "完成。")
    session.pet.done("guard-task", "完成。")
    time.sleep(0.08)
    assert session.check_reminders() == [], "done should be satisfied after a monitored call"

    failing = AgentPetSession(
        "failure-task",
        endpoint="http://127.0.0.1:1/pet/perform",
        timeout_sec=0.1,
        reminder_grace_sec=0.05,
        reminder_callback=lambda reminder: reminders.append(reminder.to_dict()),
    )
    failing.expect("waiting_user", "这里需要确认。")
    failed_result = failing.pet.waiting_user("failure-task", "这里需要确认。")
    assert not failed_result.accepted
    failed_reminders = failing.consume_reminders()
    assert any(item.reason == "pet_perform_failed" for item in failed_reminders), failed_reminders

    server.shutdown()
    server.server_close()

    print("agent_pet_reminder_guard probe passed")
    print(
        json.dumps(
            {
                "pet_calls": _ProbeHandler.calls,
                "callback_reminders": reminders,
                "failed_reminders": [item.to_dict() for item in failed_reminders],
            },
            ensure_ascii=False,
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
