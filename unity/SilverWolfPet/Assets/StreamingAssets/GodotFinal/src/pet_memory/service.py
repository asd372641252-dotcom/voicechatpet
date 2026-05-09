from __future__ import annotations

import copy
import logging
import os
import re
import time
from pathlib import Path
from typing import Any, Mapping

from .config import PetMemoryConfig
from .store import MemoryRecord, MessageRecord, SQLiteMemoryStore


class PetMemoryService:
    def __init__(
        self,
        *,
        root: Path,
        config: PetMemoryConfig,
        store: SQLiteMemoryStore | None = None,
        logger: logging.Logger | None = None,
    ) -> None:
        self.root = root
        self.config = config
        self.logger = logger or logging.getLogger(__name__)
        self.store = store if store is not None else SQLiteMemoryStore(_resolve_database_path(root, config.database_path))
        self._last_record_signature = ""
        self._last_record_at = 0.0

    @classmethod
    def open(
        cls,
        *,
        root: Path,
        config: PetMemoryConfig,
        logger: logging.Logger | None = None,
    ) -> "PetMemoryService | None":
        if not config.enabled:
            return None
        try:
            service = cls(root=root, config=config, logger=logger)
            if logger:
                logger.info("Pet memory enabled db=%s", service.store.path)
            return service
        except Exception:
            if logger:
                logger.exception("Pet memory initialization failed")
            return None

    @property
    def enabled(self) -> bool:
        return bool(self.config.enabled)

    def close(self) -> None:
        self.store.close()

    def status(self) -> dict[str, Any]:
        stats = self.store.stats()
        stats.update(
            {
                "enabled": self.enabled,
                "conversationId": self.config.conversation_id,
                "userId": self.config.user_id,
                "characterId": self.config.character_id,
                "startupSystemInjection": self.config.startup_system_injection,
                "externalTextInjection": self.config.external_text_injection,
            }
        )
        return stats

    def record_subtitle(
        self,
        *,
        role: str,
        text: str,
        is_final: bool,
        source: str,
        metadata: Mapping[str, Any] | None = None,
    ) -> int:
        if not self.config.ingest_subtitles or not is_final:
            return 0
        normalized_role = _normalize_role(role)
        clean = _clean_text(text)
        if len(clean) < self.config.min_message_chars:
            return 0
        if _looks_sensitive(clean):
            self.logger.warning("Pet memory skipped sensitive-looking subtitle source=%s role=%s", source, normalized_role)
            return 0
        signature = f"{normalized_role}|{clean}"
        now = time.time()
        if signature == self._last_record_signature and now - self._last_record_at < 3.0:
            return 0
        self._last_record_signature = signature
        self._last_record_at = now

        message_id = self.store.add_message(
            conversation_id=self.config.conversation_id,
            role=normalized_role,
            text=clean,
            source=source,
            is_final=True,
            metadata={
                "user_id": self.config.user_id,
                "character_id": self.config.character_id,
                **dict(metadata or {}),
            },
        )
        self._maybe_promote_memory(message_id=message_id, role=normalized_role, text=clean, source=source)
        self.compact_if_needed()
        return message_id

    def recall(self, query: str = "", *, limit: int | None = None) -> list[MemoryRecord]:
        records = self.store.search_memories(
            query=query,
            limit=self.config.recall_limit if limit is None else limit,
        )
        self.store.mark_memories_used(record.id for record in records)
        return records

    def render_recall_block(self, query: str = "", *, limit: int | None = None, max_chars: int | None = None) -> str:
        records = self.recall(query, limit=limit)
        latest_summary = self.store.latest_summary(conversation_id=self.config.conversation_id)
        lines: list[str] = []
        if latest_summary and latest_summary.summary:
            lines.append("Conversation summary: " + latest_summary.summary)
        for record in records:
            lines.append("- " + record.text)
        if not lines:
            return ""
        budget = self.config.max_prompt_chars if max_chars is None else max(80, int(max_chars))
        return _clip_text(
            "Relevant memory. Use it silently when helpful; do not recite it unless the user asks.\n"
            + "\n".join(lines),
            budget,
        )

    def inject_startup_system_messages(self, config: Mapping[str, Any]) -> int:
        if not self.config.startup_system_injection:
            return 0
        block = self.render_recall_block(
            "",
            limit=self.config.startup_recall_limit,
            max_chars=self.config.max_prompt_chars,
        )
        if not block:
            return 0
        llm = _llm_config(config)
        if llm is None:
            return 0
        messages = llm.setdefault("SystemMessages", [])
        if isinstance(messages, str):
            messages = [messages]
            llm["SystemMessages"] = messages
        if not isinstance(messages, list):
            return 0
        marker = "[PetMemory]"
        messages[:] = [message for message in messages if marker not in str(message)]
        messages.append(marker + "\n" + block)
        return 1

    def build_external_text(self, text: str) -> tuple[str, dict[str, Any]]:
        if not self.config.external_text_injection:
            return text, {"memoryInjected": False}
        block = self.render_recall_block(text, limit=self.config.recall_limit, max_chars=self.config.max_prompt_chars)
        if not block:
            return text, {"memoryInjected": False}
        return block + "\n\nCurrent message:\n" + text, {"memoryInjected": True}

    def compact_if_needed(self) -> int:
        count = self.store.message_count(conversation_id=self.config.conversation_id)
        if count < self.config.compact_after_messages:
            return 0
        candidates = self.store.messages_for_compaction(
            conversation_id=self.config.conversation_id,
            keep_latest=self.config.short_term_turns * 2,
        )
        if len(candidates) < max(4, self.config.short_term_turns):
            return 0
        summary = self._local_compact_messages(candidates)
        if not summary:
            return 0
        return self.store.add_session_summary(
            conversation_id=self.config.conversation_id,
            summary=summary,
            start_message_id=candidates[0].id,
            end_message_id=candidates[-1].id,
        )

    def _maybe_promote_memory(self, *, message_id: int, role: str, text: str, source: str) -> None:
        candidate = _extract_memory_candidate(text, role=role)
        if candidate is None:
            return
        if candidate["importance"] < self.config.importance_threshold:
            return
        self.store.add_or_update_memory(
            memory_type=candidate["memory_type"],
            text=candidate["text"],
            summary=candidate.get("summary", ""),
            importance=float(candidate["importance"]),
            confidence=float(candidate["confidence"]),
            tags=candidate.get("tags", ()),
            source_message_ids=[message_id],
        )
        self.logger.info("Pet memory promoted source=%s role=%s text=%s", source, role, candidate["text"])

    def _local_compact_messages(self, messages: list[MessageRecord]) -> str:
        parts: list[str] = []
        for message in messages:
            prefix = "User" if message.role == "user" else "Assistant"
            text = _clip_text(message.text, 120)
            if text:
                parts.append(f"{prefix}: {text}")
        if not parts:
            return ""
        return _clip_text(" | ".join(parts), self.config.summary_max_chars)


