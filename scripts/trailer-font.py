"""Extract the game's UI font (VCR OSD Mono, as the level titles use it) for the trailer's graphics (scripts/trailer_gfx.py).

    python scripts/trailer-font.py "<ULTRAKILL folder>" [bench/results/trailer/fonts]

Needs UnityPy (pip install UnityPy). The font goes under bench/results (not in the repository); do not commit it.
"""
import os
import sys

import UnityPy

game = sys.argv[1]
out = sys.argv[2] if len(sys.argv) > 2 else os.path.join("bench", "results", "trailer", "fonts")
bundle = os.path.join(game, "ULTRAKILL_Data", "StreamingAssets", "aa", "StandaloneWindows64", "assets_assets_assets", "fonts.bundle")
os.makedirs(out, exist_ok=True)
n = 0
for obj in UnityPy.load(bundle).objects:
    if obj.type.name != "Font":
        continue
    d = obj.read()
    data = bytes(getattr(d, "m_FontData", b"") or b"")
    if not d.m_Name.startswith("VCR_OSD_MONO") or len(data) < 1000:
        continue
    path = os.path.join(out, "VCR_OSD_MONO.ttf")
    with open(path, "wb") as f:
        f.write(data)
    print("wrote", path, len(data), "bytes")
    n += 1
if not n:
    sys.exit("no VCR_OSD_MONO font in " + bundle)
