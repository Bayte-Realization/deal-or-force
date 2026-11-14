"""Bake the picture of the world the main menu's globe wears.

The menu shows a small live globe: one sphere, one picture, a short shader
(Scripts/MenuGlobe.cs). This script makes that picture from the game's own data, so the
world behind the menu is the world the game is played on - the same coasts, the same
frontiers on the scenario's first day, the same country colours - and never a screenshot:
no flag badges, no stars, no counters, no names, nothing that depends on a language.

    python Tools/bake-menu-globe.py             writes game/gfx/interface/brand/menu_globe.jpg
                                                and menu_globe_lights.png
    python Tools/bake-menu-globe.py --preview   also writes a 1920x1080 picture of how the
                                                sphere will sit behind the menu
                                                (tmp_playtest/menu_globe_preview.png)
    python Tools/bake-menu-globe.py --still     also writes menu_world.png, the still of the
                                                same planet that the loading screen shows and
                                                the menu falls back to

What goes in (all from game/, all equirectangular, 5760x2880):
    map_data/provinces.png + definition.csv     which province a cell is
    history/provinces/*.csv                     who holds each province on the first day
    common/country_merges/*.json                entries of the country table that are not countries
    common/countries/*.json                     each country's colour
    map_data/relief_color.png                   the ground (NASA Blue Marble)
    map_data/heightmap.png                      relief, for the hill shade
    map_data/bathymetry.png                     the sea floor
    map_data/cities/*.json                      where people live, for the lights of the night side

Rerun it after the map, the first-day frontiers or the country colours change. A mod that
wants its own world behind the menu replaces the two pictures by name.
"""
import argparse
import csv
import glob
import json
import math
import os
import re
import sys

import numpy as np
from PIL import Image, ImageFilter

Image.MAX_IMAGE_PIXELS = None
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__))).replace("\\", "/")
GAME = ROOT + "/game"

# The look, in one place.
OCEAN_DEEP = np.array([0.020, 0.052, 0.098], np.float32)
OCEAN_SHELF = np.array([0.070, 0.165, 0.255], np.float32)
BORDER_INK = np.array([0.045, 0.062, 0.100], np.float32)
UNHELD = np.array([0.46, 0.47, 0.44], np.float32)   # land nobody holds: bare ground, no colour
POLITICAL = 0.50       # how much of a country's colour lies over the ground
GROUND_SATURATION = 0.50
BORDER_OPACITY = 0.82
COAST_OPACITY = 0.55
HILL_STRENGTH = 0.55
COLOUR_SATURATION = 0.74   # the countries' own colours, calmed: a globe in a study, not a toy


def strip_comments(text):
    """The game's JSON allows // comments; a string may hold // too (a URL), so walk it."""
    out, i, n, in_string = [], 0, len(text), False
    while i < n:
        ch = text[i]
        if in_string:
            out.append(ch)
            if ch == "\\" and i + 1 < n:
                out.append(text[i + 1]); i += 1
            elif ch == '"':
                in_string = False
        elif ch == '"':
            in_string = True; out.append(ch)
        elif ch == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] != "\n":
                i += 1
            continue
        else:
            out.append(ch)
        i += 1
    return "".join(out)


def read_json(path):
    with open(path, encoding="utf-8-sig") as handle:
        return json.loads(re.sub(r",(\s*[}\]])", r"\1", strip_comments(handle.read())))


def hex_colour(text):
    text = text.lstrip("#")
    return np.array([int(text[i:i + 2], 16) / 255.0 for i in (0, 2, 4)], np.float32)


def srgb_to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def linear_to_srgb(c):
    c = np.clip(c, 0.0, 1.0)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055)


