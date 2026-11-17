"""Variants of a generated map set, for the in-engine matrix (docs/materials.md, "Matrix"; plan B1).

    py -3 tools/materials/variants.py --cache <work> [--from ai] [--only k1,k2]

Reads <work>/<from>/<key>_n.png and _r.png (generate.py's output) and writes, each with its own manifest.json,
_d.png (what the plugin loads) and _n.png (for looking at):

  <from>-lp   the normals' slopes low-passed over the 3x3 texel neighbourhood (wrapped). Tests "each texel tilts on
              its own": the relief stays, the texel-to-texel disagreement goes.
  <from>-hgt  height first: the slope field is projected onto the nearest one that is the gradient of a height
              (Frankot-Chellappa; exact on a tiling texture, one FFT), and the normals are that height's central
              differences on the texel grid. Every normal then agrees with its neighbours about one surface.
  <from>-blk  the same height quantised to a few levels before the differences: flat faces with steps between
              them, the "every texel a small block" reading of pixel art.

The roughness is copied unchanged. Install a set as BepInEx/ultrart/materials/<name>/ and pick it with
-ultrart-materials-source <name>. Needs numpy and Pillow only. The maps derive from the game's textures: never commit.
"""
import argparse
import json
import os

import numpy as np
from PIL import Image


def load(path, mode):
    return np.asarray(Image.open(path).convert(mode)).astype(np.float32) / 255.0


def slopes_of(n_rgb):
    """RGB normal map (OpenGL: G = +v, up in the image) -> height slopes (dh/du, dh/dv), rows flipped so row = +v."""
    n = n_rgb[::-1] * 2.0 - 1.0
    z = np.maximum(n[..., 2], 0.05)
    return -n[..., 0] / z, -n[..., 1] / z


def normals_of(p, q):
    n = np.stack([-p, -q, np.ones_like(p)], axis=-1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return n[::-1]  # back to image rows


def lowpass(a):
    k = (0.25, 0.5, 0.25)
    b = sum(w * np.roll(a, s, axis=1) for w, s in zip(k, (-1, 0, 1)))
    return sum(w * np.roll(b, s, axis=0) for w, s in zip(k, (-1, 0, 1)))


def height_of(p, q):
    """Least-squares height of a periodic slope field (Frankot-Chellappa)."""
    h, w = p.shape
    kx = 2 * np.pi * np.fft.fftfreq(w)[None, :]
    ky = 2 * np.pi * np.fft.fftfreq(h)[:, None]
    d = kx * kx + ky * ky
    d[0, 0] = 1.0
    H = (-1j * kx * np.fft.fft2(p) - 1j * ky * np.fft.fft2(q)) / d
    H[0, 0] = 0.0
    return np.real(np.fft.ifft2(H)).astype(np.float32)


def central(h):
    return (np.roll(h, -1, axis=1) - np.roll(h, 1, axis=1)) * 0.5, (np.roll(h, -1, axis=0) - np.roll(h, 1, axis=0)) * 0.5


def quantise(h, levels):
    lo, hi = np.percentile(h, 2), np.percentile(h, 98)
    if hi - lo < 1e-4:
        return h
    step = (hi - lo) / levels
    return np.round((h - lo) / step) * step + lo


def write_detail(path, n, r):
    """As generate.py's write_detail: rg = normal xy, b = 128 + (roughness - mean) / 2, a = 255."""
    nb = np.clip(np.round((n * 0.5 + 0.5) * 255.0), 0, 255).astype(np.uint8)
    rb = np.clip(np.round(r * 255), 0, 255).astype(np.int32)
    mean = int(round(float(r.mean()) * 255))
    out = np.empty(nb.shape[:2] + (4,), np.uint8)
    out[..., 0:2] = nb[..., 0:2]
    out[..., 2] = np.clip(128 + (rb - mean) // 2, 0, 255)
    out[..., 3] = 255
    Image.fromarray(out, "RGBA").save(path)
    Image.fromarray(nb, "RGB").save(path.replace("_d.png", "_n.png"))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", required=True)
    ap.add_argument("--from", dest="src", default="ai")
    ap.add_argument("--only", default="")
    ap.add_argument("--levels", type=int, default=4)
    a = ap.parse_args()
    src = os.path.join(a.cache, a.src)
    man = json.load(open(os.path.join(src, "manifest.json"), encoding="utf-8"))
    only = set(k for k in a.only.split(",") if k)
    outs = {s: os.path.join(a.cache, a.src + "-" + s) for s in ("lp", "hgt", "blk")}
    mans = {s: {} for s in outs}
    for d in outs.values():
        os.makedirs(d, exist_ok=True)
    done = 0
    for k, info in man.items():
        if only and k not in only:
            continue
        n_path = os.path.join(src, k + "_n.png")
        if not os.path.exists(n_path):
            continue
        p, q = slopes_of(load(n_path, "RGB"))
        r = load(os.path.join(src, k + "_r.png"), "L")
        h = height_of(p, q)
        sets = {"lp": (lowpass(p), lowpass(q)), "hgt": central(h), "blk": central(quantise(h, a.levels))}
        for s, (pp, qq) in sets.items():
            n = normals_of(pp, qq)
            write_detail(os.path.join(outs[s], k + "_d.png"), n, r)
            e = dict(info)
            e["nstd"] = round(float(np.sqrt(np.mean(n[..., 0] ** 2 + n[..., 1] ** 2))), 4)
            mans[s][k] = e
        done += 1
    for s, d in outs.items():
        json.dump(mans[s], open(os.path.join(d, "manifest.json"), "w", encoding="utf-8"), indent=0)
        print("%s: %d textures -> %s" % (s, len(mans[s]), d))


if __name__ == "__main__":
    main()
