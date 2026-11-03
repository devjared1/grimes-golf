<#
.SYNOPSIS
  Builds the Windows release of Square Range 3D into dist/ and puts a shortcut to it on the desktop.

.DESCRIPTION
  Exports the "Windows Desktop" preset (src/SquareGspro.Godot/export_presets.cfg) with the Godot .NET editor,
  copies the physics tuning presets next to the exe (the app reads tuning/startup.json from there), and creates
  "Square Range 3D" on the desktop. Needs the Godot export templates for the same version as the editor, installed
  once from the editor: Editor > Manage Export Templates > Download and Install.

  Saved rounds, shots, settings.json and crash.log live in %APPDATA%\Square Range 3D, not in dist/, so re-exporting
  keeps them.

.EXAMPLE
  ./tools/export.ps1
  ./tools/export.ps1 -Godot "C:\Tools\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe" -NoShortcut
#>
param(
    [string]$Godot = "godot",                                  # the Godot .NET editor executable
    [string]$Out = "$PSScriptRoot/../dist/Square Range 3D",
    [switch]$NoShortcut
)
$ErrorActionPreference = "Stop"

$project = (Resolve-Path "$PSScriptRoot/../src/SquareGspro.Godot").Path
$version = (Select-String -Path "$project/project.godot" -Pattern '^config/version="(.+)"').Matches[0].Groups[1].Value

# Export templates are installed per editor version, e.g. 4.7.2.stable.mono.
$editorVersion = (& $Godot --version | Select-Object -Last 1).Trim()
$templateVersion = ($editorVersion -split '\.')[0..4] -join '.'
$templates = Join-Path $env:APPDATA "Godot/export_templates/$templateVersion"
if (-not (Test-Path "$templates/windows_release_x86_64.exe")) {
    throw "No Windows export templates for Godot $templateVersion in $templates. Install them from the editor: Editor > Manage Export Templates > Download and Install."
}

New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$exe = Join-Path $Out "Square Range 3D.exe"
Write-Host "Exporting Square Range 3D v$version to $Out"
& $Godot --headless --path $project --export-release "Windows Desktop" $exe
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) { throw "Godot export failed (exit code $LASTEXITCODE)." }

# Physics presets beside the exe (excluded from the pack so they stay editable). Presets already there may have been
# changed from the exported app's tuner, so they're kept.
New-Item -ItemType Directory -Force "$Out/tuning" | Out-Null
Get-ChildItem "$project/tuning/*.json" | Where-Object { -not (Test-Path (Join-Path "$Out/tuning" $_.Name)) } |
    Copy-Item -Destination "$Out/tuning"

if (-not $NoShortcut) {
    $link = Join-Path ([Environment]::GetFolderPath("Desktop")) "Square Range 3D.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($link)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $Out
    $shortcut.Description = "Square Range 3D v$version"
    $shortcut.Save()
    Write-Host "Shortcut: $link"
}
Write-Host "Done: $exe"
