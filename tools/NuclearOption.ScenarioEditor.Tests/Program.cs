using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NuclearOption.ScenarioEditor.Core;

var checks = 0;
Check(UnitCatalog.All.Count == 138 && UnitCatalog.All.All(u => u.Id.Length > 0 && u.Name.Length > 0), "Every catalogue entry has an ID and game name");
Check(UnitCatalog.Name("6x6_1_AA") == "AFV6 AA", "AFV6 uses the actual in-game name");
Check(!UnitCatalog.Get("6x6_1_AA").Disabled && !UnitCatalog.Get("6x6_1_AA").EventOnly, "Omitted availability flags default to false");
Check(UnitCatalog.Name("MBT") == "Type-12 MBT" && UnitCatalog.Name("MBT1") == "Spearhead MBT", "Tank IDs map to the correct distinct names");
Check(UnitCatalog.Name("RadarSAM1") == "T9K41 Boltstrike", "Air defence name mapping");
Check(UnitCatalog.ResolveId("AFV6 AA") == "6x6_1_AA" && UnitCatalog.ResolveId("AFV6 AA [6x6_1_AA]") == "6x6_1_AA", "Friendly names and selector labels resolve to JSON IDs");
Check(UnitCatalog.ResolveId("NewModId") == "NewModId" && UnitCatalog.Name("NewModId") == "NewModId", "Unknown IDs remain editable and visible");
Check(UnitCatalog.Options(["RadarSAM1", "6x6_1_AA"]).First().Id == "6x6_1_AA", "Selectors sort by game name");
Check(!UnitCatalog.Get("UFO").IsReplacement && !UnitCatalog.Get("SAMTurret1").IsReplacement, "Event and disabled prototypes excluded from default suggestions");
var fixture = """
{
  "JsonVersion": 6,
  "missionSettings": { "description": "Tank must remain in this text", "future": { "n": 1234567890123456789 } },
  "vehicles": [
    { "type": "Tank", "UniqueName": "Tank", "faction": "Boscali", "waypoints": [{ "objective": "Tank" }], "rotation": { "w": 1.0 } },
    { "type": "Tank", "UniqueName": "Tank2", "faction": "Primeva", "globalPosition": { "x": 123.000000000001 } },
    { "type": "TankPlus", "UniqueName": "Keep" }
  ],
  "aircraft": [{ "type": "Jet", "UniqueName": "Jet1" }],
  "ships": [{ "type": "Ship", "UniqueName": "Ship1" }],
  "buildings": [
    { "type": "factory_large", "UniqueName": "Factory1", "faction": "Boscali", "factoryOptions": { "productionType": "Tank", "productionTime": 12.5, "unknown": true } },
    { "type": "factory_tall", "UniqueName": "Factory2", "faction": "Primeva", "factoryOptions": { "productionType": "", "productionTime": 20 } },
    { "type": "radarStation1", "factoryOptions": { "productionType": "", "productionTime": 900 } },
    { "type": "enrichmentPlant1", "UniqueName": "Nukes", "factoryOptions": { "productionType": "Nuclear Warhead", "productionTime": 900 } }
  ],
  "factions": [{ "factionName": "Boscali", "supplies": [{ "UnitType": "Tank", "Count": 17 }] }],
  "outcomes": [{ "Type": "SpawnUnit", "UnitsToSpawn": ["Tank", "Tank2"] }],
  "unknownArray": [null, true, 0.0000000000000000123, "Tank"]
}
""";

var mission = new MissionDocument(fixture);
Check(!mission.IsDirty && mission.Factories.Count == 3, "Initial document and production building detection");
Check(mission.PreviewReplacement("Tank", "IFV").Count == 4 && !mission.CanUndo, "Preview has no side effects");
Check(mission.ReplaceGlobally("Tank", "IFV") == 4, "Global replacement covers both factions, production and reserves");
var expected = JsonNode.Parse(fixture)!;
expected["vehicles"]![0]!["type"] = "IFV";
expected["vehicles"]![1]!["type"] = "IFV";
expected["buildings"]![0]!["factoryOptions"]!["productionType"] = "IFV";
expected["factions"]![0]!["supplies"]![0]!["UnitType"] = "IFV";
Check(JsonNode.DeepEquals(expected, JsonNode.Parse(mission.Serialize())), "Only exact type fields change; references, coordinates, unknown data and reserve quantity survive");
Check(mission.IsDirty && mission.Edits.Count == 4, "Dirty state and review entries");
Check(mission.ReplaceGlobally("Missing", "IFV") == 0 && mission.ReplaceGlobally("IFV", "IFV") == 0, "No-op substitutions");
mission.Undo();
Check(!mission.IsDirty && !mission.CanUndo && mission.Edits.Count == 0, "Undo restores the complete operation");
Check(mission.SetFactories([mission.Factories[1]], "Jet", 30) == 2, "Selected factory settings");
Check(mission.Factories[0].Product == "Tank" && mission.Factories[1].Product == "Jet" && mission.Factories[1].Seconds == 30, "Unselected factories stay intact");
mission.MarkSaved();
Check(!mission.IsDirty, "Saving establishes a clean point");
mission.Undo();
Check(mission.IsDirty, "Undo after saving requires saving again");
mission.SetFactories([mission.Factories[0]], "", null);
Check(mission.Factories[0].Product == "" && mission.Factories[0].Seconds == 12.5, "Clear production without changing interval");
Throws<ArgumentException>(() => mission.SetFactories(mission.Factories, null, 0), "Zero production time rejected");
Throws<ArgumentException>(() => mission.SetFactories(mission.Factories, null, double.NaN), "NaN production time rejected");
Throws<ArgumentException>(() => mission.SetFactories(mission.Factories, null, double.PositiveInfinity), "Infinite production time rejected");
Throws<ArgumentException>(() => mission.ReplaceGlobally("Tank", " bad "), "Accidental whitespace rejected");
Throws<JsonException>(() => new MissionDocument("{\"FileName\":\"meta\"}"), "Metadata rejected as a mission");
Throws<JsonException>(() => new MissionDocument("[]"), "Invalid root rejected");
Throws<JsonException>(() => new MissionDocument("{\"JsonVersion\":6,\"missionSettings\":{},\"vehicles\":{}}"), "Malformed unit arrays rejected");

