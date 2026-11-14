"""Generate per-texel normal and roughness maps for the textures extract.py wrote, at the textures' native resolution.

Methods (each writes its own folder under --cache, with the same layout):
  ai    PBRify Remix (Kim2091, CC0): 1x-PBRify_NormalV3 + 1x-PBRify_RoughnessV2, SPAN networks loaded with spandrel.
        The networks were trained on photographic ambientCG/PolyHaven tiles, so pixel art is first upscaled
        nearest-neighbour by --ai-scale (each texel becomes an f x f block), run through the network, then reduced back
        to one value per texel by a box (area) filter; normals are renormalised after averaging. The texture is padded
        by wrapping (tiling textures) so the network sees the opposite edge, then cropped.
  proc  Procedural, no AI: height = sRGB luminance (dark = low), per-texel central differences on the wrapped texture
        (a Sobel kernel) give the normal; roughness from darkness and local contrast.

Output per method: <cache>/<method>/<key>_d.png (what the runtime loads, see write_detail), <key>_n.png (RGB tangent-space normal, OpenGL convention: G = +v = up in the
image; PBRify's DirectX maps are converted), <key>_r.png (grayscale roughness, 0 = smooth), and manifest.json:
  { "<key>": { "name", "w", "h", "rmean": mean roughness, "nstd": rms normal tilt (xy length) } }
The runtime (src/UltraRT/MaterialMaps.cs) loads <key> = "<name>@<w>x<h>" (see extract.py).

Usage: tools/materials/.venv/Scripts/python.exe -I tools/materials/generate.py --cache <cache> [--methods ai,proc]
"""
import argparse
import json
import os
import sys
import time

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))


def srgb_luma(rgb):
    return rgb[..., 0] * 0.2126 + rgb[..., 1] * 0.7152 + rgb[..., 2] * 0.0722


def load_rgba(path):
    im = Image.open(path).convert("RGBA")
    return np.asarray(im).astype(np.float32) / 255.0


def fill_transparent(rgba):
    """Cutout textures: transparent texels carry junk colour; replace it with the mean opaque colour."""
    a = rgba[..., 3]
    rgb = rgba[..., :3].copy()
    mask = a < 0.5
    if mask.any() and (~mask).any():
        rgb[mask] = rgb[~mask].mean(axis=0)
    return rgb


def encode_normal(n):
    """n: HxWx3 unit vectors (x = +u, y = +v up, z out) -> uint8 RGB."""
    return np.clip(np.round((n * 0.5 + 0.5) * 255.0), 0, 255).astype(np.uint8)


def normalize(v):
    return v / np.maximum(np.linalg.norm(v, axis=-1, keepdims=True), 1e-6)


