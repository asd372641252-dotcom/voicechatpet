from __future__ import annotations

import argparse
import json
import urllib.request
from typing import Any


def main() -> int:
    parser = argparse.ArgumentParser(description="Probe desktop pet Agent speaker plugin API.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=17342)
    parser.add_argument("--text", default="外挂链路测试。收到就眨一下，别装卡。")
    parser.add_argument("--voice", action="store_true", help="Also request cloned-voice TTS.")
    args = parser.parse_args()

    base = f"http://{args.host}:{args.port}"
    print(_get_json(f"{base}/v1/capabilities"))
    print(
        _post_json(
            f"{base}/v1/say",
            {
                "text": args.text,
                "emotion": "mocking",
                "gesture": "small_tease",
                "posture": "stand",
                "voice": args.voice,
                "priority": 70,
            },
        )
    )
    return 0


def _get_json(url: str) -> Any:
    with urllib.request.urlopen(url, timeout=5) as response:
        return json.loads(response.read().decode("utf-8"))


def _post_json(url: str, payload: dict[str, Any]) -> Any:
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=data,
        headers={"Content-Type": "application/json; charset=utf-8"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=5) as response:
        return json.loads(response.read().decode("utf-8"))


if __name__ == "__main__":
    raise SystemExit(main())
