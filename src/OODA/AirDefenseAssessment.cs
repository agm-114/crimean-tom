using Newtonsoft.Json.Linq;
using SeaPower;
using SeaPower.GameBridge;
using SeapowerUI;

namespace OODA;

internal class AirDefenseAssessment
{
    private const double SilenceSeconds = 20;
    private const double StrikeWindowSeconds = 300;
    private record Site(string Label, string Category, GeoPosition? Position, bool Live, double? LastEmission, double? SilentSince, double? LastFire, double? LastInbound,
        bool Inbound, string Assessment);
    private readonly Dictionary<string, Site> sites = [];

    internal IEnumerable<(string Id, string Label, string Assessment)> Poll(Taskforce side)
    {
        double now = GameTime.missionElapsedTime;
        HashSet<ObjectBase> inbound = new(PlayerCombat.MissileTargetsInFlight(includeAircraft: true));
        HashSet<string> live = [];
        foreach (Vehicle track in side.PlottingTable?.Vehicles.AsEnumerable() ?? [])
        {
            if (!track.BaseObject || !track.IsVisible() || track.CurrentRelationship() != RelationsState.Hostile
                || track.GetMapType() is not (MapVisualType.SAM or MapVisualType.Radar)) continue;
            string id = PlayerPicture.Id(track);
            Site? prior = sites.GetValueOrDefault(id);
            if (!PlayerPicture.IsLive(track))
            {
                sites[id] = new(PlayerPicture.ContactLabel(track), track.GetMapType().ToString(), track.PositionEstimate?.Item1 ?? prior?.Position,
                    false, prior?.LastEmission, null, PlayerCombat.LastOwnFireAt(id), prior?.LastInbound, false, "Track lost");
                continue;
            }
            live.Add(id);
            bool emitting = PlayerPicture.ObservesEmission(track);
            bool missileInbound = inbound.Contains(track.BaseObject);
            double? fire = PlayerCombat.LastOwnFireAt(id);
            double? lastInbound = missileInbound ? now : prior?.LastInbound;
            double? lastEmission = emitting ? now : prior?.LastEmission;
            double? silentSince = null;
            if (!emitting && prior is { Live: true })
                silentSince = prior.Assessment == "Emitting" ? now : prior.SilentSince;
            string assessment = emitting ? "Emitting" : "No emission detected";
            if (!emitting && fire is double shot && lastEmission is double seen && silentSince is double quiet
                && (prior?.Assessment == "Likely suppressed"
                    || !missileInbound && seen >= shot - StrikeWindowSeconds && quiet >= shot && now - quiet >= SilenceSeconds
                        && quiet - Math.Max(shot, lastInbound ?? shot) <= StrikeWindowSeconds))
                assessment = "Likely suppressed";
            Site current = new(PlayerPicture.ContactLabel(track), track.GetMapType().ToString(), track.PositionEstimate?.Item1 ?? prior?.Position,
                true, lastEmission, silentSince, fire, lastInbound, missileInbound, assessment);
            sites[id] = current;
            if (fire.HasValue && (assessment == "Likely suppressed" && prior?.Assessment != assessment
                || emitting && prior?.Assessment == "Likely suppressed"))
                yield return (id, current.Label, emitting ? "Radar emitting again" : assessment);
        }

        foreach (string id in sites.Keys.Where(id => !live.Contains(id) && sites[id].Live).ToArray())
            sites[id] = sites[id] with { Live = false, SilentSince = null, Inbound = false, Assessment = "Track lost" };
    }

    internal JArray Read() => new(sites.OrderByDescending(entry => entry.Value.LastFire).Take(32).Select(entry => new JObject
        {
            ["targetId"] = entry.Key, ["label"] = entry.Value.Label, ["category"] = entry.Value.Category,
            ["position"] = entry.Value.Position is { } position ? PlayerPicture.Point(position) : null, ["assessment"] = entry.Value.Assessment,
            ["lastEmissionSeconds"] = entry.Value.LastEmission, ["lastFireSeconds"] = entry.Value.LastFire,
            ["missileInbound"] = entry.Value.Inbound
        }));

    internal bool LikelySuppressed(string id) => sites.GetValueOrDefault(id)?.Assessment == "Likely suppressed";

    internal void Reset() => sites.Clear();
}
