<#
.SYNOPSIS
  Makes a course folder: downloads its map, ground and trees for one box, writes course.json and checks it loads.

.DESCRIPTION
  Runs fetch-osm-course.ps1, fetch-elevation.ps1 and fetch-canopy.ps1 on the same box and puts the results in
  courses/<Id> at the top of the repository (not in git: they can be downloaded again). publish-courses.ps1 then
  makes them downloadable. Use find-course.ps1 to get the box. Making a course again raises its version, which is how
  players get offered the update.

  Elevation and trees come from USGS surveys, so outside the United States use -NoElevation -NoTrees (the course is
  then flat, with trees planted at random that a ball passes through). Trees need Python and download several hundred
  MB of lidar; without them the course still plays, with planted trees (-NoTrees). The lidar is only needed to find the
  trees, so it's deleted once the course is made (-KeepLidar keeps it), and kept if the run fails, to save downloading
  it again on the next try.

  -PublicName publishes the course's shape under another name: the map keeps only what the game plays from, with no
  hole names, pond names or anything else identifying the club, and -Name is only used to find the course in the map.
  Choose -Id, -Place and -Description to match, as they're shown to players as they are.

.EXAMPLE
  ./tools/make-course.ps1 -Id deer-run -Name "Deer Run" -Place "Moulton, AL" -Bbox "-87.335,34.415,-87.298,34.442"
  ./tools/make-course.ps1 -Id st-andrews -Name "Old Course" -Place "St Andrews, Scotland" -Bbox "..." -NoElevation -NoTrees
  ./tools/make-course.ps1 -Id georgia-pines -Name "Augusta National Golf Club" -PublicName "Georgia Pines" -Bbox "..."
#>
param(
    [Parameter(Mandatory)][string]$Id,
    [Parameter(Mandatory)][string]$Name,        # as mapped: it picks out the course's outline in the map
    [Parameter(Mandatory)][string]$Bbox,        # west,south,east,north
    [string]$PublicName = "",                   # the name to publish it under, if not the mapped one
    [string]$Place = "",
    [string]$Description = "",
    [string]$Out = "$PSScriptRoot/../courses",
    [switch]$NoElevation,
    [switch]$NoTrees,
    [switch]$KeepLidar
)
$ErrorActionPreference = "Stop"

if ($Id -notmatch '^[a-z0-9]+(-[a-z0-9]+)*$') { throw "-Id must be lower-case letters, digits and hyphens." }
$folder = Join-Path ([System.IO.Path]::GetFullPath($Out)) $Id
$lidar = Join-Path $env:LOCALAPPDATA "SquareRange\ept-cache\$Id"
New-Item -ItemType Directory -Force $folder | Out-Null

# Downloaded beside the course and only moved in once everything's worked, so a failed run leaves it as it was.
$work = Join-Path $folder ".new"
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory $work | Out-Null
try {
    & "$PSScriptRoot/fetch-osm-course.ps1" -Bbox $Bbox -Out "$work/map.osm"
    if (-not $NoElevation) { & "$PSScriptRoot/fetch-elevation.ps1" -Bbox $Bbox -Out "$work/ground.dem" }
    if (-not $NoTrees) { & "$PSScriptRoot/fetch-canopy.ps1" -Bbox $Bbox -Out "$work/trees.csv.gz" -Cache $lidar }

    if ($PublicName) {
        dotnet run --project "$PSScriptRoot/../src/GrimesGolf.CourseTool" -v q -- anonymise "$work/map.osm" $Name $PublicName
        if ($LASTEXITCODE -ne 0) { throw "Couldn't rename the course in its map, so $folder hasn't been changed." }
    }

    $manifest = Join-Path $folder "course.json"
    $version = if (Test-Path $manifest) { (Get-Content -Raw $manifest | ConvertFrom-Json).version + 1 } else { 1 }
    $shownName = if ($PublicName) { $PublicName } else { $Name }
    $text = [ordered]@{ id = $Id; name = $shownName; place = $Place; description = $Description; version = $version } | ConvertTo-Json
    [System.IO.File]::WriteAllText("$work/course.json", $text)

    Write-Host ""
    dotnet run --project "$PSScriptRoot/../src/GrimesGolf.CourseTool" -v q -- check $work
    if ($LASTEXITCODE -ne 0) { throw "The course didn't load, so $folder hasn't been changed." }

    Get-ChildItem $folder -File | Remove-Item
    Get-ChildItem $work -File | Move-Item -Destination $folder
    if (-not $KeepLidar -and (Test-Path $lidar)) { Remove-Item -Recurse -Force $lidar }
    Write-Host "`nMade $folder (version $version)."
}
finally {
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
}
