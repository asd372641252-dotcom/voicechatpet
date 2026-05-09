from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_TEMPLATE = ROOT / "config" / "volc_start_voice_chat.example.json"
DEFAULT_OUTPUT = ROOT / "config" / "volc_start_voice_chat.local.json"
TOKEN_SRC = ROOT / "RTC_Token" / "python" / "src"

DEFAULT_ROOM_ID = "silver_wolf_room_001"
DEFAULT_USER_ID = "local_user_001"
DEFAULT_BOT_UID = "silver_wolf_bot_001"
DEFAULT_S2S_MODEL_VERSION = "2.2.0.0"
DEFAULT_SPEAKER_ID = "S_PfaVJY802"
DEFAULT_LLM_MAX_TOKENS = 512
DEFAULT_CHARACTER_MANIFEST_PATH = ROOT / "config" / "text" / "silver_wolf_persona.txt"

REQUIRED_ENV = (
    "VOLC_ACCESS_KEY_ID",
    "VOLC_SECRET_ACCESS_KEY",
    "VOLC_RTC_APP_ID",
    "VOLC_RTC_APP_KEY",
    "VOLC_S2S_APP_ID",
    "VOLC_S2S_ACCESS_TOKEN",
    "VOLC_ARK_ENDPOINT_ID",
)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Create ignored local Volc StartVoiceChat config and RTC token from environment variables."
    )
    parser.add_argument("--template", default=str(DEFAULT_TEMPLATE))
    parser.add_argument("--output", default=str(DEFAULT_OUTPUT))
    parser.add_argument("--room-id", default=os.getenv("VOLC_RTC_ROOM_ID", DEFAULT_ROOM_ID))
    parser.add_argument("--user-id", default=os.getenv("VOLC_RTC_USER_ID", DEFAULT_USER_ID))
    parser.add_argument("--bot-uid", default=os.getenv("VOLC_RTC_BOT_UID", DEFAULT_BOT_UID))
    parser.add_argument("--task-id", default=os.getenv("VOLC_RTC_TASK_ID", ""))
    parser.add_argument("--token-ttl-sec", type=int, default=7200)
    parser.add_argument("--force", action="store_true", help="Overwrite existing local config.")
    args = parser.parse_args()

    missing = [name for name in REQUIRED_ENV if not os.getenv(name)]
    if missing:
        print("Missing required environment variables:", ", ".join(missing), file=sys.stderr)
        print("Set them in your local shell first. No config was written.", file=sys.stderr)
        return 2

    template_path = Path(args.template)
    output_path = Path(args.output)
    if output_path.exists() and not args.force:
        print(f"Refusing to overwrite existing file: {output_path}", file=sys.stderr)
        print("Pass --force if you intentionally want to regenerate it.", file=sys.stderr)
        return 3

    task_id = args.task_id or f"silver_wolf_task_{time.strftime('%Y%m%d_%H%M%S')}"
    rtc_token = os.getenv("VOLC_RTC_TOKEN") or _generate_rtc_token(
        app_id=os.environ["VOLC_RTC_APP_ID"],
        app_key=os.environ["VOLC_RTC_APP_KEY"],
        room_id=args.room_id,
        user_id=args.user_id,
        ttl_sec=args.token_ttl_sec,
    )

    replacements = {
        "VOLC_ACCESS_KEY_ID": os.environ["VOLC_ACCESS_KEY_ID"],
        "VOLC_SECRET_ACCESS_KEY": os.environ["VOLC_SECRET_ACCESS_KEY"],
        "VOLC_RTC_APP_ID": os.environ["VOLC_RTC_APP_ID"],
        "VOLC_RTC_APP_KEY": os.environ["VOLC_RTC_APP_KEY"],
        "VOLC_RTC_ROOM_ID": args.room_id,
        "VOLC_RTC_USER_ID": args.user_id,
        "VOLC_RTC_BOT_UID": args.bot_uid,
        "VOLC_RTC_TASK_ID": task_id,
        "VOLC_RTC_TOKEN": rtc_token,
        "VOLC_S2S_APP_ID": os.environ["VOLC_S2S_APP_ID"],
        "VOLC_S2S_ACCESS_TOKEN": os.environ["VOLC_S2S_ACCESS_TOKEN"],
        "VOLC_S2S_MODEL_VERSION": os.getenv("VOLC_S2S_MODEL_VERSION", DEFAULT_S2S_MODEL_VERSION),
        "VOLC_S2S_CHARACTER_MANIFEST": os.getenv(
            "VOLC_S2S_CHARACTER_MANIFEST",
            _read_default_character_manifest(),
        ),
        "VOLC_S2S_SPEAKER_ID": os.getenv("VOLC_S2S_SPEAKER_ID", DEFAULT_SPEAKER_ID),
        "VOLC_ARK_ENDPOINT_ID": os.environ["VOLC_ARK_ENDPOINT_ID"],
        "VOLC_WEBSEARCH_API_KEY": os.getenv("VOLC_WEBSEARCH_API_KEY", ""),
    }

    config_text = template_path.read_text(encoding="utf-8")
    config_text = re.sub(r"\$\{([A-Z0-9_]+)\}", lambda match: replacements.get(match.group(1), ""), config_text)
    config = json.loads(config_text)
    llm_config = config.get("StartVoiceChat", {}).get("Config", {}).get("LLMConfig", {})
    if isinstance(llm_config, dict):
        llm_config["MaxTokens"] = int(os.getenv("VOLC_ARK_MAX_TOKENS", DEFAULT_LLM_MAX_TOKENS))
    s2s_extra = (
        config.get("StartVoiceChat", {})
        .get("Config", {})
        .get("S2SConfig", {})
        .get("ProviderParams", {})
        .get("dialog", {})
        .get("extra", {})
    )
    if isinstance(s2s_extra, dict):
        websearch_key = os.getenv("VOLC_WEBSEARCH_API_KEY", "").strip()
        # This field is for the S2S built-in web-search plugin API key.
        # TOP gateway AK/SK search is configured separately in WebSearchOpenAPI.
        s2s_extra["enable_volc_websearch"] = _env_bool("VOLC_ENABLE_S2S_BUILTIN_WEBSEARCH", bool(websearch_key))
        s2s_extra["volc_websearch_api_key"] = websearch_key
        s2s_extra["volc_websearch_type"] = os.getenv("VOLC_WEBSEARCH_TYPE", "web_summary")
        s2s_extra["volc_websearch_no_result_message"] = os.getenv(
            "VOLC_WEBSEARCH_NO_RESULT_MESSAGE",
            "啧，网络上没刷到相关信息。",
        )
    websearch_openapi = config.get("WebSearchOpenAPI", {})
    if isinstance(websearch_openapi, dict):
        websearch_openapi["Enabled"] = _env_bool("VOLC_ENABLE_WEBSEARCH", bool(os.getenv("VOLC_ENABLE_WEBSEARCH")))
        websearch_openapi["SearchType"] = os.getenv("VOLC_WEBSEARCH_TYPE", "web_summary")

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("w", encoding="utf-8", newline="\n") as handle:
        handle.write(json.dumps(config, ensure_ascii=False, indent=2) + "\n")

    print(f"Wrote local config: {output_path}")
    print(f"RoomId={args.room_id}")
    print(f"UserId={args.user_id}")
    print(f"BotUid={args.bot_uid}")
    print(f"TaskId={task_id}")
    print("Secrets and RTC token were written only to the ignored local config and were not printed.")
    return 0


