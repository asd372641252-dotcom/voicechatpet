from __future__ import annotations

import argparse
import json
import socket
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
UNITY_ROOT = ROOT / "unity" / "SilverWolfPet"
HEAD_TRACKER_ROOT = ROOT / "head_tracker"
HEAD_TRACKER_SCRIPT = HEAD_TRACKER_ROOT / "head_tracker.py"
HEAD_TRACKER_PYTHON = HEAD_TRACKER_ROOT / ".venv" / "Scripts" / "python.exe"
HEAD_TRACKER_MODEL = HEAD_TRACKER_ROOT / "models" / "face_landmarker.task"
SCENE_PATH = UNITY_ROOT / "Assets" / "Scenes" / "BlenderIndoorScene.unity"
FACE_TRACKER_SOURCE = UNITY_ROOT / "Assets" / "TransparentPet" / "Scripts" / "SceneHost" / "TransparentPetSceneFaceTracker.cs"
VOICE_LAUNCHER_SOURCE = UNITY_ROOT / "Assets" / "TransparentPet" / "Scripts" / "TransparentPetVoiceRuntimeLauncher.cs"


@dataclass(frozen=True)
class Message:
    path: str
    detail: str


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    parser = argparse.ArgumentParser(description="Offline face-tracking preflight for the Unity scene pet.")
    parser.add_argument("--skip-imports", action="store_true", help="Skip importing cv2/mediapipe in the tracker venv.")
    args = parser.parse_args()

    failures: list[Message] = []
    warnings: list[Message] = []
    facts: dict[str, object] = {}

    check_files(failures, warnings)
    check_scene_config(failures, warnings, facts)
    check_source_contracts(failures, facts)
    if not args.skip_imports:
        check_python_dependencies(failures, warnings, facts)
    check_ports(warnings, facts)

    for failure in failures:
        print(f"FAIL {failure.path}: {failure.detail}")
    for warning in warnings:
        print(f"WARN {warning.path}: {warning.detail}")

    print("FACT " + json.dumps(facts, ensure_ascii=False, sort_keys=True))

    if failures:
        print(f"\nFace tracking preflight failed: {len(failures)} failure(s), {len(warnings)} warning(s).")
        return 1

    print(f"Face tracking preflight passed: 0 failure(s), {len(warnings)} warning(s).")
    return 0


def check_files(failures: list[Message], warnings: list[Message]) -> None:
    required = {
        HEAD_TRACKER_SCRIPT: "head tracker script is missing",
        HEAD_TRACKER_MODEL: "MediaPipe face landmarker model is missing",
        SCENE_PATH: "Unity product scene is missing",
        FACE_TRACKER_SOURCE: "Unity scene face tracker source is missing",
        VOICE_LAUNCHER_SOURCE: "Unity voice launcher source is missing",
    }
    for path, detail in required.items():
        if not path.exists():
            failures.append(Message(to_rel(path), detail))

    if not HEAD_TRACKER_PYTHON.exists():
        warnings.append(Message(to_rel(HEAD_TRACKER_PYTHON), "tracker virtualenv python is missing"))


def check_scene_config(failures: list[Message], warnings: list[Message], facts: dict[str, object]) -> None:
    if not SCENE_PATH.exists():
        return

    text = SCENE_PATH.read_text(encoding="utf-8", errors="replace")
    required_tokens = {
        "TransparentPetSceneFaceTracker": "scene face tracker component is not serialized",
        "trackingBackend: 0": "tracking backend is not ExternalMediaPipe",
        "trackingEnabled: 1": "tracking is not enabled",
        "headFollowEnabled: 1": "head follow is not enabled",
        "cameraParallaxEnabled: 1": "camera parallax is not enabled",
        "cameraOrbitEnabled: 1": "camera orbit is not enabled",
        "mirrorHorizontal: 1": "horizontal mirror is not enabled",
        "mirrorVertical: 1": "vertical mirror is not enabled",
        "startCameraOnEnable: 1": "camera does not start on enable",
        "launchExternalProcess: 1": "external tracker process launch is disabled",
        "externalTrackerScript: head_tracker.py": "external tracker script is not head_tracker.py",
        "externalTrackerPort: 5055": "external tracker UDP port is not 5055",
        "requestedWidth: 1280": "requested camera width is not 1280",
        "requestedHeight: 720": "requested camera height is not 720",
    }
    for token, detail in required_tokens.items():
        if token not in text:
            failures.append(Message(to_rel(SCENE_PATH), detail))

    facts["scene_has_face_tracker"] = "TransparentPetSceneFaceTracker" in text
    facts["scene_udp_port"] = first_scene_value(text, "externalTrackerPort")
    facts["scene_camera_request"] = {
        "width": first_scene_value(text, "requestedWidth"),
        "height": first_scene_value(text, "requestedHeight"),
        "fps": first_scene_value(text, "requestedFps"),
    }
    facts["scene_serializes_frame_server_enabled"] = "externalFrameServerEnabled:" in text


