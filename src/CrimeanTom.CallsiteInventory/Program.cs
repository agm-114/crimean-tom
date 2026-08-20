using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Mono.Cecil;
using Mono.Cecil.Cil;

var arguments = ParseArguments(args);
var assemblyPath = RequireFile(arguments, "assembly");
var outputPath = RequireValue(arguments, "output");

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(assemblyPath));
using var assembly = AssemblyDefinition.ReadAssembly(
    assemblyPath,
    new ReaderParameters
    {
        AssemblyResolver = resolver,
        InMemory = true,
        ReadingMode = ReadingMode.Immediate,
    });
var entries = new List<CallsiteEntry>();
var methodsWithBodies = 0;

foreach (var type in AllTypes(assembly.MainModule.Types).OrderBy(type => type.FullName))
{
    foreach (var method in type.Methods.OrderBy(method => method.MetadataToken.ToInt32()))
    {
        if (!method.HasBody)
        {
            continue;
        }
        methodsWithBodies++;
        var methodHash = ComputeMethodHash(method);
        AddMethodEntry(entries, method, methodHash);

        foreach (var instruction in method.Body.Instructions)
        {
            foreach (var family in FamiliesForInstruction(instruction))
            {
                var classification = Classify(method, family);
                entries.Add(new CallsiteEntry(
                    family,
                    method.FullName,
                    method.MetadataToken.ToInt32(),
                    methodHash,
                    instruction.Offset,
                    FormatOperand(instruction.Operand),
                    classification.Value,
                    classification.Basis,
                    classification.Value == "unknown"));
            }
        }
    }
}

entries = entries
    .OrderBy(entry => entry.Family, StringComparer.Ordinal)
    .ThenBy(entry => entry.DeclaringMethod, StringComparer.Ordinal)
    .ThenBy(entry => entry.IlOffset)
    .ToList();
var manifest = new CallsiteManifest(
    1,
    Path.GetFileName(assemblyPath),
    ComputeFileSha256(assemblyPath),
    methodsWithBodies,
    entries.Count,
    entries.Count(entry => entry.PromotionCriticalUnknown),
    entries
        .GroupBy(entry => entry.Family, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
    entries
        .GroupBy(entry => entry.Classification, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
    entries);

outputPath = Path.GetFullPath(outputPath);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)
    ?? throw new InvalidOperationException("The output path has no parent directory."));
var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
});
File.WriteAllText(outputPath, json + Environment.NewLine, new UTF8Encoding(false));
Console.WriteLine(JsonSerializer.Serialize(new
{
    manifest.SchemaVersion,
    manifest.AssemblyFileName,
    manifest.AssemblySha256,
    manifest.MethodsWithBodies,
    manifest.CallsiteCount,
    manifest.PromotionCriticalUnknownCount,
    manifest.FamilyCounts,
    manifest.ClassificationCounts,
    outputPath,
    outputSha256 = ComputeFileSha256(outputPath),
}));

static void AddMethodEntry(
    List<CallsiteEntry> entries,
    MethodDefinition method,
    string methodHash)
{
    string? family = null;
    if (IsDirectCallback(method))
    {
        family = "direct-update-callback";
    }
    else if (IsCoroutineOwner(method))
    {
        family = "coroutine-owner";
    }
    else if (IsJobOwner(method))
    {
        family = "job-owner";
    }
    if (family is null)
    {
        return;
    }

    var classification = Classify(method, family);
    entries.Add(new CallsiteEntry(
        family,
        method.FullName,
        method.MetadataToken.ToInt32(),
        methodHash,
        -1,
        "method-entry",
        classification.Value,
        classification.Basis,
        classification.Value == "unknown"));
}

static IEnumerable<string> FamiliesForInstruction(Instruction instruction)
{
    if (instruction.Operand is FieldReference field)
    {
        if (field.DeclaringType.FullName == "SeaPower.Globals"
            && field.Name is "_rnd" or "_unityRandom")
        {
            yield return "global-rng";
        }
        yield break;
    }
    if (instruction.Operand is not MethodReference method)
    {
        yield break;
    }

    var owner = method.DeclaringType.FullName;
    if (owner == "SeaPower.GameTime" && method.Name.StartsWith("get_", StringComparison.Ordinal))
    {
        yield return $"game-time.{method.Name[4..]}";
    }
    if (owner == "UnityEngine.Time" && method.Name.StartsWith("get_", StringComparison.Ordinal))
    {
        yield return $"unity-time.{method.Name[4..]}";
    }
    if (owner == "UnityEngine.Random")
    {
        yield return "unity-random";
    }
    if (owner == "System.Random")
    {
        yield return "system-random";
    }
    if (method.Name == "StartCoroutine")
    {
        yield return "coroutine-start";
    }
    if (method.Name == "Schedule"
        && (owner.Contains("Unity.Jobs", StringComparison.Ordinal)
            || method.FullName.Contains("JobHandle", StringComparison.Ordinal)))
    {
        yield return "job-schedule";
    }
    if (method.Name == "Complete" && owner == "Unity.Jobs.JobHandle")
    {
        yield return "job-complete";
    }
    if (owner.Contains("WeightedRandom", StringComparison.OrdinalIgnoreCase))
    {
        yield return "weighted-random";
    }
    if (owner == "SeaPower.ObjectsManager"
        && method.Name.Contains("object", StringComparison.OrdinalIgnoreCase))
    {
        yield return "object-registration";
    }
    if ((owner.Contains("StateMachine", StringComparison.Ordinal)
            || owner.Contains("Order", StringComparison.Ordinal))
        && (method.Name.Contains("state", StringComparison.OrdinalIgnoreCase)
            || method.Name.Contains("order", StringComparison.OrdinalIgnoreCase)
            || method.Name is "Transition" or "Execute"))
    {
        yield return "order-state-dispatch";
    }
}

