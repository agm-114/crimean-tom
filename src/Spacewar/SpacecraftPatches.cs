using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;

namespace Spacewar;

[HarmonyPatch]
internal static class SpacecraftPatches
{
    // GameTime updates immediately before RenderPosition. Move before camera follow, not in LateUpdate.
    [HarmonyPrefix, HarmonyPatch(typeof(RenderPosition), nameof(RenderPosition.OnUpdate))]
    private static void BeforeCamera() => SpacewarController.Active?.MoveSpacecraft();

    [HarmonyPostfix, HarmonyPatch(typeof(RelocationHandler), nameof(RelocationHandler.relocate))]
    private static void AfterRelocation() => SpacewarController.Active?.MoveSpacecraft();

    // Retain native unit bookkeeping and systems without aircraft physics or AI.
    [HarmonyReversePatch, HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.OnFixedUpdate))]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void UnitFixedUpdate(ObjectBase instance) => throw new NotSupportedException("Harmony reverse patch");

    [HarmonyPrefix, HarmonyPatch(typeof(Aircraft), nameof(Aircraft.OnFixedUpdate))]
    private static bool FixedUpdate(Aircraft __instance)
    {
        var proxy = SpacecraftProxy.For(__instance);
        if (proxy == null) return true;
        proxy.Move();
        if (__instance.Disabled) return false;
        UnitFixedUpdate(__instance);
        proxy.Move();
        return false;
    }

    // Aircraft's frame update includes atmospheric flight, fuel and the global altitude clamp.
    [HarmonyPrefix, HarmonyPatch(typeof(Aircraft), nameof(Aircraft.OnUpdateEveryFrame))]
    private static bool FrameUpdate(Aircraft __instance) => SpacecraftProxy.For(__instance) == null;

    [HarmonyPrefix, HarmonyPatch(typeof(AI), nameof(AI.OnFixedUpdate))]
    private static bool Orders(ObjectBase ____baseObject) => SpacecraftProxy.For(____baseObject) == null;

    [HarmonyPrefix, HarmonyPatch(typeof(AI), nameof(AI.OnUpdate))]
    private static bool LazyOrders(ObjectBase ____baseObject) => SpacecraftProxy.For(____baseObject) == null;

    [HarmonyPrefix, HarmonyPatch(typeof(SensorSystem), nameof(SensorSystem.CanProduceContacts), MethodType.Getter)]
    private static bool Contacts(ObjectBase ____baseObject, ref bool __result)
    {
        if (SpacecraftProxy.For(____baseObject) == null) return true;
        __result = false;
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(SeekerBase), "isValidTarget")]
    private static bool Target(ObjectBase targetObject, ref bool __result)
    {
        if (SpacecraftProxy.For(targetObject) == null) return true;
        __result = false;
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.GetMaxEngagementRanges))]
    private static bool Engagement(ObjectBase __instance, ObjectBase targetObject, ref ObjectBase.EngagementRange __result)
    {
        if (SpacecraftProxy.For(__instance) == null && SpacecraftProxy.For(targetObject) == null) return true;
        __result = default;
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.EnableColliders))]
    private static bool Colliders(ObjectBase __instance) => SpacecraftProxy.For(__instance) == null;

    [HarmonyPrefix, HarmonyPatch(typeof(Aircraft), nameof(Aircraft.notifyOfHit))]
    private static bool Hit(Aircraft __instance) => SpacecraftProxy.For(__instance) == null;

    [HarmonyPostfix, HarmonyPatch(typeof(Aircraft), nameof(Aircraft.SaveStateToFile))]
    private static void Save(Aircraft __instance, IniHandler ini, string sectionName)
    {
        if (SpacecraftProxy.For(__instance) is { } proxy)
            ini.writeValue(sectionName, "SpacewarProfile", proxy.Craft.Name);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(Aircraft), nameof(Aircraft.LoadStateFromFile))]
    private static void Load(Aircraft __instance, IniHandler ini, string sectionName)
    {
        string name = ini.readValue(sectionName, "SpacewarProfile", "");
        var craft = SpacecraftCatalog.Units.Find(item => item.Name == name);
        if (craft != null) SpacecraftProxy.Attach(__instance, craft);
    }
}