def check_source_contracts(failures: list[Message], facts: dict[str, object]) -> None:
    if FACE_TRACKER_SOURCE.exists():
        tracker = FACE_TRACKER_SOURCE.read_text(encoding="utf-8", errors="replace")
        required = {
            "externalFrameServerEnabled = true": "frame server default is not enabled",
            "--frame-port": "external tracker command does not pass frame server port",
            "--frame-server-fps": "external tracker command does not pass frame server fps",
            "--frame-jpeg-quality": "external tracker command does not pass JPEG quality",
            "CameraHubStreamUrl": "camera hub stream URL is not exposed to voice launcher",
        }
        for token, detail in required.items():
            if token not in tracker:
                failures.append(Message(to_rel(FACE_TRACKER_SOURCE), detail))
        facts["frame_server_default_enabled"] = "externalFrameServerEnabled = true" in tracker
        facts["frame_server_default_port"] = "17863" if "externalFrameServerPort = 17863" in tracker else None

    if VOICE_LAUNCHER_SOURCE.exists():
        launcher = VOICE_LAUNCHER_SOURCE.read_text(encoding="utf-8", errors="replace")
        required = {
            "cameraVideoUseCameraHub = true": "voice launcher does not default to scene camera hub",
            'cameraVideoHubUrl = "http://127.0.0.1:17863/stream.mjpg"': "voice launcher camera hub URL is not 17863",
            "cameraVideoWidth = 1280": "voice launcher camera video width is not 1280",
            "cameraVideoHeight = 720": "voice launcher camera video height is not 720",
        }
        for token, detail in required.items():
            if token not in launcher:
                failures.append(Message(to_rel(VOICE_LAUNCHER_SOURCE), detail))
        facts["voice_uses_camera_hub_default"] = "cameraVideoUseCameraHub = true" in launcher


def check_python_dependencies(failures: list[Message], warnings: list[Message], facts: dict[str, object]) -> None:
    if not HEAD_TRACKER_PYTHON.exists():
        return

    code = (
        "import json, cv2, mediapipe as mp, numpy as np; "
        "print(json.dumps({"
        "'python':'ok',"
        "'cv2':getattr(cv2,'__version__','unknown'),"
        "'mediapipe':getattr(mp,'__version__','unknown'),"
        "'numpy':getattr(np,'__version__','unknown')"
        "}, ensure_ascii=False))"
    )
    result = subprocess.run(
        [str(HEAD_TRACKER_PYTHON), "-c", code],
        cwd=HEAD_TRACKER_ROOT,
        text=True,
        encoding="utf-8",
        errors="replace",
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        timeout=45,
        check=False,
    )
    if result.returncode != 0:
        failures.append(Message(to_rel(HEAD_TRACKER_PYTHON), "could not import cv2/mediapipe/numpy in tracker venv"))
        if result.stderr.strip():
            warnings.append(Message(to_rel(HEAD_TRACKER_PYTHON), result.stderr.strip().splitlines()[-1]))
        return

    try:
        facts["python_dependencies"] = json.loads(result.stdout.strip().splitlines()[-1])
    except (IndexError, json.JSONDecodeError):
        warnings.append(Message(to_rel(HEAD_TRACKER_PYTHON), "dependency import succeeded but version output was not parseable"))

    help_result = subprocess.run(
        [str(HEAD_TRACKER_PYTHON), str(HEAD_TRACKER_SCRIPT), "--help"],
        cwd=HEAD_TRACKER_ROOT,
        text=True,
        encoding="utf-8",
        errors="replace",
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        timeout=20,
        check=False,
    )
    if help_result.returncode != 0:
        failures.append(Message(to_rel(HEAD_TRACKER_SCRIPT), "head_tracker.py --help failed"))
        return
    for flag in ("--camera-index", "--port", "--width", "--height", "--frame-port", "--max-frames"):
        if flag not in help_result.stdout:
            failures.append(Message(to_rel(HEAD_TRACKER_SCRIPT), f"CLI flag missing: {flag}"))


def check_ports(warnings: list[Message], facts: dict[str, object]) -> None:
    udp_free = can_bind_udp("127.0.0.1", 5055)
    tcp_free = can_bind_tcp("127.0.0.1", 17863)
    facts["ports_free"] = {"udp_5055": udp_free, "tcp_17863": tcp_free}
    if not udp_free:
        warnings.append(Message("127.0.0.1:5055", "UDP face-tracking port is currently in use"))
    if not tcp_free:
        warnings.append(Message("127.0.0.1:17863", "camera frame server port is currently in use"))


def can_bind_udp(host: str, port: int) -> bool:
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        sock.bind((host, port))
        return True
    except OSError:
        return False
    finally:
        sock.close()


def can_bind_tcp(host: str, port: int) -> bool:
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        sock.bind((host, port))
        return True
    except OSError:
        return False
    finally:
        sock.close()


def first_scene_value(text: str, key: str) -> str | None:
    prefix = f"{key}:"
    for line in text.splitlines():
        stripped = line.strip()
        if stripped.startswith(prefix):
            return stripped[len(prefix) :].strip()
    return None


def to_rel(path: Path) -> str:
    try:
        return path.relative_to(ROOT).as_posix()
    except ValueError:
        return path.as_posix()


if __name__ == "__main__":
    raise SystemExit(main())
