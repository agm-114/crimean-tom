using HarmonyLib;

using SeaPower;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeNativeUnitReadiness
{
    private static readonly System.Reflection.FieldInfo? CanUpdateField =
        AccessTools.Field(typeof(ObjectBase), "_canUpdate");

    public static bool IsInitialized(ObjectBase? unit) =>
        unit is not null
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
}
