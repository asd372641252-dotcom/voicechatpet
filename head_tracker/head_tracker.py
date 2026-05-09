from __future__ import annotations

import argparse
import json
import math
import os
import socket
import tempfile
import threading
import time
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Dict, Iterable, Optional, Tuple

import cv2
import mediapipe as mp
import numpy as np

SCRIPT_DIR = os.path.dirname(os.path.realpath(__file__))

if os.name == "nt":
    import msvcrt
else:
    import fcntl


def clamp(value: float, low: float, high: float) -> float:
    return max(low, min(high, value))


def apply_deadzone(value: float, deadzone: float) -> float:
    magnitude = abs(value)
    if magnitude <= deadzone:
        return 0.0

    if deadzone >= 1.0:
        return math.copysign(1.0, value)

    return math.copysign((magnitude - deadzone) / (1.0 - deadzone), value)


def lerp(current: float, target: float, alpha: float) -> float:
    return current + (target - current) * clamp(alpha, 0.0, 1.0)


def ema_alpha(cutoff_hz: float, dt: float) -> float:
    if cutoff_hz <= 0.0:
        return 1.0

    tau = 1.0 / (2.0 * math.pi * cutoff_hz)
    return 1.0 / (1.0 + tau / max(dt, 1e-5))


def write_status_file(path: str, payload: Dict[str, object]) -> None:
    if not path:
        return

    try:
        directory = os.path.dirname(os.path.abspath(path))
        if directory:
            os.makedirs(directory, exist_ok=True)
        data = dict(payload)
        data["timestamp"] = time.time()
        temp_path = path + ".tmp"
        with open(temp_path, "w", encoding="utf-8") as handle:
            json.dump(data, handle, ensure_ascii=False, separators=(",", ":"))
        os.replace(temp_path, path)
    except Exception:
        pass


class SingleInstanceLock:
    def __init__(self, path: str) -> None:
        self.path = path
        self._handle = None

    def acquire(self) -> bool:
        directory = os.path.dirname(os.path.abspath(self.path))
        if directory:
            os.makedirs(directory, exist_ok=True)
        handle = open(self.path, "a+", encoding="utf-8")
        try:
            if os.name == "nt":
                handle.seek(0)
                msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            handle.close()
            return False

        handle.seek(0)
        handle.truncate()
        handle.write(json.dumps({"pid": os.getpid(), "timestamp": time.time()}, separators=(",", ":")))
        handle.flush()
        self._handle = handle
        return True

    def release(self) -> None:
        handle = self._handle
        self._handle = None
        if handle is None:
            return
        try:
            if os.name == "nt":
                handle.seek(0)
                msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(handle.fileno(), fcntl.LOCK_UN)
        finally:
            handle.close()
        try:
            os.remove(self.path)
        except OSError:
            pass


def tracker_lock_path(args: argparse.Namespace) -> str:
    root = os.path.dirname(os.path.abspath(args.status_file)) if args.status_file else tempfile.gettempdir()
    host = str(args.host).replace(".", "_").replace(":", "_")
    return os.path.join(root, f"head_tracker_{host}_{args.port}_camera_{args.camera_index}.lock")


