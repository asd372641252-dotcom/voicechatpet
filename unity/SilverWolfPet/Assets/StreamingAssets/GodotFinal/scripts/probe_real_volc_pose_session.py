from __future__ import annotations

import argparse
import importlib
import json
import logging
import os
import re
import sys
import time
from pathlib import Path
from typing import Any, Callable, Mapping

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from src.pet_pose_bridge import GodotPoseClient
from src.voice_backends.volc_rtc import (
    VolcPoseEventAdapter,
    VolcSessionCallbackBridge,
    check_start_voice_chat_config,
)


DEFAULT_CONFIG_PATH = ROOT / "config" / "volc_start_voice_chat.local.json"
EXAMPLE_CONFIG_PATH = ROOT / "config" / "volc_start_voice_chat.example.json"
DEFAULT_RAW_LOG_PATH = ROOT / "logs" / "volc_pose_raw_events.jsonl"


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Run a real Volc StartVoiceChat / RTC session and bridge callbacks to Godot pose events."
    )
    parser.add_argument("--config", default=str(DEFAULT_CONFIG_PATH), help="Local StartVoiceChat JSON config.")
    parser.add_argument(
        "--session-factory",
        default=os.getenv("VOLC_SESSION_FACTORY", ""),
        help=(
            "Python factory in module:function form. It receives request, callbacks, adapter. "
            "Use this to hook your existing Volc SDK backend without rewriting it."
        ),
    )
    parser.add_argument("--godot-host", default="127.0.0.1")
    parser.add_argument("--godot-port", type=int, default=17865)
    parser.add_argument("--bot-uid", default=os.getenv("VOLC_RTC_BOT_UID", ""))
    parser.add_argument("--duration-sec", type=float, default=120.0)
    parser.add_argument("--raw-log", default=str(DEFAULT_RAW_LOG_PATH))
    parser.add_argument("--check-only", action="store_true")
    parser.add_argument("--require-godot", action="store_true")
    parser.add_argument("--print-template", action="store_true")
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    logger = logging.getLogger("probe_real_volc_pose_session")

    if args.print_template:
        print(EXAMPLE_CONFIG_PATH.read_text(encoding="utf-8"))
        return 0

    config_path = Path(args.config)
    if not config_path.exists():
        logger.error(
            "StartVoiceChat config missing: %s. Copy %s and fill real fields.",
            config_path,
            EXAMPLE_CONFIG_PATH,
        )
        return 2

    config = _load_json_with_env(config_path)
    request = _extract_start_voice_chat_request(config)
    bot_uid = args.bot_uid or _find_bot_uid(request)

    issues = check_start_voice_chat_config(request)
    if issues:
        logger.warning("StartVoiceChat config check found %d issue(s):", len(issues))
        for issue in issues:
            logger.warning("[%s] %s: %s", issue.severity, issue.key, issue.message)
    else:
        logger.info("StartVoiceChat config check passed.")

    sent_records: list[dict[str, Any]] = []

    def on_send(item, ok: bool) -> None:
        now = time.time()
        record = {
            "trace_id": item.trace_id,
            "source": item.source,
            "sent": ok,
            "event_to_pose_ms": _elapsed_ms(item.event_received_at, item.pose_generated_at),
            "pose_to_send_ms": _elapsed_ms(item.pose_generated_at, now),
            "event_to_send_ms": _elapsed_ms(item.event_received_at, now),
        }
        sent_records.append(record)
        logger.info("pose_send %s", json.dumps(record, ensure_ascii=False))

    adapter = VolcPoseEventAdapter(
        godot_client=GodotPoseClient(args.godot_host, args.godot_port, timeout_sec=0.25),
        bot_uids={bot_uid} if bot_uid else set(),
        raw_event_log_path=args.raw_log,
        on_send=on_send,
    )
    callbacks = VolcSessionCallbackBridge(adapter)

    if args.check_only:
        adapter.close()
        return 0

    if not args.session_factory:
        logger.error(
            "No real Volc session factory configured. For the current local client path, run "
            "`python scripts/run_volc_rtc_web_client.py --open-browser` and click Start, or pass "
            "--session-factory module:function for a native SDK backend."
        )
        adapter.close()
        return 3

    factory = _load_factory(args.session_factory)
    session = None
    try:
        session = _call_factory(factory, config=config, request=request, callbacks=callbacks, adapter=adapter)
        _start_session_if_possible(session)
        logger.info("Real Volc session started. raw event log: %s", args.raw_log)
        _wait_for_session(session, args.duration_sec)
    finally:
        _stop_session_if_possible(session)
        adapter.close()

    if args.require_godot and any(record["sent"] is False for record in sent_records):
        logger.error("At least one real Volc pose event failed to send to Godot.")
        return 4

    _print_latency_summary(sent_records, logger)
    return 0


