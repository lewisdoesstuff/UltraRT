"""Writes the aliasing measurement suite and its camera paths (docs/aliasing.md).

    py -3 tools/aliasing/make_suite.py            # bench/suites/aliasing.json + bench/cinematics/alias-*.json
    py -3 tools/aliasing/make_suite.py --quick    # bench/suites/aliasing-quick.json: one pose, live + refs, no pan refs

Per pose one cinematic scenario renders, with the world frozen (TimeScale 0), so every take shows the same instant:
  - a path: HOLD frames still at the pose, then SLOW frames of a slow pan (sub-pixel per frame), then FAST frames of a
    fast pan + strafe (about 10 px per frame at the screen centre);
  - live takes (the mode as played: trace scale, denoiser / TAAU / DLSS RR and their histories) of off, relight, hybrid
    and pathtrace over the whole path;
  - references from the hold's last instant on: each mode accumulated at full resolution with AA x SAMPLES (Halton
    sub-pixel jitter, a box-filtered pixel), and the game's own image with AA x16;
  - un-jittered references (AA 1, the same sample count in one pixel centre) for the still frame only: the converged
    image with the live frame's aliasing, which splits a live frame's error into aliasing and everything else;
  - the instance, normals, depth and spec-params debug views (AA 1, full resolution) for the edge and specular masks.
tools/aliasing/analyze.py turns a run into the numbers.
"""
import argparse
import json
import math
import os

FPS = 60
HOLD, SLOW, FAST = 8, 12, 12          # frames
SLOW_YAW, SLOW_STRAFE = 0.05, 0.005   # per frame: degrees, metres (3 deg/s, 0.3 m/s: ~0.5 px/frame at 1440p)
FAST_YAW, FAST_STRAFE = 1.0, 0.1      # per frame: 60 deg/s, 6 m/s (~10 px/frame)
AA_REF, SAMPLES_REF = 8, 16           # accumulated references: 8 jitter positions x 16 frames (>= 16: LDR AA-average trap)
AA_OFF = 16                           # the game's image is not noisy: more jitter positions for the same cost

POSES = [
    # name, scene, approach recording, load, feet position, yaw, pitch, what it stresses
    ("7-1 graves", "Level 7-1", "../recordings/u1118-7-1-2.json", None, (8.9406, -10.1772, 315.8913), 39.0, -24.88,
     "rails, crosses, tree branches and grass against a bright sky: thin geometry"),
    ("1-4 hall", "Level 1-4", "../recordings/u1118-1-4-4.json", "checkpoint", (1.1189, -16.5404, 575.8654), 359.04, 3.08,
     "long dark hall: distant lamp posts, window frames, pillars"),
    ("0-1 metal", "Level 0-1", "../recordings/0-1-metal.json", None, (35.0840, 0.0, 386.1976), 38.52, -7.8,
     "the statue room's metal floor and walls: specular on PBR normal maps"),
    ("8-2 street", "Level 8-2", "../recordings/8-2-c.json", None, (5.772, -2.76, 379.065), 226.0, -1.0,
     "wet street and pool: reflections, lamp posts"),
]


def slug(name):
    return name.replace(" ", "-")


def path_file(name, scene, pos, yaw, pitch):
    """Hold (a cut segment), then a slow and a fast pan with a sideways strafe."""
    def right(y):
        r = math.radians(y)
        return (math.cos(r), 0.0, -math.sin(r))
    rx, _, rz = right(yaw)
    def key(off, dyaw, dur, ease):
        return {"Player": [round(pos[0] + rx * off, 4), round(pos[1], 4), round(pos[2] + rz * off, 4)],
                "Yaw": round(yaw + dyaw, 4), "Pitch": pitch, "Duration": round(dur, 7), "Ease": ease}
    s1, y1 = SLOW * SLOW_STRAFE, SLOW * SLOW_YAW
    s2, y2 = s1 + FAST * FAST_STRAFE, y1 + FAST * FAST_YAW
    return {
        "Version": 1, "Name": "alias-" + slug(name), "Scene": scene,
        "Note": f"Aliasing measurement (docs/aliasing.md): {HOLD} frames still, {SLOW} frames of a slow pan "
                f"({SLOW_YAW * FPS:g} deg/s, {SLOW_STRAFE * FPS:g} m/s), {FAST} of a fast one ({FAST_YAW * FPS:g} deg/s, "
                f"{FAST_STRAFE * FPS:g} m/s) at {FPS} fps. Written by tools/aliasing/make_suite.py.",
        "Keys": [key(0, 0, HOLD / FPS, "cut"), key(0, 0, SLOW / FPS, "linear"), key(s1, y1, FAST / FPS, "linear"),
                 key(s2, y2, 1.0, "linear")],
    }


# Volumetrics off in every traced take: the offline renderer applies the game's fog only on the upsampled path
# (Plugin.Volumetrics.cs, applyGameFog = upsampled), so accumulated references would lack the fog live frames have.
NOVOL = "+vol.enabled=0"
# Live variants (specular AA): Toksvig roughness from the filtered-away normal-map variance off, and tripled.
VARIANTS = [("tk0", "mat.toksvig=0"), ("tk3", "mat.toksvig=3")]
VARIANT_POSES = ("0-1 metal", "8-2 street")


