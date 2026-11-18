"""The paper of the mockup: img/paper.jpg, one large sheet (1024 x 768) that never repeats on screen.

Revision 3, second pass: the art director found one 128 px "salt" grain on every surface - dark plates,
counters, the undo cell and the newspaper alike (anti-slop sign 11: noise without a material). Grain is gone
from the dark plates; only paper has a texture, and it is paper's own: a slow unevenness of the pulp and
short fibres, white on the multiply layer except where the sheet is a shade darker. Used by the army tags
and the newspaper clipping, each at its own offset, so two tags never show the same patch.

Run from the repository root:  python docs/design/interface-2026-11-17/mockup/tools/paper.py
"""
import math, os, random
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

OUT = os.path.join(os.getcwd(), "docs/design/interface-2026-11-17/mockup/img/paper.jpg")
W, H = 1024, 768
rng = np.random.default_rng(1924)
random.seed(1924)

# the pulp: a slow unevenness, a few percent
low = Image.fromarray((rng.random((H // 48 + 2, W // 48 + 2)) * 255).astype(np.uint8)).resize((W, H), Image.BICUBIC)
low = np.asarray(low.filter(ImageFilter.GaussianBlur(18)), np.float32) / 255 - .5
mid = Image.fromarray((rng.random((H // 6, W // 6)) * 255).astype(np.uint8)).resize((W, H), Image.BICUBIC)
mid = np.asarray(mid.filter(ImageFilter.GaussianBlur(2)), np.float32) / 255 - .5
v = 1 - .045 * (low + .5) - .018 * (mid + .5)

# fibres: short, slightly bent, mostly a shade darker, a few lighter
fib = Image.new("L", (W, H), 128)
d = ImageDraw.Draw(fib)
for _ in range(900):
    x, y = random.uniform(0, W), random.uniform(0, H)
    a = random.uniform(0, math.pi); L = random.uniform(5, 26); bend = random.uniform(-.5, .5)
    pts = []
    for t in np.linspace(0, 1, 6):
        ang = a + bend * t
        pts.append((x + math.cos(ang) * L * t, y + math.sin(ang) * L * t))
    d.line(pts, fill=random.choice([112, 116, 120, 138]), width=1)
fib = np.asarray(fib.filter(ImageFilter.GaussianBlur(.45)), np.float32) / 128 - 1
v = np.clip(v + fib * .3, 0, 1)

img = np.stack([v * 255, v * 252, v * 246], -1)
Image.fromarray(np.clip(img, 0, 255).astype(np.uint8)).save(OUT, quality=90)
print("ok", OUT)
