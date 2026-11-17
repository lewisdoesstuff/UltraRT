"""Motion graphics for scripts/trailer.py (docs/trailer.md, "Graphics"): title and end cards, level titles, feature
captions and state badges, drawn frame by frame with Pillow into PNG sequences that the assembly overlays.

The style is the game's own HUD, measured from its UI (docs/trailer.md has the numbers):
  - level titles as LevelNamePopup shows them: VCR OSD Mono, white with a hard dark drop shadow, centred near the top,
    the layer line ("PRELUDE /// FIRST") typed out at 100 letters a second, half a second's pause, the level's name
    typed under it in larger type, a hold, a one-second fade;
  - captions as HUD messages: a rounded black box at 59 % that snaps open (width first, then height: HudOpenEffect)
    low in the middle of the picture, its text typed in with a block cursor;
  - cards: the same typed lines on black.

The font is the game's (scripts/trailer-font.py extracts it to bench/results/trailer/fonts, outside the repository);
TRAILER_FONT overrides the path, and without either a system monospace font stands in.

Every function writes frames into a folder and returns { "pattern", "frames", "w", "h" } (frame numbers from 0).
Sizes are fractions of the picture height, so the same call serves the 720p draft and the 1440p master.
"""
import os
import shutil
import sys

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FONTS = [os.environ.get("TRAILER_FONT", ""), os.path.join(ROOT, "bench", "results", "trailer", "fonts", "VCR_OSD_MONO.ttf"),
         "C:/Windows/Fonts/consolab.ttf", "C:/Windows/Fonts/consola.ttf"]
ACCENT = (255, 78, 40)
WHITE = (255, 255, 255)
BOX = (0, 0, 0, 150)       # the HUD message box: black at 0.588
LAYER_PX = 26.0 / 720.0    # LevelNamePopup's LayerText and NameText sizes on the game's 720-line canvas
NAME_PX = 46.0 / 720.0
TITLE_CPS = 100.0          # LevelNamePopup: a letter every 0.01 s
_font_path = None


def font(px):
    global _font_path
    if _font_path is None:
        for k, p in enumerate(FONTS):
            if p and os.path.exists(p):
                _font_path = p
                if k > 1:
                    print("trailer_gfx: the game's font is missing (scripts/trailer-font.py); using " + p, file=sys.stderr)
                break
        else:
            raise SystemExit("trailer_gfx: no font found")
    return ImageFont.truetype(_font_path, max(4, int(round(px))))


def ramp(t, t0, d):
    return min(1.0, max(0.0, (t - t0) / max(1e-6, d)))


def typed(text, t, t0, cps):
    """The part of text typed by time t (typing starts at t0)."""
    if t < t0:
        return ""
    return text[:min(len(text), int((t - t0) * cps) + 1)]


def type_time(text, cps):
    return len(text) / cps


def put(img, xy, text, f, fill, shadow=0, anchor="mm", shadow_alpha=150):
    """Text with the game's underlay: a hard dark copy offset down and right."""
    if not text:
        return
    d = ImageDraw.Draw(img)
    if shadow:
        d.text((xy[0] + shadow, xy[1] + shadow), text, font=f, fill=(0, 0, 0, shadow_alpha), anchor=anchor)
    d.text(xy, text, font=f, fill=fill, anchor=anchor)


def with_alpha(img, a):
    if a >= 0.999:
        return img
    out = img.copy()
    out.putalpha(img.getchannel("A").point(lambda v: int(v * max(0.0, a))))
    return out


def open_effect(t, speed=30.0):
    """HudOpenEffect: from 5 % in both axes, the width runs to full, then the height (rate = (distance + 0.1) * speed)."""
    sx = sy = 0.05
    dt = 1.0 / 480.0
    for _ in range(int(max(0.0, t) / dt)):
        if sx < 1.0:
            sx = min(1.0, sx + dt * ((1.0 - sx) + 0.1) * speed)
        elif sy < 1.0:
            sy = min(1.0, sy + dt * ((1.0 - sy) + 0.1) * speed)
        else:
            break
    return sx, sy