def takes(quick, variants=False):
    t0 = HOLD / FPS
    one = 0.001  # Seconds of a one-frame take
    pan = (SLOW + FAST) / FPS
    out = [{"Name": n, "Mode": "current", "View": v, "Aa": 1, "Samples": 1, "Start": t0, "Seconds": one if quick else pan}
           for n, v in (("inst", "instance"), ("nrm", "normals"), ("depth", "depth"), ("spec", "spec-params"))]
    out.append({"Name": "off", "Mode": "off", "Aa": 1, "Start": 0, "Seconds": t0 + pan})
    out.append({"Name": "off-ref", "Mode": "off", "Aa": AA_OFF, "Start": t0, "Seconds": one if quick else pan})
    for m in ("relight", "hybrid", "pathtrace"):
        v0 = m + NOVOL
        out.append({"Name": m, "Mode": "live", "View": v0, "Start": 0, "Seconds": t0 + pan})
        out.append({"Name": m + "-ref", "Mode": "current", "View": v0, "Aa": AA_REF, "Samples": SAMPLES_REF, "Start": t0,
                    "Seconds": one if quick else pan})
        out.append({"Name": m + "-ref1", "Mode": "current", "View": v0, "Aa": 1, "Samples": AA_REF * SAMPLES_REF,
                    "Start": t0, "Seconds": one})
        if variants and m != "hybrid":
            for tag, v in VARIANTS:
                out.append({"Name": f"{m}@{tag}", "Mode": "live", "View": f"{v0}+{v}", "Start": 0, "Seconds": t0 + pan})
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--quick", action="store_true", help="one pose, references for the still frame only")
    ap.add_argument("--msaa", action="store_true", help="also write aliasing-msaa.json (the MSAA probe)")
    a = ap.parse_args()
    root = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
    poses = POSES[:1] if a.quick else POSES
    scenarios = []
    for name, scene, approach, load, pos, yaw, pitch, note in poses:
        pf = path_file(name, scene, pos, yaw, pitch)
        with open(os.path.join(root, "bench", "cinematics", pf["Name"] + ".json"), "w", newline="\n") as f:
            json.dump(pf, f, indent=2)
            f.write("\n")
        path = {"Type": "static", "Approach": approach, "Duration": 1}
        if load:
            path["Load"] = load
        scenarios.append({
            "Name": name, "Scene": scene, "Note": note, "Path": path, "Modes": ["pathtrace"], "Enemies": "blind",
            "Cinematic": {"File": f"../cinematics/{pf['Name']}.json", "Fps": FPS, "TimeScale": 0, "Preroll": 90,
                          "OutputScale": 1, "Encode": False, "Exposure": "auto", "Render": False,
                          "Takes": takes(a.quick, name in VARIANT_POSES)},
        })
    suite = {"Name": "aliasing-quick" if a.quick else "aliasing",
             "Note": "Aliasing per mode (docs/aliasing.md). Written by tools/aliasing/make_suite.py; analyse with "
                     "tools/aliasing/analyze.py. Needs -TimeoutSec 3600.",
             "Scenarios": scenarios}
    with open(os.path.join(root, "bench", "suites", suite["Name"] + ".json"), "w", newline="\n") as f:
        json.dump(suite, f, indent=1)
        f.write("\n")
    if a.msaa:
        # The MSAA probe: the game's image (off) and hybrid with the game's targets at 4x MSAA, still frame only, plus
        # frame times of off vs off at 4x in a plain static scenario (Modes alternate within one pass).
        t0 = HOLD / FPS
        msaa = {"Name": "aliasing-msaa", "Note": "MSAA feasibility probe (docs/aliasing.md, 'MSAA on the game's raster'). "
                "Written by tools/aliasing/make_suite.py --msaa.", "Scenarios": []}
        for name, scene, approach, load, pos, yaw, pitch, note in (POSES[0], POSES[2]):
            path = {"Type": "static", "Approach": approach, "Duration": 1}
            if load:
                path["Load"] = load
            msaa["Scenarios"].append({
                "Name": name + " msaa", "Scene": scene, "Path": path, "Modes": ["hybrid"], "Enemies": "blind",
                "Cinematic": {"File": f"../cinematics/alias-{slug(name)}.json", "Fps": FPS, "TimeScale": 0, "Preroll": 90,
                              "OutputScale": 1, "Encode": False, "Render": False, "Takes": [
                                  {"Name": "off", "Mode": "off", "Aa": 1, "Start": t0, "Seconds": 0.001},
                                  {"Name": "off-msaa4", "Mode": "off", "View": "off+plugin._msaaProbe=4", "Aa": 1, "Start": t0, "Seconds": 0.001},
                                  {"Name": "off-ref", "Mode": "off", "Aa": AA_OFF, "Start": t0, "Seconds": 0.001},
                                  {"Name": "hybrid-msaa4", "Mode": "live", "View": "hybrid+plugin._msaaProbe=4", "Start": t0, "Seconds": 0.001},
                              ]}})
            msaa["Scenarios"].append({
                "Name": name + " msaa cost", "Scene": scene, "Path": {**path, "Duration": 12},
                "Modes": ["off", "off+plugin._msaaProbe=4"], "Segment": 2, "Enemies": "blind"})
        with open(os.path.join(root, "bench", "suites", "aliasing-msaa.json"), "w", newline="\n") as f:
            json.dump(msaa, f, indent=1)
            f.write("\n")


if __name__ == "__main__":
    main()
