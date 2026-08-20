using System.Security.Cryptography;
using System.Text.Json;
using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;

namespace CrimeanTom.Campaign.Engine;

public static class CampaignCanonicalSerializer
{
    public static byte[] Serialize(CampaignState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteState(writer, state);
        }

        return stream.ToArray();
    }

    public static string ComputeHash(CampaignState state) =>
        Convert.ToHexString(SHA256.HashData(Serialize(state))).ToLowerInvariant();

    private static void WriteState(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", state.Metadata.SchemaVersion);
        writer.WriteNumber("algorithmVersion", state.Metadata.AlgorithmVersion);
        writer.WriteString("campaignId", state.Metadata.CampaignId.ToString());
        writer.WriteString("name", state.Metadata.Name);
        writer.WriteNumber("seed", state.Metadata.Seed);
        writer.WriteString("catalogFingerprint", state.Metadata.CatalogFingerprint);
        writer.WriteNumber("timeQuanta", state.Time.Quanta);
        writer.WriteNumber("commitPosition", state.Head.Value);
        writer.WriteNumber("campaignRevision", state.CampaignRevision.Value);
        WriteRegions(writer, state);
        WriteBases(writer, state);
        WriteEntities(writer, state);
        WriteGroups(writer, state);
        WriteOrders(writer, state);
        WriteIntelligence(writer, state);
        WriteWeather(writer, state);
        WriteAssignments(writer, state);
        writer.WriteEndObject();
    }

    private static void WriteRegions(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("regions");
        foreach (var region in state.Regions.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("id", region.RegionId.ToString());
            writer.WriteString("name", region.Name);
            writer.WriteNumber("revision", region.Revision.Value);
            writer.WriteStartArray("neighbors");
            foreach (var neighbor in region.Neighbors)
            {
                writer.WriteStringValue(neighbor.ToString());
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteBases(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("bases");
        foreach (var item in state.Bases.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.BaseId.ToString());
            writer.WriteString("name", item.Name);
            writer.WriteString("coalitionId", item.CoalitionId.ToString());
            writer.WriteString("regionId", item.RegionId.ToString());
            WriteCoordinate(writer, item.Coordinate);
            WriteResources(writer, item.Resources);
            writer.WriteNumber("revision", item.Revision.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteEntities(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("entities");
        foreach (var item in state.Entities.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.EntityId.ToString());
            writer.WriteString("name", item.Name);
            writer.WriteString("catalogUnitId", item.CatalogUnitId);
            writer.WriteString("coalitionId", item.CoalitionId.ToString());
            writer.WriteString("regionId", item.RegionId.ToString());
            WriteCoordinate(writer, item.Coordinate);
            if (item.HomeBaseId is { } homeBaseId)
            {
                writer.WriteString("homeBaseId", homeBaseId.ToString());
            }
            else
            {
                writer.WriteNull("homeBaseId");
            }
            writer.WriteNumber("lifecycle", (int)item.Lifecycle);
            writer.WriteNumber("fidelity", (int)item.Fidelity);
            WriteResources(writer, item.Resources);
            writer.WriteString("classifiedTruthTag", item.ClassifiedTruthTag);
            writer.WriteNumber("revision", item.Revision.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteGroups(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("groups");
        foreach (var item in state.Groups.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.GroupId.ToString());
            writer.WriteString("name", item.Name);
            writer.WriteString("coalitionId", item.CoalitionId.ToString());
            writer.WriteNumber("revision", item.Revision.Value);
            writer.WriteStartArray("members");
            foreach (var member in item.Members)
            {
                writer.WriteStringValue(member.ToString());
            }
            writer.WriteEndArray();
            if (item.ActiveOrderId is { } orderId)
            {
                writer.WriteString("activeOrderId", orderId.ToString());
            }
            else
            {
                writer.WriteNull("activeOrderId");
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteOrders(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("orders");
        foreach (var item in state.Orders.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.OrderId.ToString());
            writer.WriteString("groupId", item.GroupId.ToString());
            writer.WriteNumber("kind", (int)item.Kind);
            writer.WriteString("destinationRegionId", item.DestinationRegionId.ToString());
            writer.WriteNumber("startsAt", item.StartsAt.Quanta);
            if (item.MustCompleteBy is { } deadline)
            {
                writer.WriteNumber("mustCompleteBy", deadline.Quanta);
            }
            else
            {
                writer.WriteNull("mustCompleteBy");
            }
            writer.WriteNumber("revision", item.Revision.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteIntelligence(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("intelligence");
        foreach (var item in state.Intelligence.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("observerCoalitionId", item.Key.ObserverCoalitionId.ToString());
            writer.WriteString("contactEntityId", item.Key.ContactEntityId.ToString());
            writer.WriteString("estimatedRegionId", item.EstimatedRegionId.ToString());
            WriteCoordinate(writer, item.EstimatedCoordinate);
            writer.WriteNumber("confidence", (int)item.Confidence);
            writer.WriteNumber("observedAt", item.ObservedAt.Quanta);
            writer.WriteNumber("revision", item.Revision.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteWeather(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("weather");
        foreach (var item in state.Weather.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("regionId", item.RegionId.ToString());
            writer.WriteNumber("windDirectionMilliDegrees", item.WindDirection.Value);
            writer.WriteNumber("windMillimetersPerSecond", item.WindSpeed.Value);
            writer.WriteNumber("visibilityMeters", item.Visibility.Value);
            writer.WriteNumber("seaStateMilliUnits", item.SeaStateMilliUnits);
            writer.WriteNumber("revision", item.Revision.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteAssignments(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("assignments");
        foreach (var item in state.Assignments.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.AssignmentId.ToString());
            writer.WriteString("entityId", item.EntityId.ToString());
            writer.WriteNumber("role", (int)item.Role);
            writer.WriteNumber("authorityEpoch", item.AuthorityEpoch.Value);
            writer.WriteNumber("effectiveAt", item.EffectiveAt.Quanta);
            writer.WriteNumber("revision", item.Revision.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteCoordinate(Utf8JsonWriter writer, GeoCoordinate coordinate)
    {
        writer.WriteNumber("latitudeMicrodegrees", coordinate.LatitudeMicrodegrees);
        writer.WriteNumber("longitudeMicrodegrees", coordinate.LongitudeMicrodegrees);
    }

    private static void WriteResources(
        Utf8JsonWriter writer,
        IEnumerable<KeyValuePair<ResourceKind, ResourceAmount>> resources)
    {
        writer.WriteStartArray("resources");
        foreach (var item in resources.OrderBy(item => item.Key))
        {
            writer.WriteStartObject();
            writer.WriteNumber("kind", (int)item.Key);
            writer.WriteNumber("milliUnits", item.Value.MilliUnits);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}
