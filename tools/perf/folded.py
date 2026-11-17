"""Queries on a folded-stack file written by tools/perf/EtwProfile (one line per stack, root first: "a;b;c <microseconds>").

    py -3 tools/perf/folded.py <file.folded> [--frames N] [--focus SUBSTR] [--depth 6] [--top 30] [--min 0.5]

Without --focus: the top functions by inclusive time.
With --focus: for the stacks that pass through a frame whose name contains SUBSTR, the tree below the first such
frame (children by inclusive time, down to --depth), and the callers above it. --min hides nodes under that percent of
the focus. --frames divides by a frame count (ms per frame, as the summary).
"""
import argparse
import collections
import sys


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("file")
    ap.add_argument("--frames", type=int, default=0)
    ap.add_argument("--focus")
    ap.add_argument("--depth", type=int, default=6)
    ap.add_argument("--top", type=int, default=30)
    ap.add_argument("--min", type=float, default=0.5)
    a = ap.parse_args()

    stacks = []
    for line in open(a.file, encoding="utf-8"):
        line = line.rstrip("\n")
        if not line:
            continue
        s, _, us = line.rpartition(" ")
        stacks.append((s.split(";"), int(us) / 1000.0))
    total = sum(ms for _, ms in stacks)
    div = a.frames if a.frames > 0 else 1.0
    unit = "ms/frame" if a.frames > 0 else "ms"

    if not a.focus:
        incl = collections.Counter()
        for fr, ms in stacks:
            for f in set(fr):
                incl[f] += ms
        print("total %.3f %s" % (total / div, unit))
        for f, ms in incl.most_common(a.top):
            print("%9.3f %5.1f%%  %s" % (ms / div, 100 * ms / max(total, 1e-9), f))
        return

    needle = a.focus.lower()
    tree = {}
    callers = collections.Counter()
    hit = 0.0
    for fr, ms in stacks:
        idx = next((i for i, f in enumerate(fr) if needle in f.lower()), -1)
        if idx < 0:
            continue
        hit += ms
        callers[" < ".join(reversed(fr[max(0, idx - 3):idx]))] += ms
        node = tree
        for f in fr[idx:]:
            child = node.setdefault(f, [0.0, {}])
            child[0] += ms
            node = child[1]
    if hit <= 0:
        sys.exit("no stack contains '%s'" % a.focus)
    print("focus '%s': %.3f %s (%.1f%% of the thread's %.3f)" % (a.focus, hit / div, unit, 100 * hit / max(total, 1e-9), total / div))

    def walk(node, depth):
        for f, (ms, kids) in sorted(node.items(), key=lambda kv: -kv[1][0]):
            if 100 * ms / hit < a.min:
                continue
            print("%9.3f %5.1f%%  %s%s" % (ms / div, 100 * ms / hit, "  " * depth, f))
            if depth + 1 < a.depth:
                walk(kids, depth + 1)

    walk(tree, 0)
    print("callers:")
    for c, ms in callers.most_common(8):
        print("%9.3f %5.1f%%  %s" % (ms / div, 100 * ms / hit, c or "(root)"))


if __name__ == "__main__":
    main()
