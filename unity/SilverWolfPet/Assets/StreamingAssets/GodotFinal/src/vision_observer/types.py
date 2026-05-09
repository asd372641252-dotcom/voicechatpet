from __future__ import annotations

import time
import uuid
from dataclasses import dataclass, field
from typing import Any, Mapping


def now_ms() -> int:
    return int(time.time() * 1000)


def new_id(prefix: str) -> str:
    return f"{prefix}-{now_ms()}-{uuid.uuid4().hex[:8]}"


@dataclass(frozen=True)
class VisionObserverConfig:
    enabled: bool = False
    provider: str = "mimo_v2_5"
    active_window_only: bool = True
    capture_interval_ms: int = 1000
    min_diff_ratio: float = 0.035
    max_width: int = 960
    jpeg_quality: int = 70
    api_timeout_ms: int = 1200
    max_calls_per_minute: int = 20
    event_ttl_ms: int = 8000
    max_context_chars: int = 180
    speak_cooldown_ms: int = 15000
    user_pause_ms: int = 2500
    interrupt_priority: float = 0.92
    default_speak_policy: str = "speak_if_asked"
    loop_tick_ms: int = 200
    companion_play_mode: bool = False
    companion_force_interval_ms: int = 5000
    companion_report_cooldown_ms: int = 6000
    companion_min_priority: float = 0.35
    companion_inject_mode: str = "deferred_speech"
    api_base_url: str = ""
    api_key: str = ""
    model: str = "mimo-v2.5"
    thinking_type: str = "disabled"
    mock_response: Mapping[str, Any] | None = None


@dataclass(frozen=True)
class NarrationRouterConfig:
    enabled: bool = True
    allow_active_speech: bool = True
    allow_interrupt_user: bool = False
    allow_interrupt_ai: bool = False
    high_risk_can_interrupt: bool = True
    speak_cooldown_ms: int = 15000
    user_pause_ms: int = 2500
    interrupt_priority: float = 0.92


@dataclass(frozen=True)
class UiTarget:
    name: str
    reason: str = ""
    confidence: float = 0.0

    @classmethod
    def from_mapping(cls, value: Mapping[str, Any]) -> "UiTarget":
        return cls(
            name=str(value.get("name") or value.get("title") or "").strip(),
            reason=str(value.get("reason") or "").strip(),
            confidence=clamp_float(value.get("confidence"), 0.0, 1.0),
        )


@dataclass(frozen=True)
class ScreenFrame:
    frame_id: str
    captured_at_ms: int
    window_title: str
    process_name: str
    image_jpeg: bytes
    width: int
    height: int
    diff_ratio: float


@dataclass(frozen=True)
class FrameCaptureResult:
    frame: ScreenFrame | None
    skipped: bool
    reason: str
    trace_id: str = field(default_factory=lambda: new_id("vision"))
    latency_ms: float = 0.0
    diff_ratio: float = 0.0


@dataclass(frozen=True)
class VisionModelOutput:
    scene: str = "unknown"
    summary: str = ""
    important_text: tuple[str, ...] = ()
    ui_targets: tuple[UiTarget, ...] = ()
    user_state_guess: str = ""
    risk: str = "low"
    priority: float = 0.0
    speak_policy: str = "silent"
    suggested_speech: str = ""
    confidence: float = 0.0
    raw_provider: str = ""
    raw_latency_ms: float = 0.0
    is_degraded: bool = False
    degraded_reason: str = ""


