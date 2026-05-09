from __future__ import annotations

import argparse
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from src.agent_plugin import AgentSpeakerServer


DEFAULT_CONFIG = ROOT / "config" / "agent_speaker.example.json"


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")

    parser = argparse.ArgumentParser(description="Run desktop pet Agent speaker plugin server.")
    parser.add_argument("--config", default=str(DEFAULT_CONFIG))
    parser.add_argument("--host", default="")
    parser.add_argument("--port", type=int, default=0)
    parser.add_argument("--godot-host", default="")
    parser.add_argument("--godot-port", type=int, default=0)
    parser.add_argument("--presentation-route", default="", help="Presentation route id, for example unity or godot.")
    parser.add_argument("--presentation-backend", default="", help="Presentation backend override: unity or godot.")
    parser.add_argument("--presentation-host", default="")
    parser.add_argument("--presentation-port", type=int, default=0)
    args = parser.parse_args()

    config_path = Path(args.config)
    if not config_path.exists():
        fallback = DEFAULT_CONFIG
        print(f"Agent speaker config not found: {config_path}; fallback={fallback}")
        config_path = fallback

    server = AgentSpeakerServer(
        config_path=config_path,
        root=ROOT,
        godot_host=args.godot_host,
        godot_port=args.godot_port,
        presentation_route=args.presentation_route,
        presentation_backend=args.presentation_backend,
        presentation_host=args.presentation_host,
        presentation_port=args.presentation_port,
    )
    if args.host:
        object.__setattr__(server.settings, "host", args.host)
    if args.port > 0:
        object.__setattr__(server.settings, "port", args.port)
    server.serve_forever()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
