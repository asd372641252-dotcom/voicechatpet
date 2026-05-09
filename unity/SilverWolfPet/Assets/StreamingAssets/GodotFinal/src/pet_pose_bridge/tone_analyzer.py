from __future__ import annotations

import time
from dataclasses import dataclass
import re
from typing import Iterable, Mapping

from .pose_command import PoseCommand


@dataclass(frozen=True)
class ToneMatch:
    state: str = "speaking"
    emotion: str = "neutral"
    gesture: str = "none"
    posture: str = "stand"
    priority: int = 1
    duration_ms: int = 1200
    interruptible: bool = True
    rule_name: str = "default"


class ToneAnalyzer:
    """Local rule-based AI subtitle tone analyzer.

    Tone-to-pose decisions stay local. The analyzer only emits semantic fields
    and never requests large posture changes such as fly or lie.
    """

    def __init__(self, *, cooldown_sec: float = 2.5, default_duration_ms: int = 1200) -> None:
        self.cooldown_sec = max(0.0, cooldown_sec)
        self.default_duration_ms = max(0, default_duration_ms)
        self._last_trigger_at: dict[tuple[str, str], float] = {}

    def analyze(
        self,
        text: str,
        *,
        voice_state: str = "speaking",
        tool_state: Mapping[str, object] | None = None,
        is_final: bool = False,
    ) -> PoseCommand:
        match = self.match(text, voice_state=voice_state, tool_state=tool_state, is_final=is_final)
        emotion = match.emotion
        gesture = match.gesture
        if not self._can_trigger(emotion, gesture, match.priority):
            emotion = "neutral"
            gesture = "none"

        return PoseCommand.from_mapping(
            {
                "type": "pet_pose",
                "state": match.state,
                "emotion": emotion,
                "gesture": gesture,
                "posture": "stand",
                "bubble_text": text,
                "mouth": "audio_volume" if match.state == "speaking" else "none",
                "priority": match.priority,
                "duration_ms": match.duration_ms,
                "interruptible": match.interruptible,
            }
        )

    def match(
        self,
        text: str,
        *,
        voice_state: str = "speaking",
        tool_state: Mapping[str, object] | None = None,
        is_final: bool = False,
    ) -> ToneMatch:
        normalized = _normalize_text(text)
        priority = 2 if is_final else 1

        if _matches_tone(normalized, HAPPY_SMUG_PATTERNS):
            return ToneMatch("speaking", "happy", "smug", priority=priority, duration_ms=self.default_duration_ms, rule_name="happy_smug")
        if _matches_tone(normalized, MOCKING_TEASE_PATTERNS):
            return ToneMatch("speaking", "mocking", "small_tease", priority=priority, duration_ms=self.default_duration_ms, rule_name="mocking_tease")
        if _matches_tone(normalized, CONFUSED_THINK_PATTERNS):
            return ToneMatch("thinking", "confused", "think", priority=priority, duration_ms=self.default_duration_ms, rule_name="confused_think")
        if _matches_tone(normalized, ANGRY_STOP_PATTERNS):
            return ToneMatch("speaking", "angry", "shake_head", priority=priority, duration_ms=self.default_duration_ms, rule_name="angry_stop")
        if _matches_tone(normalized, WORK_THINK_PATTERNS):
            return ToneMatch("thinking", "confused", "think", priority=priority, duration_ms=self.default_duration_ms, rule_name="analysis_think")
        if _matches_tone(normalized, SURPRISED_PATTERNS):
            return ToneMatch("speaking", "surprised", "none", priority=priority, duration_ms=self.default_duration_ms, rule_name="surprised")
        if _matches_tone(normalized, SLEEPY_PATTERNS):
            return ToneMatch("speaking", "sleepy", "none", priority=priority, duration_ms=self.default_duration_ms, rule_name="sleepy")
        if _matches_tone(normalized, SERIOUS_PATTERNS):
            return ToneMatch("speaking", "neutral", "arms_crossed", priority=priority, duration_ms=self.default_duration_ms, rule_name="serious")
        if _matches_tone(normalized, AGREE_PATTERNS):
            return ToneMatch("speaking", "neutral", "nod", priority=priority, duration_ms=self.default_duration_ms, rule_name="agree_nod")
        if _matches_tone(normalized, GUIDE_PATTERNS):
            return ToneMatch("speaking", "neutral", "point", priority=priority, duration_ms=self.default_duration_ms, rule_name="guide_point")

        if tool_state and str(tool_state.get("status", "")).lower() in {"running", "pending"}:
            return ToneMatch("thinking", "neutral", "think", priority=priority, duration_ms=self.default_duration_ms, rule_name="tool_running")

        return ToneMatch(_safe_voice_state(voice_state), "neutral", "none", priority=priority, duration_ms=0)

    def _can_trigger(self, emotion: str, gesture: str, priority: int) -> bool:
        if emotion == "neutral" and gesture == "none":
            return True
        now = time.monotonic()
        key = (emotion, gesture)
        last = self._last_trigger_at.get(key, 0.0)
        if priority < 10 and now - last < self.cooldown_sec:
            return False
        self._last_trigger_at[key] = now
        return True


