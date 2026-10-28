"""regime_flags.py <game folder> [--apply] [--tags ISR,PSE,...]

A country with no flag of its own for a regime flies the generic banner - a communist Israel
under the same red flag as a communist Chad. This draws each country a flag of its own for the
regimes that most often change a flag: communism and fascism (ideologies), absolute monarchy,
military junta and theocracy (forms of government), in the colours of the country's own flag.

  communism         a red field, the country's main colour at the hoist, a gold star
  fascism           a dark field crossed by the country's colours, a gold sword in a white disc
  absolute_monarchy the country's flag in a gold frame, a gold crown on a disc of its main colour
  military_junta    the country's flag darkened, an olive band, a star in a wreath at the hoist
  theocracy         by the faith of the country's own people: green with a crescent (Islam),
                    white with a cross (Christianity), saffron with a wheel (Buddhism), saffron
                    with a sun (Hinduism), otherwise the country's colours with a gold sun

A flag is drawn only when none exists for that country and regime, and never for the regime a
country already has in history/countries/2024/20_politics.json: a flag named for the government
a country starts with replaces its own flag on the first day (the flag variant trap).
"""
import glob
import json
import math
import os
import re
import sys

from PIL import Image, ImageDraw

W, H = 768, 512
REGIMES = ["communism", "fascism", "absolute_monarchy", "military_junta", "theocracy"]


def load(path):
    return json.loads(re.sub(r'//[^\n]*', '', open(path, encoding='utf-8').read()))


def palette(path):
    im = Image.open(path).convert('RGBA').resize((96, 64))
    counts = {}
    for r, g, b, a in (im.get_flattened_data() if hasattr(im, 'get_flattened_data') else im.getdata()):
        if a < 128:
            continue
        key = (r // 24 * 24, g // 24 * 24, b // 24 * 24)
        counts[key] = counts.get(key, 0) + 1
    ordered = [c for c, _ in sorted(counts.items(), key=lambda kv: -kv[1])]
    picked = []
    for c in ordered:
        if all(sum(abs(c[i] - p[i]) for i in range(3)) > 90 for p in picked):
            picked.append(c)
        if len(picked) == 3:
            break
    while len(picked) < 3:
        picked.append((240, 240, 240))
    return picked


def light(c):
    return 0.299 * c[0] + 0.587 * c[1] + 0.114 * c[2] > 170


def main_colour(pal):
    # The country's main colour: the most common one that is not white-ish.
    for c in pal:
        if not light(c):
            return c
    return pal[0]


GOLD = (214, 172, 58)
WHITE = (244, 241, 232)


def star(d, cx, cy, r, fill, points=5, inner=0.42):
    pts = []
    for i in range(points * 2):
        a = -math.pi / 2 + i * math.pi / points
        rr = r if i % 2 == 0 else r * inner
        pts.append((cx + rr * math.cos(a), cy + rr * math.sin(a)))
    d.polygon(pts, fill=fill)


def crown(d, cx, cy, w, h, fill):
    x, y = cx - w / 2, cy - h / 2
    d.polygon([(x, y + h), (x + w, y + h), (x + w, y + h * .25), (x + w * .75, y + h * .58),
               (x + w * .5, y), (x + w * .25, y + h * .58), (x, y + h * .25)], fill=fill)
    for px in (x, x + w * .5, x + w):
        d.ellipse((px - w * .07, y + (h * .25 if px != x + w * .5 else 0) - w * .07,
                   px + w * .07, y + (h * .25 if px != x + w * .5 else 0) + w * .07), fill=fill)
    d.rectangle((x, y + h * .86, x + w, y + h), fill=fill)


def sun(d, cx, cy, r, fill, rays=16):
    d.ellipse((cx - r * .55, cy - r * .55, cx + r * .55, cy + r * .55), fill=fill)
    for i in range(rays):
        a = i * 2 * math.pi / rays
        b1, b2 = a - math.pi / rays * .45, a + math.pi / rays * .45
        d.polygon([(cx + r * math.cos(a), cy + r * math.sin(a)),
                   (cx + r * .6 * math.cos(b1), cy + r * .6 * math.sin(b1)),
                   (cx + r * .6 * math.cos(b2), cy + r * .6 * math.sin(b2))], fill=fill)


def wheel(d, cx, cy, r, fill, spokes=8):
    d.ellipse((cx - r, cy - r, cx + r, cy + r), outline=fill, width=int(r * .16))
    d.ellipse((cx - r * .18, cy - r * .18, cx + r * .18, cy + r * .18), fill=fill)
    for i in range(spokes):
        a = i * 2 * math.pi / spokes
        d.line((cx, cy, cx + r * math.cos(a), cy + r * math.sin(a)), fill=fill, width=int(r * .09))


def crescent(d, cx, cy, r, fill, ground):
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=fill)
    d.ellipse((cx - r * .55, cy - r * .85, cx + r * 1.15, cy + r * .85), fill=ground)
    star(d, cx + r * .9, cy, r * .38, fill)


