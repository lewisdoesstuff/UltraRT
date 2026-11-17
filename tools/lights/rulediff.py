"""A level's light review table, from the game's own census.

    py -3 tools/lights/rulediff.py bench/results/<run> [--layer proposed] [--all] [--out DIR]

A bench run with -Extra "-ultrart-light-census" writes lights_<scene>.json per level (every light the RT scene tracks,
on or off: class, the rule that set it, what RT does with it) and lights_<scene>.<layer>.json for every layer folder
under data/lights (proposed). This writes lights_<scene>.md next to them:

  - the classes of the level: lights, how many are on at the census, what ships and what the layer proposes;
  - the rule diff: every group of lights whose policy differs between what ships and the layer (same class, rule,
    name, range, intensity, colour = one row with a count), which is what a reviewer signs off;
  - with --all, every group, changed or not.

docs/lighting-overrides.md, "Review".
"""
import argparse
import collections
import glob
import json
import os
import re


def key_of(r):
    return (r["path"], tuple(r["pos"]))


def group_name(path):
    """The last two path parts with instance numbers removed: 'Lamps/Lamp (3)/Point Light' -> 'Lamp/Point Light'."""
    parts = [re.sub(r"\s*\(\d+\)$", "", p) for p in path.split("/")]
    return "/".join(parts[-2:])


def room(path):
    return path.split("/")[0]


def col(c):
    return "%.2f %.2f %.2f" % tuple(c)


def write(scene, base, layer, layer_name, out, show_all):
    lines = ["# Lights of %s" % scene, ""]
    by_key = {key_of(r): r for r in layer} if layer else {}
    classes = collections.OrderedDict()
    for r in base:
        c = classes.setdefault(r["class"], {"n": 0, "on": 0, "ship": collections.Counter(), "prop": collections.Counter()})
        c["n"] += 1
        c["on"] += 1 if r["active"] else 0
        c["ship"][r["policy"]] += 1
        p = by_key.get(key_of(r))
        if p:
            c["prop"][p["policy"]] += 1
    head = "| Class | lights | on at the census | ships as |" + (" %s |" % layer_name if layer else "")
    lines += [head, "|---|---|---|---|" + ("---|" if layer else "")]

    def pol(counter):
        return "; ".join("%s%s" % (k, " (%d)" % v if len(counter) > 1 else "") for k, v in counter.most_common())

    for name, c in sorted(classes.items(), key=lambda kv: -kv[1]["n"]):
        lines.append("| %s | %d | %d | %s |" % (name, c["n"], c["on"], pol(c["ship"])) + (" %s |" % pol(c["prop"]) if layer else ""))
    lines.append("")

    groups = collections.OrderedDict()
    for r in base:
        p = by_key.get(key_of(r))
        new = p["policy"] if p else r["policy"]
        new_class = p["class"] if p else r["class"]
        changed = new != r["policy"] or new_class != r["class"]
        if not (changed or show_all):
            continue
        k = (room(r["path"]), new_class, (p or r)["rule"], group_name(r["path"]), r["type"], r["range"], r["intensity"], col(r["color"]), r["policy"], new)
        g = groups.setdefault(k, [0, 0])
        g[0] += 1
        g[1] += 1 if r["active"] else 0
    title = "Rule diff: what %s changes" % layer_name if layer and not show_all else "Every light"
    lines += ["## %s" % title, ""]
    if not groups:
        lines.append("Nothing differs.")
    else:
        lines += ["| Room | class | rule | light | n (on) | type | range | intensity | colour | ships as | %s |" % (layer_name or "-"),
                  "|---|---|---|---|---|---|---|---|---|---|---|"]
        for k, g in sorted(groups.items(), key=lambda kv: (kv[0][0], kv[0][1], kv[0][3])):
            lines.append("| %s | %s | %s | %s | %d (%d) | %s | %g | %g | %s | %s | %s |" % (k[0], k[1], k[2] or "-", k[3], g[0], g[1], k[4], k[5], k[6], k[7], k[8], k[9]))
    lines.append("")
    path = os.path.join(out, "lights_%s.md" % scene.replace(" ", "_"))
    open(path, "w", encoding="utf-8", newline="\n").write("\n".join(lines))
    return path, sum(g[0] for g in groups.values())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("run")
    ap.add_argument("--layer", default="proposed")
    ap.add_argument("--all", action="store_true")
    ap.add_argument("--out")
    a = ap.parse_args()
    out = a.out or a.run
    for f in sorted(glob.glob(os.path.join(a.run, "lights_*.json"))):
        name = os.path.basename(f)[7:-5]
        if "." in name:
            continue  # a layer's file
        base = json.load(open(f, encoding="utf-8"))
        lf = os.path.join(a.run, "lights_%s.%s.json" % (name, a.layer))
        layer = json.load(open(lf, encoding="utf-8"))["lights"] if os.path.exists(lf) else None
        path, n = write(base["scene"], base["lights"], layer, a.layer if layer else None, out, a.all or layer is None)
        print("%s: %d lights, %d listed -> %s" % (base["scene"], len(base["lights"]), n, path))


if __name__ == "__main__":
    main()
