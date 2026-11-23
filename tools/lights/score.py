"""Brightness scoreboard: every RT screenshot of a bench run against the game's frame of the same pose.

    py -3 tools/lights/score.py bench/results/<run> [more runs] [--modes pathtrace,relight] [--sheet]
                                [--board bench/results/scoreboard.csv] [--label "after exposure policy"]

Per pose (a scenario's screenshot time, or a tour view) and mode, over the screen minus HUD and viewmodel (luma 0-255):

  ratio       mean luma, RT / game (what every earlier audit used)
  tint        sum |rgb/sum - game rgb/sum| x 100
  dark%       share of the frame the game leaves dark (game luma <= --dark, default 8)
  dark game / rt / delta
              mean luma of exactly those pixels in the game's frame and in the RT frame, and the difference: light on
              what the game hides (1-4's far wall and trees have the same frame mean as the game and a delta of 3)
  reveal%     share of the frame that the game leaves dark and RT shows at least --reveal-step (6) levels brighter:
              hidden geometry that became visible, also where it is too small to move the dark mean (1-4's far wall
              with the haze off: delta 0.6, reveal 1 %)
  p10 p50 p90 luma percentiles, RT / game

Verdict: ok, or the reasons it is not: bright / dim (ratio outside --lo..--hi), dark (delta over --dark-delta, when at
least 2 % of the frame is dark), reveal (reveal% over --reveal). Thresholds were set on the
user's examples of 2026-11-17 (the look-and-perf plan, 1.1): 1-4 lamps, the 1-4 bridge and the 6-2 hall must fail.

Writes <run>/score.csv and score.md, with --sheet also score.png (game | modes, numbers on the thumbnails, failures in
red), and with --board appends the rows to a CSV that accumulates over runs (same run, pose and mode: replaced).
Judge lights at fixed exposure (mode variant ae.enabled=0) and the product at the shipped settings: with auto exposure
following the game, a room that is too bright is hidden by the exposure.
"""
import argparse
import csv
import json
import os
import re
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

LUMA = np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)
FIELDS = ["run", "tag", "label", "level", "pose", "stop", "mode", "game", "rt", "ratio", "tint", "dark_pct", "dark_game", "dark_rt",
          "dark_delta", "reveal", "p10", "p50", "p90", "verdict"]


def mask_for(h, w):
    """Viewmodel (lower right) and HUD (lower left) out, as the burst statistics and sheet.py leave them out."""
    m = np.ones((h, w), bool)
    m[int(h * 0.45):, int(w * 0.45):] = False
    m[int(h * 0.70):, : int(w * 0.30)] = False
    return m


def load(path):
    a = np.asarray(Image.open(path).convert("RGB"), dtype=np.float32)
    return a, a @ LUMA


