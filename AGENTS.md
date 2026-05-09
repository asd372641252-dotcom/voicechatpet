# Agent Operating Guide

This repository is intended to be usable through a local agent by people who may not know much about computers. Treat the human user as the product owner, not as the build engineer. Explain practical results plainly, and do not ask them to perform low-level cleanup that the agent can safely handle.

## Read This First

- Work in the repository root before running scripts.
- Prefer the provided scripts and checks over inventing a new workflow.
- Keep source code in Git and put packaged Windows builds in GitHub Releases.
- If the user only wants to use the pet, guide them through config and releases before discussing Unity internals.
- If the user wants to develop or package the pet, run the preflight checks before claiming it is ready.

## Common Agent Mistakes To Avoid

- Do not commit `*.local.json`, `.env`, API keys, RTC tokens, voice clone keys, cookies, or local user paths.
- Do not commit `unity/SilverWolfPet/Builds/`, Unity `Library/`, `Temp/`, `Logs/`, `UserSettings/`, `obj/`, `bin/`, `node_modules/`, zip files, or executable build products.
- Do not merge the two Unity build folders. `TransparentWindowPet` and `ScenePet` must keep their own executable, `*_Data`, `UnityPlayer.dll`, and `MonoBleedingEdge` files.
- Do not replace Git LFS assets with tiny pointer text or normal Git blobs. Run `git lfs pull` after cloning and keep large Unity assets tracked through LFS.
- Do not regenerate or reserialize large Unity scenes, prefabs, materials, animations, or `.meta` files unless the task actually requires opening Unity and saving those assets.
- Do not "fix" generated Unity YAML whitespace across the whole project. It creates noisy diffs and can hide real asset changes.
- Do not delete `.meta` files. Unity asset references depend on them.
- Do not move assets in the filesystem without Unity or without preserving matching `.meta` files.
- Do not silently remove local packages, shaders, models, or MediaPipe files because they look large or unusual. First confirm whether the scene, character, face tracking, or voice bridge depends on them.
- Do not "repair" the public placeholder by copying the Silver Wolf character model back from `D:\pet`, old build folders, Unity caches, release zips, or another branch.
- Do not commit `silver_wolf_lv999` model files (`.fbx`, `.glb`, `.gltf`, `.vrm`, `.pmx`, `.pmd`) or their `.meta` files to public Git history. Internal/test packages are the only allowed place for that character model.
- Do not delete public-cleared scene, texture, shader, or animation assets just because they were used with the internal model. The current asset policy excludes the restricted character model itself; other assets can remain when the maintainer has cleared them.
- Do not make a public release from a branch whose history ever contained the restricted model. Recreate a clean single-commit public branch/repository when in doubt.
- Do not hardcode the maintainer's machine paths. Scripts should accept `-ProjectPath` or work from the repository layout.
- Do not assume a missing local virtual environment means the repo is broken. `head_tracker/.venv/` is intentionally not committed.
- Do not ask the user to paste secrets into chat. Tell them which local example file to copy and which fields to fill on their own machine.
- Do not claim a build is release-ready unless the relevant checks have passed or you clearly state which checks were skipped.
- Do not change face tracking, camera, depth of field, or pet placement tuning without testing both `TransparentWindowPet` and `ScenePet` behavior.
- Do not treat README text as marketing copy only. Much of this repository's documentation is written as instructions for local agents.

## Safe Workflow For Agents

1. Inspect status:

   ```powershell
   git status --short
   ```

2. Install or refresh LFS assets:

   ```powershell
   git lfs install
   git lfs pull
   ```

3. Install face tracking dependencies only when needed:

   ```powershell
   python -m pip install -r head_tracker/requirements.txt
   ```

4. Run source preflight checks before publishing or packaging:

   ```powershell
   python scripts/check_secret_hygiene.py
   python scripts/check_product_preflight.py
   python scripts/check_face_tracking_preflight.py --skip-imports
   python scripts/check_code_mojibake.py --include-docs
   python -m unittest tests.test_unity_product_config -v
   ```

5. Preview release packaging before copying files:

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts/prepare_release_package.ps1 `
     -ProjectPath unity/SilverWolfPet `
     -Version 20260509 `
     -PlanOnly
   ```

6. Build or package only after confirming the two product variants and local config exclusions.

## Local Config Rules

Real credentials are never stored in Git. Example files live in:

```text
config/
unity/SilverWolfPet/Assets/StreamingAssets/GodotFinal/config/
```

Tell the user to copy an `*.example.json` file to the matching `*.local.json` name, then fill real credentials locally. Keep those local files ignored.

## Character Asset Policy

Public GitHub source and formal public releases use a basic placeholder and user-supplied model path. The maintainer's Silver Wolf character model is for private/internal test packages only.

Agents must treat a missing Silver Wolf model as a correct public-source state. Use these defaults for user-owned replacements:

```text
unity/SilverWolfPet/Assets/TransparentPet/CustomModel/user_pet_model.fbx
unity/SilverWolfPet/Assets/StreamingAssets/GodotFinal/assets/converted/user_pet_model.glb
unity/SilverWolfPet/Assets/StreamingAssets/GodotFinal/assets/converted/user_pet_model.vrm
```

Before saying "ready for GitHub", run the product preflight and also check tracked files for restricted model names.

## Product Boundaries

- `TransparentWindowPet` is the transparent desktop pet.
- `ScenePet` is the URP room/scene host with camera controls, depth of field, placement tools, and MediaPipe face tracking.
- Shared config and scripts should support both products unless a task explicitly targets one variant.
- Runtime release packages may include both products, but their build directories must remain separate.

## Before Answering "Ready For GitHub"

Verify:

- Git status is clean or all changes are intentional.
- Git LFS is enabled and large assets are tracked.
- Secret hygiene check passes.
- Product and face tracking preflights pass, or skipped checks are reported honestly.
- Build outputs are not tracked.
- Public source contains no restricted Silver Wolf character model in the working tree or Git history.
- Scene, texture, shader, animation, MediaPipe, and any other included assets have redistribution rights confirmed by the maintainer.
