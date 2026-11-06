<#
.SYNOPSIS
  Downloads a golf course from OpenStreetMap and keeps only what the course importer uses.

.DESCRIPTION
  Calls the OSM API "map" request for a bounding box, then keeps golf features (holes, tees, greens, fairways,
  bunkers, water hazards, rough, cart paths), the golf_course outline, water, ditches and woods, plus the nodes they use. Everything
  else (roads, buildings, ...) is dropped. Re-run it after editing the course on openstreetmap.org.

  Map data (c) OpenStreetMap contributors, available under the Open Database Licence (ODbL).

.EXAMPLE
  ./tools/fetch-osm-course.ps1                       # Deer Run, Moulton AL (the default)
  ./tools/fetch-osm-course.ps1 -Bbox "-87.335,34.415,-87.298,34.442" -Out src/GrimesGolf.Godot/courses/deer-run/map.osm
  ./tools/fetch-osm-course.ps1 -Source saved.osm     # trim a file you already downloaded
#>
param(
    [string]$Bbox = "-87.335,34.415,-87.298,34.442",   # west,south,east,north
    [string]$Out = "$PSScriptRoot/../src/GrimesGolf.Godot/courses/deer-run/map.osm",
    [string]$Source
)
$ErrorActionPreference = "Stop"

if ($Source) {
    [xml]$doc = Get-Content -Raw -LiteralPath $Source
} else {
    $url = "https://api.openstreetmap.org/api/0.6/map?bbox=$Bbox"
    Write-Host "Downloading $url"
    $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -Headers @{ "User-Agent" = "GrimesGolf course importer" }
    [xml]$doc = $resp.Content
}

function Tags($el) {
    $t = @{}
    foreach ($tag in $el.SelectNodes("tag")) { $t[$tag.k] = $tag.v }
    $t
}
function Wanted($t) {
    $t.ContainsKey("golf") -or $t["leisure"] -eq "golf_course" -or $t["natural"] -in @("water", "wood") -or
        $t["landuse"] -eq "forest" -or $t.ContainsKey("waterway") -or $t["golf_cart"] -in @("yes", "designated")
}

$osm = $doc.osm
$keepWays = @{}
$keepRels = @()
foreach ($r in $osm.SelectNodes("relation")) {
    if (-not (Wanted (Tags $r))) { continue }
    $keepRels += $r
    foreach ($m in $r.SelectNodes("member[@type='way']")) { $keepWays[$m.ref] = $true }
}
foreach ($w in $osm.SelectNodes("way")) { if (Wanted (Tags $w)) { $keepWays[$w.id] = $true } }

$ways = @($osm.SelectNodes("way") | Where-Object { $keepWays.ContainsKey($_.id) })
$keepNodes = @{}
foreach ($w in $ways) { foreach ($nd in $w.SelectNodes("nd")) { $keepNodes[$nd.ref] = $true } }

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
[void]$sb.AppendLine('<osm version="0.6" generator="fetch-osm-course.ps1" copyright="OpenStreetMap and contributors" license="http://opendatacommons.org/licenses/odbl/1-0/">')
foreach ($n in $osm.SelectNodes("node")) {
    if ($keepNodes.ContainsKey($n.id)) { [void]$sb.AppendLine("  <node id=""$($n.id)"" lat=""$($n.lat)"" lon=""$($n.lon)""/>") }
}
function Esc([string]$s) { [System.Security.SecurityElement]::Escape($s) }
foreach ($w in $ways) {
    [void]$sb.AppendLine("  <way id=""$($w.id)"">")
    foreach ($nd in $w.SelectNodes("nd")) { [void]$sb.AppendLine("    <nd ref=""$($nd.ref)""/>") }
    foreach ($tag in $w.SelectNodes("tag")) { [void]$sb.AppendLine("    <tag k=""$(Esc $tag.k)"" v=""$(Esc $tag.v)""/>") }
    [void]$sb.AppendLine("  </way>")
}
foreach ($r in $keepRels) {
    [void]$sb.AppendLine("  <relation id=""$($r.id)"">")
    foreach ($m in $r.SelectNodes("member[@type='way']")) {
        if ($keepWays.ContainsKey($m.ref)) { [void]$sb.AppendLine("    <member type=""way"" ref=""$($m.ref)"" role=""$(Esc $m.role)""/>") }
    }
    foreach ($tag in $r.SelectNodes("tag")) { [void]$sb.AppendLine("    <tag k=""$(Esc $tag.k)"" v=""$(Esc $tag.v)""/>") }
    [void]$sb.AppendLine("  </relation>")
}
[void]$sb.AppendLine('</osm>')

$full = [System.IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force -Path (Split-Path $full) | Out-Null
[System.IO.File]::WriteAllText($full, $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Host "Kept $($keepNodes.Count) nodes, $($ways.Count) ways, $($keepRels.Count) relations -> $full"