def box(a, f):
    """Mean over f x f blocks (edges cropped)."""
    if f <= 1:
        return a
    h, w = a.shape[0] // f * f, a.shape[1] // f * f
    return a[:h, :w].reshape(h // f, f, w // f, f).mean(axis=(1, 3))


def measure(game_path, rt_path, dark_t, step):
    ga, gl = load(game_path)
    ra, rl = load(rt_path)
    if ra.shape != ga.shape:
        ra = np.asarray(Image.open(rt_path).convert("RGB").resize((ga.shape[1], ga.shape[0]), Image.BILINEAR), dtype=np.float32)
        rl = ra @ LUMA
    h, w = gl.shape
    m = mask_for(h, w)
    gm, rm = ga[m].mean(axis=0), ra[m].mean(axis=0)
    g_mean, r_mean = float(gm @ LUMA), float(rm @ LUMA)
    gn, rn = gm / max(gm.sum(), 1e-3), rm / max(rm.sum(), 1e-3)
    dark = (gl <= dark_t) & m
    dark_pct = 100.0 * dark.sum() / m.sum()
    res = {
        "game": g_mean, "rt": r_mean, "ratio": r_mean / max(g_mean, 1e-3), "tint": float(np.abs(rn - gn).sum() * 100),
        "dark_pct": dark_pct, "dark_game": None, "dark_rt": None, "dark_delta": None, "reveal": None,
    }
    if dark_pct >= 2.0:
        dg, dr = float(gl[dark].mean()), float(rl[dark].mean())
        res.update(dark_game=dg, dark_rt=dr, dark_delta=dr - dg)
        # At about 640 px across (noise and single texels averaged out): blocks the game leaves dark throughout that RT
        # shows clearly brighter, as a share of the measured frame.
        f = max(1, int(round(w / 640.0)))
        gb, rb = box(gl, f), box(rl, f)
        mb = box(m.astype(np.float32), f) > 0.999
        db = (box(dark.astype(np.float32), f) > 0.999) & mb
        res["reveal"] = 100.0 * float(((rb - gb >= step) & db).sum()) / max(float(mb.sum()), 1.0)
    for q in (10, 50, 90):
        res[f"p{q}"] = (float(np.percentile(rl[m], q)) + 1.0) / (float(np.percentile(gl[m], q)) + 1.0)
    return res, ga, ra


def verdict(r, a):
    why = []
    if r["game"] >= 3.0:
        if r["ratio"] > a.hi:
            why.append(f"bright x{r['ratio']:.2f}")
        elif r["ratio"] < a.lo:
            why.append(f"dim x{r['ratio']:.2f}")
    if r["dark_delta"] is not None:
        if r["dark_delta"] > a.dark_delta:
            why.append(f"dark +{r['dark_delta']:.1f}")
        if r["reveal"] is not None and r["reveal"] > a.reveal:
            why.append(f"reveal {r['reveal']:.1f}%")
    return "ok" if not why else ", ".join(why)


def fmt(v, nd=2):
    return "" if v is None else f"{v:.{nd}f}"


def sanitize(s):
    return "".join(c if c.isalnum() or c == "-" else "_" for c in s)


def run_info(run):
    """Scenario -> level, tour view -> stop name, from results.json (missing: file names only)."""
    levels, stops, tag = {}, {}, ""
    try:
        with open(os.path.join(run, "results.json"), encoding="utf-8-sig") as f:
            r = json.load(f)
        tag = r.get("Tag") or ""
        for s in r.get("Scenarios", []):
            levels[sanitize(s["Name"])] = (s.get("Scene") or "").replace("Level ", "")
            for st in s.get("Tour") or []:
                for v in st.get("Views") or []:
                    stops[v] = f"{st['Index']}: {st['Name'].split('/')[0] if st['Name'] != 'start' else 'start'}"
    except (OSError, ValueError):
        pass
    return levels, stops, tag


def score_run(run, a):
    levels, stops, tag = run_info(run)
    scen = {}
    for f in sorted(os.listdir(run)):
        if not f.endswith(".png") or f.endswith("_raw.png") or "_burst_" in f or "_motion_" in f or f.startswith("score"):
            continue
        m = re.match(r"(.+?_\d+(?:\.\d+)?s)_(.+)\.png$", f)
        if m:
            scen.setdefault(m.group(1), {})[m.group(2)] = os.path.join(run, f)
    rows, thumbs = [], []
    want = [sanitize(x) for x in a.modes.split(",")] if a.modes else None
    for pose, vs in scen.items():
        if "off" not in vs:
            continue
        modes = [v for v in vs if v != "off" and (want is None or v in want)]
        if want:
            modes.sort(key=want.index)
        scenario = re.sub(r"(-s\d+-v\d+)?_\d+(?:\.\d+)?s$", "", pose)
        row_thumbs = [("off", None, None)]
        for mode in modes:
            r, ga, ra = measure(vs["off"], vs[mode], a.dark, a.reveal_step)
            r.update(run=os.path.basename(os.path.normpath(run)), tag=tag, label=a.label or "", level=levels.get(scenario, ""),
                     pose=pose, stop=stops.get(pose, ""), mode=mode)
            r["verdict"] = verdict(r, a)
            rows.append(r)
            if row_thumbs[0][1] is None:
                row_thumbs[0] = ("off", ga, r)
            row_thumbs.append((mode, ra, r))
        if len(row_thumbs) > 1:
            thumbs.append((pose, stops.get(pose, ""), row_thumbs))
    return rows, thumbs


def write_md(path, rows, a):
    lines = [f"Luma 0-255 over the screen minus HUD and viewmodel. dark = pixels the game leaves at luma <= {a.dark:g}. "
             f"reveal = share of the frame that is dark in the game and at least {a.reveal_step:g} brighter in RT. "
             f"Limits: ratio {a.lo:g}-{a.hi:g}, dark delta {a.dark_delta:g}, reveal {a.reveal:g} %.", "",
             "| Pose | Mode | game | ratio | tint | dark % | dark game -> RT (delta) | reveal % | p10 / p50 / p90 | Verdict |",
             "|---|---|---|---|---|---|---|---|---|---|"]
    for r in rows:
        dark = "" if r["dark_delta"] is None else f"{r['dark_game']:.1f} -> {r['dark_rt']:.1f} ({r['dark_delta']:+.1f})"
        name = r["pose"] + (f" ({r['stop']})" if r["stop"] else "")
        lines.append(f"| {name} | {r['mode']} | {r['game']:.1f} | x{r['ratio']:.2f} | {r['tint']:.1f} | {r['dark_pct']:.0f} | {dark} | "
                     f"{'' if r['reveal'] is None else format(r['reveal'], '.1f')} | "
                     f"x{r['p10']:.2f} / x{r['p50']:.2f} / x{r['p90']:.2f} | {r['verdict']} |")
    lines += ["", "| Mode | poses | ok | bright | dim | dark | median ratio | median dark delta |", "|---|---|---|---|---|---|---|---|"]
    for mode in dict.fromkeys(r["mode"] for r in rows):
        rs = [r for r in rows if r["mode"] == mode]
        dd = [r["dark_delta"] for r in rs if r["dark_delta"] is not None]
        lines.append(f"| {mode} | {len(rs)} | {sum(r['verdict'] == 'ok' for r in rs)} | {sum('bright' in r['verdict'] for r in rs)} | "
                     f"{sum('dim' in r['verdict'] for r in rs)} | {sum('dark' in r['verdict'] or 'reveal' in r['verdict'] for r in rs)} | "
                     f"x{np.median([r['ratio'] for r in rs]):.2f} | {'' if not dd else format(np.median(dd), '+.1f')} |")
    with open(path, "w", newline="\n", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    return lines


def write_csv(path, rows):
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f, lineterminator="\n")
        w.writerow(FIELDS)
        for r in rows:
            w.writerow([r[k] if isinstance(r[k], str) else fmt(r[k], 3) for k in FIELDS])


def update_board(path, rows):
    old = []
    if os.path.exists(path):
        with open(path, newline="", encoding="utf-8") as f:
            old = list(csv.DictReader(f))
    keys = {(r["run"], r["pose"], r["mode"]) for r in rows}
    old = [o for o in old if (o.get("run"), o.get("pose"), o.get("mode")) not in keys]
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f, lineterminator="\n")
        w.writerow(FIELDS)
        for o in old:
            w.writerow([o.get(k, "") for k in FIELDS])
        for r in rows:
            w.writerow([r[k] if isinstance(r[k], str) else fmt(r[k], 3) for k in FIELDS])


