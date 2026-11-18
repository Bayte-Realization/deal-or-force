"""Draws the mockup's map from the game's own map data - no screenshot, nothing rubbed out.

Revision 4 (19 November): the art director found the old map was a screenshot with the old
interface wiped off it (smudges over UKRAINE, Bosnia, Hungary, Sofia), English names lying on
each other, and selection outlines that were hand-drawn polygons. This script replaces it:

  game/map_data/provinces.png + definition.csv   provinces, owner of each
  game/map_data/source_control.png              who holds the ground (occupation, the front)
  game/map_data/heightmap.png, bathymetry.png   relief and sea depth
  game/map_data/rivers.png, lakes.png           water
  game/common/countries/00_countries.json       country colours
  game/map_data/cities/00_cities.json           city places
  game/localization/russian/*.json              Russian names of countries and cities

Writes into mockup/:
  img/map_clean.jpg   the political map at rest, no names (the interface draws over it)
  img/map.jpg         the same with building marks (Economy mode, or B)
  img/map_labels.png  the names, on their own layer: crisis hatching and selection go UNDER them
  img/minimap.jpg     a wider view of Europe for the minimap (x2)
  mapgeo.js           the projection, outlines of Serbia, Kosovo and Sumadija traced from the
                      province map, the front line traced from the control map, city places,
                      and the box of every name (tokens are moved off them, the census counts it)

The projection is plate carree with the longitude scaled by cos 44 deg: the provinces raster is
already plate carree, so the map is the raster's crop resized - every border is where the data
puts it. Province edges are smoothed by resizing each province's mask bicubically and taking the
strongest at each pixel, so borders are curves, not 4-pixel stairs.

Run from the repository root:  python docs/design/interface-2026-11-17/mockup/tools/render_map.py
Pure numpy + Pillow.
"""
import glob, json, math, os, re, sys
import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageFilter

Image.MAX_IMAGE_PIXELS = None
ROOT = os.getcwd()
MD = os.path.join(ROOT, "game/map_data")
OUT = os.path.join(ROOT, "docs/design/interface-2026-11-17/mockup")
FONTS = os.path.join(OUT, "fonts")
W, H = 1920, 1080
SRC = 16                                   # source pixels per degree (5760 / 360)
KY = 72.0                                  # target pixels per degree of latitude
KX = KY * math.cos(math.radians(44))       # ... of longitude
LON0 = 20.9 - 740 / KX                     # Serbia's middle (20.9 E, 44 N) at x 740, y 600
LAT0 = 44.0 + 600 / KY

# the screen's plates at rest (1080): names are not placed under them
UI = [(0, 0, 1170, 74), (0, 0, 70, 796), (1468, 0, 1920, 96), (1572, 92, 1920, 300), (0, 1000, 1260, 1080), (1596, 1010, 1920, 1080)]

def proj(lon, lat):
    return ((lon - LON0) * KX, (LAT0 - lat) * KY)

def load_json(path):
    txt = open(path, encoding="utf-8").read()
    txt = re.sub(r"^\s*//.*$", "", txt, flags=re.M)
    return json.loads(txt)

def ru_strings():
    out = {}
    for f in glob.glob(os.path.join(ROOT, "game/localization/russian/*.json")):
        try:
            d = load_json(f)
        except Exception:
            continue
        if isinstance(d, dict):
            out.update({k: v for k, v in d.items() if isinstance(v, str)})
    return out

def hexrgb(h):
    h = h.lstrip("#"); return np.array([int(h[i:i + 2], 16) for i in (0, 2, 4)], dtype=np.float32)

# ---------------------------------------------------------------- data
tables = load_json(os.path.join(MD, "source_tables.json"))
TAG = {int(c[0]): (c[9] if c[1] in ("-99", "") else c[1]) for c in tables["countries"]}
COUNTRIES = load_json(os.path.join(ROOT, "game/common/countries/00_countries.json"))
RU = ru_strings()