def write_detail(path, n, r):
    """The runtime's detail texture as an RGBA PNG: rg = normal xy as in _n.png, b = 128 + (roughness - mean
    roughness) / 2 (in 0-255 units), a = 255. MaterialMaps.cs loads only this file (one LoadImage per texture)."""
    nb = encode_normal(n)
    rb = np.clip(np.round(r * 255), 0, 255).astype(np.int32)
    mean = int(round(float(r.mean()) * 255))
    out = np.empty(nb.shape[:2] + (4,), np.uint8)
    out[..., 0:2] = nb[..., 0:2]
    out[..., 2] = np.clip(128 + (rb - mean) // 2, 0, 255)
    out[..., 3] = 255
    Image.fromarray(out, "RGBA").save(path)


def box_down(img, f):
    if f == 1:
        return img
    h, w = img.shape[0] // f, img.shape[1] // f
    return img[: h * f, : w * f].reshape(h, f, w, f, -1).mean(axis=(1, 3))


# ---------------------------------------------------------------- procedural

def procedural(rgb, strength=2.0):
    h = srgb_luma(rgb)
    # Wrapped central differences with Sobel smoothing (rows: image row 0 = top, so +v = up = row - 1).
    def shift(a, dy, dx):
        return np.roll(np.roll(a, dy, axis=0), dx, axis=1)
    # d/du: right minus left;  d/dv: up minus down (up = previous row).
    gx = (shift(h, 1, -1) + 2 * shift(h, 0, -1) + shift(h, -1, -1)) - (shift(h, 1, 1) + 2 * shift(h, 0, 1) + shift(h, -1, 1))
    gy = (shift(h, 1, 1) + 2 * shift(h, 1, 0) + shift(h, 1, -1)) - (shift(h, -1, 1) + 2 * shift(h, -1, 0) + shift(h, -1, -1))
    gx /= 8.0
    gy /= 8.0
    n = normalize(np.stack([-gx * strength, -gy * strength, np.ones_like(h)], axis=-1))
    # Roughness: darker texels (cracks, grime) rougher, local contrast rougher; bright flat texels smoother.
    mean3 = sum(shift(h, dy, dx) for dy in (-1, 0, 1) for dx in (-1, 0, 1)) / 9.0
    var3 = sum((shift(h, dy, dx) - mean3) ** 2 for dy in (-1, 0, 1) for dx in (-1, 0, 1)) / 9.0
    r = 0.55 + 0.6 * (h.mean() - h) + 1.5 * np.sqrt(var3)
    return n, np.clip(r, 0.0, 1.0)


# ---------------------------------------------------------------- PBRify (spandrel)

class Pbrify:
    def __init__(self, models_dir, device):
        import torch
        from spandrel import ModelLoader
        self.torch = torch
        self.device = device
        loader = ModelLoader(device=device)
        self.normal = loader.load_from_file(os.path.join(models_dir, "1x-PBRify_NormalV3.pth")).eval()
        self.rough = loader.load_from_file(os.path.join(models_dir, "1x-PBRify_RoughnessV2.pth")).eval()
        for m in (self.normal, self.rough):
            m.model.half() if device == "cuda" else None
        print(f"PBRify: normal {self.normal.architecture.name} in {self.normal.input_channels} out {self.normal.output_channels}, "
              f"roughness {self.rough.architecture.name}, size req {self.normal.size_requirements}", flush=True)

    def run(self, model, rgb):
        torch = self.torch
        t = torch.from_numpy(np.ascontiguousarray(rgb.transpose(2, 0, 1)))[None].to(self.device)
        if self.device == "cuda":
            t = t.half()
        with torch.inference_mode():
            out = model(t)
        return out[0].float().clamp(0, 1).cpu().numpy().transpose(1, 2, 0)

    def __call__(self, rgb, scale, pad_texels):
        h, w = rgb.shape[:2]
        p = min(pad_texels, h, w)
        big = np.pad(rgb, ((p, p), (p, p), (0, 0)), mode="wrap")
        if scale > 1:
            big = big.repeat(scale, axis=0).repeat(scale, axis=1)
        # Large inputs: the SPAN nets are fully convolutional and these textures are small; 1024 px at 4x fits easily.
        nrm = self.run(self.normal, big)
        rgh = self.run(self.rough, big)
        crop = slice(p * scale, (p + h) * scale), slice(p * scale, (p + w) * scale)
        nrm, rgh = nrm[crop], rgh[crop]
        v = nrm * 2.0 - 1.0
        v[..., 1] = -v[..., 1]  # DirectX (G = down) -> OpenGL (G = up)
        v = normalize(box_down(normalize(v), scale))
        r = box_down(rgh.mean(axis=-1, keepdims=True), scale)[..., 0]
        return v, r


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", required=True)
    ap.add_argument("--methods", default="ai,proc")
    ap.add_argument("--ai-scale", type=int, default=1, help="nearest-neighbour pre-upscale factor for the AI models")
    ap.add_argument("--ai-pad", type=int, default=8, help="wrap padding in texels")
    ap.add_argument("--ai-max", type=int, default=2048, help="upscale factor is reduced so the input stays under this")
    ap.add_argument("--proc-strength", type=float, default=2.0)
    ap.add_argument("--models", default=os.path.join(HERE, "models"))
    ap.add_argument("--only", default=None, help="comma-separated keys (default: every texture in textures.json)")
    ap.add_argument("--skip-atlas", action="store_true", default=True)
    ap.add_argument("--suffix", default="", help="output folder suffix, e.g. -s4 for a scale experiment")
    ap.add_argument("--pack-only", action="store_true", help="only (re)write the _d.png files from existing _n/_r PNGs")
    args = ap.parse_args()

    if args.pack_only:
        for method in [m.strip() for m in args.methods.split(",") if m.strip()]:
            out_dir = os.path.join(args.cache, method + args.suffix)
            man = json.load(open(os.path.join(out_dir, "manifest.json")))
            for k in man:
                n = np.asarray(Image.open(os.path.join(out_dir, k + "_n.png")).convert("RGB")).astype(np.float32) / 127.5 - 1.0
                r = np.asarray(Image.open(os.path.join(out_dir, k + "_r.png")).convert("L")).astype(np.float32) / 255.0
                write_detail(os.path.join(out_dir, k + "_d.png"), n, r)
            print(f"{method}: packed {len(man)} detail files", flush=True)
        return

    manifest_in = json.load(open(os.path.join(args.cache, "textures.json")))
    keys = sorted(manifest_in)
    if args.only:
        keys = [k for k in args.only.split(",") if k in manifest_in]
    if args.skip_atlas:
        keys = [k for k in keys if not manifest_in[k].get("atlas")]

    methods = [m.strip() for m in args.methods.split(",") if m.strip()]
    pbr = None
    if "ai" in methods:
        import torch
        device = "cuda" if torch.cuda.is_available() else "cpu"
        pbr = Pbrify(args.models, device)

    for method in methods:
        out_dir = os.path.join(args.cache, method + args.suffix)
        os.makedirs(out_dir, exist_ok=True)
        man_path = os.path.join(out_dir, "manifest.json")
        man = json.load(open(man_path)) if os.path.exists(man_path) else {}
        t0 = time.time()
        for i, k in enumerate(keys):
            info = manifest_in[k]
            try:
                rgb = fill_transparent(load_rgba(os.path.join(args.cache, "src", k + ".png")))
            except Exception as e:
                print(f"  {k}: {e}", file=sys.stderr)
                continue
            h, w = rgb.shape[:2]
            if min(h, w) < 2:
                continue
            if method == "ai":
                scale = args.ai_scale
                while scale > 1 and (max(h, w) + 2 * args.ai_pad) * scale > args.ai_max:
                    scale //= 2
                n, r = pbr(rgb, scale, args.ai_pad)
            else:
                n, r = procedural(rgb, args.proc_strength)
            Image.fromarray(encode_normal(n), "RGB").save(os.path.join(out_dir, k + "_n.png"))
            Image.fromarray(np.clip(np.round(r * 255), 0, 255).astype(np.uint8), "L").save(os.path.join(out_dir, k + "_r.png"))
            write_detail(os.path.join(out_dir, k + "_d.png"), n, r)
            man[k] = {"name": info["name"], "w": w, "h": h, "rmean": round(float(r.mean()), 4),
                      "nstd": round(float(np.sqrt((n[..., :2] ** 2).sum(-1).mean())), 4)}
            if (i + 1) % 200 == 0:
                print(f"{method}: {i + 1}/{len(keys)} {time.time() - t0:.0f}s", flush=True)
        json.dump(man, open(man_path, "w"), indent=1, sort_keys=True)
        print(f"{method}: {len(man)} maps in {out_dir}, {time.time() - t0:.0f}s", flush=True)


if __name__ == "__main__":
    main()
