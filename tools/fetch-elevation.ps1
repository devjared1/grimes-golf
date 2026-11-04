<#
.SYNOPSIS
  Downloads ground elevation for a golf course from the USGS 3D Elevation Program (3DEP) and saves it compactly.

.DESCRIPTION
  Asks the USGS 3DEP elevation service (best available resolution: 1 m from lidar where flown) to export a GeoTIFF of
  just the bounding box, then converts it to the app's .dem format: a gzipped grid of heights in centimetres
  (delta-encoded along each row), with the grid's latitude/longitude edges. Rows run north to south.

  3DEP data is public domain (U.S. Geological Survey).

.EXAMPLE
  ./tools/fetch-elevation.ps1                          # Deer Run, Moulton AL (the default)
  ./tools/fetch-elevation.ps1 -Bbox "-87.335,34.415,-87.298,34.442" -Out src/GrimesGolf.Core/Course/Data/deer-run.dem
#>
param(
    [string]$Bbox = "-87.335,34.415,-87.298,34.442",   # west,south,east,north (same box as fetch-osm-course.ps1)
    [string]$Out = "$PSScriptRoot/../src/GrimesGolf.Core/Course/Data/deer-run.dem",
    [double]$Metres = 1.0                              # target cell size
)
$ErrorActionPreference = "Stop"

$w, $s, $e, $n = $Bbox.Split(',') | ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) }
$midLat = ($s + $n) / 2 * [Math]::PI / 180
$width = [int][Math]::Ceiling(($e - $w) * 111320 * [Math]::Cos($midLat) / $Metres)
$height = [int][Math]::Ceiling(($n - $s) * 110950 / $Metres)
if ($width -gt 8000 -or $height -gt 8000) { throw "Area too large for one request ($width x $height cells); use a smaller box or larger -Metres." }

$service = "https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer/exportImage"
$query = "bbox=$Bbox&bboxSR=4326&imageSR=4326&size=$width,$height&format=tiff&pixelType=F32" +
         "&noDataInterpretation=esriNoDataMatchAny&interpolation=RSP_BilinearInterpolation&f=json"
