from __future__ import annotations

import datetime as dt
import hashlib
import hmac
import json
from dataclasses import dataclass
from typing import Any, Iterator, Mapping
from urllib.parse import quote

import requests


@dataclass(frozen=True)
class VolcWebSearchConfig:
    access_key_id: str
    secret_access_key: str
    host: str = "mercury.volcengineapi.com"
    region: str = "cn-north-1"
    service: str = "volc_torchlight_api"
    version: str = "2025-01-01"
    search_type: str = "web_summary"
    count: int = 3
    timeout_sec: float = 12.0


class VolcWebSearchClient:
    """Volc TOP gateway WebSearch client using IAM AK/SK signing."""

    def __init__(self, config: VolcWebSearchConfig) -> None:
        if not config.access_key_id or not config.secret_access_key:
            raise ValueError("Volc WebSearch AK/SK are required.")
        self.config = config

    @classmethod
    def from_project_config(cls, config: Mapping[str, Any]) -> "VolcWebSearchClient | None":
        websearch = config.get("WebSearchOpenAPI") or {}
        if not isinstance(websearch, Mapping) or not bool(websearch.get("Enabled", False)):
            return None
        auth = config.get("OpenAPIAuth") or {}
        if not isinstance(auth, Mapping):
            return None
        return cls(
            VolcWebSearchConfig(
                access_key_id=str(auth.get("AccessKeyId") or ""),
                secret_access_key=str(auth.get("SecretAccessKey") or ""),
                host=str(websearch.get("Host") or "mercury.volcengineapi.com"),
                region=str(websearch.get("Region") or "cn-north-1"),
                service=str(websearch.get("Service") or "volc_torchlight_api"),
                version=str(websearch.get("Version") or "2025-01-01"),
                search_type=str(websearch.get("SearchType") or "web_summary"),
                count=int(websearch.get("Count") or 3),
                timeout_sec=float(websearch.get("TimeoutSec") or 12.0),
            )
        )

    def search(self, query: str, *, count: int | None = None, search_type: str | None = None) -> dict[str, Any]:
        chunks: list[dict[str, Any]] = []
        for payload in self.search_stream(query, count=count, search_type=search_type):
            chunks.append(payload)
        if not chunks:
            raise RuntimeError("WebSearch returned no stream payload.")
        return _aggregate_stream_payloads(chunks)

    def search_stream(
        self,
        query: str,
        *,
        count: int | None = None,
        search_type: str | None = None,
    ) -> Iterator[dict[str, Any]]:
        query = str(query or "").strip()
        if not query:
            raise ValueError("WebSearch query is empty.")
        body = {
            "Query": query,
            "SearchType": search_type or self.config.search_type,
            "Count": int(count or self.config.count),
            "Filter": {
                "NeedContent": False,
                "NeedUrl": True,
            },
            "NeedSummary": True,
        }
        yield from self._post_stream("WebSearch", self.config.version, body)

    def _post_stream(self, action: str, version: str, body: Mapping[str, Any]) -> Iterator[dict[str, Any]]:
        body_bytes = json.dumps(body, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        query = {"Action": action, "Version": version}
        url = f"https://{self.config.host}/?{_canonical_query(query)}"
        headers = self._signed_headers(body_bytes, query)
        with requests.post(
            url,
            data=body_bytes,
            headers=headers,
            timeout=self.config.timeout_sec,
            stream=True,
        ) as response:
            if response.status_code >= 400:
                payload = _parse_response_payload(response)
                raise RuntimeError(f"{action} failed http_status={response.status_code} response={payload}")

            emitted = False
            buffered_lines: list[str] = []
            for raw_line in response.iter_lines(decode_unicode=False):
                if not raw_line:
                    continue
                line = raw_line.decode("utf-8", errors="replace").strip()
                buffered_lines.append(line)
                payload = _parse_sse_line(line)
                if payload is None:
                    continue
                emitted = True
                yield _repair_mojibake(payload)

            if not emitted and buffered_lines:
                fallback_text = "\n".join(buffered_lines)
                fallback = _parse_sse_data(fallback_text) or {"raw_text": fallback_text}
                yield _repair_mojibake(fallback)

    def _signed_headers(self, body: bytes, query: Mapping[str, str]) -> dict[str, str]:
        now = dt.datetime.utcnow()
        x_date = now.strftime("%Y%m%dT%H%M%SZ")
        short_date = now.strftime("%Y%m%d")
        body_hash = hashlib.sha256(body).hexdigest()
        content_type = "application/json"
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


def compact_search_result(payload: Mapping[str, Any], *, max_chars: int = 1400) -> str:
    text = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
    if len(text) <= max_chars:
        return text
    return text[: max_chars - 1] + "…"


def format_search_answer_context(payload: Mapping[str, Any], *, max_chars: int = 1200) -> str:
    result = payload.get("Result") if isinstance(payload, Mapping) else None
    if not isinstance(result, Mapping):
        return compact_search_result(payload, max_chars=max_chars)

    summary_text = result.get("SummaryText")
    if isinstance(summary_text, str) and summary_text.strip():
        return _truncate_text(summary_text.strip(), max_chars=max_chars)

    web_results = result.get("WebResults")
    if not isinstance(web_results, list):
        return compact_search_result(payload, max_chars=max_chars)

    lines: list[str] = []
    for index, item in enumerate(web_results[:3], start=1):
        if not isinstance(item, Mapping):
            continue
        title = str(item.get("Title") or "").strip()
        site = str(item.get("SiteName") or "").strip()
        publish_time = str(item.get("PublishTime") or "").strip()
        summary = str(item.get("Summary") or item.get("Snippet") or item.get("Content") or "").strip()
        if not title and not summary:
            continue
        header = f"{index}. {title or '搜索结果'}"
        meta = "，".join(part for part in [site, publish_time[:10] if publish_time else ""] if part)
        if meta:
            header += f"（{meta}）"
        lines.append(header)
        if summary:
            lines.append(_truncate_text(_squash_space(summary), max_chars=360))

    if not lines:
        return compact_search_result(payload, max_chars=max_chars)
    return _truncate_text("\n".join(lines), max_chars=max_chars)


def _truncate_text(text: str, *, max_chars: int) -> str:
    if len(text) <= max_chars:
        return text
    return text[: max_chars - 1] + "…"


def _squash_space(text: str) -> str:
    return " ".join(text.replace("\r", "\n").split())


def _aggregate_stream_payloads(chunks: list[dict[str, Any]]) -> dict[str, Any]:
    if len(chunks) == 1:
        return chunks[0]

    first = chunks[0]
    last = chunks[-1]
    summary_text = "".join(_iter_delta_content(chunks)).strip()
    web_results = _first_nested(chunks, "Result", "WebResults")
    search_context = _first_nested(chunks, "Result", "SearchContext")
    usage = _last_nested(chunks, "Result", "Usage")
    response_metadata = _first_nested(chunks, "ResponseMetadata")
    log_id = _last_nested(chunks, "Result", "LogId")
    result_count = _first_nested(chunks, "Result", "ResultCount")
    if not summary_text and not web_results:
        return last

    return {
        "ResponseMetadata": response_metadata or first.get("ResponseMetadata"),
        "Result": {
            "ResultCount": result_count,
            "WebResults": web_results,
            "SummaryText": summary_text,
            "SearchContext": search_context,
            "Usage": usage,
            "ChunkCount": len(chunks),
            "LogId": log_id,
        },
    }


def _iter_delta_content(chunks: list[dict[str, Any]]) -> Iterator[str]:
    for payload in chunks:
        result = payload.get("Result") if isinstance(payload, Mapping) else None
        choices = None
        if isinstance(result, Mapping):
            choices = result.get("Choices")
        if choices is None:
            choices = payload.get("Choices") if isinstance(payload, Mapping) else None
        if not isinstance(choices, list):
            continue
        for choice in choices:
            if not isinstance(choice, Mapping):
                continue
            delta = choice.get("Delta")
            if isinstance(delta, Mapping):
                content = delta.get("Content")
                if isinstance(content, str) and content:
                    yield content
            message = choice.get("Message")
            if isinstance(message, Mapping):
                content = message.get("Content")
                if isinstance(content, str) and content:
                    yield content


def _first_nested(chunks: list[dict[str, Any]], *keys: str) -> Any:
    for chunk in chunks:
        value = _get_nested(chunk, *keys)
        if value not in (None, "", [], {}):
            return value
    return None


def _last_nested(chunks: list[dict[str, Any]], *keys: str) -> Any:
    for chunk in reversed(chunks):
        value = _get_nested(chunk, *keys)
        if value not in (None, "", [], {}):
            return value
    return None


def _get_nested(value: Any, *keys: str) -> Any:
    current = value
    for key in keys:
        if not isinstance(current, Mapping):
            return None
        current = current.get(key)
    return current


def _parse_response_payload(response: requests.Response) -> dict[str, Any]:
    try:
        payload = response.json()
    except ValueError:
        text = response.content.decode("utf-8", errors="replace")
        payload = _parse_sse_data(text) or {"raw_text": text}
    return _repair_mojibake(payload)


def _parse_sse_data(text: str) -> dict[str, Any] | None:
    for line in text.splitlines():
        line = line.strip()
        parsed = _parse_sse_line(line)
        if parsed is not None:
            return parsed
    return None


def _parse_sse_line(line: str) -> dict[str, Any] | None:
    if not line or not line.startswith("data:"):
        return None
    body = line[5:].strip()
    if not body or body == "[DONE]":
        return None
    try:
        parsed = json.loads(body)
    except json.JSONDecodeError:
        return None
    if isinstance(parsed, dict):
        return parsed
    return None


def _repair_mojibake(value: Any) -> Any:
    if isinstance(value, str):
        mojibake_markers = ("\u00e3", "\u00e5", "\u00e6", "\u00e4", "\u00e9", "\u00ef\u00bc", "\u00e7")
        if any(marker in value for marker in mojibake_markers):
            try:
                repaired = value.encode("latin1").decode("utf-8")
                if sum("\u4e00" <= ch <= "\u9fff" for ch in repaired) > sum(
                    "\u4e00" <= ch <= "\u9fff" for ch in value
                ):
                    return repaired
            except UnicodeError:
                return value
        return value
    if isinstance(value, list):
        return [_repair_mojibake(item) for item in value]
    if isinstance(value, dict):
        return {key: _repair_mojibake(item) for key, item in value.items()}
    return value


def _signing_key(secret: str, short_date: str, region: str, service: str) -> bytes:
    k_date = _hmac(secret.encode("utf-8"), short_date)
    k_region = _hmac(k_date, region)
    k_service = _hmac(k_region, service)
    return _hmac(k_service, "request")


def _hmac(key: bytes, message: str) -> bytes:
    return hmac.new(key, message.encode("utf-8"), hashlib.sha256).digest()


def _canonical_query(query: Mapping[str, str]) -> str:
    return "&".join(f"{quote(str(key), safe='-_.~')}={quote(str(query[key]), safe='-_.~')}" for key in sorted(query))
