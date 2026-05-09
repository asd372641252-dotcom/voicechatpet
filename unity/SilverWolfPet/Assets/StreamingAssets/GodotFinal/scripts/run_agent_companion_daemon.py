from __future__ import annotations

import argparse
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from src.agent_pet_control_bridge import AgentCompanionDaemon, CompanionDaemonSettings


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")

    parser = argparse.ArgumentParser(description="Run persistent Agent companion mode daemon.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=17343)
    parser.add_argument("--pet-endpoint", default="http://127.0.0.1:17342/pet/perform")
    parser.add_argument("--heartbeat-sec", type=float, default=10.0)
    parser.add_argument("--throttle-sec", type=float, default=2.0)
    args = parser.parse_args()

    daemon = AgentCompanionDaemon(
        CompanionDaemonSettings(
            host=args.host,
            port=args.port,
            pet_endpoint=args.pet_endpoint,
            heartbeat_sec=args.heartbeat_sec,
            throttle_sec=args.throttle_sec,
        )
    )
    daemon.serve_forever()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
