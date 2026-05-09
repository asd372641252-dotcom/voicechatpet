from __future__ import annotations

import argparse
import json
import socket
import sys
import time
from pathlib import Path
from typing import Any, Mapping

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from src.pet_pose_bridge import GodotPoseClient


PROBE_COMMANDS: list[dict[str, Any]] = [
    {
        "type": "pet_pose",
        "state": "listening",
        "emotion": "neutral",
        "gesture": "none",
        "posture": "stand",
        "priority": 50,
        "duration_ms": 1500,
        "interruptible": True,
    },
    {
        "type": "pet_pose",
        "state": "thinking",
        "emotion": "neutral",
        "gesture": "think",
        "posture": "stand",
    },
    {
        "type": "pet_pose",
        "state": "speaking",
        "emotion": "neutral",
        "gesture": "none",
        "posture": "stand",
        "mouth": "audio_volume",
        "duration_ms": 3600,
        "_delay_sec": 3.6,
    },
    {
        "type": "pet_pose",
        "state": "idle",
        "emotion": "neutral",
        "gesture": "none",
        "posture": "stand",
    },
]


def main() -> int:
    parser = argparse.ArgumentParser(description="Probe the local Godot PetPoseServer TCP interface.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=17865)
    parser.add_argument("--delay-sec", type=float, default=0.8)
    parser.add_argument("--timeout-sec", type=float, default=0.35)
    parser.add_argument("--jsonl", default="", help="Optional JSONL file with extra pet_pose commands.")
    args = parser.parse_args()

    if not _is_port_open(args.host, args.port, args.timeout_sec):
        print(f"FAIL PetPoseServer is not reachable at {args.host}:{args.port}")
        return 2

    client = GodotPoseClient(args.host, args.port, timeout_sec=args.timeout_sec)
    commands = list(PROBE_COMMANDS)
    if args.jsonl:
        commands.extend(_load_jsonl(Path(args.jsonl)))

    failures = 0
    for index, command in enumerate(commands, start=1):
        send_command = {key: value for key, value in command.items() if not key.startswith("_")}
        ok = client.send_pose(send_command)
        print(
            f"{'OK' if ok else 'FAIL'} #{index} "
            f"{send_command.get('state')} {send_command.get('emotion', 'neutral')} "
            f"{send_command.get('gesture', 'none')}"
        )
        if not ok:
            failures += 1
        delay_sec = float(command.get("_delay_sec", args.delay_sec))
        time.sleep(max(0.0, delay_sec))

    if failures:
        print(f"FAIL sent={len(commands) - failures} failed={failures}")
        return 1
    print(f"PASS PetPoseServer accepted {len(commands)} probe commands.")
    return 0


def _is_port_open(host: str, port: int, timeout_sec: float) -> bool:
    try:
        with socket.create_connection((host, port), timeout=timeout_sec):
            return True
    except OSError:
        return False


def _load_jsonl(path: Path) -> list[Mapping[str, Any]]:
    commands: list[Mapping[str, Any]] = []
    for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        text = line.strip()
        if not text:
            continue
        payload = json.loads(text)
        if not isinstance(payload, dict):
            raise ValueError(f"{path}:{line_number} must be a JSON object")
        commands.append(payload)
    return commands


if __name__ == "__main__":
    raise SystemExit(main())
