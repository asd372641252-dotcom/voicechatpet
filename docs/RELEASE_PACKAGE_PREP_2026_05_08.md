# Release Package Prep

The product package contains both Unity variants while keeping their runtime files separate:

```text
SilverWolfPet_<version>/
|-- Start-DesktopPet.ps1
|-- Start-ScenePet.ps1
|-- README_PACKAGE.txt
|-- PACKAGE_MANIFEST.json
|-- desktop/
|   `-- TransparentWindowPet/
|       |-- TransparentWindowPet.exe
|       `-- TransparentWindowPet_Data/
|-- scene/
|   `-- ScenePet/
|       |-- ScenePet.exe
|       `-- ScenePet_Data/
|-- config_templates/
`-- docs/
```

The two Unity builds must not be merged into one executable directory. UnityPlayer files, MonoBleedingEdge folders, and `*_Data` folders stay under their own variant directory.

## Variants

| Variant | Builder | Output before packaging | Role |
| --- | --- | --- | --- |
| Desktop | `TransparentPetSceneBuilder.BuildWindows` | `unity/SilverWolfPet/Builds/TransparentWindowPet` | Transparent desktop pet |
| Scene | `TransparentPetSceneBuilder.BuildSceneHostWindows` | `unity/SilverWolfPet/Builds/ScenePet` | Room/scene host mode |

## Commands

Preview the packaging plan:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/prepare_release_package.ps1 `
  -ProjectPath unity/SilverWolfPet `
  -Version 20260509 `
  -PlanOnly
```

Build both Unity variants:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build_unity_dual_product.ps1 `
  -ProjectPath unity/SilverWolfPet
```

Prepare one package after builds exist:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/prepare_release_package.ps1 `
  -ProjectPath unity/SilverWolfPet `
  -Version 20260509
```

Create a zip as well:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/prepare_release_package.ps1 `
  -ProjectPath unity/SilverWolfPet `
  -Version 20260509 `
  -Zip
```

If Unity is already open on the project, close it before batch build. Use `-AllowWhenEditorOpen` only for deliberate local testing.

## Exclusions

Do not package or commit these paths:

```text
**/Library/
**/Temp/
**/Logs/
**/UserSettings/
**/config/*.local.json
**/*.pdb
**/*_BurstDebugInformation_DoNotShip/
RTC_Token/
SDK/
akskdemo/
head_tracker/.venv/
```

Build outputs should be uploaded as GitHub Release assets, not committed to the source repository.
