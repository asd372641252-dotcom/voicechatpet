from __future__ import annotations

import argparse
import base64
import json
import mimetypes
import os
import time
from pathlib import Path
from typing import Any, Mapping

import requests


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_CONFIG = ROOT / "config" / "volc_traditional_voice_chat.local.json"
DEFAULT_SAMPLE = ROOT / "\u8bed\u97f3\u5305" / "\u94f6\u72fc" / "archive_silverwolf_10.wav"
DEFAULT_OUTPUT_DIR = ROOT / ".tmp" / "mimo_tts_voiceclone"
DEFAULT_TEXT = "\u522b\u6025\uff0c\u8def\u7ebf\u6211\u770b\u5230\u4e86\u3002\u4f60\u5f80\u5de6\u8fb9\u90a3\u4e2a\u9ad8\u53f0\u8d70\uff0c\u5b9d\u7bb1\u5927\u6982\u5c31\u85cf\u5728\u90a3\u513f\u3002"


def main() -> int:
    parser = argparse.ArgumentParser(description="Compare MiMo TTS voiceclone non-stream vs stream-compatible latency.")
    parser.add_argument("--config", default=str(DEFAULT_CONFIG))
    parser.add_argument("--sample", default=str(DEFAULT_SAMPLE))
    parser.add_argument("--text", default=DEFAULT_TEXT)
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--api-key", default="")
    parser.add_argument("--base-url", default="")
    parser.add_argument("--output-dir", default=str(DEFAULT_OUTPUT_DIR))
    args = parser.parse_args()

    config = _load_json(Path(args.config))
    llm_config = _pick_llm_config(config)
    api_key = str(args.api_key or llm_config.get("APIKey") or llm_config.get("api_key") or os.environ.get("MIMO_API_KEY", "")).strip()
    if not api_key:
        raise SystemExit("Missing MiMo API key.")
    url = _completion_url(str(args.base_url or llm_config.get("Url") or llm_config.get("URL") or ""))

    sample_path = Path(args.sample)
    if not sample_path.is_absolute():
        sample_path = ROOT / sample_path
    voice = _sample_data_url(sample_path)
    output_dir = Path(args.output_dir)
    if not output_dir.is_absolute():
        output_dir = ROOT / output_dir
    output_dir.mkdir(parents=True, exist_ok=True)

    rows: list[dict[str, Any]] = []
    for index in range(max(1, args.runs)):
        rows.append(_run_non_stream(url, api_key, voice, args.text, index, output_dir))
        rows.append(_run_stream(url, api_key, voice, args.text, index, output_dir))

    summary = {
        "sample": str(sample_path),
        "text": args.text,
        "runs": rows,
        "summary": _summary(rows),
    }
    summary_path = output_dir / f"mimo_tts_latency_{int(time.time())}.json"
    summary_path.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"ok": True, "summary": summary["summary"], "report": str(summary_path)}, ensure_ascii=False))
    return 0


def _run_non_stream(url: str, api_key: str, voice: str, text: str, index: int, output_dir: Path) -> dict[str, Any]:
    body = _body(text, voice, fmt="wav", stream=False)
    started = time.perf_counter()
    response = requests.post(url, headers=_headers(api_key), json=body, timeout=120)
    elapsed_ms = int((time.perf_counter() - started) * 1000)
    payload = response.json()
    audio_data = _extract_audio_data(payload)
    audio_bytes = base64.b64decode(audio_data) if audio_data else b""
    if audio_bytes:
        (output_dir / f"mimo_nonstream_{index}.wav").write_bytes(audio_bytes)
    return {
        "mode": "non_stream",
        "index": index,
        "status": response.status_code,
        "elapsed_ms": elapsed_ms,
        "first_audio_ms": elapsed_ms if audio_bytes else None,
        "audio_bytes": len(audio_bytes),
    }


