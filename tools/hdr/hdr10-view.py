#!/usr/bin/env python3
"""Reads an HDR10 frame captured by UltraRT's swapchain proxy (docs/hdr.md) and reports what the display was sent.

A capture is written by a bench run with  -Extra '-ultrart-hdr','on','-ultrart-hdr-capture','raw'  next to each
screenshot as <screenshot>.png.hdr10.bin: "URTHDR10", width, height (uint32), then rows of R10G10B10A2 (PQ, Rec.2020).

  python tools/hdr/hdr10-view.py <file.hdr10.bin> [--paper 200] [--out <folder>] [--sdr <screenshot.png>]

Prints luminance statistics and writes two PNGs:
  <name>.nits.png   false colour of luminance: grey up to paper white, then yellow -> red -> magenta -> white towards
                    the frame's peak (where the HDR highlights are)
  <name>.sdr.png    the frame shown as an SDR display would at paper white (everything above clips), to compare with
                    the game's own screenshot; --sdr prints the difference to it
  <name>.peak.png   the whole frame scaled so the display peak is white (linear light, sRGB encoded): what is brighter
                    than what on the HDR display, with hue, e.g. an effect against the wall behind it (--peak, nits,
                    default the frame's brightest channel)

Also prints, per channel (Rec.709), the share of pixels at the peak (99 % and 99.9 % of --peak or the brightest
channel): where the display transform has cut the gradation flat.

  --sheet <out.png> <file2> [...]   a review sheet: one column per capture (the first file, then the others), rows
                    peak-scaled (as .peak.png) and paper-white exposed (everything above paper white clips, as .sdr.png
                    but in linear light, sRGB encoded); --crop y0,y1,x0,x1 and --scale shrink it
  --scene           the capture is a -ultrart-hdr-tone log frame (docs/hdr.md, "Clipping"): decodes the scene values
                    (display linear, 1 = paper white) and prints their range instead (--scene-peak: the run's
                    -ultrart-hdr-peak, default 10000)
"""
import argparse
import os
import struct
import sys

import numpy as np
from PIL import Image

M1, M2, C1, C2, C3 = 0.1593017578125, 78.84375, 0.8359375, 18.8515625, 18.6875
TO_709 = np.array([[1.660491, -0.587641, -0.072850], [-0.124550, 1.132900, -0.008349], [-0.018151, -0.100579, 1.118730]], dtype=np.float32)


