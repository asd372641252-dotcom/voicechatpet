from __future__ import annotations

import base64
import json
import queue
import re
import threading
import time
from typing import Any, Callable, Mapping
from urllib.parse import urlparse

import requests

from .types import ScreenFrame, UiTarget, VisionModelOutput, VisionObserverConfig, clamp_float, normalize_risk, normalize_scene, normalize_speak_policy, trim_text


VISION_SYSTEM_PROMPT = """你是桌面陪玩助手的视觉观察模块。请分析截图，不要复述全部文字，不要输出菜单栏和无关 UI 噪声，只提取对用户当前任务最重要的信息。

必须输出 JSON：
{
  "scene": "game | document | browser | chat | video | desktop | code | unknown",
  "summary": "80字以内总结当前画面",
  "important_text": ["最多5条关键文字"],
  "ui_targets": [
    {
      "name": "按钮或区域名称",
      "reason": "为什么重要",
      "confidence": 0.0
    }
  ],
  "user_state_guess": "用户可能正在做什么，50字以内",
  "risk": "low | medium | high",
  "priority": 0.0,
  "speak_policy": "silent | speak_if_asked | speak_if_user_pauses | speak_now",
  "suggested_speech": "给角色口播用的一句话，30字以内，必须像陪玩助手，不要像客服"
}

规则：
- 不确定就降低 confidence。
- 普通窗口标题、菜单栏、按钮列表不是重点。
- 不要写“我看到截图里有……”这种废话。
- suggested_speech 必须短，有角色感，但不要过度表演。
- 如果当前画面对用户没帮助，speak_policy 设为 silent。"""


VISION_SYSTEM_PROMPT = """你是桌面陪玩助手的视觉观察模块。请分析截图，不要复述全部文字，不要输出菜单栏和无关 UI 噪声，只提取对用户当前任务最重要的信息。

必须只输出合法 JSON 对象，不要 Markdown，不要解释，不要输出推理过程。字段名和枚举值必须严格使用下面的英文 schema：
{
  "scene": "game | document | browser | chat | video | desktop | code | unknown",
  "summary": "80字以内总结当前画面",
  "important_text": ["最多5条关键文字"],
  "ui_targets": [
    {
      "name": "按钮或区域名称",
      "reason": "为什么重要",
      "confidence": 0.0
    }
  ],
  "user_state_guess": "用户可能正在做什么，50字以内",
  "risk": "low | medium | high",
  "priority": 0.0,
  "speak_policy": "silent | speak_if_asked | speak_if_user_pauses | speak_now",
  "suggested_speech": "给角色口播用的一句话，30字以内，必须像陪玩助手，不要像客服"
}

规则：
- 不确定就降低 confidence 和 priority。
- 普通窗口标题、菜单栏、按钮列表不是重点。
- 不要写“我看到截图里有……”这种废话。
- suggested_speech 必须短，有角色感，但不要过度表演。
- 如果当前画面对用户没帮助，speak_policy 设为 silent。
- 如果没有关键文字，important_text 输出空数组；如果没有重要区域，ui_targets 输出空数组。"""


Transport = Callable[[str, Mapping[str, str], Mapping[str, Any], float], Any]


