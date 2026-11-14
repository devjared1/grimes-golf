<#
.SYNOPSIS
  Remakes installer/THIRD-PARTY-NOTICES.txt, the licences of what ships inside the app.

.DESCRIPTION
  Godot's licence and those of the libraries inside it come from the engine itself, and .NET's from the SDK. Run it
  again after moving to a new Godot or .NET version.

.EXAMPLE
  ./tools/make-notices.ps1
#>
param([string]$Godot = "godot")
$ErrorActionPreference = "Stop"

$root = (Resolve-Path "$PSScriptRoot/..").Path
$out = "$root/installer/THIRD-PARTY-NOTICES.txt"
$temp = Join-Path ([IO.Path]::GetTempPath()) "grimes-golf-godot-notices.txt"
Remove-Item -ErrorAction SilentlyContinue $temp

$cmd = (Get-Command $Godot).Source
$exe = if ($cmd -like "*.cmd") { (@(Get-Content $cmd) -match "\.exe" | Select-Object -First 1) -replace '^@"', '' -replace '" %\*$', '' } else { $cmd }
# The editor is a windowed program, so it has to be waited for.
$run = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -ArgumentList @("--headless", "--script", "`"$PSScriptRoot/make-notices.gd`"", "--", "`"$temp`"")
if (-not $run.WaitForExit(120000)) { $run.Kill(); throw "Godot didn't finish." }
if (-not (Test-Path $temp)) { throw "Godot didn't write the notices." }

$dotnet = Split-Path (Get-Command dotnet).Source
$lines = @(
    "Grimes Golf includes the software below. Its own licence is at https://github.com/devjared1/grimes-golf/blob/main/LICENSE"
    ""
    # Code ported into the app itself (the Garmin R10 support), from the repository's NOTICE.
    (Get-Content -Raw "$root/NOTICE")
    "-------------------------------------------------------------------------------"
    ""
    (Get-Content -Raw $temp)
    ".NET RUNTIME"
    ""
    (Get-Content -Raw "$dotnet/LICENSE.txt")
    (Get-Content -Raw "$dotnet/ThirdPartyNotices.txt")
)
[IO.File]::WriteAllLines($out, $lines)
Remove-Item $temp
Write-Host "$out ($([math]::Round((Get-Item $out).Length / 1KB)) KB)"
