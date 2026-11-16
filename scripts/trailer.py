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


def run(args, cwd, log):
    """Run ffmpeg quietly; on failure show the tail of its output."""
    cmd = [FFMPEG, "-hide_banner", "-loglevel", "warning", "-y"] + args
    with open(log, "a", encoding="utf-8") as f:
        f.write("\n$ ffmpeg " + " ".join(f'"{a}"' if (" " in a or "'" in a) else a for a in cmd[1:]) + "\n")
    p = subprocess.run(cmd, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
    with open(log, "a", encoding="utf-8") as f:
        f.write(p.stdout or "")
    if p.returncode != 0:
        sys.exit(f"ffmpeg failed ({p.returncode}); see {log}\n" + "\n".join((p.stdout or "").splitlines()[-15:]))


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
            first = int(re.search(r"frame_(\d+)\.png$", frames[0]).group(1)) + int(round(t_in * sfps))
            have = (len(frames) - int(round(t_in * sfps))) / sfps
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
                      "-c:v", "libx264", "-preset", "fast", "-crf", "12", "-pix_fmt", "yuv420p", out], self.work, self.log)

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
        divider is "After". Each side's label is burned into its own picture, so the wipe reveals and hides it."""
        dur = self.clip_duration(c)
        t_in, speed = float(c.get("In", 0)), float(c.get("Speed", 1))
        a_args, a_src = self.source_args(c["After"], t_in + float(c.get("AfterOffset", 0)), dur, speed)
        b_args, b_src = self.source_args(c["Before"], t_in, dur, speed)
        w = c.get("Wipe", {})
        keys = w.get("Keys", [[0, 0], [dur * 0.3, 0], [dur * 0.7, 1], [dur, 1]])
        slant = float(w.get("Slant", 0.18))
        line = float(w.get("Line", 2.0)) * self.H / 720.0
        labels = c.get("Labels", ["RT OFF", "PATH TRACED"])
        y = f"{int(self.H * 0.06)}"
        before_label = [self.drawtext(labels[0], 0.036, f"w-text_w-{int(self.W * 0.04)}", y, 0.0, dur + 1, fade=0.3, box=True)] if labels and labels[0] else []
        after_label = [self.drawtext(labels[1], 0.036, f"{int(self.W * 0.04)}", y, 0.0, dur + 1, fade=0.3, box=True)] if labels and len(labels) > 1 and labels[1] else []
        pos = self.wipe_position(keys, slant)
        # D < 0: left of the divider. gbrp: every plane is full size, so one expression draws the white line in all three.
        d = f"(X-W*({pos})-(Y-H/2)*{num(slant)})"
        blend = f"blend=all_expr='if(lt(abs({d}),{num(line)}),255,if(lt({d},0),A,B))'"
        graph = (self.base_chain(c, dur, "0:v", after_label) + ",format=gbrp[a];" +
                 self.base_chain(c, dur, "1:v", before_label) + ",format=gbrp[b];" +
                 "[a][b]" + blend + "," + ",".join(["format=yuv420p"] + self.caption_filters(c, dur) + self.fades(c, dur)) + "[v]")
        self.encode(a_args + b_args, graph, dur, out)
        return dur, f"{b_src} | {a_src}"

    def build_title(self, c, out):
        """A title card: "Text" (big), "Sub" and "Small" lines, on black or over a dimmed shot ("Source", "Dim")."""
        dur = self.clip_duration(c)
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
        if music and music.get("File"):
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
        return final


def main():
    global FFMPEG
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("edit", help="edit-list JSON")
    ap.add_argument("--root", help="folder the sources and the music are relative to (default: the edit list's Root)")
    ap.add_argument("--out", help="output folder (default: <root>/trailer/<name>)")
    ap.add_argument("--only", help="rebuild only these clips (1-based, comma separated); the others are reused if present")
    ap.add_argument("--keep", action="store_true", help="keep the work folder (text files, fonts)")
    a = ap.parse_args()
    FFMPEG = find_ffmpeg()
    edit = json.load(open(a.edit, encoding="utf-8-sig"))
    b = Builder(edit, a.edit, a.root, a.out)
    only = {int(x) for x in a.only.split(",")} if a.only else None
    clips = b.build(only)
    b.join(clips)
    if not a.keep:
        shutil.rmtree(b.work, ignore_errors=True)


if __name__ == "__main__":
    main()
