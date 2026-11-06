<#
.SYNOPSIS
  Finds the trees on a golf course (single trees, groups of 2-5 and dense woods) from USGS 3DEP lidar.

.DESCRIPTION
  The tree counterpart of fetch-elevation.ps1. The same lidar surveys the elevation comes from record the tops of
  trees as well as the ground: canopy height = highest return - ground. Tree tops are the peaks of the canopy, each
  with its height and crown radius, and trees whose crowns touch are grouped (single, group of 2-5, dense woods).

  The work is done by fetch-canopy.py (see the notes at its top), which needs Python 3 with numpy and laspy[lazrs]
  (Pillow too for -Preview); this script checks for them, and -Install installs them for the current user. Only the
  lidar over the box is downloaded (from the public USGS Entwine point tiles on AWS), and it's cached, so runs
  with other settings don't download it again. The whole Deer Run box is several hundred MB the first time.

  Writes a gzipped CSV, one row per tree: lat,lon,height_m,crown_m,cluster,kind.
  3DEP lidar is public domain (U.S. Geological Survey).

.EXAMPLE
  ./tools/fetch-canopy.ps1                               # Deer Run, Moulton AL (the default)
  ./tools/fetch-canopy.ps1 -Install                      # first time: install the Python packages
  ./tools/fetch-canopy.ps1 -Preview                      # also a PNG of the canopy with the trees marked
  ./tools/fetch-canopy.ps1 -Bbox "-87.318,34.425,-87.310,34.430" -Out "$env:TEMP/test.trees.csv.gz" -Preview
#>
param(
    [string]$Bbox = "-87.335,34.415,-87.298,34.442",   # west,south,east,north (same box as fetch-osm-course.ps1)
    [string]$Out = "$PSScriptRoot/../src/GrimesGolf.Godot/courses/deer-run/trees.csv.gz",
    [string]$Project = "",                             # lidar survey (EPT name); default: the newest over the box
    [double]$Metres = 1.0,                             # canopy grid cell size
    [double]$Density = 6,                              # lidar points per square metre to download
    [double]$MinHeight = 4,                            # shortest tree counted, m
    [string]$Cache = "",                               # where downloaded lidar is kept (default: %LOCALAPPDATA%\SquareRange\ept-cache)
    [switch]$Preview,
    [switch]$Install
)
$ErrorActionPreference = "Stop"

# "python" first, so an activated virtual environment is the one used.
$python = @("python", "python3", "py") | Where-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
if (-not $python) { throw "Python 3 is needed (https://www.python.org/downloads/)." }
$packages = @("numpy", "laspy[lazrs]") + $(if ($Preview) { @("pillow") } else { @() })
if ($Install) {
    & $python -m pip install --user --upgrade @packages
    if ($LASTEXITCODE -ne 0) { throw "pip couldn't install $($packages -join ', ')." }
}
$check = "import numpy, laspy, lazrs" + $(if ($Preview) { ", PIL" } else { "" })
# (Windows PowerShell turns a native program's error output into an exception under "Stop", so relax it for the check.)
$ErrorActionPreference = "Continue"
& $python -c $check 2>&1 | Out-Null
$missing = $LASTEXITCODE -ne 0
$ErrorActionPreference = "Stop"
if ($missing) {
    throw "Python packages missing: run with -Install, or: $python -m pip install --user $($packages -join ' ')"
}

$inv = [Globalization.CultureInfo]::InvariantCulture
$arguments = @(
    "$PSScriptRoot/fetch-canopy.py", "--bbox=$Bbox", "--out=$([System.IO.Path]::GetFullPath($Out))",
    "--metres=$($Metres.ToString($inv))", "--density=$($Density.ToString($inv))", "--min-height=$($MinHeight.ToString($inv))"
)
if ($Project) { $arguments += "--project=$Project" }
if ($Cache) { $arguments += "--cache=$([System.IO.Path]::GetFullPath($Cache))" }
if ($Preview) { $arguments += "--preview" }
$env:PYTHONIOENCODING = "utf-8"
& $python @arguments
if ($LASTEXITCODE -ne 0) { throw "fetch-canopy.py failed." }
