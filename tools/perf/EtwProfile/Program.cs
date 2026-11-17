// EtwProfile: per-thread CPU and wait profiles of one process from a WPR "CPU" trace (docs/performance.md, "ETW").
//
//   EtwProfile <trace.etl> --out <dir> [--process ULTRAKILL] [--window <fromUtc>,<toUtc>]... [--frames N]
//              [--symbols <cache dir>] [--threads 6] [--top 40]
//
// --window: unix seconds (UTC), as bench results give them (ModeStats.WindowsUtc); several add up. Without one the
//           whole trace counts.
// --frames: frames rendered inside the windows; totals are then also given per frame.
//
// Output in <dir>:
//   summary.md               threads by CPU time; per thread: CPU by module, top functions (self and inclusive), waits
//   cpu_<thread>.folded      one line per distinct stack, root first, "a;b;c <microseconds>" (flame-graph format)
//   wait_<thread>.folded     the same for time spent blocked, by the stack the thread blocked at
//   ready_<thread>.folded    time runnable but not running (CPU contention), by stack
// tools/perf/folded.py reads the folded files (callers, callees, sub-trees).
using System.Globalization;
using System.Text;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

static class Program
{
    sealed class ThreadAcc
    {
        public int Id;
        public string Name = "";
        public double CpuMs, WaitMs, ReadyMs;
        public readonly Dictionary<string, double> Cpu = new(), Wait = new(), Ready = new();
        public double OutAt = -1;       // when it was last switched out (ms), -1 = running or unknown
        public bool OutWaiting;         // switched out because it blocked (else: preempted)
    }

