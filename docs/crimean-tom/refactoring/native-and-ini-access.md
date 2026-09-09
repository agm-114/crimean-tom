# Native and INI access

## 5. Reuse parsed unit metadata

`Scenarios/ScenarioUnitCatalog.cs` repeatedly opens aircraft files through
`ReadIniValue`, `ReadFirstIniValue`, and `ReadSonobuoyAmmunition`.
Start by parsing each aircraft once per air-package read and passing the ordered
sections to those lookups. This does not require a global cache or a new dependency.

Sharing a reader with `Fleet/VesselCombatProfileReader.cs` and
`TaskForce/TaskForceRosterGenerator.cs` is a separate step. Their current behavior
is not interchangeable:

| Reader | Behavior to preserve |
|---|---|
| `ScenarioUnitCatalog.ReadSections` | Trims section names, retains repeated sections in order, keeps the last duplicate key within a section. Lookup helpers return the first matching section containing the key. |
| `VesselCombatProfileReader.ReadFromDisk` | Recognizes an exact case-insensitive `[AI]` header, leaves AI on any line starting with `[`, and accumulates values across repeated AI sections. |
| `TaskForceRosterGenerator.FindReferences` | Recognizes numbered Variant/Squadron headers by prefix regex, canonicalizes their numbers, and applies the Default nation known when each reference is flushed. It recognizes `Nation=`, not `Nation =`. |

The readers strip `//` and `#` comments, but not at the same stage.
Do not replace ordered sections with one dictionary keyed by section name.
Keep roster inheritance and variant sorting outside a generic reader.

Compare extracted profiles, variants, CAP/AEW/ASW choices, and dated sonobuoy
ammunition on installed metadata. Check duplicate sections/keys, header whitespace,
comments, missing nations, and Default section ordering with bounded comparisons.
Do not replace native campaign INI access or change cache lifetime in this cleanup.

## 15. Share repeated native names

`Campaign/CampaignPatches.cs`, `Campaign/NativeCampaignAccess.cs`,
`TaskForce/TaskForceCommanderPatches.cs`, and `TaskForce/TaskForceRosterGenerator.cs`
repeat private native type names for the save manager, debrief, commander manager,
and mode rules.

When editing these integrations, share repeated type-name constants with the
access wrapper. Keep Harmony target-method resolution in each patch. Do not change
resolution timing or add a general reflection registry.

The debrief section string in `CampaignRewardApplicationPatch` also duplicates
`DebriefSection` in `Campaign/CampaignPersistence.cs`, a partial declaration of
`CampaignOperationsState`. Reuse or move that naming helper without changing how
either reward hook obtains its result. One hook reads the supplied state INI before
the debrief is available; the other uses `BattleWon`. They are not duplicate
state-access paths. Verify target names and emitted section names remain identical.

Back to the [index](README.md).
