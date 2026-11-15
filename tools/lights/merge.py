"""Builds a shipped light override file (data/lights/<scene>.json) from the classifier's and the fit's output
(docs/lighting-overrides.md).

    py -3 tools/lights/merge.py --classified "bench/results/classified/Level 7-3.json" --fit "bench/results/fit/Level 7-3.json" \
        --out "data/lights/Level 7-3.json"

- Entries the fit touched (source contains "fit") win: their intensity / colour already include the classifier's change
  the fit started from.
- Other classifier entries are kept, except fill entries the current rules no longer make (a light named like a fixture,
  or one a script drives: the classifier fixed on 2026-11-15), so an older classification can be shipped consistently.
"""
import argparse
import json
import re

FIXTURE_WORDS = re.compile(r"lamp|lantern|chandelier|candle|torch|brazier|sconce|bulb|fire|flame|light ?fixture|neon|fluor", re.I)


def key(e):
    p = e["pos"]
    return f"{e['path']}@{p[0]:.1f},{p[1]:.1f},{p[2]:.1f}"


def stale_fill(e):
    if e.get("class") != "fill":
        return False
    parents = e["path"].rsplit("/", 1)[0] if "/" in e["path"] else ""
    note = e.get("note", "")
    return bool(FIXTURE_WORDS.search(parents)) or "scripted:" in note


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--classified")
    ap.add_argument("--fit")
    ap.add_argument("--out", required=True)
    ap.add_argument("--note", default="")
    a = ap.parse_args()
    entries, added, scene = {}, [], None
    dropped = 0
    if a.classified:
        c = json.load(open(a.classified, encoding="utf-8-sig"))
        scene = c.get("scene")
        for e in c.get("lights", []):
            if stale_fill(e):
                dropped += 1
                continue
            entries[key(e)] = e
        added += c.get("added", [])
    fitted = 0
    if a.fit:
        f = json.load(open(a.fit, encoding="utf-8-sig"))
        scene = scene or f.get("scene")
        for e in f.get("lights", []):
            if "fit" not in (e.get("source") or ""):
                continue
            if stale_fill(e):
                # The fit's factor already includes the stale fill dimming it started from: keep the product, fix the label.
                e["class"] = "fixture:named"
                e["note"] = re.sub(r"fill: intensity x[0-9.]+ \(bounce covers it\)", "named fixture (fill rule withdrawn)", e.get("note", ""))
            entries[key(e)] = e
            fitted += 1
    out = {
        "version": 1,
        "scene": scene,
        "note": ("Shipped light overrides (docs/lighting-overrides.md): classifier fixes + per-light fit. " + a.note).strip(),
        "lights": sorted(entries.values(), key=lambda e: e["path"]),
        "added": added,
    }
    with open(a.out, "w", encoding="utf-8", newline="\r\n") as fo:
        json.dump(out, fo, indent=2)
        fo.write("\n")
    print(f"{a.out}: {len(out['lights'])} entries ({fitted} fitted, {dropped} stale fill entries dropped)")


if __name__ == "__main__":
    main()
