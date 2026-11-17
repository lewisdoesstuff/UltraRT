"""Extract the textures ULTRAKILL's materials use (UnityPy, read-only over the game's data files).

Writes <out>/src/<key>.png for every 2D texture referenced by a material's _MainTex (and _BlendTex: VERTEX_BLENDING
secondary textures and the StaticSceneOptimizer blend atlases), plus <out>/textures.json:

  { "<key>": { "name": texture name, "w", "h", "format", "bundle", "materials": [material names],
               "surface": [SurfaceType values seen], "atlas": true if a StaticSceneData atlas } }

key = "<name>@<w>x<h>" with characters outside [A-Za-z0-9._-] replaced by '_'. The runtime (MaterialMaps.cs) looks
textures up by Unity name and size, so two different textures with the same name and size collide; the first one
seen wins and the collision is logged.

The source textures that StaticSceneOptimizer packed into a level's atlas are ordinary material textures (the level's
own materials and the shared models/materials bundles), so they are covered; the atlases themselves are extracted too
(marked "atlas") for reference and for the procedural fallback.

Usage (from the repo root, see tools/materials/README.md):
  tools/materials/.venv/Scripts/python.exe -I tools/materials/extract.py --game D:/.../ULTRAKILL --out <cache>
"""
import argparse
import json
import os
import re
import sys
import time

import UnityPy

SKIP_BUNDLES = ("sounds.bundle", "shaders.bundle", "music_")
TEX_PROPS = ("_MainTex", "_BlendTex")


def safe_key(name, w, h):
    return re.sub(r"[^A-Za-z0-9._-]", "_", name) + f"@{w}x{h}"


def data_files(game):
    data = os.path.join(game, "ULTRAKILL_Data")
    out = []
    aa = os.path.join(data, "StreamingAssets", "aa", "StandaloneWindows64")
    for root, _, files in os.walk(aa):
        for f in files:
            if f.endswith(".bundle") and not any(s in f for s in SKIP_BUNDLES):
                out.append(os.path.join(root, f))
    for f in os.listdir(data):
        if f.endswith(".assets") or re.fullmatch(r"level\d+", f):
            out.append(os.path.join(data, f))
    return sorted(out)


def pptr_target(obj, pptr):
    """(assets file name, path id) of a PPtr read from obj's typetree, or None."""
    fid, pid = pptr.get("m_FileID", 0), pptr.get("m_PathID", 0)
    if pid == 0:
        return None
    af = obj.assets_file
    if fid == 0:
        return (af.name.lower(), pid)
    ext = af.externals[fid - 1]
    path = getattr(ext, "path", None) or getattr(ext, "name", "")
    return (os.path.basename(path).lower(), pid)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", required=True, help="ULTRAKILL install folder")
    ap.add_argument("--out", required=True, help="cache folder (src/ and textures.json are written here)")
    ap.add_argument("--max-size", type=int, default=1024, help="skip textures larger than this (UI art, skies)")
    ap.add_argument("--only", default=None, help="regex: only bundles whose file name matches")
    args = ap.parse_args()

    src_dir = os.path.join(args.out, "src")
    os.makedirs(src_dir, exist_ok=True)
    files = data_files(args.game)
    if args.only:
        files = [f for f in files if re.search(args.only, os.path.basename(f))]

    # Pass 1: materials -> referenced texture ids; StaticSceneData atlases. Pass 2 decodes the textures.
    wanted = {}  # (assets name, path id) -> {"materials": set, "surface": set, "atlas": bool}
    t0 = time.time()
    for i, path in enumerate(files):
        try:
            env = UnityPy.load(path)
        except Exception as e:
            print(f"skip {path}: {e}", file=sys.stderr)
            continue
        nmat = 0
        for obj in env.objects:
            tname = obj.type.name
            if tname == "Material":
                try:
                    tree = obj.read_typetree()
                except Exception:
                    continue
                props = tree.get("m_SavedProperties", {})
                floats = dict(props.get("m_Floats", []))
                for prop, env_ in props.get("m_TexEnvs", []):
                    if prop not in TEX_PROPS:
                        continue
                    tgt = pptr_target(obj, env_.get("m_Texture", {}))
                    if tgt is None:
                        continue
                    e = wanted.setdefault(tgt, {"materials": set(), "surface": set(), "atlas": False})
                    e["materials"].add(tree.get("m_Name", "?") + ("" if prop == "_MainTex" else " (blend)"))
                    if "_SurfaceType" in floats:
                        e["surface"].add(int(floats["_SurfaceType"]))
                nmat += 1
            elif tname == "MonoBehaviour":
                try:
                    tree = obj.read_typetree()
                except Exception:
                    continue
                if "mainTexAtlas" in tree:
                    for fld in ("mainTexAtlas", "blendTexAtlas"):
                        tgt = pptr_target(obj, tree.get(fld, {}))
                        if tgt is not None:
                            e = wanted.setdefault(tgt, {"materials": set(), "surface": set(), "atlas": False})
                            e["atlas"] = True
                            e["materials"].add(f"StaticSceneData {tree.get('m_Name', '')} {fld} ({os.path.basename(path)})")
        print(f"[{i + 1}/{len(files)}] {os.path.basename(path)}: {nmat} materials, {len(wanted)} textures wanted, {time.time() - t0:.0f}s")
        del env

    manifest = {}
    collisions = 0
    for i, path in enumerate(files):
        try:
            env = UnityPy.load(path)
        except Exception:
            continue
        for obj in env.objects:
            if obj.type.name != "Texture2D":
                continue
            key = (obj.assets_file.name.lower(), obj.path_id)
            info = wanted.get(key)
            if info is None:
                continue
            try:
                tex = obj.read()
                w, h = tex.m_Width, tex.m_Height
                if w <= 0 or h <= 0 or (max(w, h) > args.max_size and not info["atlas"]):
                    continue
                name = tex.m_Name
                if info["atlas"]:  # StaticSceneData atlases are unnamed; name them after their bundle
                    name = "atlas_" + os.path.splitext(os.path.basename(path))[0] + ("_" + name if name else "")
                k = safe_key(name, w, h)
                if k in manifest:
                    collisions += 1
                    manifest[k]["materials"] = sorted(set(manifest[k]["materials"]) | info["materials"])
                    continue
                img = tex.image  # PIL, top row first
                img.save(os.path.join(src_dir, k + ".png"))
                manifest[k] = {
                    "name": name, "w": w, "h": h, "format": str(tex.m_TextureFormat),
                    "bundle": os.path.basename(path), "materials": sorted(info["materials"]),
                    "surface": sorted(info["surface"]), "atlas": info["atlas"],
                }
            except Exception as e:
                print(f"  texture {key}: {e}", file=sys.stderr)
        print(f"[{i + 1}/{len(files)}] {os.path.basename(path)}: {len(manifest)} textures written, {time.time() - t0:.0f}s")
        del env

    with open(os.path.join(args.out, "textures.json"), "w") as f:
        json.dump(manifest, f, indent=1, sort_keys=True)
    missing = len(wanted) - len(manifest)
    print(f"done: {len(manifest)} textures ({collisions} same name+size merged, {missing} referenced but not written: "
          f"too large, not 2D, or in a skipped file), {time.time() - t0:.0f}s")


if __name__ == "__main__":
    main()
