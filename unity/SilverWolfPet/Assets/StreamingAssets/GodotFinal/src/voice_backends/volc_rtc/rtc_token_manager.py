from __future__ import annotations

import json
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Mapping


ROOT = Path(__file__).resolve().parents[3]
TOKEN_SRC = ROOT / "RTC_Token" / "python" / "src"
DEFAULT_TOKEN_TTL_SEC = 7 * 24 * 3600
DEFAULT_REFRESH_MARGIN_SEC = 30 * 60


@dataclass(frozen=True)
class RtcTokenStatus:
    app_id: str = ""
    room_id: str = ""
    user_id: str = ""
    expire_at: int = 0
    expired: bool = True
    expires_in_sec: int = 0
    verified: bool = False
    matches_config: bool = False
    needs_refresh: bool = True
    reason: str = ""


@dataclass(frozen=True)
class RtcTokenRefreshResult:
    refreshed: bool
    status: RtcTokenStatus
    config_path: Path


def ensure_fresh_rtc_token_file(
    config_path: str | Path,
    *,
    ttl_sec: int = DEFAULT_TOKEN_TTL_SEC,
    refresh_margin_sec: int = DEFAULT_REFRESH_MARGIN_SEC,
) -> RtcTokenRefreshResult:
    path = Path(config_path)
    data = json.loads(path.read_text(encoding="utf-8"))
    refreshed = ensure_fresh_rtc_token(
        data,
        ttl_sec=ttl_sec,
        refresh_margin_sec=refresh_margin_sec,
    )
    if refreshed:
        with path.open("w", encoding="utf-8", newline="\n") as handle:
            handle.write(json.dumps(data, ensure_ascii=False, indent=2) + "\n")
    return RtcTokenRefreshResult(
        refreshed=refreshed,
        status=inspect_rtc_token(data, refresh_margin_sec=refresh_margin_sec),
        config_path=path,
    )


def ensure_fresh_rtc_token(
    config: dict[str, Any],
    *,
    ttl_sec: int = DEFAULT_TOKEN_TTL_SEC,
    refresh_margin_sec: int = DEFAULT_REFRESH_MARGIN_SEC,
) -> bool:
    status = inspect_rtc_token(config, refresh_margin_sec=refresh_margin_sec)
    if not status.needs_refresh:
        return False

    start = _start_voice_chat(config)
    client = _client_rtc(config)
    app_id, app_key, room_id, user_id = _required_token_fields(config)
    client["Token"] = generate_rtc_token(
        app_id=app_id,
        app_key=app_key,
        room_id=room_id,
        user_id=user_id,
        ttl_sec=ttl_sec,
    )
    client["UserId"] = user_id
    client["AppKey"] = app_key
    start["AppId"] = app_id
    start["RoomId"] = room_id
    return True


def inspect_rtc_token(
    config: Mapping[str, Any],
    *,
    refresh_margin_sec: int = DEFAULT_REFRESH_MARGIN_SEC,
) -> RtcTokenStatus:
    now = int(time.time())
    try:
        app_id, app_key, room_id, user_id = _required_token_fields(config)
    except ValueError as exc:
        return RtcTokenStatus(reason=str(exc))

    token_text = str(_client_rtc(config).get("Token") or _client_rtc(config).get("token") or "")
    if not token_text:
        return RtcTokenStatus(reason="missing ClientRTC.Token")

    token = _parse_rtc_token(token_text)
    if token is None:
        return RtcTokenStatus(reason="token parse failed")

    expires_in = int(token.expire_at) - now if int(token.expire_at) > 0 else 0
    expired = 0 < int(token.expire_at) <= now
    verified = bool(token.verify(app_key))
    matches = token.app_id == app_id and token.room_id == room_id and token.user_id == user_id
    needs_refresh = expired or expires_in <= refresh_margin_sec or not verified or not matches

    reason = ""
    if expired:
        reason = "expired"
    elif expires_in <= refresh_margin_sec:
        reason = "near_expiry"
    elif not verified:
        reason = "app_key_verify_failed"
    elif not matches:
        reason = "token_identity_mismatch"

    return RtcTokenStatus(
        app_id=str(token.app_id),
        room_id=str(token.room_id),
        user_id=str(token.user_id),
        expire_at=int(token.expire_at),
        expired=expired,
        expires_in_sec=max(0, expires_in),
        verified=verified,
        matches_config=matches,
        needs_refresh=needs_refresh,
        reason=reason,
    )


def generate_rtc_token(
    *,
    app_id: str,
    app_key: str,
    room_id: str,
    user_id: str,
    ttl_sec: int,
) -> str:
    access_token = _load_access_token_module()
    expire_at = int(time.time()) + max(60, int(ttl_sec))
    token = access_token.AccessToken(app_id, app_key, room_id, user_id)
    token.add_privilege(access_token.PrivSubscribeStream, expire_at)
    token.add_privilege(access_token.PrivPublishStream, expire_at)
    token.expire_time(expire_at)
    return token.serialize()


def _parse_rtc_token(token_text: str) -> Any:
    access_token = _load_access_token_module()
    return access_token.parse(token_text)


def _load_access_token_module() -> Any:
    if str(TOKEN_SRC) not in sys.path:
        sys.path.insert(0, str(TOKEN_SRC))
    import AccessToken  # type: ignore

    return AccessToken


def _required_token_fields(config: Mapping[str, Any]) -> tuple[str, str, str, str]:
    start = _start_voice_chat(config)
    client = _client_rtc(config)
    agent = start.get("AgentConfig", {})
    target_users = agent.get("TargetUserId", []) if isinstance(agent, Mapping) else []

    app_id = str(start.get("AppId") or client.get("AppId") or "")
    app_key = str(client.get("AppKey") or "")
    room_id = str(start.get("RoomId") or client.get("RoomId") or "")
    user_id = _first_text(target_users) or str(client.get("UserId") or client.get("UserID") or "")

    missing = [
        name
        for name, value in {
            "StartVoiceChat.AppId": app_id,
            "ClientRTC.AppKey": app_key,
            "StartVoiceChat.RoomId": room_id,
            "AgentConfig.TargetUserId[0] / ClientRTC.UserId": user_id,
        }.items()
        if not value
    ]
    if missing:
        raise ValueError("missing token fields: " + ", ".join(missing))
    return app_id, app_key, room_id, user_id


def _start_voice_chat(config: Mapping[str, Any]) -> dict[str, Any]:
    start = config.get("StartVoiceChat", config.get("start_voice_chat", config))
    if not isinstance(start, dict):
        raise ValueError("StartVoiceChat config must be a JSON object")
    return start


def _client_rtc(config: Mapping[str, Any]) -> dict[str, Any]:
    client = config.get("ClientRTC", {})
    if not isinstance(client, dict):
        raise ValueError("ClientRTC config must be a JSON object")
    return client


def _first_text(value: Any) -> str:
    if isinstance(value, list) and value:
        return str(value[0])
    if isinstance(value, str):
        return value
    return ""
