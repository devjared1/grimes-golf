#!/usr/bin/env python3
"""Makes the app's logo, splash screen and icons from branding/GrimesGolfLogo.svg.

Run it again only when the SVG changes; what it makes is committed:
  src/GrimesGolf.Godot/assets/ui/grimes_golf_logo.png    the logo on the menu, loading screen and top bar
  src/GrimesGolf.Godot/assets/ui/splash.png              the boot splash (project.godot)
  src/GrimesGolf.Godot/assets/icons/grimes_golf_icon.png the window and taskbar icon (project.godot)
  src/GrimesGolf.Godot/assets/icons/grimes_golf.ico      the exe (export_presets.cfg), so its shortcuts too, and the installer

The logo has fine script lettering, too wide and thin to read at icon sizes, so the icon is a monogram of its two G's on
a dark tile: both G's interlocked from 48 px up, one G below that, where two would blur together. The SVG holds the
letters as vector shapes, which are what the icon is cut from. Small sizes get heavier strokes to stay readable at 16 px.

Needs Pillow and Google Chrome or Microsoft Edge (which renders the SVG, with its masks and filters).
"""
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
import time

from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = pathlib.Path(__file__).resolve().parent.parent
SVG = ROOT / "branding" / "GrimesGolfLogo.svg"
UI = ROOT / "src" / "GrimesGolf.Godot" / "assets" / "ui"
ICONS = ROOT / "src" / "GrimesGolf.Godot" / "assets" / "icons"