def _load_json_with_env(path: Path) -> dict[str, Any]:
    text = path.read_text(encoding="utf-8")
    text = re.sub(r"\$\{([A-Z0-9_]+)\}", lambda match: os.getenv(match.group(1), ""), text)
    data = json.loads(text)
    if not isinstance(data, dict):
        raise ValueError(f"Config root must be a JSON object: {path}")
    return data


def _extract_start_voice_chat_request(config: Mapping[str, Any]) -> dict[str, Any]:
    request = config.get("StartVoiceChat", config.get("start_voice_chat", config))
    if not isinstance(request, dict):
        raise ValueError("StartVoiceChat config must be a JSON object")
    return request


def _find_bot_uid(request: Mapping[str, Any]) -> str:
    for key in ("BotUid", "BotUID", "bot_uid", "BotUserId", "BotUserID", "botUserId"):
        if key in request and request[key]:
            return str(request[key])
    for value in request.values():
        if isinstance(value, Mapping):
            found = _find_bot_uid(value)
            if found:
                return found
    return ""


def _load_factory(spec: str) -> Callable[..., Any]:
    if ":" not in spec:
        raise ValueError("--session-factory must be in module:function form")
    module_name, function_name = spec.split(":", 1)
    module = importlib.import_module(module_name)
    factory = getattr(module, function_name)
    if not callable(factory):
        raise TypeError(f"Session factory is not callable: {spec}")
    return factory


def _call_factory(
    factory: Callable[..., Any],
    *,
    config: Mapping[str, Any],
    request: Mapping[str, Any],
    callbacks: VolcSessionCallbackBridge,
    adapter: VolcPoseEventAdapter,
) -> Any:
    try:
        return factory(config=config, request=request, callbacks=callbacks, adapter=adapter)
    except TypeError:
        pass
    try:
        return factory(request=request, callbacks=callbacks, adapter=adapter)
    except TypeError:
        try:
            return factory(request, callbacks, adapter)
        except TypeError:
            return factory(request, callbacks)


def _start_session_if_possible(session: Any) -> None:
    for method_name in ("start", "join", "run", "connect"):
        method = getattr(session, method_name, None)
        if callable(method):
            method()
            return


def _wait_for_session(session: Any, duration_sec: float) -> None:
    deadline = time.monotonic() + max(0.0, duration_sec)
    wait = getattr(session, "wait", None)
    if callable(wait):
        remaining = max(0.0, deadline - time.monotonic())
        try:
            wait(timeout=remaining)
            return
        except TypeError:
            wait()
            return
    while time.monotonic() < deadline:
        time.sleep(0.2)


def _stop_session_if_possible(session: Any) -> None:
    if session is None:
        return
    for method_name in ("stop", "close", "leave", "disconnect"):
        method = getattr(session, method_name, None)
        if callable(method):
            try:
                method()
            except Exception:
                logging.getLogger("probe_real_volc_pose_session").exception("session %s failed", method_name)
            return


def _elapsed_ms(start: float, end: float) -> float | None:
    if not start or not end:
        return None
    return round((end - start) * 1000.0, 3)


def _print_latency_summary(records: list[dict[str, Any]], logger: logging.Logger) -> None:
    if not records:
        logger.warning("No real Volc pose events were sent.")
        return
    latencies = [record["event_to_send_ms"] for record in records if record["event_to_send_ms"] is not None]
    if not latencies:
        return
    logger.info(
        "latency_summary count=%d min_ms=%.3f avg_ms=%.3f max_ms=%.3f",
        len(latencies),
        min(latencies),
        sum(latencies) / len(latencies),
        max(latencies),
    )


if __name__ == "__main__":
    raise SystemExit(main())
