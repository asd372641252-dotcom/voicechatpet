from __future__ import annotations

import json
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlparse


ROOT = Path(__file__).resolve().parents[1]
UNITY_PROJECT = ROOT / "unity" / "SilverWolfPet"
SCENE_RUNTIME = UNITY_PROJECT / "Assets" / "StreamingAssets" / "GodotFinal"

LOCAL_CONFIGS = (
    "agent_speaker.local.json",
    "volc_start_voice_chat.local.json",
    "volc_traditional_companion_polling.local.json",
    "volc_traditional_voice_chat.local.json",
)

ROOT_DEBUG_PATTERNS = (
    "unity_",
    "desktop_",
    "secondary_",
    "urp_",
)


@dataclass(frozen=True)
class Message:
    path: str
    detail: str


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    failures: list[Message] = []
    warnings: list[Message] = []

    check_local_configs_are_ignored(failures)
    check_runtime_manifests(failures)
    check_expression_map(failures)
    check_example_config_parity(failures)
    check_public_character_asset_policy(failures)
    check_face_tracking_static_assets(failures, warnings)
    check_unity_product_sources(failures)
    check_visible_release_artifacts(failures)
    check_local_config_drift(warnings)

    for failure in failures:
        print(f"FAIL {failure.path}: {failure.detail}")
    for warning in warnings:
        print(f"WARN {warning.path}: {warning.detail}")

    if failures:
        print(f"\nProduct preflight failed: {len(failures)} failure(s), {len(warnings)} warning(s).")
        return 1

    print(f"Product preflight passed: 0 failure(s), {len(warnings)} warning(s).")
    return 0


def check_local_configs_are_ignored(failures: list[Message]) -> None:
    for rel in LOCAL_CONFIGS:
        for base in (ROOT / "config", SCENE_RUNTIME / "config"):
            path = base / rel
            if not path.exists():
                continue
            if not git_check_ignore(path):
                failures.append(Message(to_rel(path), "local secret-bearing config is not ignored by git"))


def check_runtime_manifests(failures: list[Message]) -> None:
    root_routes = ROOT / "config" / "voice_routes.json"
    scene_routes = SCENE_RUNTIME / "config" / "voice_routes.json"
    if not root_routes.exists() or not scene_routes.exists():
        failures.append(Message("config/voice_routes.json", "root or embedded voice route manifest is missing"))
        return
    if root_routes.read_bytes() != scene_routes.read_bytes():
        failures.append(Message("config/voice_routes.json", "root and embedded voice route manifests differ"))


def check_expression_map(failures: list[Message]) -> None:
    root_map = ROOT / "config" / "expression_map.json"
    scene_map = SCENE_RUNTIME / "config" / "expression_map.json"
    if not root_map.exists() or not scene_map.exists():
        failures.append(Message("config/expression_map.json", "root or embedded expression map is missing"))
        return

    try:
        root_data = load_json(root_map)
        scene_data = load_json(scene_map)
    except (OSError, json.JSONDecodeError) as exc:
        failures.append(Message("config/expression_map.json", f"expression map is not valid json: {exc}"))
        return

    if root_data != scene_data:
        failures.append(Message("config/expression_map.json", "root and embedded expression maps differ"))

    lip_sync = root_data.get("lip_sync") or {}
    if lip_sync.get("expression") != "mouth_open":
        failures.append(Message(to_rel(root_map), "lip_sync expression is not mouth_open"))

    expressions = root_data.get("expressions") or {}
    required_mouth = (
        "mouth_open",
        "mouth_small",
        "mouth_wide",
        "mouth_round",
        "mouth_closed",
        "mouth_smirk",
    )
    viseme_names: dict[str, set[str]] = {}
    for key in required_mouth:
        expression = expressions.get(key) or {}
        if expression.get("exclusive_group") != "mouth":
            failures.append(Message(to_rel(root_map), f"{key} is not in the mouth exclusive group"))
        if expression.get("reset_others") is not True:
            failures.append(Message(to_rel(root_map), f"{key} does not reset other mouth shapes"))

        names = expression_blend_shape_names(expression)
        if not names:
            failures.append(Message(to_rel(root_map), f"{key} has no blend shape aliases"))
        viseme_names[key] = names

    if viseme_names.get("mouth_open") == viseme_names.get("mouth_wide"):
        failures.append(Message(to_rel(root_map), "mouth_open and mouth_wide resolve to the same aliases"))
    if viseme_names.get("mouth_round") == viseme_names.get("mouth_small"):
        failures.append(Message(to_rel(root_map), "mouth_round and mouth_small resolve to the same aliases"))


