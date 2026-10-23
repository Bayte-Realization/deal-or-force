"""Compare all transcribed Belarus counts with the official volume-2 PDF; no files are changed."""
import argparse
import json
import re
from pathlib import Path
from pypdf import PdfReader

root = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("pdf", nargs="?", default=root / "tmp/pdfs/belarus-census-2019-volume2.pdf", help="Local Belstat 2019 census volume-2 PDF")
reader = PdfReader(parser.parse_args().pdf)
data = json.loads((root / "game/common/regional_demography/00_belarus_2019.json").read_text(encoding="utf-8"))
labels = {
    "белорусы": "belarusian", "русские": "russian", "поляки": "polish", "украинцы": "ukrainian",
    "евреи": "jewish", "армяне": "armenian", "татары": "tatar", "цыгане": "romani",
    "азербайджанцы": "azerbaijani", "литовцы": "lithuanian", "туркмены": "turkmen",
    "немцы": "german", "грузины": "georgian", "молдаване": "moldovan",
    "китайцы": "chinese_diaspora", "латыши": "latvian", "узбеки": "uzbek", "казахи": "kazakh",
    "таджики": "tajik", "арабы": "unmapped", "Все население": "population",
    "Усе насельніцтва": "population"
}
checked = 0
for region_id, record in data.items():
    if region_id.startswith("//"):
        continue
    for area in record["areas"]:
        observed = {}
        for line in reader.pages[area["page"] - 1].extract_text(extraction_mode="layout").splitlines():
            columns = re.split(r"\s{2,}", line.strip())
            if columns and columns[0] in labels:
                assert len(columns) >= 4, (area["name"], columns)
                observed[labels[columns[0]]] = int(columns[3].replace(" ", ""))
        for culture, count in {**area["counts"], "unmapped": area["unmapped"], "population": area["population"]}.items():
            assert observed[culture] == count, (region_id, area["page"], culture, observed.get(culture), count)
            checked += 1
        print(f"SOURCE_COUNTS_OK region={region_id} page={area['page']} counts={len(observed)}")
print(f"SOURCE_COUNTS_VERIFIED={checked}")
