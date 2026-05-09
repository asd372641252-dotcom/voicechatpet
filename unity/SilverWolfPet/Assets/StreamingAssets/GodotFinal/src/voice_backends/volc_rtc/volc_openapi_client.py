from __future__ import annotations

import datetime as dt
import hashlib
import hmac
import json
from dataclasses import dataclass
from typing import Any, Mapping
from urllib.parse import quote

import requests


@dataclass(frozen=True)
class VolcOpenAPIConfig:
    access_key_id: str
    secret_access_key: str
    host: str = "rtc.volcengineapi.com"
    region: str = "cn-north-1"
    service: str = "rtc"
    start_version: str = "2025-06-01"
    stop_version: str = "2025-06-01"
    update_version: str = "2025-06-01"
    timeout_sec: float = 15.0


class VolcOpenAPIClient:
    """Small Volc OpenAPI HMAC client for StartVoiceChat / StopVoiceChat smoke tests."""

    def __init__(self, config: VolcOpenAPIConfig) -> None:
        if not config.access_key_id or not config.secret_access_key:
            raise ValueError("Volc OpenAPI access key and secret key are required.")
        self.config = config

    @classmethod
    def from_config(cls, config: Mapping[str, Any]) -> "VolcOpenAPIClient":
        auth = config.get("OpenAPIAuth") or {}
        api = config.get("OpenAPI") or {}
        if not isinstance(auth, Mapping) or not isinstance(api, Mapping):
            raise ValueError("OpenAPIAuth/OpenAPI must be JSON objects.")
        return cls(
            VolcOpenAPIConfig(
                access_key_id=str(auth.get("AccessKeyId") or ""),
                secret_access_key=str(auth.get("SecretAccessKey") or ""),
                host=str(api.get("Host") or "rtc.volcengineapi.com"),
                region=str(api.get("Region") or "cn-north-1"),
                service=str(api.get("Service") or "rtc"),
                start_version=str(api.get("StartVoiceChatVersion") or "2025-06-01"),
                stop_version=str(api.get("StopVoiceChatVersion") or "2025-06-01"),
                update_version=str(api.get("UpdateVoiceChatVersion") or api.get("StartVoiceChatVersion") or "2025-06-01"),
            )
        )

    def start_voice_chat(self, request: Mapping[str, Any]) -> dict[str, Any]:
        return self._post("StartVoiceChat", self.config.start_version, request)

    def stop_voice_chat(self, request: Mapping[str, Any]) -> dict[str, Any]:
        return self._post("StopVoiceChat", self.config.stop_version, request)

    def update_voice_chat(self, request: Mapping[str, Any]) -> dict[str, Any]:
        return self._post("UpdateVoiceChat", self.config.update_version, request)

    def _post(self, action: str, version: str, body: Mapping[str, Any]) -> dict[str, Any]:
        body_bytes = json.dumps(body, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        query = {"Action": action, "Version": version}
        url = f"https://{self.config.host}/?{_canonical_query(query)}"
        headers = self._signed_headers(body_bytes, query)
        response = requests.post(url, data=body_bytes, headers=headers, timeout=self.config.timeout_sec)
        try:
            payload = response.json()
        except ValueError:
            payload = {"raw_text": response.text}
        if response.status_code >= 400:
            raise RuntimeError(f"{action} failed http_status={response.status_code} response={payload}")
        response_error = _response_metadata_error(payload)
        if response_error:
            raise RuntimeError(f"{action} failed response_error={response_error} response={payload}")
        return payload

    def _signed_headers(self, body: bytes, query: Mapping[str, str]) -> dict[str, str]:
        now = dt.datetime.utcnow()
        x_date = now.strftime("%Y%m%dT%H%M%SZ")
        short_date = now.strftime("%Y%m%d")
        body_hash = hashlib.sha256(body).hexdigest()
        content_type = "application/json; charset=utf-8"
        canonical_headers = (
            f"content-type:{content_type}\n"
            f"host:{self.config.host}\n"
            f"x-content-sha256:{body_hash}\n"
            f"x-date:{x_date}\n"
        )
        signed_headers = "content-type;host;x-content-sha256;x-date"
        canonical_request = "\n".join(
            [
                "POST",
                "/",
                _canonical_query(query),
                canonical_headers,
                signed_headers,
                body_hash,
            ]
        )
        credential_scope = f"{short_date}/{self.config.region}/{self.config.service}/request"
        string_to_sign = "\n".join(
            [
                "HMAC-SHA256",
                x_date,
                credential_scope,
                hashlib.sha256(canonical_request.encode("utf-8")).hexdigest(),
            ]
        )
        signing_key = _signing_key(
            self.config.secret_access_key,
            short_date,
            self.config.region,
            self.config.service,
        )
        signature = hmac.new(signing_key, string_to_sign.encode("utf-8"), hashlib.sha256).hexdigest()
        authorization = (
            f"HMAC-SHA256 Credential={self.config.access_key_id}/{credential_scope}, "
            f"SignedHeaders={signed_headers}, Signature={signature}"
        )
        return {
            "Authorization": authorization,
            "Content-Type": content_type,
            "Host": self.config.host,
            "X-Content-Sha256": body_hash,
            "X-Date": x_date,
        }


def build_stop_voice_chat_request(start_request: Mapping[str, Any]) -> dict[str, Any]:
    return {
        "AppId": start_request.get("AppId"),
        "RoomId": start_request.get("RoomId"),
        "TaskId": start_request.get("TaskId"),
    }


def _response_metadata_error(payload: Any) -> Any:
    if not isinstance(payload, Mapping):
        return None
    metadata = payload.get("ResponseMetadata")
    if not isinstance(metadata, Mapping):
        return None
    error = metadata.get("Error")
    if isinstance(error, Mapping) and (error.get("Code") or error.get("Message")):
        return error
    return None


def build_update_voice_chat_function_result_request(
    start_request: Mapping[str, Any],
    *,
    tool_call_id: str,
    content: str,
    response_id: str = "",
) -> dict[str, Any]:
    message: dict[str, Any] = {
        "ToolCallID": tool_call_id,
        "Content": content,
    }
    if response_id:
        message["ResponseId"] = response_id
        message["response_id"] = response_id
    return {
        "AppId": start_request.get("AppId"),
        "RoomId": start_request.get("RoomId"),
        "TaskId": start_request.get("TaskId"),
        "Command": "function",
        "Message": json.dumps(message, ensure_ascii=False, separators=(",", ":")),
    }


def _signing_key(secret: str, short_date: str, region: str, service: str) -> bytes:
    k_date = _hmac(secret.encode("utf-8"), short_date)
    k_region = _hmac(k_date, region)
    k_service = _hmac(k_region, service)
    return _hmac(k_service, "request")


def _hmac(key: bytes, message: str) -> bytes:
    return hmac.new(key, message.encode("utf-8"), hashlib.sha256).digest()


def _canonical_query(query: Mapping[str, str]) -> str:
    return "&".join(f"{quote(str(key), safe='-_.~')}={quote(str(query[key]), safe='-_.~')}" for key in sorted(query))