def expression_blend_shape_names(expression: dict) -> set[str]:
    names: set[str] = set()
    blend_shapes = expression.get("blend_shapes")
    if not isinstance(blend_shapes, list):
        return names
    for item in blend_shapes:
        if not isinstance(item, dict):
            continue
        aliases = item.get("names")
        if not isinstance(aliases, list):
            continue
        for alias in aliases:
            text = str(alias or "").strip()
            if text:
                names.add(text)
    return names


def check_example_config_parity(failures: list[Message]) -> None:
    root_config = ROOT / "config"
    scene_config = SCENE_RUNTIME / "config"
    for root_example in sorted(root_config.glob("*.example.json")):
        scene_example = scene_config / root_example.name
        if not scene_example.exists():
            failures.append(Message(to_rel(scene_example), "embedded example config is missing"))
            continue
        if load_json(root_example) != load_json(scene_example):
            failures.append(Message(to_rel(root_example), "root and embedded example configs differ"))


def check_public_character_asset_policy(failures: list[Message]) -> None:
    model_suffixes = (".fbx", ".vrm", ".glb", ".gltf", ".pmx", ".pmd")
    restricted_markers = (
        "silver_wolf_lv999",
        "silver_wolf_lv999_unity_humanoid",
    )
    default_model_tokens = (
        "res://assets/converted/silver_wolf_lv999.glb",
        "Assets/TransparentPet/Models/silver_wolf_lv999_unity_humanoid.fbx",
    )

    for result_args, failure_detail in (
        (["ls-files", "-z"], "could not list tracked files for public character asset policy"),
        (["ls-files", "--others", "--exclude-standard", "-z"], "could not list untracked files for public character asset policy"),
    ):
        result = run_git(result_args)
        if result.returncode != 0:
            failures.append(Message("git", failure_detail))
            return

        for raw in result.stdout.split("\0"):
            if not raw:
                continue
            rel = raw.replace("\\", "/")
            lower = rel.lower()
            suffix = Path(lower).suffix
            if suffix in model_suffixes and any(marker in lower for marker in restricted_markers):
                failures.append(Message(rel, "restricted character model must not be visible to public git"))

    scan_paths = (
        ROOT / "config" / "pet_config.json",
        ROOT / "config" / "asset_pipeline.json",
        SCENE_RUNTIME / "config" / "pet_config.json",
        SCENE_RUNTIME / "config" / "asset_pipeline.json",
        SCENE_RUNTIME / "scripts" / "model" / "pet_model_loader.gd",
        SCENE_RUNTIME / "scripts" / "motion" / "kawaii_action_player.gd",
        SCENE_RUNTIME / "scripts" / "render" / "anime_render_preset_controller.gd",
        SCENE_RUNTIME / "scripts" / "debug" / "dump_blend_shapes.gd",
        UNITY_PROJECT / "Assets" / "TransparentPet" / "Editor" / "TransparentPetSceneBuilder.cs",
        UNITY_PROJECT / "Assets" / "TransparentPet" / "Scripts" / "TransparentPetPlacementController.cs",
    )
    for path in scan_paths:
        if not path.exists():
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        for token in default_model_tokens:
            if token in text:
                failures.append(Message(to_rel(path), "public source still defaults to the restricted character model"))

    history = run_git(["log", "--all", "--name-only", "--pretty=format:"])
    if history.returncode == 0:
        for raw in history.stdout.splitlines():
            rel = raw.strip().replace("\\", "/")
            lower = rel.lower()
            suffix = Path(lower).suffix
            if suffix in model_suffixes and any(marker in lower for marker in restricted_markers):
                failures.append(Message(rel, "restricted character model exists in git history; recreate the public repo/branch without that history"))
                break


