# Content and build

## 26. Explain descriptor reward fallbacks

In repository `content/campaigns/crimean-tom-operations/campaign.ini`,
`TaskForceModeCompletionPoints` and `TaskForceModeCompletionCapPoints` look like
the active balance settings. For supported Crimean Tom contracts, the payment and
reward-application patches replace them with the contract offer's victory-gated
payment.

Add a short comment near these values naming `CampaignContractRules` and
`CampaignContractOffers` as the runtime tuning locations. Keep fallback amounts
intact; do not zero them or assume they are inert when patches are unavailable.
This needs a content diff, not a generator for the repeated mission blocks.

## 27. Reduce build metadata duplication

`CrimeanTom.SeaPower.csproj` repeats the repository campaign content root in
`CampaignDescriptor`, `CampaignRoster`, `CampaignArtDirectory`, and
`CampaignMissionDirectory`. Derive them from one `CampaignContentDirectory`
property when next editing deployment. Keep source and deployment roots distinct.

The csproj version and the `ACPlugin` attribute in `Plugin.cs` both say
`0.3.0`. They are currently consistent. If unifying them, remember that attribute
arguments must be compile-time values; reading the assembly version at runtime
cannot supply the attribute. Avoid adding a custom build generator solely for this.
At minimum, identify both locations in release guidance.

Do not replace the explicit assembly reference blocks with a transform just to
save lines. AnchorChain and Harmony use different roots from the game assemblies.

Verify evaluated content paths and copied filenames after any build edit.
For version changes, inspect both the assembly version and plugin attribute.

Back to the [index](README.md).
