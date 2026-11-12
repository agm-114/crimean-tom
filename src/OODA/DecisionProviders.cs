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
                Arguments = $"--disable apps --disable plugins --disable shell_tool --disable shell_snapshot -c mcp_servers={{}} " +
                    $"-c \"web_search=\\\"disabled\\\"\" --ask-for-approval never exec --ignore-user-config --sandbox read-only " +
                    $"--ephemeral --skip-git-repo-check --model {Model} --json --output-schema schema.json --output-last-message decision.json -",
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
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
            await process.StandardInput.WriteAsync(prompt.ToCodexInput()).ConfigureAwait(false);
            process.StandardInput.Close();
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            process.WaitForExit();
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
                ("radiusNm", Number(nullable: true))))), ("timeRules", TimeRules())),
        DecisionRole.RoutineTactical => Object(("orders", Array(Order("action", "SetWaypoint", "Wait", "RequestReplan"))),
            ("standingOrderChanges", Array(Object(("targetId", Text()), ("settingId", Text()), ("value", Text()), ("reason", Text())))),
            ("timeCompression", new JObject { ["type"] = new JArray("integer", "null"),
                ["enum"] = new JArray(DecisionClock.Speeds.Select(speed => new JValue(speed)).Append(JValue.CreateNull())) }),
            ("timeReason", Text(nullable: true)), ("timeRules", TimeRules())),
        DecisionRole.SurfaceDefense => Object(("action", Choice("Engage", "Hold")), ("shipId", Text(nullable: true)),
            ("targetId", Text(nullable: true)), ("victimId", Text(nullable: true)),
            ("evidence", Choice("ObservedGunfire", "AttackReport", "None")),
            ("messageIndex", new JObject { ["type"] = new JArray("integer", "null") }), ("reason", Text()),
            ("roeAssessment", Choice("Consistent", "Unclear", "PossibleViolation")), ("roeReason", Text())),
        _ => Object(("optionId", Text()), ("reason", Text()))
    };

    private static JObject Order(string verb, params string[] values) => Object(("groupId", Text()),
        (verb, Choice(values)),
        ("destinationId", Text(nullable: true)), ("reason", Text()));
    private static JObject Array(JObject items) => new() { ["type"] = "array", ["items"] = items };
    private static JObject TimeRules() => new()
    {
        ["type"] = new JArray("array", "null"),
        ["items"] = Object(("event", Choice(Enum.GetNames(typeof(TimeEventKind)))), ("subjectId", Text(nullable: true)),
            ("category", Text(nullable: true)), ("posture", Text(nullable: true)), ("speed", new JObject
            {
                ["type"] = "integer", ["enum"] = new JArray(DecisionClock.Speeds)
            }), ("holdSeconds", new JObject { ["type"] = "integer" }))
    };
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
