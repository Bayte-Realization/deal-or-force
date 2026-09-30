"""Offline GIS import, never used by the game at runtime. Does not change province IDs.

Needs numpy and Pillow; source files stay in the ignored cache/ folder:

  globcover2009.zip (or its aligned cover_aligned.npy)  ESA GlobCover 2009 land cover, (c) ESA 2010
                                                        and UCLouvain - free use with that credit
  gebco_08_rev_elev_5400x2700.tif                       NASA Blue Marble topography from GEBCO data,
  gebco_08_rev_bath_5400x2700.tif                       free to re-use, commercially too, with credit

  https://doi.org/10.1594/PANGAEA.787668
  https://science.nasa.gov/earth/earth-observatory/blue-marble-next-generation/topography-bathymetry-maps/

It writes the terrain the game plays on (terrain.png, terrain_provinces.csv) and the three layers
it is drawn with: heightmap.png (the relief the globe is lit by), bathymetry.png (the depth of the
sea, for the shelves along every coast) and relief_color.png (the physical map's colours).

The elevation used to be WorldClim 2.1's. WorldClim is CC BY-NC-SA: no commercial use and no
redistribution without permission, and terrain.png was derived from it - so the hills and
mountains of the shipped map now come from the NASA/GEBCO topography instead. `--elevation
worldclim` still reads the old archive (and then needs rasterio) for anyone comparing the two.

--recount-only recounts terrain_provinces.csv from the terrain.png already there and the current
provinces.png - what a recompiled map needs, its cells having kept their terrain.
"""
import argparse
import csv
import hashlib
import json
import re
from pathlib import Path
import zipfile

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
CACHE = Path(__file__).resolve().parent / "cache"
NASA_ELEVATION = "gebco_08_rev_elev_5400x2700.tif"
NASA_BATHYMETRY = "gebco_08_rev_bath_5400x2700.tif"
NASA_TOP_METRES = 6400.0  # the NASA topography is scaled 0-6400 metres over 0-255


def raster(archive, width, height, kind, cache_name):
    cached = CACHE / cache_name
    if cached.exists():
        array = np.load(cached)
        if array.shape == (height, width):
            return array
    import rasterio
    from rasterio.warp import reproject, Resampling
    from rasterio.transform import from_bounds
    with zipfile.ZipFile(archive) as z:
        tif = next(n for n in z.namelist() if n.endswith('.tif') and 'QL' not in n)
    target = np.full((height, width), -32768 if kind == 'elevation' else 0,
                     dtype=np.float32 if kind == 'elevation' else np.uint8)
    with rasterio.open('/vsizip/' + archive.as_posix() + '/' + tif) as src:
        print(kind, src.shape, src.bounds, flush=True)
        reproject(rasterio.band(src, 1), target, src_transform=src.transform,
                  src_crs=src.crs, dst_transform=from_bounds(-180, -90, 180, 90, width, height),
                  dst_crs='EPSG:4326', src_nodata=src.nodata,
                  dst_nodata=-32768 if kind == 'elevation' else 0,
                  resampling=Resampling.bilinear if kind == 'elevation' else Resampling.nearest,
                  num_threads=2, warp_mem_limit=256)
    np.save(cached, target)
    return target


def nasa_layer(name, width, height):
    """A NASA 5400x2700 plate carree layer, resampled to the map's own grid."""
    path = CACHE / name
    if not path.exists():
        raise SystemExit(f"Put {name} (NASA Blue Marble topography/bathymetry) under {CACHE}")
    image = Image.open(path).convert('L').resize((width, height), Image.BILINEAR)
    return np.asarray(image, dtype=np.float32)


def window_extreme(values, size, pick):
    """A size x size moving maximum or minimum, edges repeated - scipy's filter without scipy."""
    pad = size // 2
    padded = np.pad(values, pad, mode='edge')
    height, width = values.shape
    result = padded[0:height, 0:width].copy()
    for dy in range(size):
        for dx in range(size):
            pick(result, padded[dy:dy + height, dx:dx + width], out=result)
    return result


