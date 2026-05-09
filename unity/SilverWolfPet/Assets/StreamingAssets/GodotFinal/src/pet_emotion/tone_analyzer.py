from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Iterable

from .emotion_types import EmotionCommand, EmotionType


@dataclass(frozen=True)
class ToneRule:
    name: str
    patterns: tuple[str, ...]
    command: EmotionCommand


class ToneAnalyzer:
    """Rule-based local text-to-emotion analyzer for AI subtitles."""

    def __init__(self, rules: Iterable[ToneRule] | None = None) -> None:
        self.rules = tuple(rules or DEFAULT_RULES)

    def analyze(
        self,
        text: str,
        *,
        voice_state: str = "speaking",
        is_final: bool = False,
        source: str = "subtitle:ai",
    ) -> EmotionCommand:
        normalized = _normalize_text(text)
        if not normalized:
            return _neutral(text, source)

        for rule in self.rules:
            if _matches_any(normalized, rule.patterns):
                priority_bonus = 5 if is_final else 0
                command = rule.command
                return EmotionCommand(
                    emotion=command.emotion,
                    valence=command.valence,
                    arousal=command.arousal,
                    dominance=command.dominance,
                    intensity=command.intensity,
                    face=command.face,
                    gesture=command.gesture,
                    posture_hint="stand",
                    duration_ms=command.duration_ms,
                    decay_ms=command.decay_ms,
                    priority=command.priority + priority_bonus,
                    source=source,
                    text_excerpt=text.strip()[:80],
                ).normalized()

        if voice_state == "thinking":
            return EmotionCommand(
                emotion=EmotionType.FOCUSED,
                valence=0.05,
                arousal=0.35,
                dominance=0.55,
                intensity=0.32,
                face="focused",
                gesture="none",
                duration_ms=900,
                decay_ms=900,
                priority=20,
                source=source,
                text_excerpt=text.strip()[:80],
            ).normalized()

        return _neutral(text, source)


def _cmd(
    emotion: EmotionType,
    *,
    valence: float,
    arousal: float,
    dominance: float,
    intensity: float,
    face: str,
    gesture: str = "none",
    duration_ms: int = 1400,
    decay_ms: int = 1400,
    priority: int = 40,
) -> EmotionCommand:
    return EmotionCommand(
        emotion=emotion,
        valence=valence,
        arousal=arousal,
        dominance=dominance,
        intensity=intensity,
        face=face,
        gesture=gesture,
        duration_ms=duration_ms,
        decay_ms=decay_ms,
        priority=priority,
    ).normalized()


