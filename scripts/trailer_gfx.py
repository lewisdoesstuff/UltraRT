"""Motion graphics for scripts/trailer.py (docs/trailer.md, "Graphics"): title and end cards, lower-third captions,
state badges and level tags, drawn frame by frame with Pillow into PNG sequences that the assembly overlays.

One visual idea runs through all of them: a thin slanted line of light (the same line that sweeps the before / after
comparisons) passes, and what it passes over appears. Colours: white type, one warm accent.

Every function writes frames into a folder and returns { "pattern", "frames", "w", "h" } (frame numbers from 0).
Sizes are fractions of the picture height, so the same call serves the 720p draft and the 1440p master.
"""
import math
import os

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

FONT = "C:/Windows/Fonts/bahnschrift.ttf"
ACCENT = (255, 78, 40)
SLANT = 0.18  # horizontal shift per unit of height, as the wipes' default


def font(px, style=b"Bold Condensed"):
    f = ImageFont.truetype(FONT, max(4, int(round(px))))
    try:
        f.set_variation_by_name(style)
    except (OSError, ValueError, AttributeError):
        pass
    return f


def ease_out(t):
    t = min(1.0, max(0.0, t))
    return 1 - (1 - t) ** 3


def ease_in_out(t):
    t = min(1.0, max(0.0, t))
    return 4 * t * t * t if t < 0.5 else 1 - (-2 * t + 2) ** 3 / 2


def ramp(t, t0, d):
    return min(1.0, max(0.0, (t - t0) / max(1e-6, d)))


def text_size(text, f, tracking=0.0):
    w = 0.0
    for ch in text:
        w += f.getlength(ch) + tracking
    asc, desc = f.getmetrics()
    return int(math.ceil(w - tracking)) if text else 0, asc + desc


def draw_text(img, xy, text, f, fill, tracking=0.0):
    """Text with letter spacing; returns the x of each glyph (for per-letter effects)."""
    d = ImageDraw.Draw(img)
    x, y = xy
    xs = []
    for ch in text:
        xs.append(x)
        d.text((x, y), ch, font=f, fill=fill)
        x += f.getlength(ch) + tracking
    return xs


def text_layer(text, f, fill=(255, 255, 255, 255), tracking=0.0, pad=0):
    w, h = text_size(text, f, tracking)
    img = Image.new("RGBA", (w + 2 * pad + 2, h + 2 * pad), (0, 0, 0, 0))
    xs = draw_text(img, (pad, pad), text, f, fill, tracking)
    return img, xs


def with_alpha(img, a):
    if a >= 0.999:
        return img
    out = img.copy()
    out.putalpha(img.getchannel("A").point(lambda v: int(v * max(0.0, a))))
    return out


def glow(img, radius, strength=1.0):
    """A blurred copy of the layer, to put under it."""
    g = img.filter(ImageFilter.GaussianBlur(radius))
    if strength != 1.0:
        g.putalpha(g.getchannel("A").point(lambda v: min(255, int(v * strength))))
    return g


def slanted_mask(size, x_mid, soft, slant=SLANT, invert=False):
    """L mask: 255 left of a slanted edge that crosses the vertical middle at x_mid (soft: blur in px)."""
    w, h = size
    m = Image.new("L", size, 0)
    dx = slant * h / 2
    ImageDraw.Draw(m).polygon([(-10, -10), (x_mid + dx, -10), (x_mid - dx, h + 10), (-10, h + 10)], fill=255)
    if soft > 0:
        m = m.filter(ImageFilter.GaussianBlur(soft))
    return ImageChops.invert(m) if invert else m


def ray(size, x_mid, width, color, alpha, slant=SLANT):
    """The line of light: a bright slanted stroke with a soft halo."""
    w, h = size
    layer = Image.new("RGBA", size, (0, 0, 0, 0))
    if alpha <= 0:
        return layer
    dx = slant * h / 2
    d = ImageDraw.Draw(layer)
    d.line([(x_mid + dx, -4), (x_mid - dx, h + 4)], fill=color + (int(255 * alpha),), width=max(1, int(round(width))))
    halo = layer.filter(ImageFilter.GaussianBlur(max(1.0, width * 3)))
    halo.putalpha(halo.getchannel("A").point(lambda v: min(255, int(v * 2.2))))
    return Image.alpha_composite(halo, layer)


def save(frames_dir, i, img):
    img.save(os.path.join(frames_dir, "f_%05d.png" % i), compress_level=1)


