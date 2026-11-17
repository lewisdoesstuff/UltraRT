"""Offline light census: every Light in every campaign level, and what the rules make of it, without the game.

    py -3 tools/lights/census.py extract <game>\\ULTRAKILL_Data\\StreamingAssets\\aa\\StandaloneWindows64 census.json [level ...]
    py -3 tools/lights/census.py classify census.json [--rules data/lights/_rules.json] [--level 1-4] [--list CLASS] [--unmatched]

extract reads the level bundles with UnityPy (read only) and writes path, type, range, intensity, colour and shadow
type per light. The output is derived from the game's files: keep it out of the repository (bench/results is ignored).

classify applies the rules of data/lights/_rules.json (the format of docs/lighting-overrides.md, "Classes and rules")
the way the plugin does, as far as a file can know: conditions on path, type, range, intensity and saturation. Rules
with "signals" (fixture geometry, scripts: known only in the running game) are reported as "in game only" and skipped,
and the classes the plugin assigns in code are guessed from names (enemy, item). The authority for one level is the
game's own census (bench.ps1 ... -Extra "-ultrart-light-census", then tools/lights/rulediff.py); this tool is for
writing rules against all 43 levels at once.
"""
import argparse
import collections
import glob
import json
import os
import re
import sys

MOVING = ("enemy", "enemies", "projectile", "explosion", "spawneffect", "weapon", "v2", "gabriel", "minos", "leviathan",
          "ferryman", "sisyphus", "rocket", "grenade", "beam", "muzzle", "armature", "zombie", "streetcleaner", "drone",
          "virtue", "cerberus", "turret", "spider", "mannequin", "guttertank", "husk", "flameboirig", "mindflayer", "centaur")
ITEMS = ("skullblue", "skullred", "torch (item)", "pickup", "soap", "coin")


def extract(root, out, filt):
    import UnityPy

    res = {}
    for path in sorted(glob.glob(os.path.join(root, "campaign_scenes_level*.bundle"))):
        name = re.search(r"level(.+)\.bundle", os.path.basename(path)).group(1).upper()
        if filt and not any(f.lower() in name.lower() for f in filt):
            continue
        env = UnityPy.load(path)
        gos, trs, lights = {}, {}, []
        for o in env.objects:
            t = o.type.name
            if t == "GameObject":
                gos[o.path_id] = o
            elif t in ("Transform", "RectTransform"):
                trs[o.path_id] = o
            elif t == "Light":
                lights.append(o)
        go_name, tr_tree = {}, {}

        def tr_info(pid):
            if pid not in tr_tree:
                o = trs.get(pid)
                if o is None:
                    return None
                d = o.read_typetree()
                tr_tree[pid] = (d["m_GameObject"]["m_PathID"], d["m_Father"]["m_PathID"])
            return tr_tree[pid]

        def gname(pid):
            if pid not in go_name:
                o = gos.get(pid)
                go_name[pid] = o.read_typetree()["m_Name"] if o is not None else "?"
            return go_name[pid]

        go_tr = {}
        for pid in trs:
            info = tr_info(pid)
            if info:
                go_tr[info[0]] = pid
        rows = []
        for o in lights:
            d = o.read_typetree()
            parts, t, guard = [], go_tr.get(d["m_GameObject"]["m_PathID"]), 0
            while t and guard < 64:
                info = tr_info(t)
                if not info:
                    break
                parts.append(gname(info[0]))
                t = info[1]
                guard += 1
            c = d.get("m_Color", {})
            rows.append({
                "path": "/".join(reversed(parts)), "type": d.get("m_Type"), "range": d.get("m_Range"),
                "intensity": d.get("m_Intensity"), "color": [round(c.get(k, 0), 3) for k in ("r", "g", "b")],
                "shadows": d.get("m_Shadows", {}).get("m_Type"), "enabled": d.get("m_Enabled"),
            })
        res[name] = rows
        print("%s: %d lights" % (name, len(rows)), flush=True)
    json.dump(res, open(out, "w"))


def saturation(c):
    mx, mn = max(c), min(c)
    return (mx - mn) / mx if mx > 1e-5 else 0.0


def norm_path(p):
    return re.sub(r"\s*\(Clone\)", "", p)


