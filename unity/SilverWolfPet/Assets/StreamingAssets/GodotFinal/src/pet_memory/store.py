from __future__ import annotations

import json
import sqlite3
import time
from dataclasses import dataclass
from pathlib import Path
from threading import RLock
from typing import Any, Iterable, Mapping


@dataclass(frozen=True)
class MessageRecord:
    id: int
    conversation_id: str
    role: str
    text: str
    source: str
    is_final: bool
    created_at: float
    metadata: Mapping[str, Any]


@dataclass(frozen=True)
class MemoryRecord:
    id: int
    memory_type: str
    text: str
    summary: str
    importance: float
    confidence: float
    tags: tuple[str, ...]
    created_at: float
    updated_at: float
    last_used_at: float
    use_count: int


@dataclass(frozen=True)
class SessionSummaryRecord:
    id: int
    conversation_id: str
    summary: str
    start_message_id: int
    end_message_id: int
    created_at: float
    updated_at: float


class SQLiteMemoryStore:
    def __init__(self, path: Path) -> None:
        self.path = path
        self._lock = RLock()
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._conn = sqlite3.connect(str(path), check_same_thread=False)
        self._conn.row_factory = sqlite3.Row
        self._initialize()

    def close(self) -> None:
        with self._lock:
            self._conn.close()

    def add_message(
        self,
        *,
        conversation_id: str,
        role: str,
        text: str,
        source: str,
        is_final: bool,
        metadata: Mapping[str, Any] | None = None,
        created_at: float | None = None,
    ) -> int:
        now = float(created_at or time.time())
        with self._lock:
            cursor = self._conn.execute(
                """
                INSERT INTO messages(conversation_id, role, text, source, is_final, metadata_json, created_at)
                VALUES (?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    conversation_id,
                    role,
                    text,
                    source,
                    1 if is_final else 0,
                    _json_dumps(metadata or {}),
                    now,
                ),
            )
            self._conn.commit()
            return int(cursor.lastrowid)

    def add_or_update_memory(
        self,
        *,
        memory_type: str,
        text: str,
        summary: str = "",
        importance: float = 0.5,
        confidence: float = 0.6,
        tags: Iterable[str] = (),
        source_message_ids: Iterable[int] = (),
        expires_at: float = 0.0,
    ) -> int:
        normalized = _normalize_key(text)
        now = time.time()
        tag_tuple = tuple(sorted({str(tag).strip() for tag in tags if str(tag).strip()}))
        source_ids = [int(value) for value in source_message_ids if int(value) > 0]
        with self._lock:
            existing = self._conn.execute(
                "SELECT id, importance, confidence, tags_json, source_message_ids_json FROM memories WHERE normalized_key=? AND status='active'",
                (normalized,),
            ).fetchone()
            if existing:
                merged_tags = sorted(set(_json_loads(existing["tags_json"], [])) | set(tag_tuple))
                merged_source_ids = sorted(set(_json_loads(existing["source_message_ids_json"], [])) | set(source_ids))
                self._conn.execute(
                    """
                    UPDATE memories
                    SET text=?, summary=?, importance=?, confidence=?, tags_json=?, source_message_ids_json=?,
                        expires_at=?, updated_at=?
                    WHERE id=?
                    """,
                    (
                        text,
                        summary,
                        max(float(existing["importance"] or 0.0), float(importance)),
                        max(float(existing["confidence"] or 0.0), float(confidence)),
                        _json_dumps(merged_tags),
                        _json_dumps(merged_source_ids),
                        float(expires_at or 0.0),
                        now,
                        int(existing["id"]),
                    ),
                )
                self._conn.commit()
                return int(existing["id"])

            cursor = self._conn.execute(
                """
                INSERT INTO memories(
                    memory_type, text, normalized_key, summary, importance, confidence, tags_json,
                    source_message_ids_json, created_at, updated_at, last_used_at, use_count, expires_at, status
                )
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, 0, ?, 'active')
                """,
                (
                    memory_type,
                    text,
                    normalized,
                    summary,
                    float(importance),
                    float(confidence),
                    _json_dumps(tag_tuple),
                    _json_dumps(source_ids),
                    now,
                    now,
                    float(expires_at or 0.0),
                ),
            )
            self._conn.commit()
            return int(cursor.lastrowid)

    def recent_messages(self, *, conversation_id: str, limit: int) -> list[MessageRecord]:
        with self._lock:
            rows = self._conn.execute(
                """
                SELECT * FROM messages
                WHERE conversation_id=?
                ORDER BY id DESC
                LIMIT ?
                """,
                (conversation_id, int(limit)),
            ).fetchall()
        return [_message_from_row(row) for row in reversed(rows)]

    def message_count(self, *, conversation_id: str) -> int:
        with self._lock:
            row = self._conn.execute(
                "SELECT COUNT(*) AS count FROM messages WHERE conversation_id=?",
                (conversation_id,),
            ).fetchone()
        return int(row["count"] if row else 0)

    def latest_summary(self, *, conversation_id: str) -> SessionSummaryRecord | None:
        with self._lock:
            row = self._conn.execute(
                """
                SELECT * FROM session_summaries
                WHERE conversation_id=?
                ORDER BY end_message_id DESC, id DESC
                LIMIT 1
                """,
                (conversation_id,),
            ).fetchone()
        return _summary_from_row(row) if row else None

    def add_session_summary(
        self,
        *,
        conversation_id: str,
        summary: str,
        start_message_id: int,
        end_message_id: int,
    ) -> int:
        now = time.time()
        with self._lock:
            existing = self._conn.execute(
                """
                SELECT id FROM session_summaries
                WHERE conversation_id=? AND start_message_id=? AND end_message_id=?
                """,
                (conversation_id, int(start_message_id), int(end_message_id)),
            ).fetchone()
            if existing:
                self._conn.execute(
                    "UPDATE session_summaries SET summary=?, updated_at=? WHERE id=?",
                    (summary, now, int(existing["id"])),
                )
                self._conn.commit()
                return int(existing["id"])
            cursor = self._conn.execute(
                """
                INSERT INTO session_summaries(conversation_id, summary, start_message_id, end_message_id, created_at, updated_at)
                VALUES (?, ?, ?, ?, ?, ?)
                """,
                (conversation_id, summary, int(start_message_id), int(end_message_id), now, now),
            )
            self._conn.commit()
            return int(cursor.lastrowid)

    def messages_for_compaction(self, *, conversation_id: str, keep_latest: int) -> list[MessageRecord]:
        latest = self.latest_summary(conversation_id=conversation_id)
        after_id = latest.end_message_id if latest else 0
        with self._lock:
            rows = self._conn.execute(
                """
                SELECT * FROM messages
                WHERE conversation_id=? AND id>? AND id NOT IN (
                    SELECT id FROM messages WHERE conversation_id=? ORDER BY id DESC LIMIT ?
                )
                ORDER BY id ASC
                """,
                (conversation_id, int(after_id), conversation_id, int(keep_latest)),
            ).fetchall()
        return [_message_from_row(row) for row in rows]

    def search_memories(self, *, query: str, limit: int) -> list[MemoryRecord]:
        query_terms = _terms(query)
        now = time.time()
        with self._lock:
            rows = self._conn.execute(
                """
                SELECT * FROM memories
                WHERE status='active' AND (expires_at=0 OR expires_at>?)
                ORDER BY importance DESC, updated_at DESC
                LIMIT 100
                """,
                (now,),
            ).fetchall()
        scored: list[tuple[float, MemoryRecord]] = []
        for row in rows:
            record = _memory_from_row(row)
            score = float(record.importance) * 0.55 + float(record.confidence) * 0.25
            if query_terms:
                text_terms = _terms(record.text + " " + record.summary + " " + " ".join(record.tags))
                overlap = len(query_terms & text_terms)
                score += min(0.45, overlap * 0.12)
                if overlap == 0 and record.importance < 0.8:
                    score *= 0.35
            recency_days = max(0.0, (now - record.updated_at) / 86400.0)
            score += max(0.0, 0.08 - recency_days * 0.002)
            scored.append((score, record))
        scored.sort(key=lambda item: item[0], reverse=True)
        return [record for _, record in scored[: max(0, int(limit))]]

    def mark_memories_used(self, ids: Iterable[int]) -> None:
        id_list = [int(value) for value in ids if int(value) > 0]
        if not id_list:
            return
        now = time.time()
        with self._lock:
            self._conn.executemany(
                "UPDATE memories SET last_used_at=?, use_count=use_count+1 WHERE id=?",
                [(now, value) for value in id_list],
            )
            self._conn.commit()

    def stats(self) -> dict[str, Any]:
        with self._lock:
            message_count = self._conn.execute("SELECT COUNT(*) AS count FROM messages").fetchone()["count"]
            memory_count = self._conn.execute("SELECT COUNT(*) AS count FROM memories WHERE status='active'").fetchone()["count"]
            summary_count = self._conn.execute("SELECT COUNT(*) AS count FROM session_summaries").fetchone()["count"]
        return {
            "path": str(self.path),
            "messages": int(message_count),
            "memories": int(memory_count),
            "summaries": int(summary_count),
        }

    def _initialize(self) -> None:
        with self._lock:
            self._conn.executescript(
                """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;

                CREATE TABLE IF NOT EXISTS messages (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    conversation_id TEXT NOT NULL,
                    role TEXT NOT NULL,
                    text TEXT NOT NULL,
                    source TEXT NOT NULL DEFAULT '',
                    is_final INTEGER NOT NULL DEFAULT 1,
                    metadata_json TEXT NOT NULL DEFAULT '{}',
                    created_at REAL NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_messages_conversation_id_id
                    ON messages(conversation_id, id);

                CREATE TABLE IF NOT EXISTS memories (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    memory_type TEXT NOT NULL,
                    text TEXT NOT NULL,
                    normalized_key TEXT NOT NULL,
                    summary TEXT NOT NULL DEFAULT '',
                    importance REAL NOT NULL DEFAULT 0.5,
                    confidence REAL NOT NULL DEFAULT 0.6,
                    tags_json TEXT NOT NULL DEFAULT '[]',
                    source_message_ids_json TEXT NOT NULL DEFAULT '[]',
                    created_at REAL NOT NULL,
                    updated_at REAL NOT NULL,
                    last_used_at REAL NOT NULL DEFAULT 0,
                    use_count INTEGER NOT NULL DEFAULT 0,
                    expires_at REAL NOT NULL DEFAULT 0,
                    status TEXT NOT NULL DEFAULT 'active'
                );

                CREATE UNIQUE INDEX IF NOT EXISTS idx_memories_normalized_key_active
                    ON memories(normalized_key)
                    WHERE status='active';

                CREATE TABLE IF NOT EXISTS session_summaries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    conversation_id TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    start_message_id INTEGER NOT NULL,
                    end_message_id INTEGER NOT NULL,
                    created_at REAL NOT NULL,
                    updated_at REAL NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_session_summaries_conversation_end
                    ON session_summaries(conversation_id, end_message_id);
                """
            )
            self._conn.commit()


def _message_from_row(row: sqlite3.Row) -> MessageRecord:
    return MessageRecord(
        id=int(row["id"]),
        conversation_id=str(row["conversation_id"]),
        role=str(row["role"]),
        text=str(row["text"]),
        source=str(row["source"]),
        is_final=bool(row["is_final"]),
        created_at=float(row["created_at"]),
        metadata=_json_loads(row["metadata_json"], {}),
    )


def _memory_from_row(row: sqlite3.Row) -> MemoryRecord:
    return MemoryRecord(
        id=int(row["id"]),
        memory_type=str(row["memory_type"]),
        text=str(row["text"]),
        summary=str(row["summary"] or ""),
        importance=float(row["importance"] or 0.0),
        confidence=float(row["confidence"] or 0.0),
        tags=tuple(str(tag) for tag in _json_loads(row["tags_json"], [])),
        created_at=float(row["created_at"] or 0.0),
        updated_at=float(row["updated_at"] or 0.0),
        last_used_at=float(row["last_used_at"] or 0.0),
        use_count=int(row["use_count"] or 0),
    )


def _summary_from_row(row: sqlite3.Row) -> SessionSummaryRecord:
    return SessionSummaryRecord(
        id=int(row["id"]),
        conversation_id=str(row["conversation_id"]),
        summary=str(row["summary"]),
        start_message_id=int(row["start_message_id"]),
        end_message_id=int(row["end_message_id"]),
        created_at=float(row["created_at"]),
        updated_at=float(row["updated_at"]),
    )


def _json_dumps(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def _json_loads(value: Any, default: Any) -> Any:
    try:
        return json.loads(value) if isinstance(value, str) and value else default
    except Exception:
        return default


def _normalize_key(value: str) -> str:
    return " ".join(str(value or "").strip().lower().split())[:500]


def _terms(value: str) -> set[str]:
    text = str(value or "").lower()
    terms = set()
    for token in text.replace("_", " ").replace("-", " ").split():
        token = token.strip(".,;:!?()[]{}<>\"'")
        if len(token) >= 2:
            terms.add(token)
    for index in range(0, max(0, len(text) - 1)):
        pair = text[index : index + 2].strip()
        if len(pair) == 2 and not pair.isspace():
            terms.add(pair)
    return terms
