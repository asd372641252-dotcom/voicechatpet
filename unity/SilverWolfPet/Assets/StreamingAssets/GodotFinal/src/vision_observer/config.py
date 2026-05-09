from __future__ import annotations

import os
from typing import Any, Mapping

from .types import NarrationRouterConfig, VisionObserverConfig


def load_vision_observer_config(config: Mapping[str, Any]) -> VisionObserverConfig:
    raw = _pick_mapping(config, "vision_observer", "VisionObserver")
    start = _pick_mapping(config, "StartVoiceChat", "start_voice_chat")
    voice_config = _pick_mapping(start, "Config", "config")
    llm_config = _pick_mapping(voice_config, "LLMConfig", "llm_config")
    api = _pick_mapping(raw, "api", "API", "ProviderConfig")
    provider = str(
        _pick(raw, "provider", "Provider")
        or _guess_provider_from_llm(llm_config)
        or "openai_compatible_vision"
    ).strip()
    env_prefix = _provider_env_prefix(provider)
    api_key = str(
        _pick(raw, "api_key", "APIKey", "ApiKey")
        or _pick(api, "api_key", "APIKey", "ApiKey")
        or _pick(llm_config, "APIKey", "ApiKey", "api_key")
        or os.getenv(f"{env_prefix}_API_KEY", "")
        or os.getenv("MIMO_API_KEY", "")
    ).strip()
    api_base_url = str(
        _pick(raw, "api_base_url", "ApiBaseUrl", "BaseURL", "Url", "URL", "Endpoint")
        or _pick(api, "api_base_url", "ApiBaseUrl", "BaseURL", "Url", "URL", "Endpoint")
        or _pick(llm_config, "Url", "URL", "Endpoint", "BaseURL", "BaseUrl")
        or os.getenv(f"{env_prefix}_API_BASE_URL", "")
        or os.getenv("MIMO_API_BASE_URL", "")
    ).strip()
    model = str(
        _pick(raw, "model", "Model", "ModelName")
        or _pick(api, "model", "Model", "ModelName")
        or _pick(llm_config, "ModelName", "Model", "model")
        or os.getenv(f"{env_prefix}_MODEL", "")
        or os.getenv("MIMO_MODEL_NAME", "")
        or "mimo-v2.5"
    ).strip()
    return VisionObserverConfig(
        enabled=_bool(_pick(raw, "enabled", "Enabled", default=False), False),
        provider=provider or "openai_compatible_vision",
        active_window_only=_bool(_pick(raw, "active_window_only", "ActiveWindowOnly", default=False), False),
        capture_interval_ms=_int(_pick(raw, "capture_interval_ms", "CaptureIntervalMs", default=1000), 1000),
        min_diff_ratio=_float(_pick(raw, "min_diff_ratio", "MinDiffRatio", default=0.035), 0.035),
        max_width=_int(_pick(raw, "max_width", "MaxWidth", default=960), 960),
        jpeg_quality=_int(_pick(raw, "jpeg_quality", "JpegQuality", "JPEGQuality", default=70), 70),
        api_timeout_ms=_int(_pick(raw, "api_timeout_ms", "ApiTimeoutMs", "TimeoutMs", default=1200), 1200),
        max_calls_per_minute=_int(_pick(raw, "max_calls_per_minute", "MaxCallsPerMinute", default=20), 20),
        event_ttl_ms=_int(_pick(raw, "event_ttl_ms", "EventTtlMs", default=8000), 8000),
        max_context_chars=_int(_pick(raw, "max_context_chars", "MaxContextChars", default=180), 180),
        speak_cooldown_ms=_int(_pick(raw, "speak_cooldown_ms", "SpeakCooldownMs", default=15000), 15000),
        user_pause_ms=_int(_pick(raw, "user_pause_ms", "UserPauseMs", default=2500), 2500),
        interrupt_priority=_float(_pick(raw, "interrupt_priority", "InterruptPriority", default=0.92), 0.92),
        default_speak_policy=str(_pick(raw, "default_speak_policy", "DefaultSpeakPolicy", default="speak_if_asked")),
        loop_tick_ms=_int(_pick(raw, "loop_tick_ms", "LoopTickMs", default=200), 200),
        companion_play_mode=_bool(_pick(raw, "companion_play_mode", "CompanionPlayMode", default=False), False),
        companion_force_interval_ms=_int(_pick(raw, "companion_force_interval_ms", "CompanionForceIntervalMs", default=5000), 5000),
        companion_report_cooldown_ms=_int(_pick(raw, "companion_report_cooldown_ms", "CompanionReportCooldownMs", default=6000), 6000),
        companion_min_priority=_float(_pick(raw, "companion_min_priority", "CompanionMinPriority", default=0.35), 0.35),
        companion_inject_mode=str(_pick(raw, "companion_inject_mode", "CompanionInjectMode", default="deferred_speech") or "deferred_speech"),
        api_base_url=api_base_url,
        api_key=api_key,
        model=model,
        thinking_type=str(
            _pick(raw, "thinking_type", "ThinkingType", default=_pick(llm_config, "ThinkingType", "thinking_type", default="disabled"))
            or "disabled"
        ).strip().lower(),
        mock_response=raw.get("mock_response") if isinstance(raw.get("mock_response"), Mapping) else None,
    )


