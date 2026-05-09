from __future__ import annotations

import argparse
import base64
import json
import mimetypes
import os
import time
import uuid
import winsound
from pathlib import Path
from typing import Any, Mapping

import requests


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_CONFIG = ROOT / "config" / "volc_traditional_voice_chat.local.json"
DEFAULT_SAMPLE = ROOT / "\u8bed\u97f3\u5305" / "\u94f6\u72fc" / "archive_silverwolf_11.wav"
DEFAULT_OUTPUT_DIR = ROOT / ".tmp" / "mimo_tts_voiceclone"
DEFAULT_TEXT = "\u8fd9\u662f MiMo \u8bed\u97f3\u514b\u9686\u6d4b\u8bd5\u3002\u522b\u6025\uff0c\u5148\u542c\u542c\u97f3\u8272\u50cf\u4e0d\u50cf\u3002"


def main() -> int:
    parser = argparse.ArgumentParser(description="Probe MiMo V2.5 TTS voice clone and save a wav file.")
    parser.add_argument("--config", default=str(DEFAULT_CONFIG))
    parser.add_argument("--sample", default=str(DEFAULT_SAMPLE), help="Reference wav/mp3 sample for voice cloning.")
    parser.add_argument("--text", default=DEFAULT_TEXT, help="Text to synthesize.")
    parser.add_argument("--style", default="", help="Optional style prompt for the user message.")
    parser.add_argument("--style-from-persona", action="store_true", help="Use LLMConfig.SystemMessages as the style prompt.")
    parser.add_argument("--output-dir", default=str(DEFAULT_OUTPUT_DIR))
    parser.add_argument("--base-url", default="", help="Override MiMo base URL or full /chat/completions URL.")
    parser.add_argument("--api-key", default="")
    parser.add_argument("--play", action="store_true", help="Play the generated wav after saving.")
    args = parser.parse_args()

    config = _load_json(Path(args.config))
    llm_config = _pick_llm_config(config)
    api_key = str(args.api_key or llm_config.get("APIKey") or llm_config.get("api_key") or os.environ.get("MIMO_API_KEY", "")).strip()
    if not api_key:
        raise SystemExit("Missing MiMo API key. Fill config LLMConfig.APIKey or set MIMO_API_KEY.")

    url = _completion_url(str(args.base_url or llm_config.get("Url") or llm_config.get("URL") or ""))
    sample_path = Path(args.sample)
    if not sample_path.is_absolute():
        sample_path = ROOT / sample_path
    if not sample_path.exists():
        raise SystemExit(f"Reference voice sample not found: {sample_path}")

    voice_data_url = _sample_data_url(sample_path)
    text = str(args.text or "").strip()
    if not text:
        raise SystemExit("TTS text is empty.")

    style_prompt = str(args.style or "").strip()
    if args.style_from_persona:
        style_prompt = _persona_prompt(llm_config) or style_prompt
    if not style_prompt:
        style_prompt = "\u4fdd\u6301\u53c2\u8003\u97f3\u9891\u7684\u97f3\u8272\uff0c\u8bf4\u8bdd\u7b80\u77ed\u3001\u61d2\u6563\u3001\u5e26\u4e00\u70b9\u8f7b\u4f7b\u3002"

    request_body = {
        "model": "mimo-v2.5-tts-voiceclone",
        "messages": [
            {"role": "user", "content": style_prompt},
            {"role": "assistant", "content": text},
        ],
        "audio": {
            "format": "wav",
            "voice": voice_data_url,
        },
    }

    output_dir = Path(args.output_dir)
    if not output_dir.is_absolute():
        output_dir = ROOT / output_dir
    output_dir.mkdir(parents=True, exist_ok=True)

    started = time.perf_counter()
    response = requests.post(
        url,
        headers={
            "Authorization": f"Bearer {api_key}",
            "api-key": api_key,
            "Content-Type": "application/json",
        },
        json=request_body,
        timeout=120,
    )
    elapsed_ms = int((time.perf_counter() - started) * 1000)

    try:
        payload = response.json()
    except ValueError as exc:
        body_path = output_dir / f"mimo_voiceclone_error_{uuid.uuid4().hex}.txt"
        body_path.write_text(response.text, encoding="utf-8")
        raise SystemExit(f"MiMo response is not JSON: status={response.status_code} saved={body_path}") from exc

    probe_id = uuid.uuid4().hex
    debug_path = output_dir / f"mimo_voiceclone_response_{probe_id}.redacted.json"
    debug_path.write_text(json.dumps(_redact_audio(payload), ensure_ascii=False, indent=2), encoding="utf-8")

    if response.status_code >= 400:
        raise SystemExit(f"MiMo TTS failed: status={response.status_code} elapsed_ms={elapsed_ms} debug={debug_path}")

    audio_b64 = _extract_audio_data(payload)
    if not audio_b64:
        raise SystemExit(f"MiMo TTS returned no audio data: elapsed_ms={elapsed_ms} debug={debug_path}")

    audio_bytes = base64.b64decode(audio_b64)
    output_path = output_dir / f"mimo_voiceclone_{probe_id}.wav"
    output_path.write_bytes(audio_bytes)
    meta_path = output_dir / f"mimo_voiceclone_{probe_id}.json"
    meta_path.write_text(
        json.dumps(
            {
                "ok": True,
                "elapsed_ms": elapsed_ms,
                "model": "mimo-v2.5-tts-voiceclone",
                "sample": str(sample_path),
                "text": text,
                "style": style_prompt,
                "output": str(output_path),
                "debug_response": str(debug_path),
            },
            ensure_ascii=False,
            indent=2,
        ),
        encoding="utf-8",
    )

    print(json.dumps({"ok": True, "elapsed_ms": elapsed_ms, "output": str(output_path), "meta": str(meta_path)}, ensure_ascii=False))
    if args.play:
        winsound.PlaySound(str(output_path), winsound.SND_FILENAME)
    return 0


