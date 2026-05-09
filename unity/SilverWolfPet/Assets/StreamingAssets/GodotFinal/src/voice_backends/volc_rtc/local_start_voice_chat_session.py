from __future__ import annotations

import logging
import json
import time
from typing import Any, Mapping

from .volc_openapi_client import VolcOpenAPIClient, build_stop_voice_chat_request


LOGGER = logging.getLogger(__name__)


class LocalStartVoiceChatSession:
    """OpenAPI-only StartVoiceChat session.

    This is a smoke-test session factory. It starts/stops the cloud task but does
    not join RTC or receive SDK callbacks by itself.
    """

    def __init__(self, *, config: Mapping[str, Any], request: Mapping[str, Any]) -> None:
        self.config = config
        self.request = dict(request)
        self.client = VolcOpenAPIClient.from_config(config)
        self.started = False
        self.start_response: dict[str, Any] | None = None
        self.stop_response: dict[str, Any] | None = None

    def start(self) -> None:
        LOGGER.info(
            "Starting Volc StartVoiceChat task app_id_len=%s room_id=%s task_id=%s bot_uid=%s target_user=%s",
            len(str(self.request.get("AppId") or "")),
            self.request.get("RoomId"),
            self.request.get("TaskId"),
            self.request.get("AgentConfig", {}).get("UserId"),
            self.request.get("AgentConfig", {}).get("TargetUserId"),
        )
        self.start_response = self.client.start_voice_chat(self.request)
        self.started = True
        LOGGER.info(
            "StartVoiceChat accepted response=%s",
            json.dumps(_redact_for_log(self.start_response), ensure_ascii=False, separators=(",", ":")),
        )

    def wait(self, timeout: float | None = None) -> None:
        if timeout and timeout > 0:
            time.sleep(timeout)

    def stop(self) -> None:
        if not self.started:
            return
        stop_request = build_stop_voice_chat_request(self.request)
        LOGGER.info("Stopping Volc VoiceChat task room_id=%s task_id=%s", stop_request["RoomId"], stop_request["TaskId"])
        self.stop_response = self.client.stop_voice_chat(stop_request)
        self.started = False
        LOGGER.info(
            "StopVoiceChat accepted response=%s",
            json.dumps(_redact_for_log(self.stop_response), ensure_ascii=False, separators=(",", ":")),
        )


def create_session(
    *,
    config: Mapping[str, Any],
    request: Mapping[str, Any],
    callbacks: Any = None,
    adapter: Any = None,
) -> LocalStartVoiceChatSession:
    return LocalStartVoiceChatSession(config=config, request=request)


def _redact_for_log(value: Any) -> Any:
    if isinstance(value, Mapping):
        redacted: dict[str, Any] = {}
        for key, item in value.items():
            key_text = str(key).lower()
            if any(part in key_text for part in ("token", "secret", "apikey", "api_key", "accesskey", "authorization")):
                redacted[str(key)] = "***"
            else:
                redacted[str(key)] = _redact_for_log(item)
        return redacted
    if isinstance(value, list):
        return [_redact_for_log(item) for item in value]
    return value
