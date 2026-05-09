from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_CONFIG_PATH = ROOT / "config" / "volc_start_voice_chat.local.json"
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from src.voice_backends.volc_rtc.rtc_token_manager import ensure_fresh_rtc_token


def main() -> int:
    parser = argparse.ArgumentParser(description="Refresh the local Volc RTC room token in config.")
    parser.add_argument("--config", default=str(DEFAULT_CONFIG_PATH))
    parser.add_argument("--ttl-sec", type=int, default=7 * 24 * 3600)
    args = parser.parse_args()

    config_path = Path(args.config)
    data = json.loads(config_path.read_text(encoding="utf-8"))
    try:
        ensure_fresh_rtc_token(data, ttl_sec=args.ttl_sec, refresh_margin_sec=args.ttl_sec + 1)
    except ValueError as exc:
        print(str(exc), file=sys.stderr)
        return 2

    with config_path.open("w", encoding="utf-8", newline="\n") as handle:
        handle.write(json.dumps(data, ensure_ascii=False, indent=2) + "\n")
    start = data.get("StartVoiceChat", {})
    client = data.get("ClientRTC", {})
    agent = start.get("AgentConfig", {})
    user_id = _first_text(agent.get("TargetUserId", []) if isinstance(agent, dict) else [])
    expire_at = int(time.time()) + max(60, args.ttl_sec)
    print(f"Refreshed RTC token in {config_path}")
    print(f"RoomId={start.get('RoomId')}")
    print(f"UserId={user_id}")
    print(f"ExpiresAt={expire_at} {time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(expire_at))}")
    return 0


def _first_text(value: Any) -> str:
    if isinstance(value, list) and value:
        return str(value[0])
    if isinstance(value, str):
        return value
    return ""


if __name__ == "__main__":
    raise SystemExit(main())