STATE_C = {int(s_[0]): int(s_[2]) for s_ in tables["states"] if s_[2].lstrip("-").isdigit()}
prov_of = {}                                # rgb key -> (province id, nation index, country of its state)
for line in open(os.path.join(MD, "definition.csv"), encoding="utf-8").read().splitlines()[1:]:
    p = line.split(";")
    if len(p) >= 6:
        prov_of[int(p[1]) + 256 * int(p[2]) + 65536 * int(p[3])] = (int(p[0]), int(p[5]), STATE_C.get(int(p[4]), -1))

def crop_box(lon0, lat0, w, h, kx, ky, margin=4):
    sx0, sy0 = (lon0 + 180) * SRC, (90 - lat0) * SRC
    sw, sh = w / kx * SRC, h / ky * SRC
    ix0, iy0 = int(math.floor(sx0)) - margin, int(math.floor(sy0)) - margin
    ix1, iy1 = int(math.ceil(sx0 + sw)) + margin, int(math.ceil(sy0 + sh)) + margin
    return (sx0, sy0, sw, sh), (ix0, iy0, ix1, iy1)

def read_crop(name, box, mode="RGB"):
    im = Image.open(os.path.join(MD, name)).convert(mode)
    return np.array(im.crop(box))

def rgbkey(a):
    a = a.astype(np.int64); return a[..., 0] + 256 * a[..., 1] + 65536 * a[..., 2]

(sx0, sy0, sw, sh), CB = crop_box(LON0, LAT0, W, H, KX, KY)
fx, fy = W / sw, H / sh                       # target px per source px
ox, oy = sx0 - CB[0], sy0 - CB[1]             # where the view starts inside the crop

keys = rgbkey(read_crop("provinces.png", CB))
lakes = read_crop("lakes.png", CB)
lake = (lakes[..., 2] > 200) & (lakes[..., 0] < 100)
uk, inv = np.unique(keys, return_inverse=True)
inv = inv.reshape(keys.shape)
nat = np.array([prov_of.get(int(k), (-1, -1, -1))[1] if k else -1 for k in uk])
stc = np.array([prov_of.get(int(k), (-1, -1, -1))[2] if k else -1 for k in uk])
label = inv.copy()                            # local label = index into uk; water = len(uk)
WATER = len(uk)
label[(keys == 0) | lake] = WATER
ctl_raw = rgbkey(read_crop("source_control.png", CB)) % 65536

# who holds each province: the most common control value inside it
ctl_of = np.full(len(uk) + 1, -1)
for i in range(len(uk)):
    v = ctl_raw[label == i]
    if v.size:
        vals, cnt = np.unique(v, return_counts=True); ctl_of[i] = vals[cnt.argmax()]

# ---------------------------------------------------------------- smooth province map at target size
def to_target(x, y):  # crop source px -> target px
    return (x - ox) * fx, (y - oy) * fy