def load_world():
    provinces = np.asarray(Image.open(GAME + "/map_data/provinces.png").convert("RGB"))
    height, width = provinces.shape[:2]
    key = (provinces[..., 0].astype(np.uint32) << 16) | (provinces[..., 1].astype(np.uint32) << 8) | provinces[..., 2]

    province_of_colour = np.zeros(1 << 24, np.int32)
    with open(GAME + "/map_data/definition.csv", encoding="utf-8-sig") as handle:
        for row in csv.reader(handle, delimiter=";"):
            if not row or not row[0].strip().isdigit():
                continue
            province_of_colour[(int(row[1]) << 16) | (int(row[2]) << 8) | int(row[3])] = int(row[0])
    province = province_of_colour[key]
    land = key != 0

    owner = {}
    for path in sorted(glob.glob(GAME + "/history/provinces/*.csv")):
        with open(path, encoding="utf-8-sig") as handle:
            for line in handle:
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                parts = line.split(";")
                if parts[0].isdigit() and len(parts) > 1 and parts[1].strip():
                    owner[int(parts[0])] = parts[1].strip()

    merges = {}
    for path in sorted(glob.glob(GAME + "/common/country_merges/*.json")):
        for tag, entry in read_json(path).items():
            merges[tag] = (entry or {}).get("into", "")
    colours = {}
    for path in sorted(glob.glob(GAME + "/common/countries/*.json")):
        for tag, entry in read_json(path).items():
            if isinstance(entry, dict) and entry.get("color"):
                colours[tag] = hex_colour(entry["color"])

    tags = ["", "\0none"]                      # 0 sea / unknown, 1 land nobody holds
    index_of = {"": 0, "\0none": 1}
    holder_of_province = np.zeros(int(province.max()) + 1, np.int32)
    holder_of_province[1:] = 1
    for number, tag in owner.items():
        seen = set()
        while tag in merges and merges[tag] not in ("", None) and tag not in seen:
            seen.add(tag)
            tag = merges[tag]
        if tag == "none" or tag not in colours:
            continue
        if tag not in index_of:
            index_of[tag] = len(tags); tags.append(tag)
        if number < len(holder_of_province):
            holder_of_province[number] = index_of[tag]
    holder = holder_of_province[province]
    holder[~land] = 0
    palette = np.zeros((len(tags), 3), np.float32)
    palette[1] = UNHELD
    for tag, slot in index_of.items():
        if slot > 1:
            palette[slot] = colours[tag]
    print(f"world {width}x{height}: {int(province.max())} provinces, {len(tags) - 2} countries on the first day")
    return width, height, land, holder, palette


def soften(mask, radius):
    image = Image.fromarray((np.clip(mask, 0, 1) * 255).astype(np.uint8))
    return np.asarray(image.filter(ImageFilter.GaussianBlur(radius)), np.float32) / 255.0


def edges(labels, land):
    """Cells where the label changes towards the east or the south, on land both sides."""
    edge = np.zeros(labels.shape, bool)
    east = labels != np.roll(labels, -1, axis=1)
    edge |= east & land & np.roll(land, -1, axis=1)
    south = np.zeros(labels.shape, bool)
    south[:-1] = (labels[:-1] != labels[1:]) & land[:-1] & land[1:]
    edge |= south
    return edge