def check_visible_release_artifacts(failures: list[Message]) -> None:
    result = run_git(["ls-files", "--others", "--exclude-standard", "-z"])
    if result.returncode != 0:
        failures.append(Message("git", "could not list untracked release candidates"))
        return

    for raw in result.stdout.split("\0"):
        if not raw:
            continue
        rel = raw.replace("\\", "/")
        name = Path(rel).name
        lower = name.lower()
        if lower.endswith((".exe", ".pdb")) and "/" not in rel:
            failures.append(Message(rel, "root-level generated binary is visible to git"))
        if lower.endswith(".png") and name.startswith(ROOT_DEBUG_PATTERNS) and "/" not in rel:
            failures.append(Message(rel, "root-level debug screenshot is visible to git"))
        if lower.endswith(".cs") and (name.startswith("click_") or name.startswith("capture_")) and "/" not in rel:
            failures.append(Message(rel, "root-level input/capture helper source is visible to git"))
        if "/Library/" in rel or "/Temp/" in rel or "/Logs/" in rel:
            failures.append(Message(rel, "Unity generated directory is visible to git"))


def check_face_tracking_static_assets(failures: list[Message], warnings: list[Message]) -> None:
    tracker_root = ROOT / "head_tracker"
    tracker_script = tracker_root / "head_tracker.py"
    tracker_model = tracker_root / "models" / "face_landmarker.task"
    tracker_python = tracker_root / ".venv" / "Scripts" / "python.exe"
    scene_path = UNITY_PROJECT / "Assets" / "Scenes" / "BlenderIndoorScene.unity"
    tracker_component = UNITY_PROJECT / "Assets" / "TransparentPet" / "Scripts" / "SceneHost" / "TransparentPetSceneFaceTracker.cs"
    voice_launcher = UNITY_PROJECT / "Assets" / "TransparentPet" / "Scripts" / "TransparentPetVoiceRuntimeLauncher.cs"

    for path, detail in (
        (tracker_script, "head tracker script is missing"),
        (tracker_model, "MediaPipe face landmarker model is missing"),
        (tracker_component, "Unity scene face tracker component source is missing"),
        (voice_launcher, "Unity voice runtime launcher source is missing"),
        (scene_path, "Unity product scene is missing"),
    ):
        if not path.exists():
            failures.append(Message(to_rel(path), detail))

    if not tracker_python.exists():
        warnings.append(Message(to_rel(tracker_python), "head tracker virtualenv python is missing; run head_tracker setup before live tracking"))

    if scene_path.exists():
        scene_text = scene_path.read_text(encoding="utf-8", errors="replace")
        required_scene_tokens = {
            "TransparentPetSceneFaceTracker": "scene face tracker is not serialized in product scene",
            "trackingBackend: 0": "scene face tracker is not set to ExternalMediaPipe",
            "trackingEnabled: 1": "scene face tracking is not enabled",
            "launchExternalProcess: 1": "scene face tracker will not launch the external process",
            "externalTrackerScript: head_tracker.py": "scene face tracker script path is not head_tracker.py",
            "externalTrackerPort: 5055": "scene face tracker UDP port is not 5055",
            "requestedWidth: 1280": "scene face tracker request width is not 1280",
            "requestedHeight: 720": "scene face tracker request height is not 720",
        }
        for token, detail in required_scene_tokens.items():
            if token not in scene_text:
                failures.append(Message(to_rel(scene_path), detail))

    if tracker_component.exists():
        tracker_text = tracker_component.read_text(encoding="utf-8", errors="replace")
        required_tracker_tokens = {
            "externalFrameServerEnabled = true": "frame server default is not enabled",
            "--frame-port": "external tracker command does not pass frame server port",
            "--frame-server-fps": "external tracker command does not pass frame server fps",
            "--frame-jpeg-quality": "external tracker command does not pass frame jpeg quality",
            "CameraHubStreamUrl": "scene tracker does not expose camera hub stream URL",
        }
        for token, detail in required_tracker_tokens.items():
            if token not in tracker_text:
                failures.append(Message(to_rel(tracker_component), detail))

    if voice_launcher.exists():
        launcher_text = voice_launcher.read_text(encoding="utf-8", errors="replace")
        required_launcher_tokens = {
            'cameraVideoUseCameraHub = true': "voice launcher does not default to camera hub video",
            'cameraVideoHubUrl = "http://127.0.0.1:17863/stream.mjpg"': "voice launcher camera hub URL is not the scene frame server",
            'cameraVideoWidth = 1280': "voice launcher camera video width is not 720p-class",
            'cameraVideoHeight = 720': "voice launcher camera video height is not 720p",
        }
        for token, detail in required_launcher_tokens.items():
            if token not in launcher_text:
                failures.append(Message(to_rel(voice_launcher), detail))


