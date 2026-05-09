from __future__ import annotations

import re
from dataclasses import dataclass, field

from .types import VisionEvent, now_ms, trim_text


EMPTY_SUMMARY_PATTERNS = (
    "看不清",
    "无法判断",
    "没有明显",
    "不确定",
    "视觉观察暂时不可用",
    "没有返回可用",
)


@dataclass
class VisionEventCompressor:
    max_chars: int = 180
    default_chars: int = 120
    dedupe_window_ms: int = 15000
    _recent_signatures: dict[str, int] = field(default_factory=dict)

    def compress(self, event: VisionEvent, *, at_ms: int | None = None) -> str | None:
        current = at_ms if at_ms is not None else now_ms()
        if event.is_expired(current):
            return None
        if self._is_hollow(event):
            return None
        signature = self.signature(event)
        last_seen = self._recent_signatures.get(signature, 0)
        if last_seen and current - last_seen < self.dedupe_window_ms:
            return None
        self._recent_signatures[signature] = current
        self._recent_signatures = {
            key: value for key, value in self._recent_signatures.items() if current - value <= self.dedupe_window_ms
        }

        label = "屏幕事件" if event.priority >= 0.7 or event.risk == "high" else "屏幕观察"
        parts = [f"{label}："]
        main = self._main_observation(event)
        if main:
            parts.append(main)
        action = self._suggestion(event)
        if action:
            parts.append(action)
        policy = self._policy_text(event)
        if policy:
            parts.append(policy)
        text = "".join(parts)
        text = _clean_text(text)
        if not text:
            return None
        limit = min(max(40, int(self.max_chars)), 180)
        soft_limit = min(max(40, int(self.default_chars)), limit)
        if len(text) > soft_limit:
            text = trim_text(text, soft_limit)
        if len(text) > limit:
            text = trim_text(text, limit)
        return ensure_sentence_end(text)

    def signature(self, event: VisionEvent) -> str:
        text = f"{event.scene}|{event.summary}|{event.user_state_guess}|{event.suggested_speech}"
        text = re.sub(r"\s+", "", text.lower())
        return text[:120]

    def _is_hollow(self, event: VisionEvent) -> bool:
        if event.is_degraded:
            return True
        combined = f"{event.summary}{event.user_state_guess}{event.suggested_speech}".strip()
        if len(combined) < 6:
            return True
        return any(pattern in combined for pattern in EMPTY_SUMMARY_PATTERNS) and event.priority < 0.45

    def _main_observation(self, event: VisionEvent) -> str:
        scene = _scene_text(event.scene)
        summary = event.summary or event.user_state_guess
        if summary:
            return f"{scene}{summary}。"
        return scene

    def _suggestion(self, event: VisionEvent) -> str:
        target = ""
        if event.ui_targets:
            primary = event.ui_targets[0]
            if primary.name and primary.confidence >= 0.45:
                target = f"关键目标是{trim_text(primary.name, 24)}"
                if primary.reason:
                    target += f"，{trim_text(primary.reason, 34)}"
                target += "。"
        speech = event.suggested_speech.strip()
        if speech:
            speech = trim_text(speech, 36)
            return f"{target}建议短句：{speech}。"
        return target

    def _policy_text(self, event: VisionEvent) -> str:
        if event.priority < 0.45 or event.speak_policy == "silent":
            return "只更新上下文，不主动说。"
        if event.speak_policy == "speak_if_asked" or event.priority < 0.7:
            return "用户问到再用。"
        if event.speak_policy == "speak_if_user_pauses" or event.priority < 0.9:
            return "只在用户停顿时短提醒。"
        return "可短提醒，不要展开。"


def ensure_sentence_end(text: str) -> str:
    text = str(text or "").strip()
    if not text:
        return ""
    if text[-1] in "。！？!?":
        return text
    return text + "。"


def _scene_text(scene: str) -> str:
    mapping = {
        "game": "游戏中，",
        "document": "文档中，",
        "browser": "网页中，",
        "chat": "聊天窗口中，",
        "video": "视频画面中，",
        "desktop": "桌面上，",
        "code": "代码窗口中，",
        "unknown": "",
    }
    return mapping.get(scene, "")


def _clean_text(text: str) -> str:
    text = re.sub(r"\s+", "", str(text or ""))
    text = text.replace("。。", "。").replace("：。", "：")
    return text.strip()