def fill_nearest(types, missing, iterations=96):
    """Missing cells take a neighbour's value, spreading outwards ring by ring."""
    filled = types.copy()
    hole = missing.copy()
    for _ in range(iterations):
        if not hole.any():
            break
        for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0), (1, 1), (-1, -1), (1, -1), (-1, 1)):
            neighbour = np.roll(np.roll(filled, dy, axis=0), dx, axis=1)
            take = hole & (neighbour > 0)
            filled[take] = neighbour[take]
            hole &= ~take
    return filled, hole


def blur(values, passes=1):
    """A light separable blur: 25-metre steps of an 8-bit source must not light as terraces."""
    out = values
    for _ in range(passes):
        out = (np.roll(out, 1, axis=1) + 2 * out + np.roll(out, -1, axis=1)) / 4
        vertical = np.pad(out, ((1, 1), (0, 0)), mode='edge')
        out = (vertical[:-2] + 2 * vertical[1:-1] + vertical[2:]) / 4
    return out


# The physical map's ramp, by metres: lowland green through dry upland to rock and snow.
# Muted on purpose - it sits under the atlas look, not over it.
RAMP = [(0, (0x5c, 0x76, 0x4c)), (200, (0x78, 0x86, 0x52)), (500, (0x97, 0x94, 0x5f)),
        (1000, (0xa6, 0x8e, 0x62)), (2000, (0x8e, 0x78, 0x5b)), (3200, (0x8b, 0x7f, 0x75)),
        (4500, (0xb4, 0xad, 0xa6)), (6000, (0xe9, 0xe7, 0xe4))]


def relief_colours(elevation, types, slots):
    metres = np.array([m for m, _ in RAMP], dtype=np.float32)
    colours = np.array([c for _, c in RAMP], dtype=np.float32)
    rgb = np.stack([np.interp(elevation, metres, colours[:, channel]) for channel in range(3)], axis=-1)
    tints = {'ice': (0xe6, 0xec, 0xef), 'desert': (0xcf, 0xb8, 0x82), 'jungle': (0x3f, 0x6a, 0x3c),
             'forest': (0x4d, 0x6e, 0x45), 'tundra': (0x8f, 0x96, 0x80), 'marsh': (0x55, 0x6f, 0x5e)}
    lowland = np.clip(1.0 - elevation / 1400.0, 0.0, 1.0)[..., None]
    for name, colour in tints.items():
        if name not in slots:
            continue
        mask = types == slots[name]
        weight = 1.0 if name == 'ice' else 0.55
        blend = np.where(mask[..., None], weight * (lowland if name != 'ice' else 1.0), 0.0)
        rgb = rgb * (1 - blend) + np.array(colour, dtype=np.float32) * blend
    return np.clip(rgb, 0, 255).astype(np.uint8)


def despeckle(types, land, slot_count, passes):
    """
    A majority vote over each 3x3 neighbourhood, a couple of times over.

    Land cover at eight kilometres is noisy: a single pixel of steppe in a farmland plain, a
    speck of forest on a hillside. Drawn on the map every one of them is a square of another
    colour, and at close zoom the ground reads as a scatter of pixels - which is the pixelation
    the map was criticised for. A pixel keeps its type unless most of its neighbours are something
    else; coherent areas, coasts and mountain ranges survive whole.
    """
    for _ in range(max(0, passes)):
        votes = np.zeros((slot_count,) + types.shape, dtype=np.uint8)
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                shifted = np.roll(np.roll(types, dy, axis=0), dx, axis=1)
                for slot in range(1, slot_count):
                    votes[slot] += (shifted == slot)
        winner = votes.argmax(axis=0).astype(np.uint8)
        strong = votes.max(axis=0) >= 5
        own = np.take_along_axis(votes, types[None].astype(np.int64), axis=0)[0]
        change = land & strong & (own <= 2) & (winner > 0)
        types = np.where(change, winner, types)
    return types