def analyze_tone(text: str, *, voice_state: str = "speaking") -> PoseCommand:
    return ToneAnalyzer().analyze(text, voice_state=voice_state)


HAPPY_SMUG_PATTERNS = (
    "哈哈",
    "不错",
    "搞定",
    "完成",
    "稳了",
    "拿下",
    "通关",
    "过了",
    "跑通",
    "修好了",
    "解决了",
    "没问题",
    "漂亮",
    "可以啊",
    "这波可以",
    "小问题",
    "easy",
    "ok",
)

MOCKING_TEASE_PATTERNS = (
    "终于",
    "这都",
    "你这操作",
    "算了我来",
    "就这",
    "还不算太迟",
    "不算太迟",
    "卡关",
    "翻车",
    "又卡",
    "又炸",
    "又崩",
    "离谱",
    "菜",
    "笨",
    "啧",
    "漏洞",
    "这个 bug",
    "这不是 bug",
)

CONFUSED_THINK_PATTERNS = (
    "等等",
    "嗯？",
    "嗯?",
    "奇怪",
    "怪了",
    "不对",
    "怎么会",
    "为什么",
    "哪里不对",
    "不太对",
    "有点怪",
    "看起来不",
    "不像是",
    "没对上",
)

ANGRY_STOP_PATTERNS = (
    "不行",
    "别乱来",
    "停一下",
    "先停",
    "别碰",
    "别删",
    "不要删",
    "危险",
    "不能这样",
    "会炸",
    "会崩",
    "搞坏",
)

WORK_THINK_PATTERNS = (
    "我看看",
    "分析",
    "排查",
    "先理一下",
    "检查",
    "确认一下",
    "看日志",
    "看配置",
    "看代码",
    "定位",
    "复现",
    "验证",
    "查一下",
    "扫一遍",
)

SURPRISED_PATTERNS = (
    "哦？",
    "哦?",
    "诶？",
    "诶?",
    "啊？",
    "啊?",
    "居然",
    "真的假的",
    "什么情况",
    "这么快",
    "突然",
)

SLEEPY_PATTERNS = (
    "困",
    "睡",
    "累",
    "慢慢来",
    "懒得",
    "歇会",
    "休息",
    "挂机",
)

SERIOUS_PATTERNS = (
    "重点",
    "注意",
    "记住",
    "关键",
    "别忘",
    "先别",
    "建议",
    "最好",
    "必须",
)

AGREE_PATTERNS = (
    "对",
    "没错",
    "确实",
    "可以",
    "行",
    "嗯",
    "好",
)

GUIDE_PATTERNS = (
    "这里",
    "这边",
    "看这个",
    "点这里",
    "打开",
    "选择",
    "切到",
)


def _matches_tone(text: str, patterns: Iterable[str]) -> bool:
    return any(_pattern_matches(text, pattern) for pattern in patterns)


def _pattern_matches(text: str, pattern: str) -> bool:
    normalized_pattern = pattern.strip().lower()
    if not normalized_pattern:
        return False
    if re.fullmatch(r"[a-z0-9_+-]+", normalized_pattern):
        return re.search(rf"(?<![a-z0-9_+-]){re.escape(normalized_pattern)}(?![a-z0-9_+-])", text) is not None
    return normalized_pattern in text


def _normalize_text(text: str) -> str:
    return str(text or "").strip().lower()


def _safe_voice_state(state: str) -> str:
    normalized = str(state or "speaking").strip().lower()
    if normalized in {"speaking", "thinking", "idle"}:
        return normalized
    return "speaking"
