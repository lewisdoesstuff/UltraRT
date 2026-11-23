# Material maps pipeline

Offline generation of per-texel normal and roughness maps for ULTRAKILL's textures, used by the plugin's hit shader
(docs/materials.md). Nothing here is committed except the scripts: the game's textures and every map derived from them
stay on your machine (the game's art is copyrighted).

| Script | Does |
|---|---|
| `extract.py` | UnityPy, read-only over `ULTRAKILL_Data`: every 2D texture a material uses as `_MainTex` or `_BlendTex`, plus the StaticSceneOptimizer atlases (`atlas_<bundle>@WxH`, for reference). Writes `<work>/src/<name>@<w>x<h>.png` and `textures.json` (materials, surface types, bundle). |
| `generate.py` | Per texture, at native resolution: `ai` (PBRify Remix normal + roughness networks through spandrel on CUDA) and `proc` (luminance height, Sobel normal, darkness/contrast roughness). Writes `<work>/<method>/<key>_n.png`, `_r.png` and `manifest.json`. |
| `variants.py` | From a generated set, three more for the in-engine matrix (docs/materials.md, section 9): `<set>-lp` (slopes low-passed over 3x3 texels), `<set>-hgt` (normals from the least-squares height), `<set>-blk` (that height quantised). numpy and Pillow only. |
| `matrix.py` | Crop sheet of one pose from the matrix runs: the game, maps off, roughness only and each set, per denoiser. |
| `compare.py` | Contact sheet: albedo, then per method the normal map, the albedo lit from the upper left with it, and the roughness map. |

## Setup (once)

```powershell
cd tools\materials
python -m venv .venv
.venv\Scripts\python.exe -m pip install -r requirements.txt
# CUDA PyTorch (an RTX 50 card needs CUDA 12.8 wheels). spandrel pulls a CPU torch from PyPI, so install the CUDA
# build afterwards, without dependencies:
.venv\Scripts\python.exe -m pip install --force-reinstall --no-deps torch torchvision --index-url https://download.pytorch.org/whl/cu128
# PBRify Remix models (CC0, Kim2091/PBRify_Remix on GitHub, Models/ folder):
mkdir models
gh api repos/Kim2091/PBRify_Remix/contents/Models/1x-PBRify_NormalV3.pth -H "Accept: application/vnd.github.raw" > models\1x-PBRify_NormalV3.pth
gh api repos/Kim2091/PBRify_Remix/contents/Models/1x-PBRify_RoughnessV2.pth -H "Accept: application/vnd.github.raw" > models\1x-PBRify_RoughnessV2.pth
```

`.venv/`, `models/` and `work/` are gitignored. The `.pth` files are pickles: spandrel loads them with
`torch.load(weights_only=True)`.

## Run

```powershell
$work = "$env:LOCALAPPDATA\UltraRT\materials-work"   # anywhere outside the repo
.venv\Scripts\python.exe -I extract.py --game <ULTRAKILL_DIR> --out $work   # ~1.5 min
.venv\Scripts\python.exe -I generate.py --cache $work                                                   # ai ~2.5 min (RTX 5070 Ti), proc ~30 s
```

Then copy `manifest.json` and `*_d.png` of `$work\ai` and `$work\proc` to `<game>\BepInEx\ultrart\materials\ai|proc\`
(about 130 MB each; `_d.png` packs the normal and the centred roughness, the plugin reads nothing else):

```powershell
foreach ($m in 'ai', 'proc') { robocopy "$work\$m" "<game>\BepInEx\ultrart\materials\$m" manifest.json *_d.png /NFL /NDL }
```

Do it while the game is closed, or under `scripts\game-lock.ps1` when agents share the
install. The plugin picks the set with `-ultrart-materials-source ai|proc` (config `[Materials] Source`).

## Options worth knowing

- `--ai-scale N`: nearest-neighbour pre-upscale before the network, then box-filtered back to one value per texel.
  Default 1. At 4x and 8x each texel is a flat block and the network returns nearly flat normals (rms tilt 0.23 at 1x
  vs 0.05 at 4x on `tile-light`), so native resolution is the right input for pixel art.
- `--ai-pad N`: wrap padding (texels) so tiling edges stay seamless. Default 8.
- `--only k1,k2`, `--suffix=-s4`: run a subset into another folder (experiments).
- `--pack-only`: rewrite the `_d.png` files from existing `_n`/`_r` PNGs (no GPU).
- `--proc-strength F`: height-to-slope scale of the procedural normal. Default 2.

Conventions: normal maps are OpenGL style (green = +v = up in the image); PBRify's DirectX output is flipped.
Roughness 0 = smooth. The runtime subtracts each texture's mean roughness (`rmean` in the manifest), so only the
per-texel variation is used, around the material class's own roughness.