NEON = (126, 224, 129)        # the logo's lit lettering
SPLASH_CENTRE = (23, 41, 28)  # the same dark green and near-black as the loading screen (LoadingScreen.cs)
SPLASH_EDGE = (6, 8, 9)
ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def browser() -> str:
    for path in (r"C:\Program Files\Google\Chrome\Application\chrome.exe",
                 r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"):
        if pathlib.Path(path).exists():
            return path
    found = shutil.which("chrome") or shutil.which("msedge")
    if not found:
        sys.exit("Needs Google Chrome or Microsoft Edge to render the SVG.")
    return found


def render(html: str, width: int, height: int, scale: int) -> Image.Image:
    """A page drawn by a headless browser, on transparency."""
    with tempfile.TemporaryDirectory() as tmp:
        tmp = pathlib.Path(tmp)
        page, shot = tmp / "page.html", tmp / "shot.png"
        page.write_text(html, encoding="utf-8")
        subprocess.run([browser(), "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
                        f"--user-data-dir={tmp / 'profile'}", "--default-background-color=00000000",
                        f"--force-device-scale-factor={scale}", f"--window-size={width},{height}",
                        f"--screenshot={shot}", page.as_uri()], check=True, capture_output=True)
        for _ in range(100):  # the launcher can return before the browser has written the file
            if shot.exists() and shot.stat().st_size > 0:
                break
            time.sleep(0.2)
        image = Image.open(shot).convert("RGBA")
        image.load()
        time.sleep(0.5)       # let the browser let go of its profile, so the folder can be removed
    return image


def make_logo() -> Image.Image:
    data = SVG.read_text(encoding="utf-8")
    page = ("<!DOCTYPE html><style>html,body{margin:0;background:transparent}svg{display:block;width:1003px;height:272px}</style>"
            + data[data.index("<svg"):])
    raw = render(page, 1003, 272, 4)
    # Trim to the glow, ignoring its faintest edge, and leave a little of it as a margin.
    box = raw.getchannel("A").point(lambda a: 255 if a > 6 else 0).getbbox()
    pad = 16
    box = (max(box[0] - pad, 0), max(box[1] - pad, 0), min(box[2] + pad, raw.width), min(box[3] + pad, raw.height))
    return raw.crop(box)


def letter_masks() -> dict[str, Image.Image]:
    """The two G's as large white shapes, from the SVG's vector letters."""
    data = SVG.read_text(encoding="utf-8")
    letters = re.findall(r'<g fill="#[0-9a-f]{6}" fill-opacity="1"><g transform="translate\(([\d.]+), ([\d.]+)\)"><g><path d="([^"]+)"', data)
    if len(letters) < 7:
        sys.exit(f"Expected the letters of the logo in the SVG, found {len(letters)} shapes.")
    masks = {}
    for name, (x, y, d) in {"grimes": letters[0], "golf": letters[6]}.items():
        svg = (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{float(x) - 30} {float(y) - 75} 120 120" width="500" height="500">'
               f'<path fill="#fff" transform="translate({x},{y})" d="{d}"/></svg>')
        image = render(f"<!DOCTYPE html><style>html,body{{margin:0;background:transparent}}svg{{display:block}}</style>{svg}", 500, 500, 4)
        alpha = image.getchannel("A")
        masks[name] = alpha.crop(alpha.getbbox())
    return masks


def centred_square(shape: Image.Image, canvas: int) -> Image.Image:
    fit = canvas / max(shape.size)
    shape = shape.resize((round(shape.width * fit), round(shape.height * fit)), Image.LANCZOS)
    square = Image.new("L", (canvas, canvas), 0)
    square.paste(shape, ((canvas - shape.width) // 2, (canvas - shape.height) // 2))
    return square


def monogram(masks: dict[str, Image.Image], canvas: int) -> Image.Image:
    """The two G's interlocked, the second lower and to the right, as a square mask."""
    a, b = masks["grimes"], masks["golf"]
    second = (int(a.width * 0.72), int(a.height * 0.34))
    out = Image.new("L", (second[0] + b.width, second[1] + b.height), 0)
    out.paste(a, (0, 0), a)
    out.paste(ImageChops.lighter(out.crop((*second, second[0] + b.width, second[1] + b.height)), b), second)
    return centred_square(out, canvas)


def radial(size: tuple[int, int], centre: tuple[int, int, int], edge: tuple[int, int, int], reach: float = 1.0, noise: float = 0.0) -> Image.Image:
    """A dark radial background. A touch of noise keeps a gradient this dark from showing bands on a big screen."""
    small = Image.radial_gradient("L").resize(size, Image.BICUBIC)            # 0 at the centre, 255 at the corners
    t = small.point(lambda v: min(255, int(v / reach)))
    image = Image.composite(Image.new("RGB", size, edge), Image.new("RGB", size, centre), t)
    if noise:
        grain = Image.effect_noise(size, noise).convert("L")
        image = ImageChops.add(image, Image.merge("RGB", (grain, grain, grain)), scale=1, offset=-128)
    return image


def make_splash(logo: Image.Image) -> Image.Image:
    size = (1920, 1080)
    image = radial(size, SPLASH_CENTRE, SPLASH_EDGE, reach=0.9, noise=2.5).convert("RGBA")
    width = int(size[0] * 0.56)  # within the middle of the screen, so a 4:3 projector cropping the sides keeps all of it
    scaled = logo.resize((width, round(logo.height * width / logo.width)), Image.LANCZOS)
    image.alpha_composite(scaled, ((size[0] - scaled.width) // 2, (size[1] - scaled.height) // 2))
    return image.convert("RGB")


def make_icon(mark: Image.Image, size: int) -> Image.Image:
    """One icon size. Drawn large and scaled down; the smaller it ends up, the heavier the strokes and the less glow."""
    s = max(size * 4, 512)
    tile_inset = 0 if size <= 24 else round(s * 0.02)
    radius = round(s * (0.16 if size > 24 else 0.12))
    fill = 0.84 if size <= 32 else 0.78
    # Stroke weight added at this size, as a share of the drawing: a little when large, about a pixel's worth at 16.
    weight = {16: 0.070, 20: 0.058, 24: 0.048, 32: 0.038, 40: 0.03, 48: 0.024, 64: 0.016}.get(size, 0.012)

    inner = round(s * fill)
    letters = mark.resize((inner, inner), Image.LANCZOS)
    shape = Image.new("L", (s, s), 0)
    shape.paste(letters, ((s - inner) // 2, (s - inner) // 2))
    grow = max(1, round(s * weight / 2))
    shape = shape.filter(ImageFilter.GaussianBlur(grow)).point(lambda v: 255 if v > 44 else int(v * 255 / 44))

    tile = radial((s, s), SPLASH_CENTRE, SPLASH_EDGE, reach=0.85).convert("RGBA")
    if size >= 32:  # below this the glow only muddies the letters
        glow = shape.filter(ImageFilter.GaussianBlur(s * 0.022)).point(lambda v: int(v * 0.75))
        tile.paste(Image.new("RGBA", (s, s), (*NEON, 255)), (0, 0), glow)
    tile.paste(Image.new("RGBA", (s, s), (*NEON, 255)), (0, 0), shape)

    rounded = Image.new("L", (s, s), 0)
    ImageDraw.Draw(rounded).rounded_rectangle((tile_inset, tile_inset, s - 1 - tile_inset, s - 1 - tile_inset), radius, fill=255)
    tile.putalpha(rounded)
    return tile.resize((size, size), Image.LANCZOS)


def main() -> None:
    UI.mkdir(parents=True, exist_ok=True)
    ICONS.mkdir(parents=True, exist_ok=True)

    logo = make_logo()
    width = 1600
    logo.resize((width, round(logo.height * width / logo.width)), Image.LANCZOS).save(UI / "grimes_golf_logo.png", optimize=True)
    make_splash(logo).save(UI / "splash.png", optimize=True)

    masks = letter_masks()
    pair, single = monogram(masks, 2048), centred_square(masks["golf"], 2048)
    icons = [make_icon(pair if size >= 48 else single, size) for size in ICON_SIZES]
    icons[-1].save(ICONS / "grimes_golf_icon.png", optimize=True)
    # Plain bitmaps at every size: everything that reads .ico files understands them.
    icons[-1].save(ICONS / "grimes_golf.ico", format="ICO", sizes=[(i.width, i.height) for i in icons],
                   append_images=icons[:-1], bitmap_format="bmp")
    print("Logo, splash and icons written.")


if __name__ == "__main__":
    main()
