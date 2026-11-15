"""Review sheets for light overrides (docs/lighting-overrides.md).

For every scenario of a bench results folder: the game's frame (mode off) next to each RT variant, with the mean luma and
mean colour over the screen minus the HUD and the viewmodel, and the ratio to the game. Writes <out>.png (one row per
scenario) and <out>.md (the numbers).

    py -3 tools/lights/sheet.py bench/results/<run> [--out bench/results/<run>/sheet] [--cols off,relight,...]
"""
import argparse
import os
import re
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

LUMA = np.array([0.2126, 0.7152, 0.0722])


def mask_for(h, w):
    m = np.ones((h, w), bool)
    # Viewmodel (lower right) and HUD (lower left), as the burst statistics leave them out.
    m[int(h * 0.45):, int(w * 0.45):] = False
    m[int(h * 0.70):, : int(w * 0.30)] = False
    return m


def stats(path):
    a = np.asarray(Image.open(path).convert("RGB"), dtype=np.float32)
    m = mask_for(*a.shape[:2])
    px = a[m]
    mean = px.mean(axis=0)
    return {"rgb": mean, "luma": float(mean @ LUMA), "img": a}


def variant_of(name, scenario_prefix):
    v = name[len(scenario_prefix):]
    return v[:-4]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("run")
    ap.add_argument("--out")
    ap.add_argument("--cols", help="comma-separated variant order (default: off first, then as found)")
    ap.add_argument("--labels", help="comma-separated column labels")
    ap.add_argument("--title", default="")
    args = ap.parse_args()
    run = args.run
    out = args.out or os.path.join(run, "sheet")
    files = sorted(f for f in os.listdir(run) if f.endswith(".png") and not f.endswith("_raw.png") and "_burst_" not in f)
    scen = {}
    for f in files:
        m = re.match(r"(.+?_\d+(?:\.\d+)?s)_(.+)\.png$", f)
        if not m:
            continue
        scen.setdefault(m.group(1), {})[m.group(2)] = os.path.join(run, f)
    if not scen:
        print("no screenshots in", run)
        return 1
    cols = args.cols.split(",") if args.cols else None
    rows = []
    for s, vs in scen.items():
        order = cols or (["off"] + sorted(v for v in vs if v != "off"))
        rows.append((s, [(v, vs.get(v)) for v in order]))
    ncol = max(len(r[1]) for r in rows)
    tw, th = 640, 360
    lab_h = 54
    sheet = Image.new("RGB", (tw * ncol, (th + lab_h) * len(rows) + 40), (20, 20, 20))
    d = ImageDraw.Draw(sheet)
    try:
        font = ImageFont.truetype("arial.ttf", 15)
        big = ImageFont.truetype("arialbd.ttf", 18)
    except OSError:
        font = big = ImageFont.load_default()
    d.text((10, 10), args.title or os.path.basename(os.path.normpath(run)), fill=(255, 255, 255), font=big)
    labels = args.labels.split(",") if args.labels else None
    md = ["| Scenario | " + " | ".join(labels or [c for c, _ in rows[0][1]]) + " |", "|---" * (1 + ncol) + "|"]
    for r, (s, vs) in enumerate(rows):
        y = 40 + r * (th + lab_h)
        game = None
        cells = []
        for c, (v, path) in enumerate(vs):
            x = c * tw
            if not path:
                cells.append("-")
                continue
            st = stats(path)
            if v == "off":
                game = st
            im = Image.fromarray(st["img"].astype(np.uint8)).resize((tw, th), Image.BILINEAR)
            sheet.paste(im, (x, y + lab_h))
            rgb = st["rgb"]
            txt = f"{(labels[c] if labels and c < len(labels) else v)}"
            num = f"luma {st['luma']:.1f}  rgb {rgb[0]:.0f},{rgb[1]:.0f},{rgb[2]:.0f}"
            if game is not None and v != "off":
                ratio = st["luma"] / max(game["luma"], 1e-3)
                gn = game["rgb"] / max(game["rgb"].sum(), 1e-3)
                sn = rgb / max(rgb.sum(), 1e-3)
                tint = np.abs(sn - gn).sum() * 100
                num += f"  x{ratio:.2f} game, tint err {tint:.1f}"
                cells.append(f"{st['luma']:.1f} (x{ratio:.2f}; {rgb[0]:.0f},{rgb[1]:.0f},{rgb[2]:.0f}; tint {tint:.1f})")
            else:
                cells.append(f"{st['luma']:.1f} ({rgb[0]:.0f},{rgb[1]:.0f},{rgb[2]:.0f})")
            d.text((x + 6, y + 4), (s if c == 0 else "") + ("  |  " if c == 0 else "") + txt, fill=(255, 255, 255), font=font)
            d.text((x + 6, y + 26), num, fill=(200, 230, 200), font=font)
        md.append(f"| {s} | " + " | ".join(cells) + " |")
    sheet.save(out + ".png")
    with open(out + ".md", "w", newline="\r\n") as f:
        f.write("Mean over the screen minus HUD and viewmodel (0-255); x = luma / game; tint = sum |rgb/sum - game rgb/sum| x 100.\n\n")
        f.write("\n".join(md) + "\n")
    print("\n".join(md))
    print("sheet:", out + ".png")
    return 0


if __name__ == "__main__":
    sys.exit(main())
