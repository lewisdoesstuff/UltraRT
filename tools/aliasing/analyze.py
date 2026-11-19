"""Aliasing metrics for a bench run of bench/suites/aliasing.json (docs/aliasing.md, "Method").

    py -3 tools/aliasing/analyze.py bench/results/<run>_aliasing [--crops 3] [--out <dir>]

Reads cine_<pose>_<take>/frame_NNNNN.png (tools/aliasing/make_suite.py lays the takes out) and writes
<run>/aliasing/metrics.json, metrics.md and crops/ (worst tiles: live | reference | un-jittered reference | error x4).
Needs numpy and Pillow only. Units: 8-bit display levels (0-255), RGB RMS.

Pixel classes (from the full-resolution debug views of the same frame, so they are geometry, not image content):
  edge     a geometric discontinuity: the instance changes, the normal turns > 30 deg, or the depth jumps > 4% (both
           pixels of the pair; sky / miss counts as an instance)
  texture  not an edge, but the AA reference has a luma gradient > 12 levels (texel edges, painted detail)
  flat     the rest
  spec     not an edge, specular surface (spec-params: specular weight / reflection strength > 0.05)
  hilite   spec pixels in the brightest 10% of spec pixels of the reference (sub-pixel highlights, reflections)

Static metrics (the still frame: live frame HOLD vs reference frame 0):
  gain        least-squares scale of live on reference over flat pixels (auto exposure differences); applied before all
              error numbers
  rmse_*      live vs AA reference per class
  alias_*     un-jittered reference vs AA reference per class: the aliasing of the mode's own converged image
  aa_edge     where live sits between the un-jittered (0) and the AA reference (1) on edge pixels:
              <L - R1, R - R1> / |R - R1|^2 over edge pixels with |R - R1| > 6. 0 = pixel-centre (aliased) edges,
              1 = anti-aliased like the reference; noise uncorrelated with the AA difference averages out
  aa_hilite   the same on highlight pixels (specular aliasing / normal-map filtering)
  stair       RMS of live - AA reference over the partial-coverage pixels (edge pixels where |R - R1| > 6)
Temporal metrics (pan frames, every frame against the reference of the same instant):
  shimmer_*   RMS over frames of the frame-to-frame change of the error (e_t - e_t-1) / sqrt 2, per class, for the
              still hold (static: noise and jitter flicker only, the reference does not change), the slow pan and
              the fast pan. Crawling stair-steps and flickering highlights raise it in motion above the static baseline
  rmse_*_pan  mean error against the reference over the slow / fast pan
  lag         ghosting / lag: <L_t - R_t, R_t-1 - R_t> / |R_t-1 - R_t|^2 over pixels whose reference changes by > 8
              between frames (moving edges and texels): the share of last frame's image still in this one
"""
import argparse
import glob
import json
import os
import re

import numpy as np
from PIL import Image

HOLD, SLOW, FAST = 8, 12, 12
MODES = ("off", "relight", "hybrid", "pathtrace")
LUMA = np.array([0.2126, 0.7152, 0.0722], np.float32)


def load(path):
    return np.asarray(Image.open(path).convert("RGB"), dtype=np.float32)


def frames(d):
    return sorted(glob.glob(os.path.join(d, "frame_*.png")))


def box(img, r):
    """Mean over a (2r+1)^2 box, edge-clamped (integral image)."""
    p = np.pad(img, r, mode="edge")
    c = np.cumsum(np.cumsum(p, 0), 1)
    c = np.pad(c, ((1, 0), (1, 0)))
    k = 2 * r + 1
    return (c[k:, k:] - c[:-k, k:] - c[k:, :-k] + c[:-k, :-k]) / (k * k)


def dilate(m, r=1):
    return box(m.astype(np.float32), r) > 0


