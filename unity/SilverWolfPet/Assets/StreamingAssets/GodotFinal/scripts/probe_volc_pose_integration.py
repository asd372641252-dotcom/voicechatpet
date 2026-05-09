from __future__ import annotations

import argparse
import json
import logging
import sys
import time
from pathlib import Path
from typing import Any, Mapping

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from src.pet_pose_bridge import GodotPoseClient
from src.voice_backends.volc_rtc import VolcPoseEventAdapter


class RecordingClient:
    def __init__(self) -> None:
        self.payloads: list[dict[str, Any]] = []

    def send_pose(self, payload: Mapping[str, Any]) -> bool:
        self.payloads.append(dict(payload))
        return True


def main() -> int:
    parser = argparse.ArgumentParser(description="Probe Volc RTC event to Godot pose integration.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=17865)
    parser.add_argument("--dry-run", action="store_true", help="Do not connect to Godot; print generated pet_pose payloads.")
    parser.add_argument("--require-godot", action="store_true", help="Exit non-zero if any send to Godot fails.")
    parser.add_argument("--bot-uid", default="volc_ai_bot")
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")
    sent: list[tuple[str, bool, dict[str, Any]]] = []

    def on_send(item, ok: bool) -> None:
        sent.append((item.source, ok, item.payload))
        print(
            json.dumps(
                {
                    "source": item.source,
                    "trace_id": item.trace_id,
                    "sent": ok,
                    "payload": item.payload,
                },
                ensure_ascii=False,
            )
        )

    client = RecordingClient() if args.dry_run else GodotPoseClient(args.host, args.port, timeout_sec=0.5)
    adapter = VolcPoseEventAdapter(
        godot_client=client,
        bot_uids={args.bot_uid},
        on_send=on_send,
    )

    try:
        adapter.on_volc_ai_state_event({"trace_id": "probe-001", "state": "user_speaking"})
        adapter.on_volc_ai_state_event({"trace_id": "probe-002", "state": "processing"})
        adapter.on_volc_ai_state_event({"trace_id": "probe-003", "state": "speaking"})
        adapter.on_volc_subtitle_event(
            {
                "trace_id": "probe-004",
                "speaker": "ai",
                "text": "终于想明白了？还不算太迟。",
                "is_final": False,
            }
        )
        adapter.on_volc_subtitle_event(
            {
                "trace_id": "probe-004-user-intent",
                "speaker": "user",
                "text": "摆个得意姿势",
                "is_final": True,
            }
        )
        adapter.on_volc_function_call(
            {
                "trace_id": "probe-005",
                "name": "set_pet_pose",
                "arguments": {
                    "state": "speaking",
                    "emotion": "mocking",
                    "gesture": "small_tease",
                    "posture": "stand",
                    "priority": 2,
                    "duration_ms": 1200,
                    "interruptible": True,
                },
            }
        )
        # Volume callbacks are intentionally ignored by the pose bridge now.
        # TTS mouth flap is started by ai_state:speaking and stopped by idle.
        for volume in (0, 15, 35, 70, 100):
            adapter.on_volc_remote_audio_volume(args.bot_uid, volume)
            time.sleep(0.05)
        adapter.on_volc_ai_state_event({"trace_id": "probe-007", "state": "completed"})
        time.sleep(0.8)
    finally:
        adapter.close()

    expected_sources = {
        "ai_state:listening",
        "ai_state:thinking",
        "ai_state:speaking",
        "subtitle:ai",
        "function_call:set_pet_pose",
        "ai_state:idle",
    }
    actual_sources = {source for source, _ok, _payload in sent}
    missing = sorted(expected_sources - actual_sources)
    if missing:
        print("missing expected sources: " + ", ".join(missing), file=sys.stderr)
        return 2
    if args.require_godot and any(not ok for _source, ok, _payload in sent):
        print("Godot send failed for at least one event", file=sys.stderr)
        return 3

    print("volc pose integration probe ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