def write_sheet(path, thumbs, title, tw):
    th = tw * 9 // 16
    lab = 62
    ncol = max(len(t[2]) for t in thumbs)
    sheet = Image.new("RGB", (tw * ncol, (th + lab) * len(thumbs) + 34), (20, 20, 20))
    d = ImageDraw.Draw(sheet)
    try:
        font = ImageFont.truetype("arial.ttf", 13)
        big = ImageFont.truetype("arialbd.ttf", 16)
    except OSError:
        font = big = ImageFont.load_default()
    d.text((8, 8), title, fill=(255, 255, 255), font=big)
    for i, (pose, stop, cells) in enumerate(thumbs):
        y = 34 + i * (th + lab)
        for c, (mode, img, r) in enumerate(cells):
            x = c * tw
            sheet.paste(Image.fromarray(img.astype(np.uint8)).resize((tw, th), Image.BILINEAR), (x, y + lab))
            if mode == "off":
                d.text((x + 5, y + 3), f"{pose}  {stop}"[: tw // 7], fill=(255, 255, 255), font=font)
                dark = "" if r["dark_delta"] is None else f"   dark {r['dark_pct']:.0f}% at {r['dark_game']:.1f}"
                d.text((x + 5, y + 22), f"game  luma {r['game']:.1f}{dark}", fill=(200, 230, 200), font=font)
            else:
                ok = r["verdict"] == "ok"
                d.text((x + 5, y + 3), mode[: tw // 7], fill=(255, 255, 255), font=font)
                dark = "" if r["dark_delta"] is None else f"  dark {r['dark_delta']:+.1f}" + ("" if r["reveal"] is None else f"  reveal {r['reveal']:.1f}%")
                d.text((x + 5, y + 22), f"x{r['ratio']:.2f}  tint {r['tint']:.1f}{dark}"[: tw // 7], fill=(200, 230, 200), font=font)
                d.text((x + 5, y + 41), ("OK" if ok else r["verdict"])[: tw // 7], fill=(150, 235, 150) if ok else (255, 120, 110), font=font)
    sheet.save(path)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("runs", nargs="+")
    ap.add_argument("--modes", help="comma-separated modes to score, in column order (default: every mode found)")
    ap.add_argument("--dark", type=float, default=8.0, help="game luma at or below which a pixel counts as dark")
    ap.add_argument("--lo", type=float, default=0.9)
    ap.add_argument("--hi", type=float, default=1.1)
    ap.add_argument("--dark-delta", type=float, default=2.0, dest="dark_delta")
    ap.add_argument("--reveal", type=float, default=0.5, help="limit on reveal%% (share of the frame)")
    ap.add_argument("--reveal-step", type=float, default=6.0, dest="reveal_step", help="luma levels brighter than the game's dark pixel that count as revealed")
    ap.add_argument("--sheet", action="store_true")
    ap.add_argument("--thumb", type=int, default=480, help="sheet thumbnail width")
    ap.add_argument("--board", help="CSV that accumulates rows over runs")
    ap.add_argument("--label", help="free text stored with the rows (what this run tests)")
    a = ap.parse_args()
    code = 0
    for run in a.runs:
        rows, thumbs = score_run(run, a)
        if not rows:
            print("no game / RT screenshot pairs in", run)
            code = 1
            continue
        write_csv(os.path.join(run, "score.csv"), rows)
        lines = write_md(os.path.join(run, "score.md"), rows, a)
        print(f"== {run}")
        print("\n".join(lines[2:]))
        if a.sheet:
            write_sheet(os.path.join(run, "score.png"), thumbs, os.path.basename(os.path.normpath(run)) + (f"  ({a.label})" if a.label else ""), a.thumb)
            print("sheet:", os.path.join(run, "score.png"))
        if a.board:
            update_board(a.board, rows)
    return code


if __name__ == "__main__":
    sys.exit(main())