DEFAULT_RULES = (
    ToneRule(
        "mocking_light",
        ("哼", "呵，", "呵。", "呵呵", "啧", "这都", "你这操作", "算了我来", "不算太迟", "终于", "就这", "还行吧", "卡关"),
        _cmd(
            EmotionType.MOCKING_LIGHT,
            valence=0.15,
            arousal=0.48,
            dominance=0.78,
            intensity=0.62,
            face="mocking",
            gesture="small_tease",
            priority=55,
            duration_ms=2500,
        ),
    ),
    ToneRule(
        "mocking_heavy",
        ("离谱", "又炸了", "又崩", "翻车", "这也能", "还真能", "漏洞百出"),
        _cmd(
            EmotionType.MOCKING_HEAVY,
            valence=-0.15,
            arousal=0.62,
            dominance=0.82,
            intensity=0.78,
            face="mocking",
            gesture="small_tease",
            priority=65,
            duration_ms=2800,
        ),
    ),
    ToneRule(
        "annoyed",
        ("别乱点", "停一下", "不行", "别乱来", "先停", "会炸", "会崩", "危险"),
        _cmd(
            EmotionType.ANNOYED,
            valence=-0.45,
            arousal=0.62,
            dominance=0.78,
            intensity=0.7,
            face="angry",
            gesture="shake_head",
            priority=60,
            duration_ms=2200,
        ),
    ),
    ToneRule(
        "impatient",
        ("快点", "别磨蹭", "省点时间", "别卡这", "别绕了"),
        _cmd(
            EmotionType.IMPATIENT,
            valence=-0.25,
            arousal=0.58,
            dominance=0.72,
            intensity=0.52,
            face="angry",
            gesture="shake_head",
            priority=50,
            duration_ms=1800,
        ),
    ),
    ToneRule(
        "confused",
        ("嗯？", "嗯?", "奇怪", "不对", "等等", "怎么会", "没对上", "不太对"),
        _cmd(
            EmotionType.CONFUSED,
            valence=-0.08,
            arousal=0.45,
            dominance=0.42,
            intensity=0.55,
            face="confused",
            gesture="think",
            priority=50,
            duration_ms=2000,
        ),
    ),
    ToneRule(
        "victory",
        ("搞定", "完成", "拿下", "通关", "跑通", "解决了", "修好了"),
        _cmd(
            EmotionType.VICTORY,
            valence=0.72,
            arousal=0.7,
            dominance=0.78,
            intensity=0.78,
            face="happy",
            gesture="smug",
            priority=70,
            duration_ms=2500,
        ),
    ),
    ToneRule(
        "proud",
        ("不错", "还行", "稳了", "漂亮", "这波可以", "可以啊", "小问题"),
        _cmd(
            EmotionType.PROUD,
            valence=0.58,
            arousal=0.55,
            dominance=0.75,
            intensity=0.58,
            face="smug",
            gesture="smug",
            priority=55,
            duration_ms=2000,
        ),
    ),
    ToneRule(
        "focused",
        ("我看看", "排查", "分析", "先理一下", "定位", "复现", "验证", "看日志"),
        _cmd(
            EmotionType.FOCUSED,
            valence=0.05,
            arousal=0.38,
            dominance=0.68,
            intensity=0.52,
            face="focused",
            gesture="think",
            priority=50,
            duration_ms=2200,
        ),
    ),
    ToneRule(
        "thinking",
        ("想一下", "推一下", "算一下", "先理一下"),
        _cmd(
            EmotionType.THINKING,
            valence=0.02,
            arousal=0.34,
            dominance=0.58,
            intensity=0.43,
            face="thinking",
            gesture="think",
            priority=45,
            duration_ms=1800,
        ),
    ),
    ToneRule(
        "comforting",
        ("别急", "还没死局", "能救", "问题不大", "别慌", "我在", "可以救"),
        _cmd(
            EmotionType.COMFORTING,
            valence=0.35,
            arousal=0.28,
            dominance=0.62,
            intensity=0.48,
            face="neutral",
            gesture="nod",
            priority=45,
            duration_ms=2200,
        ),
    ),
    ToneRule(
        "sleepy",
        ("困", "懒得", "先挂着", "睡", "累了", "挂机", "歇会"),
        _cmd(
            EmotionType.SLEEPY,
            valence=-0.05,
            arousal=0.15,
            dominance=0.36,
            intensity=0.56,
            face="sleepy",
            gesture="none",
            priority=30,
            duration_ms=2600,
        ),
    ),
    ToneRule(
        "bored",
        ("无聊", "随便", "都行", "老套路", "没意思"),
        _cmd(
            EmotionType.BORED,
            valence=-0.2,
            arousal=0.18,
            dominance=0.45,
            intensity=0.38,
            face="sleepy",
            gesture="none",
            priority=30,
            duration_ms=2200,
        ),
    ),
    ToneRule(
        "surprised",
        ("哦？", "哦?", "诶？", "诶?", "啊？", "啊?", "居然", "什么情况", "真的假的"),
        _cmd(
            EmotionType.SURPRISED,
            valence=0.05,
            arousal=0.72,
            dominance=0.46,
            intensity=0.58,
            face="surprised",
            gesture="none",
            priority=50,
            duration_ms=1600,
        ),
    ),
    ToneRule(
        "serious",
        ("重点", "注意", "关键", "别忘"),
        _cmd(
            EmotionType.SERIOUS,
            valence=0.0,
            arousal=0.42,
            dominance=0.74,
            intensity=0.45,
            face="serious",
            gesture="arms_crossed",
            priority=45,
            duration_ms=1800,
        ),
    ),
    ToneRule(
        "amused",
        ("哈哈", "笑死", "有点意思", "挺有意思", "好玩"),
        _cmd(
            EmotionType.AMUSED,
            valence=0.62,
            arousal=0.52,
            dominance=0.65,
            intensity=0.52,
            face="happy",
            gesture="small_tease",
            priority=50,
            duration_ms=1800,
        ),
    ),
    ToneRule(
        "fail_tease",
        ("失败了", "没过", "没跑通", "又挂了", "报错了"),
        _cmd(
            EmotionType.FAIL_TEASE,
            valence=-0.18,
            arousal=0.46,
            dominance=0.66,
            intensity=0.52,
            face="mocking",
            gesture="small_tease",
            priority=50,
            duration_ms=2000,
        ),
    ),
)


def _neutral(text: str, source: str) -> EmotionCommand:
    return EmotionCommand(
        emotion=EmotionType.NEUTRAL,
        valence=0.0,
        arousal=0.2,
        dominance=0.5,
        intensity=0.12,
        face="neutral",
        gesture="none",
        duration_ms=0,
        decay_ms=800,
        priority=10,
        source=source,
        text_excerpt=text.strip()[:80],
    ).normalized()


def _normalize_text(text: str) -> str:
    return str(text or "").strip().lower()


def _matches_any(text: str, patterns: Iterable[str]) -> bool:
    return any(_pattern_matches(text, pattern) for pattern in patterns)


def _pattern_matches(text: str, pattern: str) -> bool:
    normalized = pattern.strip().lower()
    if not normalized:
        return False
    if re.fullmatch(r"[a-z0-9_+-]+", normalized):
        return re.search(rf"(?<![a-z0-9_+-]){re.escape(normalized)}(?![a-z0-9_+-])", text) is not None
    return normalized in text