def read(path):
    with open(path, 'rb') as f:
        if f.read(8) != b'URTHDR10':
            sys.exit(f'{path}: not an UltraRT HDR10 capture')
        w, h = struct.unpack('<II', f.read(8))
        px = np.frombuffer(f.read(w * h * 4), dtype='<u4').reshape(h, w)
    code = np.stack([px & 1023, (px >> 10) & 1023, (px >> 20) & 1023], axis=-1)
    lut = np.arange(1024, dtype=np.float64) / 1023.0
    p = lut ** (1.0 / M2)
    lut = 10000.0 * (np.maximum(p - C1, 0.0) / (C2 - C3 * p)) ** (1.0 / M1)
    return lut.astype(np.float32)[code], code  # nits, Rec.2020


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('file')
    ap.add_argument('--paper', type=float, default=200.0, help='paper white of the run, nits')
    ap.add_argument('--out')
    ap.add_argument('--sdr', help="the game's screenshot of the same frame")
    ap.add_argument('--peak', type=float, help="display peak for <name>.peak.png, nits (default: the frame's brightest channel)")
    ap.add_argument('--sheet', nargs='+', metavar=('OUT', 'FILE'), help='review sheet: output png, then more captures')
    ap.add_argument('--crop', help='sheet crop y0,y1,x0,x1 (pixels)')
    ap.add_argument('--scale', type=float, default=1.0, help='sheet scale')
    ap.add_argument('--scene', action='store_true', help='decode a -ultrart-hdr-tone log capture')
    ap.add_argument('--scene-peak', type=float, default=10000.0, help='the -ultrart-hdr-peak of the log capture, nits')
    a = ap.parse_args()
    if a.sheet:
        return sheet(a, [a.file] + a.sheet[1:], a.sheet[0])
    if a.scene:
        return scene(a)
    nits, code = read(a.file)
    h, w, _ = nits.shape
    lum = nits @ np.array([0.2627, 0.6780, 0.0593], dtype=np.float32)
    n = lum.size
    print(f'{os.path.basename(a.file)}: {w}x{h}, PQ codes up to {int(code.max())}')
    print(f'  luminance: mean {lum.mean():.1f} nits, median {np.median(lum):.1f}, 99% {np.percentile(lum, 99):.0f}, '
          f'99.9% {np.percentile(lum, 99.9):.0f}, max {lum.max():.0f}; brightest channel {nits.max():.0f} nits')
    for k in (1.05, 1.5, 2.0, 3.0, 4.0):
        print(f'  above {k:.2f}x paper white ({a.paper * k:.0f} nits): {100.0 * np.count_nonzero(lum > a.paper * k) / n:.3f}% of pixels')

    rgb = np.maximum(nits @ TO_709.T, 0.0)
    pk_n = a.peak or float(rgb.max())
    for q in (0.99, 0.999):
        at = rgb >= q * pk_n
        print(f'  at {100 * q:g}% of the peak ({pk_n:.0f} nits) or more: R {100 * at[..., 0].mean():.3f}%, G {100 * at[..., 1].mean():.3f}%, '
              f'B {100 * at[..., 2].mean():.3f}%, any channel {100 * at.any(-1).mean():.3f}% of pixels')
    out = a.out or os.path.dirname(os.path.abspath(a.file))
    os.makedirs(out, exist_ok=True)
    base = os.path.join(out, os.path.basename(a.file).replace('.png.hdr10.bin', '').replace('.hdr10.bin', ''))

    rgb709 = np.maximum(nits @ TO_709.T, 0.0)
    sdr = np.clip(rgb709 / a.paper, 0.0, 1.0) ** (1.0 / 2.2)
    Image.fromarray((sdr * 255.0 + 0.5).astype(np.uint8)).save(base + '.sdr.png')
    x = np.clip(rgb709 / (a.peak or max(float(rgb709.max()), 1.0)), 0.0, 1.0)
    pk = np.where(x <= 0.0031308, x * 12.92, 1.055 * x ** (1.0 / 2.4) - 0.055)
    Image.fromarray((pk * 255.0 + 0.5).astype(np.uint8)).save(base + '.peak.png')

    peak = max(float(lum.max()), a.paper * 1.01)
    grey = (np.clip(lum / a.paper, 0.0, 1.0) ** (1.0 / 2.2))[..., None] * np.array([0.6, 0.6, 0.6], dtype=np.float32)
    t = np.clip(np.log2(np.maximum(lum, 1e-3) / a.paper) / np.log2(peak / a.paper), 0.0, 1.0)[..., None]
    stops = np.array([[1.0, 1.0, 0.0], [1.0, 0.0, 0.0], [1.0, 0.0, 1.0], [1.0, 1.0, 1.0]], dtype=np.float32)
    x = t * 3.0
    i = np.minimum(x.astype(np.int32), 2)
    heat = stops[i[..., 0]] * (1.0 - (x - i)) + stops[i[..., 0] + 1] * (x - i)
    img = np.where(lum[..., None] > a.paper * 1.05, heat, grey)
    Image.fromarray((np.clip(img, 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8)).save(base + '.nits.png')
    print(f'  wrote {base}.sdr.png, {base}.peak.png and {base}.nits.png (false colour from {a.paper:.0f} to {peak:.0f} nits)')

    if a.sdr:
        ref = np.asarray(Image.open(a.sdr).convert('RGB'), dtype=np.float32) / 255.0
        if ref.shape[:2] != (h, w):
            print(f'  --sdr: size differs ({ref.shape[1]}x{ref.shape[0]})')
        else:
            d = np.abs(sdr - ref)
            below = (lum <= a.paper * 1.02)
            print(f'  against {os.path.basename(a.sdr)}: mean abs difference {d.mean() * 255:.2f} / 255 over the frame, '
                  f'{d[below].mean() * 255:.2f} where the HDR frame is at or below paper white')


def linear709(path):
    return np.maximum(read(path)[0] @ TO_709.T, 0.0)


def encode(x):
    x = np.clip(x, 0.0, 1.0)
    return (np.where(x <= 0.0031308, x * 12.92, 1.055 * x ** (1.0 / 2.4) - 0.055) * 255.0 + 0.5).astype(np.uint8)


def sheet(a, files, out):
    cols = []
    for f in files:
        rgb = linear709(f)
        if a.crop:
            y0, y1, x0, x1 = (int(v) for v in a.crop.split(','))
            rgb = rgb[y0:y1, x0:x1]
        peak = a.peak or float(rgb.max())
        cols.append(np.concatenate([encode(rgb / peak), encode(rgb / a.paper)], 0))
        print(f'  column {len(cols)}: {os.path.basename(f)}')
    im = Image.fromarray(np.concatenate(cols, 1))
    if a.scale != 1.0:
        im = im.resize((int(im.width * a.scale), int(im.height * a.scale)), Image.LANCZOS)
    im.save(out)
    print(f'  wrote {out}: rows peak-scaled ({a.peak or "each frame's brightest channel"} nits = white) and paper white ({a.paper:.0f} nits) exposed')


def scene(a):
    # -ultrart-hdr-tone log: y = peak x log2(1 + x) / log2(9411) per channel in linear light (UltraRTHdrDisplay mode 4)
    t = a.scene_peak / a.paper
    x = np.exp2(np.clip(linear709(a.file) / a.paper / t, 0.0, 1.0) * np.log2(9411.0)) - 1.0
    m = x.max(-1)
    print(f'{os.path.basename(a.file)}: scene values (linear light, 1 = paper white), brightest channel: '
          f'p50 {np.percentile(m, 50):.2f}, p90 {np.percentile(m, 90):.2f}, p99 {np.percentile(m, 99):.1f}, p99.9 {np.percentile(m, 99.9):.1f}, max {m.max():.0f}')
    for k in (1.0, 3.0, 11.2, 26.0, 100.0):
        print(f'  brightest channel above {k:g}: {100 * np.mean(m > k):.3f}% of pixels')
    return x


if __name__ == '__main__':
    main()
