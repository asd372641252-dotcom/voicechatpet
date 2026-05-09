from __future__ import annotations

import json
import logging
from typing import Any


def log_structured(logger: logging.Logger, event_name: str, **fields: Any) -> None:
    record = {
        "event": event_name,
        "trace_id": fields.pop("trace_id", ""),
        "frame_id": fields.pop("frame_id", ""),
        "latency_ms": fields.pop("latency_ms", None),
        "route_action": fields.pop("route_action", ""),
        "priority": fields.pop("priority", None),
        "scene": fields.pop("scene", ""),
        "reason": fields.pop("reason", ""),
    }
    record.update(fields)
    logger.info("%s %s", event_name, json.dumps(record, ensure_ascii=False, separators=(",", ":")))