def _load_json(path: Path) -> dict[str, Any]:
    data = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(data, dict):
        raise ValueError(f"JSON root must be object: {path}")
    return data


def _pick_llm_config(config: Mapping[str, Any]) -> Mapping[str, Any]:
    start = config.get("StartVoiceChat", config)
    if isinstance(start, Mapping):
        cfg = start.get("Config", {})
        if isinstance(cfg, Mapping):
            llm = cfg.get("LLMConfig", {})
            if isinstance(llm, Mapping):
                return llm
    return {}


def _persona_prompt(llm_config: Mapping[str, Any]) -> str:
    messages = llm_config.get("SystemMessages", llm_config.get("system_messages", []))
    if isinstance(messages, str):
        return messages.strip()
    if isinstance(messages, list):
        parts = [str(item).strip() for item in messages if str(item).strip()]
        return "\n".join(parts)
    return ""


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
    if mime_type not in {"audio/wav", "audio/mpeg", "audio/mp3"}:
        raise SystemExit(f"Unsupported reference sample type: {path.suffix}; use wav or mp3.")
    encoded = base64.b64encode(path.read_bytes()).decode("ascii")
    if len(encoded.encode("ascii")) > 10 * 1024 * 1024:
        raise SystemExit("Reference sample is too large after base64 encoding; MiMo limit is 10 MB.")
    return f"data:{mime_type};base64,{encoded}"


def _extract_audio_data(payload: Mapping[str, Any]) -> str:
    choices = payload.get("choices")
    if not isinstance(choices, list) or not choices:
        return ""
    choice = choices[0]
    if not isinstance(choice, Mapping):
        return ""
    message = choice.get("message")
    if not isinstance(message, Mapping):
        return ""
    audio = message.get("audio")
    if isinstance(audio, Mapping):
        data = audio.get("data")
        if isinstance(data, str):
            return data
    return ""


def _redact_audio(value: Any) -> Any:
    if isinstance(value, Mapping):
        result: dict[str, Any] = {}
        for key, item in value.items():
            if str(key).lower() in {"data", "voice"} and isinstance(item, str) and len(item) > 200:
                result[str(key)] = f"<redacted:{len(item)} chars>"
            else:
                result[str(key)] = _redact_audio(item)
        return result
    if isinstance(value, list):
        return [_redact_audio(item) for item in value]
    return value


if __name__ == "__main__":
    raise SystemExit(main())