    static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length < 1) { Console.Error.WriteLine("usage: EtwProfile <trace.etl> --out <dir> [--process NAME] [--window fromUtc,toUtc]... [--frames N] [--symbols DIR] [--threads N] [--top N]"); return 2; }
        string etl = args[0], outDir = null, process = "ULTRAKILL", symCache = Path.Combine(Path.GetTempPath(), "ultrart-symbols");
        var windowsUtc = new List<(double from, double to)>();
        int frames = 0, maxThreads = 6, top = 40;
        for (int i = 1; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("value missing after " + args[i]);
            switch (args[i])
            {
                case "--out": outDir = Next(); break;
                case "--process": process = Next(); break;
                case "--frames": frames = int.Parse(Next()); break;
                case "--symbols": symCache = Next(); break;
                case "--threads": maxThreads = int.Parse(Next()); break;
                case "--top": top = int.Parse(Next()); break;
                case "--window":
                {
                    var p = Next().Split(',');
                    windowsUtc.Add((double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture)));
                    break;
                }
                default: throw new ArgumentException("unknown argument " + args[i]);
            }
        }
        outDir ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(etl)) ?? ".", "etw");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(symCache);

        string etlx = Path.ChangeExtension(etl, ".etlx");
        if (!File.Exists(etlx) || File.GetLastWriteTimeUtc(etlx) < File.GetLastWriteTimeUtc(etl))
        {
            Console.WriteLine("converting " + etl + " ...");
            TraceLog.CreateFromEventTraceLogFile(etl, etlx, new TraceLogOptions { ConversionLog = TextWriter.Null });
        }
        using var log = new TraceLog(etlx);
        var proc = log.Processes.Where(p => string.Equals(p.Name, process, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.CPUMSec).FirstOrDefault();
        if (proc == null) { Console.Error.WriteLine($"no process named '{process}' in the trace"); return 1; }

        // Windows, relative to the session start in ms.
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double sessionStartUnix = (log.SessionStartTime.ToUniversalTime() - epoch).TotalSeconds;
        var windows = windowsUtc.Select(w => ((w.from - sessionStartUnix) * 1000.0, (w.to - sessionStartUnix) * 1000.0)).OrderBy(w => w.Item1).ToList();
        if (windows.Count == 0) windows.Add((Math.Max(0, proc.StartTimeRelativeMsec), Math.Min(log.SessionDuration.TotalMilliseconds, proc.EndTimeRelativeMsec)));
        double windowMs = windows.Sum(w => w.Item2 - w.Item1);
        bool In(double t) { foreach (var w in windows) if (t >= w.Item1 && t <= w.Item2) return true; return false; }
        // The part of [a, b] inside the windows.
        double Overlap(double a, double b) { double s = 0; foreach (var w in windows) s += Math.Max(0, Math.Min(b, w.Item2) - Math.Max(a, w.Item1)); return s; }

        // Symbols: Microsoft's and Unity's public servers, cached. Only the modules that carry samples are looked up.
        var symLog = new StringWriter();
        using var reader = new SymbolReader(symLog, $"SRV*{symCache}*https://msdl.microsoft.com/download/symbols;SRV*{symCache}*http://symbolserver.unity3d.com/");
        reader.SecurityCheck = _ => true;

        double sampleMs = log.SampleProfileInterval.TotalMilliseconds;
        if (sampleMs <= 0) sampleMs = 1.0;
        var threads = new Dictionary<int, ThreadAcc>();
        ThreadAcc Acc(int tid)
        {
            if (!threads.TryGetValue(tid, out var a)) threads[tid] = a = new ThreadAcc { Id = tid };
            return a;
        }
        var ours = new HashSet<int>(proc.Threads.Select(t => t.ThreadID));
        foreach (var t in proc.Threads) Acc(t.ThreadID).Name = t.ThreadInfo ?? "";

        // Pass 1: which modules matter (so their symbols are fetched before the stacks are named).
        var moduleSamples = new Dictionary<TraceModuleFile, int>();
        foreach (var e in proc.EventsInProcess)
        {
            if (!(e is SampledProfileTraceData) || !In(e.TimeStampRelativeMSec)) continue;
            for (var cs = e.CallStack(); cs != null; cs = cs.Caller)
            {
                var mf = cs.CodeAddress.ModuleFile;
                if (mf != null) moduleSamples[mf] = moduleSamples.TryGetValue(mf, out int n) ? n + 1 : 1;
            }
        }
        foreach (var kv in moduleSamples.OrderByDescending(kv => kv.Value).Take(40))
        {
            try { log.CodeAddresses.LookupSymbolsForModule(reader, kv.Key); }
            catch (Exception ex) { symLog.WriteLine($"symbols for {kv.Key.Name}: {ex.Message}"); }
        }

        var nameCache = new Dictionary<CodeAddressIndex, string>();
        string Frame(TraceCallStack cs)
        {
            var ca = cs.CodeAddress;
            if (nameCache.TryGetValue(ca.CodeAddressIndex, out var s)) return s;
            string mod = ca.ModuleFile != null ? ca.ModuleFile.Name : null;
            string fn = ca.FullMethodName;
            if (string.IsNullOrEmpty(mod)) s = "[jit or unknown]";
            else if (string.IsNullOrEmpty(fn)) s = mod + "!?";
            else s = mod + "!" + fn.Replace(';', ':').Replace(' ', '_');
            return nameCache[ca.CodeAddressIndex] = s;
        }
        var sb = new StringBuilder();
        var frameBuf = new List<string>(128);
        string Folded(TraceCallStack cs)
        {
            frameBuf.Clear();
            for (; cs != null; cs = cs.Caller) frameBuf.Add(Frame(cs));
            if (frameBuf.Count == 0) return "[no stack]";
            sb.Clear();
            for (int k = frameBuf.Count - 1; k >= 0; k--) { if (sb.Length > 0) sb.Append(';'); sb.Append(frameBuf[k]); }
            return sb.ToString();
        }
        static void Add(Dictionary<string, double> d, string k, double v) => d[k] = d.TryGetValue(k, out double o) ? o + v : v;

        // Pass 2: samples (CPU) and context switches (waits) of the process's threads.
        foreach (var e in log.Events)
        {
            double t = e.TimeStampRelativeMSec;
            if (e is SampledProfileTraceData)
            {
                if (e.ProcessID != proc.ProcessID || !In(t)) continue;
                var a = Acc(e.ThreadID);
                a.CpuMs += sampleMs;
                Add(a.Cpu, Folded(e.CallStack()), sampleMs);
            }
            else if (e is CSwitchTraceData cs)
            {
                if (ours.Contains(cs.OldThreadID))
                {
                    var a = Acc(cs.OldThreadID);
                    a.OutAt = t;
                    a.OutWaiting = (int)cs.OldThreadState == 5 /* KTHREAD_STATE Waiting */;
                }
                if (ours.Contains(cs.NewThreadID))
                {
                    var a = Acc(cs.NewThreadID);
                    if (a.OutAt >= 0)
                    {
                        double ms = Overlap(a.OutAt, t);
                        if (ms > 0)
                        {
                            string f = Folded(e.CallStack());
                            if (a.OutWaiting) { a.WaitMs += ms; Add(a.Wait, f, ms); }
                            else { a.ReadyMs += ms; Add(a.Ready, f, ms); }
                        }
                    }
                    a.OutAt = -1;
                }
            }
        }

        // ---------------------------------------------------------------- output
        var order = threads.Values.Where(a => a.CpuMs > 0 || a.WaitMs > 0).OrderByDescending(a => a.CpuMs).ToList();
        var md = new StringBuilder();
        string per = frames > 0 ? " (ms per frame)" : "";
        double F(double ms) => frames > 0 ? ms / frames : ms;
        md.AppendLine($"# ETW profile: {proc.Name} (pid {proc.ProcessID})");
        md.AppendLine();
        md.AppendLine($"Trace `{Path.GetFileName(etl)}`, {windows.Count} window(s), {windowMs / 1000.0:F2} s{(frames > 0 ? $", {frames} frames ({windowMs / frames:F3} ms per frame)" : "")}; sample interval {sampleMs:F3} ms.");
        md.AppendLine();
        md.AppendLine($"| Thread | id | CPU % of window | CPU{per} | blocked{per} | runnable, not running{per} |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var a in order.Take(24))
            md.AppendLine($"| {Label(a)} | {a.Id} | {100.0 * a.CpuMs / windowMs:F1} | {F(a.CpuMs):F3} | {F(a.WaitMs):F3} | {F(a.ReadyMs):F3} |");
        md.AppendLine();

        var used = new HashSet<string>();
        foreach (var a in order.Take(maxThreads))
        {
            string file = Sanitize(Label(a));
            if (!used.Add(file)) { file += "_" + a.Id; used.Add(file); }
            WriteFolded(Path.Combine(outDir, $"cpu_{file}.folded"), a.Cpu);
            WriteFolded(Path.Combine(outDir, $"wait_{file}.folded"), a.Wait);
            WriteFolded(Path.Combine(outDir, $"ready_{file}.folded"), a.Ready);

            md.AppendLine($"## {Label(a)} (thread {a.Id}): CPU {F(a.CpuMs):F3}{per}, blocked {F(a.WaitMs):F3}, runnable {F(a.ReadyMs):F3}");
            md.AppendLine();
            var self = new Dictionary<string, double>();
            var incl = new Dictionary<string, double>();
            var byModule = new Dictionary<string, double>();
            var seen = new HashSet<string>();
            foreach (var kv in a.Cpu)
            {
                var fr = kv.Key.Split(';');
                string leaf = fr[fr.Length - 1];
                Add(self, leaf, kv.Value);
                Add(byModule, leaf.Split('!')[0], kv.Value);
                seen.Clear();
                foreach (var f in fr) if (seen.Add(f)) Add(incl, f, kv.Value);
            }
            md.AppendLine("CPU by module (self): " + string.Join(", ", byModule.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key} {F(kv.Value):F3}")));
            md.AppendLine();
            md.AppendLine($"| Self{per} | % | Function |");
            md.AppendLine("|---|---|---|");
            foreach (var kv in self.OrderByDescending(kv => kv.Value).Take(top))
                md.AppendLine($"| {F(kv.Value):F3} | {100.0 * kv.Value / Math.Max(a.CpuMs, 1e-9):F1} | `{kv.Key}` |");
            md.AppendLine();
            md.AppendLine($"| Inclusive{per} | % | Function |");
            md.AppendLine("|---|---|---|");
            foreach (var kv in incl.OrderByDescending(kv => kv.Value).Take(top * 2))
                md.AppendLine($"| {F(kv.Value):F3} | {100.0 * kv.Value / Math.Max(a.CpuMs, 1e-9):F1} | `{kv.Key}` |");
            md.AppendLine();
            if (a.Wait.Count > 0)
            {
                // Blocked time by the innermost frame that is not in the kernel or ntdll: what the thread was waiting in.
                var waitBy = new Dictionary<string, double>();
                foreach (var kv in a.Wait)
                {
                    var fr = kv.Key.Split(';');
                    string pick = fr[fr.Length - 1];
                    for (int k = fr.Length - 1; k >= 0; k--)
                    {
                        string m = fr[k].Split('!')[0].ToLowerInvariant();
                        if (m == "ntoskrnl" || m == "ntdll" || m == "kernelbase" || m == "kernel32" || m == "win32u" || m == "user32") continue;
                        pick = fr[k];
                        break;
                    }
                    Add(waitBy, pick, kv.Value);
                }
                md.AppendLine($"| Blocked{per} | % | Waiting in |");
                md.AppendLine("|---|---|---|");
                foreach (var kv in waitBy.OrderByDescending(kv => kv.Value).Take(15))
                    md.AppendLine($"| {F(kv.Value):F3} | {100.0 * kv.Value / Math.Max(a.WaitMs, 1e-9):F1} | `{kv.Key}` |");
                md.AppendLine();
            }
        }
        File.WriteAllText(Path.Combine(outDir, "summary.md"), md.ToString());
        File.WriteAllText(Path.Combine(outDir, "symbols.log"), symLog.ToString());
        Console.WriteLine($"{proc.Name}: {windowMs / 1000.0:F2} s in {windows.Count} window(s), {order.Count} threads with samples -> {Path.Combine(outDir, "summary.md")}");
        return 0;

        string Label(ThreadAcc a) => string.IsNullOrEmpty(a.Name) ? "thread " + a.Id : a.Name;
    }

    static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.ToString();
    }

    static void WriteFolded(string path, Dictionary<string, double> d)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var kv in d.OrderByDescending(kv => kv.Value)) w.Write(kv.Key + " " + ((long)Math.Round(kv.Value * 1000.0)).ToString(CultureInfo.InvariantCulture) + "\n");
    }
}
