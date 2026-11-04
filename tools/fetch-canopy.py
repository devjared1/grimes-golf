"""
Finds the trees on a golf course from USGS 3DEP lidar: single trees, small groups and dense woods, each tree with its
position, height and crown size. Run it through tools/fetch-canopy.ps1 (which checks the Python packages), or directly:

    python tools/fetch-canopy.py --bbox=-87.335,34.415,-87.298,34.442 --out=src/SquareGspro.Core/Course/Data/deer-run.trees.csv.gz

How it works
------------
1. Asks The National Map which lidar surveys cover the box and takes the newest one that is published as an Entwine
   point tile (EPT) set in the public AWS bucket ``usgs-lidar-public`` (``--project`` picks one by name).
2. Reads the EPT octree's index (point counts per node, no point data) and downloads only the nodes over the box, down
   to the depth that gives about ``--density`` points per square metre. Downloads are cached, so runs with other
   settings are quick.
3. Grids the points at ``--metres`` cells: the highest return per cell (the top of whatever is there) and the mean
   height of the ground returns. Canopy height = top - ground. Buildings, bridges, wires, towers and noise are left out.
4. Tree tops are local maxima of the (lightly smoothed) canopy height, in a window that grows with the tree's height
   (tall trees have wide crowns), ignoring anything under ``--min-height``. Each canopy cell is then given to the
   nearest top it could belong to, which gives every tree its crown area and so a crown radius.
5. Trees whose crowns touch are grouped: 1 tree is ``single``, 2-5 a ``group``, 6 or more ``dense`` (woods).

Output: a gzipped CSV, one row per tree: ``lat,lon,height_m,crown_m,cluster,kind`` (``crown_m`` is the crown radius,
``cluster`` numbers the trees that touch), after a few ``#`` comment lines saying where the data came from.
``--preview`` also writes a PNG of the canopy with the trees marked (green: single, yellow: group, red: dense).

3DEP lidar is public domain (U.S. Geological Survey). Needs: numpy, laspy with LAZ support (``laspy[lazrs]``), and
Pillow for ``--preview``.
"""
import argparse
import concurrent.futures
import gzip
import io
import json
import math
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

import numpy as np

R = 6378137.0   # Web Mercator sphere (EPSG:3857), the EPT data's frame
TNM = "https://tnmaccess.nationalmap.gov/api/v1/products"
EPT = "https://s3-us-west-2.amazonaws.com/usgs-lidar-public"
USER_AGENT = "SquareRange-fetch-canopy/1.0"
# LAS classes left out of the canopy surface: buildings, noise, wires and towers, bridges, high noise.
NOT_CANOPY = (6, 7, 13, 14, 15, 16, 17, 18)


def log(message):
    print(message, flush=True)


def get(url, tries=5):
    """The bytes at url, retrying with a growing wait on network errors and 5xx/429."""
    for attempt in range(tries):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
            with urllib.request.urlopen(request, timeout=120) as response:
                return response.read()
        except urllib.error.HTTPError as e:
            if e.code < 500 and e.code != 429:
                raise
            error = e
        except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
            error = e
        time.sleep(2 ** attempt)
    raise RuntimeError(f"Couldn't download {url}: {error}")


def to_mercator(lon, lat):
    return R * math.radians(lon), R * math.log(math.tan(math.pi / 4 + math.radians(lat) / 2))


def from_mercator(x, y):
    return np.degrees(x / R), np.degrees(2 * np.arctan(np.exp(y / R)) - math.pi / 2)


# ---- which survey ----------------------------------------------------------------------------

def survey_year(name):
    """The year a survey was flown, from names like AL_17Co_1_2020 or AL_11County_2_B23 (0 if there's none)."""
    years = [int(y) for y in re.findall(r"(?<!\d)(19\d\d|20\d\d)(?!\d)", name)]
    if years:
        return max(years)
    b = re.search(r"_B(\d\d)(?!\d)", name)
    return 2000 + int(b.group(1)) if b else 0


def surveys_covering(bbox):
    """EPT names of the lidar surveys over bbox, newest first (from The National Map's download links)."""
    query = urllib.parse.urlencode({
        "datasets": "Lidar Point Cloud (LPC)", "bbox": ",".join(str(v) for v in bbox), "max": 500, "outputFormat": "JSON",
    })
    items = json.loads(get(f"{TNM}?{query}"))["items"]
    names = set()
    for item in items:
        # .../LPC/Projects/<project>/<work unit>/LAZ/<tile>.laz: the EPT set is named after the work unit.
        m = re.search(r"/Projects/[^/]+/([^/]+)/LAZ/", item.get("downloadURL", ""))
        if m:
            names.add(m.group(1))
    return sorted(names, key=lambda n: (survey_year(n), n), reverse=True)