def check_unity_product_sources(failures: list[Message]) -> None:
    unity_sources = {
        UNITY_PROJECT / "Assets/TransparentPet/Scripts/TransparentPetPlacementController.cs": {
            "SaveUserPlacementNow": "placement controller cannot explicitly save user placement",
            "ClearSavedPlacement": "placement controller cannot clear user placement",
            "ResetToFactoryDefault": "placement controller cannot reset to factory placement",
            "HasSavedPlacement": "placement controller does not expose saved placement status",
        },
        UNITY_PROJECT / "Assets/TransparentPet/Scripts/TransparentPetFreeCamera.cs": {
            "SaveUserCameraNow": "free camera cannot explicitly save user camera state",
            "ClearSavedCamera": "free camera cannot clear user camera state",
            "ResetToFactoryDefault": "free camera cannot reset to factory camera state",
            "HasSavedCamera": "free camera does not expose saved camera status",
        },
        UNITY_PROJECT / "Assets/TransparentPet/Scripts/TransparentPetContextMenu.cs": {
            "SaveUserPlacementNow": "runtime menu does not expose user placement save",
            "ResetToFactoryDefault": "runtime menu does not expose factory placement reset",
        },
        UNITY_PROJECT / "Assets/TransparentPet/Editor/TransparentPetSceneBuilder.cs": {
            "UrpHostBuildPath": "scene product build path is not declared",
            "BuildSceneHostWindows": "scene product build menu/batch method is missing",
            "ConfigureSceneHostPlayerSettings": "scene product player settings are not centralized",
            "new Vector2(520f, 560f)": "context menu product size is below the product default",
        },
        UNITY_PROJECT / "Assets/TransparentPet/Editor/TransparentPetProductValidator.cs": {
            "ValidatePlacementPersistence": "validator does not check placement persistence",
            'StartsWith("ScenePet.", StringComparison.Ordinal)': "validator does not enforce scene PlayerPrefs namespace",
            "Editor play does not overwrite user camera saves": "validator does not protect user camera saves in editor play",
            "Editor play does not overwrite user placement saves": "validator does not protect user placement saves in editor play",
        },
    }

    for path, required_tokens in unity_sources.items():
        if not path.exists():
            failures.append(Message(to_rel(path), "required Unity product source is missing"))
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        for token, detail in required_tokens.items():
            if token not in text:
                failures.append(Message(to_rel(path), detail))

    build_script = ROOT / "scripts" / "build_unity_dual_product.ps1"
    if not build_script.exists():
        failures.append(Message(to_rel(build_script), "scene product build script is missing"))
        return
    build_text = build_script.read_text(encoding="utf-8", errors="replace")
    if "TransparentPetSceneBuilder.BuildSceneHostWindows" not in build_text:
        failures.append(Message(to_rel(build_script), "scene product build script does not call the scene host build method"))