def masks(inst, nrm, depth, spec, ref):
    """Pixel classes from the debug views (see the module doc)."""
    def pair_diff(a, thr_fn):
        e = np.zeros(a.shape[:2], bool)
        dx = thr_fn(a[:, 1:], a[:, :-1])
        dy = thr_fn(a[1:], a[:-1])
        e[:, 1:] |= dx; e[:, :-1] |= dx
        e[1:] |= dy; e[:-1] |= dy
        return e
    # Sky / miss: the debug views' magenta gradient (g = 0, b > r) in both the instance and the normals view.
    sky = (inst[..., 1] < 1) & (inst[..., 2] > inst[..., 0]) & (nrm[..., 1] < 1) & (nrm[..., 2] > nrm[..., 0])
    ei = pair_diff(inst, lambda a, b: np.abs(a - b).max(-1) > 8)
    n = nrm / 127.5 - 1.0
    n /= np.maximum(np.linalg.norm(n, axis=-1, keepdims=True), 1e-3)
    en = pair_diff(n, lambda a, b: (a * b).sum(-1) < np.cos(np.radians(30)))
    dz = depth[..., 0]
    # The depth view is 8-bit (1 - t / scale): only steps of 4+ levels that are also 15%+ of the distance count.
    ed = pair_diff(dz, lambda a, b: (np.abs(a - b) >= 4) & (np.abs(a - b) > 0.15 * np.maximum(255.0 - np.maximum(a, b), 1.0)))
    edge = ei | en | ed
    lum = ref @ LUMA
    gx = np.zeros_like(lum); gy = np.zeros_like(lum)
    gx[:, 1:-1] = (lum[:, 2:] - lum[:, :-2]) * 0.5
    gy[1:-1] = (lum[2:] - lum[:-2]) * 0.5
    tex = ~edge & (np.hypot(gx, gy) > 12)
    flat = ~edge & ~tex & ~sky
    specm = ~edge & ~sky & (np.maximum(spec[..., 2], spec[..., 1]) > 13)
    hil = np.zeros_like(specm)
    if specm.sum() > 100:
        thr = np.percentile(lum[specm], 90)
        hil = specm & (lum >= thr) & (lum > 24)
    return {"edge": edge, "texture": tex & ~sky, "flat": flat, "spec": specm, "hilite": hil, "sky": sky & ~edge}


def rms(d, m):
    return float(np.sqrt((d[m] ** 2).mean())) if m.sum() > 0 else float("nan")


def proj(x, d, m, thr):
    """<x, d> / <d, d> over pixels of m where |d| > thr (per-pixel RGB dot products)."""
    sel = m & (np.abs(d).max(-1) > thr)
    if sel.sum() < 50:
        return float("nan"), int(sel.sum())
    return float((x[sel] * d[sel]).sum() / max((d[sel] ** 2).sum(), 1e-6)), int(sel.sum())


def fit_gain(live, ref, m):
    a, b = live[m], ref[m]
    return float((a * b).sum() / max((b * b).sum(), 1e-6)) if m.sum() > 100 else 1.0