def _run_stream(url: str, api_key: str, voice: str, text: str, index: int, output_dir: Path) -> dict[str, Any]:
    body = _body(text, voice, fmt="pcm16", stream=True)
    started = time.perf_counter()
    first_audio_ms: int | None = None
    audio_parts: list[bytes] = []
    status = 0
    response = requests.post(url, headers=_headers(api_key), json=body, stream=True, timeout=120)
    status = response.status_code
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
        except json.JSONDecodeError:
            continue
        data = _extract_stream_audio_data(chunk)
        if not data:
            continue
        if first_audio_ms is None:
            first_audio_ms = int((time.perf_counter() - started) * 1000)
        audio_parts.append(base64.b64decode(data))
    elapsed_ms = int((time.perf_counter() - started) * 1000)
    audio_bytes = b"".join(audio_parts)
    if audio_bytes:
        (output_dir / f"mimo_stream_{index}.pcm16").write_bytes(audio_bytes)
    return {
        "mode": "stream",
        "index": index,
        "status": status,
        "elapsed_ms": elapsed_ms,
        "first_audio_ms": first_audio_ms,
        "audio_bytes": len(audio_bytes),
        "chunks": len(audio_parts),
    }


def _body(text: str, voice: str, *, fmt: str, stream: bool) -> dict[str, Any]:
    body = {
        "model": "mimo-v2.5-tts-voiceclone",
        "messages": [
            {"role": "user", "content": "\u4fdd\u6301\u53c2\u8003\u97f3\u9891\u7684\u97f3\u8272\uff0c\u8bf4\u8bdd\u7b80\u77ed\u3001\u61d2\u6563\u3001\u5e26\u4e00\u70b9\u8f7b\u4f7b\u3002"},
            {"role": "assistant", "content": text},
        ],
        "audio": {"format": fmt, "voice": voice},
    }
    if stream:
        body["stream"] = True
    return body


def _headers(api_key: str) -> dict[str, str]:
    return {
        "Authorization": f"Bearer {api_key}",
        "api-key": api_key,
        "Content-Type": "application/json",
    }


def _load_json(path: Path) -> dict[str, Any]:
    data = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(data, dict):
        raise ValueError(f"JSON root must be object: {path}")
    return data


def _pick_llm_config(config: Mapping[str, Any]) -> Mapping[str, Any]:
    start = config.get("StartVoiceChat", config)
    cfg = start.get("Config", {}) if isinstance(start, Mapping) else {}
    llm = cfg.get("LLMConfig", {}) if isinstance(cfg, Mapping) else {}
    return llm if isinstance(llm, Mapping) else {}


def _completion_url(value: str) -> str:
    raw = value.strip() or "https://token-plan-cn.xiaomimimo.com/v1/chat/completions"
    if raw.rstrip("/").endswith("/chat/completions"):
        return raw
    return raw.rstrip("/") + "/chat/completions"


def _sample_data_url(path: Path) -> str:
    mime_type = mimetypes.guess_type(str(path))[0] or ""
    if path.suffix.lower() == ".wav":
        mime_type = "audio/wav"
    elif path.suffix.lower() == ".mp3":
        mime_type = "audio/mpeg"
    encoded = base64.b64encode(path.read_bytes()).decode("ascii")
    return f"data:{mime_type};base64,{encoded}"


def _extract_audio_data(payload: Mapping[str, Any]) -> str:
    try:
        audio = payload["choices"][0]["message"]["audio"]
        return audio["data"] if isinstance(audio, Mapping) else ""
    except (KeyError, IndexError, TypeError):
        return ""


def _extract_stream_audio_data(payload: Mapping[str, Any]) -> str:
    try:
        delta = payload["choices"][0]["delta"]
        audio = delta.get("audio") if isinstance(delta, Mapping) else None
        return audio.get("data", "") if isinstance(audio, Mapping) else ""
    except (KeyError, IndexError, TypeError):
        return ""


def _summary(rows: list[Mapping[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for mode in ("non_stream", "stream"):
        group = [row for row in rows if row.get("mode") == mode and row.get("status") == 200]
        if not group:
            continue
        result[mode] = {
            "elapsed_ms": [row.get("elapsed_ms") for row in group],
            "first_audio_ms": [row.get("first_audio_ms") for row in group],
            "audio_bytes": [row.get("audio_bytes") for row in group],
            "chunks": [row.get("chunks") for row in group if "chunks" in row],
        }
    return result


if __name__ == "__main__":
    raise SystemExit(main())
