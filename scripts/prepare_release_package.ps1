param(
    [string]$UnityExe = "D:\Unity\Editor\Unity.exe",
    [string]$ProjectPath = "",
    [string]$OutputRoot = "",
    [string]$Version = "",
    [switch]$Build,
    [switch]$AllowWhenEditorOpen,
    [switch]$AllowMissingBuilds,
    [switch]$Clean,
    [switch]$Zip,
    [switch]$PlanOnly
)

$ErrorActionPreference = "Stop"

$WorkspaceRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $defaultProject = Join-Path $WorkspaceRoot "unity\SilverWolfPet"
    if (Test-Path -LiteralPath $defaultProject) {
        $project = Get-Item -LiteralPath $defaultProject
    } else {
        $project = Get-ChildItem -LiteralPath $WorkspaceRoot -Directory |
            Where-Object { $_.Name -like "*URP*20260503" } |
            Select-Object -First 1
    }
    if (-not $project) {
        throw "Unity project not found. Pass -ProjectPath or place it at unity\SilverWolfPet."
    }

    $ProjectPath = $project.FullName
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $WorkspaceRoot "builds\release"
} elseif (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path $WorkspaceRoot $OutputRoot
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-Date -Format "yyyyMMdd_HHmm"
}

$resolvedProject = (Resolve-Path -LiteralPath $ProjectPath).Path
$resolvedOutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)

$packageName = "SilverWolfPet_$Version"
$packageRoot = Join-Path $resolvedOutputRoot $packageName
$desktopBuild = Join-Path $resolvedProject "Builds\TransparentWindowPet"
$sceneBuild = Join-Path $resolvedProject "Builds\ScenePet"
$desktopExe = Join-Path $desktopBuild "TransparentWindowPet.exe"
$sceneExe = Join-Path $sceneBuild "ScenePet.exe"

if ($PlanOnly) {
    $plan = [ordered]@{
        packageName = $packageName
        packageRoot = $packageRoot
        buildRequested = [bool]$Build
        zipRequested = [bool]$Zip
        cleanRequested = [bool]$Clean
        unityProject = $resolvedProject
        outputRootExists = Test-Path -LiteralPath $resolvedOutputRoot
        packageRootExists = Test-Path -LiteralPath $packageRoot
        desktop = [ordered]@{
            source = $desktopBuild
            sourceExists = Test-Path -LiteralPath $desktopBuild
            executable = $desktopExe
            executableExists = Test-Path -LiteralPath $desktopExe
            destination = "desktop/TransparentWindowPet"
        }
        scene = [ordered]@{
            source = $sceneBuild
            sourceExists = Test-Path -LiteralPath $sceneBuild
            executable = $sceneExe
            executableExists = Test-Path -LiteralPath $sceneExe
            destination = "scene/ScenePet"
        }
        excludes = @(
            "**/Library",
            "**/Temp",
            "**/Logs",
            "**/UserSettings",
            "**/config/*.local.json",
            "**/*.pdb",
            "**/*_BurstDebugInformation_DoNotShip",
            "RTC_Token",
            "SDK",
            "akskdemo",
            "<voice-pack-dir>",
            "<motion-source-dir>"
        )
    }
    $plan | ConvertTo-Json -Depth 8
    return
}

New-Item -ItemType Directory -Force -Path $resolvedOutputRoot | Out-Null
if (-not (Test-Path -LiteralPath $resolvedOutputRoot)) {
    throw "Output root could not be created: $resolvedOutputRoot"
}