static (string Value, string Basis) Classify(MethodDefinition method, string family)
{
    var owner = method.DeclaringType.FullName;
    if (IsPresentationOwner(owner))
    {
        return ("presentation-only", "declaring-type-ui-or-presentation");
    }
    if (IsNativeCampaignOwner(owner))
    {
        return ("native-campaign-nonauthoritative", "declaring-type-native-campaign");
    }
    if (family.StartsWith("game-time.", StringComparison.Ordinal)
        || family.StartsWith("unity-time.", StringComparison.Ordinal)
        || family is "global-rng" or "unity-random" or "system-random"
            or "weighted-random" or "job-schedule" or "job-complete"
            or "job-owner" or "object-registration" or "order-state-dispatch")
    {
        return ("simulation-critical", "simulation-affecting-api-outside-presentation");
    }
    return ("unknown", "requires-call-graph-or-runtime-ownership-review");
}

static bool IsPresentationOwner(string owner) => new[]
{
    "SeapowerUI", "ViewModel", "View", "Menu", "Editor", "Camera", "Cloud",
    "MapControl", "Briefing", "HUD", "Tooltip", "Visual", "Display", "Audio",
}.Any(marker => owner.Contains(marker, StringComparison.OrdinalIgnoreCase));

static bool IsNativeCampaignOwner(string owner) => new[]
{
    "CampaignMain", "CampaignUpdater", "LinearCampaign", "CampaignEnvironment",
    "DynamicCampaign",
}.Any(marker => owner.Contains(marker, StringComparison.OrdinalIgnoreCase));

static bool IsDirectCallback(MethodDefinition method) =>
    method.Parameters.Count == 0
    && method.Name is "Update" or "FixedUpdate" or "LateUpdate" or "OnUpdate"
        or "OnFixedUpdate" or "OnLateUpdate";

static bool IsCoroutineOwner(MethodDefinition method) =>
    method.ReturnType.FullName is "System.Collections.IEnumerator"
        or "System.Collections.Generic.IEnumerator`1";

static bool IsJobOwner(MethodDefinition method) =>
    method.Name == "Execute"
    && method.DeclaringType.Interfaces.Any(item =>
        item.InterfaceType.FullName.StartsWith("Unity.Jobs.IJob", StringComparison.Ordinal)
        || item.InterfaceType.FullName.StartsWith("Unity.Entities.IJob", StringComparison.Ordinal));

static string ComputeMethodHash(MethodDefinition method)
{
    var canonical = string.Join("\n", method.Body.Instructions.Select(instruction =>
        $"{instruction.OpCode.Code}:{FormatOperand(instruction.Operand)}"));
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
        .ToLowerInvariant();
}

static string FormatOperand(object? operand) => operand switch
{
    null => string.Empty,
    MethodReference method => method.FullName,
    FieldReference field => field.FullName,
    TypeReference type => type.FullName,
    ParameterDefinition parameter => $"parameter:{parameter.Index}",
    VariableDefinition variable => $"variable:{variable.Index}",
    Instruction instruction => $"target:{instruction.Offset}",
    Instruction[] instructions => "targets:" + string.Join(",", instructions.Select(item => item.Offset)),
    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
    _ => operand.ToString() ?? string.Empty,
};

static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> roots)
{
    foreach (var root in roots)
    {
        yield return root;
        foreach (var nested in AllTypes(root.NestedTypes))
        {
            yield return nested;
        }
    }
}

static Dictionary<string, string> ParseArguments(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length; index += 2)
    {
        if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("Arguments must be --name value pairs.");
        }
        result.Add(args[index][2..], args[index + 1]);
    }
    return result;
}

static string RequireValue(IReadOnlyDictionary<string, string> values, string key) =>
    values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"--{key} is required.");

static string RequireFile(IReadOnlyDictionary<string, string> values, string key)
{
    var path = Path.GetFullPath(RequireValue(values, key));
    return File.Exists(path)
        ? path
        : throw new FileNotFoundException($"--{key} file not found.", path);
}

static string ComputeFileSha256(string path) =>
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

internal sealed record CallsiteManifest(
    int SchemaVersion,
    string AssemblyFileName,
    string AssemblySha256,
    int MethodsWithBodies,
    int CallsiteCount,
    int PromotionCriticalUnknownCount,
    IReadOnlyDictionary<string, int> FamilyCounts,
    IReadOnlyDictionary<string, int> ClassificationCounts,
    IReadOnlyList<CallsiteEntry> Callsites);

internal sealed record CallsiteEntry(
    string Family,
    string DeclaringMethod,
    int MethodMetadataToken,
    string MethodSha256,
    int IlOffset,
    string Operand,
    string Classification,
    string ClassificationBasis,
    bool PromotionCriticalUnknown);
