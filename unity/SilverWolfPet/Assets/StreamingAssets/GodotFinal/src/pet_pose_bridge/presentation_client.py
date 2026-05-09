from __future__ import annotations

import json
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Mapping, Protocol

from .godot_pose_client import GodotPoseClient
from .pose_command import PoseCommand
from .unity_pose_client import UnityPoseClient


DEFAULT_PRESENTATION_ROUTES_PATH = Path("config") / "presentation_routes.json"


class PosePresentationClient(Protocol):
    backend: str
    host: str
    port: int

    def send_pose(
        self,
        command: PoseCommand | Mapping[str, Any],
        *,
        raise_on_error: bool = False,
    ) -> bool:
        ...


@dataclass(frozen=True)
class PresentationEndpoint:
    route_id: str
    backend: str
    host: str
    port: int
    timeout_sec: float


def create_presentation_client(
    *,
    root: Path,
    config: Mapping[str, Any] | None = None,
    route_id: str = "",
    backend: str = "",
    host: str = "",
    port: int = 0,
    timeout_sec: float = 0.0,
    legacy_godot_host: str = "",
    legacy_godot_port: int = 0,
    default_timeout_sec: float = 0.2,
    offline_cooldown_sec: float = 2.0,
) -> PosePresentationClient:
    endpoint = resolve_presentation_endpoint(
        root=root,
        config=config,
        route_id=route_id,
        backend=backend,
        host=host,
        port=port,
        timeout_sec=timeout_sec,
        legacy_godot_host=legacy_godot_host,
        legacy_godot_port=legacy_godot_port,
        default_timeout_sec=default_timeout_sec,
    )
    if endpoint.backend == "unity":
        return UnityPoseClient(
            endpoint.host,
            endpoint.port,
            timeout_sec=endpoint.timeout_sec,
            offline_cooldown_sec=offline_cooldown_sec,
        )
    if endpoint.backend == "godot":
        return GodotPoseClient(
            endpoint.host,
            endpoint.port,
            timeout_sec=endpoint.timeout_sec,
            offline_cooldown_sec=offline_cooldown_sec,
        )
    raise ValueError(f"Unsupported presentation backend: {endpoint.backend}")


def resolve_presentation_endpoint(
    *,
    root: Path,
    config: Mapping[str, Any] | None = None,
    route_id: str = "",
    backend: str = "",
    host: str = "",
    port: int = 0,
    timeout_sec: float = 0.0,
    legacy_godot_host: str = "",
    legacy_godot_port: int = 0,
    default_timeout_sec: float = 0.2,
) -> PresentationEndpoint:
    config = config or {}
    route_config = _load_route_config(root)
    presentation_config = _mapping(config.get("Presentation", config.get("presentation", {})))

    selected_route = _first_text(
        route_id,
        presentation_config.get("Route"),
        presentation_config.get("route"),
        os.getenv("PET_PRESENTATION_ROUTE", ""),
    )
    selected_backend = _normalize_backend(
        _first_text(
            backend,
            presentation_config.get("Backend"),
            presentation_config.get("backend"),
            os.getenv("PET_PRESENTATION_BACKEND", ""),
        )
    )

    route_data = _route_data(route_config, selected_route)
    explicit_legacy_godot = bool(legacy_godot_host or legacy_godot_port > 0) and not selected_backend and not selected_route
    if explicit_legacy_godot:
        route_data = {"backend": "godot"}

    if not route_data and not selected_backend:
        route_data = _default_route_data(route_config)

    endpoint_backend = _normalize_backend(
        selected_backend
        or str(route_data.get("backend") or route_data.get("Backend") or "")
        or ("godot" if explicit_legacy_godot else "")
        or "godot"
    )
    default_port = 17861 if endpoint_backend == "unity" else 17865
    endpoint_host = _first_text(
        host,
        presentation_config.get("Host"),
        presentation_config.get("host"),
        route_data.get("host"),
        route_data.get("Host"),
        legacy_godot_host if endpoint_backend == "godot" else "",
        "127.0.0.1",
    )
    endpoint_port = _first_positive_int(
        port,
        presentation_config.get("Port"),
        presentation_config.get("port"),
        route_data.get("port"),
        route_data.get("Port"),
        legacy_godot_port if endpoint_backend == "godot" else 0,
        default_port,
    )
    endpoint_timeout = _first_positive_float(
        timeout_sec,
        presentation_config.get("TimeoutSec"),
        presentation_config.get("timeout_sec"),
        route_data.get("timeout_sec"),
        route_data.get("TimeoutSec"),
        default_timeout_sec,
    )
    return PresentationEndpoint(
        route_id=str(route_data.get("id") or selected_route or endpoint_backend),
        backend=endpoint_backend,
        host=endpoint_host,
        port=endpoint_port,
        timeout_sec=endpoint_timeout,
    )


def _load_route_config(root: Path) -> Mapping[str, Any]:
    path = root / DEFAULT_PRESENTATION_ROUTES_PATH
    if not path.exists():
        return {}
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}
    return data if isinstance(data, Mapping) else {}


def _route_data(route_config: Mapping[str, Any], route_id: str) -> dict[str, Any]:
    if not route_id:
        return {}
    routes = _mapping(route_config.get("routes", {}))
    normalized = _normalize_backend(route_id)
    for candidate in (route_id, normalized):
        value = routes.get(candidate)
        if isinstance(value, Mapping):
            data = dict(value)
            data["id"] = candidate
            return data
    return {"backend": normalized, "id": normalized} if normalized in {"unity", "godot"} else {}


def _default_route_data(route_config: Mapping[str, Any]) -> dict[str, Any]:
    default_route = str(route_config.get("default_route") or route_config.get("defaultRoute") or "").strip()
    return _route_data(route_config, default_route)


def _mapping(value: Any) -> Mapping[str, Any]:
    return value if isinstance(value, Mapping) else {}


def _normalize_backend(value: Any) -> str:
    normalized = str(value or "").strip().lower().replace("-", "_")
    aliases = {
        "u3d": "unity",
        "umity": "unity",
        "unity3d": "unity",
        "petdesktop": "unity",
        "pet_desktop": "unity",
        "godot4": "godot",
    }
    return aliases.get(normalized, normalized)


def _first_text(*values: Any) -> str:
    for value in values:
        if value is not None and str(value).strip():
            return str(value).strip()
    return ""


def _first_positive_int(*values: Any) -> int:
    for value in values:
        try:
            number = int(value)
        except (TypeError, ValueError):
            continue
        if number > 0:
            return number
    return 0


def _first_positive_float(*values: Any) -> float:
    for value in values:
        try:
            number = float(value)
        except (TypeError, ValueError):
            continue
        if number > 0:
            return number
    return 0.0
