"""The cities on the map, from Natural Earth's populated places.

Offline, never used by the game at runtime. Reads the public-domain Natural Earth table
ne_10m_populated_places (https://www.naturalearthdata.com, 1:10m Cultural, "Populated Places")
from Tools/CityCompiler/cache/ - download the zip and unpack it there, the folder is ignored -
and writes:

  game/map_data/cities/00_cities.json      every city: its name and where it is, nothing else
  game/localization/english/24_cities.json its name in English   (city.<id>.name)
  game/localization/russian/24_cities.json its name in Russian   (city.<id>.name)

Needs only the Python standard library.

  python Tools/CityCompiler/compile_cities.py [--out <folder>]

--out writes the three files under another folder instead of the game's, to compare a
regeneration with what is shipped before replacing it.

Which cities: every place Natural Earth ranks (it ranks 0, the world cities, to 10, small towns).

What the game is given about a city is its name and its place. Every city is equal there: no
head count, no rank, no word about being a capital - a country names its capital city itself
(common/countries, "capital_city") and the map marks whichever city is a capital at the moment.

The one thing this script still decides is the ORDER of the file, because the game takes the
cities in the order they are written whenever it cannot take them all: the map letters the
earlier names first when there is no room for every one, and "a city of this region" is the
first written that stands there. So the file goes from the most to the least prominent place,
by Natural Earth's own rank and then by the size of the urban area - figures that are used
here to sort and are not written out. The counts printed at the end are how many cities each
of Natural Earth's rank bands holds; the map's zoom bands (game/gui/24_workshop.json,
map.city_band*_count) start from them.

The towns that are somebody's capital and that Natural Earth has no place for are written by hand
in game/map_data/cities/10_capitals_added.json, which this script does not touch.

Compare before replacing: the shipped 00_cities.json has been corrected by hand since it was
generated (Astana, Prayagraj, Kropyvnytskyi, Gurugram, Shimla - and their names in the two
24_cities.json), and a regeneration would put Natural Earth's older names back and shift the
order by the places those corrections moved. Run with --out and diff.
"""
import json
import re
import struct
import sys
import unicodedata
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CACHE = Path(__file__).resolve().parent / "cache"
# Every place Natural Earth ranks (0-10): the towns below rank 7 are drawn only close in
# (tier 3) and the map never lets two names overlap, so more of them costs nothing far out.
MAXIMUM_SCALERANK = 10


def read_dbf(path):
    with open(path, "rb") as f:
        header = f.read(32)
        count, header_length, record_length = struct.unpack("<IHH", header[4:12])
        fields = []
        while True:
            descriptor = f.read(32)
            if descriptor[0] == 0x0D:
                break
            fields.append((descriptor[:11].split(b"\0")[0].decode("ascii"), chr(descriptor[11]), descriptor[16]))
        f.seek(header_length)
        for _ in range(count):
            record = f.read(record_length)
            if not record or record[0] == 0x2A:
                continue
            row, offset = {}, 1
            for name, kind, length in fields:
                text = record[offset:offset + length].decode("utf-8", errors="replace").strip()
                offset += length
                if kind in "NF":
                    try:
                        row[name] = float(text) if text else None
                    except ValueError:
                        row[name] = None
                else:
                    row[name] = text
            yield row


def slug(text):
    text = unicodedata.normalize("NFKD", text).encode("ascii", "ignore").decode("ascii").lower()
    return re.sub(r"[^a-z0-9]+", "_", text).strip("_") or "city"


def tier(row):
    """Natural Earth's rank in four bands, used only to order the file."""
    rank = row["SCALERANK"] if row["SCALERANK"] is not None else 10
    if row["FEATURECLA"].startswith("Admin-0 capital") or rank <= 2:
        return 0
    if rank <= 4:
        return 1
    if rank <= 6:
        return 2
    return 3


def national(row):
    """A sovereign state's capital in the source: sorted ahead of its band, and that is all."""
    return row["FEATURECLA"] == "Admin-0 capital"


