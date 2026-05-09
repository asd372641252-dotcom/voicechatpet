from __future__ import annotations

import argparse
import json
import math
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_CONFIG = ROOT / "config" / "volc_start_voice_chat.local.json"
DEFAULT_RAW_LOG = ROOT / "logs" / "scene_face_tracking_offline_probe.jsonl"
DEFAULT_BRIDGE_SCRIPT = ROOT / "scripts" / "run_volc_rtc_web_client.py"


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser(
        description="Offline probe for scene camera face-tracking packets. "
        "It does not start voice chat, open the browser, or request the real camera."
    )
    parser.add_argument("--config", default=str(DEFAULT_CONFIG))
    parser.add_argument("--bridge-script", default=str(DEFAULT_BRIDGE_SCRIPT))
    parser.add_argument("--bridge-host", default="127.0.0.1")
    parser.add_argument("--bridge-port", type=int, default=17974)
    parser.add_argument("--udp-host", default="127.0.0.1")
    parser.add_argument("--udp-port", type=int, default=5055)
    parser.add_argument("--samples", type=int, default=48)
    parser.add_argument("--fps", type=float, default=15.0)
    parser.add_argument("--yaw-deg", type=float, default=28.0)
    parser.add_argument("--pitch-deg", type=float, default=16.0)
    parser.add_argument("--center-amplitude", type=float, default=0.45)
    parser.add_argument("--depth-amplitude", type=float, default=0.5)
    parser.add_argument("--raw-log", default=str(DEFAULT_RAW_LOG))
    parser.add_argument(
        "--mode",
        choices=("bridge", "dry-run"),
        default="bridge",
        help="bridge starts the local HTTP bridge and posts packets through /api/face_tracking/packet.",
    )
    args = parser.parse_args()

    packets = _build_packet_sweep(args)
    if args.mode == "dry-run":
        for packet in packets[: min(len(packets), 8)]:
            print(json.dumps(packet, ensure_ascii=False, separators=(",", ":")))
        print(f"dry-run packet_count={len(packets)}")
        return 0

    proc = _start_bridge(args)
    try:
        bridge_base = f"http://{args.bridge_host}:{args.bridge_port}"
        _wait_health(bridge_base)
        before = _get_json(bridge_base + "/api/face_tracking/status")
        _check("initial face_tracking_status ok", bool(before.get("ok", True)), before)

        sent = 0
        for packet in packets:
            packet["timestamp"] = time.time()
            result = _post_json(bridge_base + "/api/face_tracking/packet", packet)
            _check("packet accepted by local bridge", result.get("ok") is True, result)
            sent += 1
            time.sleep(max(0.0, 1.0 / max(args.fps, 1.0)))

        after = _get_json(bridge_base + "/api/face_tracking/status")
        delta = int(after.get("packetCount") or 0) - int(before.get("packetCount") or 0)
        _check("bridge packet counter advanced", delta >= sent, {"sent": sent, "delta": delta, "status": after})
        _check("bridge udp send has no error", not str(after.get("lastError") or ""), after)
        print(
            "scene_face_tracking_offline probe passed "
            f"packets={sent} udp={args.udp_host}:{args.udp_port} bridge={bridge_base}"
        )
        print(
            "If Unity Play Mode is running with the scene tracker enabled, "
            "Editor.log should contain 'Scene face tracking drive' lines with changing yaw/pitch/depth."
        )
        return 0
    finally:
        _stop_bridge(proc)


def _build_packet_sweep(args: argparse.Namespace) -> list[dict[str, Any]]:
    samples = max(4, int(args.samples))
    packets: list[dict[str, Any]] = [_packet(0.0, 0.0, 0.0, 0.0, args)]
    for index in range(samples):
        phase = (index / max(1, samples - 1)) * math.tau
        center_x = math.sin(phase) * float(args.center_amplitude)
        center_y = math.sin(phase * 0.75 + math.pi / 5.0) * float(args.center_amplitude) * 0.65
        yaw = math.sin(phase) * float(args.yaw_deg)
        pitch = math.sin(phase * 0.75 + math.pi / 5.0) * float(args.pitch_deg)
        depth = math.sin(phase * 1.25 + math.pi / 3.0) * float(args.depth_amplitude)
        packets.append(_packet(center_x, center_y, yaw, pitch, args, depth))
    packets.append(_packet(0.0, 0.0, 0.0, 0.0, args))
    return packets