def _result(frames_dir, n, size):
    return {"pattern": os.path.join(frames_dir, "f_%05d.png"), "frames": n, "w": size[0], "h": size[1]}


# ---------------------------------------------------------------- cards

def card(frames_dir, W, H, fps, dur, title, accent_from=5, sub=None, lines=(), small=None, scale=1.0, y=0.43, out_fade=0.45):
    """A full-frame card on black. The line of light crosses once and lights the title letter by letter (the letters
    from accent_from on take the accent colour: ULTRA + RT); a hairline opens under it, the sub line tightens its
    spacing as it fades in, further lines follow one after another; everything drifts slowly towards the viewer."""
    os.makedirs(frames_dir, exist_ok=True)
    n = int(round(dur * fps))
    tf = font(H * 0.17 * scale)
    track = H * 0.012 * scale
    tw, th = text_size(title, tf, track)
    pad = int(H * 0.08)
    base = Image.new("RGBA", (tw + 2 * pad, th + 2 * pad), (0, 0, 0, 0))
    xs = draw_text(base, (pad, pad), title[:accent_from], tf, (255, 255, 255, 255), track)
    x_acc = xs[-1] + tf.getlength(title[accent_from - 1]) + track if xs else pad
    xs += draw_text(base, (x_acc, pad), title[accent_from:], tf, ACCENT + (255,), track)
    letters = []
    for k, ch in enumerate(title):
        x0 = int(xs[k] - track / 2)
        x1 = int((xs[k + 1] if k + 1 < len(xs) else xs[k] + tf.getlength(ch) + track) - track / 2)
        letters.append((x0, x1))
    hot = glow(base, H * 0.012, 1.6)
    cx, cy = W / 2, H * y
    sub_f = font(H * 0.036 * scale, b"SemiBold SemiCondensed")
    line_f = font(H * 0.024, b"SemiLight")
    small_f = font(H * 0.021, b"Light")
    sweep0, sweep1 = 0.35, 1.25
    for i in range(n):
        t = i / fps
        frame = Image.new("RGBA", (W, H), (0, 0, 0, 255))
        # title: each letter appears as the ray passes it, glowing hot for a moment
        layer = Image.new("RGBA", base.size, (0, 0, 0, 0))
        p = ease_in_out(ramp(t, sweep0, sweep1 - sweep0))
        ray_x = -pad + p * (base.size[0] + 2 * pad)
        for (x0, x1) in letters:
            mid = (x0 + x1) / 2
            if ray_x < mid:
                continue
            t_hit = sweep0 + (sweep1 - sweep0) * _inv_ease((mid + pad) / (base.size[0] + 2 * pad))
            a = ease_out(ramp(t, t_hit, 0.18))
            heat = max(0.0, 1 - ramp(t, t_hit, 0.7))
            box = (x0, 0, x1, base.size[1])
            if heat > 0.02:
                layer.alpha_composite(with_alpha(hot.crop(box), heat * a), (x0, 0))
            layer.alpha_composite(with_alpha(base.crop(box), a), (x0, 0))
        if 0 < p < 1:
            layer = Image.alpha_composite(layer, ray(base.size, ray_x, H * 0.004, (255, 255, 255), math.sin(p * math.pi) ** 0.5))
        z = 1.0 + 0.035 * (t / max(dur, 1e-6))  # slow push in
        lw, lh = int(layer.size[0] * z), int(layer.size[1] * z)
        zl = layer.resize((lw, lh), Image.BILINEAR)
        frame.alpha_composite(zl, (int(cx - lw / 2), int(cy - lh / 2)))
        # hairline under the title, opening from the centre
        hl = ease_out(ramp(t, sweep1 - 0.15, 0.6))
        if hl > 0:
            half = hl * tw * 0.5 * z
            yy = int(cy + th * 0.5 * z + H * 0.012)
            ImageDraw.Draw(frame).line([(cx - half, yy), (cx + half, yy)], fill=(255, 255, 255, 150), width=max(1, H // 720))
        # sub line: spacing eases in from wide
        yy = cy + th * 0.5 * z + H * 0.045
        if sub:
            a = ease_out(ramp(t, sweep1, 0.7))
            if a > 0:
                tr = H * (0.014 + 0.03 * (1 - a)) * scale
                sl, _ = text_layer(sub, sub_f, (255, 255, 255, 235), tr)
                frame.alpha_composite(with_alpha(sl, a), (int(cx - sl.size[0] / 2), int(yy)))
            yy += H * 0.07 * scale
        for k, ln in enumerate(lines):
            a = ease_out(ramp(t, sweep1 + 0.35 + 0.22 * k, 0.6))
            if a > 0:
                sl, _ = text_layer(ln, line_f, (255, 255, 255, 200), H * 0.004)
                frame.alpha_composite(with_alpha(sl, a), (int(cx - sl.size[0] / 2), int(yy + (1 - a) * H * 0.008)))
            yy += H * 0.042
        if small:
            for k, ln in enumerate(small):
                a = ease_out(ramp(t, sweep1 + 0.9 + 0.15 * k, 0.6)) * 0.62
                if a > 0:
                    sl, _ = text_layer(ln, small_f, (255, 255, 255, 255), H * 0.0015)
                    frame.alpha_composite(with_alpha(sl, a), (int(cx - sl.size[0] / 2), int(H * (0.865 + 0.034 * k))))
        out = frame.convert("RGB")
        fade = 1 - ramp(t, dur - out_fade, out_fade) if out_fade > 0 else 1.0
        if fade < 1:
            out = Image.blend(Image.new("RGB", (W, H), (0, 0, 0)), out, max(0.0, fade))
        save(frames_dir, i, out)
    return _result(frames_dir, n, (W, H))


def _inv_ease(p):
    """Inverse of ease_in_out (the ray's position as a time fraction)."""
    p = min(1.0, max(0.0, p))
    return (p / 4) ** (1 / 3) if p < 0.5 else 1 - ((1 - p) * 2) ** (1 / 3) / 2


# ---------------------------------------------------------------- overlays

def lower_third(frames_dir, H, fps, dur, text, sub=None):
    """A feature caption: an accent bar grows, the line of light crosses and uncovers the heading, the sub line rises in
    under it; at the end the heading is covered again the same way."""
    os.makedirs(frames_dir, exist_ok=True)
    hf = font(H * 0.052)
    sf = font(H * 0.027, b"SemiLight SemiCondensed")
    head, _ = text_layer(text.upper(), hf, (255, 255, 255, 255), H * 0.004)
    subl = text_layer(sub, sf, (255, 255, 255, 215), H * 0.002)[0] if sub else None
    bar_w = max(2, int(H * 0.005))
    gap = int(H * 0.018)
    pad = int(H * 0.03)
    w = bar_w + gap + max(head.size[0], subl.size[0] if subl else 0) + 2 * pad
    h = head.size[1] + (subl.size[1] + int(H * 0.004) if subl else 0) + 2 * pad
    shadow_src = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    shadow_src.alpha_composite(head, (pad + bar_w + gap, pad))
    if subl:
        shadow_src.alpha_composite(subl, (pad + bar_w + gap, pad + head.size[1] + int(H * 0.004)))
    shade = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    shade.putalpha(shadow_src.getchannel("A").filter(ImageFilter.GaussianBlur(H * 0.012)).point(lambda v: min(255, int(v * 1.5))))
    n = int(round(dur * fps))
    t_in, t_out = 0.55, 0.4
    x_text = pad + bar_w + gap
    for i in range(n):
        t = i / fps
        closing = ramp(t, dur - t_out, t_out)
        frame = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        vis = 1 - ease_out(closing)
        # bar
        bh = ease_out(ramp(t, 0, 0.3)) * (h - 2 * pad) * vis
        if bh > 1:
            mid = h / 2
            ImageDraw.Draw(frame).rectangle([pad, mid - bh / 2, pad + bar_w, mid + bh / 2], fill=ACCENT + (255,))
        p = ease_in_out(ramp(t, 0.08, t_in))
        edge = x_text - pad * 0.5 + p * (head.size[0] + pad * 1.5)
        if closing > 0:
            edge = x_text - pad * 0.5 + (1 - ease_in_out(closing)) * (head.size[0] + pad * 1.5)
        mask = slanted_mask((w, h), edge, 1.5)
        content = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        content.alpha_composite(with_alpha(shade, 0.55))
        content.alpha_composite(head, (x_text + int((1 - p) * H * 0.012), pad))
        if subl:
            a = ease_out(ramp(t, 0.3, 0.5))
            content.alpha_composite(with_alpha(subl, a), (x_text, pad + head.size[1] + int(H * 0.004) + int((1 - a) * H * 0.01)))
        content.putalpha(ImageChops.multiply(content.getchannel("A"), mask))
        frame.alpha_composite(content)
        ra = math.sin(p * math.pi) if closing <= 0 else math.sin(closing * math.pi)
        if ra > 0.02:
            frame = Image.alpha_composite(frame, ray((w, h), edge, H * 0.003, (255, 255, 255), ra))
        save(frames_dir, i, frame)
    return _result(frames_dir, n, (w, h))


def badge(frames_dir, H, fps, dur, text, on, pop=True):
    """The state badge (top left): a small plate with a dot. Off: grey dot. On: the accent dot, with a short flash of
    the plate when it comes in (the switch)."""
    os.makedirs(frames_dir, exist_ok=True)
    f = font(H * 0.026, b"SemiBold SemiCondensed")
    tl, _ = text_layer(text.upper(), f, (255, 255, 255, 240), H * 0.004)
    pad_x, pad_y = int(H * 0.016), int(H * 0.009)
    dot = int(H * 0.011)
    m = int(H * 0.02)  # room for the glow
    pw = pad_x + dot + int(H * 0.012) + tl.size[0] + pad_x
    ph = tl.size[1] + 2 * pad_y
    w, h = pw + 2 * m, ph + 2 * m
    plate = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(plate)
    r = int(H * 0.006)
    d.rounded_rectangle([m, m, m + pw, m + ph], radius=r, fill=(0, 0, 0, 150), outline=(255, 255, 255, 70 if not on else 110), width=max(1, H // 720))
    cx, cy = m + pad_x + dot / 2, m + ph / 2
    col = ACCENT if on else (150, 150, 150)
    if on:
        g = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        ImageDraw.Draw(g).ellipse([cx - dot, cy - dot, cx + dot, cy + dot], fill=ACCENT + (255,))
        plate.alpha_composite(g.filter(ImageFilter.GaussianBlur(dot * 0.9)))
    ImageDraw.Draw(plate).ellipse([cx - dot / 2, cy - dot / 2, cx + dot / 2, cy + dot / 2], fill=col + (255,))
    plate.alpha_composite(tl, (int(m + pad_x + dot + H * 0.012), m + pad_y))
    flash = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    ImageDraw.Draw(flash).rounded_rectangle([m, m, m + pw, m + ph], radius=r, fill=(255, 255, 255, 255))
    n = int(round(dur * fps))
    for i in range(n):
        t = i / fps
        a = ease_out(ramp(t, 0, 0.22)) * (1 - ramp(t, dur - 0.2, 0.2))
        frame = with_alpha(plate, a)
        if on and pop:
            fl = max(0.0, 1 - ramp(t, 0, 0.28)) ** 2
            if fl > 0.01:
                frame = Image.alpha_composite(frame, with_alpha(flash, 0.75 * fl))
        if a < 1 and t < 0.3:  # slide in from the left
            sh = Image.new("RGBA", (w, h), (0, 0, 0, 0))
            sh.alpha_composite(frame, (-int((1 - a) * H * 0.012), 0))
            frame = sh
        save(frames_dir, i, frame)
    return _result(frames_dir, n, (w, h))


def tag(frames_dir, H, fps, dur, text):
    """The level's name (bottom right): a hairline draws in from the right, the name fades in above it."""
    os.makedirs(frames_dir, exist_ok=True)
    f = font(H * 0.024, b"SemiLight SemiCondensed")
    tl, _ = text_layer(text.upper(), f, (255, 255, 255, 255), H * 0.005)
    pad = int(H * 0.012)
    w, h = tl.size[0] + 2 * pad, tl.size[1] + int(H * 0.012) + 2 * pad
    shade = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    shade.putalpha(Image.new("L", (w, h), 0))
    sh = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    sh.alpha_composite(tl, (pad, pad))
    shade.putalpha(sh.getchannel("A").filter(ImageFilter.GaussianBlur(H * 0.008)).point(lambda v: min(255, int(v * 1.4))))
    n = int(round(dur * fps))
    for i in range(n):
        t = i / fps
        out = 1 - ramp(t, dur - 0.3, 0.3)
        a = ease_out(ramp(t, 0.25, 0.5)) * out
        frame = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        frame.alpha_composite(with_alpha(shade, 0.5 * a))
        frame.alpha_composite(with_alpha(tl, 0.8 * a), (pad, pad))
        ln = ease_out(ramp(t, 0.1, 0.45)) * tl.size[0]
        if ln > 1:
            yy = pad + tl.size[1] + int(H * 0.006)
            ImageDraw.Draw(frame).line([(w - pad - ln, yy), (w - pad, yy)], fill=(255, 255, 255, int(150 * out)), width=max(1, H // 720))
        save(frames_dir, i, frame)
    return _result(frames_dir, n, (w, h))
