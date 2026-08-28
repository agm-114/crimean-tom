using System.Collections.Immutable;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;

namespace CrimeanTom.Campaign.Engine;

/// <summary>
/// Per-ammunition magazine accounting for the server simulation.
///
/// Sea Power tracks rounds in <c>ObjectBase.AmmunitionAmountDictionary</c>, one
/// integer count per ammunition name, and every shot decrements the named
/// count. A ship that has fired its last Harpoon cannot engage a distant
/// surface contact even though its gun magazine is nearly full. The campaign
/// previously drew every weapon from one fungible munitions pool, which let a
/// platform keep employing a weapon it had actually exhausted and let gun fire
/// consume missile stock. These helpers replace that pool for weapons whose
/// magazines the catalog can count.
///
/// A missing key means the catalog declares no countable magazine for that
/// ammunition, which happens for gun and CIWS mounts that Sea Power's own
/// loadout rule does not enumerate. Those weapons stay usable and unlimited
/// here rather than being given an invented capacity. A present key holding
/// zero is a spent magazine and blocks employment.
/// </summary>
internal static class CampaignMagazines
{
    /// <summary>
    /// The magazine state a unit carries when it enters the campaign: every
    /// countable magazine full to its native capacity.
    /// </summary>
    public static ImmutableSortedDictionary<string, int> Full(
        IEnumerable<OperationalWeaponCapability> weapons)
    {
        var result = EntityState.NoMagazines.ToBuilder();
        foreach (var weapon in weapons)
        {
            if (weapon.MagazineCapacityRounds <= 0 || weapon.AmmunitionId.Length == 0)
            {
                continue;
            }

            // One ammunition can appear once per target classification. Every
            // such row reports the same magazine, so take the capacity rather
            // than summing it.
            result[weapon.AmmunitionId] = Math.Max(
                result.TryGetValue(weapon.AmmunitionId, out var existing) ? existing : 0,
                weapon.MagazineCapacityRounds);
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// The mount that fires next, or <see langword="null"/> when every mount is
    /// still reloading and when the catalog declares no mounts at all.
    ///
    /// Sea Power reloads each mount independently, so a ship with two tubes can
    /// launch both in quick succession and then wait a full cycle, while a ship
    /// with one tube of the same type launches half as often in bursts of one.
    /// Averaging the reload across the mounts would reproduce the sustained
    /// rate and lose that burst. The first declared free mount shoots, which
    /// keeps selection deterministic.
    /// </summary>
    public static OperationalWeaponMount? SelectMount(
        EntityState entity,
        OperationalWeaponCapability weapon,
        CampaignTime now)
    {
        foreach (var mount in weapon.Mounts)
        {
            if (!entity.MountReadyAt.TryGetValue(mount.MountId, out var readyAt)
                || readyAt <= now.Quanta)
            {
                return mount;
            }
        }
        return null;
    }

    /// <summary>
    /// Whether some mount can employ this ammunition now. A weapon the catalog
    /// gives no mounts, such as an aircraft hardpoint, is always ready.
    /// </summary>
    public static bool IsReady(
        EntityState entity,
        OperationalWeaponCapability weapon,
        CampaignTime now) =>
        weapon.Mounts.Count == 0 || SelectMount(entity, weapon, now) is not null;

    /// <summary>
    /// The campaign quantum at which one mount finishes reloading. A mount with
    /// no declared reload never becomes unavailable and is not recorded.
    /// </summary>
    public static ImmutableSortedDictionary<string, long> Reserve(
        ImmutableSortedDictionary<string, long> readiness,
        OperationalWeaponMount? mount,
        CampaignTime now) =>
        mount is null || mount.ReloadSeconds <= 0
            ? readiness
            : readiness.SetItem(mount.MountId, checked(now.Quanta + mount.ReloadSeconds));

    // Elapsed entries are deliberately left in place. An expired timer and an
    // absent key mean the same thing to SelectMount, so sweeping them would bump
    // an entity's revision for pure housekeeping. With short reload cycles across
    // a theater-sized order of battle that churn floods the projection stream
    // and destabilizes revision-sensitive handoffs. Reserve overwrites the key
    // on the next shot, and the map stays bounded by the mounts the entity
    // carries.

    /// <summary>
    /// Rounds one engagement from this mount consumes, from its native salvo
    /// size or rate of fire. Falls back to a single round when the catalog
    /// declares neither, or when no mount was resolved.
    /// </summary>
    public static int SalvoRounds(OperationalWeaponMount? mount) =>
        Math.Max(1, mount?.SalvoRounds ?? 0);

    /// <summary>
    /// Rounds this platform can guide at once for one ammunition, summed across
    /// the mounts that employ it. At least one, because a weapon with no
    /// declared fire-control sensor is not thereby unable to shoot.
    /// </summary>
    public static int GuidanceChannels(OperationalWeaponCapability weapon)
    {
        var channels = 0;
        foreach (var mount in weapon.Mounts)
        {
            channels = checked(channels + Math.Max(0, mount.GuidanceChannels));
        }
        return Math.Max(1, channels);
    }

    /// <summary>
    /// Rounds left of one ammunition, or <see langword="null"/> when the
    /// catalog does not count this magazine and the weapon is unlimited.
    /// </summary>
    public static int? Remaining(EntityState entity, string ammunitionId) =>
        entity.Magazines.TryGetValue(ammunitionId, out var rounds) ? rounds : null;

    /// <summary>
    /// Whether the entity can still employ this ammunition. An uncounted
    /// magazine is always available.
    /// </summary>
    public static bool HasRounds(EntityState entity, string ammunitionId) =>
        Remaining(entity, ammunitionId) is not 0;

    /// <summary>
    /// Whether any weapon the entity carries still has rounds. Used to decide
    /// that a platform is out of the fight rather than merely low on one
    /// magazine.
    /// </summary>
    public static bool HasAnyRounds(EntityState entity, IEnumerable<OperationalWeaponCapability> weapons) =>
        weapons.Any(weapon => HasRounds(entity, weapon.AmmunitionId));

    /// <summary>
    /// Removes rounds of one ammunition and reports how many were actually
    /// available. An uncounted magazine yields the full request and leaves the
    /// state untouched.
    /// </summary>
    public static (ImmutableSortedDictionary<string, int> Magazines, int Expended) Expend(
        ImmutableSortedDictionary<string, int> magazines,
        string ammunitionId,
        int requested)
    {
        if (requested <= 0 || !magazines.TryGetValue(ammunitionId, out var available))
        {
            return (magazines, Math.Max(0, requested));
        }

        var expended = Math.Min(available, requested);
        return expended == 0
            ? (magazines, 0)
            : (magazines.SetItem(ammunitionId, available - expended), expended);
    }

    /// <summary>
    /// Refills countable magazines toward their native capacity, drawing on the
    /// supplied transfer budget in rounds. Returns the refilled magazines and
    /// the budget left over.
    /// </summary>
    public static (ImmutableSortedDictionary<string, int> Magazines, long Remaining, bool Changed) Replenish(
        ImmutableSortedDictionary<string, int> magazines,
        IEnumerable<OperationalWeaponCapability> weapons,
        long budgetRounds)
    {
        if (budgetRounds <= 0 || magazines.Count == 0)
        {
            return (magazines, Math.Max(0, budgetRounds), false);
        }

        var capacities = Full(weapons);
        var result = magazines;
        var budget = budgetRounds;
        var changed = false;

        // Ordered by ammunition id so replenishment stays deterministic when the
        // budget cannot fill every magazine.
        foreach (var (ammunitionId, capacity) in capacities)
        {
            if (budget <= 0)
            {
                break;
            }
            if (!result.TryGetValue(ammunitionId, out var current) || current >= capacity)
            {
                continue;
            }

            var transfer = (int)Math.Min(capacity - current, budget);
            if (transfer <= 0)
            {
                continue;
            }
            result = result.SetItem(ammunitionId, current + transfer);
            budget -= transfer;
            changed = true;
        }

        return (result, budget, changed);
    }
}
