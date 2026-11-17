"""Builds the mockup's images from files already in the repository.

map.jpg      - the Balkans, cut out of docs/screenshots/ui-aoh/step-04-crisis-1920.jpg
               where no old interface covers the map, scaled to 1920x1080.
minimap.jpg  - a wider cut of the same screenshot, 312x156 (x2 for sharpness).
icons/*.png  - copies of game/gfx/interface/icons (white masks, 128 px).
map_clean.jpg - map.jpg with the old map layer taken off, for the map budget (spec 2.4):
               the dark building squares and the wall of unit counters along the
               Ukrainian front are found and filled from their surroundings; the mockup
               then draws merged counters and the front line itself. Building icons come
               back only in the Economy mode (C) or with B, which show map.jpg.
flags/*.png  - copies of game/gfx/flags, scaled to 96x64.
fonts/*.ttf  - Source Serif 4 (SIL OFL 1.1), copied from the local font folder if found,
               so the owner judges the intended serif and not Georgia.
icons.css    - the same icons as data URIs, so CSS masks work when index.html is
               opened straight from disk (file:// refuses cross-file masks).

Run from the repository root:  python docs/design/interface-2026-11-17/mockup/tools/build_assets.py
"""
import base64, os, shutil, sys
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.getcwd()
OUT = os.path.join(ROOT, "docs/design/interface-2026-11-17/mockup")
SRC = os.path.join(ROOT, "docs/screenshots/ui-aoh/step-04-crisis-1920.jpg")

shot = Image.open(SRC).convert("RGB")
# Main view: the clean rectangle between the old rail, top bar, report and army bar.
main = shot.crop((280, 395, 1290, 963)).resize((1920, 1080), Image.LANCZOS)
main = main.filter(ImageFilter.UnsharpMask(radius=2, percent=60, threshold=2))
main.save(os.path.join(OUT, "img/map.jpg"), quality=88)


def box(a, r):
    """Mean over a (2r+1)^2 window, by integral image (no scipy needed)."""
    p = np.pad(a, ((r + 1, r), (r + 1, r)) + ((0, 0),) * (a.ndim - 2), mode="edge").cumsum(0).cumsum(1)
    k = 2 * r + 1
    return (p[k:, k:] - p[:-k, k:] - p[k:, :-k] + p[:-k, :-k]) / (k * k)

def fill(img, mask, r=5):
    """Fill masked pixels from the outside in (onion peel of local means)."""
    out, known = img.copy(), ~mask
    while not known.all():
        kn = known.astype(np.float64)
        den = box(kn, r)
        est = box(out * kn[..., None], r) / np.maximum(den, 1e-6)[..., None]
        grow = ~known & (den > .25)
        if not grow.any(): grow = ~known & (den > 0)
        out[grow] = est[grow]; known |= grow
    return out

a = np.asarray(main).astype(np.float64)
lum = a @ np.array([.299, .587, .114]); sat = a.max(2) - a.min(2)
black = ((lum < 36) & (sat < 16)).astype(np.float64)
white = ((lum > 150) & (sat < 40)).astype(np.float64)
# building squares: near-black frames with a white glyph inside; a country is never both
bw, ww = box(black, 18), box(white, 18)
centres = (bw > .22) & (ww > .07) & (bw + ww > .66)
# one 78x62 rectangle per square, centred on each cluster of centres
build = np.zeros(centres.shape, bool)
seen = np.zeros(centres.shape, bool)
ys, xs = np.nonzero(centres)
for y0, x0 in zip(ys, xs):
    if seen[y0, x0]: continue
    stack, pts = [(y0, x0)], []
    seen[y0, x0] = True
    while stack:
        y, x = stack.pop(); pts.append((y, x))
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            v, u = y + dy, x + dx
            if 0 <= v < centres.shape[0] and 0 <= u < centres.shape[1] and centres[v, u] and not seen[v, u]:
                seen[v, u] = True; stack.append((v, u))
    cy, cx = np.mean(pts, axis=0).astype(int)
    if 220 < cx < 560 and cy < 100: continue          # the letters of GERMANY, not a square
    build[max(0, cy - 31):cy + 31, max(0, cx - 39):cx + 39] = True
