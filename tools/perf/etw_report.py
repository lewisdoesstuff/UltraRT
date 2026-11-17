"""Per-thread CPU and wait profiles for every scenario and mode of a bench run recorded with bench.ps1 -Etw.

    py -3 tools/perf/etw_report.py bench/results/<run> [--scenario NAME] [--mode MODE] [--top 40]

Reads results.json (each mode's measurement windows and frame count) and trace.etl in the run folder, runs
tools/perf/EtwProfile once per scenario and mode, and writes <run>/etw/<scenario>_<mode>/summary.md plus folded
stacks (tools/perf/folded.py reads those). docs/performance.md, "ETW".

The trace is system-wide and holds call stacks of every process: keep it and its reports out of the repo
(bench/results is gitignored).
"""
import argparse
import json
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PROJECT = os.path.join(ROOT, "tools", "perf", "EtwProfile")
DLL = os.path.join(PROJECT, "bin", "Release", "net9.0-windows", "EtwProfile.dll")


def dotnet():
    local = os.path.join(os.environ.get("USERPROFILE", ""), ".dotnet", "dotnet.exe")
    return local if os.path.exists(local) else "dotnet"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("run")
    ap.add_argument("--scenario")
    ap.add_argument("--mode")
    ap.add_argument("--top", type=int, default=40)
    ap.add_argument("--process", default="ULTRAKILL")
    a = ap.parse_args()

    etl = os.path.join(a.run, "trace.etl")
    if not os.path.exists(etl):
        sys.exit("no trace.etl in " + a.run + " (run bench.ps1 with -Etw)")
    if not os.path.exists(DLL):
        subprocess.check_call([dotnet(), "build", PROJECT, "-c", "Release", "-v", "q", "-nologo"])
    with open(os.path.join(a.run, "results.json"), encoding="utf-8-sig") as f:
        results = json.load(f)

    for sc in results["Scenarios"]:
        if a.scenario and sc["Name"] != a.scenario:
            continue
        for mode, st in (sc.get("Modes") or {}).items():
            if a.mode and mode != a.mode:
                continue
            windows = st.get("WindowsUtc") or []
            if not windows or not st.get("Frames"):
                continue
            name = re.sub(r"[^A-Za-z0-9-]", "_", sc["Name"] + "_" + mode)
            out = os.path.join(a.run, "etw", name)
            cmd = [dotnet(), DLL, etl, "--out", out, "--process", a.process, "--frames", str(st["Frames"]), "--top", str(a.top)]
            for w in windows:
                cmd += ["--window", "%.6f,%.6f" % (w[0], w[1])]
            print("%s / %s: %d frames, %.3f ms avg" % (sc["Name"], mode, st["Frames"], st["AvgMs"]), flush=True)
            subprocess.check_call(cmd)


if __name__ == "__main__":
    main()