class Rule:
    def __init__(self, d):
        self.d = d
        self.name = d.get("name") or "(unnamed)"
        self.cls = d.get("class")
        self.scene = re.compile(d["scene"], re.I) if d.get("scene") else None
        self.path = re.compile(d["path"], re.I) if d.get("path") else None
        self.not_path = re.compile(d["notPath"], re.I) if d.get("notPath") else None
        self.signals = d.get("signals") or []

    def matches(self, scene, r):
        d = self.d
        if self.scene and not self.scene.search(scene):
            return False
        t = "spot" if r["type"] == 0 else "point"
        if d.get("type") and d["type"].lower() != t:
            return False
        for key, val, lo in (("rangeMin", r["range"], True), ("rangeMax", r["range"], False),
                             ("intensityMin", r["intensity"], True), ("intensityMax", r["intensity"], False),
                             ("saturationMin", saturation(r["color"]), True), ("saturationMax", saturation(r["color"]), False)):
            if d.get(key) is not None and ((val < d[key]) if lo else (val > d[key])):
                return False
        p = norm_path(r["path"])
        if self.path and not self.path.search(p):
            return False
        if self.not_path and self.not_path.search(p):
            return False
        return True


def classify_one(scene, r, rules):
    """(class, rule name). Signals: a rule that needs one cannot match here; a rule that forbids one ("!fixture") can."""
    if r["type"] == 1:
        return "sun/skyfill", "code"
    if r["type"] not in (0, 2):
        return "other", "code"
    low = r["path"].lower()
    if low.startswith("firstroom/player/") or "/player/" in low and "firstroom" in low:
        return "player", "code (by name)"
    if any(w in low for w in ITEMS):
        return "item", "code (by name)"
    if any(w in low for w in MOVING):
        return "enemy", "code (by name)"
    for rule in rules:
        if any(not s.startswith("!") for s in rule.signals):
            continue
        if rule.matches(scene, r):
            return rule.cls, rule.name
    return "other", ""


def classify(args):
    census = json.load(open(args.census))
    rules_doc = json.load(open(args.rules, encoding="utf-8"))
    rules = [Rule(d) for d in rules_doc.get("rules", [])]
    policies = rules_doc.get("classes", {})
    in_game = [r.name for r in rules if any(not s.startswith("!") for s in r.signals)]
    total = collections.Counter()
    per_level = {}
    by_rule = collections.Counter()
    listed = []
    for level, rows in census.items():
        if args.level and args.level.lower() != level.lower():
            continue
        scene = "Level " + level
        c = collections.Counter()
        for r in rows:
            cls, rule = classify_one(scene, r, rules)
            c[cls] += 1
            total[cls] += 1
            by_rule[(cls, rule)] += 1
            if (args.list and (cls == args.list or cls.startswith(args.list + ":"))) or (args.unmatched and cls == "other" and rule == ""):
                listed.append((level, cls, rule, r))
        per_level[level] = c
    classes = [k for k, _ in total.most_common()]
    print("| Level | lights | " + " | ".join(classes) + " |")
    print("|---|---|" + "---|" * len(classes))
    for level, c in per_level.items():
        print("| %s | %d | %s |" % (level, sum(c.values()), " | ".join(str(c.get(k, 0)) for k in classes)))
    print("| all | %d | %s |" % (sum(total.values()), " | ".join(str(total[k]) for k in classes)))
    print()
    for (cls, rule), n in sorted(by_rule.items(), key=lambda kv: -kv[1]):
        pol = policies.get(cls) or policies.get(cls.split(":")[0])
        print("%6d  %-20s %-34s %s" % (n, cls, rule or "(no rule)", json.dumps(pol) if pol else ""))
    if in_game:
        print("\nin game only (need signals): " + ", ".join(in_game))
    if listed:
        print()
        groups = collections.Counter()
        for level, cls, rule, r in listed:
            key = (re.sub(r"\s*\(\d+\)", "", norm_path(r["path"]))[-100:], "spot" if r["type"] == 0 else "point", r["range"], r["intensity"], tuple(r["color"]))
            groups[key] += 1
        for key, n in groups.most_common(args.top):
            print("%5d  %s" % (n, key))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sp = ap.add_subparsers(dest="cmd", required=True)
    e = sp.add_parser("extract")
    e.add_argument("root")
    e.add_argument("out")
    e.add_argument("levels", nargs="*")
    c = sp.add_parser("classify")
    c.add_argument("census")
    c.add_argument("--rules", default=os.path.join(os.path.dirname(__file__), "..", "..", "data", "lights", "_rules.json"))
    c.add_argument("--level")
    c.add_argument("--list", help="list the lights of this class, grouped")
    c.add_argument("--unmatched", action="store_true", help="list the lights no rule matched, grouped")
    c.add_argument("--top", type=int, default=60)
    a = ap.parse_args()
    if a.cmd == "extract":
        extract(a.root, a.out, a.levels)
    else:
        classify(a)


if __name__ == "__main__":
    sys.exit(main())
