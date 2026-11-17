"""Turns the PNGs from tools/capture.sh into the shots/ folder.

  python tools/finish_shots.py <png-dir> <shots-dir> [old-state-1.jpg]

- state-*.png  -> state-*.jpg (quality 88), the names the spec links to;
- flag-frames.jpg - the corner and the panel header with five flags of different proportions
  (Serbia 2:3, Switzerland 1:1, Nepal's own outline, Qatar 11:28, Vatican 1:1) and Qatar at 1280x720;
- compare-before-after.jpg - the old state-1 on the left, the new one on the right (if the old file
  is given; take it from git: git show <rev>:docs/design/.../shots/state-1.jpg > old.jpg).
Pure Pillow, nothing downloaded.
"""
import glob, os, sys
from PIL import Image, ImageDraw, ImageFont

src, out = sys.argv[1], sys.argv[2]
old = sys.argv[3] if len(sys.argv) > 3 else None
os.makedirs(out, exist_ok=True)


def font(size, serif=False):
    here = os.path.dirname(os.path.abspath(__file__))
    for f in ([os.path.join(here, "..", "fonts", "SourceSerif4-SemiBold.ttf")] if serif else []) + ["C:/Windows/Fonts/segoeui.ttf", "C:/Windows/Fonts/arial.ttf"]:
        if os.path.exists(f):
            return ImageFont.truetype(f, size)
    return ImageFont.load_default()


for p in sorted(glob.glob(os.path.join(src, "state-*.png"))):
    Image.open(p).convert("RGB").save(os.path.join(out, os.path.basename(p)[:-4] + ".jpg"), quality=88)

# the flag frame sheet
rows = [("srb", "Сербия 2:3", "state-1.png", "state-2.png"), ("che", "Швейцария 1:1", "flag-che.png", "flagpanel-che.png"),
        ("npl", "Непал, свой силуэт", "flag-npl.png", "flagpanel-npl.png"), ("qat", "Катар 11:28", "flag-qat.png", "flagpanel-qat.png"),
        ("vat", "Ватикан 1:1", "flag-vat.png", "flagpanel-vat.png")]
if all(os.path.exists(os.path.join(src, r[2])) for r in rows):
    S, bg = 2, (20, 17, 14)
    corner, header = (0, 0, 300, 150), (72, 146, 544, 214)
    cw, hw = (corner[2] - corner[0]) * S, (header[2] - header[0]) * S
    rh = (corner[3] - corner[1]) * S
    sheet = Image.new("RGB", (40 + 300 + cw + 30 + hw + 40, 90 + len(rows) * (rh + 24) + 330), bg)
    d = ImageDraw.Draw(sheet)
    d.text((40, 24), "Рамка флага в углу и в шапке панели: флаг в своих пропорциях, без щита (×2)", font=font(30, True), fill=(239, 228, 200))
    y = 90
    for code, name, a, b in rows:
        d.text((40, y + rh // 2 - 14), name, font=font(24), fill=(207, 197, 171))
        im = Image.open(os.path.join(src, a)).convert("RGB").crop(corner).resize((cw, rh), Image.LANCZOS)
        sheet.paste(im, (340, y))
        hb = Image.open(os.path.join(src, b)).convert("RGB").crop(header)
        hb = hb.resize((hw, hb.height * S), Image.LANCZOS)
        sheet.paste(hb, (340 + cw + 30, y + (rh - hb.height) // 2))
        y += rh + 24
    q = os.path.join(src, "flag-720-qat.png")
    if os.path.exists(q):
        d.text((40, y + 10), "1280×720, Катар", font=font(24), fill=(207, 197, 171))
        im = Image.open(q).convert("RGB").crop((0, 0, 200, 100)).resize((600, 300), Image.LANCZOS)
        sheet.paste(im, (340, y + 10))
    sheet.save(os.path.join(out, "flag-frames.jpg"), quality=90)

# before / after
if old and os.path.exists(old):
    a = Image.open(old).convert("RGB").resize((1440, 810), Image.LANCZOS)
    b = Image.open(os.path.join(src, "state-1.png")).convert("RGB").resize((1440, 810), Image.LANCZOS)
    cmp = Image.new("RGB", (2 * 1440 + 24, 810 + 64), (20, 17, 14))
    d = ImageDraw.Draw(cmp)
    d.text((16, 14), "До: редакция 2 (17 ноября) — щит, латунь на каждой плите, свечение", font=font(28, True), fill=(207, 197, 171))
    d.text((1440 + 24 + 16, 14), "После: редакция 3 (18 ноября) — флаг как есть, бумага и чернила по ролям", font=font(28, True), fill=(239, 228, 200))
    cmp.paste(a, (0, 64)); cmp.paste(b, (1440 + 24, 64))
    cmp.save(os.path.join(out, "compare-before-after.jpg"), quality=88)
print("ok")