OPEN_TIME = 0.16  # both axes of open_effect at speed 30


class Seq:
    """A PNG sequence; a frame equal to the one before it (same key) is a file copy."""

    def __init__(self, frames_dir, size):
        os.makedirs(frames_dir, exist_ok=True)
        self.dir, self.size, self.n, self.key, self.last = frames_dir, size, 0, None, None

    def add(self, key, draw):
        path = os.path.join(self.dir, "f_%05d.png" % self.n)
        if key is not None and key == self.key and self.last:
            shutil.copyfile(self.last, path)
        else:
            draw().save(path, compress_level=1)
        self.key, self.last = key, path
        self.n += 1

    def result(self, **extra):
        r = {"pattern": os.path.join(self.dir, "f_%05d.png"), "frames": self.n, "w": self.size[0], "h": self.size[1]}
        r.update(extra)
        return r


# ---------------------------------------------------------------- cards

def card(frames_dir, W, H, fps, dur, title, accent_from=5, sub=None, lines=(), small=None, scale=1.0, y=0.43, out_fade=0.45, beats=None):
    """A full-frame card on black, laid out as a level title: the sub line typed first (the layer line's place), then
    the title under it in large type (the letters from accent_from on in the accent colour: ULTRA + RT), then further
    lines typed one after another, and small print at the bottom. beats: when the sub line, the title and the first
    further line start (seconds); the defaults follow the game (title half a second after the sub line)."""
    title_f = font(H * 0.17 * scale)
    sub_f = font(H * 0.17 * scale * LAYER_PX / NAME_PX * 0.62)
    line_f = font(H * 0.03)
    small_f = font(H * 0.0215)
    sub = sub or ""
    b = list(beats or [])
    t_sub = b[0] if len(b) > 0 else 0.3
    t_title = b[1] if len(b) > 1 else t_sub + type_time(sub, TITLE_CPS) + 0.5
    t_lines = b[2] if len(b) > 2 else t_title + 0.9
    title_cps = 22.0  # a short word in type this large: slow enough to see it typed
    cx, cy = W / 2, H * y
    th = H * 0.17 * scale
    seq = Seq(frames_dir, (W, H))
    # the title is monospaced: its accent part is drawn in place, so the split never shifts the letters
    adv = title_f.getlength("M")
    for i in range(int(round(dur * fps))):
        t = i / fps
        s_now = typed(sub, t, t_sub, TITLE_CPS)
        n_title = len(typed(title, t, t_title, title_cps))
        l_now, tl = [], t_lines
        for ln in lines:
            l_now.append(typed(ln, t, tl, TITLE_CPS))
            tl += type_time(ln, TITLE_CPS) + 0.35
        a_small = ramp(t, tl, 0.5) if small else 0.0
        fade = 1 - ramp(t, dur - out_fade, out_fade) if out_fade > 0 else 1.0

        def draw():
            frame = Image.new("RGBA", (W, H), (0, 0, 0, 255))
            put(frame, (cx, cy - th * 0.62), s_now, sub_f, WHITE + (255,))
            x0 = cx - adv * n_title / 2  # centred as typed, like the game's titles
            for k in range(n_title):
                put(frame, (x0 + adv * (k + 0.5), cy), title[k], title_f, (ACCENT if k >= accent_from else WHITE) + (255,))
            yy = cy + th * 0.62
            for ln in l_now:
                put(frame, (cx, yy), ln, line_f, (225, 225, 225, 255))
                yy += H * 0.05
            if a_small > 0:
                layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
                for k, ln in enumerate(small):
                    put(layer, (cx, H * (0.85 + 0.036 * k)), ln, small_f, (255, 255, 255, 255))
                frame.alpha_composite(with_alpha(layer, 0.6 * a_small))
            out = frame.convert("RGB")
            if fade < 1:
                out = Image.blend(Image.new("RGB", (W, H), (0, 0, 0)), out, max(0.0, fade))
            return out

        seq.add((s_now, n_title, tuple(l_now), round(a_small, 3), round(fade, 3)), draw)
    return seq.result()