if (Test-Path -LiteralPath $packageRoot) {
    if (-not $Clean) {
        throw "Package directory already exists. Pass -Clean to recreate it: $packageRoot"
    }

    $resolvedPackageRoot = (Resolve-Path -LiteralPath $packageRoot).Path
    if (-not $resolvedPackageRoot.StartsWith($resolvedOutputRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove package outside output root: $resolvedPackageRoot"
    }

    Remove-Item -LiteralPath $resolvedPackageRoot -Recurse -Force
}

if ($Build) {
    $buildScript = Join-Path $PSScriptRoot "build_unity_dual_product.ps1"
    $buildArgs = @{
        UnityExe = $UnityExe
        ProjectPath = $resolvedProject
        LogDir = (Join-Path $WorkspaceRoot "logs")
    }
    if ($AllowWhenEditorOpen) {
        $buildArgs["AllowWhenEditorOpen"] = $true
    }
    & $buildScript @buildArgs
}

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
$desktopPackage = Join-Path $packageRoot "desktop\TransparentWindowPet"
$scenePackage = Join-Path $packageRoot "scene\ScenePet"
$docsPackage = Join-Path $packageRoot "docs"
$configPackage = Join-Path $packageRoot "config_templates"
New-Item -ItemType Directory -Force -Path $desktopPackage,$scenePackage,$docsPackage,$configPackage | Out-Null

function Copy-DirectoryContents {
    param(
        [string]$Source,
        [string]$Destination,
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        if ($AllowMissingBuilds) {
            Write-Warning "$Label build directory missing: $Source"
            return $false
        }
        throw "$Label build directory missing: $Source"
    }

    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-ReleaseItem -SourceRoot $Source -SourcePath $_.FullName -DestinationRoot $Destination
    }
    return $true
}

function Test-ReleasePathExcluded {
    param(
        [string]$RelativePath,
        [bool]$IsDirectory
    )

    $normalized = $RelativePath.Replace("\", "/").TrimStart("/")
    $name = [System.IO.Path]::GetFileName($normalized)
    $lower = $normalized.ToLowerInvariant()
    $lowerName = $name.ToLowerInvariant()

    if ($IsDirectory) {
        if ($lowerName -in @("library", "temp", "logs", "usersettings", "rtc_token", "sdk", "akskdemo", "__pycache__")) {
            return $true
        }

        if ($lowerName.EndsWith("_burstdebuginformation_donotship")) {
            return $true
        }

        if ($lowerName.EndsWith(".exe.webview2") -or
            $lowerName -eq "ebwebview" -or
            $lowerName -eq "browsermetrics") {
            return $true
        }

        if ($lower -like "*/head_tracker/.venv" -or $lower -like "head_tracker/.venv") {
            return $true
        }
    }

    if (-not $IsDirectory) {
        if ($lowerName.EndsWith(".local.json") -or
            $lowerName.EndsWith(".local.json.meta") -or
            $lowerName -eq ".env" -or
            $lowerName.EndsWith(".env") -or
            $lowerName.EndsWith(".env.meta")) {
            return $true
        }

        if ($lowerName.EndsWith(".pdb") -or
            $lowerName.EndsWith(".mdb") -or
            $lowerName.EndsWith(".ilk")) {
            return $true
        }
    }

    return $false
}

function Copy-ReleaseItem {
    param(
        [string]$SourceRoot,
        [string]$SourcePath,
        [string]$DestinationRoot
    )

    $item = Get-Item -LiteralPath $SourcePath -Force
    $relative = Get-PackageRelativePath -SourceRoot $SourceRoot -SourcePath $item.FullName
    if (Test-ReleasePathExcluded -RelativePath $relative -IsDirectory $item.PSIsContainer) {
        Write-Verbose "Excluded from package: $relative"
        return
    }

    $destination = Join-Path $DestinationRoot $relative
    if ($item.PSIsContainer) {
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        Get-ChildItem -LiteralPath $item.FullName -Force | ForEach-Object {
            Copy-ReleaseItem -SourceRoot $SourceRoot -SourcePath $_.FullName -DestinationRoot $DestinationRoot
        }
        return
    }

    $destinationDir = Split-Path -Parent $destination
    if ($destinationDir) {
        New-Item -ItemType Directory -Force -Path $destinationDir | Out-Null
    }
    Copy-Item -LiteralPath $item.FullName -Destination $destination -Force
}

function Get-PackageRelativePath {
    param(
        [string]$SourceRoot,
        [string]$SourcePath
    )

    $root = (Resolve-Path -LiteralPath $SourceRoot).Path.TrimEnd("\", "/") + [System.IO.Path]::DirectorySeparatorChar
    $path = (Resolve-Path -LiteralPath $SourcePath).Path
    $rootUri = New-Object System.Uri($root)
    $pathUri = New-Object System.Uri($path)
    return [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString()).Replace("/", [System.IO.Path]::DirectorySeparatorChar)
}

$desktopIncluded = Copy-DirectoryContents -Source $desktopBuild -Destination $desktopPackage -Label "desktop"
$sceneIncluded = Copy-DirectoryContents -Source $sceneBuild -Destination $scenePackage -Label "scene"

$docCandidates = @(
    "docs\RELEASE_PACKAGE_PREP_2026_05_08.md",
    "docs\WORKTREE_CURRENT_2026_05_07.md",
    "docs\PRODUCT_PROGRESS_2026_05_04_UNITY.md",
    "docs\USER_SETUP_NOTICE.md",
    "README.md",
    "TEST_CHECKLIST.md",
    "TROUBLESHOOTING.md"
)
foreach ($rel in $docCandidates) {
    $path = Join-Path $WorkspaceRoot $rel
    if (Test-Path -LiteralPath $path) {
        Copy-Item -LiteralPath $path -Destination $docsPackage -Force
    }
}

$sceneRuntimeConfig = Join-Path $resolvedProject "Assets\StreamingAssets\GodotFinal\config"
$configCandidates = @()
if (Test-Path -LiteralPath $sceneRuntimeConfig) {
    $configCandidates += Get-ChildItem -LiteralPath $sceneRuntimeConfig -File -Filter "*.example.json"
    foreach ($name in @("voice_routes.json", "presentation_routes.json")) {
        $path = Join-Path $sceneRuntimeConfig $name
        if (Test-Path -LiteralPath $path) {
            $configCandidates += Get-Item -LiteralPath $path
        }
    }
}
foreach ($file in ($configCandidates | Sort-Object FullName -Unique)) {
    Copy-Item -LiteralPath $file.FullName -Destination $configPackage -Force
}

$readme = @"
SilverWolfPet package

This package contains two isolated Windows builds:

1. desktop\TransparentWindowPet
   Transparent desktop pet mode.

2. scene\ScenePet
   Room/scene host mode.

Run Start-DesktopPet.ps1 or Start-ScenePet.ps1 from this directory.

Local credentials and *.local.json files are not bundled. Use config_templates
and configure credentials on the target machine before enabling voice features.
"@
Set-Content -LiteralPath (Join-Path $packageRoot "README_PACKAGE.txt") -Value $readme -Encoding UTF8

$startDesktop = @'
$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "desktop\TransparentWindowPet\TransparentWindowPet.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "Desktop executable not found: $exe" }
Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe)
'@
Set-Content -LiteralPath (Join-Path $packageRoot "Start-DesktopPet.ps1") -Value $startDesktop -Encoding UTF8

$startScene = @'
$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "scene\ScenePet\ScenePet.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "Scene executable not found: $exe" }
Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe)
'@
Set-Content -LiteralPath (Join-Path $packageRoot "Start-ScenePet.ps1") -Value $startScene -Encoding UTF8

