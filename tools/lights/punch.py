"""Punch: highlights, colour and contrast against the game, next to the dark-pixel test (docs/lighting-overrides.md,
"Punch").

    py -3 tools/lights/punch.py <run> [--modes a,b,c] [--labels "A,B,C"] [--sheet out.png] [--only s01-v0,s04-v0] [--per-view]

The scoreboard (score.py) asks whether a frame's mean matches the game's; that target made the image dull (the user,
2026-11-17: "a little too dark ... the colours had a lot more pop ... we should still have bright highlights"). This
tool scores what was lost, per view and mode, over the screen minus HUD and viewmodel (display values, luma 0-255):

  hi%        share of the frame at luma >= --hi (default 150): the highlights. Printed next to the game's; more than
             the game is allowed, less is the fault.
  p95        the 95th percentile of luma (how bright the bright end is), as a ratio to the game's.
  sat        mean saturation ((max - min) / max per pixel) over pixels of luma >= 24, as a ratio to the game's.
  chroma     mean (max - min) channel spread over the frame, as a ratio to the game's: colour that is both
             saturated and bright ("pop").
  contrast   (p95 + 4) / (p10 + 4) of luma, as a ratio to the game's.
  dark       what the dark-pixel test of score.py measures: the mean luma of the pixels the game leaves at <= --dark,
             RT minus game. This is the part that must not move.

The summary is the median over the views; --per-view prints every view. --sheet writes the views side by side, one
column per mode (the game first), each with its numbers. Screenshots are the game's frames: sheets stay out of the
repository.
"""
import argparse
import os
import re
import statistics

import numpy as np
from PIL import Image, ImageDraw

from score import LUMA, mask_for, sanitize


def stats(a, m, dark=None):
    l = a @ LUMA
    v = a[m]
    lv = l[m]
    mx, mn = v.max(axis=1), v.min(axis=1)
    lit = lv >= 24
    r = {"hi": 100.0 * float((lv >= ARGS.hi).mean()), "p95": float(np.percentile(lv, 95)), "p10": float(np.percentile(lv, 10)),
         "sat": float(((mx - mn) / np.maximum(mx, 1.0))[lit].mean()) if lit.any() else 0.0, "chroma": float((mx - mn).mean()),
         "mean": float(lv.mean())}
    r["contrast"] = (r["p95"] + 4.0) / (r["p10"] + 4.0)
    if dark is not None and dark.sum() > 0.02 * m.sum():
        r["dark"] = float(l[dark].mean())
    return r, l


def main():
    global ARGS
    ap = argparse.ArgumentParser()
    ap.add_argument("run")
    ap.add_argument("--modes", default="")
    ap.add_argument("--labels", default="")
    ap.add_argument("--hi", type=float, default=150.0)
    ap.add_argument("--dark", type=float, default=8.0)
    ap.add_argument("--only", default="", help="comma-separated parts of view names")
    ap.add_argument("--per-view", action="store_true")
    ap.add_argument("--sheet", default="")
    ap.add_argument("--thumb", type=int, default=560)
    ARGS = a = ap.parse_args()

    scen = {}
    for f in sorted(os.listdir(a.run)):
        if not f.endswith(".png") or f.endswith("_raw.png") or f.startswith("score"):
            continue
        mm = re.match(r"(.+?_\d+(?:\.\d+)?s)_(.+)\.png$", f)
        if mm:
            scen.setdefault(mm.group(1), {})[mm.group(2)] = os.path.join(a.run, f)
    only = [x for x in a.only.split(",") if x]
    views = [p for p in scen if "off" in scen[p] and (not only or any(o in p for o in only))]
    modes = [sanitize(x) for x in a.modes.split(",")] if a.modes else sorted({m for p in views for m in scen[p] if m != "off"})
    labels = dict(zip(modes, a.labels.split(","))) if a.labels else {}
    keys = ("hi", "p95", "sat", "chroma", "contrast")
    agg = {m: {k: [] for k in keys + ("dark", "hi_abs")} for m in modes}
    game_hi = []
    sheet_rows = []
    for p in views:
        ga = np.asarray(Image.open(scen[p]["off"]).convert("RGB"), dtype=np.float32)
        h, w = ga.shape[:2]
        m = mask_for(h, w)
        dark = ((ga @ LUMA) <= a.dark) & m
        g, _ = stats(ga, m, dark)
        game_hi.append(g["hi"])
        cells = [("game", scen[p]["off"], "hi %.1f%%  p95 %.0f  sat %.2f" % (g["hi"], g["p95"], g["sat"]))]
        if a.per_view:
            print(p)
        for mode in modes:
            if mode not in scen[p]:
                continue
            im = Image.open(scen[p][mode]).convert("RGB")
            if im.size != (w, h):
                im = im.resize((w, h), Image.BILINEAR)
            r, _ = stats(np.asarray(im, dtype=np.float32), m, dark)
            row = {"hi": r["hi"] - g["hi"], "p95": (r["p95"] + 1) / (g["p95"] + 1), "sat": r["sat"] / max(g["sat"], 1e-3),
                   "chroma": r["chroma"] / max(g["chroma"], 1e-3), "contrast": r["contrast"] / g["contrast"]}
            for k in keys:
                agg[mode][k].append(row[k])
            agg[mode]["hi_abs"].append(r["hi"])
            d = None
            if "dark" in r and "dark" in g:
                d = r["dark"] - g["dark"]
                agg[mode]["dark"].append(d)
            text = "hi %.1f%%  p95 x%.2f  sat x%.2f  chroma x%.2f%s" % (r["hi"], row["p95"], row["sat"], row["chroma"], "" if d is None else "  dark %+.1f" % d)
            cells.append((labels.get(mode, mode), scen[p][mode], text))
            if a.per_view:
                print("  %-44s %s  contrast x%.2f" % (labels.get(mode, mode)[:44], text, row["contrast"]))
        sheet_rows.append((p, cells))

    med = lambda v: statistics.median(v) if v else float("nan")
    print("%d views; the game's highlights: median %.1f%% of the frame at luma >= %g" % (len(views), med(game_hi), a.hi))
    print("| Mode | hi%% (game %.1f) | p95 | sat | chroma | contrast | dark delta |" % med(game_hi))
    print("|---|---|---|---|---|---|---|")
    for mode in modes:
        s = agg[mode]
        print("| %s | %.1f | x%.2f | x%.2f | x%.2f | x%.2f | %+.1f |" % (labels.get(mode, mode), med(s["hi_abs"]), med(s["p95"]), med(s["sat"]), med(s["chroma"]), med(s["contrast"]), med(s["dark"])))

    if a.sheet and sheet_rows:
        tw = a.thumb
        cols = max(len(c) for _, c in sheet_rows)
        th = None
        tiles = []
        for p, cells in sheet_rows:
            row = []
            for label, path, text in cells:
                im = Image.open(path).convert("RGB")
                th = th or im.height * tw // im.width
                im = im.resize((tw, th), Image.LANCZOS)
                d = ImageDraw.Draw(im)
                d.rectangle((0, 0, tw, 30), fill=(0, 0, 0))
                d.text((4, 2), label, fill=(255, 255, 255))
                d.text((4, 16), text, fill=(200, 230, 200))
                row.append(im)
            tiles.append(row)
        out = Image.new("RGB", (cols * tw, len(tiles) * th))
        for j, row in enumerate(tiles):
            for i, im in enumerate(row):
                out.paste(im, (i * tw, j * th))
        out.save(a.sheet)
        print(a.sheet)


if __name__ == "__main__":
    main()
