using System.Collections;
using System.Collections.ObjectModel;
using HarmonyLib;
using SeaPower;
using SeapowerUI.ViewModels;

namespace Quickload;

[HarmonyPatch]
internal static class ScenarioCatalogue
{
    internal class Entry
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Subfolder { get; set; } = "";
        public int Difficulty { get; set; }
        public List<Entry>? Children { get; set; }
        internal IMissionTreeViewItem Restore() => Children != null
            ? new MissionFolder { Name = Name, Description = Description, Missions = new(Children.Select(item => item.Restore())) }
            : new Mission { Name = Name, Description = Description, FileName = FileName, SubfolderName = Subfolder, Difficulty = Difficulty, PlayButtonEnabled = false };
        internal static Entry Capture(IMissionTreeViewItem item) => item switch
        {
            MissionFolder folder => new() { Name = folder.Name, Description = folder.Description, Children = folder.Missions.Select(Capture).ToList() },
            Mission mission => new() { Name = mission.Name, Description = mission.Description, FileName = mission.FileName, Subfolder = mission.SubfolderName, Difficulty = mission.Difficulty },
            _ => throw new InvalidOperationException("Unknown mission catalogue entry."),
        };
    }
    private class Scan(string path, string subfolder, ObservableCollection<IMissionTreeViewItem> items)
    {
        internal string Path { get; } = path;
        internal string Subfolder { get; } = subfolder;
        internal ObservableCollection<IMissionTreeViewItem> Items { get; } = items;
    }
    private static readonly Queue<Scan> pending = new();
    private static readonly HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
    private static ObservableCollection<IMissionTreeViewItem>? root;
    private static bool nativeCall;
    private static bool complete;
    private static bool failed;
    private static Mission? selected;
    internal static bool IsRefreshing => root != null && !complete;

    internal static void Initialize()
    {
        var cached = CatalogueStore.Read<List<Entry>>("scenarios");
        root = new(cached?.Select(item => item.Restore()) ?? []);
        pending.Enqueue(new("missions", "", root));
        visited.Add("missions");
    }

    [HarmonyPrefix, HarmonyPatch(typeof(ScenarioMenuViewModel), nameof(ScenarioMenuViewModel.GetMissions))]
    private static bool Browse(string path, string subfolder, string missionFilter, ref ObservableCollection<IMissionTreeViewItem> __result)
    {
        if (root == null || missionFilter != "Scenario") return true;
        if (nativeCall) { nativeCall = false; return true; }
        if (path == "missions") { __result = root; return false; }
        // Native recursion supplies folder visibility and localisation; scan each child on a later frame.
        __result = new();
        if (!complete && visited.Add(path)) pending.Enqueue(new(path, subfolder, __result));
        else return true;
        return false;
    }

    internal static IEnumerator Refresh()
    {
        while (pending.Count > 0)
        {
            yield return null;
            Scan scan = pending.Dequeue();
            try
            {
                nativeCall = true;
                var entries = ScenarioMenuViewModel.GetMissions(scan.Path, scan.Subfolder);
                scan.Items.Clear();
                foreach (var entry in entries)
                {
                    if (entry is Mission mission && !Quickload.Instance.CoreReady) mission.PlayButtonEnabled = false;
                    scan.Items.Add(entry);
                }
            }
            catch (Exception error) { failed = true; Quickload.Log($"Scenario catalogue refresh failed: {error}"); }
            finally { nativeCall = false; }
        }
        complete = true;
        if (!failed && root != null) CatalogueStore.Write("scenarios", root.Select(Entry.Capture).ToList());
    }

    [HarmonyPrefix, HarmonyPatch(typeof(ScenarioMenuViewModel), "TryLoadBriefing")]
    private static bool Select(Mission selectedMission)
    {
        selected = selectedMission;
        // Read the selected file now, before the background catalogue reaches its directory.
        return Validate(selectedMission);
    }

    internal static bool Validate(Mission mission)
    {
        mission.PlayButtonEnabled = false;
        try
        {
            if (!File.Exists(mission.FileName) || !ActivePath(mission.FileName))
            {
                mission.Description = "This scenario is no longer available.";
                return false;
            }
            var ini = Utils.openIniFile(mission.FileName, logErrors: false, useCaching: false);
            if (ini == null || ini.readValue("General", "Hidden", false) || ini.readValue("General", "Type", "Scenario") != "Scenario") return false;
            string language = "Language_" + Singleton<LanguageResourceHandler>.Instance.Language;
            mission.Name = ini.readValue(language, "Name", ini.readValue("Language_en", "Name", Path.GetFileNameWithoutExtension(mission.FileName)));
            mission.Description = SceneCreator.FormatText(ini.readValue(language, "Description", ini.readValue("Language_en", "Description", "")));
            mission.Difficulty = ini.readValue("Mission", "Difficulty", 1) * 20;
            mission.Url = ini.readValue("General", "Url", "");
            mission.RightPane = ini.readValue("General", "RightPane", "");
            mission.PlayButtonEnabled = Quickload.Instance.CoreReady && ini.readValue("General", "PlayButtonEnabled", true);
            return true;
        }
        catch (Exception error) { Quickload.Log($"Scenario validation failed: {error.Message}"); return false; }
    }
    internal static void Ready() { if (selected != null) Validate(selected); }
    internal static void ValidateLaunch(string path)
    {
        if (selected == null || !string.Equals(selected.FileName, path, StringComparison.OrdinalIgnoreCase)) return;
        if (!Validate(selected) || !selected.PlayButtonEnabled)
            throw new InvalidOperationException("The selected scenario is unavailable or its required data is still loading.");
    }
    internal static bool ActivePath(string path)
    {
        string full = Path.GetFullPath(path);
        foreach (var item in Singleton<FileManager>.Instance.Directories.Where(item => item.IsChecked))
        {
            string prefix = item.DirectoryInfo.FullName.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string current = Singleton<FileManager>.Instance.GetFile(full[prefix.Length..].Replace('\\', '/'));
            return !string.IsNullOrEmpty(current) && string.Equals(Path.GetFullPath(current), full, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
}
