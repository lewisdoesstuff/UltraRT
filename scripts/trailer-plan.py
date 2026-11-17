#!/usr/bin/env python3
"""From one shot list, write the bench suites that render a trailer's shots and the edit list that assembles them
(docs/trailer.md, "The plan").

    python scripts/trailer-plan.py bench/trailer/final-plan.json [--tier draft|final]

A plan has the music, the bar grid, quality tiers and the shots in order. Every shot is one camera path whose time 0
is the clip's first frame. Kinds:
  switch  the game's image, then a hard switch to the path-traced one "Off" seconds in
  sweep   the game's image for "Off" seconds, then one sweep of the divider ("Sweep" seconds), then path traced
  beauty  path traced only
  title   a card (the edit list's title clip fields)
Lengths are "Bars" of the music; the first clip may add "Lead" seconds so that every later cut falls on a bar.
Comparison shots are rendered as paired captures (docs/cinematics.md): the world runs, both images show the same
world state in every frame, and each image is only rendered for the stretch the edit uses.

Writes, per tier: bench/suites/<Name>-<tier>-<n>.json (at most "PerRun" scenarios each) and
bench/trailer/<Name>-<tier>.json. Sources in the edit list are wildcards, so the newest run of each suite is used.
"""
import argparse
import json
import os


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("plan")
    ap.add_argument("--tier", help="only this quality tier (default: all)")
    ap.add_argument("--only", help="also write <Name>-<tier>-redo.json with just these shots (names, comma separated): a re-render of some shots")
    a = ap.parse_args()
    plan = json.load(open(a.plan, encoding="utf-8-sig"))
    here = os.path.dirname(os.path.abspath(a.plan))
    suites = os.path.normpath(os.path.join(here, "..", "suites"))
    name, bar = plan["Name"], float(plan.get("Bar", 1.5))
    margin = float(plan.get("Margin", 0.25))
    for tier, q in plan["Tiers"].items():
        if a.tier and tier != a.tier:
            continue
        fps = float(q.get("Fps", 60))
        scenarios, clips, t = [], [], 0.0
        for sh in plan["Shots"]:
            kind = sh.get("Kind", "beauty")
            dur = round(float(sh.get("Bars", 0)) * bar + float(sh.get("Lead", 0)) + float(sh.get("Seconds", 0)), 4)
            if kind in ("title", "end"):
                c = {"Type": "title", "Name": sh["Name"], "Duration": dur}
                c.update(sh.get("Card", {}))
                clips.append(c)
                t += dur
                continue
            sname = sh["Name"]
            folder = "cine_" + "".join(ch if ch.isalnum() or ch in "-" else "_" for ch in sname)
            # "In": the clip starts this far into the rendered take; "Render": seconds of the path to render (default: the
            # clip), e.g. a longer take of a shot whose best moment is found afterwards.
            t_in = float(sh.get("In", 0))
            mode = q.get("Mode", "pathtrace")  # live: the real-time pipeline (DLSS RR); pathtrace: offline accumulation
            cine = {"File": "../cinematics/%s.json" % sh["File"], "Mode": mode, "Fps": fps,
                    "Seconds": round(max(float(sh.get("Render", 0)), t_in + dur) + 2.0 / fps, 3),
                    "OutputScale": q.get("OutputScale", 1), "TimeScale": sh.get("TimeScale", 1),
                    "Preroll": q.get("Preroll", 60), "Encode": False}
            for k in ("Samples", "Aa"):
                if k in q:
                    cine[k] = q[k]
            off = float(sh.get("Off", 0))
            sweep = float(sh.get("Sweep", 1.5))
            if kind in ("switch", "sweep"):
                cine["Paired"] = True
                cine["OnFrom"] = round(max(0.0, t_in + off - margin), 3)
                cine["OffUntil"] = round(t_in + off + (sweep if kind == "sweep" else 0) + margin, 3)
            cine.update(sh.get("Cinematic", {}))
            cine.update((sh.get("TierCinematic") or {}).get(tier, {}))  # per-tier overrides of one shot (e.g. fewer samples)
            for k, v in (q.get("Cinematic") or {}).items():
                cine.setdefault(k, v)
            sc = {"Name": sname, "Scene": sh["Scene"], "Path": sh["Path"], "Modes": [q.get("Modes", "pathtrace") + sh.get("ModeAdd", "")]}  # ModeAdd: runtime variants for one shot ("+key=value")
            sc.update(plan.get("Scenario", {}))
            sc.update(sh.get("Scenario", {}))
            sc["Cinematic"] = cine
            scenarios.append(sc)
            src = "*_%s-%s-*_alt/%s" % (name, tier, folder)
            if kind == "beauty":
                c = {"Type": "shot", "Name": sname, "Source": src, "In": t_in, "Out": round(t_in + dur, 4)}
            else:
                keys = [[0, 0], [round(off - 0.001, 3), 0], [off, 1]] if kind == "switch" else [[0, 0], [off, 0], [round(off + sweep, 3), 1]]
                wipe = {"Keys": keys, "Slant": sh.get("Slant", 0 if kind == "switch" else 0.18)}
                if sh.get("Flip"):
                    wipe["Flip"] = True
                c = {"Type": "wipe", "Name": sname, "Before": src + "_off", "After": src + "_" + mode, "In": t_in, "Out": round(t_in + dur, 4), "Wipe": wipe}
                if "Labels" in plan:
                    c["Labels"] = plan["Labels"]
            for k in ("Caption", "Tag", "FadeIn", "FadeOut", "Transition", "Labels"):
                if k in sh:
                    c[k] = sh[k]
            clips.append(c)
            t += dur
        # suites
        per = int(plan.get("PerRun", 4))
        old = [f for f in os.listdir(suites) if f.startswith("%s-%s-" % (name, tier)) and f.endswith(".json")]
        for f in old:
            os.remove(os.path.join(suites, f))
        runs = [(str(n), scenarios[i:i + per]) for n, i in enumerate(range(0, len(scenarios), per), 1)]
        if a.only:
            want = [x.strip() for x in a.only.split(",")]
            runs.append(("redo", [sc for sc in scenarios if sc["Name"] in want]))
        for n, group in runs:
            doc = {"Name": "%s-%s-%s" % (name, tier, n),
                   "Note": "Written by scripts/trailer-plan.py from bench/trailer/%s (tier %s, run %s): do not edit, change the plan." %
                           (os.path.basename(a.plan), tier, n),
                   "Scenarios": group}
            with open(os.path.join(suites, doc["Name"] + ".json"), "w", encoding="utf-8", newline="\r\n") as f:
                json.dump(doc, f, indent=2)
                f.write("\n")
        # edit list
        edit = {"Name": "%s-%s" % (name, tier),
                "Note": "Written by scripts/trailer-plan.py from %s (tier %s): do not edit, change the plan." % (os.path.basename(a.plan), tier),
                "Root": "../results", "Width": q["Width"], "Height": q["Height"], "Fps": fps, "Crf": q.get("Crf", 16), "ClipCrf": q.get("ClipCrf", 12)}
        for k in ("Font", "TitleFont", "Music", "Graphics", "Labels"):
            if k in plan:
                edit[k] = plan[k]
        if q.get("Exports"):
            edit["Exports"] = q["Exports"]
        edit["Clips"] = clips
        path = os.path.join(here, "%s-%s.json" % (name, tier))
        with open(path, "w", encoding="utf-8", newline="\r\n") as f:
            f.write("{\n")
            head = [k for k in edit if k != "Clips"]
            for k in head:
                f.write("  %s: %s,\n" % (json.dumps(k), json.dumps(edit[k])))
            f.write('  "Clips": [\n' + ",\n".join("    " + json.dumps(c) for c in clips) + "\n  ]\n}\n")
        frames = sum(int(round(s["Cinematic"]["Seconds"] * fps)) for s in scenarios)
        print("%s: %d shots in %d run(s), %.2f s, %d output frames -> %s" % (tier, len(scenarios), len(runs), t, frames, path))
        tt = 0.0
        for c in clips:
            d = c.get("Duration", c.get("Out", 0) - c.get("In", 0))
            print("   %6.2f  %5.2f  %s" % (tt, d, c["Name"]))
            tt += d


if __name__ == "__main__":
    main()
