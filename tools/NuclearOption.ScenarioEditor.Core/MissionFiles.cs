using System.Text;
using System.Text.Json.Nodes;

namespace NuclearOption.ScenarioEditor.Core;

public record MissionFile(string Name, string Path)
{
    public override string ToString() => Name;
}

public static class MissionFiles
{
    public static string MissionsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "LocalLow", "Shockfront", "NuclearOption", "Missions");

    public static IReadOnlyList<MissionFile> Discover(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.json", new EnumerationOptions
            { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(p => !Path.GetFileName(p).Equals("meta.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => new MissionFile(Path.GetFileNameWithoutExtension(p), p)).ToList();
    }

    public static string DefaultOutput(string source)
    {
        var name = Path.GetFileNameWithoutExtension(source) + " - Edited";
        return Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(source)))!, name, name + ".json");
    }

    public static void ValidateOutput(string source, string output)
    {
        if (!Path.IsPathFullyQualified(output) || !Path.GetExtension(output).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an absolute output path ending in .json.");
        var sourceDirectory = Resolve(Path.GetDirectoryName(Path.GetFullPath(source))!);
        var destination = Resolve(output);
        if (destination.Equals(Resolve(source), StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output must be outside the source mission folder. The source is read-only.");
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(output))!;
        if (!Path.GetFileNameWithoutExtension(output).Equals(Path.GetFileName(outputDirectory), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The mission folder and JSON filename must match. Choose a folder and the editor will name its JSON.");
    }

    public static string Save(string source, string output, MissionDocument document)
    {
        ValidateOutput(source, output);
        var directory = Path.GetDirectoryName(output)!;
        var metadata = Path.Combine(Path.GetDirectoryName(source)!, "meta.json");
        var outputMetadata = Path.Combine(directory, "meta.json");
        JsonObject? copiedMetadata = null;
        if (File.Exists(metadata) && !File.Exists(outputMetadata))
        {
            copiedMetadata = JsonNode.Parse(File.ReadAllText(metadata)) as JsonObject
                ?? throw new System.Text.Json.JsonException("The source meta.json must be an object.");
            copiedMetadata["FileName"] = Path.GetFileNameWithoutExtension(output);
        }
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        var backup = "";
        try
        {
            File.WriteAllText(temporary, document.Serialize(), new UTF8Encoding(false));
            if (File.Exists(output))
            {
                backup = output + $".{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.bak";
                File.Replace(temporary, output, backup);
            }
            else
                File.Move(temporary, output);
            // Only seed metadata. Never overwrite an existing output mission's metadata.
            if (copiedMetadata is not null)
                File.WriteAllText(outputMetadata, copiedMetadata.ToJsonString(), new UTF8Encoding(false));
            document.MarkSaved();
            return backup;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // Resolve directory junctions and symlinks before applying source protection.
    private static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            root = Path.Combine(root, part);
            FileSystemInfo info = Directory.Exists(root) ? new DirectoryInfo(root) : new FileInfo(root);
            if (info.Exists && info.LinkTarget is not null) root = info.ResolveLinkTarget(true)!.FullName;
        }
        return root;
    }
}
