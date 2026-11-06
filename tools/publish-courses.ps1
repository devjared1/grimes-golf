<#
.SYNOPSIS
  Makes the courses downloadable: zips them with a catalog, and with -Upload puts them on the "courses" GitHub release.

.DESCRIPTION
  Takes the courses that ship with the app and those in courses/ (from make-course.ps1), checks every one loads, and
  writes a zip per course plus catalog.json to dist/courses. Without -Upload that's all it does, so the result can be
  tried first with a local web server and the app's --catalog switch (see docs/development.md).

  -Upload adds to what's already published: it reads the live catalog, uploads only the courses that are new or have a
  higher version, and leaves the rest alone. A course that isn't on this PC stays on the list; a course at the same
  version as the live one isn't replaced (raise "version" in its course.json, or make it again). Zips nothing lists any
  more are deleted from the release once the new list is up. -Remove takes a course off the list.

  -Upload needs the GitHub CLI (gh), signed in. The app finds the list at releases/download/courses/catalog.json. The
  release is marked as a pre-release so that it never counts as the app's latest version.

.EXAMPLE
  ./tools/publish-courses.ps1
  ./tools/publish-courses.ps1 -Upload
  ./tools/publish-courses.ps1 -Upload -Remove old-course
#>
param(
    [string]$Out = "$PSScriptRoot/../dist/courses",
    [switch]$Upload,
    [string[]]$Remove = @()
)
$ErrorActionPreference = "Stop"

if ($Remove -and -not $Upload) { throw "-Remove changes the published list, so it needs -Upload." }
$root = (Resolve-Path "$PSScriptRoot/..").Path
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
dotnet run --project "$root/src/GrimesGolf.CourseTool" -v q -- publish $Out "$root/src/GrimesGolf.Godot/courses" "$root/courses"
if ($LASTEXITCODE -ne 0) { throw "A course isn't ready to publish (see above). Nothing was uploaded." }
Write-Host "Ready in $Out"
if (-not $Upload) { return }

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "-Upload needs the GitHub CLI: https://cli.github.com" }
$ErrorActionPreference = "Continue"
gh release view courses *> $null
$exists = $LASTEXITCODE -eq 0
$ErrorActionPreference = "Stop"
if (-not $exists) {
    gh release create courses --prerelease --title "Courses" --notes "The courses the app can download (Get More Courses). Map data (c) OpenStreetMap contributors, ODbL; elevation and trees from USGS 3DEP, public domain."
    if ($LASTEXITCODE -ne 0) { throw "Couldn't create the release." }
}

function ReleaseAssets {
    $names = gh release view courses --json assets --jq ".assets[].name"
    if ($LASTEXITCODE -ne 0) { throw "Couldn't read the courses release. Nothing was uploaded." }
    @($names)
}

# The live list decides what's kept, so if there is one and it can't be read, stop: guessing could drop courses.
$live = "none"
$temp = Join-Path ([System.IO.Path]::GetTempPath()) "grimes-live-catalog-$PID"
try {
    if ((ReleaseAssets) -contains "catalog.json") {
        New-Item -ItemType Directory -Force $temp | Out-Null
        gh release download courses --pattern catalog.json --dir $temp --clobber
        if ($LASTEXITCODE -ne 0) { throw "Couldn't read the live course list. Nothing was uploaded." }
        $live = Join-Path $temp "catalog.json"
    }
    dotnet run --project "$root/src/GrimesGolf.CourseTool" -v q -- merge $Out $live @Remove
    if ($LASTEXITCODE -ne 0) { throw "Nothing was uploaded." }
}
finally {
    if (Test-Path $temp) { Remove-Item -Recurse -Force $temp }
}

# The zips first: a new catalog must never list one that isn't up yet.
$zips = @(Get-ChildItem "$Out/*.zip")
if ($zips) {
    gh release upload courses $zips.FullName --clobber
    if ($LASTEXITCODE -ne 0) { throw "Couldn't upload the courses; the catalog wasn't changed." }
}
gh release upload courses "$Out/catalog.json" --clobber
if ($LASTEXITCODE -ne 0) { throw "The zips are up but the catalog isn't." }

# Only now is nothing pointing at the old zips.
$listed = (Get-Content -Raw "$Out/catalog.json" | ConvertFrom-Json).courses | ForEach-Object { $_.file }
foreach ($asset in (ReleaseAssets) | Where-Object { $_ -like "*.zip" -and $listed -notcontains $_ }) {
    gh release delete-asset courses $asset --yes
    if ($LASTEXITCODE -ne 0) { Write-Warning "Couldn't delete $asset from the release; it's no longer listed, so it's harmless." }
    else { Write-Host "Deleted $asset (no longer listed)." }
}
Write-Host "Published."
