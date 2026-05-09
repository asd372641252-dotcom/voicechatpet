param(
    [string]$UnityExe = "D:\Unity\Editor\Unity.exe",
    [string]$ProjectPath = "",
    [string]$LogDir = "",
    [switch]$AllowWhenEditorOpen
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

if ([string]::IsNullOrWhiteSpace($LogDir)) {
    $LogDir = Join-Path $WorkspaceRoot "logs"
}

if (-not (Test-Path -LiteralPath $UnityExe)) {
    throw "Unity executable not found: $UnityExe"
}

if (-not (Test-Path -LiteralPath $ProjectPath)) {
    throw "Unity project not found: $ProjectPath"
}

$resolvedProject = (Resolve-Path -LiteralPath $ProjectPath).Path
$normalizedProject = $resolvedProject.Replace("/", "\").ToLowerInvariant()
$openEditor = Get-CimInstance Win32_Process |
    Where-Object {
        $_.Name -eq "Unity.exe" -and
        $_.CommandLine -and
        $_.CommandLine.Replace("/", "\").ToLowerInvariant().Contains($normalizedProject)
    } |
    Select-Object -First 1

if ($openEditor -and -not $AllowWhenEditorOpen) {
    Write-Warning "Unity project is already open by PID $($openEditor.ProcessId). Close the editor, then rerun this build."
    exit 2
}

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

function Invoke-UnityBuildMethod {
    param(
        [string]$Method,
        [string]$LogName
    )

    $logPath = Join-Path $LogDir $LogName
    Write-Host "Running Unity build method: $Method"
    $unityArgs = @(
        "-batchmode",
        "-quit",
        "-projectPath", $resolvedProject,
        "-executeMethod", $Method,
        "-logFile", $logPath
    )
    $process = Start-Process -FilePath $UnityExe -ArgumentList $unityArgs -Wait -PassThru -WindowStyle Hidden

    if ($process.ExitCode -ne 0) {
        throw "Unity build failed: $Method. See log: $logPath"
    }
}

Invoke-UnityBuildMethod `
    -Method "TransparentPetSceneBuilder.BuildWindows" `
    -LogName "unity_desktop_pet_build.log"

Invoke-UnityBuildMethod `
    -Method "TransparentPetSceneBuilder.BuildSceneHostWindows" `
    -LogName "unity_scene_pet_build.log"

Write-Host "Dual Unity product build finished."