def bake(size):
    width, height, land, holder, palette = load_world()
    ground = np.asarray(Image.open(GAME + "/map_data/relief_color.png").convert("RGB").resize((width, height)), np.float32) / 255.0
    elevation = np.asarray(Image.open(GAME + "/map_data/heightmap.png").convert("L").resize((width, height)), np.float32) / 255.0
    depth = np.asarray(Image.open(GAME + "/map_data/bathymetry.png").convert("L").resize((width, height)), np.float32) / 255.0

    # The ground, quietened: the land's own colour carries deserts, forests and ice, the
    # country's colour is laid over it and takes its light and dark from it.
    luma = ground @ np.array([0.2126, 0.7152, 0.0722], np.float32)
    quiet = luma[..., None] + (ground - luma[..., None]) * GROUND_SATURATION

    # Hill shade from the north-west, the atlas convention; longitude wraps.
    dx = np.roll(elevation, -1, axis=1) - np.roll(elevation, 1, axis=1)
    dy = np.zeros_like(elevation); dy[1:-1] = elevation[2:] - elevation[:-2]
    shade = np.clip(1.0 + HILL_STRENGTH * 14.0 * (dx * -0.7 + dy * -0.7), 0.55, 1.45)

    held = holder > 1
    colour = palette[holder]
    colour_luma = colour @ np.array([0.2126, 0.7152, 0.0722], np.float32)
    colour = colour_luma[..., None] + (colour - colour_luma[..., None]) * COLOUR_SATURATION
    tone = 0.46 + 0.80 * luma[..., None]
    political = colour * tone
    # Ice stays ice: over snow the colour thins, so Greenland and the high Himalaya read as
    # ground somebody holds, not as a painted shape.
    icy = np.clip((luma - 0.72) / 0.2, 0.0, 1.0)
    cover = (np.where(held, POLITICAL, 0.0) * (1.0 - 0.62 * icy)).astype(np.float32)[..., None]
    land_colour = (quiet * (1.0 - cover) + political * cover) * shade[..., None]

    shallow = np.clip(depth, 0.0, 1.0) ** 1.6
    sea = OCEAN_DEEP + (OCEAN_SHELF - OCEAN_DEEP) * shallow[..., None]

    land_soft = soften(land.astype(np.float32), 0.8)[..., None]
    picture = sea * (1.0 - land_soft) + land_colour * land_soft

    frontier = soften(edges(holder, land).astype(np.float32), 1.25)
    frontier = np.clip(frontier * 2.6, 0.0, 1.0)[..., None]
    picture = picture * (1.0 - frontier * BORDER_OPACITY) + BORDER_INK * frontier * BORDER_OPACITY

    coast = np.zeros(land.shape, bool)
    coast |= land != np.roll(land, -1, axis=1)
    coast[:-1] |= land[:-1] != land[1:]
    shore = np.clip(soften(coast.astype(np.float32), 1.0) * 2.2, 0.0, 1.0)[..., None]
    picture = picture * (1.0 - shore * COAST_OPACITY) + BORDER_INK * shore * COAST_OPACITY

    out = Image.fromarray((np.clip(picture, 0, 1) * 255 + 0.5).astype(np.uint8)).resize((size, size // 2), Image.LANCZOS)
    return out


def lights(size):
    """Where people live, as light: one soft point per city, sized by its people."""
    width, height = size, size // 2
    glow = np.zeros((height, width), np.float32)
    count = 0
    for path in sorted(glob.glob(GAME + "/map_data/cities/*.json")):
        for city in read_json(path).values():
            if not isinstance(city, dict) or "latitude" not in city:
                continue
            x = (float(city["longitude"]) + 180.0) / 360.0 * width
            y = (90.0 - float(city["latitude"])) / 180.0 * height
            people = max(float(city.get("population", 0)), 20000.0)
            strength = min(1.0, 0.22 + 0.16 * math.log10(people / 20000.0))
            xi, yi = int(x) % width, min(max(int(y), 0), height - 1)
            glow[yi, xi] = max(glow[yi, xi], strength)
            count += 1
    # A bright core a texel or two wide and a faint breath around it: a city seen from
    # orbit is a point, and a thousand of them close together are what reads as a coast.
    core = np.asarray(Image.fromarray((glow * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(0.6)), np.float32)
    halo = np.asarray(Image.fromarray((glow * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(1.6)), np.float32)
    combined = np.clip(core * 2.0 + halo * 2.4, 0, 255)
    print(f"lights: {count} cities")
    return Image.fromarray(combined.astype(np.uint8))


def preview(albedo, glow, out_path, yaw_degrees=18.0, width=1920, height=1080, keep=None):
    """How the sphere sits behind the menu: the same camera and the same light as MenuGlobe."""
    texture = np.asarray(albedo, np.float32) / 255.0
    night = np.asarray(glow.resize(albedo.size), np.float32) / 255.0
    th, tw = texture.shape[:2]
    fov = math.radians(34.0)
    distance, radius = 17.4, 5.0
    centre_shift = 2.75                      # the planet stands right of the middle
    ys, xs = np.mgrid[0:height, 0:width].astype(np.float32)
    px = (xs - width / 2) / (height / 2) * math.tan(fov / 2)
    py = -(ys - height / 2) / (height / 2) * math.tan(fov / 2)
    direction = np.stack([px, py, -np.ones_like(px)], -1)
    direction /= np.linalg.norm(direction, axis=-1, keepdims=True)
    origin = np.array([-centre_shift, 0.0, distance], np.float32)
    b = direction @ origin
    c = origin @ origin - radius * radius
    disc = b * b - c
    hit = disc > 0
    t = -b - np.sqrt(np.where(hit, disc, 0))
    point = origin + direction * t[..., None]
    normal = point / radius
    tilt = math.radians(-27.0)
    cy, sy = math.cos(tilt), math.sin(tilt)
    ny = normal[..., 1] * cy - normal[..., 2] * sy
    nz = normal[..., 1] * sy + normal[..., 2] * cy
    nx = normal[..., 0]
    latitude = np.arcsin(np.clip(ny, -1, 1))
    longitude = np.arctan2(nx, nz) + math.radians(yaw_degrees)
    u = ((longitude / (2 * math.pi) + 0.5) % 1.0) * (tw - 1)
    v = (0.5 - latitude / math.pi) * (th - 1)
    ui, vi = u.astype(np.int32), v.astype(np.int32)
    surface = srgb_to_linear(texture[vi, ui])
    sun = np.array([-0.76, 0.32, 0.42], np.float32); sun /= np.linalg.norm(sun)
    lit = normal @ sun
    day = np.clip(lit * 1.7 + 0.06, 0.0, 1.0) ** 0.8
    colour = surface * (0.022 + 1.12 * day[..., None])
    dark = np.clip(-lit * 3.0 + 0.25, 0.0, 1.0)
    colour += (night[vi, ui] * dark)[..., None] * np.array([1.0, 0.84, 0.60], np.float32) * 0.62
    facing = np.clip(-(normal * direction).sum(-1), 0.0, 1.0)
    rim = (1.0 - facing) ** 3.2
    colour += rim[..., None] * np.array([0.22, 0.42, 0.78], np.float32) * (0.25 + 0.9 * np.clip(lit + 0.35, 0, 1))[..., None]
    space = np.array([0.0035, 0.0058, 0.0105], np.float32)
    frame = np.where(hit[..., None], colour, space)
    # the air outside the limb
    closest = np.sqrt(np.clip(origin @ origin - b * b, 0, None))   # distance of the ray from the centre
    halo = np.clip(1.0 - (closest - radius) / 0.30, 0.0, 1.0) ** 2.6 * (~hit)
    side = np.clip((origin + direction * (-b)[..., None]) @ sun / radius + 0.35, 0.0, 1.0)
    frame += (halo * side * 0.55)[..., None] * np.array([0.20, 0.40, 0.80], np.float32)
    image = Image.fromarray((linear_to_srgb(frame) * 255 + 0.5).astype(np.uint8))
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    if keep is not None:
        image = image.resize(keep, Image.LANCZOS)
    image.save(out_path)
    print("picture:", out_path, image.size)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--size", type=int, default=4096, help="width of the baked picture; the height is half")
    parser.add_argument("--preview", action="store_true")
    parser.add_argument("--still", action="store_true", help="write menu_world.png, the planet as a still picture")
    parser.add_argument("--yaw", type=float, default=18.0, help="preview only: the longitude facing the viewer")
    parser.add_argument("--out", default=GAME + "/gfx/interface/brand")
    arguments = parser.parse_args()

    albedo = bake(arguments.size)
    glow = lights(arguments.size // 2)
    albedo.save(arguments.out + "/menu_globe.jpg", quality=93, subsampling=0)
    glow.save(arguments.out + "/menu_globe_lights.png", optimize=True)
    for name in ("menu_globe.jpg", "menu_globe_lights.png"):
        print(name, os.path.getsize(arguments.out + "/" + name) // 1024, "kB")
    if arguments.preview:
        preview(albedo, glow, ROOT + "/tmp_playtest/menu_globe_preview.png", arguments.yaw)
    if arguments.still:
        # Drawn half as large again and brought down, so the limb and the frontiers are smooth.
        preview(albedo, glow, arguments.out + "/menu_world.png", arguments.yaw, 3840, 2160, (2560, 1440))
    return 0


if __name__ == "__main__":
    sys.exit(main())