# ---------------------------------------------------------------- overlays

def level_title(frames_dir, W, H, fps, dur, layer, name, scale=0.85, hold=3.0):
    """The level's title as the game shows it on entering a level: a full-width strip for the top of the picture
    (place it at y = result["y"]). Layer line typed, half a second, name typed, hold, one-second fade."""
    layer_f, name_f = font(H * LAYER_PX * scale), font(H * NAME_PX * scale)
    # the game's canvas: layer line 245 and name 210 units above the middle of 720
    y_layer, y_name = H * (0.5 - 245.0 / 720.0), H * (0.5 - 210.0 / 720.0)
    top = int(y_layer - H * 0.03)
    h = int(y_name + H * NAME_PX * scale - top)
    shadow = max(1, int(round(H * scale / 420.0)))
    t_layer = 0.0
    t_name = t_layer + type_time(layer, TITLE_CPS) + 0.5
    t_fade = min(t_name + type_time(name, TITLE_CPS) + hold, dur - 1.0)
    seq = Seq(frames_dir, (W, h))
    for i in range(int(round(dur * fps))):
        t = i / fps
        a_now, b_now = typed(layer, t, t_layer, TITLE_CPS), typed(name, t, t_name, TITLE_CPS)
        a = 1 - ramp(t, t_fade, 1.0)

        def draw():
            frame = Image.new("RGBA", (W, h), (0, 0, 0, 0))
            if a > 0:
                put(frame, (W / 2, y_layer - top), a_now, layer_f, WHITE + (255,), shadow)
                put(frame, (W / 2, y_name - top), b_now, name_f, WHITE + (255,), shadow)
            return with_alpha(frame, a)

        seq.add((a_now, b_now, round(a, 3)), draw)
    return seq.result(y=top)


def _box(size, sx, sy, radius, fill=BOX, flash=0.0):
    w, h = size
    img = Image.new("RGBA", size, (0, 0, 0, 0))
    bw, bh = max(2.0, w * sx), max(2.0, h * sy)
    col = fill if flash <= 0 else tuple(int(fill[k] + (255 - fill[k]) * flash) for k in range(3)) + (int(fill[3] + (235 - fill[3]) * flash),)
    ImageDraw.Draw(img).rounded_rectangle([(w - bw) / 2, (h - bh) / 2, (w + bw) / 2 - 1, (h + bh) / 2 - 1], radius=min(radius, bw / 2, bh / 2), fill=col)
    return img


def lower_third(frames_dir, H, fps, dur, text, sub=None):
    """A feature caption as a HUD message: the box snaps open, the heading is typed in with a block cursor, the sub line
    after it; the box snaps shut at the end. Place it centred, low in the picture."""
    head_f, sub_f = font(H * 0.04), font(H * 0.026)
    head = text.upper()
    cps = 70.0
    pad_x, pad_y, gap = int(H * 0.03), int(H * 0.02), int(H * 0.012)
    hw = head_f.getlength(head + "M")
    sw = sub_f.getlength(sub) if sub else 0
    hh, sh = int(H * 0.04), int(H * 0.026)
    w = int(max(hw, sw) + 2 * pad_x)
    h = hh + (sh + gap if sub else 0) + 2 * pad_y
    radius = H * 0.012
    t_head = OPEN_TIME * 0.6
    t_sub = t_head + type_time(head, cps) + 0.25
    t_shut = dur - 0.12
    adv = head_f.getlength("M")
    seq = Seq(frames_dir, (w, h))
    for i in range(int(round(dur * fps))):
        t = i / fps
        sx, sy = open_effect(t)
        if t >= t_shut:  # shut: the height first, then gone
            sy = max(0.05, 1 - ramp(t, t_shut, 0.1))
        h_now = typed(head, t, t_head, cps)
        s_now = typed(sub, t, t_sub, cps * 1.6) if sub else ""
        typing = h_now and (len(h_now) < len(head) or (sub and len(s_now) < len(sub) and t < t_sub))
        show = sy >= 0.999

        def draw():
            frame = _box((w, h), sx, sy, radius)
            if show:
                y0 = pad_y + hh / 2
                x0 = (w - head_f.getlength(head)) / 2
                put(frame, (x0, y0), h_now, head_f, WHITE + (255,), anchor="lm")
                if typing and len(h_now) < len(head):  # the writing cursor: a block after the last letter
                    xc = x0 + head_f.getlength(h_now) + adv * 0.08
                    ImageDraw.Draw(frame).rectangle([xc, y0 - hh * 0.36, xc + adv * 0.8, y0 + hh * 0.36], fill=WHITE + (255,))
                if s_now:
                    put(frame, ((w - sw) / 2, pad_y + hh + gap + sh / 2), s_now, sub_f, (205, 205, 205, 255), anchor="lm")
            return frame

        seq.add((round(sx, 3), round(sy, 3), h_now, s_now, bool(typing), show), draw)
    return seq.result()