def main():
    table = next(CACHE.glob("**/ne_10m_populated_places.dbf"), None)
    if table is None:
        raise SystemExit(f"Put ne_10m_populated_places.dbf (Natural Earth 1:10m populated places) under {CACHE}")
    cities = []
    for row in read_dbf(table):
        rank = row["SCALERANK"] if row["SCALERANK"] is not None else 10
        seat = row["FEATURECLA"].startswith("Admin-0 capital")
        if rank > MAXIMUM_SCALERANK and not seat:
            continue
        cities.append(row)
    # The bigger first, so an id collision gives the suffix to the smaller place.
    # The places of rank 7 and above take their keys first, as they did before the smaller towns
    # were added, so no existing key moves to another place.
    cities.sort(key=lambda r: ((r["SCALERANK"] if r["SCALERANK"] is not None else 10) > 7, tier(r), -(r["POP_MAX"] or 0), r["NAME"]))
    used, entries, english, russian, prominence = set(), {}, {}, {}, {}
    for row in cities:
        base = f"{slug(row['NAME'] or row['NAMEASCII'])}_{row['ADM0_A3'].lower()}"
        key, n = base, 2
        while key in used:
            key, n = f"{base}_{n}", n + 1
        used.add(key)
        entries[key] = {
            "name": row["NAME"],
            "latitude": round(row["LATITUDE"], 4),
            "longitude": round(row["LONGITUDE"], 4),
        }
        # Most prominent first: the rank band, a state's capital ahead of the rest of its band,
        # then the larger urban area, then the key so that equal places keep one order for ever.
        prominence[key] = (tier(row), 0 if national(row) else 1, -max(0, int(row["POP_MAX"] or 0)), key)
        english[f"city.{key}.name"] = row["NAME"]
        russian[f"city.{key}.name"] = row.get("NAME_RU") or row["NAME"]

    header = [
        "// Deal or Force - the cities on the map (CityCatalog, GlobeCities).",
        "//",
        "// A city is a name and a place, and nothing else: every city is equal here. It has no head",
        "// count, no rank and no word about being a capital. It belongs to the region it stands in and",
        "// to whoever holds that ground - both are found from the map, so it changes hands with its",
        "// ground. A country names its capital city itself (common/countries, \"capital_city\"), and the",
        "// map gives the capital's mark to whichever city is a capital at the moment.",
        "//",
        "//   name                  what it is called when no language has city.<key>.name for it",
        "//   latitude, longitude   where it stands",
        "//",
        "// THE ORDER OF THE FILE MATTERS, and it is the only thing here that does: the cities go from",
        "// the most to the least prominent place. When the game cannot take every city it takes the",
        "// earlier ones - the map letters them first when there is no room for every name (how many",
        "// from how far out is map.city_band*_count in game/gui), and a government, a militia or a base",
        "// that needs a city of some region takes the first written that stands there.",
        "//",
        "// Later files win, key by key: a mod adds a city under a key of its own (it goes after these),",
        "// moves or renames one by naming its key again (it keeps its place in the order), and removes",
        "// one with \"removed\": true. A file in the older shape - \"population\", \"tier\", \"capital\" -",
        "// still loads; those keys are ignored, except that \"tier\": -1 still removes the city.",
        "//",
        "// Generated by Tools/CityCompiler/compile_cities.py from Natural Earth (public domain).",
    ]
    root = ROOT
    if "--out" in sys.argv:
        root = Path(sys.argv[sys.argv.index("--out") + 1]).resolve()
    out = root / "game/map_data/cities/00_cities.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    lines = ["{"]
    items = sorted(entries.items(), key=lambda item: prominence[item[0]])
    for index, (key, entry) in enumerate(items):
        comma = "," if index < len(items) - 1 else ""
        lines.append(f"  {json.dumps(key)}: {json.dumps(entry, ensure_ascii=False)}{comma}")
    lines.append("}")
    out.write_text("\n".join(header + lines) + "\n", encoding="utf-8")

    for language, names in (("english", english), ("russian", russian)):
        path = root / f"game/localization/{language}/24_cities.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        body = ["// Deal or Force - every city on the map by its id (game/map_data/cities).",
                "//",
                "// Generated by Tools/CityCompiler/compile_cities.py from Natural Earth's own names.",
                "{"]
        pairs = list(names.items())
        for index, (key, value) in enumerate(pairs):
            comma = "," if index < len(pairs) - 1 else ""
            body.append(f"  {json.dumps(key)}: {json.dumps(value, ensure_ascii=False)}{comma}")
        body.append("}")
        path.write_text("\n".join(body) + "\n", encoding="utf-8")
    counts = [sum(1 for p in prominence.values() if p[0] <= band) for band in range(4)]
    print(f"{len(entries)} cities; the first {counts[0]}, {counts[1]}, {counts[2]} and {counts[3]} "
          "are Natural Earth's four rank bands (map.city_band0_count .. map.city_band2_count)")


if __name__ == "__main__":
    main()
