using System.Collections;
using System.Collections.Concurrent;
using System.Reflection.Emit;
using HarmonyLib;
using SeaPower;
using Debug = UnityEngine.Debug;
using System.Threading.Tasks;
using IniData = System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>;

namespace Quickload;

[HarmonyPatch]
internal static class IniWarmupCache
{
    private const int Format = 3;
    private const string FileName = "ini-warmup.bin.gz";
    private const int SegmentFormat = 2;
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

    internal static Task<Prepared>? Prefetch(StartupArchives source) => available ? ReadStartup(source) : null;

    private static async Task<Prepared> ReadStartup(StartupArchives source)
    {
        var full = await source.Read(FileName, Format, SnapshotReader.Read, decoded: SnapshotReader.Take()).ConfigureAwait(false);
        if (full != null) return new(full, null, null);
        var inputTask = source.Inputs;
        var segments = await StartupSegments.Read(source, "inis.bin.gz", SegmentFormat, SnapshotReader.ReadSegments).ConfigureAwait(false);
        return new(null, await inputTask.ConfigureAwait(false), segments);
    }

    internal static void Begin(StartupArchives? source, Task<Prepared>? prefetch)
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
        if (!loaded || active == null || active.Full != null || active.Added.IsEmpty) return;
        Debug.Log($"[Quickload] Reused {active.Segmented:N0} parsed INIs from layer snapshots.");
        active.Source.Save(FileName, Format, active.Added, (stream, entries) => Write(stream, entries, active.Source));
        active.Source.Track(System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var inputs = await active.Source.Inputs.ConfigureAwait(false);
                var reusable = active.Added
                    .Where(pair => inputs.Files.TryGetValue(StartupArchives.Normalize(pair.Key), out var file) &&
                        !file.HasIncludes && pair.Value.Unmodified)
                    .ToDictionary(pair => active.Source.PortablePath(pair.Key), pair => new Segment(inputs.Files[StartupArchives.Normalize(pair.Key)].Hash,
                        pair.Value), StringComparer.OrdinalIgnoreCase);
                StartupSegments.Save(active.Source, "inis.bin.gz", SegmentFormat, reusable,
                    pair => active.Source.LayerFor(active.Source.LocalPath(pair.Key)), SnapshotReader.ReadSegments, WriteSegments);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Debug.LogWarning($"[Quickload] Could not segment parsed INIs: {error.Message}");
            }
        }));
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
        if (!useCaching || persistent || forceDisk || session is not { } active) return null;
        string path = ini.getFilePath();
        Entry entry;
        if (active.Full != null)
        {
            if (!active.Full.TryRemove(active.Source.PortablePath(path), out entry)) return null;
            scanModifiers();
        }
        else
        {
            string normalized = StartupArchives.Normalize(path);
            if (active.Inputs == null || active.Segments == null || !active.Segments.TryGetValue(active.Source.PortablePath(path), out var saved) ||
                !active.Inputs.Files.TryGetValue(normalized, out var file) || file.Hash != saved.Hash ||
                !HasNoModifiers(path)) return null;
            entry = saved.Value;
            active.Added.TryAdd(path, entry);
            entry = entry with { Data = Clone(entry.Data) };
            Interlocked.Increment(ref active.Segmented);
        }
        ini.aliasFiles.AddRange(entry.Aliases.Select(active.Source.LocalPath));
        if (entry.Copies.Length > 0) registerCopies(ini.getFilePath(), entry.Copies.Select(active.Source.LocalPath).ToList());
        Interlocked.Increment(ref active.Restored);
        entry.PriceHash?.Register(path);
        return entry.Data;
    }

    private static void RegisterCopies(string path, List<string> files)
    {
        registerCopies(path, files);
        if (session is { Full: null }) copies = (path, files.ToArray());
    }

    private static void ExpandAndCapture(IniHandler ini, IniData data, bool useCaching, bool persistent, bool forceDisk)
    {
        expandTemplates(ini, data);
        if (!useCaching || persistent || forceDisk || session is not { Full: null } active || data.Count == 0) return;
        string path = ini.getFilePath();
        if (!active.Source.Includes(path) || active.Added.ContainsKey(path) ||
            ini.aliasFiles.Any(alias => Path.IsPathRooted(alias) && !active.Source.Includes(alias)) ||
            copies is { } source && source.Path == path && source.Files.Any(file => !active.Source.Includes(file))) return;
        // Snapshot worker output before it becomes writable through the native cache.
        active.Added.TryAdd(path, new(ini.aliasFiles.ToArray(), copies is { } copied && copied.Path == path ? copied.Files : [],
            Clone(data), HasNoModifiers(path)));
    }

    private static bool HasNoModifiers(string path)
    {
        IniHandler.GetModifierFilePaths(path, out var extend, out var prepend);
        return extend.Count == 0 && prepend.Count == 0;
    }

    private static IniData Clone(IniData data)
    {
        var sections = new IniData(data.Count, data.Comparer);
        foreach (var section in data) sections.Add(section.Key, new(section.Value, section.Value.Comparer));
        return sections;
    }

    // The splash worker can decode INI data without initializing the Harmony fields on the outer type.
    internal static class SnapshotReader
    {
        private static Task<StartupArchives.Decoded<ConcurrentDictionary<string, Entry>>?>? early;
        internal static void Start() => early = StartupArchives.DecodeDuringStartup(FileName, Format, Read);
        internal static Task<StartupArchives.Decoded<ConcurrentDictionary<string, Entry>>?>? Take() =>
            Interlocked.Exchange(ref early, null);

        internal static ConcurrentDictionary<string, Entry> Read(Stream stream) =>
            new(ReadEntries(stream, segmented: false).ToDictionary(pair => pair.Key, pair => pair.Value.Value),
                StringComparer.OrdinalIgnoreCase);

        internal static Dictionary<string, Segment> ReadSegments(Stream stream) => ReadEntries(stream, segmented: true);

        private static Dictionary<string, Segment> ReadEntries(Stream stream, bool segmented)
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            int count = ReadCount(reader);
            var result = new Dictionary<string, Segment>(count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                string path = reader.ReadString();
                string hash = segmented ? reader.ReadString() : "";
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
                result.Add(path, new(hash, new(aliases, copies, sections, segmented,
                    segmented ? null : PreparedPriceHash.FromSnapshot(path, sections))));
            }
            if (stream.ReadByte() != -1) throw new InvalidDataException("Unexpected data after the INI snapshot.");
            return result;
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
    }

    private static void Write(Stream stream, ConcurrentDictionary<string, Entry> entries, StartupArchives source)
        => WriteEntries(stream, entries.Count, entries.Select(pair => (source.PortablePath(pair.Key), (string?)null, pair.Value with
        {
            Aliases = pair.Value.Aliases.Select(source.PortablePath).ToArray(),
            Copies = pair.Value.Copies.Select(source.PortablePath).ToArray(),
        })));

    private static void WriteSegments(Stream stream, Dictionary<string, Segment> entries)
        => WriteEntries(stream, entries.Count, entries.Select(pair => (pair.Key, (string?)pair.Value.Hash, pair.Value.Value)));

    private static void WriteEntries(Stream stream, int count, IEnumerable<(string Path, string? Hash, Entry Value)> entries)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(count);
        foreach (var pair in entries)
        {
            writer.Write(pair.Path);
            if (pair.Hash != null) writer.Write(pair.Hash);
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

    private static void WriteStrings(BinaryWriter writer, string[] values)
    {
        writer.Write(values.Length);
        foreach (string value in values) writer.Write(value);
    }

    internal record Entry(string[] Aliases, string[] Copies, IniData Data, bool Unmodified = false, PreparedPriceHash? PriceHash = null);
    internal record Segment(string Hash, Entry Value);
    internal record Prepared(ConcurrentDictionary<string, Entry>? Full, StartupArchives.InputIndex? Inputs,
        Dictionary<string, Segment>? Segments);

    private class Session(StartupArchives source, Prepared? prepared)
    {
        internal StartupArchives Source { get; } = source;
        internal ConcurrentDictionary<string, Entry>? Full { get; } = prepared?.Full;
        internal StartupArchives.InputIndex? Inputs { get; } = prepared?.Inputs;
        internal Dictionary<string, Segment>? Segments { get; } = prepared?.Segments;
        internal ConcurrentDictionary<string, Entry> Added { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal int Restored;
        internal int Segmented;
    }
}
