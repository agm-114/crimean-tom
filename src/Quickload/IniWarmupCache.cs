using System.Collections;
using System.Collections.Concurrent;
using System.Reflection.Emit;
using HarmonyLib;
using SeaPower;
using Debug = UnityEngine.Debug;
using Task = System.Threading.Tasks.Task;
using IniData = System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>;

namespace Quickload;

[HarmonyPatch]
internal static class IniWarmupCache
{
    private const int Format = 1;
    private const string FileName = "ini-warmup.bin.gz";
    private static readonly Type handler = typeof(IniHandler);
    private static readonly Action scanModifiers = AccessTools.MethodDelegate<Action>(AccessTools.Method(handler, "ScanForModifierFiles"));
    private static readonly Action<string, List<string>> registerCopies =
        AccessTools.MethodDelegate<Action<string, List<string>>>(AccessTools.Method(handler, "RegisterCopySourceNames"));
    private static readonly Action<IniHandler, IniData> expandTemplates =
        AccessTools.MethodDelegate<Action<IniHandler, IniData>>(AccessTools.Method(handler, "ExpandTemplates"));
    // The installed Harmony assembly uses Mono's mscorlib for its label list.
    private static readonly System.Reflection.FieldInfo labels = AccessTools.Field(typeof(CodeInstruction), "labels");
    private static bool available;
    private static Session? session;
    [ThreadStatic] private static bool preloading;
    [ThreadStatic] private static string[]? copiedFiles;

    internal static Task<ConcurrentDictionary<string, Entry>?>? Prefetch(StartupArchives source) =>
        available ? source.Read(FileName, Format, Read) : null;

