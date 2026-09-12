using System.Reflection.Emit;
using HarmonyLib;
using SeaPower;

namespace Quickload;

[HarmonyPatch(typeof(IniHandler), nameof(IniHandler.open))]
internal static class IniPathCache
{
    private const int Capacity = 8192;
    private static readonly Dictionary<(string Root, string Path), string> paths = [];

    internal static string RelativePath(string root, string path)
    {
        // Relative inputs depend on the process working directory. Leave those native.
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)
            || !Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(path))
            return Path.GetRelativePath(root, path);
        lock (paths)
        {
            if (paths.TryGetValue((root, path), out var cached)) return cached;
            string result = Path.GetRelativePath(root, path);
            if (paths.Count >= Capacity) paths.Clear();
            paths.Add((root, path), result);
            return result;
        }
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original = typeof(Path).GetMethod(nameof(Path.GetRelativePath), [typeof(string), typeof(string)])!;
        var replacement = typeof(IniPathCache).GetMethod(nameof(RelativePath), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var body = instructions.ToList();
        var calls = body.Where(instruction => instruction.opcode == OpCodes.Call && Equals(instruction.operand, original)).ToArray();
        // The supported native method has separate streaming-assets and persistent paths.
        if (calls.Length != 2)
        {
            Quickload.Log("INI path cache skipped: native path calls did not match.");
            return body;
        }
        foreach (var instruction in calls) instruction.operand = replacement;
        return body;
    }
}
