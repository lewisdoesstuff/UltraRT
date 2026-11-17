"""Which fake lights have a glowing part to hand their job to (docs/lighting-overrides.md, "Emissive fixtures").

    py -3 tools/lights/fixtures.py bench/results [--since <stamp>] [--level "Level 8-2"] [--all] [--out file.md]

Reads the light censuses (lights_<scene>.json, written by a bench run with -ultrart-light-census) and groups every
light by its fixture: the parent object, numbering removed ("StreetlightFraud (2)" = "StreetlightFraud"). Per group:
the number of lights, their class, and the fixture's materials as the census saw them:

  emissive   the game's EMISSIVE keyword: the surface is in the emissive light list, an emitter rule can scale it
             and the lights can go (a level rule with "set": {"intensity": 0}).
  unlit      a fullbright surface that shows its texture and lights nothing: a candidate for an emitter mask
             (an emitter rule with "glow").
  lit        an ordinary surface.

Verdict per group: "emissive" (ready), "unlit" (needs a mask), "lit" (needs a mask on a lit texture, or keeps its
light), "none" (a light with no fixture: fill). Only fixture classes are listed unless --all. The newest census of a
level wins.
"""
import argparse
import collections
import glob
import json
import os
import re


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("root")
    ap.add_argument("--since", default="")
    ap.add_argument("--level", default="")
    ap.add_argument("--all", action="store_true", help="every class, not only fixture*")
    ap.add_argument("--out", default="")
    a = ap.parse_args()

    newest = {}
    for f in glob.glob(os.path.join(a.root, "*", "lights_*.json")):
        base = os.path.basename(f)[len("lights_"):-len(".json")]
        if "." in base:
            continue  # a layer's census (lights_<scene>.<layer>.json)
        run = os.path.basename(os.path.dirname(f))
        if a.since and run < a.since:
            continue
        if a.level and base != a.level:
            continue
        if base not in newest or run > newest[base][0]:
            newest[base] = (run, f)

    lines = []
    totals = collections.Counter()
    for level in sorted(newest):
        run, f = newest[level]
        doc = json.load(open(f, encoding="utf-8"))
        rows = doc.get("lights", [])
        if not any("fixture" in r for r in rows):
            continue  # a census from before the fixture field
        groups = collections.OrderedDict()
        for r in rows:
            if not a.all and not r["class"].startswith("fixture"):
                continue
            parts = r["path"].split("/")
            name = re.sub(r" \(\d+\)$", "", parts[-2]) if len(parts) > 1 else "(root)"
            mats = tuple(sorted(r.get("fixture") or []))
            g = groups.setdefault((name, r["class"], mats), [0, 0.0])
            g[0] += 1
            g[1] = max(g[1], r["intensity"])
        if not groups:
            continue
        lines.append("\n## %s (%s)\n" % (level, run))
        lines.append("| fixture | class | lights | max intensity | verdict | materials |")
        lines.append("|---|---|---|---|---|---|")
        for (name, cls, mats), (n, inten) in sorted(groups.items(), key=lambda kv: -kv[1][0]):
            kinds = set(m.rsplit(":", 1)[1] for m in mats)
            verdict = "emissive" if "emissive" in kinds else "unlit" if "unlit" in kinds else "lit" if kinds else "none"
            totals[verdict] += n
            lines.append("| %s | %s | %d | %.3g | %s | %s |" % (name, cls, n, inten, verdict, ", ".join(mats) or "-"))
    head = ["# Fixtures and their glowing parts", "",
            "Lights by verdict: " + ", ".join("%s %d" % kv for kv in totals.most_common())]
    text = "\n".join(head + lines) + "\n"
    if a.out:
        open(a.out, "w", encoding="utf-8", newline="\n").write(text)
        print(a.out)
    else:
        print(text)


if __name__ == "__main__":
    main()