    internal static IEnumerator Wrap(IEnumerator native, StartupArchives? source, Task<ConcurrentDictionary<string, Entry>?>? prefetch)
    {
        if (source == null || prefetch == null)
        {
            yield return native;
            yield break;
        }
        // Prefer the native pass if the disk worker is still busy when warmup starts.
        var entries = prefetch.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? prefetch.Result : null;
        var active = new Session(source, entries);
        session = active;
        try { yield return native; }
        finally { session = null; }
        Debug.Log($"[Quickload] INI warmup snapshot restored {active.Restored:N0} files.");
        if (IniPreloader.Completed && active.Restored == 0 && !active.Added.IsEmpty)
            source.Save(FileName, Format, active.Added, Write);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(IniPreloader), "Open")]
    private static void Begin() => preloading = session != null;

    [HarmonyFinalizer, HarmonyPatch(typeof(IniPreloader), "Open")]
    private static void End()
    {
        preloading = false;
        copiedFiles = null;
    }

    [HarmonyTranspiler, HarmonyPatch(typeof(IniHandler), nameof(IniHandler.open))]
    private static IEnumerable<CodeInstruction> ReuseParsedData(IEnumerable<CodeInstruction> code, ILGenerator generator)
    {
        var instructions = code.ToList();
        int scan = instructions.FindIndex(instruction => instruction.Calls(AccessTools.Method(handler, "ScanForModifierFiles")));
        int expand = instructions.FindIndex(instruction => instruction.Calls(AccessTools.Method(handler, "ExpandTemplates")));
        int copies = instructions.FindIndex(instruction => instruction.Calls(AccessTools.Method(handler, "RegisterCopySourceNames")));
        if (scan < 0 || expand <= scan + 2 || copies <= scan || copies >= expand
            || instructions[expand - 2].opcode != OpCodes.Ldarg_0 || instructions[expand - 1].opcode != OpCodes.Ldloc_2)
            return instructions;

        // Both paths join at the native cache publication, after template expansion.
        var useParser = generator.DefineLabel();
        var publish = generator.DefineLabel();
        var tryRestore = new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instructions[scan]);
        var discardMiss = new CodeInstruction(OpCodes.Pop);
        AddLabel(discardMiss, useParser);
        AddLabel(instructions[expand + 1], publish);
        instructions[expand].operand = AccessTools.Method(typeof(IniWarmupCache), nameof(ExpandAndCapture));
        instructions[expand].opcode = OpCodes.Call;
        instructions[copies].operand = AccessTools.Method(typeof(IniWarmupCache), nameof(RegisterCopies));
        instructions.InsertRange(scan,
        [
            tryRestore,
            new(OpCodes.Call, AccessTools.Method(typeof(IniWarmupCache), nameof(Restore))),
            new(OpCodes.Dup),
            new(OpCodes.Brfalse, useParser),
            new(OpCodes.Stloc_2),
            new(OpCodes.Br, publish),
            discardMiss,
        ]);
        available = true;
        return instructions;
    }

    private static void AddLabel(CodeInstruction instruction, object label) => ((IList)labels.GetValue(instruction)).Add(label);

    private static IniData? Restore(IniHandler ini)
    {
        if (!preloading || session is not { Entries: { } entries } active
            || !entries.TryRemove(ini.getFilePath(), out var entry)) return null;
        scanModifiers();
        ini.aliasFiles.AddRange(entry.Aliases);
        if (entry.Copies.Length > 0) registerCopies(ini.getFilePath(), entry.Copies.ToList());
        Interlocked.Increment(ref active.Restored);
        return entry.Data;
    }

    private static void RegisterCopies(string path, List<string> copies)
    {
        registerCopies(path, copies);
        if (preloading) copiedFiles = copies.ToArray();
    }

    private static void ExpandAndCapture(IniHandler ini, IniData data)
    {
        expandTemplates(ini, data);
        if (!preloading || session is not { Entries: null } active || !active.Source.Includes(ini.getFilePath())
            || data.Count == 0) return;
        // Snapshot worker output before it becomes writable through the native cache.
        var sections = new IniData(data.Count, data.Comparer);
        foreach (var section in data) sections.Add(section.Key, new(section.Value, section.Value.Comparer));
        active.Added.TryAdd(ini.getFilePath(), new(ini.aliasFiles.ToArray(), copiedFiles ?? [], sections));
    }

    private static ConcurrentDictionary<string, Entry> Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        int count = ReadCount(reader);
        var result = new Dictionary<string, Entry>(count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            string path = reader.ReadString();
            string[] aliases = ReadStrings(reader);
            string[] copies = ReadStrings(reader);
            int sectionCount = ReadCount(reader);
            var sections = new IniData(sectionCount);
            for (int section = 0; section < sectionCount; section++)
            {
                string name = reader.ReadString();
                int keyCount = ReadCount(reader);
                var values = new Dictionary<string, string>(keyCount, FastIgnoreCaseStringComparer.Instance);
                for (int key = 0; key < keyCount; key++) values.Add(reader.ReadString(), reader.ReadString());
                sections.Add(name, values);
            }
            result.Add(path, new(aliases, copies, sections));
        }
        if (stream.ReadByte() != -1) throw new InvalidDataException("Unexpected data after the INI snapshot.");
        return new(result, StringComparer.OrdinalIgnoreCase);
    }

    private static void Write(Stream stream, ConcurrentDictionary<string, Entry> entries)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(entries.Count);
        foreach (var pair in entries)
        {
            writer.Write(pair.Key);
            WriteStrings(writer, pair.Value.Aliases);
            WriteStrings(writer, pair.Value.Copies);
            writer.Write(pair.Value.Data.Count);
            foreach (var section in pair.Value.Data)
            {
                writer.Write(section.Key);
                writer.Write(section.Value.Count);
                foreach (var value in section.Value)
                {
                    writer.Write(value.Key);
                    writer.Write(value.Value);
                }
            }
        }
    }

    private static int ReadCount(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        return count is >= 0 and <= 1_000_000 ? count : throw new InvalidDataException("Invalid INI snapshot count.");
    }

    private static string[] ReadStrings(BinaryReader reader)
    {
        var values = new string[ReadCount(reader)];
        for (int i = 0; i < values.Length; i++) values[i] = reader.ReadString();
        return values;
    }

    private static void WriteStrings(BinaryWriter writer, string[] values)
    {
        writer.Write(values.Length);
        foreach (string value in values) writer.Write(value);
    }

    internal record Entry(string[] Aliases, string[] Copies, IniData Data);

    private class Session(StartupArchives source, ConcurrentDictionary<string, Entry>? entries)
    {
        internal StartupArchives Source { get; } = source;
        internal ConcurrentDictionary<string, Entry>? Entries { get; } = entries;
        internal ConcurrentDictionary<string, Entry> Added { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal int Restored;
    }
}
