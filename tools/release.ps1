<#
.SYNOPSIS
  Builds a release: runs the tests, exports the app, makes the installer and, with -Publish, puts it on GitHub.

.DESCRIPTION
  The version comes from application/config/version in src/GrimesGolf.Godot/project.godot: raise it there first. The
  result is dist/GrimesGolf-Setup-<version>.exe.

  -Publish tags the commit v<version> and creates the GitHub release with the installer attached, which is what the
  app's update check looks at. It refuses unless everything is committed and pushed, so that a release is always a
  commit that exists. It needs the GitHub CLI (gh), signed in. Needs Inno Setup 6: winget install JRSoftware.InnoSetup.

.EXAMPLE
  ./tools/release.ps1
  ./tools/release.ps1 -Publish
#>
param(
    [string]$Godot = "godot",
    [switch]$SkipTests,
    [switch]$Publish
)
$ErrorActionPreference = "Stop"

$root = (Resolve-Path "$PSScriptRoot/..").Path
$version = (Select-String -Path "$root/src/GrimesGolf.Godot/project.godot" -Pattern '^config/version="(.+)"').Matches[0].Groups[1].Value
$tag = "v$version"
$installer = "$root/dist/GrimesGolf-Setup-$version.exe"

if ($Publish) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "-Publish needs the GitHub CLI: https://cli.github.com" }
    if (git -C $root status --porcelain) { throw "There are uncommitted changes: a release has to be a commit. Commit them first." }
    git -C $root fetch origin --quiet
    if (git -C $root log "@{u}..HEAD" --oneline) { throw "This branch has commits that aren't pushed yet." }
    $ErrorActionPreference = "Continue"
    gh release view $tag *> $null
    $exists = $LASTEXITCODE -eq 0
    $ErrorActionPreference = "Stop"
    if ($exists) { throw "Release $tag already exists: raise the version in project.godot." }
}

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 isn't installed: winget install JRSoftware.InnoSetup" }

if (-not $SkipTests) {
    foreach ($tests in "GrimesGolf.Tests", "GrimesGolf.Physics.Tests", "GrimesGolf.Online.Tests") {
        dotnet test "$root/tests/$tests" -v q
        if ($LASTEXITCODE -ne 0) { throw "$tests failed: no release." }
    }
}

& "$PSScriptRoot/export.ps1" -Godot $Godot -NoShortcut
& $iscc /Q "/DAppVersion=$version" "$root/installer/GrimesGolf.iss"
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $installer)) { throw "Inno Setup failed (exit code $LASTEXITCODE)." }
Write-Host "Installer: $installer ($([math]::Round((Get-Item $installer).Length / 1MB)) MB)"
if (-not $Publish) { return }

$notes = "Download **GrimesGolf-Setup-$version.exe** and run it. It installs over any earlier version and keeps your rounds and settings. Windows may warn about an unrecognised app (the installer isn't code-signed): choose More info, then Run anyway."
gh release create $tag $installer --title "Grimes Golf $version" --notes $notes
if ($LASTEXITCODE -ne 0) { throw "Couldn't create the release." }
Write-Host "Published $tag."
