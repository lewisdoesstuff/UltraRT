"""Which lights make a pose too bright: a run of bench/suites/attr/attr-<level>.json as one table.

    py -3 tools/lights/attribute.py bench/results/<run> [--base pathtrace_ae_enabled_0]

Runs tools/lights/score.py's numbers (score.csv; score the run first) through a pivot: one row per view, the base
mode's ratio to the game and dark delta, then per switched-off class (or any other variant of the base mode) what is
left: "x0.61 +0.4" = the frame is 0.61 of the game and the game's dark pixels are 0.4 levels brighter with that
class off. The class whose column drops most is the one that lights the view; a class whose column equals the base
does nothing there. docs/lighting-overrides.md, "Review".
"""
import argparse
import collections
import csv
import os


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("run")
    ap.add_argument("--base", default="pathtrace_ae_enabled_0")
    a = ap.parse_args()
    rows = list(csv.DictReader(open(os.path.join(a.run, "score.csv"), encoding="utf-8")))
    poses = collections.OrderedDict()
    modes = []
    for r in rows:
        if not r["mode"].startswith(a.base):
            continue
        name = r["mode"][len(a.base):].lstrip("_") or "base"
        name = name.replace("lc_class_", "").replace("_0", " off") if name.startswith("lc_class_") else name
        if name not in modes:
            modes.append(name)
        key = r["pose"] + (" " + r["stop"] if r.get("stop") else "")
        if not r["ratio"]:
            continue
        # A view with no dark pixels in the game has no dark delta.
        poses.setdefault(key, {"game": r["game"], "dark": r["dark_pct"]})[name] = (float(r["ratio"]), float(r["dark_delta"] or "nan"))
    modes = ["base"] + [m for m in modes if m != "base"]
    lines = ["| View | game | dark % | " + " | ".join(modes) + " |", "|---|---|---|" + "---|" * len(modes)]
    for pose, d in poses.items():
        cells = []
        for m in modes:
            v = d.get(m)
            cells.append(("x%.2f %+.1f" % v).replace(" +nan", "") if v else "")
        lines.append("| %s | %s | %s | %s |" % (pose, d["game"], d["dark"], " | ".join(cells)))
    out = os.path.join(a.run, "attribution.md")
    open(out, "w", encoding="utf-8", newline="\n").write("\n".join(lines) + "\n")
    print("\n".join(lines))
    print(out)


if __name__ == "__main__":
    main()
