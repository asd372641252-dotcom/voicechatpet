from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
import urllib.request
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_CONFIG = ROOT / "config" / "volc_start_voice_chat.local.json"
DEFAULT_LOG = ROOT / "logs" / "screen_vision_offline_probe.jsonl"


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser(description="Offline probe for S2S screen-vision compatibility guard.")
    parser.add_argument("--config", default=str(DEFAULT_CONFIG))
    parser.add_argument("--port", type=int, default=17972)
    parser.add_argument("--log", default=str(DEFAULT_LOG))
    args = parser.parse_args()

    config_path = Path(args.config)
    raw_log_path = Path(args.log)
    raw_log_path.parent.mkdir(parents=True, exist_ok=True)
    if raw_log_path.exists():
        raw_log_path.unlink()

    checks: list[tuple[str, bool, str]] = []
    config = _load_json(config_path)
    vision = _pick(config, "StartVoiceChat", "Config", "LLMConfig", "VisionConfig")
    checks.append(("config has no S2S VisionConfig", not bool(vision), str(vision)))

    proc = subprocess.Popen(
        [
            sys.executable,
            str(ROOT / "scripts" / "run_volc_rtc_web_client.py"),
            "--config",
            str(config_path),
            "--port",
            str(args.port),
            "--godot-port",
            "17961",
            "--raw-log",
            str(raw_log_path),
        ],
        cwd=str(ROOT),
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
    )

    try:
        _wait_health(args.port)
        status = _get_json(args.port, "/api/vision/status")
        checks.append(("initial desired=false", status.get("desired") is False, json.dumps(status, ensure_ascii=False)))
        checks.append(
            (
                "S2S vision unsupported flag",
                status.get("s2sVisionSupported") is False,
                json.dumps(status, ensure_ascii=False),
            )
        )

        start = _post_json(args.port, "/api/vision/start", {})
        checks.append(("manual start rejected", start.get("desired") is False, json.dumps(start, ensure_ascii=False)))
        checks.append(
            (
                "manual start requires sidecar",
                start.get("mode") == "ark_multimodal_sidecar_required",
                json.dumps(start, ensure_ascii=False),
            )
        )

        client_state = _post_json(
            args.port,
            "/api/vision/client_state",
            {"screenPublished": True, "message": "offline simulated screen stream published"},
        )
        checks.append(
            (
                "client_state not desired",
                client_state.get("desired") is False,
                json.dumps(client_state, ensure_ascii=False),
            )
        )

        stop = _post_json(args.port, "/api/vision/stop", {})
        checks.append(("manual stop desired=false", stop.get("desired") is False, json.dumps(stop, ensure_ascii=False)))

        event = _post_json(
            args.port,
            "/api/event",
            {
                "event_type": "subtitle_event",
                "trace_id": "offline-screen-vision-intent",
                "payload": {
                    "userId": "local_user_001",
                    "text": "帮我看一下屏幕，现在该怎么走？",
                    "definite": True,
                },
            },
        )
        checks.append(("user subtitle routed", event.get("handled") is True, json.dumps(event, ensure_ascii=False)))

        after_intent = _get_json(args.port, "/api/vision/status")
        checks.append(
            (
                "voice intent rejected",
                after_intent.get("desired") is False,
                json.dumps(after_intent, ensure_ascii=False),
            )
        )

        _post_json(args.port, "/api/vision/stop", {})
        time.sleep(0.2)
        log_text = raw_log_path.read_text(encoding="utf-8") if raw_log_path.exists() else ""
        checks.append(("raw log vision_start_rejected", "vision_start_rejected" in log_text, raw_log_path.as_posix()))
        checks.append(("raw log vision_client_state", "vision_client_state" in log_text, raw_log_path.as_posix()))
        checks.append(("raw log user subtitle vision intent", "screen_vision_intent" in log_text, raw_log_path.as_posix()))
    finally:
        proc.terminate()
        try:
            output, _ = proc.communicate(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
            output, _ = proc.communicate(timeout=5)

    failed = [item for item in checks if not item[1]]
    for name, ok, detail in checks:
        prefix = "PASS" if ok else "FAIL"
        print(f"{prefix} {name}: {detail}")
    if output.strip():
        print("--- bridge output tail ---")
        print(output[-2000:])
    if failed:
        return 1
    print("PASS S2S screen-vision guard offline probe")
    return 0


def _load_json(path: Path) -> dict[str, Any]:
    data = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(data, dict):
        raise ValueError(f"config root must be object: {path}")
    return data


def _pick(root: dict[str, Any], *path: str) -> Any:
    current: Any = root
    for part in path:
        if not isinstance(current, dict):
            return {}
        current = current.get(part, {})
    return current


def _wait_health(port: int) -> None:
    deadline = time.time() + 12.0
    while time.time() < deadline:
        try:
            _get_json(port, "/api/health", timeout=0.5)
            return
        except Exception:
            time.sleep(0.25)
    raise TimeoutError("bridge health endpoint did not become ready")


def _get_json(port: int, path: str, timeout: float = 2.0) -> dict[str, Any]:
    with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}", timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8"))


def _post_json(port: int, path: str, payload: dict[str, Any]) -> dict[str, Any]:
    request = urllib.request.Request(
        f"http://127.0.0.1:{port}{path}",
        data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        headers={"Content-Type": "application/json; charset=utf-8"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=3.0) as response:
        return json.loads(response.read().decode("utf-8"))


if __name__ == "__main__":
    raise SystemExit(main())
