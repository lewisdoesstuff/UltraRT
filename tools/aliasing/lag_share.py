"""Share of the pan error that lag (ghosting) explains, per pose and mode (docs/aliasing.md, "Motion").

    py -3 tools/aliasing/lag_share.py bench/results/<run>_aliasing_alt

For each pan frame t (reference frame g, live frame HOLD + g) over the pixels whose reference changes by more than 8
levels from the previous frame: lag b = <L_t - R_t, R_t-1 - R_t> / |R_t-1 - R_t|^2, and the share of the error energy
it explains, max(b, 0)^2 |R_t-1 - R_t|^2 / |L_t - R_t|^2, summed over the slow and the fast segment.
"""
import argparse
import glob
import os
import re

import numpy as np
from PIL import Image

HOLD, SLOW, FAST = 8, 12, 12


def load(p):
    return np.asarray(Image.open(p).convert("RGB"), np.float32)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("run")
    a = ap.parse_args()
    poses = sorted({re.match(r"cine_(.+)_inst$", os.path.basename(p)).group(1)
                    for p in glob.glob(os.path.join(a.run, "cine_*_inst"))})
    for pose in poses:
        for m in ("off", "relight", "pathtrace"):
            pre = os.path.join(a.run, f"cine_{pose}_{m}")
            lf, rf = sorted(glob.glob(pre + "/frame_*.png")), sorted(glob.glob(pre + "-ref/frame_*.png"))
            if len(rf) < 2 or len(lf) < HOLD + len(rf):
                continue
            out = []
            for seg, rng in (("slow", range(1, SLOW + 1)), ("fast", range(SLOW + 1, min(SLOW + FAST + 1, len(rf))))):
                e_sum = g_sum = 0.0
                for g in rng:
                    r, rp, live = load(rf[g]), load(rf[g - 1]), load(lf[HOLD + g])
                    sel = np.abs(rp - r).max(-1) > 8
                    e, d = (live - r)[sel], (rp - r)[sel]
                    b = (e * d).sum() / max((d * d).sum(), 1e-6)
                    e_sum += (e * e).sum()
                    g_sum += max(b, 0.0) ** 2 * (d * d).sum()
                out.append(f"{seg} {g_sum / max(e_sum, 1e-6) * 100:.1f}%")
            print(f"{pose} {m}: lag share " + ", ".join(out), flush=True)


if __name__ == "__main__":
    main()
