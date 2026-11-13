using SeaPower;
using SeaPower.GameBridge;
using SeapowerUI;

namespace OODA;

internal class TimeObservations
{
    private const SensorSystem.SensorTypeSet Radar = SensorSystem.SensorTypeSet.SearchRadar
        | SensorSystem.SensorTypeSet.FCR | SensorSystem.SensorTypeSet.ARS;

    private record Contact(string Label, string Category, string Posture, bool Identified, bool Visual, bool Radar, bool Nearby);
    private record Unit(string Label, string Category, string Orders, ObjectBase.WeaponStatus Weapons, bool Disabled, int Damage);
    private readonly HashSet<string> nearbyContacts = [];
    private Dictionary<string, Contact>? contacts;
    private Dictionary<string, Unit>? units;

    internal IEnumerable<TimeEvent> Poll(Taskforce side)
    {
        Dictionary<string, Contact> nextContacts = [];
        ObjectBase[] ownUnits = PlayerPicture.Units(side);
        GeoPosition[]? ownPositions = null;
        if (side.PlottingTable?.Vehicles is { } vehicles)
        {
            foreach (Vehicle track in vehicles)
            {
                if (!PlayerPicture.IsLive(track) || track.BaseObject._taskforce == side) continue;
                string id = PlayerPicture.Id(track);
                Contact? prior = contacts?.GetValueOrDefault(id);
                MapVisualType category = track.GetMapType();
                RelationsState posture = track.CurrentRelationship();
                bool canBeNearby = posture == RelationsState.Hostile && category is not (MapVisualType.Missile or MapVisualType.Torpedo);
                bool nearby = canBeNearby && nearbyContacts.Contains(id);
                if (canBeNearby && track.PositionEstimate is { } estimate)
                {
                    ownPositions ??= ownUnits.Select(unit => unit.getGeoPosition()).ToArray();
                    double range = category switch
                    {
                        MapVisualType.Air or MapVisualType.Helicopter => 35,
                        MapVisualType.Submarine => 10,
                        _ => 20
                    };
                    if (nearby) range *= 1.25;
                    nearby = ownPositions.Any(position => GeoPosition.HaversineDistanceNmi(position, estimate.Item1) <= range);
                }
                nextContacts[id] = new(PlayerPicture.ContactLabel(track), category.ToString(), posture.ToString(),
                    track.Identified.Value, track.IsObservedByVisual, prior?.Radar == true || (track.DetectingSensors & Radar) != 0, nearby);
            }
        }
        foreach (var (id, contact) in nextContacts)
        {
            // A lost fix doesn't establish that a contact left nearby range.
            bool enteredRange = contact.Nearby && nearbyContacts.Add(id);
            if (!contact.Nearby) nearbyContacts.Remove(id);
            if (contacts == null) continue;
            Contact? prior = contacts.GetValueOrDefault(id);
            if (prior == null) yield return ContactEvent(TimeEventKind.TrackAppeared, id, contact, "Live track appeared");
            if (contact.Radar && prior?.Radar != true)
                yield return ContactEvent(TimeEventKind.RadarContact, id, contact, "Acquired by radar");
            if (enteredRange)
                yield return ContactEvent(TimeEventKind.HostileNearby, id, contact, "Hostile contact entered nearby range");
            if (prior != null && contact with { Radar = prior.Radar, Nearby = prior.Nearby } != prior)
                yield return ContactEvent(TimeEventKind.TrackChanged, id, contact, ContactChange(prior, contact)) with { PreviousPosture = prior.Posture };
        }
        if (contacts != null)
            foreach (var (id, prior) in contacts)
                if (!nextContacts.ContainsKey(id)) yield return ContactEvent(TimeEventKind.TrackLost, id, prior, "Live track lost");
        contacts = nextContacts;

        Dictionary<string, Unit> nextUnits = ownUnits.ToDictionary(PlayerPicture.Id, unit =>
            new Unit(unit.DisplayNameShort, unit._type.ToString(), unit.CurrentOrderText.Value, unit._weaponStatus,
                unit.Disabled, (int)unit._totalDamagePoints));
        if (units != null)
        {
            foreach (var (id, current) in nextUnits)
            {
                if (!units.TryGetValue(id, out Unit? prior)) continue;
                if (current.Damage > prior.Damage)
                    yield return new(TimeEventKind.UnitDamaged, id, current.Label, current.Category, "Own", "Reported damage increased");
                if (current with { Damage = prior.Damage } != prior)
                    yield return new(TimeEventKind.UnitStatusChanged, id, current.Label, current.Category, "Own",
                        $"{current.Orders}; weapons {current.Weapons}; disabled {current.Disabled}");
            }
            foreach (var (id, prior) in units)
                if (!nextUnits.ContainsKey(id))
                    yield return new(TimeEventKind.UnitStatusChanged, id, prior.Label, prior.Category, "Own", "Unit no longer controllable");
        }
        units = nextUnits;

        foreach (PlayerCombat.MissileImpact impact in PlayerCombat.TakeMissileImpacts())
        {
            UnityEngine.Debug.Log($"[OODA] Missile impact | {impact.Target} ({impact.TargetId}) · {impact.Detail}");
            yield return new(TimeEventKind.MissileHit, impact.TargetId, impact.Target, impact.Category, impact.Posture,
                impact.Detail, impact.MissileId);
        }
    }

    private static TimeEvent ContactEvent(TimeEventKind kind, string id, Contact contact, string detail) =>
        new(kind, id, contact.Label, contact.Category, contact.Posture, detail);

    private static string ContactChange(Contact prior, Contact current)
    {
        List<string> changes = [];
        if (prior.Posture != current.Posture) changes.Add($"Posture changed to {current.Posture.ToLowerInvariant()}");
        if (prior.Category != current.Category) changes.Add($"Now tracked as {current.Category.ToLowerInvariant()}");
        if (prior.Identified != current.Identified) changes.Add(current.Identified ? "Identified" : "Identification lost");
        if (prior.Visual != current.Visual) changes.Add(current.Visual ? "Visual contact" : "Visual contact lost");
        if (changes.Count == 0) changes.Add("Contact label updated");
        return string.Join(" · ", changes);
    }

    internal void Reset()
    {
        nearbyContacts.Clear();
        contacts = null;
        units = null;
    }
}
