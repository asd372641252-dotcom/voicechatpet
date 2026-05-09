# voicechatpet

Unity voice chat desktop pet prototype with two Windows builds:

- `TransparentWindowPet`: a transparent desktop pet window.
- `ScenePet`: the same pet integrated into a URP indoor scene, with free camera controls, depth of field, placement tools, and MediaPipe-based face tracking.

The current repository is prepared as a public source tree. Runtime builds should be attached to GitHub Releases instead of committed to Git.

This project is also documented for local agents. Many users are expected to operate it through their own desktop or coding agent rather than by manually editing Unity project files. Agents should read `AGENTS.md` before changing files, packaging builds, or guiding a non-technical user.

## Repository Layout

```text
unity/SilverWolfPet/    Unity 6 project
head_tracker/           Standalone MediaPipe tracker source and model
scripts/                Build, package, and preflight scripts
tests/                  Python preflight/unit checks
docs/                   Release and setup notes
AGENTS.md               Operating rules for local agents working on this repo
```

## Requirements

- Windows 10/11
- Unity 6 / 6000.0 compatible editor
- Git LFS
- Python 3.10+ for preflight checks and face tracking
- Optional: .NET 8 SDK for rebuilding the WebView voice runtime

After cloning:

```powershell
git lfs install
git lfs pull
python -m pip install -r head_tracker/requirements.txt
```

Open the Unity project at:

```text
unity/SilverWolfPet
```

## Build

Run both Windows products:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build_unity_dual_product.ps1 `
  -ProjectPath unity/SilverWolfPet
```

The Unity builder writes:

```text
unity/SilverWolfPet/Builds/TransparentWindowPet/TransparentWindowPet.exe
unity/SilverWolfPet/Builds/ScenePet/ScenePet.exe
```

Create a release package from existing builds:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/prepare_release_package.ps1 `
  -ProjectPath unity/SilverWolfPet `
  -Version 20260509
```

Use `-PlanOnly` first if you want to inspect paths without copying files.

## Local Configuration

No real credentials are committed. Files ending in `.local.json` are intentionally ignored.

Voice/RTC features use example configs under:

```text
unity/SilverWolfPet/Assets/StreamingAssets/GodotFinal/config/
```

Copy an example file to a matching `.local.json` name and fill your own keys locally. See `docs/USER_SETUP_NOTICE.md` for the longer setup notes.

## Character Model Replacement

The public source tree builds with a generated basic placeholder in Unity and a neutral user-model path for the embedded runtime:

```text
unity/SilverWolfPet/Assets/TransparentPet/CustomModel/user_pet_model.fbx
unity/SilverWolfPet/Assets/StreamingAssets/GodotFinal/assets/converted/user_pet_model.glb
unity/SilverWolfPet/Assets/StreamingAssets/GodotFinal/assets/converted/user_pet_model.vrm
```

Users can replace the placeholder with their own licensed character model. Keep real model licenses with the model owner; do not assume any bundled internal/test model is redistributable.

## Current Notes

- Face tracking defaults are tuned to reduce jitter and avoid snapping back too quickly.
- Scene camera depth of field is enabled by default in generated desktop and scene builds.
- Scene placement can keep the camera focus locked to the pet while the pet is moved.
- Build outputs, Unity caches, local configs, and runtime logs are excluded from Git.
- Agent-facing guardrails are in `AGENTS.md`; keep them current when release, config, or build behavior changes.

## Asset Notice

This public repo may include cleared scene, texture, shader, and animation assets. The Silver Wolf character model is not public source material: it may only be distributed in private/internal test builds when the maintainer intentionally prepares that package. Public GitHub source and formal public releases use the placeholder/user-model route above.

Agents preparing this repo for GitHub must not restore, commit, or preserve Git history containing `silver_wolf_lv999` character model files. If the public build shows a placeholder, that is expected.
