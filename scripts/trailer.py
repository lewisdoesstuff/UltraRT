#!/usr/bin/env python3
"""Assemble a trailer from rendered shots with ffmpeg, driven by an edit-list JSON (docs/trailer.md).

    python scripts/trailer.py bench/trailer/trailer-test.json [--root DIR] [--out DIR] [--only N[,N]] [--keep]

Every clip of the edit list is first rendered to its own MP4 (<out>/clips/NN_<name>.mp4: scaled, captioned, wiped),
then the clips are joined with their transitions and the music is laid under them (<out>/<Name>.mp4).
Sources are the cinematic renderer's output folders (frame_00000.png ..., docs/cinematics.md) or video files; a
source may contain wildcards, and the last match by name (= the newest bench run) is used.

Needs only Python 3.8+ and ffmpeg (PATH, or WinGet's Links folder). Nothing it writes belongs in git: outputs go
under bench/results by default.
"""
import argparse
import glob
import json
import os
import re
import shutil
import subprocess
import sys


def find_ffmpeg(name="ffmpeg"):
    exe = shutil.which(name)
    if exe:
        return exe
    for d in (os.path.join(os.environ.get("LOCALAPPDATA", ""), "Microsoft", "WinGet", "Links"), r"C:\ffmpeg\bin", r"C:\Program Files\ffmpeg\bin"):
        p = os.path.join(d, name + ".exe")
        if os.path.isfile(p):
            return p
    sys.exit(f"{name} not found (PATH or WinGet Links)")


FFMPEG = None


def run(args, cwd, log, timeout=None):
    """Run ffmpeg quietly; on failure show the tail of its output. timeout: seconds before an attempt counts as hung
    (ffmpeg 9.0.2 stalled for good, at 0 % CPU, in clip encodes: docs/trailer.md, v5); it is killed and run once more."""
    cmd = [FFMPEG, "-hide_banner", "-loglevel", "warning", "-y"] + args
    with open(log, "a", encoding="utf-8") as f:
        f.write("\n$ ffmpeg " + " ".join(f'"{a}"' if (" " in a or "'" in a) else a for a in cmd[1:]) + "\n")
    for attempt in range(2 if timeout else 1):
        try:
            p = subprocess.run(cmd, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8",
                               errors="replace", timeout=timeout)
            break
        except subprocess.TimeoutExpired:
            with open(log, "a", encoding="utf-8") as f:
                f.write(f"(no result after {timeout} s: killed{', trying again' if attempt == 0 else ''})\n")
            print(f"ffmpeg hung ({timeout} s){': trying again' if attempt == 0 else ''}", flush=True)
            if attempt == 1:
                sys.exit(f"ffmpeg hung twice; see {log}")
    with open(log, "a", encoding="utf-8") as f:
        f.write(p.stdout or "")
    if p.returncode != 0:
        sys.exit(f"ffmpeg failed ({p.returncode}); see {log}\n" + "\n".join((p.stdout or "").splitlines()[-15:]))


