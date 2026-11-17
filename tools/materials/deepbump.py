"""Normal maps from DeepBump for the materials bake-off (docs/materials.md, "Bake-off"; plan B2).

    <venv>/python.exe -I tools/materials/deepbump.py --cache <work> --deepbump <DeepBump checkout> [--scale 4] [--only k1,k2]

DeepBump (Hugo Tini, GPL-3.0, https://github.com/HugoTini/DeepBump) is used as an offline tool: clone it next to the
work folder, give its path with --deepbump; nothing of it is in this repository and nothing it writes is shipped.
Its colour-to-normals network takes the texture's grey value on 256x256 tiles and returns an OpenGL normal map
(G = up), the convention of the maps here.

As for PBRify (generate.py): pixel art is upscaled nearest-neighbour by --scale first (each texel a block), and the
result is reduced to one normal per texel by a box filter and renormalised. A texture whose upscaled size fits 192 px
is tiled (wrapped) into one 256 tile, so the network sees the texture's own repeat at its edges; larger ones go
through DeepBump's own overlapping tiles. Roughness is not DeepBump's: it is copied from --rough-from (PBRify's).

Writes <work>/db/<key>_n.png, _r.png, _d.png and manifest.json, the layout generate.py writes. Needs numpy,
onnxruntime and Pillow. The maps derive from the game's textures: never commit.
"""
import argparse
import json
import os
import sys
import time

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from generate import box_down, encode_normal, fill_transparent, load_rgba, normalize, write_detail

TILE = 256


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", required=True)
    ap.add_argument("--deepbump", required=True)
    ap.add_argument("--scale", type=int, default=4)
    ap.add_argument("--max", type=int, default=1024, help="the upscale factor is reduced so the input stays under this")
    ap.add_argument("--rough-from", default="ai")
    ap.add_argument("--out", default="db")
    ap.add_argument("--only", default="")
    ap.add_argument("--threads", type=int, default=4)
    a = ap.parse_args()

    sys.path.insert(0, os.path.abspath(a.deepbump))
    import onnxruntime as ort
    import utils_inference as ui
    ort.disable_telemetry_events()
    so = ort.SessionOptions()
    so.intra_op_num_threads = a.threads
    sess = ort.InferenceSession(os.path.join(a.deepbump, "deepbump256.onnx"), so, providers=["CPUExecutionProvider"])

    def infer(grey):
        """grey: HxW float32 0-1 (already upscaled) -> HxWx3 unit normals, image rows."""
        h, w = grey.shape
        if max(h, w) <= 192:
            oy, ox = (TILE - h) // 2, (TILE - w) // 2
            canvas = np.pad(grey, ((oy, TILE - h - oy), (ox, TILE - w - ox)), mode="wrap")
            pred = sess.run(None, {"input": canvas[None, None].astype(np.float32)})[0][0]
            pred = pred[:, oy:oy + h, ox:ox + w]
        else:
            stride = TILE - TILE // 6
            stride -= stride % 2
            tiles, pads = ui.tiles_split(grey[None].astype(np.float32), (TILE, TILE), (stride, stride))
            pred = ui.tiles_merge(ui.tiles_infer(np.asarray(tiles), sess), (stride, stride), (3, h, w), pads)
        # The network's output is the map as an image: 0-1, 0.5 = no tilt, R = +u, G = +v (up).
        return normalize(pred.transpose(1, 2, 0) * 2.0 - 1.0)

    man_in = json.load(open(os.path.join(a.cache, "textures.json")))
    keys = sorted(k for k in man_in if not man_in[k].get("atlas"))
    if a.only:
        only = set(a.only.split(","))
        keys = [k for k in keys if k in only]
    out_dir = os.path.join(a.cache, a.out)
    os.makedirs(out_dir, exist_ok=True)
    man_path = os.path.join(out_dir, "manifest.json")
    man = json.load(open(man_path)) if os.path.exists(man_path) else {}
    rough_dir = os.path.join(a.cache, a.rough_from)
    t0 = time.time()
    for i, k in enumerate(keys):
        if k in man and os.path.exists(os.path.join(out_dir, k + "_d.png")):
            continue
        try:
            rgb = fill_transparent(load_rgba(os.path.join(a.cache, "src", k + ".png")))
        except Exception as e:
            print("  %s: %s" % (k, e), file=sys.stderr)
            continue
        h, w = rgb.shape[:2]
        r_path = os.path.join(rough_dir, k + "_r.png")
        if min(h, w) < 2 or not os.path.exists(r_path):
            continue
        scale = a.scale
        while scale > 1 and max(h, w) * scale > a.max:
            scale //= 2
        grey = rgb.mean(axis=-1)
        if scale > 1:
            grey = grey.repeat(scale, axis=0).repeat(scale, axis=1)
        n = normalize(box_down(infer(grey), scale))
        r = np.asarray(Image.open(r_path).convert("L")).astype(np.float32) / 255.0
        Image.fromarray(encode_normal(n), "RGB").save(os.path.join(out_dir, k + "_n.png"))
        Image.fromarray(np.clip(np.round(r * 255), 0, 255).astype(np.uint8), "L").save(os.path.join(out_dir, k + "_r.png"))
        write_detail(os.path.join(out_dir, k + "_d.png"), n, r)
        man[k] = {"name": man_in[k]["name"], "w": w, "h": h, "rmean": round(float(r.mean()), 4),
                  "nstd": round(float(np.sqrt((n[..., :2] ** 2).sum(-1).mean())), 4)}
        if (i + 1) % 100 == 0:
            json.dump(man, open(man_path, "w"), indent=1, sort_keys=True)
            print("%d/%d %.0fs" % (i + 1, len(keys), time.time() - t0), flush=True)
    json.dump(man, open(man_path, "w"), indent=1, sort_keys=True)
    print("%s: %d maps in %s, %.0fs" % (a.out, len(man), out_dir, time.time() - t0), flush=True)


if __name__ == "__main__":
    main()
