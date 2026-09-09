using SeaPower;

namespace FlightDeckTweaks.Preparation;

// Native code reserves the requested loadout. This holds only incompatible mounted stores.
internal class StoreReservation
{
    private double ammoPoints;
    private readonly Dictionary<string, int> categories = new(StringComparer.OrdinalIgnoreCase);

    internal static StoreReservation Excess(PreparationBatch batch, PreparationLoadout target) => From(batch.Aircraft, target);
    internal static StoreReservation From(IEnumerable<AircraftPreparation> aircraft, PreparationLoadout? target = null)
    {
        StoreReservation result = new();
        foreach (AircraftPreparation unit in aircraft)
        foreach (var mounted in unit.Mounted)
        {
            double wanted = target?.Stores.FirstOrDefault(store => store.Reference.Equals(mounted.Key, StringComparison.OrdinalIgnoreCase))?.Amount ?? 0;
            int count = (int)Math.Ceiling(Math.Max(0, mounted.Value - wanted));
            if (count == 0) continue;
            WeaponService weapon = PreparationCatalog.Weapon(mounted.Key);
            if (weapon.Category.Length == 0) result.ammoPoints += weapon.AmmoPoints * count;
            else result.categories[weapon.Category] = result.categories.GetValueOrDefault(weapon.Category) + count;
        }
        return result;
    }

    internal bool CanFund(FlightDeck deck, StoreReservation held, Loadout next, int count) => Globals._cheatFlightDeckAmmo
        || deck._currentAmmo + held.ammoPoints >= ammoPoints + next._ammoPoints * count
        && categories.Keys.Union(next._accountableAmmunition.Keys).All(category =>
            deck._accountableAmmunition.GetValueOrDefault(category) + held.categories.GetValueOrDefault(category)
            >= categories.GetValueOrDefault(category) + next._accountableAmmunition.GetValueOrDefault(category) * count);

    internal bool CanReplace(FlightDeck deck, StoreReservation previous, Loadout oldLoadout, Loadout next, int count) =>
        Globals._cheatFlightDeckAmmo ||
        deck._currentAmmo + previous.ammoPoints + oldLoadout._ammoPoints * count >= ammoPoints + next._ammoPoints * count
        && categories.Keys.Union(next._accountableAmmunition.Keys).All(category =>
            deck._accountableAmmunition.GetValueOrDefault(category) + previous.categories.GetValueOrDefault(category)
                + oldLoadout._accountableAmmunition.GetValueOrDefault(category) * count
            >= categories.GetValueOrDefault(category) + next._accountableAmmunition.GetValueOrDefault(category) * count);

    internal void Replace(FlightDeck deck, StoreReservation next, bool refresh = true)
    {
        bool changed = ammoPoints != next.ammoPoints;
        deck._currentAmmo += (float)(ammoPoints - next.ammoPoints);
        foreach (string category in categories.Keys.Union(next.categories.Keys))
        {
            int refund = categories.GetValueOrDefault(category) - next.categories.GetValueOrDefault(category);
            if (refund == 0) continue;
            deck._accountableAmmunition[category] = deck._accountableAmmunition.GetValueOrDefault(category) + refund;
            changed = true;
        }
        if (changed && refresh) deck.UpdateLoadoutsAvailability();
    }
}
