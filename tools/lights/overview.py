"""One overview sheet over several bench results folders (docs/archive/audit-2026-11-15.md).

Every scenario becomes one cell: game | relight | pathtrace thumbnails with the masked luma ratio to the game and the
tint error (same measures as sheet.py). Cells are laid out two per row. Also prints a markdown table of the numbers.

    py -3 tools/lights/overview.py --out <png> run1 run2 ... [--cols off,relight,pathtrace] [--names scenario names file]
"""
import argparse
import os
import re
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, os.path.dirname(__file__))
from sheet import stats  # noqa: E402


def collect(run):
    scen = {}
    for f in sorted(os.listdir(run)):
        m = re.match(r"(.+?)_(\d+(?:\.\d+)?)s_(.+)\.png$", f)
        if not m or f.endswith("_raw.png") or "_burst_" in f:
            continue
        scen.setdefault(m.group(1), {})[m.group(3)] = os.path.join(run, f)
    return scen


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("runs", nargs="+")
    ap.add_argument("--out", required=True)
    ap.add_argument("--cols", default="off,relight,pathtrace")
    ap.add_argument("--per-row", type=int, default=2)
    args = ap.parse_args()
    cols = args.cols.split(",")
    cells = []
    for run in args.runs:
        for s, vs in collect(run).items():
            cells.append((s.replace("_", " "), vs))
    tw, th, lab = 320, 180, 36
    cw = tw * len(cols) + 8
    rows = (len(cells) + args.per_row - 1) // args.per_row
    sheet = Image.new("RGB", (cw * args.per_row, (th + lab) * rows), (20, 20, 20))
    d = ImageDraw.Draw(sheet)
    try:
        font = ImageFont.truetype("arial.ttf", 13)
    except OSError:
        font = ImageFont.load_default()
    md = []
    for i, (name, vs) in enumerate(cells):
        x0, y0 = (i % args.per_row) * cw, (i // args.per_row) * (th + lab)
        game = stats(vs["off"]) if "off" in vs else None
        txt = []
        for c, v in enumerate(cols):
            if v not in vs:
                continue
            st = stats(vs[v])
            im = Image.fromarray(st["img"].astype(np.uint8)).resize((tw, th), Image.BILINEAR)
            sheet.paste(im, (x0 + c * tw, y0 + lab))
            if v != "off" and game is not None:
                r = st["luma"] / max(game["luma"], 1e-3)
                gn = game["rgb"] / max(game["rgb"].sum(), 1e-3)
                sn = st["rgb"] / max(st["rgb"].sum(), 1e-3)
                tint = np.abs(sn - gn).sum() * 100
                txt.append((v, r, tint))
                col = (120, 230, 120) if abs(r - 1) < 0.15 else ((240, 210, 90) if abs(r - 1) < 0.35 else (255, 110, 100))
                d.text((x0 + c * tw + 4, y0 + 20), f"{v} x{r:.2f} tint {tint:.1f}", fill=col, font=font)
            elif v == "off":
                d.text((x0 + c * tw + 4, y0 + 20), f"game luma {st['luma']:.1f}", fill=(200, 200, 200), font=font)
        d.text((x0 + 4, y0 + 3), name, fill=(255, 255, 255), font=font)
        md.append(f"| {name} | " + " | ".join(f"x{r:.2f} / {t:.1f}" for _, r, t in txt) + " |")
    sheet.save(args.out)
    print("| Pose | " + " | ".join(c for c in cols if c != "off") + " |\n|---|---|---|")
    print("\n".join(md))
    print("overview:", args.out, sheet.size)


if __name__ == "__main__":
    sys.exit(main())