def check_local_config_drift(warnings: list[Message]) -> None:
    for rel in LOCAL_CONFIGS:
        root_path = ROOT / "config" / rel
        scene_path = SCENE_RUNTIME / "config" / rel
        if not root_path.exists() or not scene_path.exists():
            continue
        root_summary = summarize_runtime_config(load_json(root_path))
        scene_summary = summarize_runtime_config(load_json(scene_path))
        changed = [
            key
            for key in sorted(set(root_summary) | set(scene_summary))
            if root_summary.get(key) != scene_summary.get(key)
        ]
        if changed:
            warnings.append(
                Message(
                    rel,
                    "root and scene local configs differ in non-secret fields: " + ", ".join(changed),
                )
            )


def summarize_runtime_config(data: dict) -> dict[str, object]:
    start = data.get("StartVoiceChat") or {}
    start_config = start.get("Config") or {}
    llm = start_config.get("LLMConfig") or {}
    asr = ((start_config.get("ASRConfig") or {}).get("InterruptConfig") or {})
    companion = data.get("CompanionVision") or {}
    watchdog = data.get("SpeechTurnWatchdog") or {}
    route = data.get("VoiceRoute") or {}
    agent = start.get("AgentConfig") or {}
    url = str(llm.get("Url") or "")
    host = urlparse(url).netloc.lower() if url else ""
    return {
        "route_id": route.get("id"),
        "task_id": start.get("TaskId"),
        "welcome": agent.get("WelcomeMessage"),
        "llm_mode": llm.get("Mode"),
        "llm_host": host,
        "llm_model": llm.get("ModelName"),
        "thinking": llm.get("ThinkingType"),
        "vision_enabled": (((llm.get("VisionConfig") or {}).get("Enable"))),
        "voice_interrupt_mode": start_config.get("InterruptMode"),
        "asr_interrupt_speech_duration": asr.get("InterruptSpeechDuration"),
        "asr_interrupt_silence_duration": asr.get("InterruptSilenceDuration"),
        "speech_watchdog_enabled": watchdog.get("Enabled"),
        "speech_watchdog_delay": watchdog.get("DelaySec"),
        "speech_watchdog_busy_grace": watchdog.get("BusyGraceSec"),
        "companion_enabled": companion.get("Enabled"),
        "companion_interval": companion.get("IntervalSec"),
        "companion_pending_timeout": companion.get("PendingTimeoutSec"),
        "companion_busy_timeout": companion.get("MaxBusyWithoutAudioSec"),
        "companion_user_silence": companion.get("UserSilenceSec"),
        "companion_failure_backoff": companion.get("FailureBackoffSec"),
        "companion_max_failure_backoff": companion.get("MaxFailureBackoffSec"),
        "companion_recent_context": companion.get("RecentContextCount"),
        "companion_recent_context_window": companion.get("RecentContextWindowSec"),
        "companion_interrupt": companion.get("InterruptMode"),
        "companion_prompt": companion.get("Prompt"),
    }


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def git_check_ignore(path: Path) -> bool:
    rel = to_rel(path)
    return run_git(["check-ignore", "-q", "--", rel]).returncode == 0


def run_git(args: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["git", *args],
        cwd=ROOT,
        text=True,
        encoding="utf-8",
        errors="replace",
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )


def to_rel(path: Path) -> str:
    try:
        return path.relative_to(ROOT).as_posix()
    except ValueError:
        return path.as_posix()


if __name__ == "__main__":
    raise SystemExit(main())
