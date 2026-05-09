from __future__ import annotations

import argparse
import json
import os
import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from src.voice_backends.volc_rtc.volc_websearch_client import VolcWebSearchClient, compact_search_result


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser(description="Probe Volc TOP gateway WebSearch using project AK/SK.")
    parser.add_argument("query", nargs="?", default="星穹铁道 货币战争 攻略")
    parser.add_argument("--config", default=str(ROOT / "config" / "volc_start_voice_chat.local.json"))
    parser.add_argument("--count", type=int, default=3)
    args = parser.parse_args()

    config = _load_json_with_env(Path(args.config))
    client = VolcWebSearchClient.from_project_config(config)
    if client is None:
        raise SystemExit("WebSearchOpenAPI.Enabled is false or missing in config.")
    result = client.search(args.query, count=args.count)
    print(compact_search_result(result, max_chars=4000))
    return 0


def _load_json_with_env(path: Path) -> dict:
    text = path.read_text(encoding="utf-8")
    text = re.sub(r"\$\{([A-Z0-9_]+)\}", lambda match: os.getenv(match.group(1), ""), text)
    data = json.loads(text)
    if not isinstance(data, dict):
        raise ValueError(f"Config root must be an object: {path}")
    return data


if __name__ == "__main__":
    raise SystemExit(main())
