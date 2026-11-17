"""Writes one checkpoint-tour suite per level into bench/suites/tour/ (bench/README.md, "Tour").

    py -3 tools/lights/tour_suites.py [--modes off,pathtrace,pathtrace+ae.enabled=0] [--views 3] [levels ...]

    .\\scripts\\bench.ps1 -Suite bench\\suites\\tour\\tour-1-4.json -Slot alt
    py -3 tools/lights/score.py bench/results/<run> --sheet --board bench/results/scoreboard.csv

A tour loads the level once per stop (the start and every checkpoint), so a level takes one to three minutes: run two
or three levels per game launch, not all of them.
"""
import argparse
import json
import os

LEVELS = (["0-1", "0-2", "0-3", "0-4", "0-5", "1-1", "1-2", "1-3", "1-4", "2-1", "2-2", "2-3", "2-4", "3-1", "3-2",
           "4-1", "4-2", "4-3", "4-4", "5-1", "5-2", "5-3", "5-4", "6-1", "6-2", "7-1", "7-2", "7-3", "7-4",
           "8-1", "8-2", "8-3", "8-4", "P-1", "P-2", "0-S", "1-S", "2-S", "4-S", "5-S", "7-S", "0-E", "1-E"])


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("levels", nargs="*", help="e.g. 1-4 6-2 (default: every campaign level)")
    ap.add_argument("--modes", default="off,pathtrace,pathtrace+ae.enabled=0",
                    help="off first; ae.enabled=0 is fixed exposure, for judging the lights themselves")
    ap.add_argument("--views", type=int, default=3)
    ap.add_argument("--max", type=int, default=0, help="at most this many stops per level (0 = all)")
    a = ap.parse_args()
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "bench", "suites", "tour")
    os.makedirs(out, exist_ok=True)
    for lv in a.levels or LEVELS:
        name = f"tour-{lv}"
        suite = {
            "Name": name,
            "Note": "Checkpoint tour for the brightness scoreboard (tools/lights/tour_suites.py wrote this file; "
                    "bench/README.md, 'Tour'). Score with tools/lights/score.py.",
            "Scenarios": [{
                "Name": f"{lv} tour", "Scene": f"Level {lv}",
                "Path": {"Type": "tour"},
                "Tour": {"Views": a.views, "Max": a.max},
                "Modes": a.modes.split(","), "ShotSettle": 0.8,
            }],
        }
        with open(os.path.join(out, name + ".json"), "w", newline="\n", encoding="utf-8") as f:
            json.dump(suite, f, indent=2)
            f.write("\n")
    print(f"{len(a.levels or LEVELS)} suites in {os.path.normpath(out)}")


if __name__ == "__main__":
    main()
