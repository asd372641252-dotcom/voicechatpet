from __future__ import annotations

from typing import Any, Callable, Mapping

from .volc_pose_event_adapter import VolcPoseEventAdapter


class VolcSessionCallbackBridge:
    """Small callback target that can be registered with a real Volc RTC client.

    The real SDK language/binding may name callbacks differently. Existing
    backend code can call these methods from its actual callback handlers
    without touching the local PoseBridge or Godot server.
    """

    def __init__(
        self,
        adapter: VolcPoseEventAdapter,
        *,
        on_subtitle_event: Callable[[Mapping[str, Any]], bool | None] | None = None,
        on_ai_state_event: Callable[[Mapping[str, Any] | str], None] | None = None,
    ) -> None:
        self.adapter = adapter
        self._subtitle_callback = on_subtitle_event
        self._ai_state_callback = on_ai_state_event

    def on_ai_state(self, event: Mapping[str, Any] | str) -> None:
        self.adapter.on_volc_ai_state_event(event)
        if self._ai_state_callback is not None:
            self._ai_state_callback(event)

    def on_task_state(self, event: Mapping[str, Any] | str) -> None:
        self.adapter.on_volc_ai_state_event(event)

    def on_conversation_state(self, event: Mapping[str, Any] | str) -> None:
        self.adapter.on_volc_ai_state_event(event)

    def on_subtitle(self, event: Mapping[str, Any]) -> None:
        if self._subtitle_callback is not None and self._subtitle_callback(event) is False:
            return
        self.adapter.on_volc_subtitle_event(event)

    def on_subtitle_messages(self, messages: Any) -> None:
        """Accept Web SDK onSubtitleMessageReceived payloads.

        The Web SDK callback shape is SubtitleMessage[], where each item has
        userId/text/definite. We normalize this into the adapter's narrow
        subtitle event shape and mark bot-user text as AI bubble text.
        """
        for item in _iter_mapping_items(messages):
            user_id = str(item.get("userId", item.get("uid", item.get("user_id", ""))))
            text = item.get("text", item.get("content", item.get("subtitle", "")))
            if not text:
                continue
            speaker = "ai" if user_id and user_id in self.adapter.bot_uids else "user"
            event = {
                "trace_id": item.get("trace_id", item.get("traceId", "")),
                "speaker": speaker,
                "uid": user_id,
                "text": str(text),
                "is_final": bool(item.get("definite", item.get("is_final", item.get("final", False)))),
                "raw": item,
            }
            if self._subtitle_callback is not None and self._subtitle_callback(event) is False:
                continue
            self.adapter.on_volc_subtitle_event(event)

    def on_function_call(self, event: Mapping[str, Any]):
        return self.adapter.on_volc_function_call(event)

    def on_tool_call(self, event: Mapping[str, Any]):
        return self.adapter.on_volc_function_call(event)

    def on_remote_audio_volume(self, uid: str, volume: float | int) -> None:
        self.adapter.on_volc_remote_audio_volume(uid, volume)

    def on_remote_audio_properties_report(self, report: Any) -> None:
        """Accepts common RTC audio property report shapes.

        Supported examples:
        - [{"uid": "bot", "volume": 80}]
        - {"uid": "bot", "volume": 80}
        - {"speakers": [{"uid": "bot", "volume": 80}]}
        - [{"streamKey": {"userId": "bot"}, "audioPropertiesInfo": {"linearVolume": 80}}]
        """
        for item in _iter_audio_report_items(report):
            uid = _extract_audio_uid(item)
            volume = _extract_audio_volume(item)
            if uid:
                self.adapter.on_volc_remote_audio_volume(str(uid), volume)


def _iter_audio_report_items(report: Any) -> list[Mapping[str, Any]]:
    if isinstance(report, Mapping):
        for key in ("speakers", "audio_properties", "audioProperties", "remote_audio_properties"):
            value = report.get(key)
            if isinstance(value, list):
                return [item for item in value if isinstance(item, Mapping)]
        return [report]
    if isinstance(report, list):
        return [item for item in report if isinstance(item, Mapping)]
    return []


def _iter_mapping_items(value: Any) -> list[Mapping[str, Any]]:
    if isinstance(value, Mapping):
        for key in ("data", "messages", "subtitles", "subtitleMessages", "subtitle_messages", "result"):
            nested = value.get(key)
            if isinstance(nested, list):
                return [item for item in nested if isinstance(item, Mapping)]
            if isinstance(nested, Mapping):
                return [nested]
        return [value]
    if isinstance(value, list):
        return [item for item in value if isinstance(item, Mapping)]
    return []


def _extract_audio_uid(item: Mapping[str, Any]) -> str:
    for key in ("uid", "user_id", "userId"):
        if item.get(key):
            return str(item[key])
    stream_key = item.get("streamKey", item.get("stream_key", {}))
    if isinstance(stream_key, Mapping):
        for key in ("userId", "uid", "user_id"):
            if stream_key.get(key):
                return str(stream_key[key])
    return ""


def _extract_audio_volume(item: Mapping[str, Any]) -> float:
    for key in ("volume", "linear_volume", "linearVolume", "audio_volume"):
        if key in item:
            return _safe_float(item[key])
    info = item.get("audioPropertiesInfo", item.get("audio_properties_info", {}))
    if isinstance(info, Mapping):
        if "linearVolume" in info:
            return _safe_float(info["linearVolume"]) / 255.0
        if "linear_volume" in info:
            return _safe_float(info["linear_volume"]) / 255.0
        if "volume" in info:
            return _safe_float(info["volume"])
    return 0.0


def _safe_float(value: Any) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return 0.0
