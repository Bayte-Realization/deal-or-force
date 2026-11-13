"""The cities on the map, from Natural Earth's populated places.

Offline, never used by the game at runtime. Reads the public-domain Natural Earth table
ne_10m_populated_places (https://www.naturalearthdata.com, 1:10m Cultural, "Populated Places")
from Tools/CityCompiler/cache/ - download the zip and unpack it there, the folder is ignored -
and writes:

  game/map_data/cities/00_cities.json      every city: where it is, how big, how important
  game/localization/english/24_cities.json its name in English   (city.<id>.name)
  game/localization/russian/24_cities.json its name in Russian   (city.<id>.name)

Needs only the Python standard library.

  python Tools/CityCompiler/compile_cities.py

Which cities: every national capital, and every place Natural Earth ranks 7 or better on its
scale (it ranks 0, the world cities, to 10, small towns). The tier the game draws a city at
comes from the same rank: 0 are the capitals and the world's great cities, seen from far out;
3 are the towns that only show on a region's own screen.
"""
import json
import re
import struct
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
    rank = row["SCALERANK"] if row["SCALERANK"] is not None else 10
    if row["FEATURECLA"].startswith("Admin-0 capital") or rank <= 2:
        return 0
    if rank <= 4:
        return 1
    if rank <= 6:
        return 2
    return 3


def capital(row):
    kind = row["FEATURECLA"]
    if kind == "Admin-0 capital":
        return "national"
    if kind.startswith("Admin-0 capital"):  # "Admin-0 capital alt": a second seat of government
        return "seat"
    if kind == "Admin-1 capital" or kind == "Admin-1 region capital":
        return "regional"
    return ""


def main():
    table = next(CACHE.glob("**/ne_10m_populated_places.dbf"), None)
    if table is None:
        raise SystemExit(f"Put ne_10m_populated_places.dbf (Natural Earth 1:10m populated places) under {CACHE}")
    cities = []
    for row in read_dbf(table):
        rank = row["SCALERANK"] if row["SCALERANK"] is not None else 10
        national = row["FEATURECLA"].startswith("Admin-0 capital")
        if rank > MAXIMUM_SCALERANK and not national:
            continue
        cities.append(row)
    # The bigger first, so an id collision gives the suffix to the smaller place.
    # The places of rank 7 and above take their keys first, as they did before the smaller towns
    # were added, so no existing key moves to another place.
    cities.sort(key=lambda r: ((r["SCALERANK"] if r["SCALERANK"] is not None else 10) > 7, tier(r), -(r["POP_MAX"] or 0), r["NAME"]))
    used, entries, english, russian = set(), {}, {}, {}
    for row in cities:
        base = f"{slug(row['NAME'] or row['NAMEASCII'])}_{row['ADM0_A3'].lower()}"
        key, n = base, 2
        while key in used:
            key, n = f"{base}_{n}", n + 1
        used.add(key)
        entry = {
            "name": row["NAME"],
            "latitude": round(row["LATITUDE"], 4),
            "longitude": round(row["LONGITUDE"], 4),
            "population": int(row["POP_MAX"] or 0),
            "tier": tier(row),
        }
        kind = capital(row)
        if kind:
            entry["capital"] = kind
        entries[key] = entry
        english[f"city.{key}.name"] = row["NAME"]
        russian[f"city.{key}.name"] = row.get("NAME_RU") or row["NAME"]

    header = [
        "// Deal or Force - the cities drawn on the map (GlobeCities, CityCatalog).",
        "//",
        "// Where a city is, how many lived there on the first day and how important a place it is.",
        "// A city belongs to the region it stands in and to whoever holds that ground - the region and",
        "// province under it are found from the map, so it changes hands with its ground - and its",
        "// people are its region's: they grow and shrink as the region's urban population does",
        "// (CityLife). There is no \"country\" field: a city is where it is, not whose.",
        "//",
        "//   latitude, longitude   where it stands",
        "//   population            the urban area's people on the scenario's first day",
        "//   tier                  0 capitals and world cities, seen from far out; 1, 2; 3 towns seen",
        "//                         only close in. The map draws the lower tiers first and never lets",
        "//                         two names overlap",
        "//   capital               national, seat (a second seat of government) or regional",
        "//",
        "// Later files win, key by key: a mod adds a city under a key of its own, moves or renames one",
        "// by naming its key again, and removes one with \"tier\": -1. The name shown is",
        "// city.<key>.name from the localisation tables, falling back to \"name\".",
        "//",
        "// Generated by Tools/CityCompiler/compile_cities.py from Natural Earth (public domain).",
    ]
    out = ROOT / "game/map_data/cities/00_cities.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    lines = ["{"]
    items = list(entries.items())
    for index, (key, entry) in enumerate(items):
        comma = "," if index < len(items) - 1 else ""
        lines.append(f"  {json.dumps(key)}: {json.dumps(entry, ensure_ascii=False)}{comma}")
    lines.append("}")
    out.write_text("\n".join(header + lines) + "\n", encoding="utf-8")

    for language, names in (("english", english), ("russian", russian)):
        path = ROOT / f"game/localization/{language}/24_cities.json"
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
    counts = {t: sum(1 for e in entries.values() if e["tier"] == t) for t in range(4)}
    print(f"{len(entries)} cities, by tier {counts}")


if __name__ == "__main__":
    main()
