using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SeaPower;
using SeaPower.GameBridge;

namespace OODA;

internal class SurfaceDefense
{
    private static readonly Regex AttackLanguage = new(
        @"\b(?:under (?:attack|fire)|taking fire|opened fire|fired (?:on|at|upon)|fir(?:e|ing) (?:on|at|upon|from)|fire (?:was |is )?reported from|attacking|being attacked|shooting (?:at|on))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private HashSet<(string Attacker, string Victim)> gunfire = [];
    private HashSet<string> reportContacts = [];
    private double lastRequested;
    internal bool Pending { get; set; }
    internal bool ActiveGunfire { get; private set; }

    internal void Poll(Taskforce side)
    {
        bool hasReport = Reports().Any();
        PlayerCombat.Gunfire[] recent = PlayerCombat.Recent().Where(item => item.FriendlyVictim || hasReport).ToArray();
        double now = GameTime.missionElapsedTime;
        ActiveGunfire = recent.Any(item => now - item.LastSeen <= 12);
        HashSet<(string, string)> observed = recent.Select(item => (item.AttackerId, item.VictimId)).ToHashSet();
        if (observed.Except(gunfire).Any()) Pending = true;
        if (ActiveGunfire && now - lastRequested >= 30)
        {
            HashSet<ObjectBase> engaged = PlayerPicture.Ships(side).SelectMany(ship => ship._currentEngageTasks)
                .Select(task => task._targetObject).ToHashSet();
            if (recent.Any(item => item.LastSeen > lastRequested && now - item.LastSeen <= 12
                && PlayerPicture.LiveContact(side, item.AttackerId) is { } attacker && !engaged.Contains(attacker.BaseObject)))
                Pending = true;
        }
        gunfire = observed;

        HashSet<string> contacts = hasReport && side.PlottingTable?.Vehicles is { } tracks
            ? tracks.Where(IsReportCandidate).Select(PlayerPicture.Id).ToHashSet() : [];
        if (contacts.Except(reportContacts).Any()) Pending = true;
        reportContacts = contacts;
    }

    internal void OnMessages()
    {
        if (PlayerCombat.Recent().Any(item => item.FriendlyVictim) || Reports().Any()) Pending = true;
    }

    internal void MarkRequested()
    {
        Pending = false;
        lastRequested = GameTime.missionElapsedTime;
    }

    internal void Reset()
    {
        Pending = ActiveGunfire = false;
        lastRequested = 0;
        gunfire.Clear();
        reportContacts.Clear();
    }

    internal static void Describe(Taskforce side, JObject view)
    {
        view["attackReports"] = new JArray(Reports().Select(report => new JObject
        {
            ["messageIndex"] = report.Index, ["elapsedSeconds"] = report.Message.Time,
            ["title"] = report.Message.Title, ["from"] = report.Message.From, ["body"] = report.Message.Body
        }));
        view["protectedUnits"] = new JArray(OwnVessels(side).Where(ship => !PlayerPicture.CanCommand(ship, side)).Select(ship => new JObject
        {
            ["id"] = PlayerPicture.Id(ship), ["name"] = ship.DisplayNameShort, ["position"] = PlayerPicture.Point(ship.getGeoPosition())
        }));
    }

    internal static string? ReviewEvidence(JObject decision, Taskforce side, Vehicle target)
    {
        string targetId = PlayerPicture.Id(target);
        string? victimId = (string?)decision["victimId"];
        if (victimId == null || targetId == victimId) return "The decision did not identify a separate victim.";
        return (string?)decision["evidence"] switch
        {
            "ObservedGunfire" => PlayerCombat.Recent().Any(item => item.AttackerId == targetId && item.VictimId == victimId)
                ? null : "Recent visual gunfire does not corroborate this attacker and victim.",
            "AttackReport" => MatchesReport(side, target, victimId, (int?)decision["messageIndex"])
                ? null : "The attack report does not uniquely corroborate this live contact near the named victim.",
            _ => "The order did not cite observed gunfire or a specific attack report."
        };
    }

    private static bool MatchesReport(Taskforce side, Vehicle target, string victimId, int? messageIndex)
    {
        if (!IsReportCandidate(target) || target.PositionEstimate is not { } estimate || messageIndex == null) return false;
        MissionCommunications.Message? message = Reports().FirstOrDefault(report => report.Index == messageIndex).Message;
        if (message == null) return false;

        Vessel? own = OwnVessels(side).FirstOrDefault(ship => PlayerPicture.Id(ship) == victimId);
        Vehicle? other = own == null ? PlayerPicture.LiveContact(side, victimId) : null;
        if (own == null && (other?.BaseObject is not Vessel || other.CurrentRelationship() == RelationsState.Hostile)) return false;
        string victimName = own?.DisplayNameShort ?? (other!.HasReportedIdentity ? PlayerPicture.ContactLabel(other) : "");
        GeoPosition? victimPosition = own != null ? own.getGeoPosition() : other?.PositionEstimate?.Item1;
        string reportText = $"{message.From}\n{message.Title}\n{message.Body}";
        if (string.IsNullOrWhiteSpace(victimName) || reportText.IndexOf(victimName, StringComparison.OrdinalIgnoreCase) < 0
            || victimPosition is not { } position || !Near(estimate.Item1, position)) return false;
        if (target.HasReportedIdentity && PlayerPicture.ContactLabel(target) is { Length: > 0 } targetName
            && reportText.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        string? classification = target.Class?.Value;
        return side.PlottingTable?.Vehicles.Any(otherTrack => otherTrack != target && IsReportCandidate(otherTrack)
            && string.Equals(otherTrack.Class?.Value, classification, StringComparison.OrdinalIgnoreCase)
            && otherTrack.PositionEstimate is { } otherEstimate && Near(otherEstimate.Item1, position)) != true;
    }

    private static bool IsReportCandidate(Vehicle track) => PlayerPicture.IsLive(track) && track.BaseObject is Vessel
        && track.CurrentRelationship() is not RelationsState.Friendly and not RelationsState.Neutral
        && (!string.IsNullOrWhiteSpace(track.Class?.Value) || track.Identified.Value)
        && (track.CurrentRelationship() == RelationsState.Hostile || PlayerPicture.IsVisual(track));

    private static bool Near(GeoPosition attacker, GeoPosition victim) => GeoPosition.HaversineDistanceNmi(attacker, victim) <= 20;

    private static IEnumerable<Vessel> OwnVessels(Taskforce side) => Singleton<ObjectsManager>.Instance._listOfAllUnits.OfType<Vessel>()
        .Where(ship => ship && !ship.IsDestroyed && ship._taskforce == side);

    private static IEnumerable<(MissionCommunications.Message Message, int Index)> Reports() =>
        MissionCommunications.Messages.Select((message, index) => (Message: message, Index: index))
            .Where(report => GameTime.missionElapsedTime - report.Message.Time <= PlayerCombat.EvidenceLifetime
                && AttackLanguage.IsMatch(report.Message.Body));
}