# the counter wall along the Ukrainian front: east of Ukraine, anything that is neither
# Ukraine's yellow nor Russia's green, except the big country label and Dnipro
region = Image.new("L", (1920, 1080), 0); d = ImageDraw.Draw(region)
d.polygon([(1236, 88), (1742, 88), (1742, 470), (1440, 612), (1328, 612), (1236, 560)], fill=255)
d.polygon([(1092, 232), (1405, 232), (1574, 326), (1574, 402), (1400, 384), (1092, 304)], fill=0)
d.rectangle([1412, 402, 1534, 434], fill=0)          # Dnipro
d.rectangle([1196, 520, 1346, 556], fill=0)          # Chisinau
d.rectangle([1688, 138, 1842, 172], fill=0)          # Voronezh
yel, grn = np.array([139, 139, 67.]), np.array([51, 71, 59.])
far = (np.linalg.norm(a - yel, axis=2) > 38) & (np.linalg.norm(a - grn, axis=2) > 38)
units = (np.asarray(region) > 0) & (box(far.astype(np.float64), 3) > 0)
# what the two rules above miss: two counters inside the country label, four squares
extra = Image.new("L", (1920, 1080), 0); d = ImageDraw.Draw(extra)
for r in ([1254, 268, 1318, 306], [1356, 236, 1420, 272], [120, 260, 196, 320], [428, 758, 478, 812],
          [938, 1044, 1018, 1080], [1876, 0, 1920, 22], [1840, 806, 1920, 870]):
    d.rectangle(r, fill=255)
mask = build | units | (np.asarray(extra) > 0)
clean = Image.fromarray(np.clip(fill(a, mask), 0, 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(1.2))
m = Image.fromarray((box(mask.astype(np.float64), 1) > 0).astype(np.uint8) * 255)
clean = Image.composite(clean, main, m)
clean.save(os.path.join(OUT, "img/map_clean.jpg"), quality=88)

mini = shot.crop((140, 260, 1556, 968)).resize((624, 312), Image.LANCZOS)
mini.save(os.path.join(OUT, "img/minimap.jpg"), quality=88)

ICONS = """treasury gdp public_debt inflation stability manpower agenda military air navy
nation state_affairs government economy trade industry research diplomacy wars ledger search
alliance coins terrain relief population war battle alert_construction alert_battle alert_event
alert_supply alert_peace good_nickel good_lithium debt_default decisions decision region province
army_halt army_advance army_defend army_withdraw army_spearhead army_reserve plan_front plan_attack
plan_prepare plan_execute hq_detach hq_new hq_whole cohesion supply strength organisation
building_barracks building_civilianindustry building_infrastructure building_university
building_hospital building_powerplant building_railnetwork building_armsindustry taxes welfare
income balance credit_confidence unrest laws world calendar clock settings close back select_all
victory treaty living literacy urbanisation energy equipment artillery faith""".split()
css = ["/* generated by tools/build_assets.py from game/gfx/interface/icons - do not edit */"]
for name in ICONS:
    src = os.path.join(ROOT, f"game/gfx/interface/icons/icon_{name}.png")
    if not os.path.exists(src):
        print("missing icon", name, file=sys.stderr); continue
    dst = os.path.join(OUT, f"img/icons/{name}.png")
    shutil.copyfile(src, dst)
    im = Image.open(src).convert("RGBA").resize((64, 64), Image.LANCZOS)
    tmp = dst + ".64.png"; im.save(tmp, optimize=True)
    data = base64.b64encode(open(tmp, "rb").read()).decode()
    os.remove(tmp)
    css.append(f".i-{name.replace('_','-')}{{--m:url(data:image/png;base64,{data})}}")
open(os.path.join(OUT, "icons.css"), "w").write("\n".join(css) + "\n")

for code in "srb hrv bih hun rou bgr kos mne mkd alb ukr rus".split():
    src = os.path.join(ROOT, f"game/gfx/flags/flag_{code}.png")
    Image.open(src).convert("RGB").resize((96, 64), Image.LANCZOS).save(os.path.join(OUT, f"img/flags/{code}.png"))
# Source Serif 4 (OFL): only copied, never downloaded
os.makedirs(os.path.join(OUT, "fonts"), exist_ok=True)
for d in [os.path.join(os.environ.get("LOCALAPPDATA", ""), "Microsoft/Windows/Fonts"), "C:/Windows/Fonts"]:
    for face in ("Regular", "SemiBold", "Bold"):
        src = os.path.join(d, f"SourceSerif4-{face}.ttf")
        if os.path.exists(src):
            shutil.copyfile(src, os.path.join(OUT, f"fonts/SourceSerif4-{face}.ttf"))
print("ok")