$manifest = [ordered]@{
    package = "SilverWolfPet"
    version = $Version
    createdAt = (Get-Date).ToString("s")
    layoutVersion = 1
    unityProject = $resolvedProject
    localConfigsBundled = $false
    variants = @(
        [ordered]@{
            id = "desktop"
            displayName = "Transparent Desktop Pet"
            packagePath = "desktop/TransparentWindowPet"
            executable = "desktop/TransparentWindowPet/TransparentWindowPet.exe"
            sourceBuildPath = $desktopBuild
            included = [bool]$desktopIncluded
        },
        [ordered]@{
            id = "scene"
            displayName = "Scene Host Pet"
            packagePath = "scene/ScenePet"
            executable = "scene/ScenePet/ScenePet.exe"
            sourceBuildPath = $sceneBuild
            included = [bool]$sceneIncluded
        }
    )
    notes = @(
        "Desktop and scene builds are intentionally isolated in separate folders.",
        "Do not merge Unity *_Data folders between variants.",
        "Voice local credential configs are excluded; use config_templates as references."
    )
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $packageRoot "PACKAGE_MANIFEST.json") -Encoding UTF8

if ($Zip) {
    $zipPath = Join-Path $resolvedOutputRoot ($packageName + ".zip")
    if (Test-Path -LiteralPath $zipPath) {
        if (-not $Clean) {
            throw "Zip already exists. Pass -Clean to recreate it: $zipPath"
        }
        Remove-Item -LiteralPath $zipPath -Force
    }
    Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -Force
}

Write-Host "Release package prepared: $packageRoot"
if ($Zip) {
    Write-Host "Release zip prepared: $zipPath"
}
