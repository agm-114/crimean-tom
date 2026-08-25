using System.Text.Json;

using CrimeanTom.Protocol;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeEventPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static Func<string, string, int, bool>? _publish;

    public static void Bind(Func<string, string, int, bool> publish) => _publish = publish;

    public static void Unbind() => _publish = null;

    public static void CampaignMapSelectionChanged(SelectedSurfaceRenderProxyPayload selection)
    {
        _publish?.Invoke(
            GameEventTypes.CampaignMapSelectionChanged,
            JsonSerializer.Serialize(selection, JsonOptions),
            CommandPriorities.Control);
    }
}
