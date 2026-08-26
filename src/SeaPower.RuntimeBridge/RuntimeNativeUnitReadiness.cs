using HarmonyLib;

using SeaPower;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeNativeUnitReadiness
{
    private static readonly System.Reflection.FieldInfo? CanUpdateField =
        AccessTools.Field(typeof(ObjectBase), "_canUpdate");

    public static bool IsInitialized(ObjectBase? unit) =>
        unit != null
        && unit._obp is not null
        && CanUpdateField?.GetValue(unit) is true;

    public static bool AcceptsPlayerOrders(ObjectBase? unit) =>
        IsInitialized(unit)
        && unit is
        {
            Disabled: false,
            IsDestroyed: false,
            IsControllable: true,
        }
        && unit.AcceptsOrdersFromPlayer
        && unit._userRoot is not null;

    public static bool HasInitializedStores(ObjectBase? unit)
    {
        if (unit == null)
        {
            return false;
        }
        if (unit._obp is null || unit._obp._weaponSystems.Count == 0)
        {
            return true;
        }

        return unit.AmmunitionAmountDictionary.Values.Any(quantity => quantity > 0);
    }

    public static bool AirborneRequirementSatisfied(ObjectBase? unit)
    {
        if (unit == null)
        {
            return false;
        }
        return unit switch
        {
            Aircraft aircraft => aircraft._isInFlight,
            Helicopter helicopter => helicopter._isInFlight,
            _ => true,
        };
    }
}