def sword(d, cx, cy, h, fill):
    w = h * .09
    d.polygon([(cx, cy - h / 2), (cx + w, cy - h / 2 + w * 2), (cx + w, cy + h * .22),
               (cx - w, cy + h * .22), (cx - w, cy - h / 2 + w * 2)], fill=fill)
    d.rectangle((cx - h * .2, cy + h * .22, cx + h * .2, cy + h * .27), fill=fill)
    d.rectangle((cx - w * .8, cy + h * .27, cx + w * .8, cy + h * .45), fill=fill)
    d.ellipse((cx - w * 1.4, cy + h * .43, cx + w * 1.4, cy + h * .53), fill=fill)


def draw(regime, base_path, faith, layout=0):
    pal = palette(base_path)
    main = main_colour(pal)
    second = next((c for c in pal if c != main), WHITE)
    im = Image.new('RGBA', (W, H), (0, 0, 0, 255))
    d = ImageDraw.Draw(im)
    if regime == "communism":
        red = (176, 28, 30)
        hoist = main if main[0] < 150 or main[1] > 90 else (20, 60, 120)
        if layout == 0:
            d.rectangle((0, 0, W, H), fill=red)
            d.rectangle((0, 0, W * .26, H), fill=hoist)
            d.rectangle((W * .26, 0, W * .28, H), fill=GOLD)
            star(d, W * .52, H * .30, H * .17, GOLD)
        elif layout == 1:
            # The old flag in the canton of a red one, as the people's republics did it.
            d.rectangle((0, 0, W, H), fill=red)
            canton = Image.open(base_path).convert('RGBA').resize((int(W * .42), int(H * .5)))
            im.paste(canton, (0, 0))
            d = ImageDraw.Draw(im)
            d.rectangle((0, H * .5, W * .42, H * .52), fill=GOLD)
            d.rectangle((W * .42, 0, W * .432, H * .52), fill=GOLD)
            star(d, W * .70, H * .62, H * .2, GOLD)
        else:
            d.rectangle((0, 0, W, H), fill=red)
            d.rectangle((0, H * .38, W, H * .62), fill=hoist)
            d.ellipse((W * .14 - H * .2, H / 2 - H * .2, W * .14 + H * .2, H / 2 + H * .2), fill=red, outline=GOLD, width=int(H * .025))
            star(d, W * .14, H / 2, H * .14, GOLD)
    elif regime == "fascism":
        ground = (30, 30, 34)
        # A black stripe on a black field is no stripe: a dark main colour trades places.
        def dark(c):
            return 0.299 * c[0] + 0.587 * c[1] + 0.114 * c[2] < 70
        if dark(main):
            main, second = (second, main) if not dark(second) else (GOLD, WHITE)
        if dark(second):
            second = WHITE
        d.rectangle((0, 0, W, H), fill=ground)
        if layout == 0:
            d.rectangle((0, H * .36, W, H * .64), fill=main)
            d.rectangle((0, H * .33, W, H * .36), fill=second)
            d.rectangle((0, H * .64, W, H * .67), fill=second)
        elif layout == 1:
            # A diagonal of the country's colours across a dark field.
            d.polygon([(0, H), (W * .22, H), (W, H * .05), (W, 0), (W * .78, 0), (0, H * .95)], fill=main)
            d.line((0, H, W, 0), fill=second, width=int(H * .05))
        else:
            # An offset cross in the country's colours.
            d.rectangle((W * .28, 0, W * .40, H), fill=main)
            d.rectangle((0, H * .42, W, H * .58), fill=main)
            d.rectangle((W * .325, 0, W * .355, H), fill=second)
            d.rectangle((0, H * .485, W, H * .515), fill=second)
        cx = W / 2 if layout != 2 else W * .34
        disc = GOLD if light(main) else WHITE
        d.ellipse((cx - H * .23, H / 2 - H * .23, cx + H * .23, H / 2 + H * .23), fill=disc)
        sword(d, cx, H / 2, H * .38, (24, 24, 26))
    elif regime == "absolute_monarchy":
        base = Image.open(base_path).convert('RGBA').resize((W, H))
        im.paste(base, (0, 0))
        d = ImageDraw.Draw(im)
        frame = int(H * .045)
        d.rectangle((0, 0, W - 1, H - 1), outline=GOLD, width=frame)
        d.ellipse((W / 2 - H * .2, H / 2 - H * .2, W / 2 + H * .2, H / 2 + H * .2), fill=main, outline=GOLD, width=int(frame * .6))
        crown(d, W / 2, H / 2, H * .24, H * .17, GOLD)
    elif regime == "military_junta":
        base = Image.open(base_path).convert('RGBA').resize((W, H))
        dark = Image.new('RGBA', (W, H), (14, 18, 12, 60))
        im.paste(Image.alpha_composite(base, dark), (0, 0))
        d = ImageDraw.Draw(im)
        d.rectangle((0, H * .78, W, H), fill=(78, 86, 52))
        d.rectangle((0, H * .76, W, H * .78), fill=GOLD)
        cx, cy, r = W * .16, H * .30, H * .17
        d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(78, 86, 52), outline=GOLD, width=int(r * .14))
        star(d, cx, cy, r * .62, GOLD)
    elif regime == "theocracy":
        if faith == "islam":
            green = (24, 104, 60)
            d.rectangle((0, 0, W, H), fill=green)
            d.rectangle((0, 0, W * .22, H), fill=main if main != green else WHITE)
            crescent(d, W * .58, H * .5, H * .22, WHITE, green)
        elif faith == "christian":
            d.rectangle((0, 0, W, H), fill=WHITE)
            d.rectangle((W * .30, 0, W * .42, H), fill=main if not light(main) else (40, 60, 120))
            d.rectangle((0, H * .42, W, H * .58), fill=main if not light(main) else (40, 60, 120))
            d.rectangle((W * .335, H * .455, W * .385, H * .545), fill=GOLD)
        elif faith == "buddhist":
            d.rectangle((0, 0, W, H), fill=(226, 140, 34))
            d.rectangle((0, 0, W, H * .12), fill=main)
            d.rectangle((0, H * .88, W, H), fill=main)
            wheel(d, W / 2, H / 2, H * .25, (110, 40, 20))
        elif faith == "hindu":
            d.rectangle((0, 0, W, H), fill=(232, 120, 30))
            d.rectangle((0, 0, W * .2, H), fill=main)
            sun(d, W * .58, H / 2, H * .26, (250, 214, 92))
        else:
            d.rectangle((0, 0, W, H), fill=main)
            d.rectangle((0, H * .40, W, H * .60), fill=WHITE)
            sun(d, W / 2, H / 2, H * .22, GOLD)
    return im


