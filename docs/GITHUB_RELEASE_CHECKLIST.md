# GitHub Release Checklist

Before pushing publicly:

- Read `AGENTS.md` if an automated agent is preparing the repository or release.
- Confirm Git LFS is enabled on the remote repository.
- Confirm scene, texture, shader, animation, and MediaPipe model redistribution rights.
- Confirm the Silver Wolf character model is not present in public source, public release assets, or Git history.
- Confirm public builds use the placeholder/user-model route, not the private/internal character model.
- Run `python scripts/check_secret_hygiene.py`.
- Run `python scripts/check_product_preflight.py`.
- Run `python scripts/check_face_tracking_preflight.py --skip-imports`.
- Run `python scripts/check_code_mojibake.py --include-docs`.
- Keep `unity/SilverWolfPet/Builds/` out of Git.
- Upload packaged Windows builds as GitHub Release assets instead of committing them.

Recommended release package command:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/prepare_release_package.ps1 `
  -ProjectPath unity/SilverWolfPet `
  -Version 20260509 `
  -Zip
```

The release package should contain:

- `desktop/TransparentWindowPet`
- `scene/ScenePet`

Do not include `.local.json`, `.env`, token files, Unity `Library`, or runtime logs.

## Public Vs Internal Assets

Public GitHub/formal release:

- May include cleared scene, texture, shader, animation, scripts, configs, and MediaPipe tracker assets.
- Must not include `silver_wolf_lv999` character model files or their `.meta` files.
- Should keep model defaults pointed at `user_pet_model` paths or the generated Unity placeholder.

Private/internal test package:

- May include the Silver Wolf character model only when the maintainer intentionally packages it for user testing.
- Must not be used as the source for a public GitHub push.

## Agent Reminder

If you are an agent helping a non-technical user, do not turn this checklist into manual busywork for them. Run the checks you can run, report skipped checks clearly, and keep build artifacts in GitHub Releases instead of source control.

Do not interpret "model missing" as a bug in the public repository. The public source is supposed to be replaceable by the user's own model.
