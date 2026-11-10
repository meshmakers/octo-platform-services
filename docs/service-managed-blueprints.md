---
description: The System.TenantMode and System.UI cockpit blueprints: version bumps, roll-forward, force re-apply and the cockpit widget layout.
applies_to: src/SystemUiCkModel/Blueprints/System.TenantMode/**, src/SystemUiCkModel/Blueprints/*Cockpit/**, src/PlatformServices/Services/DefaultConfiguration*.cs
---

# Service-managed blueprints: TenantMode and cockpits

## `System.TenantMode` blueprint (1.1.0)

`src/SystemUiCkModel/Blueprints/System.TenantMode/` seeds the singleton `System/TenantModeConfiguration`
(`rtWellKnownName TenantMode`) on every tenant. Since **1.1.0** the seed also writes
`System/PublishWorkloadObservability` and `System/PublishCkModelObservability` = `true`: the platform
default is "observability on" (the System CK defaults both to `false` so a forgotten test tenant costs
nothing — on a platform that left every new tenant dark until an operator remembered the switch). Done in
the service-managed seed on purpose: a System CK bump forces a re-release of every externally built
adapter. The `ckModelDependencies` floor is `System-[2.3,3.0)` — 2.3.0 is the first
System version carrying the two attributes; keep it in step with `seed-data/entities.yaml` `dependencies`.

- **Version bump = `blueprintId` in `blueprint.yaml` only.** The `BlueprintSourceGenerator` keys the DI
  extension on the *major* version (`AddBlueprintSystemTenantModeV1`), so a minor bump changes neither
  `Program.cs` nor the generated class names.
- **Roll-forward is automatic.** `SetupTenantAsync` → `ApplyServiceManagedBlueprintsAsync` picks the newest
  embedded version per name and calls `ApplyBlueprintAsync` without force; the engine re-imports the seed
  whenever the installed row's id differs from the embedded one (`willImportSeed`), so every tenant on
  1.0.0 is upgraded — and switched on — during the first cold start after the rollout.
- **Force re-apply does NOT preserve operator edits.** `RefreshTenantStateAsync` (attach / restore / Enable
  / full `PosUpdateTenant`) applies with `force:true`, and the Upsert import rewrites every seeded attribute
  that is not `isRuntimeState` in the System CK: `MaintenanceLevel` back to Off, both observability
  switches back to true. Edits survive only the non-force same-version re-apply (cold start). Until
  a follow-up stamps the attributes `isRuntimeState` on the System CK, this is the trade-off.
- **Nightly cache-only update.** bot-services' `AttributeValueAggregatorJob` publishes
  `PosUpdateTenant` with `TenantUpdateScope.CacheOnly` for every tenant at 00:00Z. octo-common-services honours the scope
  and does not run `SetupAsync` → `RefreshTenantStateAsync` for it, so the opt-in is not reset.

## `System.UI.TenantCockpit` blueprint (1.3.0)

Seeds the `cockpit` board (`System.UI/Dashboard`, rtWellKnownName `cockpit`, 6 columns) of every
non-system tenant — the Refinery Studio's Home › Cockpit.

- **1.1.0** adds the octo-meshboard cockpit widgets (`provideCockpitWidgets()`):
  `…5c` "Needs attention" (`attentionList`, row 1, full width, `{"maxItems":6}` = all checks),
  `…5d` "Adapters online" (`adapterStatus`), `…5e` "CK models" (`ckModelState`), `…5f` "Pipeline
  executions 24 h" (`pipelineExecutions`) in row 2 (2 columns each); the CK-model pie (`…5b`) moves
  to rows 3–4. The widgets have `DataSourceType static` and their `Config` JSON is exactly what
  octo-meshboard's `toPersistedConfig` writes — `cockpit-widget-registrations.spec.ts` in
  octo-frontend-libraries holds these rows as a fixture (and compares it with this file in a
  worktree pair): **change both together**. Each check runs only for viewers with its roles.
- **1.2.0** gives "Needs attention" rows 1–2 (rowSpan 2: one 200 px row cut off the finding
  cards' action links), KPIs row 3, pie rows 4–5 (fixture in octo-meshboard updated alongside),
  and replaces the technical board description with a user-facing one ("Status of this
  tenant at a glance"); `System.UI.SystemCockpit` **1.0.1** likewise ("Status of the OctoMesh
  installation at a glance"). The Studio's Home shows neither name nor description (greeting +
  board tabs, the board embedded with `headerMode: 'compact'`); UI › MeshBoards and the board
  manager still do, so descriptions are user-facing text — never implementation notes.
- **1.3.0** adds `…60` "Recently opened" (`recentItems`, rows 4–5, columns 4–6, `{"maxItems":8}`)
  next to the CK-model pie, which widens to 3 columns (wireframe Home row "Construction Kit Models |
  Recently opened"). The entries are per viewer (the Studio's Cmd+K history via the host source
  `COCKPIT_RECENT_ITEMS`); the board stores only the row count. The Studio shows its separate
  "Recently opened" panel only as a fallback when the cockpit lacks the widget.
- **Roll-forward** as for TenantMode: the minor bump re-imports the seed on the next cold start;
  the Upsert rewrites the seeded widgets including their position, widgets the tenant added stay.
  A tenant widget placed where a new seeded widget lands is not moved by the import;
  octo-meshboard resolves the overlap when the board loads (`resolveOverlaps` pushes a widget
  down, display only until the board is saved via Customise board).
- The Studio keeps its hard-wired Home strips only as a fallback for boards without any cockpit
  widget (tenants before the roll-out, or a tenant that removed them).
