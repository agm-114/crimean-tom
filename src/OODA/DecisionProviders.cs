using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPower.GameBridge;

namespace OODA;

internal interface IDecisionProvider
{
    Task<JObject> Decide(DecisionRole role, DecisionPrompt prompt, CancellationToken cancellation);
}

internal class CodexDecisions : IDecisionProvider
{
    internal const string Model = "gpt-6.1-sol";

    public Task<JObject> Decide(DecisionRole role, DecisionPrompt prompt, CancellationToken cancellation) =>
        Task.Run(() => Run(role, prompt, cancellation), cancellation);

    private static async Task<JObject> Run(DecisionRole role, DecisionPrompt prompt, CancellationToken cancellation)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ooda-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string schema = Path.Combine(directory, "schema.json");
        string output = Path.Combine(directory, "decision.json");
        try
        {
            File.WriteAllText(schema, DecisionSchema.For(role).ToString(Formatting.None));
            string installed = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "Programs", "OpenAI", "Codex", "bin", "codex.exe");
            ProcessStartInfo start = new()
            {
                FileName = File.Exists(installed) ? installed : "codex",
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            string[] arguments = ["--disable", "apps", "--disable", "plugins", "--disable", "shell_tool", "--disable", "shell_snapshot",
                "-c", "mcp_servers={}", "-c", "web_search=\"disabled\"", "-c", $"developer_instructions={JsonConvert.ToString(prompt.StableInstructions)}",
                "--ask-for-approval", "never", "exec", "--ignore-user-config", "--sandbox", "read-only", "--ephemeral", "--skip-git-repo-check",
                "--model", Model, "--json", "--output-schema", "schema.json", "--output-last-message", "decision.json", "-"];
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            foreach (string key in new[] { "CODEX_API_KEY", "CODEX_ACCESS_TOKEN", "OPENAI_API_KEY", "OPENAI_BASE_URL" })
                start.Environment.Remove(key);

            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Codex.");
            using CancellationTokenRegistration stop = cancellation.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            string input = prompt.ToCodexInput(role);
            await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
            process.StandardInput.Close();
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            process.WaitForExit();
            LogUsage(role, await stdout.ConfigureAwait(false), Encoding.UTF8.GetByteCount(prompt.StableInstructions) + Encoding.UTF8.GetByteCount(input));
            cancellation.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !File.Exists(output))
                throw new InvalidOperationException($"Codex returned no decision (exit {process.ExitCode}). Check Codex sign-in and model access.");
            return JObject.Parse(File.ReadAllText(output));
        }
        finally
        {
            try { File.Delete(schema); File.Delete(output); Directory.Delete(directory); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void LogUsage(DecisionRole role, string events, int payloadBytes)
    {
        using StringReader reader = new(events);
        while (reader.ReadLine() is { } line)
        {
            if (line.IndexOf("\"turn.completed\"", StringComparison.Ordinal) < 0) continue;
            JObject item;
            try { item = JObject.Parse(line); }
            catch (JsonException) { continue; }
            if ((string?)item["type"] != "turn.completed" || item["usage"] is not JObject usage) continue;
            long? Count(string name) => usage[name] is JValue { Type: JTokenType.Integer } count ? count.Value<long>() : null;
            if (Count("input_tokens") is not { } input || Count("output_tokens") is not { } output) continue;
            UnityEngine.Debug.Log($"[OODA] Codex usage | {role} | model={Model} input={input} cached_input={Count("cached_input_tokens")?.ToString() ?? "unknown"} "
                + $"output={output} reasoning_output={Count("reasoning_output_tokens")?.ToString() ?? "unknown"} total={input + output} payload_bytes={payloadBytes}");
        }
    }
}

internal class OpenAiDecisions(HttpClient client, string apiKey) : IDecisionProvider
{
    public async Task<JObject> Decide(DecisionRole role, DecisionPrompt prompt, CancellationToken cancellation)
    {
        if (role != DecisionRole.UrgentTactical) throw new ArgumentOutOfRangeException(nameof(role));
        if (prompt.Input["options"] is not JArray options || options.Count < 2)
            throw new ArgumentException("A decision requires at least two eligible options.", nameof(prompt));
        JObject payload = new()
        {
            ["model"] = "gpt-6-luna",
            ["input"] = prompt.Input.ToString(Formatting.None),
            ["questions"] = new JArray(new JObject
            {
                ["name"] = "tactical_action", ["type"] = "choice",
                ["instructions"] = prompt.Instructions + "\nChoose the eligible option that best serves the supplied mission.",
                ["choices"] = new JArray(options.Select(option => new JObject
                {
                    ["value"] = (string?)option["id"], ["description"] = (string?)option["description"]
                }))
            })
        };
        using HttpRequestMessage request = new(HttpMethod.Post, "https://api.openai.com/v1/decisions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI Decisions returned HTTP {(int)response.StatusCode}.");
        JObject result = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        cancellation.ThrowIfCancellationRequested();
        JToken? answer = result["answers"]?.First;
        string? choice = (string?)answer?["choice"];
        if ((string?)answer?["type"] != "choice" || !options.Any(option => (string?)option["id"] == choice))
            throw new InvalidOperationException("OpenAI Decisions returned no eligible choice.");
        return new JObject { ["optionId"] = choice, ["confidence"] = answer?["confidence"]?.DeepClone() };
    }
}

internal static class DecisionSchema
{
    internal static JObject For(DecisionRole role) => role switch
    {
        DecisionRole.Operational => Object(("objective", Text()), ("assignments", Array(Order("task", "Transit", "Screen", "Hold"))),
            ("inspectGroupId", Text(nullable: true)), ("mapMarks", Array(Object(
                ("kind", Choice(Enum.GetNames(typeof(ChartMarkKind)))), ("label", Text()), ("latitude", Number()), ("longitude", Number()),
                ("radiusNm", Number(nullable: true))))), ("helicopterOrders", HelicopterOrders()), ("airOrders", AirOrders()),
            ("maritimeOrders", MaritimeOrders()), ("approaches", Approaches()), ("surfaceRoutes", SurfaceRoutes()),
            ("conditionalAttacks", ConditionalAttacks()), ("timePolicy", TimePolicy()), ("decisionQueries", DecisionQueries()),
            ("movementProfiles", MovementProfiles()), ("conditionalOrders", ConditionalOrders()), ("capabilityRequests", CapabilityRequests()),
            ("sensorSweeps", SensorSweeps())),
        DecisionRole.RoutineTactical => Object(("orders", Array(Order("action", "SetWaypoint", "Wait", "RequestReplan"))),
            ("standingOrderChanges", StandingOrders()),
            ("timeCompression", TimeSpeed(nullable: true)),
            ("timeReason", Text(nullable: true)), ("helicopterOrders", HelicopterOrders()), ("airOrders", AirOrders()),
            ("maritimeOrders", MaritimeOrders()), ("approaches", Approaches()), ("surfaceRoutes", SurfaceRoutes()),
            ("conditionalAttacks", ConditionalAttacks()), ("timePolicy", TimePolicy()), ("decisionQueries", DecisionQueries()),
            ("movementProfiles", MovementProfiles()), ("conditionalOrders", ConditionalOrders()), ("capabilityRequests", CapabilityRequests()),
            ("sensorSweeps", SensorSweeps())),
        DecisionRole.SurfaceDefense => Object(("engagements", Array(Object(("shipId", Text()), ("targetId", Text()),
            ("victimId", Text()), ("weapons", Choice(Enum.GetNames(typeof(SurfaceWeapon)))),
            ("evidence", Choice("ObservedGunfire", "AttackReport", "HostileThreat")),
            ("messageIndex", new JObject { ["type"] = new JArray("integer", "null") }), ("reason", Text()),
            ("roeAssessment", Choice("Consistent", "Unclear", "PossibleViolation")), ("roeReason", Text())))), ("reason", Text())),
        DecisionRole.AirDefense => Object(("standingOrderChanges", StandingOrders()), ("targetIds", Array(Text())), ("reason", Text())),
        _ => Object(("optionId", Text()), ("reason", Text()))
    };

    private static JObject Order(string verb, params string[] values) => Object(("groupId", Text()),
        (verb, Choice(values)),
        ("destinationId", Text(nullable: true)), ("reason", Text()));
    private static JObject Array(JObject items) => new() { ["type"] = "array", ["items"] = items };
    private static JObject StandingOrders() =>
        Array(Object(("targetId", Text()), ("settingId", Text()), ("value", Text()), ("reason", Text())));
    private static JObject HelicopterOrders() => Array(Object(("shipId", Text()), ("fitId", Text()), ("anchorId", Text(nullable: true)),
        ("useRadar", new JObject { ["type"] = "boolean" }), ("reason", Text())));
    private static JObject AirOrders() => Array(Object(("action", Choice("Prepare", "Launch", "Task", "Split", "Join")), ("unitId", Text()),
        ("role", Choice("CAP", "SEAD", "Recon", "ASW", "Strike", "Strafe", "AntiShip", "Transport", "Hold", "Disengage", "Recover", "Formation")), ("fitId", Text(nullable: true)),
        ("count", IntegerOrNull()), ("targetId", Text(nullable: true)), ("refitBatchId", Text(nullable: true)),
        ("route", Array(Object(("latitude", Number()), ("longitude", Number()), ("altitudeFeet", Number())))),
        ("useRadar", new JObject { ["type"] = "boolean" }), ("reason", Text())));
    private static JObject MaritimeOrders() => Array(Object(("action", Choice("EngageShip", "Bombard")), ("shipId", Text()), ("targetId", Text()),
        ("weapons", Choice(Enum.GetNames(typeof(SurfaceWeapon)))), ("reason", Text()),
        ("roeAssessment", Choice("Consistent", "Unclear", "PossibleViolation")), ("roeReason", Text())));
    private static JObject Approaches() => Array(Object(("shipId", Text()), ("targetId", Text()),
        ("standoffNm", Number(nullable: true)), ("reason", Text())));
    private static JObject SurfaceRoutes() => Array(Object(("shipId", Text()),
        ("route", Array(Object(("latitude", Number()), ("longitude", Number())))), ("reason", Text())));
    private static JObject ConditionalAttacks() => Array(Object(("action", Choice("Arm", "Cancel")),
        ("unitId", Text()), ("targetId", Text()), ("attack", Choice("CAP", "SEAD", "Strike", "Strafe", "AntiShip", "EngageShip", "Bombard")),
        ("weapons", Choice(Enum.GetNames(typeof(SurfaceWeapon)))), ("expiresInSeconds", Number()), ("reason", Text())));
    private static JObject ToolRoute() => Array(Object(("latitude", Number()), ("longitude", Number()), ("altitudeFeet", Number(nullable: true))));
    private static JObject DecisionQueries() => Array(Object(("tool", Choice("preview_route", "get_engagement_options", "get_order_status", "preview_sortie_cycle", "estimate_damage", "estimate_detection")),
        ("unitId", Text(nullable: true)), ("targetId", Text(nullable: true)), ("orderId", Text(nullable: true)), ("fitId", Text(nullable: true)),
        ("onStationSeconds", Number(nullable: true)), ("waypoints", ToolRoute()),
        ("damage", OptionalObject(("weaponId", Text()), ("directHits", IntegerOrNull()))),
        ("detection", OptionalObject(("sensorId", Text(nullable: true)), ("targetKind", Text()), ("targetHeightMeters", Number(nullable: true)),
            ("rcsSquareMeters", Number(nullable: true)), ("observerHeightMeters", Number(nullable: true)), ("observerSpeedKnots", Number(nullable: true))))));
    private static JObject OptionalObject(params (string Name, JObject Schema)[] fields) =>
        new() { ["anyOf"] = new JArray(Object(fields), new JObject { ["type"] = "null" }) };
    private static JObject SensorSweeps() => Array(Object(("unitId", Text()), ("kind", Choice("Radar", "Periscope", "ESM")),
        ("sensorId", Text(nullable: true)), ("seconds", Number(nullable: true)), ("reason", Text())));
    private static JObject MovementProfiles() => Array(Object(("unitId", Text()), ("speedKnots", Number(nullable: true)),
        ("altitudeFeet", Number(nullable: true)), ("depthPreset", IntegerOrNull()),
        ("emissions", Choice("Keep", "RadarOn", "RadarOff", "Active", "Passive")), ("reason", Text())));
    private static JObject ConditionalOrders() => Array(Object(("action", Choice("Arm", "Cancel")), ("orderId", Text()),
        ("unitId", Text(nullable: true)), ("condition", Choice("AirRouteClear", "CapCoverageBelow", "ContactLost")),
        ("order", Choice("ResumeTransport", "LaunchRelief", "SetSurfaceRoute", "DisengageFlight")),
        ("conditionId", Text(nullable: true)), ("minimumCoverage", IntegerOrNull()), ("fitId", Text(nullable: true)),
        ("count", IntegerOrNull()), ("waypoints", ToolRoute()), ("expiresInSeconds", Number()), ("reason", Text())));
    private static JObject IntegerOrNull() => new() { ["type"] = new JArray("integer", "null") };
    private static JObject CapabilityRequests() => Array(Object(("kind", Choice("Automation", "Tool", "Analysis")),
        ("title", Text()), ("problem", Text()), ("requestedBehavior", Text())));
    private static JObject TimeSpeed(bool nullable = false) => new()
    {
        ["type"] = nullable ? new JArray("integer", "null") : new JValue("integer"),
        ["enum"] = new JArray(DecisionClock.Speeds.Select(speed => new JValue(speed))
            .Concat(nullable ? [JValue.CreateNull()] : Enumerable.Empty<JValue>()))
    };
    private static JObject TimePolicy() => Object(("cruiseSpeed", TimeSpeed(nullable: true)), ("cruiseReason", Text(nullable: true)),
        ("rules", new JObject
        {
            ["type"] = new JArray("array", "null"),
            ["items"] = Object(("event", Choice(Enum.GetNames(typeof(TimeEventKind)))), ("subjectId", Text(nullable: true)),
                ("category", Text(nullable: true)), ("posture", Text(nullable: true)), ("speed", TimeSpeed()),
                ("holdSeconds", new JObject { ["type"] = "integer" }))
        }), ("defaultSuggestion", Text(nullable: true)));
    private static JObject Text(bool nullable = false) => new() { ["type"] = nullable ? new JArray("string", "null") : new JValue("string") };
    private static JObject Number(bool nullable = false) => new() { ["type"] = nullable ? new JArray("number", "null") : new JValue("number") };
    private static JObject Choice(params string[] values) => new() { ["type"] = "string", ["enum"] = new JArray(values) };
    private static JObject Object(params (string Name, JObject Schema)[] fields) => new()
    {
        ["type"] = "object", ["additionalProperties"] = false,
        ["required"] = new JArray(fields.Select(field => field.Name)),
        ["properties"] = new JObject(fields.Select(field => new JProperty(field.Name, field.Schema)))
    };
}