def badge(frames_dir, H, fps, dur, text, on, pop=True):
    """The state badge (top left): a small HUD box. Off: grey type and a hollow marker. On: white type and the accent
    marker; with pop the box snaps open again and flashes white as the picture under it changes."""
    f = font(H * 0.028)
    label = text.upper()
    tw = f.getlength(label)
    th = int(H * 0.028)
    pad_x, pad_y = int(H * 0.016), int(H * 0.011)
    mark = int(H * 0.014)
    gap = int(H * 0.011)
    w = int(pad_x + mark + gap + tw + pad_x)
    h = th + 2 * pad_y
    radius = H * 0.008
    seq = Seq(frames_dir, (w, h))
    for i in range(int(round(dur * fps))):
        t = i / fps
        sx, sy = open_effect(t) if (pop or not on) else (1.0, 1.0)
        fl = max(0.0, 1 - ramp(t, 0.02, 0.3)) ** 2 if on and pop else 0.0
        out = 1 - ramp(t, dur - 0.15, 0.15)
        show = sy >= 0.999

        def draw():
            frame = _box((w, h), sx, sy, radius, flash=fl * 0.9)
            if show:
                d = ImageDraw.Draw(frame)
                x0, y0 = pad_x, (h - mark) / 2
                if on:
                    d.rectangle([x0, y0, x0 + mark, y0 + mark], fill=ACCENT + (255,))
                else:
                    d.rectangle([x0, y0, x0 + mark, y0 + mark], outline=(160, 160, 160, 255), width=max(1, H // 480))
                put(frame, (pad_x + mark + gap, h / 2), label, f, (WHITE if on else (170, 170, 170)) + (255,), anchor="lm")
            return with_alpha(frame, out)

        seq.add((round(sx, 3), round(sy, 3), round(fl, 3), round(out, 3)), draw)
    return seq.result()


def tag(frames_dir, H, fps, dur, text):
    """A one-line level name (bottom right), for edit lists that give "Tag" as a string."""
    f = font(H * 0.026)
    label = text.upper()
    shadow = max(1, H // 480)
    w, h = int(f.getlength(label)) + 4 * shadow, int(H * 0.04)
    seq = Seq(frames_dir, (w, h))
    for i in range(int(round(dur * fps))):
        t = i / fps
        now = typed(label, t, 0.2, TITLE_CPS)
        a = 1 - ramp(t, dur - 1.0, 1.0)

        def draw():
            frame = Image.new("RGBA", (w, h), (0, 0, 0, 0))
            put(frame, (0, h / 2), now, f, WHITE + (255,), shadow, anchor="lm")
            return with_alpha(frame, a)

        seq.add((now, round(a, 3)), draw)
    return seq.result()
