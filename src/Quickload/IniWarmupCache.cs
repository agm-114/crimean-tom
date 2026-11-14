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
    private const int Format = 2;
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
    [ThreadStatic] private static (string Path, string[] Files)? copies;

    internal static Task<ConcurrentDictionary<string, Entry>?>? Prefetch(StartupArchives source) =>
        available ? source.Read(FileName, Format, Read) : null;

    internal static void Begin(StartupArchives? source, Task<ConcurrentDictionary<string, Entry>?>? prefetch)
    {
        if (source == null || prefetch == null || session != null) return;
        var entries = prefetch.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? prefetch.Result : null;
        session = new Session(source, entries);
    }

    internal static int Restored => session?.Restored ?? 0;

    internal static void Finish(bool loaded)
    {
        var active = session;
        session = null;
        if (loaded && active is { Entries: null } && !active.Added.IsEmpty)
            active.Source.Save(FileName, Format, active.Added, Write);
    }

    [HarmonyFinalizer, HarmonyPatch(typeof(IniHandler), nameof(IniHandler.open))]
    private static void ClearCopies() => copies = null;

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
        var expandCall = instructions[expand];
        AddLabel(discardMiss, useParser);
        AddLabel(instructions[expand + 1], publish);
        instructions[expand].operand = AccessTools.Method(typeof(IniWarmupCache), nameof(ExpandAndCapture));
        instructions[expand].opcode = OpCodes.Call;
        instructions[copies].operand = AccessTools.Method(typeof(IniWarmupCache), nameof(RegisterCopies));
        instructions.InsertRange(scan,
        [
            tryRestore,
            new(OpCodes.Ldarg_2),
            new(OpCodes.Ldarg_3),
            new(OpCodes.Ldarg_S, (byte)5),
            new(OpCodes.Call, AccessTools.Method(typeof(IniWarmupCache), nameof(Restore))),
            new(OpCodes.Dup),
            new(OpCodes.Brfalse, useParser),
            new(OpCodes.Stloc_2),
            new(OpCodes.Br, publish),
            discardMiss,
        ]);
        instructions.InsertRange(instructions.IndexOf(expandCall),
            [new(OpCodes.Ldarg_2), new(OpCodes.Ldarg_3), new(OpCodes.Ldarg_S, (byte)5)]);
        available = true;
        return instructions;
    }

    private static void AddLabel(CodeInstruction instruction, object label) => ((IList)labels.GetValue(instruction)).Add(label);

    private static IniData? Restore(IniHandler ini, bool useCaching, bool persistent, bool forceDisk)
    {
        if (!useCaching || persistent || forceDisk || session is not { Entries: { } entries } active
            || !entries.TryRemove(ini.getFilePath(), out var entry)) return null;
        scanModifiers();
        ini.aliasFiles.AddRange(entry.Aliases);
        if (entry.Copies.Length > 0) registerCopies(ini.getFilePath(), entry.Copies.ToList());
        Interlocked.Increment(ref active.Restored);
        return entry.Data;
    }

    private static void RegisterCopies(string path, List<string> files)
    {
        registerCopies(path, files);
        if (session is { Entries: null }) copies = (path, files.ToArray());
    }

    private static void ExpandAndCapture(IniHandler ini, IniData data, bool useCaching, bool persistent, bool forceDisk)
    {
        expandTemplates(ini, data);
        if (!useCaching || persistent || forceDisk || session is not { Entries: null } active || data.Count == 0) return;
        string path = ini.getFilePath();
        if (!active.Source.Includes(path) || active.Added.ContainsKey(path)) return;
        // Snapshot worker output before it becomes writable through the native cache.
        var sections = new IniData(data.Count, data.Comparer);
        foreach (var section in data) sections.Add(section.Key, new(section.Value, section.Value.Comparer));
        active.Added.TryAdd(path, new(ini.aliasFiles.ToArray(), copies is { } copied && copied.Path == path ? copied.Files : [], sections));
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