def pick_survey(bbox, wanted):
    if wanted:
        return wanted, json.loads(get(f"{EPT}/{wanted}/ept.json"))
    candidates = surveys_covering(bbox)
    if not candidates:
        sys.exit("No 3DEP lidar covers that box.")
    for name in candidates:
        try:
            return name, json.loads(get(f"{EPT}/{name}/ept.json"))
        except urllib.error.HTTPError:
            log(f"  {name}: not published as EPT, skipped")
    sys.exit(f"None of the surveys over the box are in the EPT bucket: {', '.join(candidates)}")


# ---- the octree ------------------------------------------------------------------------------

def overlapping_nodes(survey, meta, box):
    """(key, depth, points, share of the node inside box) for every node over box (Mercator x0, y0, x1, y1)."""
    b = meta["bounds"]
    found, pending = [], ["0-0-0-0"]
    while pending:
        key = pending.pop()
        for k, count in json.loads(get(f"{EPT}/{survey}/ept-hierarchy/{key}.json")).items():
            d, x, y, _ = (int(v) for v in k.split("-"))
            size_x, size_y = (b[3] - b[0]) / 2 ** d, (b[4] - b[1]) / 2 ** d
            x0, y0 = b[0] + x * size_x, b[1] + y * size_y
            ox = min(x0 + size_x, box[2]) - max(x0, box[0])
            oy = min(y0 + size_y, box[3]) - max(y0, box[1])
            if ox <= 0 or oy <= 0:
                continue
            if count == -1:
                if k != key:
                    pending.append(k)   # its subtree is in a file of its own
            elif count > 0:
                found.append((k, d, count, ox * oy / (size_x * size_y)))
    return found


def choose_depth(nodes, area_m2, density):
    """The shallowest depth whose points (with all shallower ones) give density points per square metre in the box."""
    by_depth = {}
    for _, d, count, share in nodes:
        by_depth[d] = by_depth.get(d, 0) + count * share
    total = 0.0
    for d in sorted(by_depth):
        total += by_depth[d]
        log(f"  depth {d:2}: {total / area_m2:6.2f} points per m2")
        if total / area_m2 >= density:
            return d
    return max(by_depth)


def download_nodes(survey, nodes, cache, workers=8):
    """Downloads the nodes' LAZ files into cache (skipping ones already there); returns their paths."""
    os.makedirs(cache, exist_ok=True)
    paths = [os.path.join(cache, f"{k}.laz") for k, *_ in nodes]

    def fetch(i):
        path = paths[i]
        if not os.path.exists(path):
            data = get(f"{EPT}/{survey}/ept-data/{nodes[i][0]}.laz")
            with open(path + ".part", "wb") as f:
                f.write(data)
            os.replace(path + ".part", path)
        return os.path.getsize(path)

    done, size = 0, 0
    with concurrent.futures.ThreadPoolExecutor(workers) as pool:
        for n in pool.map(fetch, range(len(paths))):
            done += 1
            size += n
            if done % 25 == 0 or done == len(paths):
                log(f"  {done}/{len(paths)} nodes, {size / 1e6:.0f} MB")
    return paths


# ---- the canopy height grid ------------------------------------------------------------------

