from __future__ import annotations

import os
import sys
import time
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from src.voice_backends.omnivoice_gateway_tts_provider import (
    DEFAULT_GATEWAY_URL,
    OmniVoiceGatewayTTSConfig,
    OmniVoiceGatewayTTSProvider,
)


class OmniVoiceGatewayTTSProviderIntegrationTest(unittest.TestCase):
    def test_speak_downloads_plays_and_interrupts(self) -> None:
        token = os.getenv("OMNIVOICE_API_TOKEN") or os.getenv("API_TOKEN")
        if not token:
            self.skipTest("Set OMNIVOICE_API_TOKEN to run the real OmniVoice Gateway integration test.")

        states: list[str] = []
        segments: list[dict] = []
        provider = OmniVoiceGatewayTTSProvider(
            OmniVoiceGatewayTTSConfig(
                gateway_url=os.getenv("OMNIVOICE_GATEWAY_URL", DEFAULT_GATEWAY_URL),
                api_token=token,
                voice_id=os.getenv("OMNIVOICE_VOICE_ID", "role_001"),
                lang=os.getenv("OMNIVOICE_LANG", "zh"),
                cache_dir=ROOT / ".tmp" / "cache" / "tts_test",
                playback_enabled=_env_bool("OMNIVOICE_TEST_PLAYBACK", True),
            ),
            on_state_change=lambda state, payload: states.append(state),
            on_segment_ready=lambda payload: segments.append(dict(payload)),
        )

        job_id = provider.speak("这是 OmniVoice 网关本地播放测试。")
        self.assertTrue(job_id)
        self._wait_until(lambda: provider.last_stats is not None and provider.last_stats.status == "done", timeout=120)

        stats = provider.last_stats
        self.assertIsNotNone(stats)
        assert stats is not None
        self.assertGreaterEqual(stats.downloaded_segments, 1)
        self.assertGreaterEqual(len(stats.local_paths), 1)
        self.assertIn("thinking", states)
        self.assertIn("speaking", states)
        self.assertEqual(provider.state, "idle")
        for local_path in stats.local_paths:
            self.assertTrue(Path(local_path).exists(), local_path)
        print(
            "[omnivoice_provider] downloaded=%s first_segment=%.3f paths=%s"
            % (
                stats.downloaded_segments,
                stats.first_segment_elapsed_seconds or -1.0,
                ";".join(stats.local_paths),
            )
        )

        interrupt_job_id = provider.speak(
            "这是用于模拟用户打断的一段稍长文本。第一段开始后，测试会立刻打断并清空播放队列。"
            "后续到达的分段音频应该被丢弃，不应该继续播放。"
        )
        self.assertTrue(interrupt_job_id)
        time.sleep(1.0)
        provider.handle_interrupt("user_speaking")
        time.sleep(0.3)
        self.assertFalse(provider.is_playing())
        self.assertEqual(provider.state, "idle")
        provider.stop()
        print("[omnivoice_provider] interrupt_ok job_id=%s" % interrupt_job_id)

    def _wait_until(self, predicate, *, timeout: float) -> None:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if predicate():
                return
            time.sleep(0.1)
        self.fail("Timed out waiting for OmniVoice provider test condition.")


def _env_bool(name: str, default: bool) -> bool:
    value = os.getenv(name)
    if value is None:
        return default
    return value.strip().lower() not in {"0", "false", "no", "off", ""}


if __name__ == "__main__":
    unittest.main(verbosity=2)
