using AircraftStates;
using SeaPower;

namespace SeaPower.GameBridge;

internal static partial class PlayerFlightDeck
{
    private const double FeetPerUnity = 220.47266;
    internal record AttackProgress(string Phase, string? Blocker, bool Active, bool ReleasePass = false, double? NominalRangeNm = null);

    internal static AttackProgress ReadAttackProgress(ObjectBase unit, ObjectBase target, string? weapon = null)
    {
        bool Matches(AmmunitionParameters? ammo) => ammo != null && (weapon == null || ammo._displayedName == weapon);
        AlignUnitToTarget? alignment = unit._alignmentTasks.LastOrDefault(task => !task._isGuidingWeapon
            && task._targetObject == target && Matches(task._ap));
        EngageTask? task = unit._currentEngageTasks.FirstOrDefault(task => task._targetObject == target && Matches(task._ammunition?._ap));
        WeaponSystem? mount = unit._obp?._weaponSystems.FirstOrDefault(mount => mount._executingEngageTask
            && mount._targetObject == target && Matches(mount._ammoForEngage?._ap));
        var state = unit.StateMachine?.CurrentState;
        if (state is Aligning { currentAlignment: { } active } && !active._isGuidingWeapon
            && active._targetObject == target && Matches(active._ap))
        {
            alignment = active;
            if (active._weaponSystem is { _executingEngageTask: true } alignedMount
                && alignedMount._targetObject == target && Matches(alignedMount._ammoForEngage?._ap)) mount = alignedMount;
        }
        AmmunitionParameters? ammo = mount?._ammoForEngage?._ap ?? alignment?._ap ?? task?._ammunition?._ap;
        if (ammo == null && unit._userRoot?.TaskViewModels.Select(item => item.Task).OfType<AttackTask>()
            .Any(item => item.targetObject.value == target && Matches(unit.getAmmunitionByName(item.ammunitionForEngage.value)?._ap)) == true)
            return new("Queued in native orders", null, true);
        if (ammo == null && unit._ai?._objectToDestroy != target) return new("No native attack", null, false);

        string? gate = mount == null ? null : ReleaseGate(mount._engageState);
        if (state is EngageSurfaceContact { currentAlignment: { } current } attack && current == alignment)
        {
            var phase = attack.escStateMachine?.CurrentState;
            if (phase is EngageSurfaceContact.LevelAttack or EngageSurfaceContact.DiveAttack)
                return new(phase is EngageSurfaceContact.LevelAttack ? "Level attack pass" : "Dive attack pass",
                    gate ?? (!attack.AllowEngagement ? "Native attack phase is withholding release" : null), true, true);
            return new(phase switch
            {
                EngageSurfaceContact.LowLevelIngress => "Low-level ingress",
                EngageSurfaceContact.MidLevelIngress => "Mid-level ingress",
                EngageSurfaceContact.HighLevelIngress => "High-level ingress",
                EngageSurfaceContact.PopUp => "Climbing for attack pass",
                _ => "Setting up attack pass"
            }, gate, true, NominalRangeNm: ammo?._maxLaunchRangeInMiles);
        }
        if (state is MissileEvasion or AdvancedMissileEvasion)
            return new("Evading", "Defensive maneuver interrupted the attack", true);
        return new(mount != null ? "Weapon assigned" : alignment != null ? "Aligning" : "Moving to engage", gate, true);
    }

    internal static double MinimumReleaseHeightFeet(AmmunitionParameters ammo) => Math.Round(ammo._launchAltitudesInUnity.x * FeetPerUnity);

    private static string? ReleaseGate(WeaponSystem.EngageState state) => state switch
    {
        WeaponSystem.EngageState.Idle or WeaponSystem.EngageState.NotObserved or WeaponSystem.EngageState.CheckingAbort
            or WeaponSystem.EngageState.Launch or WeaponSystem.EngageState.LaunchingSalvo or WeaponSystem.EngageState.Firing => null,
        WeaponSystem.EngageState.LauncherTooLow => "Below native release altitude",
        WeaponSystem.EngageState.LauncherTooHigh => "Above native release altitude",
        WeaponSystem.EngageState.TargetTooFar or WeaponSystem.EngageState.TargetTooFarToOpenSystem => "Outside native release range",
        WeaponSystem.EngageState.TargetTooClose => "Inside native minimum range",
        WeaponSystem.EngageState.WaitingForRoll => "Waiting for aircraft to level its wings",
        WeaponSystem.EngageState.WaitingForPitch => "Waiting for attack pitch",
        WeaponSystem.EngageState.TargetIsNotInFiringArcs => "Target outside the firing arc",
        WeaponSystem.EngageState.TargetIsNotInVerticalArc => "Target outside the vertical firing or seeker arc",
        WeaponSystem.EngageState.NoLOSToTarget => "No line of sight for release",
        WeaponSystem.EngageState.LaserCantPaintTarget => "No usable laser designation",
        WeaponSystem.EngageState.TVSeekerCantSeeTarget => "TV seeker cannot acquire the target",
        WeaponSystem.EngageState.TargetIsNotDetectableByFCR or WeaponSystem.EngageState.TargetIsBeyondFCRHorizon => "Fire-control radar cannot acquire the target",
        WeaponSystem.EngageState.EngagementNotAllowed => "Native attack phase is withholding release",
        _ => $"Native mount state: {state}"
    };

    private static string? AirWeaponBlocker(ObjectBase unit, ObjectBase target, AmmunitionParameters ammo)
    {
        if (unit.DoesAmmoMatchTarget(ammo._ammunitionFileName, target)) return null;
        if (ammo.GuidanceTypeForTarget(target) == AmmunitionParameters.GuidanceType.AntiRadiationHoming
            && RadarCalculator.getSignalStrength(target, unit, unit.transform.position, ammo._gainFactor,
                ammo._passiveRadarHomingFrequencies, out _, overHorizonMultiplier: 1f) < Globals.RcsValues._minDetectableReturnPowerESM)
            return "Seeker receives no usable compatible emission from this position (band, horizon, strength or radar coverage)";
        if (unit.CrewVisionBlocksAmmo(ammo, target)) return "Crew vision or lighting does not permit this weapon";
        if (ammo.GuidanceTypeForTarget(target) == AmmunitionParameters.GuidanceType.TVHoming
            && !ammo._midCourseCorrection.AllowsLauncherGuidance
            && unit.DoesAmmoMatchTarget(ammo._ammunitionFileName, target, ignoreSituationalRestrictions: true))
            return "TV seeker visibility is below its minimum launch range";
        return "Native weapon-to-target compatibility check failed";
    }
}