class Grid:
    """Cells of `metres` over the box in local metres east / north of its south-west corner (row 0 = south)."""

    def __init__(self, box, lat0, metres):
        self.box, self.metres = box, metres
        self.k = math.cos(math.radians(lat0))   # Mercator metres to true metres at this latitude
        self.cols = int(math.ceil((box[2] - box[0]) * self.k / metres))
        self.rows = int(math.ceil((box[3] - box[1]) * self.k / metres))
        n = self.rows * self.cols
        self.top = np.full(n, -np.inf, np.float32)
        self.ground_sum = np.zeros(n, np.float64)
        self.ground_count = np.zeros(n, np.int32)

    def add(self, path):
        import laspy
        las = laspy.read(path)
        x, y, z = np.asarray(las.x), np.asarray(las.y), np.asarray(las.z, np.float32)
        cls = np.asarray(las.classification)
        b = self.box
        inside = (x >= b[0]) & (x < b[2]) & (y >= b[1]) & (y < b[3])
        if not inside.any():
            return
        x, y, z, cls = x[inside], y[inside], z[inside], cls[inside]
        col = np.minimum(((x - b[0]) * self.k / self.metres).astype(np.int64), self.cols - 1)
        row = np.minimum(((y - b[1]) * self.k / self.metres).astype(np.int64), self.rows - 1)
        cell = row * self.cols + col
        canopy = ~np.isin(cls, NOT_CANOPY)
        np.maximum.at(self.top, cell[canopy], z[canopy])
        ground = cls == 2
        self.ground_sum += np.bincount(cell[ground], weights=z[ground], minlength=self.top.size)
        self.ground_count += np.bincount(cell[ground], minlength=self.top.size).astype(np.int32)

    def canopy_height(self):
        """Top minus ground, metres (2D, row 0 south). Ground under thick cover is filled in from round about."""
        shape = (self.rows, self.cols)
        ground = np.where(self.ground_count > 0, self.ground_sum / np.maximum(self.ground_count, 1), np.nan).reshape(shape)
        holes = np.isnan(ground)
        log(f"  ground returns in {100 * (1 - holes.mean()):.0f}% of cells; filling the rest")
        ground = fill_holes(ground)
        top = self.top.reshape(shape)
        top = np.where(np.isfinite(top), top, ground)
        return np.clip(top - ground, 0, None).astype(np.float32), ground


def shifted(a, dy, dx, fill):
    """a moved by (dy, dx) cells, edges filled with fill."""
    out = np.full_like(a, fill)
    h, w = a.shape
    out[max(dy, 0):h + min(dy, 0), max(dx, 0):w + min(dx, 0)] = a[max(-dy, 0):h + min(-dy, 0), max(-dx, 0):w + min(-dx, 0)]
    return out


def fill_holes(a, rounds=500):
    """Fills NaN cells with the mean of their filled neighbours, growing in from the edges of each hole."""
    a = a.copy()
    for _ in range(rounds):
        holes = np.isnan(a)
        if not holes.any():
            break
        total = np.zeros_like(a)
        count = np.zeros_like(a)
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                if dy or dx:
                    s = shifted(a, dy, dx, np.nan)
                    ok = ~np.isnan(s)
                    total[ok] += s[ok]
                    count[ok] += 1
        grow = holes & (count > 0)
        a[grow] = total[grow] / count[grow]
    return np.where(np.isnan(a), np.nanmean(a), a)


def smooth(a):
    """A light 3x3 blur (1-2-1 each way), so a crown's ragged top gives one maximum, not several."""
    out = np.pad(a, 1, mode="edge")   # the edge cells repeat, so nothing wraps round from the far side
    out = (out[:-2] + 2 * out[1:-1] + out[2:]) / 4
    out = (out[:, :-2] + 2 * out[:, 1:-1] + out[:, 2:]) / 4
    return out


def max_filter(a, radius):
    """Highest value within `radius` cells (a square window), by growing a 3x3 maximum."""
    out = a
    for _ in range(radius):
        grown = out
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                if dy or dx:
                    grown = np.maximum(grown, shifted(out, dy, dx, -np.inf))
        out = grown
    return out


# ---- trees -----------------------------------------------------------------------------------

def window_radius(height):
    """How far (m) a tree of this height must be the highest to count as a top: wider crowns on taller trees."""
    return np.clip(1.0 + 0.11 * height, 1.5, 6.0)


