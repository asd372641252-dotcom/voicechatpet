from __future__ import annotations

import ctypes
import ctypes.wintypes
import io
import os
import time
from dataclasses import dataclass
from typing import Callable

from .types import FrameCaptureResult, ScreenFrame, VisionObserverConfig, new_id, now_ms

try:
    from PIL import Image, ImageChops, ImageGrab
except Exception:  # pragma: no cover - exercised only on minimal Python installs.
    Image = None
    ImageChops = None
    ImageGrab = None


CaptureFunc = Callable[[bool], tuple[object, str, str]]


@dataclass(frozen=True)
class ActiveWindowInfo:
    title: str = ""
    process_name: str = ""
    bbox: tuple[int, int, int, int] | None = None


class VisionFrameProvider:
    """Captures throttled, downscaled screen frames for vision-sidecar analysis."""

    def __init__(
        self,
        config: VisionObserverConfig,
        *,
        capture_func: CaptureFunc | None = None,
        clock_ms: Callable[[], int] = now_ms,
    ) -> None:
        self.config = config
        self.capture_func = capture_func
        self.clock_ms = clock_ms
        self._last_capture_attempt_ms = 0
        self._last_diff_image: object | None = None
        self._frame_index = 0

    def poll(self, *, force: bool = False, ignore_diff: bool = False) -> FrameCaptureResult:
        trace_id = new_id("capture")
        started = time.perf_counter()
        if not self.config.enabled:
            return self._skip("disabled", trace_id, started)
        current_ms = self.clock_ms()
        if (
            not force
            and self._last_capture_attempt_ms
            and current_ms - self._last_capture_attempt_ms < max(1, self.config.capture_interval_ms)
        ):
            return self._skip("interval_wait", trace_id, started)
        self._last_capture_attempt_ms = current_ms

        try:
            image, window_title, process_name = self._capture()
            image = self._normalize_image(image)
            if image is None:
                return self._skip("capture_unavailable", trace_id, started)
            diff_image = self._make_diff_image(image)
            diff_ratio = self._diff_ratio(self._last_diff_image, diff_image)
            self._last_diff_image = diff_image
            if self._frame_index > 0 and diff_ratio < self.config.min_diff_ratio and not ignore_diff:
                return FrameCaptureResult(
                    frame=None,
                    skipped=True,
                    reason="diff_below_threshold",
                    trace_id=trace_id,
                    latency_ms=_elapsed_ms(started),
                    diff_ratio=diff_ratio,
                )

            frame_image = self._resize_for_api(image)
            jpeg = self._encode_jpeg(frame_image)
            self._frame_index += 1
            frame = ScreenFrame(
                frame_id=f"frame-{current_ms}-{self._frame_index}",
                captured_at_ms=current_ms,
                window_title=window_title,
                process_name=process_name,
                image_jpeg=jpeg,
                width=int(frame_image.width),
                height=int(frame_image.height),
                diff_ratio=diff_ratio,
            )
            return FrameCaptureResult(
                frame=frame,
                skipped=False,
                reason="captured",
                trace_id=trace_id,
                latency_ms=_elapsed_ms(started),
                diff_ratio=diff_ratio,
            )
        except Exception as exc:
            return FrameCaptureResult(
                frame=None,
                skipped=True,
                reason=f"capture_error:{type(exc).__name__}",
                trace_id=trace_id,
                latency_ms=_elapsed_ms(started),
            )

    def current_window_info(self) -> ActiveWindowInfo:
        return _active_window_info() if self.config.active_window_only else ActiveWindowInfo()

    def _capture(self) -> tuple[object, str, str]:
        if self.capture_func is not None:
            return self.capture_func(self.config.active_window_only)
        if ImageGrab is None:
            raise RuntimeError("Pillow ImageGrab is not available")
        info = self.current_window_info()
        bbox = info.bbox if self.config.active_window_only else None
        image = ImageGrab.grab(bbox=bbox, all_screens=not self.config.active_window_only)
        return image, info.title, info.process_name

    def _normalize_image(self, image: object) -> object | None:
        if Image is None or image is None:
            return None
        if isinstance(image, Image.Image):
            return image.convert("RGB")
        return None

    def _resize_for_api(self, image: object) -> object:
        if Image is None:
            return image
        max_width = max(1, int(self.config.max_width))
        if image.width <= max_width:
            return image
        height = max(1, int(round(image.height * (max_width / float(image.width)))))
        return image.resize((max_width, height), Image.Resampling.LANCZOS)

    def _make_diff_image(self, image: object) -> object:
        if Image is None:
            return image
        sample = image.convert("L")
        sample.thumbnail((96, 54), Image.Resampling.BILINEAR)
        return sample.copy()

    def _diff_ratio(self, previous: object | None, current: object) -> float:
        if previous is None or ImageChops is None or Image is None:
            return 1.0
        if not isinstance(previous, Image.Image) or not isinstance(current, Image.Image):
            return 1.0
        if previous.size != current.size:
            return 1.0
        diff = ImageChops.difference(previous, current)
        histogram = diff.histogram()
        changed = sum(count for level, count in enumerate(histogram) if level >= 18)
        total = max(1, previous.width * previous.height)
        return changed / float(total)

    def _encode_jpeg(self, image: object) -> bytes:
        if Image is None or not isinstance(image, Image.Image):
            raise RuntimeError("Pillow Image is not available")
        buffer = io.BytesIO()
        quality = max(1, min(95, int(self.config.jpeg_quality)))
        image.save(buffer, format="JPEG", quality=quality, optimize=True)
        return buffer.getvalue()

    def _skip(self, reason: str, trace_id: str, started: float) -> FrameCaptureResult:
        return FrameCaptureResult(
            frame=None,
            skipped=True,
            reason=reason,
            trace_id=trace_id,
            latency_ms=_elapsed_ms(started),
        )


def _active_window_info() -> ActiveWindowInfo:
    if os.name != "nt":
        return ActiveWindowInfo()
    try:
        user32 = ctypes.windll.user32
        hwnd = user32.GetForegroundWindow()
        if not hwnd:
            return ActiveWindowInfo()
        length = user32.GetWindowTextLengthW(hwnd)
        buffer = ctypes.create_unicode_buffer(length + 1)
        user32.GetWindowTextW(hwnd, buffer, length + 1)
        rect = ctypes.wintypes.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(rect))
        pid = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        title = buffer.value.strip()
        process_name = _process_name_from_pid(int(pid.value))
        bbox = (int(rect.left), int(rect.top), int(rect.right), int(rect.bottom))
        if bbox[2] <= bbox[0] or bbox[3] <= bbox[1]:
            bbox = None
        return ActiveWindowInfo(title=title, process_name=process_name, bbox=bbox)
    except Exception:
        return ActiveWindowInfo()


def _process_name_from_pid(pid: int) -> str:
    if os.name != "nt" or pid <= 0:
        return ""
    try:
        kernel32 = ctypes.windll.kernel32
        psapi = ctypes.windll.psapi
        query_limited = 0x1000
        process = kernel32.OpenProcess(query_limited | 0x0400, False, pid)
        if not process:
            return ""
        try:
            buffer = ctypes.create_unicode_buffer(260)
            if psapi.GetModuleBaseNameW(process, None, buffer, len(buffer)):
                return buffer.value
        finally:
            kernel32.CloseHandle(process)
    except Exception:
        return ""
    return ""


def _elapsed_ms(started: float) -> float:
    return round((time.perf_counter() - started) * 1000.0, 3)