@dataclass(frozen=True)
class VisionEvent:
    event_id: str
    frame_id: str
    source: str
    scene: str
    summary: str
    important_text: tuple[str, ...]
    ui_targets: tuple[UiTarget, ...]
    user_state_guess: str
    risk: str
    priority: float
    speak_policy: str
    suggested_speech: str
    confidence: float
    created_at_ms: int
    ttl_ms: int
    window_title: str
    process_name: str
    raw_provider: str
    raw_latency_ms: float
    is_degraded: bool = False

    @classmethod
    def from_model_output(
        cls,
        output: VisionModelOutput,
        frame: ScreenFrame,
        *,
        ttl_ms: int,
        created_at_ms: int | None = None,
    ) -> "VisionEvent":
        return cls(
            event_id=new_id("ve"),
            frame_id=frame.frame_id,
            source="vision_api",
            scene=normalize_scene(output.scene),
            summary=trim_text(output.summary, 120),
            important_text=tuple(trim_text(item, 60) for item in output.important_text[:5] if str(item).strip()),
            ui_targets=tuple(output.ui_targets[:5]),
            user_state_guess=trim_text(output.user_state_guess, 80),
            risk=normalize_risk(output.risk),
            priority=clamp_float(output.priority, 0.0, 1.0),
            speak_policy=normalize_speak_policy(output.speak_policy),
            suggested_speech=trim_text(output.suggested_speech, 60),
            confidence=clamp_float(output.confidence, 0.0, 1.0),
            created_at_ms=created_at_ms if created_at_ms is not None else now_ms(),
            ttl_ms=max(1, int(ttl_ms)),
            window_title=frame.window_title,
            process_name=frame.process_name,
            raw_provider=output.raw_provider,
            raw_latency_ms=output.raw_latency_ms,
            is_degraded=output.is_degraded,
        )

    def is_expired(self, at_ms: int | None = None) -> bool:
        current = at_ms if at_ms is not None else now_ms()
        return current > self.created_at_ms + self.ttl_ms


@dataclass(frozen=True)
class NarrationContext:
    s2s_state: str = "idle"
    user_recently_speaking: bool = False
    ai_speaking: bool = False
    last_user_speech_at_ms: int = 0
    last_active_speech_at_ms: int = 0
    current_task_state: str = ""
    screen_state: Mapping[str, Any] | None = None
    now_ms: int = field(default_factory=now_ms)

    def user_pause_ms(self) -> int:
        if not self.last_user_speech_at_ms:
            return 10**9
        return max(0, self.now_ms - self.last_user_speech_at_ms)


@dataclass(frozen=True)
class RouteDecision:
    action: str
    reason: str
    trace_id: str = field(default_factory=lambda: new_id("route"))
    mode: str = "context"


@dataclass(frozen=True)
class InjectionRecord:
    trace_id: str
    mode: str
    text: str
    event_id: str
    role: str = "system_observation"
    queued: bool = False
    raw_result: Mapping[str, Any] | None = None


def clamp_float(value: Any, minimum: float, maximum: float) -> float:
    try:
        number = float(value)
    except (TypeError, ValueError):
        number = minimum
    return max(minimum, min(maximum, number))


def trim_text(value: Any, limit: int) -> str:
    text = " ".join(str(value or "").split())
    if len(text) <= limit:
        return text
    return text[: max(0, limit - 1)].rstrip() + "…"


def normalize_scene(value: Any) -> str:
    scene = str(value or "unknown").strip().lower()
    if any(token in scene for token in ("game", "游戏")):
        return "game"
    if any(token in scene for token in ("document", "word", "doc", "文档")):
        return "document"
    if any(token in scene for token in ("browser", "网页", "浏览器")):
        return "browser"
    if any(token in scene for token in ("chat", "聊天", "消息")):
        return "chat"
    if any(token in scene for token in ("video", "视频")):
        return "video"
    if any(token in scene for token in ("desktop", "桌面")):
        return "desktop"
    if any(token in scene for token in ("code", "代码", "terminal", "终端")):
        return "code"
    return scene if scene in {"game", "document", "browser", "chat", "video", "desktop", "code", "unknown"} else "unknown"


def normalize_risk(value: Any) -> str:
    risk = str(value or "low").strip().lower()
    if any(token in risk for token in ("high", "高", "危险", "紧急")):
        return "high"
    if any(token in risk for token in ("medium", "中", "注意")):
        return "medium"
    if any(token in risk for token in ("low", "低")):
        return "low"
    return risk if risk in {"low", "medium", "high"} else "low"


def normalize_speak_policy(value: Any) -> str:
    policy = str(value or "silent").strip().lower()
    if any(token in policy for token in ("speak_now", "立即", "马上", "现在提醒")):
        return "speak_now"
    if any(token in policy for token in ("speak_if_user_pauses", "停顿", "暂停")):
        return "speak_if_user_pauses"
    if any(token in policy for token in ("speak_if_asked", "询问", "主动问")):
        return "speak_if_asked"
    if any(token in policy for token in ("silent", "沉默", "不说", "保持沉默")):
        return "silent"
    allowed = {"silent", "speak_if_asked", "speak_if_user_pauses", "speak_now"}
    return policy if policy in allowed else "silent"
