"""Offline GIS import, never used by the game at runtime. Does not change province IDs.
Requires numpy, Pillow, rasterio, scipy; source archives stay in ignored cache/.
"""
import argparse
import csv
import hashlib
import json
from pathlib import Path
import zipfile

import numpy as np
from PIL import Image
import rasterio
from rasterio.warp import reproject, Resampling
from rasterio.transform import from_bounds
from scipy.ndimage import distance_transform_edt, maximum_filter, minimum_filter

ROOT = Path(__file__).resolve().parents[2]
CACHE = Path(__file__).resolve().parent / "cache"


def raster(archive, width, height, kind, cache_name):
    cached = CACHE / cache_name
    if cached.exists():
        array = np.load(cached)
        if array.shape == (height, width):
            return array
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


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--initialize-relief', action='store_true', help='Create flat data layers only if missing')
    args = parser.parse_args()
    config = json.loads((Path(__file__).parent / 'classification.json').read_text())
    definitions = {}
    for p in sorted((ROOT / 'game/common/terrain').glob('*.json')):
        for key, fields in json.loads(p.read_text()).items():
            definitions.setdefault(key, {}).update(fields)
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
    cover = raster(CACHE / 'globcover2009.zip', width, height, 'cover', 'cover_aligned.npy')
    elevation = raster(CACHE / 'elevation.zip', width, height, 'elevation', 'elevation_aligned.npy')
    lat = 90 - (np.arange(height)[:, None] + .5) * 180 / height
    types = np.zeros((height, width), dtype=np.uint8)
    for code, name in config['cover_classes'].items():
        types[cover == int(code)] = slots[name]
    types[(cover == 40) & (np.abs(lat) < config['jungle_max_latitude'])] = slots['jungle']
    types[np.isin(cover, [130, 140, 150, 200]) & (np.abs(lat) >= config['tundra_min_latitude'])] = slots['tundra']
    # GlobCover ends at 65 S. Antarctic land is an explicitly documented ice fallback.
    types[(lat < -60) & land] = slots['ice']
    missing = (types == 0) & land
    if missing.any():
        nearest = distance_transform_edt(types == 0, return_distances=False, return_indices=True)
        types[missing] = types[tuple(nearest[:, missing])]
        del nearest
    valid_height = elevation > -1000
    # Missing ocean elevations must not manufacture coastal mountain cliffs.
    if (~valid_height).any():
        nearest = distance_transform_edt(~valid_height, return_distances=False, return_indices=True)
        elevation[~valid_height] = elevation[tuple(nearest[:, ~valid_height])]
        del nearest
    window = config['elevation_neighborhood_pixels']
    ruggedness = maximum_filter(elevation, size=window, mode='nearest') - minimum_filter(elevation, size=window, mode='nearest')
    convertible = land & valid_height & ~np.isin(types, [slots['ice'], slots['marsh'], slots['urban']])
    types[convertible & (ruggedness >= config['hills_local_range_m'])] = slots['hills']
    types[convertible & (ruggedness >= config['mountains_local_range_m'])] = slots['mountains']
    types[~land] = 0
    if np.any(types[land] == 0):
        raise RuntimeError('Unassigned land pixels')
    Image.fromarray(palette[types]).save(ROOT / 'game/map_data/terrain.png')
    counts = np.bincount((provinces.astype(np.int64) * len(palette) + types).ravel(),
                         minlength=(int(provinces.max()) + 1) * len(palette)).reshape(-1, len(palette))
    dominant = counts.argmax(axis=1)
    with (ROOT / 'game/map_data/terrain_provinces.csv').open('w', newline='') as f:
        writer = csv.writer(f, delimiter=';')
        writer.writerow(['province_id', 'terrain', 'dominant_share'])
        for p in range(1, len(counts)):
            if counts[p].sum():
                writer.writerow([p, names[int(dominant[p]) - 1], round(float(counts[p].max() / counts[p].sum()), 4)])
    if args.initialize_relief:
        for name, color in [('heightmap.png', (128, 128, 128)), ('relief_color.png', (146, 149, 119))]:
            path = ROOT / 'game/map_data' / name
            if not path.exists():
                Image.new('RGB', (4, 2), color).save(path)
    report = {'size': [width, height], 'source_year': 2009, 'land_pixels': int(land.sum()),
              'nearest_landcover_fallback_pixels': int(missing.sum()),
              'provinces': int(np.count_nonzero(counts[1:].sum(axis=1))),
              'province_distribution': {n: int(np.count_nonzero(dominant[1:] == slots[n])) for n in names},
              'classification': config,
              'sources': ['https://doi.org/10.1594/PANGAEA.787668', 'https://www.worldclim.org/data/worldclim21.html'],
              'sha256': {p.name: hashlib.file_digest(p.open('rb'), 'sha256').hexdigest()
                         for p in [CACHE / 'globcover2009.zip', CACHE / 'elevation.zip']}}
    (ROOT / 'game/map_data/terrain_provenance.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(report, indent=2), flush=True)


if __name__ == '__main__':
    main()
