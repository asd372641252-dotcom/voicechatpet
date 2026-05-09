from __future__ import annotations

import re


_BRACKETED_RE = re.compile(r"[\(（【\[]\s*([^()\[\]（）【】]{0,80})\s*[\)）】\]]")
_STAGE_LINE_RE = re.compile(
    r"^\s*(动作|表情|情绪|姿势|舞台说明|旁白|状态|gesture|action|emotion|pose|stage)\s*[:：].*$",
    re.IGNORECASE | re.MULTILINE,
)
_ASTERISK_STAGE_RE = re.compile(r"\*{1,2}([^*\n]{1,80})\*{1,2}")
_LEADING_STAGE_CUE_RE = re.compile(
    r"^\s*(轻笑|坏笑|冷笑|微笑|苦笑|叹气|沉默|停顿|小声|低声|挑眉|眨眼|wink|laugh|smile|sigh)\s*[,，。.!！?？、:：-]*\s*",
    re.IGNORECASE,
)

_STAGE_KEYWORDS = (
    "笑",
    "挑眉",
    "眨眼",
    "叹气",
    "叹",
    "摊手",
    "叉腰",
    "抱臂",
    "托腮",
    "歪头",
    "点头",
    "摇头",
    "挥手",
    "坐下",
    "站起",
    "靠近",
    "盯",
    "看向",
    "吐槽",
    "沉默",
    "冷笑",
    "得意",
    "皱眉",
    "微笑",
    "坏笑",
    "苦笑",
    "撇嘴",
    "眯眼",
    "翻白眼",
    "小声",
    "低声",
    "轻声",
    "停顿",
    "无奈",
    "挑衅",
    "动作",
    "表情",
    "情绪",
    "姿势",
    "舞台",
    "旁白",
    "gesture",
    "action",
    "emotion",
    "pose",
    "stage",
    "smile",
    "laugh",
    "sigh",
    "wink",
    "shrug",
    "nod",
)


def sanitize_spoken_text(text: str) -> str:
    """Remove model stage directions that should not be spoken by TTS."""

    cleaned = str(text or "").strip()
    if not cleaned:
        return ""

    cleaned = _STAGE_LINE_RE.sub("", cleaned)
    cleaned = _ASTERISK_STAGE_RE.sub(lambda match: _strip_stage_match(match.group(1)), cleaned)
    cleaned = _BRACKETED_RE.sub(lambda match: _strip_stage_match(match.group(1)), cleaned)
    cleaned = _LEADING_STAGE_CUE_RE.sub("", cleaned)
    cleaned = re.sub(r"\s+", " ", cleaned)
    cleaned = re.sub(r"\s+([，。！？、,.!?；;：:])", r"\1", cleaned)
    cleaned = re.sub(r"^[，。！？、,.!?；;：:\s]+", "", cleaned)
    return cleaned.strip()


def _strip_stage_match(inner: str) -> str:
    content = str(inner or "").strip()
    if not content:
        return ""
    lowered = content.lower()
    if any(keyword in lowered or keyword in content for keyword in _STAGE_KEYWORDS):
        return ""
    return content
