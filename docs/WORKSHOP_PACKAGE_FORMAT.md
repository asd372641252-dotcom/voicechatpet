# Workshop Package Format

voicechatpet is designed so a non-technical user can subscribe to or download a package, open the in-app Mods menu, and click once to select it. Do not ask ordinary users to import FBX files into Unity.

Creator-source files such as `.fbx`, Blender files, PSDs, and authoring rigs belong in creator tooling. Runtime Workshop packages should contain files the built app can scan and load or remember directly.

## Package Layout

Each item is a folder with a `manifest.json` file:

```text
WorkshopItem/
  manifest.json
  preview.png
  model/character.assetbundle
  model/character.glb
  model/character.vrm
  scene/
  actions/
```

Use only the files needed for that item type. `preview.png` is optional.

## Minimal Model Manifest

```json
{
  "schema_version": 1,
  "type": "model",
  "name": "My Pet Model",
  "entry": "model/character.assetbundle",
  "asset": "assets/workshop/my_pet.prefab",
  "thumbnail": "preview.png",
  "format": "assetbundle",
  "requires": {
    "skeleton": "humanoid"
  }
}
```

Fields:

- `schema_version`: currently `1`.
- `type`: `model`, `scene`, or `action`.
- `name`: display name shown in the in-app Mods menu.
- `entry`: runtime file or folder path relative to the package root.
- `asset`: optional AssetBundle prefab asset name. If omitted, the first GameObject in the bundle is used.
- `thumbnail`: optional preview image.
- `format`: optional hint such as `assetbundle`, `glb`, or `vrm`.

## Runtime Support

The current first pass supports:

- Scanning `manifest.json` packages from `Application.persistentDataPath/Workshop`.
- Scanning bundled examples from `StreamingAssets/Workshop`.
- Remembering selected model, scene, and action package IDs in `PlayerPrefs`.
- Applying model packages whose `entry` is a Unity AssetBundle containing a prefab.
- Recognizing `.glb`, `.gltf`, and `.vrm` model packages as selectable packages for the future runtime importer.

The current first pass does not load `.fbx` at runtime. FBX is an authoring format for the future creator tool, which should convert creator assets into a runtime Workshop package before publishing.

## Steam Workshop

When Steam integration is enabled, subscribed items should be presented to the same scanner as folders containing `manifest.json`. A later Steamworks bridge can pass the resolved Workshop content directory into `TransparentPetWorkshopManager.extraWorkshopRoots` or set `steamAppId` once the app ID is known.

For Steam users, the intended flow is:

1. Subscribe to an item in Steam Workshop.
2. Open voicechatpet.
3. Open `创意工坊 / Mods`.
4. Click the package to apply or select it.

The user should not need to open Unity, run a conversion command, or manually edit config files for normal use.
