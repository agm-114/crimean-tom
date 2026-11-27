using System.Collections.Concurrent;
using HarmonyLib;
using SeaPower;
using IniData = System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>;

namespace Quickload;

[HarmonyPatch(typeof(PointValueCache), nameof(PointValueCache.HashIni))]
internal class PreparedPriceHash
{
    private static readonly ConcurrentDictionary<string, PreparedPriceHash> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly (string Name, KeyValuePair<string, string>[] Values)[] sections;
    private readonly string hash;
    private static int hits;

    private PreparedPriceHash(IniData data)
    {
        sections = data.Select(section => (section.Key, section.Value.ToArray())).ToArray();
        // Match Sea Power's ordinal sort, UTF-16 code units and newline after each string.
        ulong digest = 14695981039346656037UL;
        foreach (var (name, values) in sections.OrderBy(section => section.Name, StringComparer.Ordinal))
        {
            Append(name);
            foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Append(key);
                Append(value);
            }
        }
        hash = digest.ToString("x16");

        void Append(string value)
        {
            if (value == null) return;
            foreach (char c in value) digest = unchecked((digest ^ c) * 1099511628211UL);
            digest = unchecked((digest ^ '\n') * 1099511628211UL);
        }
    }

    internal static PreparedPriceHash? FromSnapshot(string path, IniData data) =>
        Path.GetFileName(Path.GetDirectoryName(path))?.ToLowerInvariant() is "vessels" or "aircraft" or "land_units" or "submarines"
            ? new(data) : null;

    internal void Register(string path) => pending[path] = this;

    private static bool Prefix(IniHandler ini, ref string __result)
    {
        if (ini?.Data == null || ini.getFilePath() is not { } path ||
            !pending.TryRemove(path, out var saved) || !saved.Matches(ini.Data)) return true;
        __result = saved.hash;
        Interlocked.Increment(ref hits);
        return false;
    }

    private bool Matches(IniData data)
    {
        if (data.Count != sections.Length) return false;
        int section = 0;
        foreach (var (name, values) in data)
        {
            var saved = sections[section++];
            if (name != saved.Name || values.Count != saved.Values.Length) return false;
            int index = 0;
            foreach (var (key, value) in values)
            {
                var pair = saved.Values[index++];
                if (key != pair.Key || value != pair.Value) return false;
            }
        }
        return true;
    }

    internal static int Clear()
    {
        pending.Clear();
        return Interlocked.Exchange(ref hits, 0);
    }
}