def crop_tiles(err, m, n, tile=96):
    """The n tiles (non-overlapping) with the highest error energy on mask m."""
    e2 = (err ** 2).sum(-1) * m
    h, w = e2.shape
    th, tw = h // tile, w // tile
    s = e2[:th * tile, :tw * tile].reshape(th, tile, tw, tile).sum((1, 3))
    order = np.argsort(s.ravel())[::-1][:n]
    return [(int(i // tw) * tile, int(i % tw) * tile) for i in order]


def save_crop(path, panels, y, x, tile=96, scale=3):
    tiles = []
    for p in panels:
        t = np.clip(p[y:y + tile, x:x + tile], 0, 255).astype(np.uint8)
        tiles.append(np.kron(t, np.ones((scale, scale, 1), np.uint8)))
    sep = np.full((tile * scale, 4, 3), 255, np.uint8)
    row = tiles[0]
    for t in tiles[1:]:
        row = np.concatenate([row, sep, t], 1)
    Image.fromarray(row).save(path)


def analyze_pose(run, pose, out, ncrops):
    pre = os.path.join(run, f"cine_{pose}_")
    have = lambda t: os.path.isdir(pre + t) and frames(pre + t)
    need = ("inst", "nrm", "depth", "spec")
    if not all(have(t) for t in need):
        print(f"{pose}: missing debug views, skipped")
        return None
    mk = {t: frames(pre + t) for t in need}
    npan = len(mk["inst"])
    # Masks per reference frame (cached: they are reused by every mode).
    cache = {}

    def mask_at(g, ref_off):
        if g not in cache:
            cache[g] = masks(*(load(mk[t][min(g, len(mk[t]) - 1)]) for t in need), ref_off)
        return cache[g]

    res = {"pose": pose, "frames": npan, "labels": []}
    off_ref = frames(pre + "off-ref")
    m0 = mask_at(0, load(off_ref[0]))
    total = m0["edge"].size
    res["share"] = {k: float(v.sum() / total) for k, v in m0.items()}
    labels = []
    for mode in MODES:
        labels.append((mode, mode))
        for d in sorted(glob.glob(pre + mode + "_*")):  # live variants: <mode>@<tag> (sanitised to <mode>_<tag>)
            labels.append((mode + "@" + d[len(pre + mode) + 1:], mode))
    for label, mode in labels:
        live_f, ref_f = frames(pre + label.replace("@", "_")), frames(pre + mode + "-ref")
        ref1_f = frames(pre + mode + "-ref1") if mode != "off" else live_f[HOLD:HOLD + 1]
        if not live_f or not ref_f:
            continue
        r = {}
        res["labels"].append(label)
        L, R, R1 = load(live_f[HOLD]), load(ref_f[0]), load(ref1_f[0])
        m = mask_at(0, load(off_ref[0]))
        a = fit_gain(L, R, m["flat"])
        r["gain"] = a
        L /= a
        a1 = fit_gain(R1, R, m["flat"])
        R1 /= a1
        d, d1 = L - R, R1 - R
        for k in ("edge", "texture", "flat", "spec", "hilite"):
            r["rmse_" + k] = rms(d, m[k])
            r["alias_" + k] = rms(d1, m[k])
        r["aa_edge"], r["aa_edge_px"] = proj(L - R1, -d1, m["edge"], 6)
        r["aa_hilite"], r["aa_hilite_px"] = proj(L - R1, -d1, m["hilite"], 6)
        part = m["edge"] & (np.abs(d1).max(-1) > 6)
        r["stair"] = rms(d, part)
        r["partial_share"] = float(part.sum() / total)
        # Static flicker: hold frames against the (constant) reference.
        prev = None
        acc = {k: [] for k in ("edge", "flat", "hilite", "texture")}
        for f in range(1, HOLD + 1):
            e = load(live_f[f]) / a - R
            if prev is not None:
                for k in acc:
                    acc[k].append(((e - prev) ** 2)[m[k]].mean() if m[k].sum() else np.nan)
            prev = e
        for k in acc:
            r[f"shimmer_{k}_static"] = float(np.sqrt(np.nanmean(acc[k]) / 2)) if acc[k] else float("nan")
        # Pan.
        if len(ref_f) > 1 and len(live_f) >= HOLD + len(ref_f):
            segs = {"slow": range(1, SLOW + 1), "fast": range(SLOW + 1, min(SLOW + FAST + 1, len(ref_f)))}
            prev_e, prev_R = d, R
            per = {s: {"edge": [], "flat": [], "hilite": [], "texture": [], "rmse_edge": [], "rmse_flat": [], "lag": [], "gain": []}
                   for s in segs}
            for g in range(1, len(ref_f)):
                seg = "slow" if g in segs["slow"] else "fast"
                Rg = load(ref_f[g])
                mg = mask_at(g, load(off_ref[min(g, len(off_ref) - 1)]))
                Lg = load(live_f[HOLD + g])
                ag = fit_gain(Lg, Rg, mg["flat"])
                Lg /= ag
                e = Lg - Rg
                for k in ("edge", "flat", "hilite", "texture"):
                    mm = mg[k]
                    per[seg][k].append(((e - prev_e) ** 2)[mm].mean() if mm.sum() else np.nan)
                per[seg]["rmse_edge"].append(rms(e, mg["edge"]))
                per[seg]["rmse_flat"].append(rms(e, mg["flat"]))
                lag, _ = proj(e, prev_R - Rg, np.ones_like(mg["edge"]), 8)
                per[seg]["lag"].append(lag)
                per[seg]["gain"].append(ag)
                prev_e, prev_R = e, Rg
            for s, v in per.items():
                for k in ("edge", "flat", "hilite", "texture"):
                    r[f"shimmer_{k}_{s}"] = float(np.sqrt(np.nanmean(v[k]) / 2)) if v[k] else float("nan")
                r[f"rmse_edge_{s}"] = float(np.nanmean(v["rmse_edge"])) if v["rmse_edge"] else float("nan")
                r[f"rmse_flat_{s}"] = float(np.nanmean(v["rmse_flat"])) if v["rmse_flat"] else float("nan")
                r[f"lag_{s}"] = float(np.nanmean(v["lag"])) if v["lag"] else float("nan")
        res[label] = r
        if label != mode:
            continue
        # Worst tiles of the still frame: edges, and highlights.
        if ncrops:
            cdir = os.path.join(out, "crops")
            os.makedirs(cdir, exist_ok=True)
            heat = np.clip(np.abs(d) * 4, 0, 255)
            for kind in ("edge", "hilite"):
                if m[kind].sum() < 200:
                    continue
                for i, (y, x) in enumerate(crop_tiles(d, m[kind], ncrops)):
                    save_crop(os.path.join(cdir, f"{pose}_{mode}_{kind}{i}_{x}x{y}.png"), [L, R, R1, heat], y, x)
    return res


def fmt(v, p=2):
    return "-" if v is None or (isinstance(v, float) and np.isnan(v)) else f"{v:.{p}f}"


def table(results):
    out = []
    out.append("### Static (still frame vs AA reference)\n")
    out.append("| Pose | Mode | gain | edge RMSE | alias edge (ref1 vs ref) | AA position edge | stair | texture RMSE | flat RMSE | hilite RMSE | alias hilite | AA position hilite |")
    out.append("|---|---|---|---|---|---|---|---|---|---|---|---|")
    for res in results:
        for mode in res["labels"]:
            r = res.get(mode)
            out.append(f"| {res['pose']} | {mode} | {fmt(r['gain'])} | {fmt(r['rmse_edge'])} | {fmt(r['alias_edge'])} | "
                       f"{fmt(r['aa_edge'])} | {fmt(r['stair'])} | {fmt(r['rmse_texture'])} | {fmt(r['rmse_flat'])} | "
                       f"{fmt(r['rmse_hilite'])} | {fmt(r['alias_hilite'])} | {fmt(r['aa_hilite'])} |")
    out.append("\n### Temporal (frame-to-frame change of the error; static = held camera)\n")
    out.append("| Pose | Mode | edge static / slow / fast | hilite static / slow / fast | flat static / slow / fast | edge RMSE slow / fast | lag slow / fast |")
    out.append("|---|---|---|---|---|---|---|")
    for res in results:
        for mode in res["labels"]:
            r = res.get(mode)
            g = lambda k: " / ".join(fmt(r.get(f"shimmer_{k}_{s}")) for s in ("static", "slow", "fast"))
            out.append(f"| {res['pose']} | {mode} | {g('edge')} | {g('hilite')} | {g('flat')} | "
                       f"{fmt(r.get('rmse_edge_slow'))} / {fmt(r.get('rmse_edge_fast'))} | {fmt(r.get('lag_slow'))} / {fmt(r.get('lag_fast'))} |")
    out.append("\n### Pixel shares (still frame)\n")
    out.append("| Pose | edge | partial coverage (relight) | texture | flat | spec | hilite |")
    out.append("|---|---|---|---|---|---|---|")
    for res in results:
        s = res["share"]
        pc = res.get("relight", {}).get("partial_share", float("nan"))
        out.append(f"| {res['pose']} | {s['edge'] * 100:.1f}% | {pc * 100:.1f}% | {s['texture'] * 100:.1f}% | {s['flat'] * 100:.1f}% | "
                   f"{s['spec'] * 100:.1f}% | {s['hilite'] * 100:.1f}% |")
    return "\n".join(out) + "\n"


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("run")
    ap.add_argument("--crops", type=int, default=2, help="worst tiles per pose, mode and class (0 = none)")
    ap.add_argument("--out", help="default: <run>/aliasing")
    a = ap.parse_args()
    out = a.out or os.path.join(a.run, "aliasing")
    os.makedirs(out, exist_ok=True)
    poses = sorted({re.match(r"cine_(.+)_inst$", os.path.basename(p)).group(1)
                    for p in glob.glob(os.path.join(a.run, "cine_*_inst"))})
    results = []
    for p in poses:
        print("pose", p, flush=True)
        r = analyze_pose(a.run, p, out, a.crops)
        if r:
            results.append(r)
    with open(os.path.join(out, "metrics.json"), "w") as f:
        json.dump(results, f, indent=1)
    md = table(results)
    with open(os.path.join(out, "metrics.md"), "w", newline="\n") as f:
        f.write(md)
    print(md)


if __name__ == "__main__":
    main()