def copy_request_with_memory(config: Mapping[str, Any], service: PetMemoryService | None) -> dict[str, Any]:
    copied = copy.deepcopy(config)
    if service is not None:
        service.inject_startup_system_messages(copied)
    return copied


def _resolve_database_path(root: Path, value: str) -> Path:
    raw = (value or "").strip()
    if not raw:
        raw = "%APPDATA%/voicechatpet/pet_memory.sqlite3"
    appdata = os.getenv("APPDATA") or str(Path.home() / "AppData" / "Roaming")
    expanded = Path(raw.replace("%APPDATA%", appdata))
    if not expanded.is_absolute():
        expanded = root / expanded
    return expanded


def _llm_config(config: Mapping[str, Any]) -> dict[str, Any] | None:
    start = config.get("StartVoiceChat", config.get("start_voice_chat", {}))
    if not isinstance(start, Mapping):
        return None
    voice_config = start.get("Config", start.get("config", {}))
    if not isinstance(voice_config, Mapping):
        return None
    llm = voice_config.get("LLMConfig", voice_config.get("llm_config", {}))
    return llm if isinstance(llm, dict) else None


def _extract_memory_candidate(text: str, *, role: str) -> dict[str, Any] | None:
    clean = _clean_text(text)
    if not clean:
        return None
    lowered = clean.lower()
    tags: list[str] = []
    importance = 0.0
    memory_type = "fact"

    if role == "user":
        directive_tokens = (
            "\u8bb0\u4f4f",
            "\u4ee5\u540e",
            "\u522b\u518d",
            "\u4e0d\u8981",
            "\u4e0d\u51c6",
            "\u5fc5\u987b",
            "\u7edf\u4e00\u6210",
            "\u4f18\u5148",
        )
        if any(token in clean for token in directive_tokens):
            importance = 0.86
            memory_type = "directive"
            tags.append("directive")
        preference_tokens = (
            "\u6211\u559c\u6b22",
            "\u6211\u4e0d\u559c\u6b22",
            "\u6211\u60f3\u8981",
            "\u6211\u5e0c\u671b",
            "\u53eb\u6211",
            "\u6211\u7684",
        )
        if any(token in clean for token in preference_tokens):
            importance = max(importance, 0.78)
            memory_type = "preference"
            tags.append("preference")
        project_tokens = (
            "desktop",
            "unity",
            "godot",
            "llm",
            "api",
            "\u94f6\u72fc",
            "\u573a\u666f\u7248",
            "\u684c\u9762\u7248",
        )
        if any(token in lowered for token in project_tokens):
            importance = max(importance, 0.62)
            tags.append("project")
    else:
        project_state_tokens = (
            "\u5df2\u6539\u6210",
            "\u5df2\u63a5\u5165",
            "\u9a8c\u8bc1\u901a\u8fc7",
            "\u7f16\u8bd1\u901a\u8fc7",
        )
        if any(token in clean for token in project_state_tokens):
            importance = 0.5
            memory_type = "project_state"
            tags.append("project")

    if importance <= 0.0:
        return None
    return {
        "memory_type": memory_type,
        "text": _clip_text(clean, 260),
        "summary": "",
        "importance": importance,
        "confidence": 0.68 if role == "user" else 0.55,
        "tags": tuple(tags),
    }


def _normalize_role(role: str) -> str:
    lowered = str(role or "").strip().lower()
    if lowered in {"assistant", "ai", "bot", "silver_wolf"}:
        return "assistant"
    if lowered in {"system", "system_observation"}:
        return "system"
    return "user"


def _clean_text(text: str) -> str:
    return re.sub(r"\s+", " ", str(text or "")).strip()


def _looks_sensitive(text: str) -> bool:
    lowered = text.lower()
    if any(token in lowered for token in ("api_key", "apikey", "secret", "access token", "access_token", "bearer ")):
        return True
    if re.search(r"\b(sk-[a-z0-9_-]{16,}|aklt[a-z0-9+/=_-]{16,}|ep-\d{14}-[a-z0-9_-]+)\b", lowered):
        return True
    if re.search(r"\b[A-Za-z0-9+/=_-]{32,}\b", text):
        return True
    return False


def _clip_text(text: str, max_chars: int) -> str:
    value = str(text or "").strip()
    if len(value) <= max_chars:
        return value
    return value[: max(0, max_chars - 1)].rstrip() + "\u2026"
