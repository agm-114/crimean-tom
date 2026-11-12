using SeaPower;
using SeaPower.GameBridge;

namespace OODA;

internal class TimeObservations
{
    private const SensorSystem.SensorTypeSet Radar = SensorSystem.SensorTypeSet.SearchRadar
        | SensorSystem.SensorTypeSet.FCR | SensorSystem.SensorTypeSet.ARS;

    private record Contact(string Label, string Category, string Posture, bool Identified, bool Visual, bool Radar);
    private record Unit(string Label, string Category, string Orders, ObjectBase.WeaponStatus Weapons, bool Disabled, int Damage);
    private Dictionary<string, Contact>? contacts;
    private Dictionary<string, Unit>? units;

    internal IEnumerable<TimeEvent> Poll(Taskforce side)
    {
        Dictionary<string, Contact> nextContacts = [];
        if (side.PlottingTable?.Vehicles is { } vehicles)
        {
            foreach (Vehicle track in vehicles)
            {
                if (!PlayerPicture.IsLive(track) || track.BaseObject._taskforce == side) continue;
                nextContacts[PlayerPicture.Id(track)] = new(PlayerPicture.ContactLabel(track), track.GetMapType().ToString(),
                    track.CurrentRelationship().ToString(), track.Identified.Value, track.IsObservedByVisual,
                    (track.DetectingSensors & Radar) != 0);
            }
        }
        if (contacts != null)
        {
            foreach (var (id, contact) in nextContacts)
            {
                bool known = contacts.TryGetValue(id, out Contact? prior);
                if (!known) yield return ContactEvent(TimeEventKind.TrackAppeared, id, contact, "Live track appeared");
                if (contact.Radar && prior?.Radar != true)
                    yield return ContactEvent(TimeEventKind.RadarContact, id, contact, "Acquired by radar");
                if (prior != null && contact with { Radar = prior.Radar } != prior)
                    yield return ContactEvent(TimeEventKind.TrackChanged, id, contact,
                        $"{prior.Label} ({prior.Posture}) → {contact.Label} ({contact.Posture}); identified {contact.Identified}, visual {contact.Visual}");
            }
            foreach (var (id, prior) in contacts)
                if (!nextContacts.ContainsKey(id)) yield return ContactEvent(TimeEventKind.TrackLost, id, prior, "Live track lost");
        }
        contacts = nextContacts;

        Dictionary<string, Unit> nextUnits = PlayerPicture.Units(side).ToDictionary(PlayerPicture.Id, unit =>
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
            yield return new(TimeEventKind.MissileHit, impact.TargetId, impact.Target, impact.Category, impact.Posture,
                "Missile hit target", impact.MissileId);
    }

    private static TimeEvent ContactEvent(TimeEventKind kind, string id, Contact contact, string detail) =>
        new(kind, id, contact.Label, contact.Category, contact.Posture, detail);

    internal void Reset()
    {
        contacts = null;
        units = null;
    }
}