var temporary = Path.Combine(AppContext.BaseDirectory, "test-runs", Guid.NewGuid().ToString("N"));
try
{
    var sourceFolder = Path.Combine(temporary, "Source [test]");
    Directory.CreateDirectory(sourceFolder);
    var source = Path.Combine(sourceFolder, "Source [test].json");
    File.WriteAllText(source, fixture);
    File.WriteAllText(Path.Combine(sourceFolder, "meta.json"), "{\"FileName\":\"Source [test]\",\"unknown\":42}");
    var sourceHash = SHA256.HashData(File.ReadAllBytes(source));
    var output = MissionFiles.DefaultOutput(source);
    Throws<ArgumentException>(() => MissionFiles.Save(source, source, mission), "Source path protected");
    Throws<ArgumentException>(() => MissionFiles.ValidateOutput(source, source.ToUpperInvariant()), "Source protected without case sensitivity");
    Throws<ArgumentException>(() => MissionFiles.ValidateOutput(source, Path.Combine(sourceFolder, "Child", "Child.json")), "Source descendants protected");
    Throws<ArgumentException>(() => MissionFiles.ValidateOutput(source, Path.Combine(temporary, "Wrong", "Other.json")), "Mission folder and filename must match");
    Throws<ArgumentException>(() => MissionFiles.ValidateOutput(source, "relative.json"), "Relative output rejected");
    Check(MissionFiles.Save(source, output, mission) == "" && File.Exists(output), "First save creates independent output");
    var saved = File.ReadAllText(output);
    var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(output)!, "meta.json")))!;
    Check(metadata["FileName"]!.GetValue<string>() == Path.GetFileNameWithoutExtension(output) && metadata["unknown"]!.GetValue<int>() == 42,
        "Output metadata renamed, extra fields retained");
    mission.ReplaceGlobally("Tank", "Replacement");
    var backup = MissionFiles.Save(source, output, mission);
    Check(File.ReadAllText(backup) == saved && File.ReadAllText(output) != saved, "Repeated saves back up previous output");
    Check(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "Source bytes never change");
    Check(JsonNode.Parse(File.ReadAllText(Path.Combine(sourceFolder, "meta.json")))!["FileName"]!.GetValue<string>() == "Source [test]", "Source metadata never changes");
    Check(MissionFiles.Discover(temporary).Count == 2, "Discovery excludes metadata, temporary files and backups");
    Check(MissionFiles.Discover(Path.Combine(temporary, "absent")).Count == 0, "Missing missions directory handled");

    if (args.Length > 0)
    {
        var samplePath = Path.GetFullPath(args[0]);
        var hash = SHA256.HashData(File.ReadAllBytes(samplePath));
        var sample = new MissionDocument(File.ReadAllText(samplePath));
        var sampleBefore = JsonNode.Parse(sample.Serialize())!;
        Check(sample.Usage.Sum(u => u.Ground) == 574 && sample.Factories.Count == 78, "Real scenario: 574 vehicles and 78 production buildings, including two enrichment plants");
        Check(sample.Usage.All(u => UnitCatalog.Get(u.Type).Category != "Unknown") &&
            sample.Factories.All(f => UnitCatalog.Get(f.Building).Category != "Unknown"), "All unit IDs and production buildings in the supplied mission have name mappings");
        var usage = sample.Usage.Single(u => u.Type == "AFV8_IFV");
        var changed = sample.ReplaceGlobally("AFV8_IFV", "Linebreaker_IFV");
        Check(changed == usage.Total && sample.Usage.All(u => u.Type != "AFV8_IFV"), "Real scenario global substitution covers all occurrences");
        var sampleAfter = JsonNode.Parse(sample.Serialize())!;
        foreach (var key in new[] { "objectives", "outcomes", "missionSettings", "airbases", "environment" })
            Check(JsonNode.DeepEquals(sampleBefore[key], sampleAfter[key]), $"Real scenario retains {key}");
        sample.Undo();
        Check(JsonNode.DeepEquals(sampleBefore, JsonNode.Parse(sample.Serialize())), "Real scenario undo restores every value");
        var realOutput = Path.Combine(temporary, "Real output", "Real output.json");
        MissionFiles.Save(samplePath, realOutput, sample);
        Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(samplePath))), "Real source remains byte-identical after export");
    }
}
finally
{
    Directory.Delete(temporary, true);
}

Console.WriteLine($"Passed {checks} checks.");

void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
    Console.WriteLine("PASS " + name);
}

void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { Check(true, name); return; }
    throw new Exception("FAILED: " + name);
}
