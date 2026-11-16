---
description: Rules for the System.UI CK model: ownership markers, the deprecated Theme.Colors elements and the Folder associations.
applies_to: src/SystemUiCkModel/ConstructionKit/**, src/SystemUiCkModel/SystemUiCkModel.csproj
---

# The System.UI CK model

## Ownership markers are enforced by the build

`SystemUiCkModel.csproj` sets `OctoEnforceRuntimeStateMarkers=true`: the `CkLintRuntimeStateMarkers` task (CK MsBuildTasks
package) fails the build with `OCTO-CK001` when an attribute in `ConstructionKit/attributes/*.yaml` declares no `ownership`
(or the deprecated `isRuntimeState`). Introducing the markers restated the existing behaviour: all 128 attributes got `ownership: SeedOwned`.
Same resolved ownership, so `octo-ckc ValidateVersion` reports "no bump required" and System.UI stays 2.10.0.
The 12 `Dashboard.*` / `DashboardWidget.*` attributes carry an inline comment that their ownership is under review (audit F9,
dashboard policy decision pending): the app blueprints seed dashboards, so a seed version bump resets a board a tenant
customised. Changing them is a behaviour change (System.UI minor) and belongs to that review.
**Review question for every new attribute:** could an operator or user type this value in the product? Yes → `TenantOwned`
(or `Secret` for a credential); a service writes it → `RuntimeState`; the product ships and corrects it → `SeedOwned`.

## System.UI on ckLanguage 2 (System.UI 2.12.0)

2.12.0 is the first `ckLanguage: 2` release of System.UI (CK v2 migration), a **Minor** against the published
2.11.0. Same pattern as System.Bot 3.6.0 and System.StreamData 1.23.0.

- **`derivable: Any` on every type and record.** ckLanguage 2 flips the default of `derivable` from `Any` to `Model`, which is a
  Major of its own (row T6 of `octo-construction-kit-engine/docs/ck-semver-rules.md`). All 11 types (the abstract base `UIElement`
  included) and all 8 records therefore declare `derivable: Any`. **A new type or record defaults to `Model`**: declare
  `derivable: Any` where another model should derive from it or use it.
- **No System interfaces.** `Named-1`, `Describable-1` and `Hierarchical-1` live in `System` 2.7+. 2.11.0 resolved System 2.5.0, so
  raising the floor of `System-[2.5,3.0)` to 2.7 is a Major (row D1). The folder tree uses `FolderParent`, not `System/ParentChild`,
  so `Hierarchical-1` would not fit `Folder` anyway. Revisit with the next System.UI major.
- **No access tightening.** All attributes are `SeedOwned` and written through the generic GraphQL API by Refinery Studio /
  `@meshmakers/*` (meshboards: `meshboard-persistence.service.ts`, process diagrams, symbol libraries, library folders:
  `query-folder.service.ts`, tree navigation settings, entity forms) or by the blueprints. None is service-written
  (`RuntimeState`), none is a credential (`EntityFormField.Secret` is a boolean display flag) and none is `securitySensitive`, so
  `ReadOnly`/`Hidden` would be a Major (row T7). 2.12.0 uses up the one-time T7a exception of System.UI without tightening anything.
- **No methods.** platform-services has no write action for UI entities (no REST POST/PUT/PATCH/DELETE, no GraphQL mutation); Studio
  writes them through the generic API, so there is no operation a CK method would replace.
- **Engine and catalog.** The compiler stamps `minEngineVersion` 3.5.1 (unchanged from 2.11.0); the model is published under
  `ck-models/v3/` only. A tenant on 2.11.0 moves to 2.12.0 by a service restart (service-managed descriptor, downgrade guard).
- **Tests.** `tests/PlatformServices.ContractTests/CkModels/SystemUiCkLanguage2Tests.cs` (embedded model: version, language,
  `minEngineVersion` pinned to 3.5.1, v3 root, System range unchanged, `derivable: Any` and `Public` on every element, nothing
  tightened, implemented or declared as a method) and `tests/PlatformServices.IntegrationTests/SystemUiCkLanguage2UpgradeTests.cs`
  (a tenant set up by a pod with the published 2.11.0 snapshot from `testData/ck-models/` moves to 2.12.0 by two restarts, no
  `ResolveFailed`, `derivable: Any` survives the MongoDB round trip, the seeded UI entities stay).
- **Cascade.** `ValidateCascade`: `EnergyCommunity.Registration` 2.10.0/3.1.0 Compatible; the exact-pinned legacy
  `EnergyCommunity-2.0.0` reports `NeedsRepin` exactly as for the unchanged 2.11.0 (it pins `System.UI-2.0.0`).

## Deprecated `Theme.Colors` (System.UI 2.11.0)

`System.UI-2.0.0` defined the record `Theme.Colors` (attributes `PrimaryColor`, `SecondaryColor`, `TertiaryColor`, `NeutralColor`) and the
record-valued attribute `Theme.Colors`; a 2.x release dropped them silently (2.2.0 and every later published version lack them; superseded by
`Branding.LightTheme` / `ThemePalette`). The published, exact-pinned `EnergyCommunity-2.0.0` (and `EnergyCommunity.Registration-2.8.0` through it)
still references `System.UI-2.0.0/Theme.Colors-1`, so the cascade check (`ValidateCascade`, CI step "CK cascade check") reports `Breaks` for every
**new** System.UI version (an already published, unchanged version is skipped with `-sp`, which is why 2.10.0 never failed). The elements are
back, unchanged in shape and marked DEPRECATED (`attributes/attributes-theme-legacy.yaml`, `records/themeColors.yaml`): purely additive, so the bump
stays MINOR. Do not remove them again without a major bump; new models use `ThemePalette`.

## `System.UI/Folder` + `System.UI/FolderItem` / `System.UI/FolderParent` (System.UI 2.10.0)

Folders of the Refinery Studio library (concept wave3-concepts/data-explorer-and-queries-concept.md §3.4, not in this repository).
Minor, purely additive, no migration, System floor unchanged. `Folder` (derives from `UIElement`) has `Name`,
optional `Description`, `Folder.Scope` (String, e.g. `Queries`; folders of different scopes are separate
trees) and optional `Folder.SortOrder` (Int). Subfolders use the role `FolderParent` (Folder → Folder,
outbound `Parent` ZeroOrOne, inbound `Children` N); top-level folders have no `FolderParent` edge. The role
`FolderItem` (Folder → `System/PersistentQuery`, outbound `Items` N, inbound `Folder` ZeroOrOne) places a
query in a folder. Deliberately NOT `System/ParentChild`: its outbound multiplicity is `One`, and the engine
rejects inserting an entity whose type declares it without a parent edge, so root folders would be impossible. No blueprint seeds folders, and no blueprint needed a bump (EntityForms/cockpits pin ranges).

Engine behaviour (verified in `octo-construction-kit-engine`, `GraphRuleEngine`):
- **Multiplicity is enforced** on every `ApplyChanges` write: a second `FolderItem` edge to the same query (or
  a second `FolderParent` of a folder) is rejected (`AssociationCardinalityViolationOnModification`). Moving =
  Delete(old) + Create(new) in ONE mutation.
- **RT import does NOT enforce it** (`ImportRtModelCommand` writes edges without the rule engine): importers
  must not create duplicate `FolderItem` / `FolderParent` memberships.
- **Only outbound `One` is mandatory on insert** (`GraphRuleEngine` filters `Multiplicity == One`), so a
  `Folder` without a `FolderParent` edge (ZeroOrOne) can be created.
- **Delete does not cascade**: it removes every edge of the deleted entity (both directions). Subfolders of a
  deleted folder silently become top-level and its queries end up in no folder — clients must move children
  (subfolders and items) up to the parent before deleting. A deleted query drops its `FolderItem` edge.
- **Studio "Export Query"** exports edges only when both ends are exported, so folder membership does not
  travel with a query (intended: imported queries land in "no folder").
