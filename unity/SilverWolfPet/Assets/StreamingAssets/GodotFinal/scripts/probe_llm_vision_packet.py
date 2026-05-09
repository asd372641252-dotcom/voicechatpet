from __future__ import annotations

import argparse
import base64
import json
import os
import time
from pathlib import Path
from typing import Any

import requests
from PIL import Image, ImageGrab


PROJECT_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_CONFIG = PROJECT_ROOT / "config" / "volc_traditional_voice_chat.local.json"
DEFAULT_OUTPUT_DIR = PROJECT_ROOT / "logs"
DEFAULT_ARK_CHAT_URL = "https://ark.cn-beijing.volces.com/api/v3/chat/completions"


def main() -> int:
    parser = argparse.ArgumentParser(description="Send one low-resolution image packet directly to the configured CustomLLM.")
    parser.add_argument("--config", type=Path, default=DEFAULT_CONFIG)
    parser.add_argument("--image", type=Path, default=None, help="Use an existing image instead of capturing the screen.")
    parser.add_argument("--prompt", default="请只根据图片回答：你看到了什么？用两句话以内说清楚，不要空泛。")
    parser.add_argument("--max-height", type=int, default=480)
    parser.add_argument("--timeout", type=float, default=60.0)
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT_DIR)
    parser.add_argument("--dry-run", action="store_true", help="Build the request and save the image, but do not call the LLM.")
    args = parser.parse_args()

    config = _load_json(args.config)
    llm = _pick_llm_config(config)
    mode = _pick_text(llm, "Mode", "mode")
    url = _pick_text(llm, "Url", "URL", "url")
    api_key = _pick_text(llm, "APIKey", "ApiKey", "api_key", "apiKey")
    model = _pick_text(llm, "ModelName", "model", "Model")
    if mode == "ArkV3":
        url = url or os.environ.get("VOLC_ARK_CHAT_URL", DEFAULT_ARK_CHAT_URL)
        api_key = api_key or os.environ.get("VOLC_ARK_API_KEY", os.environ.get("ARK_API_KEY", ""))
        model = model or _pick_text(llm, "EndPointId", "EndpointId", "endpoint_id")
    if args.dry_run and not api_key:
        api_key = "<VOLC_ARK_API_KEY>"
    if not url or not api_key or not model:
        raise SystemExit(
            "Vision packet probe needs URL, API key and model. "
            "For ArkV3, set VOLC_ARK_API_KEY or ARK_API_KEY; model uses LLMConfig.EndPointId."
        )

    args.output_dir.mkdir(parents=True, exist_ok=True)
    image_path = _prepare_probe_image(args.image, args.output_dir, args.max_height)
    data_url = _image_to_data_url(image_path)
    request_body = _build_request_body(llm, model, args.prompt, data_url)

    request_path = args.output_dir / "llm_vision_probe_request.redacted.json"
    request_path.write_text(json.dumps(_redact_request(request_body), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    print(f"image={image_path}")
    print(f"request={request_path}")
    print(f"url={url}")
    print(f"model={model}")
    print(f"mode={mode or 'CustomLLM'}")
    if args.dry_run:
        print("dry_run=true")
        return 0

    started = time.perf_counter()
    stream_enabled = bool(request_body.get("stream"))
    response = requests.post(
        url,
        headers={
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
        },
        json=request_body,
        timeout=args.timeout,
        stream=stream_enabled,
    )
    response.encoding = "utf-8"
    headers_elapsed_ms = int((time.perf_counter() - started) * 1000)
    response_path = args.output_dir / "llm_vision_probe_response.json"
    first_delta_ms = None
    if stream_enabled:
        chunks: list[Any] = []
        parts: list[str] = []
        response_path = args.output_dir / "llm_vision_probe_response.jsonl"
        with response_path.open("w", encoding="utf-8", newline="\n") as handle:
            for raw_line in response.iter_lines(decode_unicode=True):
                if not raw_line:
                    continue
                line = raw_line.strip()
                if line.startswith("data:"):
                    line = line[5:].strip()
                if not line or line == "[DONE]":
                    continue
                try:
                    chunk = json.loads(line)
                except ValueError:
                    handle.write(json.dumps({"raw": line}, ensure_ascii=False) + "\n")
                    continue
                handle.write(json.dumps(chunk, ensure_ascii=False) + "\n")
                chunks.append(chunk)
                delta_text = _extract_delta_answer(chunk)
                if delta_text:
                    if first_delta_ms is None:
                        first_delta_ms = int((time.perf_counter() - started) * 1000)
                    parts.append(delta_text)
        parsed = {"choices": [{"message": {"content": "".join(parts)}}], "stream_chunks": len(chunks)}
        response_text = json.dumps(parsed, ensure_ascii=False)
    else:
        response_text = response.text
        try:
            parsed = response.json()
            response_path.write_text(json.dumps(parsed, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        except ValueError:
            response_path.write_text(response_text, encoding="utf-8")
            parsed = None

    print(f"status_code={response.status_code}")
    total_elapsed_ms = int((time.perf_counter() - started) * 1000)
    if stream_enabled:
        print(f"headers_elapsed_ms={headers_elapsed_ms}")
        print(f"total_elapsed_ms={total_elapsed_ms}")
    else:
        print(f"elapsed_ms={total_elapsed_ms}")
    if first_delta_ms is not None:
        print(f"first_delta_ms={first_delta_ms}")
    print(f"response={response_path}")
    if not response.ok:
        print(_short_text(response_text))
        return 2

    answer = _extract_answer(parsed)
    print("answer=" + (answer or "<empty>"))
    return 0 if answer else 3


def _load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def _pick_llm_config(config: dict[str, Any]) -> dict[str, Any]:
    start_voice_chat = config.get("StartVoiceChat", config.get("StartVoiceChatRequest", config))
    if not isinstance(start_voice_chat, dict):
        return {}
    llm = start_voice_chat.get("Config", {}).get("LLMConfig", {})
    return llm if isinstance(llm, dict) else {}


def _pick_text(mapping: dict[str, Any], *keys: str) -> str:
    for key in keys:
        value = mapping.get(key)
        if value is not None:
            return str(value).strip()
    return ""


def _prepare_probe_image(source: Path | None, output_dir: Path, max_height: int) -> Path:
    if source is None:
        image = ImageGrab.grab(all_screens=True)
    else:
        image = getattr(Image, "open")(source)
    image = image.convert("RGB")
    if max_height > 0 and image.height > max_height:
        ratio = max_height / float(image.height)
        width = max(1, int(round(image.width * ratio)))
        image = image.resize((width, max_height), Image.Resampling.LANCZOS)
    output = output_dir / "llm_vision_probe_input.jpg"
    image.save(output, format="JPEG", quality=78, optimize=True)
    return output


def _image_to_data_url(path: Path) -> str:
    encoded = base64.b64encode(path.read_bytes()).decode("ascii")
    return f"data:image/jpeg;base64,{encoded}"


def _build_request_body(llm: dict[str, Any], model: str, prompt: str, data_url: str) -> dict[str, Any]:
    messages: list[dict[str, Any]] = []
    for message in llm.get("SystemMessages", []):
        text = str(message).strip()
        if text:
            messages.append({"role": "system", "content": text})
    messages.append(
        {
            "role": "user",
            "content": [
                {"type": "text", "text": prompt},
                {"type": "image_url", "image_url": {"url": data_url, "detail": "low"}},
            ],
        }
    )
    body = {
        "model": model,
        "messages": messages,
        "max_tokens": int(llm.get("MaxTokens", 512)),
        "temperature": float(llm.get("Temperature", 0.1)),
        "top_p": float(llm.get("TopP", 0.3)),
        "stream": _pick_bool(llm.get("Stream", llm.get("stream")), False),
    }
    enable_thinking = llm.get("EnableThinking", llm.get("enable_thinking"))
    if enable_thinking is not None:
        body["enable_thinking"] = bool(enable_thinking)
    thinking_budget = llm.get("ThinkingBudget", llm.get("thinking_budget"))
    if thinking_budget is not None:
        body["thinking_budget"] = int(thinking_budget)
    return body


def _pick_bool(value: Any, default: bool) -> bool:
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    normalized = str(value).strip().lower()
    if normalized in {"1", "true", "yes", "on"}:
        return True
    if normalized in {"0", "false", "no", "off"}:
        return False
    return default


def _redact_request(body: dict[str, Any]) -> dict[str, Any]:
    redacted = json.loads(json.dumps(body, ensure_ascii=False))
    for message in redacted.get("messages", []):
        content = message.get("content")
        if not isinstance(content, list):
            continue
        for part in content:
            if part.get("type") == "image_url":
                part["image_url"] = {"url": "<base64 image omitted>", "detail": part.get("image_url", {}).get("detail", "low")}
    return redacted


def _extract_answer(parsed: Any) -> str:
    if not isinstance(parsed, dict):
        return ""
    choices = parsed.get("choices")
    if not isinstance(choices, list) or not choices:
        return ""
    message = choices[0].get("message", {}) if isinstance(choices[0], dict) else {}
    content = message.get("content") if isinstance(message, dict) else ""
    if isinstance(content, str):
        return content.strip()
    if isinstance(content, list):
        return "".join(str(part.get("text", "")) for part in content if isinstance(part, dict)).strip()
    return ""


def _extract_delta_answer(parsed: Any) -> str:
    if not isinstance(parsed, dict):
        return ""
    choices = parsed.get("choices")
    if not isinstance(choices, list) or not choices:
        return ""
    choice = choices[0] if isinstance(choices[0], dict) else {}
    delta = choice.get("delta") if isinstance(choice, dict) else {}
    if isinstance(delta, dict):
        content = delta.get("content")
        if isinstance(content, str):
            return content
        if isinstance(content, list):
            return "".join(str(part.get("text", "")) for part in content if isinstance(part, dict))
    message = choice.get("message") if isinstance(choice, dict) else {}
    if isinstance(message, dict) and isinstance(message.get("content"), str):
        return message["content"]
    return ""


def _short_text(text: str, limit: int = 600) -> str:
    cleaned = " ".join(str(text or "").split())
    if len(cleaned) <= limit:
        return cleaned
    return cleaned[:limit] + "..."


if __name__ == "__main__":
    raise SystemExit(main())
