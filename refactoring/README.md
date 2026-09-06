# Refactoring backlog

Outstanding suggestions checked against source at `5f3adae9` on 2026-09-06.
This was a source and call-site audit, not an in-game verification.
Items are candidates, not instructions to implement the whole list.

Start with 13, 24, or 11 for a bounded cleanup. Item 5 has broader value but needs
parser comparisons first. Take one item at a time and keep gameplay unchanged.

## Index

| # | Scope | Suggestion |
|---|---|---|
| 13 | Small | [Reuse the formation-spacing switch](fleet-planning.md#13-reuse-the-formation-spacing-switch) |
| 24 | Small | [Remove unused members](dead-code.md#24-remove-unused-members) |
| 11 | Small | [Simplify match copies](models.md#11-simplify-match-copies) |
| 21 | Small | [Share commander selection lookup](campaign-state.md#21-share-commander-selection-lookup) |
| 12 | Small | [Centralize launcher identity](scenario-generation.md#12-centralize-launcher-identity) |
| 19 | Cross-cutting | [Make contract identity explicit](naming-and-identity.md#19-make-contract-identity-explicit) |
| 5 | Cross-cutting | [Reuse parsed unit metadata](native-and-ini-access.md#5-reuse-parsed-unit-metadata) |
| 7 | UI | [Share window construction where layouts agree](ui.md#7-share-window-construction-where-layouts-agree) |
| 2 | Conditional | [Group fleet capability data](models.md#2-group-fleet-capability-data) |
| 10 | Conditional | [Group fleet-size tuning](fleet-planning.md#10-group-fleet-size-tuning) |
| 6 | Conditional | [Name taskforce-side operations](naming-and-identity.md#6-name-taskforce-side-operations) |
| 15 | When editing native access | [Share repeated native names](native-and-ini-access.md#15-share-repeated-native-names) |
| 23 | When editing paths | [Share runtime campaign paths](naming-and-identity.md#23-share-runtime-campaign-paths) |
| 26 | Documentation | [Explain descriptor reward fallbacks](content-and-build.md#26-explain-descriptor-reward-fallbacks) |
| 27 | When editing the build | [Reduce build metadata duplication](content-and-build.md#27-reduce-build-metadata-duplication) |

## Working rules

- Confirm the named callers still exist before editing. Paths in area notes are
  relative to `src/CrimeanTom.SeaPower/` unless stated otherwise.
- Remove completed or invalidated items, their index rows, and stale references.
  Keep remaining IDs stable. Git holds the history; do not keep completed lists.
- Prefer a local helper or record over a new framework. Conditional items should
  proceed only if the callers become clearer without forwarding properties or flags.
- Preserve serialized keys, mission references, saved campaign indexes, RNG call
  order, and native callback timing. A successful build alone does not prove parity.
- Do not add test projects or permanent test files. Use the existing build and
  bounded comparisons; use the Sea Power testing skill when live proof is needed.

Keep theater regional pools separate from commander coalition eligibility.
Keep mission-specific trigger rules explicit around `MissionTrigger`.
Do not introduce template generators just to deduplicate the small native INI files.