def find_tops(chm, metres, min_height, max_height):
    """(row, col, height) of tree tops: local maxima of the smoothed canopy, taller than min_height."""
    s = smooth(chm)
    candidate = (s >= min_height) & (s >= max_filter(s, 1))
    rows, cols = np.nonzero(candidate)
    heights = chm[rows, cols]
    keep = heights <= max_height   # higher than any tree: birds, cranes, missed noise
    rows, cols, heights = rows[keep], cols[keep], heights[keep]
    smoothed = s[rows, cols]
    radius = np.maximum(1, np.round(window_radius(smoothed) / metres).astype(int))
    is_top = np.zeros(rows.size, bool)
    for r in np.unique(radius):
        m = radius == r
        is_top[m] = smoothed[m] >= max_filter(s, int(r))[rows[m], cols[m]]
    rows, cols, heights = rows[is_top], cols[is_top], heights[is_top]

    # A flat top can give a few neighbouring maxima: keep the tallest within half a window of each other.
    order = np.argsort(-heights)
    rows, cols, heights = rows[order], cols[order], heights[order]
    cell = 4.0 / metres
    taken, keep = {}, []
    for i in range(rows.size):
        sep = 0.5 * window_radius(heights[i]) / metres
        gy, gx = int(rows[i] // cell), int(cols[i] // cell)
        clash = False
        for oy in (-1, 0, 1):
            for ox in (-1, 0, 1):
                for j in taken.get((gy + oy, gx + ox), ()):
                    if (rows[j] - rows[i]) ** 2 + (cols[j] - cols[i]) ** 2 < sep * sep:
                        clash = True
                        break
        if not clash:
            taken.setdefault((gy, gx), []).append(i)
            keep.append(i)
    keep = np.array(keep, int)
    return rows[keep], cols[keep], heights[keep]


def crown_radii(chm, rows, cols, heights, metres):
    """Each tree's crown radius (m): canopy cells round it go to the nearest top they could belong to."""
    best = np.full(chm.shape, np.inf, np.float32)
    owner = np.full(chm.shape, -1, np.int32)
    for i in range(rows.size):
        h = heights[i]
        reach = int(math.ceil(min(12.0, 2.5 + 0.35 * h) / metres))
        r0, r1 = max(rows[i] - reach, 0), min(rows[i] + reach + 1, chm.shape[0])
        c0, c1 = max(cols[i] - reach, 0), min(cols[i] + reach + 1, chm.shape[1])
        yy, xx = np.mgrid[r0:r1, c0:c1]
        d2 = ((yy - rows[i]) ** 2 + (xx - cols[i]) ** 2).astype(np.float32)
        window = chm[r0:r1, c0:c1]
        mine = (d2 <= reach * reach) & (window >= max(2.0, 0.4 * h)) & (window <= h + 0.5) & (d2 < best[r0:r1, c0:c1])
        best[r0:r1, c0:c1][mine] = d2[mine]
        owner[r0:r1, c0:c1][mine] = i
    area = np.bincount(owner[owner >= 0], minlength=rows.size) * metres * metres
    return np.maximum(np.sqrt(area / math.pi), metres)


def clusters(rows, cols, radii, metres, gap=1.0):
    """Cluster number of each tree: trees whose crowns come within `gap` metres of each other are in one."""
    parent = np.arange(rows.size)

    def root(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    e, n = cols * metres, rows * metres
    cell = 2 * float(radii.max(initial=1)) + gap
    buckets = {}
    for i in range(rows.size):
        buckets.setdefault((int(e[i] // cell), int(n[i] // cell)), []).append(i)
    for (bx, by), members in buckets.items():
        near = [j for ox in (-1, 0, 1) for oy in (-1, 0, 1) for j in buckets.get((bx + ox, by + oy), ())]
        for i in members:
            for j in near:
                if j > i and (e[i] - e[j]) ** 2 + (n[i] - n[j]) ** 2 <= (radii[i] + radii[j] + gap) ** 2:
                    parent[root(i)] = root(j)
    roots = np.array([root(i) for i in range(rows.size)], int)
    _, number, size = np.unique(roots, return_inverse=True, return_counts=True)
    return number, size[number]


def kind_of(size):
    return "single" if size == 1 else "group" if size <= 5 else "dense"


# ---- output ----------------------------------------------------------------------------------

def write_csv(path, survey, grid, rows, cols, heights, radii, number, size, args):
    e = (cols + 0.5) * grid.metres
    n = (rows + 0.5) * grid.metres
    lon, lat = from_mercator(grid.box[0] + e / grid.k, grid.box[1] + n / grid.k)
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with gzip.open(path, "wt", encoding="utf-8", newline="\n") as f:
        f.write(f"# Trees from USGS 3DEP lidar, survey {survey} (public domain, U.S. Geological Survey).\n")
        f.write(f"# Made by tools/fetch-canopy.py: box {args.bbox}, {grid.metres} m cells, {args.density} points/m², "
                f"trees from {args.min_height} m.\n")
        f.write("# crown_m = crown radius; trees with the same cluster touch; kind: single, group (2-5), dense (6+).\n")
        f.write("lat,lon,height_m,crown_m,cluster,kind\n")
        for i in np.argsort(number, kind="stable"):
            f.write(f"{lat[i]:.7f},{lon[i]:.7f},{heights[i]:.1f},{radii[i]:.1f},{number[i]},{kind_of(size[i])}\n")


def write_preview(path, chm, rows, cols, radii, size, metres):
    from PIL import Image, ImageDraw
    shade = np.clip(chm / 30.0, 0, 1)
    rgb = np.stack([30 + 20 * shade, 40 + 160 * shade, 30 + 40 * shade], axis=-1).astype(np.uint8)[::-1]   # north up
    image = Image.fromarray(rgb)
    draw = ImageDraw.Draw(image)
    colours = {"single": (80, 255, 80), "group": (255, 220, 60), "dense": (255, 70, 60)}
    h = chm.shape[0]
    for i in range(rows.size):
        x, y, r = cols[i], h - 1 - rows[i], max(1.0, radii[i] / metres)
        draw.ellipse([x - r, y - r, x + r, y + r], outline=colours[kind_of(size[i])])
    image.save(path)


def main():
    p = argparse.ArgumentParser(description="Tree positions, heights and crowns from USGS 3DEP lidar.")
    p.add_argument("--bbox", default="-87.335,34.415,-87.298,34.442", help="west,south,east,north (degrees)")
    p.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "..", "src", "SquareGspro.Core", "Course", "Data", "deer-run.trees.csv.gz"))
    p.add_argument("--project", default="", help="EPT survey name (default: the newest over the box)")
    p.add_argument("--metres", type=float, default=1.0, help="canopy grid cell size")
    p.add_argument("--density", type=float, default=6.0, help="lidar points per square metre to download")
    p.add_argument("--min-height", type=float, default=4.0, help="shortest tree counted (m)")
    p.add_argument("--max-height", type=float, default=50.0, help="tallest believable tree (m)")
    p.add_argument("--cache", default=os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "SquareRange", "ept-cache"))
    p.add_argument("--preview", action="store_true", help="also write a PNG of the canopy and trees next to --out")
    args = p.parse_args()

    w, s, e, n = (float(v) for v in args.bbox.split(","))
    x0, y0 = to_mercator(w, s)
    x1, y1 = to_mercator(e, n)
    box = (x0, y0, x1, y1)
    lat0 = (s + n) / 2
    k = math.cos(math.radians(lat0))
    area = (x1 - x0) * (y1 - y0) * k * k

    log("Finding the lidar surveys over the box (The National Map)")
    survey, meta = pick_survey((w, s, e, n), args.project)
    log(f"Survey {survey}: {meta['points']:,} points in all")
    log("Reading the point index")
    nodes = overlapping_nodes(survey, meta, box)
    depth = choose_depth(nodes, area, args.density)
    nodes = [node for node in nodes if node[1] <= depth]
    # About 12 bytes a point compressed (measured on USGS EPT data); nodes come whole, edges and all.
    log(f"Downloading {len(nodes)} nodes (to depth {depth}) for {area / 1e6:.1f} km2, about "
        f"{sum(c for _, _, c, _ in nodes) * 12 / 1e6:.0f} MB (cached in {args.cache})")
    paths = download_nodes(survey, nodes, os.path.join(args.cache, survey))

    grid = Grid(box, lat0, args.metres)
    log(f"Gridding into {grid.cols} x {grid.rows} cells of {args.metres} m")
    for i, path in enumerate(paths):
        grid.add(path)
        if (i + 1) % 100 == 0:
            log(f"  {i + 1}/{len(paths)} nodes")
    chm, _ = grid.canopy_height()

    log("Finding the trees")
    rows, cols, heights = find_tops(chm, args.metres, args.min_height, args.max_height)
    radii = crown_radii(chm, rows, cols, heights, args.metres)
    number, size = clusters(rows, cols, radii, args.metres)
    write_csv(args.out, survey, grid, rows, cols, heights, radii, number, size, args)
    kinds = {kd: int(sum(1 for c in size if kind_of(c) == kd)) for kd in ("single", "group", "dense")}
    log(f"{rows.size:,} trees: {kinds['single']:,} single, {kinds['group']:,} in groups of 2-5, {kinds['dense']:,} in dense woods; "
        f"heights {np.percentile(heights, 10):.0f}-{np.percentile(heights, 90):.0f} m (10th-90th percentile)")
    log(f"-> {os.path.abspath(args.out)} ({os.path.getsize(args.out) / 1e6:.1f} MB)")
    if args.preview:
        # Beside the cached lidar, not the output: it's big, and only for checking by eye.
        name = os.path.basename(re.sub(r"(\.csv)?(\.gz)?$", "", args.out))
        png = os.path.join(args.cache, f"{name}-preview.png")
        write_preview(png, chm, rows, cols, radii, size, args.metres)
        log(f"-> {os.path.abspath(png)}")


if __name__ == "__main__":
    main()
