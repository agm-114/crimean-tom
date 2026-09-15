using System.Globalization;

namespace SeaPower.Workshop;

// Shared with the CLI so invalid local targets fail before contacting the game.
internal class WorkshopManifest
{
    internal const uint AppId = 1286220;
    internal string Directory { get; }
    internal string Title { get; }
    internal string Name { get; }
    internal string Description { get; }
    internal ulong ItemId { get; }
    internal string Url => $"https://steamcommunity.com/sharedfiles/filedetails/?id={ItemId}";

    internal WorkshopManifest(string directory)
    {
        Directory = Path.GetFullPath(directory);
        string info = Path.Combine(Directory, "_info.ini");
        if (!File.Exists(info)) throw new ArgumentException("--mod-dir must contain the packaged mod's _info.ini.");
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> listing = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in new[] { info, Path.Combine(Directory, "mod.info") }.Where(File.Exists))
        {
            string section = "";
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2).Trim(); continue; }
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                int separator = line.IndexOf('=');
                if (separator < 0) continue;
                string key = line.Substring(0, separator).Trim(), value = line.Substring(separator + 1).Trim();
                if (section.Equals("Language_en", StringComparison.OrdinalIgnoreCase) && path == info)
                    Store(listing, key, value, section);
                if (!section.Equals("Workshop", StringComparison.OrdinalIgnoreCase)) continue;
                Store(values, key, value, section);
            }
        }
        if (!values.TryGetValue("Url", out string? url))
            throw new ArgumentException("Add [Workshop] Url=<existing Steam Workshop URL> to _info.ini or mod.info. Updates never create an item.");
        ItemId = ParseUrl(url);
        Name = listing.TryGetValue("Name", out string? name) ? name : "";
        if (string.IsNullOrWhiteSpace(Name) || Name.Contains('\0'))
            throw new ArgumentException("Set a nonempty Language_en/Name in _info.ini to publish.");
        if (!listing.TryGetValue("Description", out string? description) || description.Contains('\0'))
            throw new ArgumentException("Set Language_en/Description in _info.ini. An explicit empty value clears the Steam description.");
        Description = description.Replace("\\n", "\n").Replace("\\r", "\r");
        Title = values.TryGetValue("Title", out string? title) ? title : Name;
        if (string.IsNullOrWhiteSpace(Title)) throw new ArgumentException("Set Workshop/Title or Language_en/Name to the existing Workshop title.");
        if (values.TryGetValue("AppId", out string? app) && app != AppId.ToString(CultureInfo.InvariantCulture))
            throw new ArgumentException("Workshop/AppId must be 1286220 for Sea Power.");
        if (values.TryGetValue("PublishedFileId", out string? id) && id != ItemId.ToString(CultureInfo.InvariantCulture))
            throw new ArgumentException("Workshop/PublishedFileId conflicts with Workshop/Url.");
    }

    private static void Store(Dictionary<string, string> values, string key, string value, string section)
    {
        if (values.TryGetValue(key, out string? previous) && previous != value)
            throw new ArgumentException($"Conflicting {section}/{key} values in mod metadata.");
        values[key] = value;
    }

    internal static ulong ParseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != "https"
            || !url.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase)
            || !url.IsDefaultPort || url.UserInfo.Length > 0 || url.Fragment.Length > 0
            || url.AbsolutePath.TrimEnd('/') != "/sharedfiles/filedetails")
            throw new ArgumentException("Workshop/Url must be https://steamcommunity.com/sharedfiles/filedetails/?id=<item ID>.");
        var ids = url.Query.TrimStart('?').Split('&').Select(pair => pair.Split(new[] { '=' }, 2))
            .Where(pair => Uri.UnescapeDataString(pair[0]).Equals("id", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (ids.Length != 1 || ids[0].Length != 2
            || !ulong.TryParse(ids[0][1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || id == 0)
            throw new ArgumentException("Workshop/Url requires exactly one nonzero numeric id.");
        return id;
    }

    internal void ValidateTarget(ulong itemId, uint appId, ulong owner, ulong account, string title)
    {
        if (itemId != ItemId || appId != AppId)
            throw new InvalidOperationException("URL does not identify the expected Sea Power Workshop item.");
        if (owner == 0 || owner != account)
            throw new InvalidOperationException("Workshop item belongs to a different Steam account.");
        // Accept the desired name too, so a successful rename does not break subsequent updates.
        if (!string.Equals(title.Trim(), Title.Trim(), StringComparison.Ordinal)
            && !string.Equals(title.Trim(), Name.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException($"Workshop title '{title}' does not match local expected title '{Title}'. Check the URL and Workshop/Title.");
    }

    internal FileInfo[] ContentFiles()
    {
        List<FileInfo> files = [];
        Visit(new DirectoryInfo(Directory));
        if (!files.Any(file => file.Name is not ("_info.ini" or "mod.info" or "thumbnail.jpg")))
            throw new ArgumentException("Mod folder contains no payload to upload.");
        return files.ToArray();

        void Visit(DirectoryInfo folder)
        {
            if ((folder.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException($"Upload folders cannot contain directory links: {folder.FullName}");
            foreach (var entry in folder.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException($"Upload folders cannot contain links: {entry.FullName}");
                if (entry is DirectoryInfo child)
                {
                    if (child.Name is ".git" or "bin" or "obj")
                        throw new ArgumentException("Use a packaged mod folder, not a source/build directory.");
                    Visit(child);
                }
                else if (entry is FileInfo file) files.Add(file);
            }
        }
    }
}
