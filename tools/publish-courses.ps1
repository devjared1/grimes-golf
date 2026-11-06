<#
.SYNOPSIS
  Makes the courses downloadable: zips them with a catalog, and with -Upload puts them on the "courses" GitHub release.

.DESCRIPTION
  Takes the courses that ship with the app and those in courses/ (from make-course.ps1), checks every one loads, and
  writes a zip per course plus catalog.json to dist/courses. Without -Upload that's all it does, so the result can be
  tried first with a local web server and the app's --catalog switch (see docs/development.md).

  -Upload needs the GitHub CLI (gh), signed in. The app finds the list at releases/download/courses/catalog.json. The
  release is marked as a pre-release so that it never counts as the app's latest version.

.EXAMPLE
  ./tools/publish-courses.ps1
  ./tools/publish-courses.ps1 -Upload
#>
param(
    [string]$Out = "$PSScriptRoot/../dist/courses",
    [switch]$Upload
)
$ErrorActionPreference = "Stop"

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
# The zips first: a new catalog must never list one that isn't up yet. (A zip's name holds its version, so old ones stay until removed by hand.)
gh release upload courses (Get-ChildItem "$Out/*.zip").FullName --clobber
if ($LASTEXITCODE -ne 0) { throw "Couldn't upload the courses; the catalog wasn't changed." }
gh release upload courses "$Out/catalog.json" --clobber
if ($LASTEXITCODE -ne 0) { throw "The zips are up but the catalog isn't." }
Write-Host "Published."
