<#
.SYNOPSIS
  Finds golf courses on OpenStreetMap by name, says how well each is mapped, and prints the command to make it.

.DESCRIPTION
  Asks the Overpass API for golf courses whose name matches, then counts what's mapped inside each one's box. A course
  plays properly only if its holes are mapped (golf=hole lines); greens, fairways and bunkers make it look like itself.
  The box it prints is the course's outline plus a margin, which the ground and trees around the course need.

.EXAMPLE
  ./tools/find-course.ps1 "Deer Run"
  ./tools/find-course.ps1 "Pebble Beach" -Limit 5
#>
param(
    [Parameter(Mandatory)][string]$Name,
    [int]$Limit = 10,
    [double]$Margin = 0.004                     # degrees around the outline (about 400 m)
)
$ErrorActionPreference = "Stop"

$overpass = "https://overpass-api.de/api/interpreter"
$headers = @{ "User-Agent" = "GrimesGolf course finder" }
$inv = [Globalization.CultureInfo]::InvariantCulture
function Ask($query) { Invoke-RestMethod $overpass -Method Post -Body @{ data = $query } -Headers $headers -TimeoutSec 180 }

$pattern = $Name -replace '["\\]', ''
$found = (Ask "[out:json][timeout:120];(way[`"leisure`"=`"golf_course`"][`"name`"~`"$pattern`",i];relation[`"leisure`"=`"golf_course`"][`"name`"~`"$pattern`",i];);out tags bb;").elements
if (-not $found) { Write-Host "No golf course on OpenStreetMap has a name like '$Name'."; return }
Write-Host "$(@($found).Count) found$(if (@($found).Count -gt $Limit) { ", showing the first $Limit" }):`n"

foreach ($c in @($found) | Select-Object -First $Limit) {
    $b = $c.bounds
    $box = [string]::Format($inv, "{0:0.####},{1:0.####},{2:0.####},{3:0.####}", ($b.minlon - $Margin), ($b.minlat - $Margin), ($b.maxlon + $Margin), ($b.maxlat + $Margin))
    $s = [string]::Format($inv, "{0},{1},{2},{3}", $b.minlat, $b.minlon, $b.maxlat, $b.maxlon)
    $parts = (Ask "[out:json][timeout:60];(way[`"golf`"=`"hole`"]($s);way[`"golf`"=`"green`"]($s);way[`"golf`"=`"fairway`"]($s);way[`"golf`"=`"bunker`"]($s););out tags;").elements
    $count = { param($kind) @($parts | Where-Object { $_.tags.golf -eq $kind }).Count }
    $place = @($c.tags.'addr:city', $c.tags.'addr:state') | Where-Object { $_ }
    $tag = "$($c.tags.name)$(if ($place) { " ($($place -join ', '))" })"
    Write-Host $tag
    Write-Host "  $(& $count 'hole') holes, $(& $count 'green') greens, $(& $count 'fairway') fairways, $(& $count 'bunker') bunkers mapped"
    Write-Host "  https://www.openstreetmap.org/$($c.type)/$($c.id)"
    $id = ($c.tags.name.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')
    Write-Host "  ./tools/make-course.ps1 -Id $id -Name `"$($c.tags.name)`" -Place `"$($place -join ', ')`" -Bbox `"$box`""
    Write-Host ""
}
