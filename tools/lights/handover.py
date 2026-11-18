"""Per level: what became of the fixture lights (docs/lighting-overrides.md, "Handover").

    py -3 tools/lights/handover.py bench/results [--since <stamp>] [--out file.md] [--detail]

Reads the light censuses (lights_<scene>.json, written by a bench run with -ultrart-light-census; the newest census
of a level wins) and counts the lights of the fixture classes (fixture:*), without the pooled blood-splatter lights:

  handed    the light is off, the fixture's glowing part gives its output (a rule with "handover")
  hybrid    the part glows and the light stays at a fraction (a hooded fixture)
  emissive  off by an older, hand-tuned level rule ("set": intensity 0) next to an emitter rule
  flame     the fixture is a flame (a fire sprite or particles): a flame-driven light, not a surface
  kept      everything else, with the reason:
              no part     the fixture has no glowing surface of its own (lit materials only, or baked into the level)
              beam        its only glowing mesh is a light-shaft mesh (LightPillar): the beam channel's
              part, kept  it has a glowing part and no rule hands it over (a scripted light, or not covered yet)
              no fixture  the light hangs in the air
"""
import argparse
import collections
import glob
import json
import os
import re

ORDER = ["handed", "hybrid", "emissive", "flame", "no part", "beam", "part, kept", "no fixture"]


def verdict(r):
    h = r.get("handover")
    if h in ("handed", "hybrid"):
        return h
    fx = r.get("fixture") or []
    names = [f.rsplit(":", 1)[0] for f in fx]
    kinds = {f.rsplit(":", 1)[0]: f.rsplit(":", 1)[1] for f in fx if ":" in f}
    pol = r.get("policy") or ""
    if re.match(r"x0(,|$)", pol) and not r["class"].startswith("fixture:torch"):
        return "emissive"
    if any(n.startswith("FireMaterial") or n.startswith("Campfire") for n in names) or r["class"] == "fixture:torch":
        return "flame"
    if not fx:
        return "no fixture"
    glow = [n for n, k in kinds.items() if k in ("emissive", "unlit")]
    if not glow:
        return "no part"
    if all(n.startswith("LightPillar") for n in glow):
        return "beam"
    return "part, kept"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("root")
    ap.add_argument("--since", default="")
    ap.add_argument("--out", default="")
    ap.add_argument("--detail", action="store_true", help="also list the kept fixtures per level")
    a = ap.parse_args()
    newest = {}
    for f in glob.glob(os.path.join(a.root, "*", "lights_*.json")):
        base = os.path.basename(f)[len("lights_"):-len(".json")]
        if "." in base:
            continue
        run = os.path.basename(os.path.dirname(f))
        if a.since and run < a.since:
            continue
        if base not in newest or run > newest[base][0]:
            newest[base] = (run, f)

    def key(level):
        n = level.replace("Level ", "")
        return (n[0] == "P", n[-1] in "SE", n)

    lines = ["| Level | fixture lights | " + " | ".join(ORDER) + " |", "|---|---|" + "---|" * len(ORDER)]
    total = collections.Counter()
    detail = []
    for level in sorted(newest, key=key):
        doc = json.load(open(newest[level][1], encoding="utf-8"))
        c = collections.Counter()
        kept = collections.Counter()
        for r in doc.get("lights", []):
            if not r["class"].startswith("fixture") or "BloodsplatterManager" in r["path"]:
                continue
            v = verdict(r)
            c[v] += 1
            if v in ("no part", "part, kept", "beam", "no fixture"):
                parts = r["path"].split("/")
                name = re.sub(r"\s*\(\d+\)$", "", parts[-2] if len(parts) > 1 else parts[-1])
                kept[(name, v)] += 1
        n = sum(c.values())
        if n == 0:
            continue
        total.update(c)
        lines.append("| %s | %d | %s |" % (level.replace("Level ", ""), n, " | ".join(str(c[k]) if c[k] else "" for k in ORDER)))
        if kept:
            detail.append("- **%s**: %s" % (level.replace("Level ", ""), "; ".join("%s x%d (%s)" % (k[0], v, k[1]) for k, v in kept.most_common(8))))
    lines.append("| **all** | %d | %s |" % (sum(total.values()), " | ".join(str(total[k]) for k in ORDER)))
    text = "\n".join(lines) + "\n"
    if a.detail:
        text += "\nKept, per level (the largest groups):\n\n" + "\n".join(detail) + "\n"
    if a.out:
        with open(a.out, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
    print(text)


if __name__ == "__main__":
    main()
