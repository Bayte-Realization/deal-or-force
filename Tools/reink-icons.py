"""Re-ink the interface icons: one ink for the whole set, shape and shading untouched.

The icons were painted in several golds (#b38631, #d9ad67, #e6bc68 and anti-aliased edges
drifting to orange), which fixed the interface to a brown-and-yellow palette: a skin could
recolour every panel and button and the pictures stayed gold. This rewrites each icon's
colour as one near-white ink, keeping the alpha channel exactly and the picture's own light
and dark as a share of that ink. A near-white picture takes any tint the interface gives it
(`icon_normal_color`, `modulate`), so the palette in game/interface decides the colour again.

    python Tools/reink-icons.py            report what would change
    python Tools/reink-icons.py --apply    rewrite the files
    python Tools/reink-icons.py --ink #f2eee4 --apply

Only pictures that are at least 55 % gold by opaque pixels are touched; anything painted in
other colours (flags, the capacity atlas, the grey staff icons) is left alone. Running it
twice changes nothing the second time. Needs Pillow.
"""
import argparse
import colorsys
import glob
import os
import sys

from PIL import Image

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "game", "gfx", "interface", "icons"))


def gold_share(pixels):
    opaque = [p for p in pixels if p[3] > 40]
    if not opaque:
        return 0.0, 0
    step = max(1, len(opaque) // 4000)
    sample = opaque[::step]
    gold = 0
    for r, g, b, _ in sample:
        h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
        if s > 0.18 and v > 0.2 and 14 <= h * 360 <= 58:
            gold += 1
    return gold / len(sample), len(opaque)


def reink(image, ink):
    pixels = list(image.getdata())
    values = sorted(max(r, g, b) for r, g, b, a in pixels if a > 40)
    if not values:
        return None
    # The brightest ordinary pixel is "full ink"; a darker passage keeps its share of it, never
    # less than 45 % so a shaded icon does not turn into a grey smudge on a dark panel.
    reference = max(1, values[int(len(values) * 0.95)])
    out = []
    for r, g, b, a in pixels:
        share = min(1.0, max(0.45, max(r, g, b) / reference)) if a else 1.0
        out.append((round(ink[0] * share), round(ink[1] * share), round(ink[2] * share), a))
    result = Image.new("RGBA", image.size)
    result.putdata(out)
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--ink", default="#f2eee4")
    parser.add_argument("--root", default=ROOT)
    args = parser.parse_args()
    ink = tuple(int(args.ink[i:i + 2], 16) for i in (1, 3, 5))

    changed, skipped = [], []
    for path in sorted(glob.glob(os.path.join(args.root, "**", "*.png"), recursive=True)):
        image = Image.open(path).convert("RGBA")
        share, opaque = gold_share(list(image.getdata()))
        name = os.path.relpath(path, args.root).replace("\\", "/")
        if share < 0.55:
            skipped.append((name, round(share, 2)))
            continue
        result = reink(image, ink)
        if result is None:
            skipped.append((name, 0))
            continue
        changed.append(name)
        if args.apply:
            result.save(path, optimize=True)
    print(f"{'re-inked' if args.apply else 'would re-ink'} {len(changed)} icon(s) with {args.ink}; left {len(skipped)} alone")
    for name, share in skipped:
        print(f"   left: {name} (gold share {share})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
