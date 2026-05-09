from __future__ import annotations

import sys
import unittest
from pathlib import Path
from typing import Any, Mapping


ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from src.voice_backends.volc_rtc.volc_session_callback_bridge import VolcSessionCallbackBridge


class FakeAdapter:
    def __init__(self) -> None:
        self.bot_uids = {"bot_001"}
        self.subtitles: list[Mapping[str, Any]] = []
        self.states: list[Mapping[str, Any] | str] = []

    def on_volc_subtitle_event(self, event: Mapping[str, Any]) -> None:
        self.subtitles.append(event)

    def on_volc_ai_state_event(self, event: Mapping[str, Any] | str) -> None:
        self.states.append(event)


class VolcSessionCallbackBridgeVoiceOutputTest(unittest.TestCase):
    def test_subtitle_messages_emit_normalized_voice_output_callbacks(self) -> None:
        adapter = FakeAdapter()
        seen: list[Mapping[str, Any]] = []
        bridge = VolcSessionCallbackBridge(adapter, on_subtitle_event=lambda event: seen.append(dict(event)))

        bridge.on_subtitle_messages(
            [
                {"userId": "bot_001", "text": "哦，你来了", "definite": True},
                {"userId": "local_user", "text": "停一下", "definite": False},
            ]
        )

        self.assertEqual(len(adapter.subtitles), 2)
        self.assertEqual(len(seen), 2)
        self.assertEqual(seen[0]["speaker"], "ai")
        self.assertTrue(seen[0]["is_final"])
        self.assertEqual(seen[0]["text"], "哦，你来了")
        self.assertEqual(seen[1]["speaker"], "user")
        self.assertFalse(seen[1]["is_final"])

    def test_ai_state_callback_is_forwarded(self) -> None:
        adapter = FakeAdapter()
        seen: list[Mapping[str, Any] | str] = []
        bridge = VolcSessionCallbackBridge(adapter, on_ai_state_event=seen.append)

        bridge.on_ai_state({"state": "user_barge_in"})

        self.assertEqual(adapter.states, [{"state": "user_barge_in"}])
        self.assertEqual(seen, [{"state": "user_barge_in"}])


if __name__ == "__main__":
    unittest.main(verbosity=2)