def probe_duration(path):
    """A video file's duration (seconds) from ffprobe next to ffmpeg, or None."""
    probe = os.path.join(os.path.dirname(FFMPEG), "ffprobe" + os.path.splitext(FFMPEG)[1])
    if not os.path.isfile(probe):
        probe = shutil.which("ffprobe")
    if not probe:
        return None
    p = subprocess.run([probe, "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path],
                       stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
    try:
        return float(p.stdout.strip())
    except ValueError:
        return None


def slug(s):
    return re.sub(r"[^A-Za-z0-9]+", "-", s or "clip").strip("-").lower() or "clip"


def num(v):
    return f"{v:.5f}".rstrip("0").rstrip(".")


class Builder:
    def __init__(self, edit, edit_path, root, out):
        self.e = edit
        self.dir = os.path.dirname(os.path.abspath(edit_path))
        self.root = os.path.abspath(root or os.path.join(self.dir, edit.get("Root", ".")))
        self.out = os.path.abspath(out or os.path.join(self.root, "trailer", slug(edit.get("Name", "trailer"))))
        self.work = os.path.join(self.out, "work")
        self.clips = os.path.join(self.out, "clips")
        for d in (self.out, self.work, self.clips):
            os.makedirs(d, exist_ok=True)
        self.log = os.path.join(self.out, "ffmpeg.log")
        open(self.log, "w").close()
        self.W, self.H, self.fps = int(edit.get("Width", 1280)), int(edit.get("Height", 720)), float(edit.get("Fps", 60))
        self.crf = int(edit.get("Crf", 18))
        self.texts = 0
        # Fonts are copied next to the text files: relative names need no drive-letter escaping in a filter graph.
        self.fonts = {}
        self.font = self.use_font(edit.get("Font", "C:/Windows/Fonts/bahnschrift.ttf"))
        self.font_title = self.use_font(edit.get("TitleFont", edit.get("Font", "C:/Windows/Fonts/bahnschrift.ttf")))
        # "Graphics": "motion": cards, captions, badges and tags are animated overlays (scripts/trailer_gfx.py, needs
        # Pillow) instead of drawtext.
        self.motion = str(edit.get("Graphics", "")).lower() == "motion"
        self.gfx_n = 0
        if self.motion:
            sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
            import trailer_gfx
            self.gfx = trailer_gfx

    # ------------------------------------------------------------ helpers

    def use_font(self, path):
        if path not in self.fonts:
            src = path if os.path.isabs(path) else os.path.join(self.dir, path)
            if not os.path.isfile(src):
                sys.exit(f"font not found: {src}")
            name = f"font{len(self.fonts)}{os.path.splitext(src)[1]}"
            shutil.copyfile(src, os.path.join(self.work, name))
            self.fonts[path] = name
        return self.fonts[path]

    def resolve(self, source):
        """A frames folder or a video file; wildcards pick the last match by name."""
        p = source if os.path.isabs(source) else os.path.join(self.root, source)
        if any(c in p for c in "*?["):
            hits = sorted(glob.glob(p))
            if not hits:
                sys.exit(f"no match for source '{source}' under {self.root}")
            p = hits[-1]
        if not os.path.exists(p):
            sys.exit(f"source not found: {p}")
        return p

    def source_args(self, source, t_in, dur, speed=1.0):
        """Input options for a source; returns (args, description). Frame folders take their fps from render.json."""
        p = self.resolve(source)
        need = dur * speed
        if os.path.isdir(p):
            sfps = self.fps
            rj = os.path.join(p, "render.json")
            if os.path.isfile(rj):
                try:
                    sfps = float(json.load(open(rj, encoding="utf-8-sig")).get("Fps") or sfps)
                except (ValueError, OSError):
                    pass
            frames = sorted(glob.glob(os.path.join(p, "frame_*.png")))
            if not frames:
                sys.exit(f"no frame_*.png in {p}")
            # Frame numbers are the path's: frame N is path time N / fps. A folder may start late or end early (paired
            # renders write only the frames their window needs).
            lo = int(re.search(r"frame_(\d+)\.png$", frames[0]).group(1))
            hi = int(re.search(r"frame_(\d+)\.png$", frames[-1]).group(1))
            first = int(round(t_in * sfps))
            if first < lo:
                print(f"  warning: {os.path.basename(p)} starts at {lo / sfps:.2f}s, the clip wants {t_in:.2f}s (starting late)")
                first = lo
            have = (hi - first + 1) / sfps
            if have + 0.5 / sfps < need:
                print(f"  warning: {os.path.basename(p)} has {have:.2f}s from {t_in:.2f}s, the clip wants {need:.2f}s (last frame held)")
            return ["-framerate", num(sfps), "-start_number", str(first), "-i", os.path.join(p, "frame_%05d.png")], p
        return ["-ss", num(t_in), "-i", p], p

    def text_file(self, text):
        self.texts += 1
        name = f"text{self.texts:03d}.txt"
        with open(os.path.join(self.work, name), "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        return name

    @staticmethod
    def alpha(t0, t1, fade):
        """Text opacity: fades in from t0 and out to t1."""
        fade = max(0.01, min(fade, (t1 - t0) / 2))
        return (f"if(lt(t,{num(t0)}),0,if(lt(t,{num(t0 + fade)}),(t-{num(t0)})/{num(fade)},"
                f"if(lt(t,{num(t1 - fade)}),1,if(lt(t,{num(t1)}),({num(t1)}-t)/{num(fade)},0))))")

    def drawtext(self, text, size, x, y, t0, t1, fade=0.35, color="white", font=None, shadow=True, box=False):
        """size: fraction of the height. x / y: drawtext expressions (text_w, text_h, w, h available)."""
        opts = [f"fontfile={font or self.font}", f"textfile={self.text_file(text)}", f"fontcolor={color}",
                f"fontsize={int(round(self.H * size))}", f"x={x}", f"y={y}", f"alpha='{self.alpha(t0, t1, fade)}'"]
        if shadow:
            opts += ["shadowcolor=black@0.75", f"shadowx={max(1, self.H // 360)}", f"shadowy={max(1, self.H // 360)}"]
        if box:
            opts += ["box=1", "boxcolor=black@0.45", f"boxborderw={int(self.H * size * 0.35)}"]
        return "drawtext=" + ":".join(opts)

    # ------------------------------------------------------------ motion graphics

    def gfx_dir(self):
        # Emptied first: the numbers restart with every run, and a work folder kept from an earlier run (a failed or
        # killed one) held a longer sequence under the same number, whose extra frames ffmpeg then played on (v5's
        # 2-4 tunnel clip showed 5-2's level title after its own).
        self.gfx_n += 1
        d = os.path.join(self.work, f"gfx{self.gfx_n:03d}")
        shutil.rmtree(d, ignore_errors=True)
        return d

    def motion_overlays(self, c, dur, first_input, switch_at=None, labels=None, beauty=False):
        """The animated overlays of a clip: (ffmpeg input args, [(input index, x, y, start time)])."""
        g, H, W, fps = self.gfx, self.H, self.W, self.fps
        args, items = [], []

        def add(r, x, y, t0):
            args.extend(["-framerate", num(fps), "-i", r["pattern"]])
            items.append((first_input + len(items), int(x), int(y), t0))

        # The badge is open from a clip's first frame to its last (a cut swaps it in place); only a change of state
        # mid-clip snaps it open again and flashes it (pop).
        bx, by = W * g.BADGE_X, H * g.BADGE_Y
        if labels and switch_at is not None:
            early = switch_at <= 0.3 or not labels[0]  # no "before" badge: the "after" one covers the whole clip
            if not early:
                add(g.badge(self.gfx_dir(), H, fps, switch_at, labels[0], False), bx, by, 0.0)
            if dur - switch_at > 0.3 and len(labels) > 1 and labels[1]:
                if early:
                    add(g.badge(self.gfx_dir(), H, fps, dur, labels[1], True), bx, by, 0.0)
                else:
                    add(g.badge(self.gfx_dir(), H, fps, dur - switch_at, labels[1], True, pop=True), bx, by, switch_at)
        elif beauty and labels and len(labels) > 1 and labels[1]:
            add(g.badge(self.gfx_dir(), H, fps, dur, labels[1], True), bx, by, 0.0)
        cap = c.get("Caption")
        if cap:
            cs = cap if isinstance(cap, dict) else {"Text": cap}
            t0 = float(cs.get("At", 0.3))
            length = min(float(cs.get("For", 4.5)), dur - t0 - 0.1)
            if length > 1.0:
                r = g.lower_third(self.gfx_dir(), H, fps, length, cs["Text"], cs.get("Sub", c.get("Sub")))
                add(r, (W - r["w"]) / 2, H * 0.875 - r["h"], t0)  # a HUD message: centred, low
        tag = c.get("Tag")
        if isinstance(tag, list) and len(tag) > 1:
            # [layer line, name]: the level's title as the game shows it, top centre
            t0 = float(c.get("TagAt", 0.3))
            r = g.level_title(self.gfx_dir(), W, H, fps, dur - t0 - 0.1, tag[0], tag[1])
            add(r, 0, r["y"], t0)
        elif tag:
            r = g.tag(self.gfx_dir(), H, fps, dur - 0.45, tag if isinstance(tag, str) else tag[0])
            add(r, W * 0.972 - r["w"], H * 0.955 - r["h"], 0.25)
        return args, items

    def overlay_chain(self, label, items, tail, dur):
        """[label] with every overlay on it, then the tail filters, to [v]. Each overlay is padded with transparent
        frames to cover the whole clip: an overlay that started late or ended early (a caption's last frame) made
        ffmpeg 9.0.2's overlay drop main frames there, a visible jump of about 6 frames (v5's P-2 street and terminal
        clips, at their captions' ends)."""
        parts, cur = [], label
        clear = "0x00000000"
        for k, (idx, x, y, t0) in enumerate(items):
            parts.append(f"[{idx}:v]format=rgba,setpts=PTS-STARTPTS,fps={num(self.fps)},"
                         f"tpad=start_mode=add:start_duration={num(t0)}:stop_mode=add:stop_duration={num(dur)}:color={clear},"
                         f"trim=duration={num(dur)},setpts=PTS-STARTPTS[o{k}]")
            parts.append(f"[{cur}][o{k}]overlay=x={x}:y={y}:eof_action=pass:format=auto[m{k}]")
            cur = f"m{k}"
        parts.append(f"[{cur}]" + ",".join(["format=yuv420p"] + list(tail)) + "[v]")
        return ";".join(parts)

    def wipe_fraction(self, keys, slant, t):
        """The divider position of wipe_position at time t, in Python."""
        margin = abs(slant) * self.H / (2.0 * self.W) + 6.0 / self.W
        pts = [(float(a), -margin + float(b) * (1 + 2 * margin)) for a, b in keys]
        if t <= pts[0][0]:
            return pts[0][1]
        for (t0, p0), (t1, p1) in zip(pts, pts[1:]):
            if t < t1:
                u = (t - t0) / max(1e-3, t1 - t0)
                return p0 + (p1 - p0) * u * u * (3 - 2 * u)
        return pts[-1][1]

    def fit(self):
        return f"scale={self.W}:{self.H}:force_original_aspect_ratio=increase:flags=lanczos,crop={self.W}:{self.H},setsar=1"

    def caption_filters(self, c, dur):
        """Small captions: "Caption" (and "Sub" under it) bottom left, "Tag" bottom right (the level)."""
        f = []
        cap = c.get("Caption")
        if cap:
            cs = cap if isinstance(cap, dict) else {"Text": cap}
            t0 = float(cs.get("At", 0.3))
            t1 = min(dur, t0 + float(cs.get("For", dur - t0 - 0.2)))
            x, base = f"{int(self.W * 0.045)}", self.H * 0.865
            sub = cs.get("Sub", c.get("Sub"))
            if sub:
                base -= self.H * 0.035
                f.append(self.drawtext(sub, 0.03, x, f"{int(base + self.H * 0.062)}", t0 + 0.15, t1, color="white@0.8"))
            f.append(self.drawtext(cs["Text"].upper() if cs.get("Upper", True) else cs["Text"], 0.05, x, f"{int(base)}", t0, t1))
        tag = c.get("Tag")
        if tag:
            f.append(self.drawtext(tag, 0.026, f"w-text_w-{int(self.W * 0.045)}", f"{int(self.H * 0.905)}", 0.2, dur - 0.1, color="white@0.7"))
        return f

    def fades(self, c, dur):
        f = []
        if c.get("FadeIn"):
            f.append(f"fade=t=in:st=0:d={num(float(c['FadeIn']))}")
        if c.get("FadeOut"):
            d = float(c["FadeOut"])
            f.append(f"fade=t=out:st={num(dur - d)}:d={num(d)}")
        return f

    def encode(self, inputs, graph, dur, out):
        run(inputs + ["-filter_complex", graph, "-map", "[v]", "-t", num(dur), "-r", num(self.fps), "-an",
                      "-c:v", "libx264", "-preset", "fast", "-crf", str(int(self.e.get("ClipCrf", 12))), "-pix_fmt", "yuv420p", out], self.work, self.log,
            timeout=max(600, 60 * dur))  # a clip encodes in 1-3 minutes at 1440p

    # ------------------------------------------------------------ clip types

    def clip_duration(self, c):
        if "Duration" in c:
            return float(c["Duration"])
        return (float(c["Out"]) - float(c.get("In", 0))) / float(c.get("Speed", 1))

    def base_chain(self, c, dur, label_in, extra=()):
        speed = float(c.get("Speed", 1))
        chain = [self.fit()]
        if speed != 1:
            chain.append(f"setpts=PTS/{num(speed)}")
        chain += [f"fps={num(self.fps)}", f"tpad=stop_mode=clone:stop_duration={num(dur)}", f"trim=duration={num(dur)}", "setpts=PTS-STARTPTS"]
        chain += list(extra)
        return f"[{label_in}]" + ",".join(chain)

    def build_shot(self, c, out):
        dur = self.clip_duration(c)
        args, src = self.source_args(c["Source"], float(c.get("In", 0)), dur, float(c.get("Speed", 1)))
        if self.motion:
            ov_args, items = self.motion_overlays(c, dur, 1, labels=c.get("Labels", self.e.get("Labels")), beauty=True)
            graph = self.base_chain(c, dur, "0:v") + "[m];" + self.overlay_chain("m", items, self.fades(c, dur), dur)
            args = args + ov_args
        else:
            graph = self.base_chain(c, dur, "0:v", self.caption_filters(c, dur) + self.fades(c, dur)) + ",format=yuv420p[v]"
        self.encode(args, graph, dur, out)
        return dur, src

    def wipe_position(self, keys, slant):
        """Divider position (fraction of the width, 0 = all "before", 1 = all "after") as an ffmpeg expression of T:
        smoothstep between [time, position] keys; 0 and 1 put the (slanted) divider just off screen."""
        margin = abs(slant) * self.H / (2.0 * self.W) + 6.0 / self.W
        pts = [(float(t), -margin + float(p) * (1 + 2 * margin)) for t, p in keys]
        expr = num(pts[-1][1])
        for (t0, p0), (t1, p1) in reversed(list(zip(pts, pts[1:]))):
            u = f"((T-{num(t0)})/{num(max(1e-3, t1 - t0))})"
            seg = f"({num(p0)}+({num(p1 - p0)})*{u}*{u}*(3-2*{u}))"
            expr = f"if(lt(T,{num(t1)}),{seg},{expr})"
        return f"if(lt(T,{num(pts[0][0])}),{num(pts[0][1])},{expr})"

    def build_wipe(self, c, out):
        """Before / after: two renders of the same camera path, an animated (slanted) divider between them. Left of the
        divider is "After" (right of it with "Flip": the wipe then comes in from the right). Each side's label is burned
        into its own picture, so the wipe reveals and hides it. Keys that jump (two keys a millisecond apart) make a
        hard switch; keys that stay between 0 and 1 a split screen."""
        dur = self.clip_duration(c)
        t_in, speed = float(c.get("In", 0)), float(c.get("Speed", 1))
        w = c.get("Wipe", {})
        keys = w.get("Keys", [[0, 0], [dur * 0.3, 0], [dur * 0.7, 1], [dur, 1]])
        slant = float(w.get("Slant", 0.18))
        flip = bool(w.get("Flip", False))
        if flip:
            keys = [[t, 1 - float(p)] for t, p in keys]
        line = float(w.get("Line", 2.0)) * self.H / 720.0
        # "Border": a dark edge either side of the white line (720p pixels; the picture under it at "Shade" of its
        # brightness), so the line stays visible over white and yellow walls (4-4's window hall, the museum).
        border = float(w.get("Border", 1.5)) * self.H / 720.0
        shade = float(w.get("Shade", 0.3))
        labels = c.get("Labels", ["RT OFF", "PATH TRACED"])
        y = f"{int(self.H * 0.06)}"
        left_x, right_x = f"{int(self.W * 0.04)}", f"w-text_w-{int(self.W * 0.04)}"
        before_label = [self.drawtext(labels[0], 0.036, left_x if flip else right_x, y, 0.0, dur + 1, fade=0.3, box=True)] if labels and labels[0] else []
        after_label = [self.drawtext(labels[1], 0.036, right_x if flip else left_x, y, 0.0, dur + 1, fade=0.3, box=True)] if labels and len(labels) > 1 and labels[1] else []
        if self.motion:
            before_label, after_label = [], []  # the state badge is an overlay instead
        # The clip in three parts: "before" alone until the divider enters (ta), the wipe, "after" alone once it has
        # left (tb). Only the middle needs both pictures (and the slow per-pixel blend); a hard switch has no middle.
        okeys = [[float(t), float(q)] for t, q in (w.get("Keys") or keys)]
        fr = 1.0 / self.fps
        ta = max([t for t, q in okeys if q <= 0 and all(q2 <= 0 for t2, q2 in okeys if t2 <= t)] or [0.0])
        tb = min([t for t, q in okeys if q >= 1 and all(q2 >= 1 for t2, q2 in okeys if t2 >= t)] or [dur])
        ta, tb = max(0.0, round(ta * self.fps) * fr), min(dur, round(tb * self.fps) * fr)
        if speed != 1 or c.get("AfterOffset") or c.get("Whole"):
            ta, tb = 0.0, dur
        if tb - ta < 1.5 * fr:
            ta = tb
        a_args, a_src = self.source_args(c["After"], t_in + ta + float(c.get("AfterOffset", 0)), dur - ta, speed)
        b_args, b_src = self.source_args(c["Before"], t_in, max(tb, fr), speed)

        def chain(label_in, length, extra):
            ch = [self.fit(), f"fps={num(self.fps)}", f"tpad=stop_mode=clone:stop_duration={num(length)}", f"trim=duration={num(length)}", "setpts=PTS-STARTPTS"]
            return f"[{label_in}]" + ",".join(ch + list(extra))

        pos = self.wipe_position(keys, slant).replace("T", f"(T+{num(ta)})")
        # D < 0: left of the divider. gbrp: every plane is full size, so one expression draws the white line in all three.
        d = f"(X-W*({pos})-(Y-H/2)*{num(slant)})"
        # Most pixels are far from the divider: the first test sends them to their side at the cost of the old
        # expression (D twice); only the line and its border evaluate D more often.
        side = f"if(lt({d},0),{'B' if flip else 'A'},{'A' if flip else 'B'})"
        if border > 0 and shade < 1:
            blend = (f"blend=all_expr='if(gte(abs({d}),{num(line + border)}),{side},"
                     f"if(lt(abs({d}),{num(line)}),255,{side}*{num(shade)}))'")
        else:
            blend = f"blend=all_expr='if(lt(abs({d}),{num(line)}),255,{side})'"
        parts, segs = [], []
        # Each picture is split only into the parts that use it (a hard switch uses each picture once): no idle
        # nullsink branches in the graph.
        use_a = [n for n, used in (("a0", tb > ta), ("a1", dur > tb)) if used]
        use_b = [n for n, used in (("b0", ta > 0), ("b1", tb > ta)) if used]

        def fan(label_in, length, extra, outs):
            head = chain(label_in, length, extra) + ",format=gbrp"
            if len(outs) == 1:
                return head + f"[{outs[0]}]"
            if not outs:
                return head + ",nullsink"
            return head + f",split={len(outs)}" + "".join(f"[{o}]" for o in outs)
        parts.append(fan("0:v", dur - ta, after_label, use_a))
        parts.append(fan("1:v", max(tb, fr), before_label, use_b))
        if ta > 0:
            parts.append(f"[b0]trim=duration={num(ta)},setpts=PTS-STARTPTS[s0]")
            segs.append("[s0]")
        if tb > ta:
            parts.append(f"[a0]trim=duration={num(tb - ta)},setpts=PTS-STARTPTS[am]")
            parts.append(f"[b1]trim=start={num(ta)}:duration={num(tb - ta)},setpts=PTS-STARTPTS[bm]")
            parts.append(f"[am][bm]{blend}[s1]")
            segs.append("[s1]")
        if dur > tb:
            parts.append(f"[a1]trim=start={num(tb - ta)},setpts=PTS-STARTPTS[s2]")
            segs.append("[s2]")
        if self.motion:
            # The badge changes when the picture under it does: at the switch, or when the divider passes over it.
            bx, by = self.W * 0.06, self.H * 0.06
            switch = ta
            if tb > ta:
                thr = (bx - (by - self.H / 2) * slant) / self.W
                switch = tb
                for k in range(int((tb - ta) * self.fps) + 1):
                    t = ta + k * fr
                    q = self.wipe_fraction(keys, slant, t)
                    if (q < thr) if flip else (q > thr):
                        switch = t
                        break
            # Two passes: the picture (both renders, the divider) losslessly to a work file, then the overlays on it. In
            # one graph, ffmpeg 9.0.2 stalled for good (0 % CPU) when the overlays' inputs were read alongside two
            # picture inputs that concat only takes one after the other (v5's 7-3 gun clip, every time).
            base = os.path.join(self.work, os.path.splitext(os.path.basename(out))[0] + "_base.mkv")
            parts.append("".join(segs) + (f"concat=n={len(segs)}:v=1:a=0," if len(segs) > 1 else "") + "format=yuv420p[v]")
            run(a_args + b_args + ["-filter_complex", ";".join(parts), "-map", "[v]", "-t", num(dur), "-r", num(self.fps), "-an",
                                   "-c:v", "ffv1", base], self.work, self.log, timeout=max(600, 60 * dur))
            ov_args, items = self.motion_overlays(c, dur, 1, switch_at=switch, labels=labels)
            # A render a frame or two short of the clip (the museum take) holds its last frame.
            self.encode(["-i", base] + ov_args, f"[0:v]tpad=stop_mode=clone:stop_duration={num(dur)}[m];" + self.overlay_chain("m", items, self.fades(c, dur), dur), dur, out)
            os.remove(base)
            return dur, f"{b_src} | {a_src}"
        tail = ",".join(["format=yuv420p"] + self.caption_filters(c, dur) + self.fades(c, dur))
        parts.append("".join(segs) + (f"concat=n={len(segs)}:v=1:a=0," if len(segs) > 1 else "") + tail + "[v]")
        self.encode(a_args + b_args, ";".join(parts), dur, out)
        return dur, f"{b_src} | {a_src}"

    def build_title(self, c, out):
        """A title card: "Text" (big), "Sub" and "Small" lines, on black or over a dimmed shot ("Source", "Dim")."""
        dur = self.clip_duration(c)
        if self.motion and not c.get("Source"):
            # An animated card: Text (the letters from AccentFrom on in the accent colour), Sub, Lines and Small (lists of
            # strings), Scale, Y.
            r = self.gfx.card(self.gfx_dir(), self.W, self.H, self.fps, dur, c.get("Text", ""), int(c.get("AccentFrom", 5)), c.get("Sub"),
                              lines=[x if isinstance(x, str) else x.get("Text", "") for x in c.get("Lines", [])], small=c.get("Small") if isinstance(c.get("Small"), list) else None,
                              scale=float(c.get("Scale", 1.0)), y=float(c.get("Y", 0.43)), out_fade=float(c.get("OutFade", 0.45)), beats=c.get("Beats"))
            graph = "[0:v]" + ",".join([f"fps={num(self.fps)}", "setsar=1"] + self.fades(c, dur)) + ",format=yuv420p[v]"
            self.encode(["-framerate", num(self.fps), "-i", r["pattern"]], graph, dur, out)
            return dur, "card"
        fade = float(c.get("TextFade", 0.6))
        t0, t1 = float(c.get("TextAt", 0.25)), dur - float(c.get("TextEnd", 0.25))
        text = []
        cy = self.H * float(c.get("Y", 0.44))
        if c.get("Text"):
            text.append(self.drawtext(c["Text"], float(c.get("Size", 0.15)), "(w-text_w)/2", f"{int(cy)}-text_h/2", t0, t1, fade, font=self.font_title))
        if c.get("Sub"):
            text.append(self.drawtext(c["Sub"], float(c.get("SubSize", 0.042)), "(w-text_w)/2", f"{int(cy + self.H * 0.13)}", t0 + 0.3, t1, fade, color="white@0.9"))
        if c.get("Small"):
            text.append(self.drawtext(c["Small"], 0.026, "(w-text_w)/2", f"{int(self.H * 0.9)}", t0 + 0.6, t1, fade, color="white@0.6"))
        # "Lines": any number of centred lines: { Text, Y (fraction of the height), Size, Color, At, End, Fade, Title (font) }
        for ln in c.get("Lines", []):
            text.append(self.drawtext(ln["Text"], float(ln.get("Size", 0.03)), "(w-text_w)/2", f"{int(self.H * float(ln.get('Y', 0.5)))}-text_h/2",
                                      float(ln.get("At", t0)), dur - float(ln.get("End", dur - t1)), float(ln.get("Fade", fade)),
                                      color=ln.get("Color", "white"), font=self.font_title if ln.get("Title") else None, shadow=False))
        if c.get("Source"):
            args, src = self.source_args(c["Source"], float(c.get("In", 0)), dur, float(c.get("Speed", 1)))
            dim = [f"eq=brightness={num(-float(c.get('Dim', 0.25)))}:saturation={num(float(c.get('Saturation', 0.8)))}"]
            if c.get("Blur"):
                dim.append(f"gblur=sigma={num(float(c['Blur']) * self.H / 720.0)}")
            graph = self.base_chain(c, dur, "0:v", dim + text + self.fades(c, dur)) + ",format=yuv420p[v]"
        else:
            args, src = ["-f", "lavfi", "-i", f"color=c=black:s={self.W}x{self.H}:r={num(self.fps)}:d={num(dur)}"], "black"
            graph = "[0:v]" + ",".join(["setsar=1"] + text + self.fades(c, dur)) + ",format=yuv420p[v]"
        self.encode(args, graph, dur, out)
        return dur, src

    # ------------------------------------------------------------ assembly

    def build(self, only=None):
        clips = []
        for i, c in enumerate(self.e["Clips"]):
            kind = c.get("Type", "shot").lower()
            name = f"{i + 1:02d}_{slug(c.get('Name') or c.get('Caption') if not isinstance(c.get('Caption'), dict) else c.get('Name') or c['Caption'].get('Text'))}.mp4"
            out = os.path.join(self.clips, name)
            if only and (i + 1) not in only and os.path.isfile(out):
                clips.append((c, self.clip_duration(c), out))
                continue
            fn = {"shot": self.build_shot, "wipe": self.build_wipe, "title": self.build_title}.get(kind)
            if fn is None:
                sys.exit(f"clip {i + 1}: unknown Type '{kind}'")
            dur, src = fn(c, out)
            got = probe_duration(out)
            if got is not None and abs(got - dur) > 1.5 / self.fps:
                sys.exit(f"clip {i + 1}: {got:.3f} s written, {dur:.3f} s planned ({out}); see {self.log}")
            print(f"clip {i + 1:2d} {kind:5s} {dur:5.2f}s  {name}  <- {src}")
            clips.append((c, dur, out))
        return clips

    def join(self, clips):
        """Clips in order; each clip's "Transition" says how it comes in: cut (default), fade (cross-fade), dip (through black)."""
        inputs, parts = [], []
        for _, _, path in clips:
            inputs += ["-i", path]
        norm = f"fps={num(self.fps)},format=yuv420p,settb=AVTB,setpts=PTS-STARTPTS"
        for i in range(len(clips)):
            parts.append(f"[{i}:v]{norm}[c{i}]")
        acc, total = "c0", clips[0][1]
        for i in range(1, len(clips)):
            c, dur, _ = clips[i]
            tr = c.get("Transition") or {}
            if isinstance(tr, str):
                tr = {"Type": tr}
            kind, d = tr.get("Type", "cut").lower(), float(tr.get("Duration", 0.5))
            nxt = f"j{i}"
            if kind == "cut":
                parts.append(f"[{acc}][c{i}]concat=n=2:v=1:a=0,{norm}[{nxt}]")
                total += dur
            else:
                d = min(d, dur * 0.9, total * 0.9)
                name = {"fade": "fade", "dip": "fadeblack", "white": "fadewhite"}.get(kind, kind)
                parts.append(f"[{acc}][c{i}]xfade=transition={name}:duration={num(d)}:offset={num(total - d)},{norm}[{nxt}]")
                total += dur - d
            acc = nxt
        final = os.path.join(self.out, slug(self.e.get("Name", "trailer")) + ".mp4")
        args = inputs + []
        maps = ["-map", f"[{acc}]"]
        music = self.e.get("Music")
        if music and music.get("Tracks"):
            # Several pieces on the trailer's timeline: { File, Start (seconds into the file), At (trailer time it comes in),
            # Until (trailer time it has gone), FadeIn, FadeOut (lengths, ending at Until), Volume }. Layers of one song
            # (ULTRAKILL's clean / battle versions) cross-fade when one's Until fade overlaps the next one's At fade.
            mix = []
            for k, tr in enumerate(music["Tracks"]):
                mfile = tr["File"] if os.path.isabs(tr["File"]) else os.path.join(self.root, tr["File"])
                if not os.path.isfile(mfile):
                    print(f"warning: music not found ({mfile})")
                    continue
                at, until = float(tr.get("At", 0)), min(total, float(tr.get("Until", total)))
                length = max(0.1, until - at)
                fi, fo = float(tr.get("FadeIn", 0.02)), float(tr.get("FadeOut", 0.02))
                args += ["-stream_loop", "-1", "-ss", num(float(tr.get("Start", 0))), "-i", mfile]
                af = [f"atrim=duration={num(length)}", "asetpts=PTS-STARTPTS", f"volume={num(float(tr.get('Volume', 1)))}",
                      f"afade=t=in:st=0:d={num(fi)}", f"afade=t=out:st={num(max(0, length - fo))}:d={num(fo)}",
                      f"adelay={int(round(at * 1000))}:all=1"]
                parts.append(f"[{len(clips) + len(mix)}:a]" + ",".join(af) + f"[m{k}]")
                mix.append(f"[m{k}]")
            if mix:
                fo = float(music.get("FadeOut", 2.0))
                parts.append("".join(mix) + f"amix=inputs={len(mix)}:normalize=0:duration=longest,volume={num(float(music.get('Volume', 1)))},"
                             f"afade=t=out:st={num(max(0, total - fo))}:d={num(fo)},atrim=duration={num(total)}[a]")
                maps += ["-map", "[a]", "-c:a", "aac", "-b:a", "320k"]
        elif music and music.get("File"):
            mfile = music["File"] if os.path.isabs(music["File"]) else os.path.join(self.root, music["File"])
            if any(ch in mfile for ch in "*?["):
                hits = sorted(glob.glob(mfile))
                mfile = hits[-1] if hits else mfile
            if not os.path.isfile(mfile):
                print(f"warning: music not found ({mfile}); the trailer is silent")
            else:
                args += ["-ss", num(float(music.get("Start", 0))), "-i", mfile]
                fi, fo = float(music.get("FadeIn", 0.05)), float(music.get("FadeOut", 1.5))
                af = [f"volume={num(float(music.get('Volume', 1)))}", f"afade=t=in:st=0:d={num(fi)}",
                      f"afade=t=out:st={num(max(0, total - fo))}:d={num(fo)}", f"atrim=duration={num(total)}"]
                parts.append(f"[{len(clips)}:a]" + ",".join(af) + "[a]")
                maps += ["-map", "[a]", "-c:a", "aac", "-b:a", "256k"]
        run(args + ["-filter_complex", ";".join(parts)] + maps + ["-t", num(total), "-r", num(self.fps), "-c:v", "libx264", "-preset", "slow",
                    "-crf", str(self.crf), "-pix_fmt", "yuv420p", "-movflags", "+faststart", final], self.work, self.log)
        print(f"trailer {total:.2f}s -> {final}")
        # "Exports": smaller copies of the finished trailer: { Suffix, Height, Crf }.
        for ex in ([] if self.no_exports else self.e.get("Exports", [])):
            h = int(ex["Height"])
            w = int(round(self.W * h / self.H / 2)) * 2
            path = os.path.join(self.out, slug(self.e.get("Name", "trailer")) + ex.get("Suffix", f"-{h}p") + ".mp4")
            run(["-i", final, "-vf", f"scale={w}:{h}:flags=lanczos", "-c:v", "libx264", "-preset", "slow", "-crf", str(int(ex.get("Crf", 16))),
                 "-pix_fmt", "yuv420p", "-c:a", "copy", "-movflags", "+faststart", path], self.work, self.log)
            print(f"export -> {path}")
        return final


def main():
    global FFMPEG
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("edit", help="edit-list JSON")
    ap.add_argument("--root", help="folder the sources and the music are relative to (default: the edit list's Root)")
    ap.add_argument("--out", help="output folder (default: <root>/trailer/<name>)")
    ap.add_argument("--only", help="rebuild only these clips (1-based, comma separated); the others are reused if present")
    ap.add_argument("--keep", action="store_true", help="keep the work folder (text files, fonts)")
    ap.add_argument("--no-exports", action="store_true", help="skip the edit list's Exports (e.g. the 1080p copy)")
    a = ap.parse_args()
    FFMPEG = find_ffmpeg()
    edit = json.load(open(a.edit, encoding="utf-8-sig"))
    b = Builder(edit, a.edit, a.root, a.out)
    b.no_exports = a.no_exports
    only = {int(x) for x in a.only.split(",")} if a.only else None
    clips = b.build(only)
    b.join(clips)
    if not a.keep:
        shutil.rmtree(b.work, ignore_errors=True)


if __name__ == "__main__":
    main()