def load_narration_router_config(config: Mapping[str, Any], vision_config: VisionObserverConfig | None = None) -> NarrationRouterConfig:
    raw = _pick_mapping(config, "narration_router", "NarrationRouter")
    return NarrationRouterConfig(
        enabled=_bool(_pick(raw, "enabled", "Enabled", default=True), True),
        allow_active_speech=_bool(_pick(raw, "allow_active_speech", "AllowActiveSpeech", default=True), True),
        allow_interrupt_user=_bool(_pick(raw, "allow_interrupt_user", "AllowInterruptUser", default=False), False),
        allow_interrupt_ai=_bool(_pick(raw, "allow_interrupt_ai", "AllowInterruptAi", "AllowInterruptAI", default=False), False),
        high_risk_can_interrupt=_bool(_pick(raw, "high_risk_can_interrupt", "HighRiskCanInterrupt", default=True), True),
        speak_cooldown_ms=_int(
            _pick(raw, "speak_cooldown_ms", "SpeakCooldownMs", default=vision_config.speak_cooldown_ms if vision_config else 15000),
            15000,
        ),
        user_pause_ms=_int(
            _pick(raw, "user_pause_ms", "UserPauseMs", default=vision_config.user_pause_ms if vision_config else 2500),
            2500,
        ),
        interrupt_priority=_float(
            _pick(raw, "interrupt_priority", "InterruptPriority", default=vision_config.interrupt_priority if vision_config else 0.92),
            0.92,
        ),
    )


def _provider_env_prefix(provider: str) -> str:
    normalized = str(provider or "").strip().upper().replace("-", "_")
    if normalized in {"MIMO", "MIMO_V25", "MIMO_V2_5", "OPENAI_COMPATIBLE_VISION"}:
        return "MIMO"
    if normalized == "DOUBAO_VISION":
        return "DOUBAO_VISION"
    if normalized == "QWEN_VL":
        return "QWEN_VL"
    return normalized or "VISION"


def _guess_provider_from_llm(llm_config: Mapping[str, Any]) -> str:
    text = " ".join(
        str(_pick(llm_config, key, default="") or "")
        for key in ("Url", "URL", "Endpoint", "BaseURL", "BaseUrl", "ModelName", "Model", "model")
    ).lower()
    if "xiaomimimo" in text or "mimo" in text:
        return "mimo_v2_5"
    if "dashscope" in text or "qwen" in text:
        return "qwen_vl"
    if "volces" in text or "doubao" in text or "ark" in text:
        return "doubao_vision"
    return "openai_compatible_vision"


def _pick_mapping(value: Mapping[str, Any], *keys: str) -> Mapping[str, Any]:
    for key in keys:
        item = value.get(key)
        if isinstance(item, Mapping):
            return item
    return {}


def _pick(value: Mapping[str, Any], *keys: str, default: Any = None) -> Any:
    for key in keys:
        if key in value:
            return value[key]
    return default


def _bool(value: Any, default: bool) -> bool:
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    text = str(value).strip().lower()
    if text in {"1", "true", "yes", "on"}:
        return True
    if text in {"0", "false", "no", "off"}:
        return False
    return default


def _int(value: Any, default: int) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def _float(value: Any, default: float) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return default