def Layout(tag):
    """One of three layouts per country, fixed by its tag, so neighbours do not all look alike."""
    return sum(ord(ch) * (i + 1) for i, ch in enumerate(tag)) % 3


def faith_of(religion):
    if isinstance(religion, dict):
        religion = max(religion.items(), key=lambda kv: kv[1])[0] if religion else ""
    religion = (religion or "").lower()
    if religion in ("sunni", "shia", "ibadi", "islam", "muslim"):
        return "islam"
    if "christian" in religion or religion in ("catholic", "protestant", "orthodox", "anglican"):
        return "christian"
    if religion in ("buddhist", "theravada", "mahayana"):
        return "buddhist"
    if religion == "hindu":
        return "hindu"
    return "other"


def run(game, apply, only):
    flags = os.path.join(game, 'gfx', 'flags')
    existing = {os.path.basename(f)[5:-4] for f in glob.glob(os.path.join(flags, 'flag_*.png'))}
    countries = load(os.path.join(game, 'common', 'countries', '00_countries.json'))
    politics = load(os.path.join(game, 'history', 'countries', '2024', '20_politics.json'))
    peoples = load(os.path.join(game, 'history', 'countries', '2024', '30_peoples.json'))
    cultures = load(os.path.join(game, 'common', 'cultures', '00_cultures.json'))
    made = 0
    for tag in countries:
        low = tag.lower()
        if only and tag not in only:
            continue
        if low not in existing:
            continue
        start = politics.get(tag, {})
        primary = (peoples.get(tag, {}).get('primary_cultures') or [""])[0]
        faith = faith_of(cultures.get(primary, {}).get('religion'))
        for regime in REGIMES:
            if regime in (start.get('government'), start.get('ideology')):
                continue
            name = f'{low}_{regime}'
            if name in existing:
                continue
            made += 1
            if apply:
                draw(regime, os.path.join(flags, f'flag_{low}.png'), faith, Layout(low)).save(os.path.join(flags, f'flag_{name}.png'))
    print(f'{made} flag(s) {"drawn" if apply else "to draw"}')


if __name__ == '__main__':
    only = set()
    if '--tags' in sys.argv:
        only = set(sys.argv[sys.argv.index('--tags') + 1].split(','))
    run(sys.argv[1], '--apply' in sys.argv, only)