class VisionApiClient:
    """Provider-neutral vision model wrapper.

    The initial real provider implementation uses the OpenAI-compatible chat
    shape. Doubao, Qwen-VL and MiMo V2.5 can be configured through endpoint,
    model and API key without changing the observer pipeline.
    """

    def __init__(
        self,
        config: VisionObserverConfig,
        *,
        transport: Transport | None = None,
        system_prompt: str = VISION_SYSTEM_PROMPT,
    ) -> None:
        self.config = config
        self.transport = transport or _requests_transport
        self.system_prompt = system_prompt

    def analyze(self, frame: ScreenFrame, screen_state: Mapping[str, Any] | None = None) -> VisionModelOutput:
        provider = _normalize_provider(self.config.provider)
        if provider == "mock":
            return self._mock_output()
        started = time.perf_counter()
        result = _call_with_timeout(
            lambda: self._call_provider(provider, frame, screen_state or {}),
            max(1, self.config.api_timeout_ms) / 1000.0,
        )
        latency_ms = round((time.perf_counter() - started) * 1000.0, 3)
        if result.get("timeout"):
            return self._degraded("timeout", latency_ms=latency_ms, provider=provider)
        if result.get("error"):
            return self._degraded(str(result["error"]), latency_ms=latency_ms, provider=provider)
        output = parse_vision_model_output(result.get("value"), provider=provider, latency_ms=latency_ms)
        return output

    def _call_provider(self, provider: str, frame: ScreenFrame, screen_state: Mapping[str, Any]) -> Any:
        if provider in {"openai_compatible_vision", "doubao_vision", "qwen_vl", "mimo_v2_5"}:
            return self._call_openai_compatible(frame, screen_state)
        return self._degraded(f"unsupported_provider:{provider}", latency_ms=0.0, provider=provider)

    def _call_openai_compatible(self, frame: ScreenFrame, screen_state: Mapping[str, Any]) -> Any:
        if not self.config.api_base_url or not self.config.api_key:
            raise RuntimeError("vision API endpoint or API key is missing")
        data_url = "data:image/jpeg;base64," + base64.b64encode(frame.image_jpeg).decode("ascii")
        state_text = json.dumps(_compact_screen_state(screen_state), ensure_ascii=False, separators=(",", ":"))
        body = {
            "model": self.config.model or "mimo-v2.5",
            "messages": [
                {"role": "system", "content": self.system_prompt},
                {
                    "role": "user",
                    "content": [
                        {
                            "type": "text",
                            "text": (
                                "最近 screen_state（可为空）："
                                + state_text
                                + "\n请只返回符合 schema 的 JSON。"
                            ),
                        },
                        {"type": "image_url", "image_url": {"url": data_url, "detail": "low"}},
                    ],
                },
            ],
            "temperature": 0.1,
            "top_p": 0.3,
            "max_tokens": 512,
            "stream": False,
        }
        if _normalize_provider(self.config.provider) == "mimo_v2_5" or "mimo" in (self.config.model or "").lower():
            body["response_format"] = {"type": "json_object"}
            thinking_type = (self.config.thinking_type or "disabled").strip().lower()
            if thinking_type:
                body["thinking"] = {"type": thinking_type}
        headers = {
            "Authorization": f"Bearer {self.config.api_key}",
            "Content-Type": "application/json",
        }
        return self.transport(
            _chat_completions_url(self.config.api_base_url),
            headers,
            body,
            max(0.1, self.config.api_timeout_ms / 1000.0),
        )

    def _mock_output(self) -> VisionModelOutput:
        payload = self.config.mock_response or {
            "scene": "desktop",
            "summary": "画面发生变化，但 mock provider 没有具体视觉内容",
            "important_text": [],
            "ui_targets": [],
            "user_state_guess": "用户可能正在切换窗口",
            "risk": "low",
            "priority": 0.2,
            "speak_policy": "silent",
            "suggested_speech": "",
        }
        return parse_vision_model_output(payload, provider="mock", latency_ms=0.0)

    def _degraded(self, reason: str, *, latency_ms: float, provider: str) -> VisionModelOutput:
        return VisionModelOutput(
            scene="unknown",
            summary="视觉观察暂时不可用",
            important_text=(),
            ui_targets=(),
            user_state_guess="等待下一次画面观察",
            risk="low",
            priority=0.0,
            speak_policy="silent",
            suggested_speech="",
            confidence=0.0,
            raw_provider=provider,
            raw_latency_ms=latency_ms,
            is_degraded=True,
            degraded_reason=reason,
        )


def parse_vision_model_output(value: Any, *, provider: str, latency_ms: float) -> VisionModelOutput:
    payload = _extract_json_payload(value)
    if not isinstance(payload, Mapping):
        return VisionModelOutput(
            summary="视觉模型没有返回可用结构化结果",
            raw_provider=provider,
            raw_latency_ms=latency_ms,
            is_degraded=True,
            degraded_reason="invalid_json",
        )
    targets: list[UiTarget] = []
    for item in _list_payload(payload.get("ui_targets")):
        if isinstance(item, Mapping):
            target = UiTarget.from_mapping(item)
            if target.name:
                targets.append(target)
    important: list[str] = []
    for item in _list_payload(payload.get("important_text")):
        text = trim_text(item, 60)
        if text:
            important.append(text)
    confidence = payload.get("confidence")
    if confidence is None:
        target_confidence = [target.confidence for target in targets if target.confidence > 0]
        confidence = sum(target_confidence) / len(target_confidence) if target_confidence else payload.get("priority", 0.0)
    return VisionModelOutput(
        scene=normalize_scene(payload.get("scene")),
        summary=trim_text(payload.get("summary"), 120),
        important_text=tuple(important[:5]),
        ui_targets=tuple(targets[:5]),
        user_state_guess=trim_text(payload.get("user_state_guess"), 80),
        risk=normalize_risk(payload.get("risk")),
        priority=_priority_float(payload.get("priority")),
        speak_policy=normalize_speak_policy(payload.get("speak_policy")),
        suggested_speech=trim_text(payload.get("suggested_speech"), 60),
        confidence=clamp_float(confidence, 0.0, 1.0),
        raw_provider=provider,
        raw_latency_ms=latency_ms,
        is_degraded=False,
    )


def _list_payload(value: Any) -> list[Any]:
    if isinstance(value, list):
        return value
    if isinstance(value, tuple):
        return list(value)
    if isinstance(value, str):
        text = value.strip().lower()
        if not text or text in {"无", "none", "null", "n/a", "[]"}:
            return []
        return [value]
    return []


