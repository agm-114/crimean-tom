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
    /// Whether every mount that can employ this ammunition has finished
    /// reloading. A weapon with no declared reload is always ready.
    /// </summary>
    public static bool IsReady(EntityState entity, string ammunitionId, CampaignTime now) =>
        !entity.WeaponReadyAt.TryGetValue(ammunitionId, out var readyAt) || readyAt <= now.Quanta;

    /// <summary>
    /// The campaign quantum at which this ammunition may be employed again.
    ///
    /// Sea Power reloads each mount independently, so a ship with two launchers
    /// can re-engage about twice as often as one with a single launcher of the
    /// same type. The server tracks one interval per ammunition rather than per
    /// mount and divides the reload time by the mount count, which reproduces
    /// the sustained rate but not the burst pattern of mounts cycling out of
    /// phase. That difference is an approximation, not a measured tolerance.
    /// </summary>
    public static ImmutableSortedDictionary<string, long> Reserve(
        ImmutableSortedDictionary<string, long> readiness,
        OperationalWeaponCapability weapon,
        CampaignTime now)
    {
        if (weapon.ReloadSeconds <= 0)
        {
            return readiness;
        }

        var mounts = Math.Max(1, weapon.MountCount);
        var interval = Math.Max(1, weapon.ReloadSeconds / mounts);
        return readiness.SetItem(weapon.AmmunitionId, checked(now.Quanta + interval));
    }

    // Elapsed entries are deliberately left in place. An expired timer and an
    // absent key mean the same thing to IsReady, so sweeping them would bump an
    // entity's revision for pure housekeeping. With short reload cycles across
    // a theater-sized order of battle that churn floods the projection stream
    // and destabilizes revision-sensitive handoffs. Reserve overwrites the key
    // on the next shot, and the map stays bounded by the ammunition the entity
    // carries.

    /// <summary>
    /// Rounds one engagement consumes, from the mount's native salvo size or
    /// rate of fire. Falls back to a single round when the catalog declares
    /// neither.
    /// </summary>
    public static int SalvoRounds(OperationalWeaponCapability weapon) =>
        Math.Max(1, weapon.SalvoRounds);

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
