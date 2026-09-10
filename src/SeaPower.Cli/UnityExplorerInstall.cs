using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SeaPower.Cli;

internal class UnityExplorerInstall(CliCommand command)
{
    private const string Plugin = "UnityExplorer.BIE5.Mono.dll";
    private const string Dependency = "UniverseLib.Mono.dll";
    private const string Folder = "sinai-dev-UnityExplorer";
    private const string Download = "https://github.com/sinai-dev/UnityExplorer/releases/download/4.9.0/UnityExplorer.BepInEx5.Mono.zip";
    private const string Sha256 = "0A125BDEEB22BC9763E7A69E74862BA60E61B4ED0F1A90CC3BFF0D7B51B09F00";
    private readonly string directory = GameDiagnostics.ResolveDirectory(command.GameDirectory);
    private string Plugins => Path.Combine(directory, "BepInEx", "plugins");

    internal async Task<JsonElement> ExecuteAsync(CancellationToken cancellation)
    {
        string operation = command.Request.Text;
        string[] found = FindPlugins();
        if (operation != "status")
        {
            if (!command.Request.Confirm) throw new ArgumentException("UnityExplorer changes require --confirm.");
            RequireClosed();
            if (!File.Exists(Path.Combine(directory, "Sea Power.exe")) || !File.Exists(Path.Combine(directory, "BepInEx", "core", "BepInEx.dll")))
                throw new InvalidOperationException("Sea Power with BepInEx 5 is required. Use --game-dir or SEA_POWER_DIR.");
            if (found.Length > 1) throw new InvalidOperationException("Multiple UnityExplorer copies found. Resolve them before changing the installation.");
            if (found.Length == 1 && Path.GetFileName(found[0]) is not (Plugin or Plugin + ".disabled"))
                throw new InvalidOperationException("Only UnityExplorer.BIE5.Mono.dll is supported. Leave other builds untouched.");
            if (operation == "install")
            {
                if (found.Length == 0) await InstallAsync(cancellation);
            }
            else
            {
                if (found.Length == 0) throw new InvalidOperationException("UnityExplorer is not installed. Run unity-explorer install --confirm first.");
                string source = found[0];
                bool disabled = source.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                string target = operation == "enable" ? disabled ? source[..^9] : source : disabled ? source : source + ".disabled";
                CheckPath(source);
                CheckPath(target);
                if (operation == "enable" && !File.Exists(Path.Combine(Path.GetDirectoryName(source)!, Dependency)))
                    throw new InvalidOperationException("UniverseLib.Mono.dll is missing beside UnityExplorer. Restore the dependency before enabling.");
                cancellation.ThrowIfCancellationRequested();
                RequireClosed();
                if (source != target) File.Move(source, target);
            }
            found = FindPlugins();
        }
        return JsonSerializer.SerializeToElement(new
        {
            ok = true, action = "unity-explorer", operation, gameDirectory = directory,
            installed = found.Length > 0, enabled = found.Any(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)),
            files = found, appliesOnNextLaunch = true,
        }, WireJson.Options);
    }

    private string[] FindPlugins() => Directory.Exists(Plugins)
        ? Directory.EnumerateFiles(Plugins, "UnityExplorer*.dll*", new EnumerationOptions
        {
            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false,
        }).Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase)).ToArray()
        : [];

    private async Task InstallAsync(CancellationToken cancellation)
    {
        string target = Path.Combine(Plugins, Folder);
        CheckPath(target);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException($"Installation destination already exists: {target}. No files were replaced.");
        using HttpClient client = new();
        byte[] bytes = await client.GetByteArrayAsync(Download, cancellation);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != Sha256) throw new InvalidDataException("UnityExplorer download checksum mismatch. Nothing installed.");
        using ZipArchive archive = new(new MemoryStream(bytes));
        string temporary = Path.Combine(directory, "BepInEx", $".unity-explorer-{Guid.NewGuid():N}");
        CheckPath(temporary);
        Directory.CreateDirectory(temporary);
        try
        {
            // Extract only these known entries, never archive-supplied paths.
            foreach (string name in new[] { Plugin, Dependency })
            {
                var entry = archive.GetEntry($"plugins/{Folder}/{name}") ?? throw new InvalidDataException($"Archive is missing {name}.");
                await using var input = entry.Open();
                await using var output = new FileStream(Path.Combine(temporary, name == Plugin ? name + ".disabled" : name), FileMode.CreateNew);
                await input.CopyToAsync(output, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            RequireClosed();
            if (FindPlugins().Length != 0) throw new IOException("UnityExplorer appeared during download. Nothing replaced.");
            Directory.CreateDirectory(Plugins);
            Directory.Move(temporary, target);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    private static void RequireClosed()
    {
        if (GameDiagnostics.ProcessIds().Length != 0) throw new InvalidOperationException("Close Sea Power before changing UnityExplorer. The CLI will not stop it.");
    }

    private static void CheckPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Refusing to modify a linked path: {current}");
    }
}
