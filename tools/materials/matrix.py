"""Crop sheet of the in-engine detail-map matrix (docs/materials.md, "Matrix").

    py -3 tools/materials/matrix.py --pose 1-1_indoor_a_2.5s --crop 150,200,480,360 --out sheet.png \
        --base bench/results/<mat-matrix run> --set lp=bench/results/<run with ai-lp> --set hgt=... --set blk=...

One native-resolution crop of the same pose per cell, labelled. Row 1: the game, maps off, roughness only, the base
set. Row 2: each --set under the same mode. Rows 3 and 4 (with --all): every set at RR 0.67 scale and under UltraRT's
denoiser. Screenshots are the game's frames: the sheet stays out of the repo.
"""
import argparse
import os

from PIL import Image, ImageDraw


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pose", required=True)
    ap.add_argument("--crop", required=True, help="x,y,w,h in screenshot pixels")
    ap.add_argument("--base", required=True)
    ap.add_argument("--set", action="append", default=[], help="name=run folder")
    ap.add_argument("--base-name", default="pbrify")
    ap.add_argument("--all", action="store_true")
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    x, y, w, h = (int(v) for v in a.crop.split(","))
    sets = [(a.base_name, a.base)] + [tuple(s.split("=", 1)) for s in a.set]

    def cell(run, mode, label):
        p = os.path.join(run, "%s_%s.png" % (a.pose, mode))
        im = Image.open(p).convert("RGB").crop((x, y, x + w, y + h)) if os.path.exists(p) else Image.new("RGB", (w, h))
        d = ImageDraw.Draw(im)
        d.rectangle((0, 0, 8 + 7 * len(label), 16), fill=(0, 0, 0))
        d.text((4, 2), label, fill=(255, 255, 255))
        return im

    rows = [[cell(a.base, "off", "game"), cell(a.base, "pathtrace_mat_enabled_0", "maps off"),
             cell(a.base, "pathtrace_mat_normalStrength_0", "roughness only"), cell(a.base, "pathtrace", a.base_name + ", RR 1/3")],
            [cell(r, "pathtrace", n + ", RR 1/3") for n, r in sets[1:]] + [cell(a.base, "pathtrace_ptx_scale_0_67", a.base_name + ", RR 0.67")]]
    if a.all:
        rows.append([cell(r, "pathtrace_ptx_scale_0_67", n + ", RR 0.67") for n, r in sets])
        rows.append([cell(r, "pathtrace_ptx_denoiser_UltraRT", n + ", UltraRT") for n, r in sets])
    cols = max(len(r) for r in rows)
    out = Image.new("RGB", (cols * w, len(rows) * h))
    for j, r in enumerate(rows):
        for i, im in enumerate(r):
            out.paste(im, (i * w, j * h))
    out.save(a.out)
    print(a.out)


if __name__ == "__main__":
    main()