class FrameHub:
    def __init__(self, host: str, port: int) -> None:
        self.host = host
        self.port = port
        self._lock = threading.Lock()
        self._frame: Optional[bytes] = None
        self._frame_count = 0
        self._last_frame_at = 0.0
        self._last_encode_at = 0.0
        self._width = 0
        self._height = 0
        self._fps = 0.0
        self._server: Optional[ThreadingHTTPServer] = None
        self._thread: Optional[threading.Thread] = None

    @property
    def enabled(self) -> bool:
        return self.port > 0

    @property
    def stream_url(self) -> str:
        return f"http://{self.host}:{self.port}/stream.mjpg"

    @property
    def status_url(self) -> str:
        return f"http://{self.host}:{self.port}/status"

    def start(self) -> None:
        if not self.enabled:
            return

        hub = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, format: str, *args) -> None:  # noqa: A002
                return

            def do_OPTIONS(self) -> None:
                self.send_response(204)
                self._send_common_headers()
                self.end_headers()

            def do_GET(self) -> None:
                if self.path.startswith("/status"):
                    self._serve_status()
                    return
                if self.path.startswith("/snapshot.jpg"):
                    self._serve_snapshot()
                    return
                if self.path.startswith("/stream.mjpg"):
                    self._serve_stream()
                    return
                self.send_response(404)
                self._send_common_headers()
                self.end_headers()

            def _send_common_headers(self) -> None:
                self.send_header("Access-Control-Allow-Origin", "*")
                self.send_header("Access-Control-Allow-Methods", "GET, OPTIONS")
                self.send_header("Access-Control-Allow-Headers", "Content-Type")
                self.send_header("Cache-Control", "no-cache, no-store, must-revalidate")
                self.send_header("Pragma", "no-cache")
                self.send_header("Expires", "0")

            def _serve_status(self) -> None:
                payload = hub.status()
                data = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
                self.send_response(200)
                self._send_common_headers()
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def _serve_snapshot(self) -> None:
                frame, _count = hub.get_frame()
                if not frame:
                    self.send_response(503)
                    self._send_common_headers()
                    self.end_headers()
                    return

                self.send_response(200)
                self._send_common_headers()
                self.send_header("Content-Type", "image/jpeg")
                self.send_header("Content-Length", str(len(frame)))
                self.end_headers()
                self.wfile.write(frame)

            def _serve_stream(self) -> None:
                self.send_response(200)
                self._send_common_headers()
                self.send_header("Content-Type", "multipart/x-mixed-replace; boundary=frame")
                self.end_headers()
                last_count = -1
                while True:
                    frame, count = hub.get_frame()
                    if not frame or count == last_count:
                        time.sleep(0.02)
                        continue
                    last_count = count
                    try:
                        self.wfile.write(b"--frame\r\n")
                        self.wfile.write(b"Content-Type: image/jpeg\r\n")
                        self.wfile.write(f"Content-Length: {len(frame)}\r\n\r\n".encode("ascii"))
                        self.wfile.write(frame)
                        self.wfile.write(b"\r\n")
                        self.wfile.flush()
                    except (BrokenPipeError, ConnectionError, OSError):
                        break

        self._server = ThreadingHTTPServer((self.host, self.port), Handler)
        self._server.daemon_threads = True
        self._thread = threading.Thread(target=self._server.serve_forever, name="head_tracker_frame_hub", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        server = self._server
        self._server = None
        if server is not None:
            server.shutdown()
            server.server_close()
        thread = self._thread
        self._thread = None
        if thread is not None and thread.is_alive():
            thread.join(timeout=1.0)

    def maybe_update(self, frame, fps_value: float, max_fps: int, jpeg_quality: int) -> None:
        if not self.enabled:
            return
        now = time.perf_counter()
        interval = 1.0 / max(1, max_fps)
        if now - self._last_encode_at < interval:
            return
        self._last_encode_at = now
        quality = int(clamp(float(jpeg_quality), 35.0, 95.0))
        ok, encoded = cv2.imencode(".jpg", frame, [int(cv2.IMWRITE_JPEG_QUALITY), quality])
        if not ok:
            return
        height, width = frame.shape[:2]
        data = encoded.tobytes()
        with self._lock:
            self._frame = data
            self._frame_count += 1
            self._last_frame_at = time.time()
            self._width = int(width)
            self._height = int(height)
            self._fps = float(fps_value)

    def get_frame(self) -> Tuple[Optional[bytes], int]:
        with self._lock:
            return self._frame, self._frame_count

    def status(self) -> Dict[str, object]:
        with self._lock:
            age = time.time() - self._last_frame_at if self._last_frame_at > 0.0 else 0.0
            return {
                "ok": self.enabled,
                "frameCount": self._frame_count,
                "lastFrameAt": self._last_frame_at,
                "lastFrameAgeSec": age,
                "width": self._width,
                "height": self._height,
                "fps": self._fps,
                "streamUrl": self.stream_url,
            }


@dataclass
class TrackerState:
    face_found: bool = False
    face_center_x: float = 0.0
    face_center_y: float = 0.0
    face_width_px: float = 0.0
    yaw: float = 0.0
    pitch: float = 0.0
    roll: float = 0.0
    z_cm: float = 0.0
    z_offset: float = 0.0

    def to_packet(self) -> Dict[str, float | bool | str]:
        return {
            "source": "standalone_mediapipe",
            "face_found": self.face_found,
            "face_center_x": self.face_center_x,
            "face_center_y": self.face_center_y,
            "face_width_px": self.face_width_px,
            "yaw": self.yaw,
            "pitch": self.pitch,
            "roll": self.roll,
            "z_cm": self.z_cm,
            "z_offset": self.z_offset,
            "timestamp": time.time(),
        }


class LowPassTracker:
    def __init__(self, cutoff_hz: float, deadzone_xy: float, deadzone_z: float, return_to_center_speed: float) -> None:
        self.cutoff_hz = cutoff_hz
        self.deadzone_xy = deadzone_xy
        self.deadzone_z = deadzone_z
        self.return_to_center_speed = return_to_center_speed
        self.state = TrackerState()
        self._last_time = time.perf_counter()

    def update(self, measurement: Optional[TrackerState]) -> TrackerState:
        now = time.perf_counter()
        dt = now - self._last_time
        self._last_time = now

        if measurement is None:
            alpha = clamp(dt * self.return_to_center_speed, 0.0, 1.0)
            self.state.face_found = False
            self.state.face_center_x = lerp(self.state.face_center_x, 0.0, alpha)
            self.state.face_center_y = lerp(self.state.face_center_y, 0.0, alpha)
            self.state.z_offset = lerp(self.state.z_offset, 0.0, alpha)
            self.state.yaw = lerp(self.state.yaw, 0.0, alpha)
            self.state.pitch = lerp(self.state.pitch, 0.0, alpha)
            self.state.roll = lerp(self.state.roll, 0.0, alpha)
            self.state.face_width_px = lerp(self.state.face_width_px, 0.0, alpha)
            return self.state

        alpha = ema_alpha(self.cutoff_hz, dt)
        self.state.face_found = True
        self.state.face_center_x = lerp(self.state.face_center_x, measurement.face_center_x, alpha)
        self.state.face_center_y = lerp(self.state.face_center_y, measurement.face_center_y, alpha)
        self.state.face_width_px = lerp(self.state.face_width_px, measurement.face_width_px, alpha)
        self.state.yaw = lerp(self.state.yaw, measurement.yaw, alpha)
        self.state.pitch = lerp(self.state.pitch, measurement.pitch, alpha)
        self.state.roll = lerp(self.state.roll, measurement.roll, alpha)
        self.state.z_cm = lerp(self.state.z_cm, measurement.z_cm, alpha)
        self.state.z_offset = lerp(self.state.z_offset, measurement.z_offset, alpha)

        self.state.face_center_x = apply_deadzone(self.state.face_center_x, self.deadzone_xy)
        self.state.face_center_y = apply_deadzone(self.state.face_center_y, self.deadzone_xy)
        self.state.z_offset = apply_deadzone(self.state.z_offset, self.deadzone_z)
        return self.state


def landmark_xy(landmarks, index: int, width: int, height: int) -> Tuple[float, float]:
    point = landmarks[index]
    return point.x * width, point.y * height


def estimate_pose_degrees(landmarks, width: int, height: int, face_width_px: float) -> Tuple[float, float, float]:
    left_eye = landmark_xy(landmarks, 33, width, height)
    right_eye = landmark_xy(landmarks, 263, width, height)
    nose = landmark_xy(landmarks, 1, width, height)
    mouth_left = landmark_xy(landmarks, 61, width, height)
    mouth_right = landmark_xy(landmarks, 291, width, height)

    eye_mid_x = (left_eye[0] + right_eye[0]) * 0.5
    eye_mid_y = (left_eye[1] + right_eye[1]) * 0.5
    mouth_mid_y = (mouth_left[1] + mouth_right[1]) * 0.5

    roll = math.degrees(math.atan2(right_eye[1] - left_eye[1], right_eye[0] - left_eye[0]))

    width_safe = max(face_width_px, 1.0)
    yaw = clamp((nose[0] - eye_mid_x) / width_safe * 95.0, -45.0, 45.0)

    eye_to_mouth = max(mouth_mid_y - eye_mid_y, 1.0)
    nose_ratio = (nose[1] - eye_mid_y) / eye_to_mouth
    pitch = clamp((0.52 - nose_ratio) * 85.0, -35.0, 35.0)

    return yaw, pitch, roll


def measure_face(
    landmarks,
    width: int,
    height: int,
    baseline_face_width_px: float,
    default_distance_cm: float,
    center_mode: str,
) -> TrackerState:
    xs = np.array([point.x * width for point in landmarks], dtype=np.float32)
    ys = np.array([point.y * height for point in landmarks], dtype=np.float32)

    min_x = float(np.min(xs))
    max_x = float(np.max(xs))
    min_y = float(np.min(ys))
    max_y = float(np.max(ys))

    center_x_px = (min_x + max_x) * 0.5
    center_y_px = (min_y + max_y) * 0.5
    face_width_px = max(max_x - min_x, 1.0)

    if center_mode == "eyes":
        left_eye = landmark_xy(landmarks, 33, width, height)
        right_eye = landmark_xy(landmarks, 263, width, height)
        # Use the viewer's eye midpoint as the translation anchor. A whole-face
        # bounding box shifts and shrinks during yaw, which makes pure head
        # rotation look like camera-space translation.
        center_x_px = (left_eye[0] + right_eye[0]) * 0.5
        center_y_px = (left_eye[1] + right_eye[1]) * 0.5

    normalized_x = (center_x_px / max(width, 1) - 0.5) * 2.0
    normalized_y = -(center_y_px / max(height, 1) - 0.5) * 2.0

    z_cm = default_distance_cm * baseline_face_width_px / face_width_px
    z_offset = (default_distance_cm - z_cm) / max(default_distance_cm, 1.0)
    z_offset = clamp(z_offset, -1.0, 1.0)

    yaw, pitch, roll = estimate_pose_degrees(landmarks, width, height, face_width_px)

    return TrackerState(
        face_found=True,
        face_center_x=clamp(normalized_x, -1.0, 1.0),
        face_center_y=clamp(normalized_y, -1.0, 1.0),
        face_width_px=face_width_px,
        yaw=yaw,
        pitch=pitch,
        roll=roll,
        z_cm=z_cm,
        z_offset=z_offset,
    )


def draw_debug(frame, state: TrackerState, fps: float) -> None:
    color = (40, 220, 80) if state.face_found else (80, 80, 255)
    text = (
        f"found={state.face_found} x={state.face_center_x:+.2f} "
        f"y={state.face_center_y:+.2f} z={state.z_offset:+.2f} "
        f"w={state.face_width_px:.0f}px yaw={state.yaw:+.0f} pitch={state.pitch:+.0f} roll={state.roll:+.0f} fps={fps:.1f}"
    )
    cv2.putText(frame, text, (12, 28), cv2.FONT_HERSHEY_SIMPLEX, 0.52, color, 2, cv2.LINE_AA)
    h, w = frame.shape[:2]
    cv2.line(frame, (w // 2 - 18, h // 2), (w // 2 + 18, h // 2), (180, 180, 180), 1)
    cv2.line(frame, (w // 2, h // 2 - 18), (w // 2, h // 2 + 18), (180, 180, 180), 1)


class FaceBackend:
    def detect(self, rgb_frame: np.ndarray, timestamp_ms: int):
        raise NotImplementedError

    def close(self) -> None:
        pass


class FaceMeshSolutionsBackend(FaceBackend):
    def __init__(self, args: argparse.Namespace) -> None:
        self.face_mesh = mp.solutions.face_mesh.FaceMesh(
            static_image_mode=False,
            max_num_faces=1,
            refine_landmarks=False,
            min_detection_confidence=args.min_detection_confidence,
            min_tracking_confidence=args.min_tracking_confidence,
        )

    def detect(self, rgb_frame: np.ndarray, timestamp_ms: int):
        result = self.face_mesh.process(rgb_frame)
        if not result.multi_face_landmarks:
            return None

        return result.multi_face_landmarks[0].landmark

    def close(self) -> None:
        self.face_mesh.close()


class FaceLandmarkerTasksBackend(FaceBackend):
    def __init__(self, args: argparse.Namespace) -> None:
        from mediapipe.tasks.python.core import base_options
        from mediapipe.tasks.python.vision import face_landmarker
        from mediapipe.tasks.python.vision.core import vision_task_running_mode

        model_path = resolve_model_path(args.model_path)
        if not os.path.exists(model_path):
            raise FileNotFoundError(
                "Face Landmarker model not found: "
                + model_path
                + ". Download face_landmarker.task or pass --model-path."
            )

        options = face_landmarker.FaceLandmarkerOptions(
            base_options=base_options.BaseOptions(model_asset_path=model_path),
            running_mode=vision_task_running_mode.VisionTaskRunningMode.VIDEO,
            num_faces=1,
            min_face_detection_confidence=args.min_detection_confidence,
            min_face_presence_confidence=args.min_detection_confidence,
            min_tracking_confidence=args.min_tracking_confidence,
            output_face_blendshapes=False,
            output_facial_transformation_matrixes=False,
        )
        self.landmarker = face_landmarker.FaceLandmarker.create_from_options(options)

    def detect(self, rgb_frame: np.ndarray, timestamp_ms: int):
        image = mp.Image(image_format=mp.ImageFormat.SRGB, data=np.ascontiguousarray(rgb_frame))
        result = self.landmarker.detect_for_video(image, timestamp_ms)
        if not result.face_landmarks:
            return None

        return result.face_landmarks[0]

    def close(self) -> None:
        self.landmarker.close()


def create_face_backend(args: argparse.Namespace) -> FaceBackend:
    if args.backend == "solutions":
        return FaceMeshSolutionsBackend(args)

    if args.backend == "tasks":
        return FaceLandmarkerTasksBackend(args)

    try:
        return FaceLandmarkerTasksBackend(args)
    except Exception as tasks_exception:
        if hasattr(mp, "solutions") and hasattr(mp.solutions, "face_mesh"):
            print(
                "MediaPipe Tasks backend failed; falling back to solutions: "
                + repr(tasks_exception),
                flush=True,
            )
            return FaceMeshSolutionsBackend(args)
        raise


def resolve_model_path(model_path: str) -> str:
    candidates = []
    if model_path:
        candidates.append(os.path.abspath(model_path))
        if not os.path.isabs(model_path):
            candidates.append(os.path.abspath(os.path.join(SCRIPT_DIR, model_path)))

    candidates.append(os.path.join(SCRIPT_DIR, "models", "face_landmarker.task"))
    candidates.append(os.path.join(os.getcwd(), "models", "face_landmarker.task"))

    for candidate in candidates:
        real_candidate = os.path.realpath(candidate)
        if os.path.exists(real_candidate):
            return real_candidate

    return os.path.realpath(candidates[0] if candidates else os.path.join(SCRIPT_DIR, "models", "face_landmarker.task"))


def run(args: argparse.Namespace) -> None:
    frame_hub = FrameHub(args.frame_host, args.frame_port)
    write_status_file(args.status_file, {
        "event": "opening",
        "camera_index": args.camera_index,
        "width": args.width,
        "height": args.height,
        "fps": args.fps,
        "backend": args.backend,
        "frame_server_url": frame_hub.stream_url if frame_hub.enabled else "",
    })
    cap = cv2.VideoCapture(args.camera_index, cv2.CAP_DSHOW)
    if not cap.isOpened():
        write_status_file(args.status_file, {
            "event": "open_failed",
            "camera_index": args.camera_index,
            "width": args.width,
            "height": args.height,
            "fps": args.fps,
            "backend": args.backend,
            "frame_server_url": frame_hub.stream_url if frame_hub.enabled else "",
        })
        raise RuntimeError(f"Could not open camera index {args.camera_index}")

    cap.set(cv2.CAP_PROP_FRAME_WIDTH, args.width)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, args.height)
    cap.set(cv2.CAP_PROP_FPS, args.fps)
    cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)

    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    target = (args.host, args.port)
    frame_hub.start()

    try:
        write_status_file(args.status_file, {
            "event": "creating_backend",
            "camera_index": args.camera_index,
            "width": args.width,
            "height": args.height,
            "fps": args.fps,
            "backend": args.backend,
            "frame_server_url": frame_hub.stream_url if frame_hub.enabled else "",
        })
        backend = create_face_backend(args)
    except Exception as exception:
        write_status_file(args.status_file, {
            "event": "backend_error",
            "camera_index": args.camera_index,
            "width": args.width,
            "height": args.height,
            "fps": args.fps,
            "backend": args.backend,
            "error": repr(exception),
            "frame_server_url": frame_hub.stream_url if frame_hub.enabled else "",
        })
        frame_hub.stop()
        cap.release()
        cv2.destroyAllWindows()
        raise
    write_status_file(args.status_file, {
        "event": "backend_ready",
        "camera_index": args.camera_index,
        "width": args.width,
        "height": args.height,
        "fps": args.fps,
        "backend": args.backend,
        "frame_server_url": frame_hub.stream_url if frame_hub.enabled else "",
    })
    tracker = LowPassTracker(args.cutoff_hz, args.deadzone_xy, args.deadzone_z, args.return_to_center_speed)

    last_face_time = 0.0
    fps_time = time.perf_counter()
    fps_value = 0.0
    frames = 0
    total_frames = 0
    frame_interval_ms = int(1000.0 / max(1, args.fps))
    last_timestamp_ms = 0
    last_status_write = 0.0

    write_status_file(args.status_file, {
        "event": "opened",
        "camera_index": args.camera_index,
        "width": args.width,
        "height": args.height,
        "fps": args.fps,
        "backend": args.backend,
        "frame_server_url": frame_hub.stream_url if frame_hub.enabled else "",
    })

    try:
        while True:
            ok, frame = cap.read()
            if not ok:
                now = time.perf_counter()
                if now - last_status_write >= 0.5:
                    last_status_write = now
                    write_status_file(args.status_file, {
                        "event": "read_failed",
                        "camera_index": args.camera_index,
                        "frame_ok": False,
                        "total_frames": total_frames,
                        "fps_value": fps_value,
                    })
                time.sleep(0.005)
                continue

            if args.mirror:
                frame = cv2.flip(frame, 1)

            height, width = frame.shape[:2]
            frame_hub.maybe_update(frame, fps_value, args.frame_server_fps, args.frame_jpeg_quality)
            if total_frames == 0:
                write_status_file(args.status_file, {
                    "event": "first_frame",
                    "camera_index": args.camera_index,
                    "frame_ok": True,
                    "frame_width": width,
                    "frame_height": height,
                    "backend": args.backend,
                    "frame_server_url": frame_hub.stream_url if frame_hub.enabled else "",
                })

            rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            rgb.flags.writeable = False
            last_timestamp_ms += frame_interval_ms
            timestamp_ms = last_timestamp_ms
            if total_frames == 0:
                write_status_file(args.status_file, {
                    "event": "detecting_first_frame",
                    "camera_index": args.camera_index,
                    "frame_width": width,
                    "frame_height": height,
                    "backend": args.backend,
                })

            landmarks = backend.detect(rgb, timestamp_ms)

            measurement: Optional[TrackerState] = None
            hold_last_measurement = False
            if landmarks:
                measurement = measure_face(
                    landmarks,
                    width,
                    height,
                    args.baseline_face_width_px,
                    args.default_distance_cm,
                    args.center_mode,
                )
                last_face_time = time.perf_counter()
            elif time.perf_counter() - last_face_time < args.lost_grace_seconds:
                hold_last_measurement = True

            if hold_last_measurement:
                state = tracker.state
                state.face_found = True
            else:
                state = tracker.update(measurement)
            packet = state.to_packet()
            udp.sendto(json.dumps(packet, separators=(",", ":")).encode("utf-8"), target)

            frames += 1
            total_frames += 1
            now = time.perf_counter()
            if now - fps_time >= 0.5:
                fps_value = frames / (now - fps_time)
                frames = 0
                fps_time = now

            if args.print_every > 0 and total_frames % args.print_every == 0:
                print(json.dumps(packet, ensure_ascii=False, separators=(",", ":")))

            if now - last_status_write >= 0.5:
                last_status_write = now
                write_status_file(args.status_file, {
                    "event": "packet",
                    "camera_index": args.camera_index,
                    "frame_ok": True,
                    "frame_width": width,
                    "frame_height": height,
                    "total_frames": total_frames,
                    "fps_value": fps_value,
                    "packet": packet,
                    "frame_server_url": frame_hub.stream_url if frame_hub.enabled else "",
                })

            if args.preview:
                draw_debug(frame, state, fps_value)
                cv2.imshow("head_tracker", frame)
                key = cv2.waitKey(1) & 0xFF
                if key == 27 or key == ord("q"):
                    break
                if key == ord("c") and state.face_width_px > 0:
                    print(f"baseline_face_width_px={state.face_width_px:.1f}")

            if args.max_frames > 0 and total_frames >= args.max_frames:
                break
    finally:
        write_status_file(args.status_file, {
            "event": "closing",
            "camera_index": args.camera_index,
            "total_frames": total_frames,
            "fps_value": fps_value,
            "frame_server_url": frame_hub.stream_url if frame_hub.enabled else "",
        })
        frame_hub.stop()
        backend.close()

    cap.release()
    cv2.destroyAllWindows()


def parse_args(argv: Optional[Iterable[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="UVC webcam head tracker for Unity parallax prototypes.")
    parser.add_argument("--camera-index", type=int, default=0)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=5055)
    parser.add_argument("--width", type=int, default=640)
    parser.add_argument("--height", type=int, default=480)
    parser.add_argument("--fps", type=int, default=30)
    parser.add_argument("--baseline-face-width-px", type=float, default=170.0)
    parser.add_argument("--default-distance-cm", type=float, default=60.0)
    parser.add_argument("--cutoff-hz", type=float, default=4.0)
    parser.add_argument("--deadzone-xy", type=float, default=0.04)
    parser.add_argument("--deadzone-z", type=float, default=0.035)
    parser.add_argument("--center-mode", choices=("eyes", "bbox"), default="eyes")
    parser.add_argument("--return-to-center-speed", type=float, default=1.2)
    parser.add_argument("--lost-grace-seconds", type=float, default=0.80)
    parser.add_argument("--min-detection-confidence", type=float, default=0.55)
    parser.add_argument("--min-tracking-confidence", type=float, default=0.55)
    parser.add_argument("--backend", choices=("auto", "tasks", "solutions"), default="auto")
    parser.add_argument(
        "--model-path",
        default=os.path.join(SCRIPT_DIR, "models", "face_landmarker.task"),
    )
    parser.add_argument("--preview", action="store_true")
    parser.add_argument("--max-frames", type=int, default=0)
    parser.add_argument("--print-every", type=int, default=0)
    parser.add_argument("--status-file", default="")
    parser.add_argument("--frame-host", default="127.0.0.1")
    parser.add_argument("--frame-port", type=int, default=0)
    parser.add_argument("--frame-server-fps", type=int, default=15)
    parser.add_argument("--frame-jpeg-quality", type=int, default=92)
    parser.add_argument("--no-mirror", dest="mirror", action="store_false")
    parser.set_defaults(mirror=True)
    return parser.parse_args(argv)


if __name__ == "__main__":
    parsed_args = parse_args()
    instance_lock = SingleInstanceLock(tracker_lock_path(parsed_args))
    if not instance_lock.acquire():
        write_status_file(parsed_args.status_file, {
            "event": "duplicate_instance",
            "camera_index": parsed_args.camera_index,
            "port": parsed_args.port,
        })
        raise SystemExit(f"Another head_tracker instance is already running for UDP {parsed_args.port}.")
    try:
        run(parsed_args)
    finally:
        instance_lock.release()