best = np.full((H, W), -1e9, dtype=np.float32)
P = np.full((H, W), WATER, dtype=np.int32)
for v in range(len(uk) + 1):
    ys, xs = np.nonzero(label == v)
    if not len(xs):
        continue
    a0, a1 = max(xs.min() - 3, 0), min(xs.max() + 4, label.shape[1])
    b0, b1 = max(ys.min() - 3, 0), min(ys.max() + 4, label.shape[0])
    X0, Y0 = max(int(math.ceil(to_target(a0, 0)[0])), 0), max(int(math.ceil(to_target(0, b0)[1])), 0)
    X1, Y1 = min(int(math.floor(to_target(a1, 0)[0])), W), min(int(math.floor(to_target(0, b1)[1])), H)
    if X1 <= X0 or Y1 <= Y0:
        continue
    # a slight blur at the source size first, so the resized edge is a curve and not rounded stairs
    m = Image.fromarray(((label[b0:b1, a0:a1] == v) * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(.85))
    box = (max(ox + X0 / fx - a0, 0), max(oy + Y0 / fy - b0, 0), min(ox + X1 / fx - a0, a1 - a0), min(oy + Y1 / fy - b0, b1 - b0))
    up = np.asarray(m.resize((X1 - X0, Y1 - Y0), Image.BICUBIC, box=box), dtype=np.float32)
    sub = best[Y0:Y1, X0:X1]
    win = up > sub
    sub[win] = up[win]
    P[Y0:Y1, X0:X1][win] = v

land = P != WATER
NAT = np.where(land, np.append(nat, -1)[P], -1)
CTL = np.where(land, ctl_of[P], -1)
STC = np.where(land, np.append(stc, -1)[P], -1)
# Ukrainian regions the scenario hands to Russia are drawn as Ukraine held by Russia: the owner's colour
# with the holder's stripes, as Paradox maps show occupation; the front is the edge of that ground.
UKR_I = next(i for i, t in TAG.items() if t == "UKR"); RUS_I = next(i for i, t in TAG.items() if t == "RUS")
taken = land & (STC == UKR_I) & (NAT == RUS_I)
NAT[taken] = UKR_I; CTL[taken] = RUS_I

# ---------------------------------------------------------------- colours
PAPER = hexrgb("#d6cab0")
def nation_rgb(n):
    tag = TAG.get(int(n))
    c = COUNTRIES.get(tag, {}).get("color") if tag else None
    base = hexrgb(c) if c else np.array([150, 140, 120], np.float32)
    # a printed map: the country's colour, cut with paper, a touch darker - names are written on it in ink
    return (base * .56 + PAPER * .44) * .92

nats = [int(n) for n in np.unique(NAT) if n >= 0]
NRGB = {n: nation_rgb(n) for n in nats}
img = np.zeros((H, W, 3), np.float32)
for n in nats:
    img[NAT == n] = NRGB[n]

# relief: hillshade from the heightmap, light from the top left
hm = Image.fromarray(read_crop("heightmap.png", CB, "L"))
hbox = (ox, oy, ox + sw, oy + sh)
hgt = np.asarray(hm.resize((W, H), Image.BICUBIC, box=hbox), np.float32)
def boxblur(a, r):
    for _ in range(3):
        p = np.pad(a, ((r + 1, r), (r + 1, r)), mode="edge").cumsum(0).cumsum(1)
        k = 2 * r + 1
        a = (p[k:, k:] - p[:-k, k:] - p[k:, :-k] + p[:-k, :-k]) / (k * k)
    return a
hgt = boxblur(hgt, 1)
gy, gx = np.gradient(hgt)
shade = np.clip(-(gx * -1 + gy * -1) * 0.055, -1, 1)          # brighter on slopes facing the top left
img[land] *= (1 + 0.20 * shade[land])[:, None]
img[land] *= (1 - np.clip((hgt[land] - 110) / 900, 0, .08))[:, None]   # high ground a shade darker

# sea: depth from bathymetry, lighter near the coast
bat = Image.fromarray(read_crop("bathymetry.png", CB, "L"))
dep = np.asarray(bat.resize((W, H), Image.BICUBIC, box=hbox), np.float32) / 255
shallow, deep = hexrgb("#55767f"), hexrgb("#24394a")
t = np.clip(dep, 0, 1)[..., None]
sea = shallow * (1 - t) + deep * t
near = np.asarray(Image.fromarray((land * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(9)), np.float32) / 255
sea = sea * (1 - .22 * near[..., None]) + hexrgb("#7d9893") * (.22 * near[..., None])
img[~land] = sea[~land]

# occupation: stripes of the holder's colour over the owner's (as Paradox maps show it)
occ = land & (CTL >= 0) & (CTL != NAT)
yy, xx = np.mgrid[0:H, 0:W]
stripe = ((xx + yy) // 7) % 2 == 0
for c in np.unique(CTL[occ]):
    m = occ & (CTL == c) & stripe
    img[m] = img[m] * .45 + NRGB.get(int(c), nation_rgb(int(c))) * .55

# rivers: the two larger classes, as thin water lines
riv = read_crop("rivers.png", CB)
rmask = ((riv[..., 2] > 200) & (riv[..., 1] > 120)).astype(np.uint8) * 255
rup = np.asarray(Image.fromarray(rmask).filter(ImageFilter.GaussianBlur(.8)).resize((W, H), Image.BICUBIC, box=hbox), np.float32) / 255
ridge = np.asarray(Image.fromarray((rup * 255).astype(np.uint8)).filter(ImageFilter.MaxFilter(5)), np.float32) / 255
rcore = ((rup >= ridge * .96) & (rup > .3)).astype(np.uint8) * 255   # the river's middle line only
rline = np.clip(np.asarray(Image.fromarray(rcore).filter(ImageFilter.GaussianBlur(.7)), np.float32) / 255 * 1.6, 0, 1) * land
img = img * (1 - .6 * rline[..., None]) + hexrgb("#4d6f7a") * (.6 * rline[..., None])

# borders: province edges faint, country edges in ink, the coast a fine dark line
def edges(A):
    e = np.zeros(A.shape, bool)
    e[:, 1:] |= A[:, 1:] != A[:, :-1]; e[1:, :] |= A[1:, :] != A[:-1, :]
    return e
def soft(m, r):
    return np.asarray(Image.fromarray((m * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(r)), np.float32) / 255
pe = soft(edges(P) & land, .55)
img = img * (1 - .16 * pe[..., None])
ce_hard = edges(np.where(land, NAT, -9)) & land
ce = np.clip(soft(ce_hard, .8) * 2.0, 0, 1)
img = img * (1 - .62 * ce[..., None]) + hexrgb("#1c1610") * (.0 * ce[..., None])
coast = np.clip(soft(edges(land.astype(np.int8)), .6) * 1.6, 0, 1)
img = img * (1 - .45 * coast[..., None])

base = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))
base.save(os.path.join(OUT, "img/map_clean.jpg"), quality=90)

# ---------------------------------------------------------------- outlines traced from the province map
def trace(mask, step=2):
    """Outer boundary of the largest blob of a boolean mask as a polygon (Moore neighbour tracing on a
    half-size grid, then Douglas-Peucker). Returns [[x, y], ...] in target pixels."""
    m = mask[::step, ::step]
    h, w = m.shape
    # largest component by flood fill
    lab = np.zeros(m.shape, np.int32); cur = 0; sizes = {}
    for y0, x0 in zip(*np.nonzero(m)):
        if lab[y0, x0]:
            continue
        cur += 1; stack = [(y0, x0)]; lab[y0, x0] = cur; n = 0
        while stack:
            y, x = stack.pop(); n += 1
            for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                yy2, xx2 = y + dy, x + dx
                if 0 <= yy2 < h and 0 <= xx2 < w and m[yy2, xx2] and not lab[yy2, xx2]:
                    lab[yy2, xx2] = cur; stack.append((yy2, xx2))
        sizes[cur] = n
    if not sizes:
        return []
    big = lab == max(sizes, key=sizes.get)
    ys, xs = np.nonzero(big)
    i = np.lexsort((xs, ys))[0]
    start = (ys[i], xs[i])
    nb = [(-1, 0), (-1, 1), (0, 1), (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1)]
    inside = lambda y, x: 0 <= y < h and 0 <= x < w and big[y, x]
    pts, cur_p, back = [start], start, 6
    for _ in range(200000):
        found = False
        for k in range(8):
            d = (back + 1 + k) % 8
            ny, nx = cur_p[0] + nb[d][0], cur_p[1] + nb[d][1]
            if inside(ny, nx):
                back = (d + 4) % 8; cur_p = (ny, nx); found = True; break
        if not found or cur_p == start:
            break
        pts.append(cur_p)
    poly = [((x + .5) * step, (y + .5) * step) for y, x in pts]
    return [[round(x, 1), round(y, 1)] for x, y in dp(poly, 1.2)]

def dp(pts, eps):
    if len(pts) < 3:
        return pts
    a, b = np.array(pts[0]), np.array(pts[-1])
    d = b - a; L = np.hypot(*d) or 1
    arr = np.array(pts)
    dist = np.abs(d[0] * (arr[:, 1] - a[1]) - d[1] * (arr[:, 0] - a[0])) / L
    i = int(dist.argmax())
    if dist[i] > eps:
        return dp(pts[:i + 1], eps)[:-1] + dp(pts[i:], eps)
    return [pts[0], pts[-1]]

nid = {t: i for i, t in TAG.items()}
SRB, KOS, UKR, RUS = nid["SRB"], nid["KOS"], nid["UKR"], nid["RUS"]
serbia = trace(NAT == SRB)
kosovo = trace(NAT == KOS)
# Sumadija: the seven Serbian provinces nearest Kragujevac (the game has no state of that name)
kx_, ky_ = proj(20.92, 44.02)
cands = []
for v in range(len(uk)):
    if nat[v] != SRB:
        continue
    ys, xs = np.nonzero(P == v)
    if len(xs) > 30:
        cands.append((math.hypot(xs.mean() - kx_, ys.mean() - ky_), v))
cands.sort()
sumadija_ids = [v for _, v in cands[:7]]
sumadija = trace(np.isin(P, sumadija_ids))

# the front: where Russian-held Ukrainian ground meets Ukrainian-held ground
held = taken | (land & (NAT == UKR) & (CTL == RUS))
free = land & (CTL == UKR)
near_free = soft(free, 3) > .05
front_pts = []
for x, y in trace(held, 2):
    if 0 <= int(y) < H and 0 <= int(x) < W and near_free[int(y), int(x)]:
        front_pts.append([x, y])
# keep the longest run of consecutive front points (the trace walks the whole occupied area)
runs, run = [], []
for p in front_pts:
    if run and math.hypot(p[0] - run[-1][0], p[1] - run[-1][1]) > 40:
        runs.append(run); run = []
    run.append(p)
runs.append(run)
front = max(runs, key=len) if runs else []

# ---------------------------------------------------------------- names (their own layer)
def font(size, w="SemiBold"):
    return ImageFont.truetype(os.path.join(FONTS, f"SourceSerif4-{w}.ttf"), size)

INK = (30, 23, 15)
lab_img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
BOXES = []                                     # [x1, y1, x2, y2, name]

def free_box(b, pad=4):
    x1, y1, x2, y2 = b
    if x1 < 4 or y1 < 4 or x2 > W - 4 or y2 > H - 4:
        return False
    for u in UI:
        if x1 < u[2] and u[0] < x2 and y1 < u[3] and u[1] < y2:
            return False
    for o in BOXES:
        if x1 < o[2] + pad and o[0] - pad < x2 and y1 < o[3] + pad and o[1] - pad < y2:
            return False
    return True

def spaced(text, f, track):
    """Draws letter-spaced text onto its own RGBA canvas; returns the canvas."""
    widths = [f.getlength(ch) for ch in text]
    tw = sum(widths) + track * (len(text) - 1)
    asc, desc = f.getmetrics()
    can = Image.new("RGBA", (int(tw) + 8, asc + desc + 8), (0, 0, 0, 0))
    d = ImageDraw.Draw(can); x = 4
    for ch, cw in zip(text, widths):
        d.text((x, 4), ch, font=f, fill=INK + (190,)); x += cw + track
    return can

MAXSIZE = {"SRB": 26, "KOS": 15, "MNE": 15, "MKD": 15, "ALB": 15, "SVN": 15, "MDA": 15}   # the player's towns must stay readable under the name
COUNTRY_RU = {"SRB": ["СЕРБИЯ"], "MKD": ["СЕВ. МАКЕДОНИЯ", "МАКЕДОНИЯ"], "BIH": ["БОСНИЯ И ГЕРЦЕГОВИНА", "БОСНИЯ"],
              "MNE": ["ЧЕРНОГОРИЯ"], "MDA": ["МОЛДАВИЯ"], "SVN": ["СЛОВЕНИЯ"], "KOS": ["КОСОВО"]}

def place_country(n):
    tag = TAG.get(n)
    names = COUNTRY_RU.get(tag) or [RU.get(f"country.{tag}.short_name") or RU.get(f"country.{tag}.name") or ""]
    names = [s.upper() for s in names if s]
    if not names:
        return
    m = (NAT == n)
    for u in UI:
        m[u[1]:u[3], u[0]:u[2]] = False
    if m.sum() < 1500:
        return
    # inner distance by erosion on a quarter grid
    q = m[::4, ::4].copy(); dist = np.zeros(q.shape, np.int32); cur = q.copy(); k = 0
    while cur.any() and k < 80:
        k += 1; dist[cur] = k
        e = cur.copy()
        e[1:, :] &= cur[:-1, :]; e[:-1, :] &= cur[1:, :]; e[:, 1:] &= cur[:, :-1]; e[:, :-1] &= cur[:, 1:]
        cur = e
    ys, xs = np.nonzero(m[::2, ::2]); ys, xs = ys * 2.0, xs * 2.0
    cx, cy = xs.mean(), ys.mean()
    cov = np.cov(np.vstack([xs - cx, ys - cy]))
    ev, evec = np.linalg.eigh(cov)
    ang = math.degrees(math.atan2(evec[1, 1], evec[0, 1]))
    if ang > 90: ang -= 180
    if ang < -90: ang += 180
    elong = math.sqrt(ev[1] / max(ev[0], 1e-6))
    ang = max(-55, min(55, ang)) if elong > 1.6 else 0
    # anchor candidates: the deepest inner points
    dm = dist.max()
    yy3, xx3 = np.nonzero(dist >= max(2, dm * .55))
    cand = sorted(((x3 * 4 + 2, y3 * 4 + 2) for y3, x3 in zip(yy3, xx3)), key=lambda c: (c[0] - cx) ** 2 + (c[1] - cy) ** 2)[::3][:60]
    for name in names:
        for size in range(MAXSIZE.get(tag, 46), 11, -2):
            f = font(size); track = size * .28
            tw = sum(f.getlength(ch) for ch in name) + track * (len(name) - 1); th = size * .78
            ca, sa = math.cos(math.radians(ang)), math.sin(math.radians(ang))
            for ax, ay in cand:
                ok = True
                for u in np.linspace(-.5, .5, 9):
                    for v in (-.5, 0, .5):
                        px_ = ax + ca * u * tw - sa * v * th * 1.3
                        py_ = ay + sa * u * tw + ca * v * th * 1.3
                        if not (0 <= int(py_) < H and 0 <= int(px_) < W and m[int(py_), int(px_)]):
                            ok = False; break
                    if not ok: break
                if not ok:
                    continue
                hw = abs(ca) * tw / 2 + abs(sa) * th / 2; hh = abs(sa) * tw / 2 + abs(ca) * th / 2
                b = (ax - hw, ay - hh, ax + hw, ay + hh)
                if not free_box(b, 2):
                    continue
                can = spaced(name, f, track).rotate(-ang, resample=Image.BICUBIC, expand=True)
                lab_img.alpha_composite(can, (int(ax - can.width / 2), int(ay - can.height / 2 + size * .08)))
                BOXES.append([round(b[0]), round(b[1]), round(b[2]), round(b[3]), name])
                return


# cities: capitals first, then the places the mockup talks about, then the big ones
cities = load_json(os.path.join(MD, "cities/00_cities.json"))
caps = {v.get("capital_city") for v in COUNTRIES.values() if isinstance(v, dict)}
WANT = ["belgrade_srb", "nis_srb", "kragujevac_srb", "novi_sad_srb", "pristina_kos", "kraljevo_srb", "zagreb_hrv", "sarajevo_bih",
        "budapest_hun", "bucharest_rou", "sofia_bgr", "podgorica_mne", "tirana_alb", "skopje_mkd", "vienna_aut", "bratislava_svk",
        "ljubljana_svn", "chisinau_mda", "kyiv_ukr", "odesa_ukr", "dnipro_ukr", "kharkiv_ukr", "lviv_ukr", "zaporizhzhia_ukr",
        "donetsk_ukr", "kherson_ukr", "sevastopol_ukr", "simferopol_ukr", "istanbul_tur", "ankara_tur", "athens_grc", "thessaloniki_grc",
        "rome_ita", "naples_ita", "krakow_pol", "warsaw_pol", "prague_cze", "munich_deu", "rostov_rus", "krasnodar_rus",
        "varna_bgr", "constanta_rou", "cluj_napoca_rou", "split_hrv", "izmir_tur", "bursa_tur"]
CITY = {}
d = ImageDraw.Draw(lab_img)
fc = font(15, "Regular"); fcap = font(16, "SemiBold")
FIRST = 16                                     # Serbia's own towns and the capitals around it come before country names
DOTS = {}
def city_dot(key):
    """First pass: the town's mark is reserved, so country names go round it."""
    c = cities.get(key)
    if not c:
        return
    x, y = proj(c["longitude"], c["latitude"])
    name = RU.get(f"city.{key}.name") or c["name"]
    CITY[name] = [round(x, 1), round(y, 1)]
    if not (8 < x < W - 8 and 8 < y < H - 8):
        return
    r = 5 if key in caps else 4
    dot = (x - r, y - r, x + r, y + r)
    if free_box(dot, 1):
        BOXES.append([round(v) for v in dot] + ["·" + name]); DOTS[key] = (x, y, name, len(BOXES) - 1)

def city_name(key):
    """Last pass: the name beside its mark, wherever there is room; no room - no mark."""
    if key not in DOTS:
        return
    x, y, name, bi = DOTS[key]
    cap = key in caps
    f = fcap if cap else fc
    tw = f.getlength(name); asc, desc = f.getmetrics()
    own = BOXES[bi]; BOXES[bi] = [-99, -99, -99, -99, own[4]]
    for bx, by in ((x + 7, y - asc * .62), (x - 7 - tw, y - asc * .62), (x - tw / 2, y - asc - 6), (x - tw / 2, y + 6)):
        b = (bx - 2, by + 2, bx + tw + 2, by + asc + 1)
        if free_box(b, 3):
            # a mark in ink with a paper ring, the name in ink with a thin paper halo, as a printed map does it
            if cap:
                d.rectangle((x - 4, y - 4, x + 4, y + 4), fill=(232, 222, 196, 235)); d.rectangle((x - 2.6, y - 2.6, x + 2.6, y + 2.6), fill=INK + (240,))
            else:
                d.ellipse((x - 3.6, y - 3.6, x + 3.6, y + 3.6), fill=(232, 222, 196, 220)); d.ellipse((x - 2.2, y - 2.2, x + 2.2, y + 2.2), fill=INK + (235,))
            d.text((bx, by), name, font=f, fill=INK + (235,), stroke_width=2, stroke_fill=(226, 216, 190, 150))
            BOXES[bi] = own
            BOXES.append([round(b[0]), round(b[1]), round(b[2]), round(b[3]), name])
            return
    BOXES.pop(bi) if bi == len(BOXES) - 1 else None

# the player's country is named first, then its towns and the capitals around it, then the other
# countries (largest first, so big names get the room), then the remaining towns
for key in WANT[:FIRST]:
    city_dot(key)
place_country(SRB)
for key in WANT[:FIRST]:
    city_name(key)
areas = sorted(((int((NAT == n).sum()), n) for n in nats if n != SRB), reverse=True)
for _, n in areas:
    place_country(n)
for key in WANT[FIRST:]:
    city_dot(key); city_name(key)
BOXES[:] = [b for b in BOXES if b[0] != -99]

# seas, in a quiet paper-blue
fs = font(18, "Regular")
for name, lon, lat, ang in (("Ч Ё Р Н О Е   М О Р Е", 34.2, 43.2, 0), ("А Д Р И А Т И Ч Е С К О Е   М О Р Е", 16.2, 42.55, -33)):
    x, y = proj(lon, lat)
    can = Image.new("RGBA", (int(fs.getlength(name)) + 8, 30), (0, 0, 0, 0))
    ImageDraw.Draw(can).text((4, 4), name, font=fs, fill=(196, 208, 204, 150))
    can = can.rotate(-ang, resample=Image.BICUBIC, expand=True)
    b = (x - can.width / 2, y - can.height / 2, x + can.width / 2, y + can.height / 2)
    if free_box(b, 2):
        lab_img.alpha_composite(can, (int(b[0]), int(b[1])))
        BOXES.append([round(v) for v in b] + [name.replace(" ", "")])

lab_img.save(os.path.join(OUT, "img/map_labels.png"), optimize=True)

# building marks (Economy mode, B): a small ink square with a paper roof at towns with industry
bld = base.copy(); bd = ImageDraw.Draw(bld)
for key in ["belgrade_srb", "nis_srb", "kragujevac_srb", "novi_sad_srb", "zagreb_hrv", "budapest_hun", "bucharest_rou", "sofia_bgr", "sarajevo_bih",
            "skopje_mkd", "timisoara_rou", "cluj_napoca_rou", "dnipro_ukr", "kharkiv_ukr", "odesa_ukr", "kyiv_ukr", "istanbul_tur", "thessaloniki_grc"]:
    c = cities.get(key)
    if not c:
        continue
    x, y = proj(c["longitude"], c["latitude"]); x += 14; y += 12
    bd.rectangle((x - 7, y - 7, x + 7, y + 7), fill=(34, 28, 22), outline=(12, 10, 8))
    bd.polygon([(x - 4, y + 4), (x - 4, y - 1), (x - 1, y - 3), (x - 1, y), (x + 2, y - 2), (x + 2, y), (x + 4, y - 1), (x + 4, y + 4)], fill=(222, 210, 180))
bld.save(os.path.join(OUT, "img/map.jpg"), quality=90)

# ---------------------------------------------------------------- minimap: Europe, plain colours (x2)
MW, MH = 624, 312
MLON0, MLAT0, MK = -12.0, 63.0, 13.0
MKX = MK * math.cos(math.radians(46))
(msx0, msy0, msw, msh), MCB = crop_box(MLON0, MLAT0, MW, MH, MKX, MK)
mkeys = rgbkey(read_crop("provinces.png", MCB))
mu, minv = np.unique(mkeys, return_inverse=True)
mnat = np.array([prov_of.get(int(k), (-1, -1, -1))[1] if k else -1 for k in mu])[minv.reshape(mkeys.shape)]
mimg = np.zeros(mnat.shape + (3,), np.float32); mimg[:] = hexrgb("#2b4252")
for n in np.unique(mnat):
    if n >= 0:
        mimg[mnat == n] = nation_rgb(int(n))
me = edges(mnat) & (mnat >= 0)
mimg[me] *= .55
mini = Image.fromarray(mimg.astype(np.uint8)).resize((MW, MH), Image.LANCZOS, box=(msx0 - MCB[0], msy0 - MCB[1], msx0 - MCB[0] + msw, msy0 - MCB[1] + msh))
mini.save(os.path.join(OUT, "img/minimap.jpg"), quality=90)
# the main view inside the minimap, as fractions
vx0 = ((LON0 - MLON0) * MKX) / MW; vy0 = ((MLAT0 - LAT0) * MK) / MH
vw = (W / KX * MKX) / MW; vh = (H / KY * MK) / MH

# ---------------------------------------------------------------- mapgeo.js
def js(o):
    return json.dumps(o, ensure_ascii=False, separators=(",", ":"))
with open(os.path.join(OUT, "mapgeo.js"), "w", encoding="utf-8") as fjs:
    fjs.write("// Written by tools/render_map.py from game/map_data - do not edit by hand.\n")
    fjs.write("// 1080-basis pixels of img/map_clean.jpg. Outlines are traced from the province map, the front from the control map.\n")
    fjs.write(f"const GEO = {js({'lon0': round(LON0, 5), 'lat0': round(LAT0, 5), 'kx': round(KX, 5), 'ky': KY})};\n")
    fjs.write("const proj = (lon, lat) => [(lon - GEO.lon0) * GEO.kx, (GEO.lat0 - lat) * GEO.ky];\n")
    fjs.write(f"const SERBIA = {js(serbia)};\n")
    fjs.write(f"const KOSOVO = {js(kosovo)};\n")
    fjs.write(f"const SUMADIJA = {js(sumadija)};\n")
    fjs.write(f"const FRONT_LINE = {js(front)};\n")
    fjs.write(f"const CITY = {js(CITY)};\n")
    fjs.write(f"const LABEL_BOXES = {js(BOXES)};\n")
    fjs.write(f"const MINI_VIEW = {js([round(vx0, 4), round(vy0, 4), round(vw, 4), round(vh, 4)])};\n")
print("ok", len(BOXES), "names;", len(serbia), len(kosovo), len(sumadija), len(front), "points; sumadija provinces", len(sumadija_ids))