Write-Host "Requesting $width x $height elevation grid from USGS 3DEP"
$job = Invoke-RestMethod "$service`?$query" -TimeoutSec 300
$tif = [System.IO.Path]::GetTempFileName()
try {
    Invoke-WebRequest $job.href -OutFile $tif -UseBasicParsing -TimeoutSec 600

    Add-Type -Language CSharp -TypeDefinition @"
using System; using System.IO; using System.IO.Compression;
public static class DemConverter
{
    // Reads an uncompressed little-endian float32 GeoTIFF (tiled or stripped) in EPSG:4326 and writes the .dem format.
    public static string Convert(string tifPath, string outPath)
    {
        byte[] b = File.ReadAllBytes(tifPath);
        if (b[0] != 'I' || b[1] != 'I') throw new InvalidDataException("Expected a little-endian TIFF.");
        int ifd = BitConverter.ToInt32(b, 4), count = BitConverter.ToUInt16(b, ifd);
        int width = 0, height = 0, bits = 0, compression = 1, format = 1, tileW = 0, tileH = 0, rowsPerStrip = 0;
        uint[] offsets = null; double[] scale = null, tie = null;
        Func<int, int, int, uint> val = (type, at, i) => type == 3 ? BitConverter.ToUInt16(b, at + 2 * i) : BitConverter.ToUInt32(b, at + 4 * i);
        for (int i = 0; i < count; i++)
        {
            int o = ifd + 2 + 12 * i, tag = BitConverter.ToUInt16(b, o), type = BitConverter.ToUInt16(b, o + 2);
            int n = BitConverter.ToInt32(b, o + 4), size = type == 3 ? 2 : type == 12 ? 8 : 4;
            int at = n * size <= 4 ? o + 8 : BitConverter.ToInt32(b, o + 8);
            switch (tag)
            {
                case 256: width = (int)val(type, at, 0); break;
                case 257: height = (int)val(type, at, 0); break;
                case 258: bits = (int)val(type, at, 0); break;
                case 259: compression = (int)val(type, at, 0); break;
                case 339: format = (int)val(type, at, 0); break;
                case 278: rowsPerStrip = (int)val(type, at, 0); break;
                case 322: tileW = (int)val(type, at, 0); break;
                case 323: tileH = (int)val(type, at, 0); break;
                case 273: case 324: offsets = new uint[n]; for (int k = 0; k < n; k++) offsets[k] = val(type, at, k); break;
                case 33550: scale = new double[n]; for (int k = 0; k < n; k++) scale[k] = BitConverter.ToDouble(b, at + 8 * k); break;
                case 33922: tie = new double[n]; for (int k = 0; k < n; k++) tie[k] = BitConverter.ToDouble(b, at + 8 * k); break;
            }
        }
        if (compression != 1 || bits != 32 || format != 3) throw new InvalidDataException("Expected uncompressed float32 samples.");
        if (scale == null || tie == null) throw new InvalidDataException("Missing GeoTIFF georeferencing.");

        var z = new float[width * height];
        if (tileW > 0)
        {
            int across = (width + tileW - 1) / tileW;
            for (int t = 0; t < offsets.Length; t++)
                for (int r = 0; r < tileH; r++)
                    for (int c = 0; c < tileW; c++)
                    {
                        int x = (t % across) * tileW + c, y = (t / across) * tileH + r;
                        if (x < width && y < height) z[y * width + x] = BitConverter.ToSingle(b, (int)offsets[t] + 4 * (r * tileW + c));
                    }
        }
        else
            for (int s = 0; s < offsets.Length; s++)
                for (int k = 0; k < rowsPerStrip * width && s * rowsPerStrip * width + k < z.Length; k++)
                    z[s * rowsPerStrip * width + k] = BitConverter.ToSingle(b, (int)offsets[s] + 4 * k);

        // Fill no-data (very large/small values) from the left neighbour, then the row above.
        float min = float.MaxValue, max = float.MinValue; int holes = 0;
        for (int i = 0; i < z.Length; i++)
        {
            if (!(z[i] > -500 && z[i] < 9000)) { holes++; z[i] = i % width > 0 ? z[i - 1] : i >= width ? z[i - width] : float.NaN; }
            if (!float.IsNaN(z[i])) { min = Math.Min(min, z[i]); max = Math.Max(max, z[i]); }
        }
        for (int i = 0; i < z.Length; i++) if (float.IsNaN(z[i])) z[i] = min;
        if (max - min > 650) throw new InvalidDataException("Elevation range too large for centimetre storage.");

        double west = tie[3], north = tie[4], east = west + scale[0] * width, south = north - scale[1] * height;
        using (var file = File.Create(outPath))
        using (var gz = new GZipStream(file, CompressionLevel.Optimal))
        using (var w = new BinaryWriter(gz))
        {
            w.Write(new char[] { 'S', 'Q', 'D', 'E', 'M', '1' });
            w.Write(width); w.Write(height);
            w.Write(west); w.Write(south); w.Write(east); w.Write(north);
            w.Write(min);
            for (int y = 0; y < height; y++)
            {
                int prev = 0;
                for (int x = 0; x < width; x++)
                {
                    int cm = (int)Math.Round((z[y * width + x] - min) * 100);
                    w.Write((short)(cm - prev));
                    prev = cm;
                }
            }
        }
        return string.Format("{0} x {1} cells, {2:0.0} to {3:0.0} m, {4} no-data cells filled", width, height, min, max, holes);
    }
}
"@

    $full = [System.IO.Path]::GetFullPath($Out)
    New-Item -ItemType Directory -Force -Path (Split-Path $full) | Out-Null
    $summary = [DemConverter]::Convert($tif, $full)
    Write-Host "$summary -> $full ($([Math]::Round((Get-Item $full).Length / 1MB, 1)) MB)"
}
finally {
    Remove-Item $tif -ErrorAction SilentlyContinue
}
