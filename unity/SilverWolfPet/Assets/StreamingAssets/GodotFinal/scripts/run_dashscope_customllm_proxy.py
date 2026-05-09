from __future__ import annotations

import argparse
import json
import logging
import os
import time
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any

import requests


DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 17863
DEFAULT_UPSTREAM_URL = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions"


def main() -> int:
    parser = argparse.ArgumentParser(description="OpenAI-compatible proxy for Volc CustomLLM -> DashScope Qwen.")
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    parser.add_argument("--api-key", default=os.environ.get("DASHSCOPE_API_KEY", ""))
    parser.add_argument("--upstream-url", default=DEFAULT_UPSTREAM_URL)
    parser.add_argument("--default-model", default="qwen3.6-flash")
    parser.add_argument("--timeout", type=float, default=90.0)
    args = parser.parse_args()

    if not args.api_key:
        raise SystemExit("DASHSCOPE_API_KEY or --api-key is required.")

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    server = _ProxyHTTPServer((args.host, args.port), _ProxyHandler)
    server.api_key = args.api_key
    server.upstream_url = args.upstream_url
    server.default_model = args.default_model
    server.timeout = args.timeout
    logging.info("DashScope CustomLLM proxy listening on http://%s:%s/chat/completions", args.host, args.port)
    try:
        server.serve_forever(poll_interval=0.5)
    except KeyboardInterrupt:
        return 0
    finally:
        server.server_close()


class _ProxyHTTPServer(ThreadingHTTPServer):
    api_key: str
    upstream_url: str
    default_model: str
    timeout: float


class _ProxyHandler(BaseHTTPRequestHandler):
    server: _ProxyHTTPServer

    def log_message(self, fmt: str, *args: Any) -> None:
        logging.getLogger("dashscope_proxy").info("%s - " + fmt, self.client_address[0], *args)

    def do_GET(self) -> None:
        if self.path.rstrip("/") in {"", "/health"}:
            self._send_json({"ok": True})
            return
        self.send_error(HTTPStatus.NOT_FOUND)

    def do_POST(self) -> None:
        if self.path not in {"/chat/completions", "/v1/chat/completions"}:
            self.send_error(HTTPStatus.NOT_FOUND)
            return
        try:
            payload = self._read_json()
            upstream_payload = _normalize_payload(payload, self.server.default_model)
            stream = bool(upstream_payload.get("stream"))
            started = time.perf_counter()
            response = requests.post(
                self.server.upstream_url,
                headers={
                    "Authorization": f"Bearer {self.server.api_key}",
                    "Content-Type": "application/json",
                },
                json=upstream_payload,
                stream=stream,
                timeout=self.server.timeout,
            )
            logging.info(
                "proxy upstream status=%s stream=%s model=%s headers_ms=%s",
                response.status_code,
                stream,
                upstream_payload.get("model"),
                int((time.perf_counter() - started) * 1000),
            )
            if stream:
                self._stream_response(response)
            else:
                self._relay_response(response)
        except Exception as exc:
            logging.exception("proxy request failed")
            self._send_json({"error": {"message": str(exc), "type": "proxy_error"}}, status=500)

    def _read_json(self) -> dict[str, Any]:
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length) if length > 0 else b"{}"
        data = json.loads(raw.decode("utf-8"))
        if not isinstance(data, dict):
            raise ValueError("request body must be a JSON object")
        return data

    def _relay_response(self, response: requests.Response) -> None:
        content_type = response.headers.get("Content-Type") or "application/json; charset=utf-8"
        body = response.content
        self.send_response(response.status_code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _stream_response(self, response: requests.Response) -> None:
        self.send_response(response.status_code)
        self.send_header("Content-Type", response.headers.get("Content-Type") or "text/event-stream; charset=utf-8")
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        for chunk in response.iter_content(chunk_size=4096):
            if chunk:
                self.wfile.write(chunk)
                self.wfile.flush()

    def _send_json(self, payload: Any, *, status: int = 200) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def _normalize_payload(payload: dict[str, Any], default_model: str) -> dict[str, Any]:
    normalized = dict(payload)
    if not normalized.get("model"):
        normalized["model"] = default_model

    max_tokens = normalized.pop("maxTokens", None)
    if max_tokens is not None and "max_tokens" not in normalized:
        normalized["max_tokens"] = max_tokens

    custom = normalized.pop("custom", None)
    if custom is None:
        custom = normalized.pop("Custom", None)
    custom_dict = _parse_custom(custom)
    for key in ("enable_thinking", "thinking_budget"):
        if key in custom_dict and key not in normalized:
            normalized[key] = custom_dict[key]

    # DashScope compatible mode accepts top-level stream, not a nested custom.stream.
    if "stream" in custom_dict:
        normalized["stream"] = bool(custom_dict["stream"])

    return normalized


def _parse_custom(value: Any) -> dict[str, Any]:
    if isinstance(value, dict):
        return value
    if isinstance(value, str) and value.strip():
        try:
            parsed = json.loads(value)
            return parsed if isinstance(parsed, dict) else {}
        except json.JSONDecodeError:
            return {}
    return {}


if __name__ == "__main__":
    raise SystemExit(main())