def load_definitions():
    definitions = {}
    for p in sorted((ROOT / 'game/common/terrain').glob('*.json')):
        text = re.sub(r"^\s*//.*$", "", p.read_text(encoding='utf-8'), flags=re.M)
        for key, fields in json.loads(text).items():
            definitions.setdefault(key, {}).update(fields)
    return definitions


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--recount-only', action='store_true',
                        help='Recount terrain_provinces.csv from the existing terrain.png for the current provinces.png')
    parser.add_argument('--elevation', choices=['nasa', 'worldclim'], default='nasa',
                        help='Where hills and mountains come from (worldclim is non-commercial; kept for comparison)')
    parser.add_argument('--layers-only', action='store_true',
                        help='Write heightmap, bathymetry and relief colour only; leave the terrain types alone')
    args = parser.parse_args()
    config = json.loads((Path(__file__).parent / 'classification.json').read_text())
    definitions = load_definitions()
    names = sorted(definitions)
    slots = {name: i + 1 for i, name in enumerate(names)}
    palette = np.zeros((len(names) + 1, 3), dtype=np.uint8)
    for name in names:
        palette[slots[name]] = tuple(bytes.fromhex(definitions[name]['color'].lstrip('#')))
    province_image = Image.open(ROOT / 'game/map_data/provinces.png').convert('RGB')
    width, height = province_image.size
    rgb = np.asarray(province_image, dtype=np.uint32)
    packed = rgb[:, :, 0] << 16 | rgb[:, :, 1] << 8 | rgb[:, :, 2]
    ids = np.zeros(1 << 24, dtype=np.uint16)
    with (ROOT / 'game/map_data/definition.csv').open() as f:
        for row in csv.DictReader(f, delimiter=';'):
            color = int(row['r']) << 16 | int(row['g']) << 8 | int(row['b'])
            ids[color] = int(row['province_id'])
    provinces = ids[packed]
    del rgb, ids, packed
    land = provinces > 0
    if args.recount_only:
        recount(names, slots, palette, provinces, land)
        return

    lat = 90 - (np.arange(height)[:, None] + .5) * 180 / height
    if args.elevation == 'worldclim':
        elevation = raster(CACHE / 'elevation.zip', width, height, 'elevation', 'elevation_aligned.npy')
        elevation = np.where(elevation > -1000, elevation, 0).astype(np.float32)
    else:
        elevation = nasa_layer(NASA_ELEVATION, width, height) * (NASA_TOP_METRES / 255.0)
    bathymetry = nasa_layer(NASA_BATHYMETRY, width, height)

    if args.layers_only:
        types = read_types(slots, palette)
        missing = np.zeros_like(land)
    else:
        cover = raster(CACHE / 'globcover2009.zip', width, height, 'cover', 'cover_aligned.npy')
        types = np.zeros((height, width), dtype=np.uint8)
        for code, name in config['cover_classes'].items():
            types[cover == int(code)] = slots[name]
        types[(cover == 40) & (np.abs(lat) < config['jungle_max_latitude'])] = slots['jungle']
        types[np.isin(cover, [130, 140, 150, 200]) & (np.abs(lat) >= config['tundra_min_latitude'])] = slots['tundra']
        # GlobCover ends at 65 S. Antarctic land is an explicitly documented ice fallback.
        types[(lat < -60) & land] = slots['ice']
        missing = (types == 0) & land
        if missing.any():
            types, left = fill_nearest(types, missing)
            types[left & land] = slots['plains']
        window = config['elevation_neighborhood_pixels']
        ruggedness = window_extreme(elevation, window, np.maximum) - window_extreme(elevation, window, np.minimum)
        convertible = land & ~np.isin(types, [slots['ice'], slots['marsh'], slots['urban']])
        types[convertible & (ruggedness >= config['hills_local_range_m'])] = slots['hills']
        types[convertible & (ruggedness >= config['mountains_local_range_m'])] = slots['mountains']
        types = despeckle(types, land, len(palette), config.get('despeckle_passes', 2))
        types[~land] = 0
        if np.any(types[land] == 0):
            raise RuntimeError('Unassigned land pixels')
        Image.fromarray(palette[types]).save(ROOT / 'game/map_data/terrain.png')

    # The three presentation layers. Height is kept at the source's own 0-6400 m scale in eight
    # bits and blurred a little so that 25-metre steps do not light as contour terraces.
    height_bytes = np.clip(blur(elevation * (255.0 / NASA_TOP_METRES), 2), 0, 255).astype(np.uint8)
    Image.fromarray(height_bytes, 'L').save(ROOT / 'game/map_data/heightmap.png', optimize=True)
    depth = np.where(land, 255, bathymetry)
    Image.fromarray(np.clip(blur(depth, 1), 0, 255).astype(np.uint8), 'L').save(
        ROOT / 'game/map_data/bathymetry.png', optimize=True)
    Image.fromarray(relief_colours(blur(elevation, 1), types, slots), 'RGB').save(
        ROOT / 'game/map_data/relief_color.png', optimize=True)

    counts, dominant = write_province_terrain(names, provinces, types, len(palette))
    sources = {
        'land cover': 'ESA GlobCover 2009 (c) ESA 2010 and UCLouvain - https://doi.org/10.1594/PANGAEA.787668',
        'elevation and bathymetry': 'NASA Earth Observatory Blue Marble topography and bathymetry (Jesse Allen), '
                                    'from GEBCO data by the British Oceanographic Data Centre - '
                                    'https://science.nasa.gov/earth/earth-observatory/blue-marble-next-generation/topography-bathymetry-maps/',
    } if args.elevation == 'nasa' else {
        'land cover': 'https://doi.org/10.1594/PANGAEA.787668',
        'elevation': 'WorldClim 2.1 (CC BY-NC-SA 4.0 - not for a commercial build)'}
    hashed = [CACHE / NASA_ELEVATION, CACHE / NASA_BATHYMETRY]
    hashed += [CACHE / 'globcover2009.zip'] if (CACHE / 'globcover2009.zip').exists() else []
    report = {'size': [width, height], 'source_year': 2009, 'land_pixels': int(land.sum()),
              'nearest_landcover_fallback_pixels': int(missing.sum()),
              'provinces': int(np.count_nonzero(counts[1:].sum(axis=1))),
              'province_distribution': {n: int(np.count_nonzero(dominant[1:] == slots[n])) for n in names},
              'classification': config,
              'elevation': args.elevation,
              'sources': sources,
              'sha256': {p.name: hashlib.file_digest(p.open('rb'), 'sha256').hexdigest() for p in hashed if p.exists()}}
    (ROOT / 'game/map_data/terrain_provenance.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({k: report[k] for k in ('provinces', 'province_distribution', 'elevation')}, indent=2), flush=True)


def read_types(slots, palette):
    terrain = np.asarray(Image.open(ROOT / 'game/map_data/terrain.png').convert('RGB'), dtype=np.uint32)
    packed = terrain[:, :, 0] << 16 | terrain[:, :, 1] << 8 | terrain[:, :, 2]
    lookup = np.zeros(1 << 24, dtype=np.uint8)
    for name, slot in slots.items():
        colour = palette[slot]
        lookup[int(colour[0]) << 16 | int(colour[1]) << 8 | int(colour[2])] = slot
    return lookup[packed]


def write_province_terrain(names, provinces, types, slots_count):
    counts = np.bincount((provinces.astype(np.int64) * slots_count + types).ravel(),
                         minlength=(int(provinces.max()) + 1) * slots_count).reshape(-1, slots_count)
    dominant = counts.argmax(axis=1)
    with (ROOT / 'game/map_data/terrain_provinces.csv').open('w', newline='') as f:
        writer = csv.writer(f, delimiter=';')
        writer.writerow(['province_id', 'terrain', 'dominant_share'])
        for p in range(1, len(counts)):
            if counts[p].sum():
                writer.writerow([p, names[int(dominant[p]) - 1], round(float(counts[p].max() / counts[p].sum()), 4)])
    return counts, dominant


def recount(names, slots, palette, provinces, land):
    """terrain.png read back into slots, and the provinces counted over it again."""
    types = read_types(slots, palette)
    if np.any(types[land] == 0):
        raise RuntimeError('terrain.png leaves land pixels without a terrain; run the full import')
    types[~land] = 0
    counts, dominant = write_province_terrain(names, provinces, types, len(palette))
    report_path = ROOT / 'game/map_data/terrain_provenance.json'
    report = json.loads(report_path.read_text())
    used = counts[1:].sum(axis=1) > 0
    report['provinces'] = int(np.count_nonzero(used))
    report['province_distribution'] = {n: int(np.count_nonzero(dominant[1:][used] == slots[n])) for n in names}
    report_path.write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({'provinces': report['provinces'], 'province_distribution': report['province_distribution']}, indent=2))


if __name__ == '__main__':
    main()
