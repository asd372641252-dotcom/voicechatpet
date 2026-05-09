from __future__ import annotations

import json
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Mapping


@dataclass(frozen=True)
class PetMemoryConfig:
    enabled: bool = True
    database_path: str = "%APPDATA%/voicechatpet/pet_memory.sqlite3"
    user_id: str = "default_user"
    character_id: str = "silver_wolf"
    conversation_id: str = "default"
    ingest_subtitles: bool = True
    startup_system_injection: bool = True
    external_text_injection: bool = False
    short_term_turns: int = 10
    recall_limit: int = 6
    startup_recall_limit: int = 8
    max_prompt_chars: int = 900
    summary_max_chars: int = 700
    compact_after_messages: int = 40
    min_message_chars: int = 2
    importance_threshold: float = 0.45

    @classmethod
    def from_mapping(cls, value: Mapping[str, Any] | None) -> "PetMemoryConfig":
        data = value if isinstance(value, Mapping) else {}
        return cls(
            enabled=_bool(data, "Enabled", "enabled", default=cls.enabled),
            database_path=_text(data, "DatabasePath", "database_path", default=cls.database_path),
            user_id=_text(data, "UserId", "user_id", default=cls.user_id),
            character_id=_text(data, "CharacterId", "character_id", default=cls.character_id),
            conversation_id=_text(data, "ConversationId", "conversation_id", default=cls.conversation_id),
            ingest_subtitles=_bool(data, "IngestSubtitles", "ingest_subtitles", default=cls.ingest_subtitles),
            startup_system_injection=_bool(
                data,
                "StartupSystemInjection",
                "startup_system_injection",
                default=cls.startup_system_injection,
            ),
            external_text_injection=_bool(
                data,
                "ExternalTextInjection",
                "external_text_injection",
                default=cls.external_text_injection,
            ),
            short_term_turns=_int(data, "ShortTermTurns", "short_term_turns", default=cls.short_term_turns),
            recall_limit=_int(data, "RecallLimit", "recall_limit", default=cls.recall_limit),
            startup_recall_limit=_int(data, "StartupRecallLimit", "startup_recall_limit", default=cls.startup_recall_limit),
            max_prompt_chars=_int(data, "MaxPromptChars", "max_prompt_chars", default=cls.max_prompt_chars),
            summary_max_chars=_int(data, "SummaryMaxChars", "summary_max_chars", default=cls.summary_max_chars),
            compact_after_messages=_int(
                data,
                "CompactAfterMessages",
                "compact_after_messages",
                default=cls.compact_after_messages,
            ),
            min_message_chars=_int(data, "MinMessageChars", "min_message_chars", default=cls.min_message_chars),
            importance_threshold=_float(
                data,
                "ImportanceThreshold",
                "importance_threshold",
                default=cls.importance_threshold,
            ),
        ).normalized()

    def normalized(self) -> "PetMemoryConfig":
        return PetMemoryConfig(
            enabled=bool(self.enabled),
            database_path=self.database_path.strip() or "%APPDATA%/voicechatpet/pet_memory.sqlite3",
            user_id=self.user_id.strip() or "default_user",
            character_id=self.character_id.strip() or "silver_wolf",
            conversation_id=self.conversation_id.strip() or "default",
            ingest_subtitles=bool(self.ingest_subtitles),
            startup_system_injection=bool(self.startup_system_injection),
            external_text_injection=bool(self.external_text_injection),
            short_term_turns=max(2, min(30, int(self.short_term_turns))),
            recall_limit=max(0, min(16, int(self.recall_limit))),
            startup_recall_limit=max(0, min(20, int(self.startup_recall_limit))),
            max_prompt_chars=max(160, min(4000, int(self.max_prompt_chars))),
            summary_max_chars=max(160, min(4000, int(self.summary_max_chars))),
            compact_after_messages=max(8, min(300, int(self.compact_after_messages))),
            min_message_chars=max(1, min(40, int(self.min_message_chars))),
            importance_threshold=max(0.0, min(1.0, float(self.importance_threshold))),
        )


def load_project_memory_config(root: Path, runtime_config: Mapping[str, Any] | None = None) -> PetMemoryConfig:
    merged: dict[str, Any] = {}
    local_path = root / "config" / "pet_memory.local.json"
    example_path = root / "config" / "pet_memory.example.json"
    for path in (example_path, local_path):
        if not path.exists():
            continue
        try:
            payload = json.loads(path.read_text(encoding="utf-8"))
        except Exception:
            continue
        memory_payload = payload.get("Memory", payload) if isinstance(payload, Mapping) else {}
        if isinstance(memory_payload, Mapping):
            merged.update(memory_payload)

    if isinstance(runtime_config, Mapping):
        runtime_memory = runtime_config.get("Memory", runtime_config.get("memory", {}))
        if isinstance(runtime_memory, Mapping):
            merged.update(runtime_memory)

    if os.getenv("PET_MEMORY_DB"):
        merged["DatabasePath"] = os.getenv("PET_MEMORY_DB")
    if os.getenv("PET_MEMORY_ENABLED"):
        merged["Enabled"] = os.getenv("PET_MEMORY_ENABLED", "").strip().lower() not in {"0", "false", "no", "off"}

    return PetMemoryConfig.from_mapping(merged)


def _text(data: Mapping[str, Any], *keys: str, default: str) -> str:
    for key in keys:
        value = data.get(key)
        if value is not None:
            return str(value)
    return default


def _bool(data: Mapping[str, Any], *keys: str, default: bool) -> bool:
    for key in keys:
        if key not in data:
            continue
        value = data.get(key)
        if isinstance(value, bool):
            return value
        if isinstance(value, (int, float)):
            return bool(value)
        text = str(value).strip().lower()
        if text in {"1", "true", "yes", "on"}:
            return True
        if text in {"0", "false", "no", "off"}:
            return False
    return default


def _int(data: Mapping[str, Any], *keys: str, default: int) -> int:
    for key in keys:
        if key not in data:
            continue
        try:
            return int(float(data.get(key)))
        except (TypeError, ValueError):
            continue
    return default


def _float(data: Mapping[str, Any], *keys: str, default: float) -> float:
    for key in keys:
        if key not in data:
            continue
        try:
            return float(data.get(key))
        except (TypeError, ValueError):
            continue
    return default
