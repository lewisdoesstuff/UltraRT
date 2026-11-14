"""Contact sheet comparing generated maps (one row per texture).

Columns per method: the normal map, the albedo lit by a light from the upper left using that normal map (flat normal
= the plain texture at the same light), and the roughness map. Every texel is drawn as a --cell / size block (nearest).

Usage: python -I tools/materials/compare.py --cache <cache> --methods ai,proc --keys a@32x32,b@64x64 --out sheet.png
"""
import argparse
import json
import os

import numpy as np
from PIL import Image, ImageDraw

L = np.array([-0.5, 0.5, 0.7])
L = L / np.linalg.norm(L)


def nn(img, size):
    return img.resize((size, size), Image.NEAREST)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", required=True)
    ap.add_argument("--methods", default="ai,proc")
    ap.add_argument("--keys", required=True)
    ap.add_argument("--cell", type=int, default=160)
    ap.add_argument("--out", required=True)
    ap.add_argument("--no-rough", action="store_true")
    args = ap.parse_args()
    methods = args.methods.split(",")
    keys = args.keys.split(",")
    c = args.cell
    per = 2 if args.no_rough else 3
    cols = 2 + per * len(methods)
    sheet = Image.new("RGB", (cols * c, len(keys) * c + 18), (20, 20, 20))
    d = ImageDraw.Draw(sheet)
    head = ["albedo", "flat lit"] + [f"{m} {x}" for m in methods for x in (["normal", "lit"] + ([] if args.no_rough else ["rough"]))]
    for i, h in enumerate(head):
        d.text((i * c + 4, 3), h, fill=(230, 230, 230))
    for row, k in enumerate(keys):
        y = 18 + row * c
        src = Image.open(os.path.join(args.cache, "src", k + ".png")).convert("RGB")
        a = np.asarray(src).astype(np.float32) / 255.0
        sheet.paste(nn(src, c), (0, y))
        flat = np.clip(a * (0.2 + 1.6 * L[2]), 0, 1)
        sheet.paste(nn(Image.fromarray((flat * 255).astype(np.uint8)), c), (c, y))
        for j, m in enumerate(methods):
            x = (2 + j * per) * c
            npath = os.path.join(args.cache, m, k + "_n.png")
            if not os.path.exists(npath):
                continue
            nimg = Image.open(npath).convert("RGB")
            n = np.asarray(nimg).astype(np.float32) / 255.0 * 2 - 1
            lit = np.clip(a * (0.2 + 1.6 * np.clip((n * L).sum(-1, keepdims=True), 0, 1)), 0, 1)
            sheet.paste(nn(nimg, c), (x, y))
            sheet.paste(nn(Image.fromarray((lit * 255).astype(np.uint8)), c), (x + c, y))
            if not args.no_rough:
                sheet.paste(nn(Image.open(os.path.join(args.cache, m, k + "_r.png")).convert("RGB"), c), (x + 2 * c, y))
        d.text((4, y + 4), k, fill=(255, 255, 0))
    sheet.save(args.out)
    print(args.out)


if __name__ == "__main__":
    main()
