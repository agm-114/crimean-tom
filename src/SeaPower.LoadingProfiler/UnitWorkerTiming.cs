using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace SeaPower.LoadingProfiler;

internal static class UnitWorkerTiming
{
    private enum Work { Build, Loadouts, Ammunition, Schema, OpenIni, VirtualIni, ModifierScan, FileRead, CacheLock }
    private static readonly string[] names = Enum.GetNames(typeof(Work));
    private static readonly ConcurrentBag<Worker> workers = [];
    private static readonly Process process = Process.GetCurrentProcess();
    private static TimeSpan startCpu;
    private static int pass;
    private static volatile bool active;
    [ThreadStatic] private static Worker? current;

    private class Worker
    {
        internal readonly int Pass = pass;
        internal readonly long[] Ticks = new long[names.Length];
        internal readonly int[] Calls = new int[names.Length];
        internal readonly HashSet<string> ReadPaths = new(StringComparer.OrdinalIgnoreCase);
        internal Work Category;
        internal long ChangedAt;
        internal int Units;

        internal Work Switch(Work next)
        {
            long now = Stopwatch.GetTimestamp();
            Ticks[(int)Category] += now - ChangedAt;
            ChangedAt = now;
            Work previous = Category;
            Category = next;
            return previous;
        }
    }

    private static readonly Dictionary<MethodBase, Work> categories = [];

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(SimpleObjectParameters), "LoadSimpleParameters"),
            Hook(nameof(BeginUnit)), finalizer: Hook(nameof(EndUnit)));
        Add(typeof(SimpleObjectParameters), "LoadLoadouts", Work.Loadouts);
        Add(typeof(SimpleObjectParameters), "LoadAmmunitionIds", Work.Ammunition);
        Add(typeof(IniHandler), "open", Work.OpenIni);
        Add(typeof(IniHandler), "ScanForModifierFiles", Work.ModifierScan);
        Add(typeof(Utils), "openVirtualIniFile", Work.VirtualIni);
        Add(typeof(LocalizationLoader), "OpenVirtualLocalizationFile", Work.VirtualIni);
        if (AccessTools.TypeByName("SeaPower.IniSchema") is Type schema)
            Add(schema, "Bind", Work.Schema);

        foreach (var method in AccessTools.GetDeclaredMethods(typeof(IniHandler))
            .Where(method => method.Name is "TryReadAllLines" or "ReadFileToIni" or "ReadModifierDirectives"))
            harmony.Patch(method, transpiler: Hook(nameof(InstrumentCalls)));

        foreach (var method in categories.Keys.Where(method => categories[method] is Work.ModifierScan or Work.VirtualIni))
            harmony.Patch(method, transpiler: Hook(nameof(InstrumentCalls)));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(LocalizationLoader))
            .Where(method => method.Name == "LoadJsonAsIniData"))
            harmony.Patch(method, transpiler: Hook(nameof(InstrumentCalls)));

        void Add(Type type, string name, Work category)
        {
            foreach (var method in AccessTools.GetDeclaredMethods(type).Where(method => method.Name == name && !method.ContainsGenericParameters))
            {
                categories.Add(method, category);
                harmony.Patch(method, Hook(nameof(BeginWork)), finalizer: Hook(nameof(EndWork)));
            }
        }
    }

    private static HarmonyMethod Hook(string name) => new(typeof(UnitWorkerTiming), name);

    internal static void Start()
    {
        while (workers.TryTake(out _)) { }
        pass++;
        startCpu = process.TotalProcessorTime;
        active = true;
    }

    internal static void Finish()
    {
        active = false;
        Worker[] totals = workers.ToArray();
        TraceLog.Write("unit-worker-total", new
        {
            units = totals.Sum(worker => worker.Units),
            threads = totals.Length,
            processCpuSeconds = (process.TotalProcessorTime - startCpu).TotalSeconds,
            paths = totals.SelectMany(worker => worker.ReadPaths).Select(path => path.Replace('\\', '/'))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            categories = names.Select((name, index) => new
            {
                name,
                calls = totals.Sum(worker => worker.Calls[index]),
                threadSeconds = totals.Sum(worker => worker.Ticks[index]) / (double)Stopwatch.Frequency
            })
        });
    }

    private static void BeginUnit(out Worker? __state)
    {
        __state = null;
        if (!active || current != null) return;
        // Reuse a tally across units handled by the same worker thread during this pass.
        if (local?.Pass != pass) local = NewWorker();
        __state = local;
        current = __state;
        current.Category = Work.Build;
        current.Calls[(int)Work.Build]++;
        current.ChangedAt = Stopwatch.GetTimestamp();
        current.Units++;
    }

    [ThreadStatic] private static Worker? local;

    private static Worker NewWorker()
    {
        var worker = new Worker();
        workers.Add(worker);
        return worker;
    }

    private static void EndUnit(Worker? __state)
    {
        if (__state == null) return;
        __state.Switch(Work.Build);
        current = null;
    }

    private static void BeginWork(MethodBase __originalMethod, out Work? __state) =>
        __state = current == null ? null : EnterWork(categories[__originalMethod]);

    private static Work? EnterWork(Work next)
    {
        if (current is not { } worker) return null;
        worker.Calls[(int)next]++;
        return worker.Switch(next);
    }

    private static void EndWork(Work? __state)
    {
        if (__state is Work previous) current?.Switch(previous);
    }

    private static IEnumerable<CodeInstruction> InstrumentCalls(IEnumerable<CodeInstruction> code)
    {
        MethodInfo readLines = AccessTools.Method(typeof(File), nameof(File.ReadAllLines), [typeof(string)]);
        MethodInfo readBytes = AccessTools.Method(typeof(File), nameof(File.ReadAllBytes), [typeof(string)]);
        MethodInfo readText = AccessTools.Method(typeof(File), nameof(File.ReadAllText), [typeof(string)]);
        MethodInfo enter = AccessTools.Method(typeof(Monitor), nameof(Monitor.Enter), [typeof(object), typeof(bool).MakeByRefType()]);
        foreach (var instruction in code)
        {
            string? replacement = instruction.Calls(readLines) ? nameof(ReadLines)
                : instruction.Calls(readBytes) ? nameof(ReadBytes)
                : instruction.Calls(readText) ? nameof(ReadText)
                : instruction.Calls(enter) ? nameof(EnterCacheLock) : null;
            if (replacement != null) instruction.operand = AccessTools.Method(typeof(UnitWorkerTiming), replacement);
            yield return instruction;
        }
    }

    private static string[] ReadLines(string path)
    {
        current?.ReadPaths.Add(path);
        var previous = EnterWork(Work.FileRead);
        try { return File.ReadAllLines(path); }
        finally { EndWork(previous); }
    }

    private static byte[] ReadBytes(string path)
    {
        current?.ReadPaths.Add(path);
        var previous = EnterWork(Work.FileRead);
        try { return File.ReadAllBytes(path); }
        finally { EndWork(previous); }
    }

    private static string ReadText(string path)
    {
        current?.ReadPaths.Add(path);
        var previous = EnterWork(Work.FileRead);
        try { return File.ReadAllText(path); }
        finally { EndWork(previous); }
    }

    private static void EnterCacheLock(object sync, ref bool taken)
    {
        var previous = EnterWork(Work.CacheLock);
        try { Monitor.Enter(sync, ref taken); }
        finally { EndWork(previous); }
    }
}
