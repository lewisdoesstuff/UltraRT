"""Where the slow frames of a bench run come from.

    py -3 tools/perf/hitches.py bench/results/<run> [--over 12] [--scenario NAME] [--list 15]

Reads frames_<scenario>.csv (one row per frame: frame time, main-thread scripts and rendering, plugin sections, GC,
scene counts) and, per mode, attributes every frame slower than --over ms to the largest excess over that mode's
median frame:

  scripts   the game's Update / physics / animation (and ours hooked into them)
  render    main-thread rendering outside the plugin's Trace (culling, the game's cameras, command recording)
  <section> one of the plugin's sections (Perf.Sec: hier, static, dynamic, motion, emissive, lights, lightsamp);
            "trace" is the rest of Plugin.Trace
  wait      the rest of the frame: the main thread waiting for the render thread or the GPU
  gc        a frame with a gen-0 collection whose excess is in scripts or render

docs/performance.md, "Hitches".
"""
import argparse
import collections
import csv
import glob
import os
import statistics


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("run")
    ap.add_argument("--over", type=float, default=12.0)
    ap.add_argument("--scenario")
    ap.add_argument("--list", type=int, default=15)
    a = ap.parse_args()

    for path in sorted(glob.glob(os.path.join(a.run, "frames_*.csv"))):
        name = os.path.basename(path)[7:-4]
        if a.scenario and a.scenario.replace(" ", "_") != name:
            continue
        rows = list(csv.DictReader(open(path, encoding="utf-8")))
        secs = [c[:-3] for c in rows[0].keys() if c.endswith("_ms") and c not in ("dt_ms", "scripts_ms", "render_ms")] if rows else []
        inner = [s for s in secs if s != "trace"]
        in_trace = [s for s in inner if s != "viewmodel"]  # the viewmodel pass is recorded outside Plugin.Trace
        by_mode = collections.defaultdict(list)
        for i, r in enumerate(rows):
            if r["mode"].endswith("*"):
                continue  # frames right after a mode switch are not counted by the bench either
            f = {k: float(r[k]) for k in ("t", "dt_ms", "scripts_ms", "render_ms")}
            for s in secs:
                f[s] = float(r[s + "_ms"])
            f["gc"] = int(r["gc0"])
            f["instances"] = int(r.get("instances") or 0)
            f["prev_instances"] = int(rows[i - 1].get("instances") or 0) if i else f["instances"]
            # Parts that add up to the frame: scripts, plugin sections, the rest of Trace, the rest of rendering, wait.
            f["trace_rest"] = max(0.0, f.get("trace", 0.0) - sum(f[s] for s in in_trace))
            f["render_rest"] = max(0.0, f["render_ms"] - f.get("trace", 0.0) - f.get("viewmodel", 0.0))
            f["wait"] = max(0.0, f["dt_ms"] - f["scripts_ms"] - f["render_ms"])
            by_mode[r["mode"]].append(f)

        print("## %s" % name)
        for mode, fs in by_mode.items():
            parts = ["scripts_ms", "render_rest", "trace_rest", "wait"] + inner
            label = {"scripts_ms": "scripts", "render_rest": "render", "trace_rest": "trace", "wait": "wait"}
            med = {p: statistics.median(f[p] for f in fs) for p in parts}
            slow = [f for f in fs if f["dt_ms"] > a.over]
            total = sum(f["dt_ms"] for f in fs)
            print("\n%s: %d frames, median %.2f ms, %d over %.0f ms (%.1f%% of frames, %.1f%% of the time)" % (
                mode, len(fs), statistics.median(f["dt_ms"] for f in fs), len(slow), a.over,
                100.0 * len(slow) / max(1, len(fs)), 100.0 * sum(f["dt_ms"] for f in slow) / max(total, 1e-9)))
            cause = collections.Counter()
            excess = collections.Counter()
            detail = []
            for f in slow:
                ex = {p: f[p] - med[p] for p in parts}
                top = max(ex, key=ex.get)
                c = label.get(top, top)
                if f["gc"] and top in ("scripts_ms", "render_rest", "trace_rest"):
                    c = "gc"
                cause[c] += 1
                excess[c] += ex[top]
                detail.append((f["dt_ms"], f["t"], c, ex[top], f["instances"] - f["prev_instances"]))
            for c, n in cause.most_common():
                print("  %-10s %4d frames, mean excess %.1f ms" % (c, n, excess[c] / n))
            for dt, t, c, ex, dinst in sorted(detail, reverse=True)[:a.list]:
                print("    %6.1f ms at %6.2f s: %-9s +%.1f ms%s" % (dt, t, c, ex, (", instances %+d" % dinst) if dinst else ""))
        print()


if __name__ == "__main__":
    main()