def _generate_rtc_token(*, app_id: str, app_key: str, room_id: str, user_id: str, ttl_sec: int) -> str:
    if str(TOKEN_SRC) not in sys.path:
        sys.path.insert(0, str(TOKEN_SRC))
    try:
        import AccessToken  # type: ignore
    except ImportError as exc:
        raise RuntimeError(f"Unable to import RTC token library from {TOKEN_SRC}") from exc

    expire_at = int(time.time()) + max(60, ttl_sec)
    token = AccessToken.AccessToken(app_id, app_key, room_id, user_id)
    token.add_privilege(AccessToken.PrivSubscribeStream, expire_at)
    token.add_privilege(AccessToken.PrivPublishStream, expire_at)
    token.expire_time(expire_at)
    return token.serialize()


def _read_default_character_manifest() -> str:
    if DEFAULT_CHARACTER_MANIFEST_PATH.exists():
        return DEFAULT_CHARACTER_MANIFEST_PATH.read_text(encoding="utf-8").strip()
    return "Silver Wolf desktop pet persona."


def _env_bool(name: str, default: bool) -> bool:
    value = os.getenv(name)
    if value is None or not value.strip():
        return default
    return value.strip().lower() in {"1", "true", "yes", "y", "on"}


if __name__ == "__main__":
    raise SystemExit(main())
