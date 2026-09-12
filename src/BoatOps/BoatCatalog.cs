using SeaPower;
using UnityEngine;

namespace BoatOps;

internal class CraftDefinition(string folder, string reference, int count, int space)
{
    internal string Folder { get; } = folder;
    internal string Reference { get; } = reference;
    internal int Count { get; } = count;
    internal int Space { get; } = space;
    internal string Key => $"{Folder}/{Reference}";
}

internal class DeckDefinition(int capacity, int storageCapacity, List<CraftDefinition> craft, int preparationSeconds, int crews)
{
    internal int Capacity { get; } = capacity;
    internal int StorageCapacity { get; } = storageCapacity;
    internal List<CraftDefinition> Craft { get; } = craft;
    internal int PreparationSeconds { get; } = preparationSeconds;
    internal int Crews { get; } = crews;
}

internal static class BoatCatalog
{
    internal static readonly Dictionary<string, DeckDefinition> Decks = new(StringComparer.OrdinalIgnoreCase);
    internal static bool Load()
    {
        try
        {
            Decks.Clear();
            string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "boat-ops.ini");
            var ini = Utils.openIniFile(path, logErrors: false, useCaching: false)
                ?? throw new IOException($"Missing {path}");
            foreach (string section in ini.getNamesOfAllSections())
            {
                int capacity = ini.readValue(section, "StagingCapacity", 4);
                int preparation = ini.readValue(section, "PreparationSeconds", 90);
                int crews = ini.readValue(section, "Crews", 1);
                if (crews is < 1 or > 128) throw new FormatException($"Invalid crew count in {section}");
                if (preparation is < 1 or > 3600) throw new FormatException($"Invalid preparation time in {section}");
                if (capacity is < 1 or > 16384) throw new FormatException($"Invalid staging capacity in {section}");
                List<CraftDefinition> craft = [];
                foreach (var entry in ini.GetSectionKeyValues(section))
                {
                    if (entry.Key is "StagingCapacity" or "StorageCapacity" or "PreparationSeconds" or "Crews") continue;
                    string[] parts = entry.Key.Split('/');
                    string[] values = entry.Value.Split(',');
                    int space = 1;
                    if (parts.Length != 2 || !(parts[0] == "vessels"
                        || parts[0] == "land_units" && parts[1].StartsWith("amphibs_", StringComparison.Ordinal))
                        || string.IsNullOrWhiteSpace(parts[1]) || parts[1].Contains("\\") || parts[1].Contains("..")
                        || values.Length is < 1 or > 2 || !int.TryParse(values[0], out int count) || count is < 1 or > 128
                        || values.Length == 2 && !int.TryParse(values[1], out space) || space < 1 || space > capacity)
                        throw new FormatException($"Invalid craft entry {section}/{entry.Key}");
                    craft.Add(new(parts[0], parts[1], count, space));
                }
                if (craft.Count == 0 || craft.Sum(item => item.Count) > 128)
                    throw new FormatException($"Invalid complement in {section}");
                int occupied = craft.Sum(item => item.Count * item.Space);
                int storage = ini.readValue(section, "StorageCapacity", occupied);
                if (storage < occupied || storage > 16384) throw new FormatException($"Complement exceeds storage capacity in {section}");
                Decks.Add(section, new(capacity, storage, craft, preparation, crews));
            }
            return Decks.Count > 0;
        }
        catch (Exception error)
        {
            Decks.Clear();
            Debug.LogError($"[Boat Ops] Configuration rejected: {error.Message}");
            return false;
        }
    }
}

internal class DeckInventory(DeckDefinition definition)
{
    internal string Id { get; set; } = Guid.NewGuid().ToString("N");
    internal DeckDefinition Definition { get; } = definition;
    internal List<BoatPreparation> Tasks { get; } = [];
    internal float LastUpdate { get; set; } = GameTime.missionSessionTime;
    internal string Message { get; set; } = "";
    internal int OccupiedSpace => Definition.Craft.Sum(craft => Aboard[craft.Key] * craft.Space);
    internal int StagingSpace => Tasks.Where(task => task.Remaining <= 0 || task.Active).Sum(task => task.Craft.Space);
    internal int BusyCrews => Tasks.Count(task => task.Active && task.Remaining > 0);
    internal Dictionary<string, int> Aboard { get; } = definition.Craft.ToDictionary(craft => craft.Key, craft => craft.Count);
    internal int Count(CraftDefinition craft) => Tasks.Count(task => task.Craft == craft && task.Remaining <= 0);
    internal int Reserved(CraftDefinition craft) => Tasks.Count(task => task.Craft == craft);
    internal void Queue(CraftDefinition craft, int count, bool launch)
    {
        if (launch)
            foreach (var task in Tasks.Where(task => task.Craft == craft && !task.AutoLaunch).OrderBy(task => task.Remaining))
            {
                if (count-- <= 0) return;
                task.AutoLaunch = true;
            }
        for (int i = 0; i < count && Reserved(craft) < Aboard[craft.Key]; i++)
            Tasks.Add(new(craft, Definition.PreparationSeconds, launch));
    }
    internal void Return(CraftDefinition craft)
    {
        var task = Tasks.FirstOrDefault(task => task.Craft == craft && task.Remaining <= 0);
        if (task != null) Tasks.Remove(task);
    }
    internal void Advance(float seconds)
    {
        int spaces = Definition.Capacity - Tasks.Where(task => task.Remaining <= 0).Sum(task => task.Craft.Space);
        int crews = Definition.Crews;
        foreach (var task in Tasks.Where(task => task.Remaining > 0))
        {
            task.WaitingForCrew = task.Craft.Space <= spaces && crews == 0;
            task.Active = task.Craft.Space <= spaces && crews > 0;
            if (!task.Active) continue;
            crews--;
            spaces -= task.Craft.Space;
            task.Remaining = Math.Max(0, task.Remaining - seconds);
        }
    }
}

internal class BoatPreparation(CraftDefinition craft, float remaining, bool launch)
{
    internal CraftDefinition Craft { get; } = craft;
    internal float Remaining { get; set; } = remaining;
    internal bool AutoLaunch { get; set; } = launch;
    internal bool Active { get; set; }
    internal bool WaitingForCrew { get; set; }
    internal string State => Remaining <= 0 ? "Ready to Launch" : Active ? "Launch Prep" : WaitingForCrew ? "Awaiting Crew" : "Awaiting Deck Space";
    internal string Info => Remaining > 0 ? $"{TimeSpan.FromSeconds(Math.Ceiling(Remaining)):m\\:ss} remaining" + (AutoLaunch ? "; auto-launch" : "")
        : AutoLaunch ? "Waiting to launch" : "";
}
