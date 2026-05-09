from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from src.pet_pose_bridge import UnityPoseClient


COMMANDS = [
    {
        "type": "pet_pose",
        "state": "idle",
        "emotion": "neutral",
        "gesture": "none",
        "posture": "stand",
        "bubble_text": "Unity route ready.",
    },
    {
        "type": "pet_pose",
        "state": "thinking",
        "emotion": "confused",
        "gesture": "think",
        "posture": "stand",
        "bubble_text": "Thinking through it.",
    },
    {
        "type": "pet_pose",
        "state": "speaking",
        "emotion": "mocking",
        "gesture": "smug",
        "posture": "stand",
        "bubble_text": "Unity presentation is listening.",
        "mouth": "audio_volume",
        "mouth_open": 0.6,
    },
    {
        "type": "pet_pose",
        "state": "acting",
        "emotion": "happy",
        "gesture": "point",
        "posture": "stand",
        "bubble_text": "Action route checked.",
    },
]


def main() -> int:
    parser = argparse.ArgumentParser(description="Send semantic pet_pose commands to the Unity PetDesktop TCP route.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=17861)
    parser.add_argument("--timeout-sec", type=float, default=0.5)
    parser.add_argument("--dry-run", action="store_true", help="Print Unity payloads without opening TCP.")
    args = parser.parse_args()

    client = UnityPoseClient(args.host, args.port, timeout_sec=args.timeout_sec, offline_cooldown_sec=0.0)
    if args.dry_run:
        for command in COMMANDS:
            print(json.dumps(client.to_unity_payload(command), ensure_ascii=False, separators=(",", ":")))
        return 0

    ok_count = 0
    for command in COMMANDS:
        ok = client.send_pose(command)
        print(f"{command['state']}: {'ok' if ok else 'failed'}")
        ok_count += int(ok)
    return 0 if ok_count == len(COMMANDS) else 1


if __name__ == "__main__":
    raise SystemExit(main())