def _priority_float(value: Any) -> float:
    try:
        number = float(value)
        return clamp_float(number, 0.0, 1.0)
    except (TypeError, ValueError):
        pass
    text = str(value or "").strip().lower()
    if any(token in text for token in ("high", "高", "重要", "紧急")):
        return 0.85
    if any(token in text for token in ("medium", "中")):
        return 0.6
    if any(token in text for token in ("low", "低")):
        return 0.2
    return 0.0


def _call_with_timeout(fn: Callable[[], Any], timeout_sec: float) -> dict[str, Any]:
    output: queue.Queue[dict[str, Any]] = queue.Queue(maxsize=1)

    def runner() -> None:
        try:
            output.put({"value": fn()}, block=False)
        except Exception as exc:
            output.put({"error": f"{type(exc).__name__}:{exc}"}, block=False)

    thread = threading.Thread(target=runner, name="vision-api-call", daemon=True)
    thread.start()
    thread.join(timeout=timeout_sec)
    if thread.is_alive():
        return {"timeout": True}
    try:
        return output.get_nowait()
    except queue.Empty:
        return {"error": "empty_result"}


def _requests_transport(url: str, headers: Mapping[str, str], body: Mapping[str, Any], timeout_sec: float) -> Any:
    response = requests.post(url, headers=dict(headers), json=body, timeout=timeout_sec)
    try:
        payload = response.json()
    except ValueError as exc:
        raise RuntimeError(f"non_json_response:{response.status_code}") from exc
    if response.status_code >= 400:
        raise RuntimeError(f"http_{response.status_code}:{_short(payload)}")
    return payload


def _extract_json_payload(value: Any) -> Any:
    if isinstance(value, Mapping):
        if _looks_like_schema(value):
            return value
        answer = _extract_chat_answer(value)
        if answer:
            return _json_from_text(answer)
        return value
    if isinstance(value, str):
        return _json_from_text(value)
    return None


def _looks_like_schema(value: Mapping[str, Any]) -> bool:
    return any(key in value for key in ("scene", "summary", "important_text", "ui_targets", "speak_policy"))


def _extract_chat_answer(value: Mapping[str, Any]) -> str:
    choices = value.get("choices")
    if not isinstance(choices, list) or not choices:
        return ""
    choice = choices[0] if isinstance(choices[0], Mapping) else {}
    message = choice.get("message") if isinstance(choice, Mapping) else {}
    if isinstance(message, Mapping):
        content = message.get("content")
        if isinstance(content, str):
            return content.strip()
        if isinstance(content, list):
            return "".join(str(item.get("text", "")) for item in content if isinstance(item, Mapping)).strip()
    return ""


def _json_from_text(text: str) -> Any:
    cleaned = str(text or "").strip()
    if not cleaned:
        return None
    fence = re.search(r"```(?:json)?\s*(.*?)```", cleaned, re.IGNORECASE | re.DOTALL)
    if fence:
        cleaned = fence.group(1).strip()
    else:
        start = cleaned.find("{")
        end = cleaned.rfind("}")
        if start >= 0 and end > start:
            cleaned = cleaned[start : end + 1]
    try:
        return json.loads(cleaned)
    except json.JSONDecodeError:
        return None


def _normalize_provider(provider: str) -> str:
    normalized = str(provider or "").strip().lower().replace("-", "_")
    if normalized in {"mimo", "mimo_v25", "mimo_v2.5", "mimo_v2_5"}:
        return "mimo_v2_5"
    if normalized in {"openai", "openai_compatible", "openai_compatible_vision"}:
        return "openai_compatible_vision"
    return normalized or "openai_compatible_vision"


def _chat_completions_url(value: str) -> str:
    raw = str(value or "").strip().rstrip("/")
    if raw.endswith("/chat/completions"):
        return raw
    lowered = raw.lower()
    if lowered.endswith("/v1") or lowered.endswith("/compatible-mode/v1"):
        return raw + "/chat/completions"
    parsed = urlparse(raw)
    path = parsed.path.rstrip("/")
    if not path or path == "/" or path.lower() == "/beta" or re.fullmatch(r"/v[0-9][A-Za-z0-9_./-]*", path, re.IGNORECASE):
        return raw + "/chat/completions"
    return raw


def _compact_screen_state(value: Mapping[str, Any]) -> Mapping[str, Any]:
    allowed = {}
    for key in ("scene", "summary", "window_title", "process_name", "last_event_at_ms"):
        if key in value:
            allowed[key] = value[key]
    return allowed


def _short(value: Any, limit: int = 300) -> str:
    text = json.dumps(value, ensure_ascii=False, separators=(",", ":")) if not isinstance(value, str) else value
    return text if len(text) <= limit else text[:limit] + "..."
