# Refactoring backlog

Remaining refactoring candidates. These require source and call-site checks before
implementation; they are not instructions to implement the whole list.

## Index

| # | Scope | Suggestion |
|---|---|---|
| 19 | Cross-cutting | [Clarify contract filename mapping](naming-and-identity.md#19-clarify-contract-filename-mapping) |
| 7 | UI | [Share window construction where layouts agree](ui.md#7-share-window-construction-where-layouts-agree) |
| 2 | Conditional | [Group fleet capability data](models.md#2-group-fleet-capability-data) |
| 10 | Conditional | [Group fleet-size tuning](fleet-planning.md#10-group-fleet-size-tuning) |
| 6 | Conditional | [Name taskforce-side operations](naming-and-identity.md#6-name-taskforce-side-operations) |
| 15 | When editing native access | [Share repeated native names](native-and-ini-access.md#15-share-repeated-native-names) |

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

Keep nation theater assignments separate from commander coalition eligibility.
Coalitions inherit theater coverage; that does not make all their members local.
Keep mission-specific trigger rules explicit around `MissionTrigger`.
Do not introduce template generators just to deduplicate the small native INI files.