def _packet(
    center_x: float,
    center_y: float,
    yaw: float,
    pitch: float,
    args: argparse.Namespace,
    depth: float = 0.0,
) -> dict[str, Any]:
    face_width = 170.0 * (1.0 + max(-0.6, min(0.8, depth)) * 0.45)
    z_cm = 60.0 * 170.0 / max(1.0, face_width)
    return {
        "face_found": True,
        "face_center_x": _clamp(center_x, -1.0, 1.0),
        "face_center_y": _clamp(center_y, -1.0, 1.0),
        "face_width_px": round(face_width, 3),
        "yaw": _clamp(yaw, -90.0, 90.0),
        "pitch": _clamp(pitch, -60.0, 60.0),
        "roll": 0.0,
        "z_cm": round(z_cm, 3),
        "z_offset": _clamp(depth, -1.0, 1.0),
        "timestamp": time.time(),
    }


def _start_bridge(args: argparse.Namespace) -> subprocess.Popen[str]:
    raw_log = Path(args.raw_log)
    raw_log.parent.mkdir(parents=True, exist_ok=True)
    script = Path(args.bridge_script)
    config = Path(args.config)
    if not script.exists():
        raise FileNotFoundError(f"bridge script not found: {script}")
    if not config.exists():
        raise FileNotFoundError(f"config not found: {config}")

    env = dict(os.environ)
    env["TRANSPARENT_PET_FACE_TRACKING_HOST"] = str(args.udp_host)
    env["TRANSPARENT_PET_FACE_TRACKING_PORT"] = str(args.udp_port)
    return subprocess.Popen(
        [
            sys.executable,
            str(script),
            "--config",
            str(config),
            "--host",
            str(args.bridge_host),
            "--port",
            str(args.bridge_port),
            "--godot-port",
            "0",
            "--presentation-backend",
            "unity",
            "--raw-log",
            str(raw_log),
        ],
        cwd=str(ROOT),
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=env,
    )


def _stop_bridge(proc: subprocess.Popen[str]) -> None:
    if proc.poll() is not None:
        _print_bridge_tail(proc)
        return
    proc.terminate()
    try:
        proc.wait(timeout=3.0)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait(timeout=3.0)
    _print_bridge_tail(proc)


def _print_bridge_tail(proc: subprocess.Popen[str]) -> None:
    if proc.stdout is None:
        return
    try:
        output = proc.stdout.read()
    except Exception:
        output = ""
    lines = [line for line in output.splitlines() if line.strip()]
    if lines:
        print("bridge log tail:")
        for line in lines[-12:]:
            print(line)


def _wait_health(base_url: str, timeout_sec: float = 8.0) -> None:
    deadline = time.time() + timeout_sec
    last_error = ""
    while time.time() < deadline:
        try:
            result = _get_json(base_url + "/api/health", timeout=0.5)
            if result.get("ok") is True:
                return
        except Exception as exc:
            last_error = str(exc)
        time.sleep(0.1)
    raise RuntimeError(f"bridge health timeout: {last_error}")


def _get_json(url: str, timeout: float = 2.0) -> dict[str, Any]:
    with urllib.request.urlopen(url, timeout=timeout) as response:
        data = response.read()
    result = json.loads(data.decode("utf-8"))
    if not isinstance(result, dict):
        raise ValueError(f"GET {url} returned non-object JSON")
    return result


def _post_json(url: str, payload: dict[str, Any], timeout: float = 2.0) -> dict[str, Any]:
    request = urllib.request.Request(
        url,
        data=json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8"),
        headers={"Content-Type": "application/json; charset=utf-8"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            data = response.read()
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"POST {url} failed: {exc.code} {body}") from exc
    result = json.loads(data.decode("utf-8"))
    if not isinstance(result, dict):
        raise ValueError(f"POST {url} returned non-object JSON")
    return result


def _check(label: str, ok: bool, detail: Any) -> None:
    if ok:
        print(f"PASS {label}")
        return
    raise AssertionError(f"FAIL {label}: {json.dumps(detail, ensure_ascii=False, default=str)}")


def _clamp(value: float, minimum: float, maximum: float) -> float:
    return max(minimum, min(maximum, float(value)))


if __name__ == "__main__":
    raise SystemExit(main())
