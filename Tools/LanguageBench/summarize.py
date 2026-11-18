# SPDX-License-Identifier: GPL-3.0-only
"""Prints the LanguageBench JSON as one Markdown table per kernel, both series side by side.

    python Tools/LanguageBench/summarize.py tmp_logs/languages/langbench/results-full.json
"""
import json
import sys
from collections import defaultdict


def main(path):
    with open(path, encoding="utf-8") as handle:
        records = json.load(handle)
    bench = defaultdict(dict)
    crossings = []
    for record in records:
        if "crossing" in record:
            crossings.append(record)
            continue
        bench[(record["kernel"], record["contender"])][record["series"]] = record
    kernels = []
    for kernel, _ in bench:
        if kernel not in kernels:
            kernels.append(kernel)
    for kernel in kernels:
        rows = [(contender, series) for (k, contender), series in bench.items() if k == kernel]
        refs = sorted({key[len("ratio_vs_"):-len("_median")] for _, series in rows for r in series.values()
                       for key in r if key.startswith("ratio_vs_") and key.endswith("_median")})
        print(f"\n### {kernel}\n")
        header = "| contender | samples | median ms (s1 / s2) | p95 ms (s1 / s2) |"
        rule = "| --- | ---: | ---: | ---: |"
        for ref in refs:
            header += f" ratio vs {ref} (s1 / s2) | faster rounds vs {ref} |"
            rule += " ---: | ---: |"
        print(header)
        print(rule)
        for contender, series in rows:
            s = [series.get(1), series.get(2)]
            def both(key, fmt="{:.3f}"):
                return " / ".join(fmt.format(r[key]) if r and key in r else "-" for r in s)
            line = f"| {contender} | {s[0]['samples'] if s[0] else '-'} | {both('median_ms')} | {both('p95_ms')} |"
            for ref in refs:
                line += f" {both('ratio_vs_' + ref + '_median')} |"
                line += " " + " / ".join(str(r.get('rounds_faster_than_' + ref, '-')) if r else "-" for r in s) + " |"
            print(line)
    print("\n### crossing\n")
    print("| kernel | contender | what | median ms (s1 / s2) |")
    print("| --- | --- | --- | ---: |")
    grouped = defaultdict(dict)
    for record in crossings:
        grouped[(record["kernel"], record["contender"], record["crossing"])][record["series"]] = record
    for (kernel, contender, what), series in grouped.items():
        values = " / ".join(f"{series[s]['median_ms']:.6f}" if s in series else "-" for s in (1, 2))
        print(f"| {kernel} | {contender} | {what} | {values} |")


if __name__ == "__main__":
    main(sys.argv[1])
