#!/usr/bin/env python3
"""Write camera paths from poses given relative to a known-good base pose (docs/trailer.md, "Authoring paths").

    python scripts/trailer-paths.py bench/trailer/paths.json            # writes bench/cinematics/<name>.json per path
    python scripts/trailer-paths.py spots.json --scout bench/suites/x.json   # cut keys + a one-frame-per-key suite

A path's keys are offsets in the base pose's own frame: [forward, right, up, yaw, pitch, seconds, ease, fov]
(metres along the base view direction / to its right / up; yaw and pitch added to the base's; seconds to the next key).
The base pose is a pose the player stood at (the end of a recorded route: bench/recordings), so "forward 8, up 2"
is read off a screenshot of that pose instead of guessed in world coordinates. "LookAt": [forward, right, up]
makes every key face that point (yaw / pitch are then added to the facing direction).

--scout: every key becomes a held "cut" key and a suite renders one low-sample quarter-size frame per key, to check
framing and that no key is inside a wall, before spending render time on the move.
"""
import argparse
import json
import math
import os


def world(base, fwd, right, up):
    yaw = math.radians(base["Yaw"])
    f, r = (math.sin(yaw), math.cos(yaw)), (math.cos(yaw), -math.sin(yaw))
    x, y, z = base["Player"]
    return [round(x + f[0] * fwd + r[0] * right, 3), round(y + up, 3), round(z + f[1] * fwd + r[1] * right, 3)]


def keys_of(spec, scout=False):
    base = spec["Base"]
    look = spec.get("LookAt")
    out = []
    for k in spec["Keys"]:
        fwd, right, up = k[0], k[1], k[2]
        dyaw, dpitch = (k[3] if len(k) > 3 else 0), (k[4] if len(k) > 4 else 0)
        if look:
            # Face the target from the eye: local direction -> yaw offset (right is +yaw) and pitch (up is +).
            df, dr, du = look[0] - fwd, look[1] - right, look[2] - up
            yaw = base["Yaw"] + math.degrees(math.atan2(dr, df)) + dyaw
            pitch = math.degrees(math.atan2(du, math.hypot(df, dr))) + dpitch
        else:
            yaw, pitch = base["Yaw"] + dyaw, base.get("Pitch", 0) + dpitch
        out.append({
            "Player": world(base, fwd, right, up), "Yaw": round(yaw, 2), "Pitch": round(pitch, 2),
            "Fov": (k[7] if len(k) > 7 else spec.get("Fov", 0)),
            "Duration": 1.0 if scout else (k[5] if len(k) > 5 else 2.0),
            "Ease": "cut" if scout else (k[6] if len(k) > 6 else "linear"),
        })
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("spec", help="JSON: a list of { Name, Scene, Base: { Player, Yaw, Pitch }, Keys, [LookAt], [Fov], [Seconds], [Note], [Path] }")
    ap.add_argument("--out", help="folder for the path files (default: bench/cinematics next to the repo's scripts)")
    ap.add_argument("--scout", help="write this suite file: one frame per key (needs Path, a bench scenario Path block, per spec)")
    ap.add_argument("--mode", default="pathtrace", help="scout render mode (default pathtrace)")
    ap.add_argument("--samples", type=int, default=8)
    a = ap.parse_args()
    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out = os.path.abspath(a.out or os.path.join(repo, "bench", "cinematics"))
    os.makedirs(out, exist_ok=True)
    specs = json.load(open(a.spec, encoding="utf-8-sig"))
    scenarios = []
    for s in specs:
        name = ("scout-" if a.scout else "") + s["Name"]
        keys = keys_of(s, scout=bool(a.scout))
        if s.get("Seconds") and not a.scout:
            # "Seconds": the whole move takes this long (every segment is stretched by the same factor).
            k = float(s["Seconds"]) / max(1e-6, sum(x["Duration"] for x in keys[:-1]))
            for x in keys:
                x["Duration"] = round(x["Duration"] * k, 3)
        doc = {"Version": 1, "Name": name, "Scene": s["Scene"]}
        if s.get("Note"):
            doc["Note"] = s["Note"]
        doc["Keys"] = keys
        path = os.path.join(out, name + ".json")
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write("{\n")
            for key in ("Version", "Name", "Scene", "Note"):
                if key in doc:
                    f.write(f'  "{key}": {json.dumps(doc[key])},\n')
            f.write('  "Keys": [\n' + ",\n".join("    " + json.dumps(k) for k in keys) + "\n  ]\n}\n")
        total = sum(k["Duration"] for k in keys[:-1])
        print(f"{name}: {len(keys)} keys, {total:.2f}s -> {path}")
        if a.scout:
            suite_dir = os.path.dirname(os.path.abspath(a.scout))
            scenarios.append({
                "Name": name, "Scene": s["Scene"], "Path": s["Path"], "Modes": ["pathtrace"], **s.get("Scenario", {}),
                "Cinematic": {"File": os.path.relpath(path, suite_dir).replace("\\", "/"), "Mode": a.mode, "Fps": 1, "Seconds": len(keys) - 1,
                              "Samples": a.samples, "Aa": 1, "OutputScale": 0.25, "Encode": False, "Preroll": 30, "TimeScale": 0,
                              **s.get("Cinematic", {})},
            })
    if a.scout:
        with open(a.scout, "w", encoding="utf-8", newline="\n") as f:
            json.dump({"Name": os.path.splitext(os.path.basename(a.scout))[0],
                       "Note": "Camera scouting (scripts/trailer-paths.py --scout): one low-sample frame per candidate key.",
                       "Scenarios": scenarios}, f, indent=2)
        print(f"suite -> {a.scout}")


if __name__ == "__main__":
    main()
