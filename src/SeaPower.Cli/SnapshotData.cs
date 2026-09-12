using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace SeaPower.Cli;

internal static class SnapshotData
{
    private static readonly ISerializer Yaml = new SerializerBuilder().WithQuotingNecessaryStrings(quoteYaml1_1Strings: true)
        .DisableAliases().WithIndentedSequences().WithTypeConverter(new JsonYamlWriter()).Build();
    private static readonly IDeserializer YamlReader = new DeserializerBuilder()
        .WithDuplicateKeyChecking().WithAttemptingUnquotedStringTypeDeserialization().Build();
    private static readonly ISerializer Json = new SerializerBuilder().JsonCompatible().Build();

    internal static string OutputFormat(string[] args)
    {
        int index = Array.IndexOf(args, "--format");
        string format = index >= 0 && index + 1 < args.Length ? args[index + 1].ToLowerInvariant()
            : index >= 0 ? "" : args.Contains("--json") ? "json" : "yaml";
        if (format is not ("json" or "yaml")) throw new ArgumentException("--format must be yaml or json.");
        if (args.Contains("--json") && format != "json") throw new ArgumentException("--json conflicts with --format yaml.");
        return format;
    }

    internal static string Format(JsonElement value, string format) => format == "json"
        ? JsonSerializer.Serialize(value, WireJson.Options) : Yaml.Serialize(value).TrimEnd('\r', '\n');

    internal static JsonElement Parse(string text, string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is ".yaml" or ".yml")
            text = Json.Serialize(YamlReader.Deserialize<object>(text));
        using JsonDocument document = JsonDocument.Parse(text, WireJson.DocumentOptions);
        return document.RootElement.Clone();
    }

    internal static object? ToObject(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(property => property.Name, property => ToObject(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().Select(ToObject).ToArray(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out long integer) => integer,
        JsonValueKind.Number when value.TryGetDecimal(out decimal number) && (number != 0 || value.GetDouble() == 0) => number,
        JsonValueKind.Number => value.GetDouble(),
        _ => null,
    };

    private class JsonYamlWriter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(JsonElement);
        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer) => throw new NotSupportedException();

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serialize)
        {
            JsonElement element = (JsonElement)value!;
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    emitter.Emit(new MappingStart());
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        serialize(JsonSerializer.SerializeToElement(property.Name));
                        serialize(property.Value);
                    }
                    emitter.Emit(new MappingEnd());
                    break;
                case JsonValueKind.Array:
                    emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, SequenceStyle.Block));
                    foreach (JsonElement item in element.EnumerateArray()) serialize(item);
                    emitter.Emit(new SequenceEnd());
                    break;
                case JsonValueKind.Number:
                    // Preserve the JSON number text, including tiny values and large integers.
                    emitter.Emit(new Scalar(element.GetRawText()));
                    break;
                case JsonValueKind.String when element.GetString() is { } text && (text.Contains('\n')
                    || Regex.IsMatch(text, @"\A\d{4}-\d{2}-\d{2}", RegexOptions.CultureInvariant)):
                    emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, text,
                        text.Contains('\n') ? ScalarStyle.Literal : ScalarStyle.DoubleQuoted, false, true));
                    break;
                default:
                    serialize(ToObject(element));
                    break;
            }
        }
    }
}
