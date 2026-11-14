"""Which English strings the code shows have no translation in a language's tables.

Tools/audit-localization.ps1 checks the tables against themselves (a key translated twice,
a dropped {0}, an empty value). It cannot see a string that is in the code and in no table
at all - which is how "Shortage: {0}" and "Situation room - {0}" reached a Russian player in
English. This reads every string literal handed to the translating calls
(Tr, T, Loc.Get, Loc.Format, Title, ActionButton, Tip, Placeholder) under Scripts/ and lists
the ones the language has no entry for.

    python Tools/audit-untranslated.py                 russian, summary and the first 60
    python Tools/audit-untranslated.py --all           every missing string
    python Tools/audit-untranslated.py --language german
    python Tools/audit-untranslated.py --fail-over 0   exit 1 when more than N are missing

A literal that is not prose (a token, a path, a number format, one word of punctuation) is
skipped. The full list is written to tmp_logs/untranslated-<language>.csv.
"""
import argparse
import csv
import glob
import io
import json
import os
import re
import sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
JOIN = re.compile(r'"\s*\+\s*(?:\r?\n\s*)+"')
CALL = re.compile(r'\b(?:Tr|T|Loc\.Get|Loc\.Format|Title|ActionButton|Tip|Ui\.Tip|Placeholder|MenuAction|MenuCaption)\(\s*"((?:[^"\\]|\\.)*)"')


def table(language):
    keys = set()
    for path in glob.glob(os.path.join(ROOT, "game", "localization", language, "*.json")):
        text = io.open(path, encoding="utf-8-sig").read()
        text = re.sub(r"^\s*//.*$", "", text, flags=re.M)
        try:
            keys.update(json.loads(text).keys())
        except ValueError:
            # A table with comments in odd places: fall back to reading the keys line by line.
            keys.update(m.group(1).encode().decode("unicode_escape", "ignore")
                        for m in re.finditer(r'^\s*"((?:[^"\\]|\\.)*)"\s*:', text, flags=re.M))
    return keys


def prose(text):
    if len(text) < 3 or not re.search(r"[A-Za-z]{3}", text):
        return False
    if re.fullmatch(r"[a-z0-9_.:/\-]+", text):          # a token, a path, an id
        return False
    if text.startswith(("res://", "user://", "#")):
        return False
    return True


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--language", default="russian")
    parser.add_argument("--all", action="store_true")
    parser.add_argument("--fail-over", type=int, default=-1)
    args = parser.parse_args()

    known = table(args.language)
    missing = {}
    for path in glob.glob(os.path.join(ROOT, "Scripts", "**", "*.cs"), recursive=True):
        source = io.open(path, encoding="utf-8-sig", errors="replace").read()
        # A sentence written as "one half " + "the other half" over two lines is one key: join the
        # halves first (keeping the line count), or the first half is reported as missing.
        source = JOIN.sub(lambda found: "\n" * found.group(0).count("\n"), source)
        for number, line in enumerate(source.splitlines(), 1):
            if line.lstrip().startswith("//"):
                continue
            for match in CALL.finditer(line):
                # Still joined to something that is not a literal (a variable, a call): the key
                # is built at run time and cannot be checked from here.
                if line[match.end():].lstrip().startswith("+"):
                    continue
                text = match.group(1).replace('\\"', '"').replace("\\n", "\n")
                if prose(text) and text not in known:
                    missing.setdefault(text, []).append("%s:%d" % (os.path.relpath(path, ROOT).replace("\\", "/"), number))

    os.makedirs(os.path.join(ROOT, "tmp_logs"), exist_ok=True)
    out = os.path.join(ROOT, "tmp_logs", "untranslated-%s.csv" % args.language)
    with io.open(out, "w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.writer(handle)
        writer.writerow(["string", "uses", "first use"])
        for text, places in sorted(missing.items(), key=lambda item: (-len(item[1]), item[0])):
            writer.writerow([text, len(places), places[0]])
    print("%s: %d table keys; %d string(s) in the code have no entry; list in %s" % (
        args.language, len(known), len(missing), os.path.relpath(out, ROOT)))
    shown = sorted(missing.items(), key=lambda item: (-len(item[1]), item[0]))
    for text, places in shown if args.all else shown[:60]:
        print("  %3d  %-70s %s" % (len(places), text[:70].replace("\n", " "), places[0]))
    return 1 if args.fail_over >= 0 and len(missing) > args.fail_over else 0


if __name__ == "__main__":
    sys.exit(main())
