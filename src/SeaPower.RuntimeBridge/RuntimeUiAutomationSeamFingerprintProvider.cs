using System.Reflection;

using CrimeanTom.Protocol;

using SeaPower;

using SeapowerUI.ViewModels;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeUiAutomationSeamFingerprintProvider
{
    private const BindingFlags AllMembers = BindingFlags.Public
        | BindingFlags.NonPublic
        | BindingFlags.Instance
        | BindingFlags.Static;

    public static UiAutomationSeamCapabilityPayload[] Capture()
    {
        return
        [
            CaptureGroupSafe(
                "task-force-builder",
                "786bf193786634d76732fe5b16a4af1a386e8c81437820a7f1c80c1da1cf7fdb",
                [GameCommandTypes.OpenTaskForceBuilder],
                () =>
                [
                    RequiredMethod(typeof(LinearCampaignMapViewModel), "get_OpenTaskForceBuilderCommand"),
                    RequiredMethod(typeof(LinearCampaignMapViewModel), "OpenTaskForceBuilder"),
                ]),
            CaptureGroupSafe(
                "scenario-navigation",
                "15b1f05d7ba1cf086a1da7d62d9bb2848805a26e285777aae49456aee36bf11f",
                [GameCommandTypes.StartScenario],
                () =>
                [
                    RequiredMethod(typeof(MenuSystemViewModel), "set_CurrentWindow"),
                    RequiredConstructor(typeof(ScenarioMenuViewModel)),
                    RequiredMethod(typeof(ScenarioMenuViewModel), "set_SelectedMission"),
                    RequiredMethod(typeof(ScenarioMenuViewModel), "get_PlayCommand"),
                    RequiredMethod(typeof(MissionManager), "DoLoad"),
                ]),
            CaptureGroupSafe(
                "campaign-navigation",
                "d30ae1f830122d765c2a1ec041fc4fbf902e0bf95a9e79403b46ba86ca94ca77",
                [GameCommandTypes.StartPortOperations],
                () =>
                [
                    RequiredMethod(typeof(MenuSystemViewModel), "set_CurrentWindow"),
                    RequiredConstructor(typeof(CampaignMenuViewModel)),
                    RequiredMethod(typeof(CampaignMenuViewModel), "set_SelectedMission"),
                    RequiredMethod(typeof(CampaignMenuViewModel), "get_PlaySelectedCampaignCommand"),
                    RequiredMethod(typeof(CampaignMenuViewModel), "PlaySelectedCampaign"),
                ]),
            CaptureGroupSafe(
                "maintenance-draft-edit",
                "03fc0c532edd799666571800d5bf8aad3df405035eaac2d9a17ac6c044bf5f83",
                [GameCommandTypes.AddMaintenanceDraftUnit],
                () =>
                [
                    RequiredMethod(typeof(LinearCampaignTaskForceBuilderViewModel), "set_SelectedRosterItem"),
                    RequiredMethod(typeof(LinearCampaignTaskForceBuilderViewModel), "set_SelectedPurchaseQuantity"),
                    RequiredMethod(typeof(LinearCampaignTaskForceBuilderViewModel), "get_AddSelectedItemCommand"),
                    RequiredMethod(typeof(LinearCampaignTaskForceBuilderViewModel), "AddSelectedItem"),
                ]),
            CaptureGroupSafe(
                "mission-briefing",
                "fbd01d14143b01471d12b38c547d4706aeb832b091eb37268dc945b90553b068",
                [GameCommandTypes.ContinueMissionBriefing],
                () =>
                [
                    RequiredConstructor(typeof(MissionBriefViewModel)),
                    RequiredMethod(typeof(MissionBriefViewModel), "get_PlayCommand"),
                    RequiredMethod(typeof(CancelAction), "UnPause"),
                ]),
            CaptureGroupSafe(
                "tactical-exit",
                "d980555afc658985fdc151a65bbd3389dfad95286e3a88c64dbef6796af9a56f",
                [GameCommandTypes.ExitTacticalSession],
                () =>
                [
                    RequiredMethod(typeof(MissionManager), "ExitToMainMenu"),
                    RequiredMethod(typeof(MissionDebriefViewModel), "get_ExitCommand"),
                    RequiredMethod(typeof(MissionDebriefViewModel), "get_ConfirmExit"),
                ]),
            CaptureGroupSafe(
                "camera-focus",
                "f48b75d40c10105d2db332a0fec8bf00a62e8604007d14544ff531522c054fd7",
                [GameCommandTypes.FocusCameraOnUnit],
                () =>
                [
                    RequiredMethod(typeof(RenderPosition), "switchToObject"),
                    RequiredMethod(typeof(RenderPosition), "getSelectedObject"),
                    RequiredMethod(typeof(RenderPosition), "switchToPivot"),
                    RequiredMethod(typeof(RenderPosition), "isAttachedToPivot"),
                    RequiredMethod(typeof(RenderPosition), "getPivotObject"),
                ]),
            CaptureGroupSafe(
                "tactical-hud-panel",
                "f346b53b868af2c2966bd08c0ede4ad7cf4c62e9861cf661d35013136c49e9ad",
                [GameCommandTypes.SetTacticalHudPanel],
                () =>
                [
                    RequiredMethod(typeof(MainGameViewModel), "ToggleMapOpen"),
                    RequiredMethod(typeof(MainGameViewModel), "SetHudVisible"),
                    RequiredMethod(typeof(PauseMenuViewModel), "get_ResumeCommand"),
                ]),
        ];
    }

    public static bool IsCommandVerified(string command) => Capture()
        .Any(seam => seam.Commands.Contains(command, StringComparer.Ordinal)
            && string.Equals(seam.Status, "verified", StringComparison.Ordinal));

    private static UiAutomationSeamCapabilityPayload CaptureGroup(
        string name,
        string expectedFingerprint,
        string[] commands,
        params MemberInfo[] members)
    {
        var observed = RuntimeDeterministicHookFingerprintProvider.CaptureGroup(name, members);
        var actual = observed.MethodFingerprintSha256 ?? string.Empty;
        var status = string.Equals(observed.Status, "inventory-observed", StringComparison.Ordinal)
            ? string.IsNullOrWhiteSpace(expectedFingerprint)
                ? "inventory-observed"
                : string.Equals(actual, expectedFingerprint, StringComparison.Ordinal)
                    ? "verified"
                    : "fingerprint-mismatch"
            : "inventory-failed";
        return new UiAutomationSeamCapabilityPayload
        {
            Name = name,
            Status = status,
            MethodFingerprintSha256 = actual,
            ExpectedFingerprintSha256 = expectedFingerprint,
            Commands = commands,
        };
    }

    private static UiAutomationSeamCapabilityPayload CaptureGroupSafe(
        string name,
        string expectedFingerprint,
        string[] commands,
        Func<MemberInfo[]> members)
    {
        try
        {
            return CaptureGroup(name, expectedFingerprint, commands, members());
        }
        catch
        {
            return new UiAutomationSeamCapabilityPayload
            {
                Name = name,
                Status = "inventory-failed",
                ExpectedFingerprintSha256 = expectedFingerprint,
                Commands = commands,
            };
        }
    }

    private static MethodInfo RequiredMethod(Type type, string name) => type
        .GetMethods(AllMembers)
        .Single(method => string.Equals(method.Name, name, StringComparison.Ordinal));

    private static ConstructorInfo RequiredConstructor(Type type) => type
        .GetConstructors(AllMembers)
        .Single(constructor => !constructor.IsStatic
            && constructor.GetParameters().Length == 0);
}
