"""The brightness board of every level: one row per level from the scored tours.

    py -3 tools/lights/board.py bench/results [--since 20261117-15] [--out bench/results/board.md]

Reads score.csv (tools/lights/score.py) of every run folder named *_tour-<level>* (the newest per level, not older
than --since) and prints per level and mode: the median ratio to the game over the tour's views, the worst view, the
median dark delta and the share of views that fail the scoreboard's limits (ratio outside 0.8-1.25, dark delta over
2.0, reveal over 0.5 %). Sorted by the fixed-exposure median, worst first: that is the order to review levels in.
docs/lighting-overrides.md, "Review".
"""
import argparse
import csv
import glob
import os
import re
import statistics


def num(s):
    try:
        return float(s)
    except ValueError:
        return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("results")
    ap.add_argument("--since", default="")
    ap.add_argument("--out", default="")
    a = ap.parse_args()
    runs = {}
    for d in sorted(glob.glob(os.path.join(a.results, "*_tour-*"))):
        m = re.match(r"(\d{8}-\d{6})_tour-([^_]+)", os.path.basename(d))
        if m and m.group(1) >= a.since and os.path.exists(os.path.join(d, "score.csv")):
            runs[m.group(2)] = d
    rows = []
    for level, d in runs.items():
        by = {}
        for r in csv.DictReader(open(os.path.join(d, "score.csv"), encoding="utf-8")):
            if num(r["ratio"]) is not None:
                by.setdefault(r["mode"], []).append(r)
        cell = {}
        for mode, rs in by.items():
            ratios = [num(r["ratio"]) for r in rs]
            darks = [x for x in (num(r["dark_delta"]) for r in rs) if x is not None]
            bad = sum(1 for r in rs if not 0.8 <= num(r["ratio"]) <= 1.25 or (num(r["dark_delta"]) or 0) > 2.0
                      or (num(r["reveal"]) or 0) > 0.5)
            cell[mode] = (statistics.median(ratios), max(ratios), statistics.median(darks) if darks else 0.0, bad, len(rs))
        rows.append((level, cell))
    fixed = "pathtrace_ae_enabled_0"
    rows.sort(key=lambda x: -(x[1].get(fixed, x[1].get("pathtrace", (0,)))[0]))
    lines = ["| Level | views | ships: median | worst | dark | fail | fixed exposure: median | worst | dark | fail |",
             "|---|---|---|---|---|---|---|---|---|---|"]
    for level, cell in rows:
        def f(mode):
            c = cell.get(mode)
            return "x%.2f | x%.2f | %+.1f | %d" % c[:4] if c else " | | | "
        n = max(c[4] for c in cell.values())
        lines.append("| %s | %d | %s | %s |" % (level, n, f("pathtrace"), f(fixed)))
    text = "\n".join(lines) + "\n"
    print(text)
    if a.out:
        open(a.out, "w", encoding="utf-8", newline="\n").write(text)
        print(a.out)


if __name__ == "__main__":
    main()
