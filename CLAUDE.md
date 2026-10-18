# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Overview

**octo-platform-services** is an ASP.NET Core service that (1) serves the public, tenant-scoped `_configuration` discovery endpoint for OctoMesh's external clients (Refinery Studio, Office Integration, PowerBI / Power Query), (2) exposes read-only tenant/blueprint/drift observability endpoints, and (3) — since Phase 4 — **owns the `System.UI` CK model and its service-managed blueprints** (the cockpit dashboards + the cross-cutting `System.TenantMode` seed), applying them on every tenant lifecycle event, and installs the `System.Files` CK model + its default root (AB#6480). All three concerns were previously hosted by `octo-frontend-admin-panel`; this repo is the consolidation point so the legacy Admin Panel can be retired.

Phase history:
- **Phase 1 (done)**: extracted the `_configuration` discovery endpoint.
- **Phase 2 (done)**: tenant / blueprint / service-drift observability endpoints (`/system/v1/...`).
- **Phase 3 (done elsewhere)**: identity-data seeding moved to the `System.Identity.Bootstrap` blueprint in `octo-identity-services`.
- **Phase 4 (this repo, in progress — AB#4261)**: own the `System.UI` CK model + service-managed blueprints so `octo-frontend-admin-panel` can be retired. The strategic direction is to manage CK models more centrally through platform-services going forward.

### What changed in Phase 4

The Phase-1 "slim, single-endpoint" framing no longer holds. Owning the blueprints means platform-services now:
- depends on the CK runtime + MongoDB (`AddRuntimeEngine().AddMongoDbRuntimeRepository().AddMongoBlueprintSupport()` — already present for the Phase-2 observability endpoints), and
- runs the **distribution event hub** tenant-lifecycle host (`AddOctoServiceInfrastructure`) so it consumes `PosCreateTenant` / `PosUpdateTenant` events and seeds the blueprints on tenant create / attach / restore. This is the same runtime footprint the admin-panel backend had.

It deliberately seeds **no identity data**: the `octo-admin-panel` OIDC client the admin-panel used to seed is dead (only the removed admin-panel UI authenticated with it), so `DefaultConfigurationCreatorService` is built on `DefaultConfigurationCreatorServiceBase` (no identity-data command client) rather than `…Standardized`. See `docs/concepts/phase-4-system-ui-ownership.md`.

## The `_configuration` contract

The endpoint returns the 11-field `TenantConfigurationDto`:

```
assetServices, botServices, communicationServices, reportingServices,
issuer, systemTenantId, crateDbAdminUrl, grafanaUrl, meshAdapterUrl, aiServices,
mcpServices
```

Configured URLs are trailing-slashed via `EnsureEndsWith("/")`; an **empty string is served verbatim** and means "service not part of this installation" (AB#4884) — consumers key on it (e.g. the Refinery Studio Tenant Features panel shows "Not installed"). The optional services Reporting, AI and MCP default to empty: a URL only appears when the deployment announces it (`OCTO_PLATFORMSERVICES__REPORTINGSERVICEURL` etc. — Helm `externalUris.reporting` / `services.aiServices.publicUri`, locally Start-Octo's per-service announcements). A localhost default must never reach a browser. Phase 4 dropped the four OAuth client fields the legacy admin-panel `ClientDto` carried (`clientId`, `redirectUri`, `postLogoutRedirectUri`, `scope`) — they only described the retired admin-panel UI's own OIDC client, which no live consumer reads (Refinery Studio, Office, PowerBI, Power Query each bring their own client registration from their bundled `config.json` and consume only the issuer + service URLs here).

**Any DTO change forces every consumer to redeploy in lockstep.** The contract snapshot test (`tests/PlatformServices.ContractTests/`) fails CI loudly on field-set / property-name / trailing-slash drift — keep it as the source of truth and update consumers first if you intend a break.

## Build and Test Commands

```bash
# Local dev — always DebugL, uses NuGet packages from ../nuget/
dotnet build Octo.PlatformServices.sln -c DebugL

# Run locally (ports 5024 http / 5025 https)
dotnet run --project src/PlatformServices/PlatformServices.csproj -c DebugL --urls=http://localhost:5024

# Smoke test
curl http://localhost:5024/octosystem/_configuration
```

After modifying shared DTOs in `octo-sdk`, run `invoke-buildall -branch main -configuration DebugL -excludeFrontend $true -excludeAdditional $true` from `octo-tools` to distribute the updated NuGets before rebuilding this repo.

## Architecture

### Project Layout

```
src/PlatformServices/
├── Controllers/TenantConfigurationController.cs   # GET {tenantId}/_configuration
├── Controllers/{Tenants,Blueprints,Services}Controller.cs  # /system/v1 observability (admin-only)
├── Dto/TenantConfigurationDto.cs                  # 11-field response (OAuth client fields dropped in Phase 4; mcpServices added AB#4381)
├── Options/PlatformServiceUrlsOptions.cs          # bound from OCTO_PLATFORMSERVICES__* (URLs + broker)
├── Configuration/ConfigureDistributionEventHubOptions.cs  # broker wiring for the tenant-event host
├── Services/DefaultConfigurationCreatorService.cs # blueprint-only tenant bootstrap (System.UI.* + TenantMode)
├── Program.cs                                     # Observability + CORS + RuntimeEngine + ServiceInfrastructure + blueprints
├── Dockerfile                                     # mcr.microsoft.com/dotnet/aspnet:10.0-noble
├── appsettings.json
├── nlog.config
└── Properties/launchSettings.json                 # 5024 http / 5025 https
src/SystemUiCkModel/                               # System.UI CK model + 4 service-managed blueprints (moved from admin-panel)
├── ConstructionKit/                               # System.UI-2.11.0 model YAML, System floor [2.5,3.0) since 2.7.0 (AB#5528, wave-1 System 2.5.0 repin): UIElement, Dashboard, ProcessDiagram, SymbolLibrary/SymbolDefinition, Branding, TreeNavigationConfiguration (Roles + Perspectives), MappingCoverageConfiguration (per-tenant source-catalogue types for the data-mappings Orphan Sources tab, singleton rtWellKnownName 'MappingCoverage'), EntityForm (+ records EntityFormSection/Field/Column, AB#5521), Folder + association roles FolderItem/FolderParent (Studio library folders, 2.10.0, AB#3444)
└── Blueprints/{System.UI.SystemCockpit,System.UI.TenantCockpit,System.UI.EntityForms,System.TenantMode}/
```

### Local-dev ports

| Service | https | http |
|---|---|---|
| asset-repo | 5001 | 5000 |
| identity | 5003 | 5002 |
| admin-panel | 5005 | 5004 |
| reporting | 5007 | 5006 |
| bot | 5009 | 5008 |
| communication-controller | 5015 | 5014 |
| mcp | 5017 | 5016 |
| ai-services | 5019 | 5018 |
| mesh-adapter | 5020 | (none) |
| ai-worker | 5023 | 5022 |
| **platform-services** | **5025** | **5024** |

### Dependencies

- `Meshmakers.Octo.Services.Observability` — `/healthz/live`, `/healthz/ready`, Prometheus scrape, OpenTelemetry. The 15-second startup grace before `/healthz/ready` flips to 200 is shared with every Octo service (`StartupBackgroundService`).
- `Meshmakers.Octo.Communication.Contracts` — `CommonConstants.OctoApiFullAccess` (admin policy), `CommonConstants.GetScopes`, and transitively `Meshmakers.Common.Shared` (for `EnsureEndsWith`).
- `Meshmakers.Octo.Runtime.Engine.MongoDb` — CK runtime + Mongo blueprint support (`IBlueprintService`, `ISystemContext`, `ITenantBlueprintInstallations`) for both the observability endpoints and the System.UI blueprint apply.
- `Meshmakers.Octo.Services.Infrastructure` — `AddOctoServiceInfrastructure` (distribution event hub tenant-event host + `IDefaultConfigurationCreatorService` lifecycle) and `InfrastructureCommon.ClaimScope`.

Phase-1 NOTE (now obsolete): the service used to avoid Infrastructure / the CK runtime to stay slim. Phase 4 owns the System.UI blueprints, which requires both. It still does **not** seed identity data — see §"What changed in Phase 4" and the config-creator's class doc.

### `System.Files` CK model install and the default root (AB#6480)

platform-services installs the **System.Files** CK model (platform file system; source in
octo-construction-kit-engine, package `Meshmakers.Octo.ConstructionKit.Models.System.Files` from the libs train)
into every tenant and creates its default root `Files`. asset-repo and report-services import nothing for it.

- **Install = `ServiceManagedCkModelDescriptor` only** (`Program.cs`, same mechanism as System.UI): the engine imports
  the model at its embedded version on tenant resolve, once per process and tenant, with a downgrade guard. A tenant
  that already has `System.Files-1.0.0` (installed by asset-repo before) is skipped: no second import, no ERROR, no
  entity change. System.Files has no seed, so there is no blueprint. There is deliberately **no** explicit install in
  `SetupTenantAsync`: nothing in asset-repo needs the model during its own setup, so the order of the services at a
  tenant start is irrelevant.
- **Default root = `DefaultFilesRootService`**, called from `DefaultConfigurationCreatorService.SetupTenantAsync`
  right after the tenant is resolved (that resolve installs the model), before the blueprint apply: a plain
  `System.Files/FolderRoot` with `name` and well-known name `Files` (no protection flag on the entity - the asset-repo
  mutation guard protects roots by type; owner = the admin session). It is what asset-repo created at its tenant start
  before. `SetupTenantAsync` runs for new tenants, attach/restore (PosCreate), full updates (PosUpdate), and the
  cold-start iteration of **all** tenants including the system tenant; a CacheOnly update skips the setup (shared
  consumer, AB#5497) and the nightly TenantMode re-apply only force-applies the `System.TenantMode` blueprint, so
  neither touches System.Files.
- **Observable**: `DefaultFilesRootService` logs at Information `Ensuring default file root 'Files' for tenant ...` at the start, `Default file root already exists for tenant ...: N root(s), rtId ...` on the exists path and `Created the default file root ...` when it seeds one, so a tenant without a root can be told from a healthy one in the logs (AB#6482 finding on test-2).
- **Idempotent**: one indexed query when the root exists (matched case-insensitively on the well-known name, as the
  file API resolves roots). Every pod runs the setup, so two pods can seed `Files` at once (no unique index on
  well-known names): the root wins that the file API answers (exact-case `Files` first, then the oldest - the order of
  asset-repo's `FindRootAsync`), empty others are erased (also on a later run), a duplicate with content is kept and
  logged; a failed clean-up only logs.
- **A failed install is fatal for that tenant's setup (Gerald's decision).** The engine's descriptor import logs its
  cause and does not throw (missing dependency, `ResolveFailed`), so `DefaultFilesRootService` checks
  `IsCkModelSatisfiedAsync(System.Files-1.0.0)` afterwards, imports again if it is missing (`ImportCkModelWithDowngradeGuardAsync`:
  the descriptor's per-process guard is spent after one failed attempt, so a retry that only re-checked would heal nothing)
  and only then throws `SystemFilesSetupException` ("System.Files could not
  be set up for tenant 'x': <cause, e.g. the tenant's System model is older than System-2.5.0>. Model: System.Files-1.0.0").
  The creator passes the engine's `ITenantSetupRetryStore` to the base, so `SetupAsync` records the failure durably and the
  background loop (`FailedTenantRetryBackgroundService`: first run 30 s after start, every 30 s) re-runs the setup: constant
  60 s between attempts per tenant, **10 attempts**, a 5 min lease (single-flight across pods), 10 tenants per tick; after the
  budget the entry stays for an operator (a new tenant event, a restart or a successful setup clears/resets it). No
  exponential backoff, no unbounded retry. One tenant never blocks another (the cold-start loop catches per tenant and
  records the retry). A tenant that no longer exists keeps its bare `TenantException` (`IsTenantNotFound`), so the retry loop
  drops the entry (AB#4829) instead of retrying it 10 times.
- **Only System.Files failures are retried.** The base records *every* exception of `SetupTenantAsync` in the store it gets, so
  the creator passes a `SystemFilesOnlyTenantSetupRetryStore` wrapper: `RecordFailureAsync` is forwarded for a System.Files
  failure (typed marker set right before the `SystemFilesSetupException` is thrown; the exception's message prefix as fallback)
  and for a tenant that is already queued (a retry in progress still counts its attempt and releases the lease); claim, clear,
  release and list are forwarded unchanged. Not retried, exactly as before: System.UI / TenantMode blueprint failures and every
  other setup error (logged, thrown, picked up by the next event or restart). Bounds of the retry: first tick 30 s after start,
  tick every 30 s, 60 s between attempts per tenant, 10 attempts, then the entry waits for an operator.
- **The other steps are not blocked.** In `SetupTenantAsync` the Files failure is caught, the System.UI/TenantMode blueprint
  apply still runs, and the failure is thrown last. Both paths are real: `DeferTenantStart` is a per-instance flag, set true only on the instance of
  `DefaultConfigurationInitializationService` (cold start; never reset - only the Standardized base resets it) while
  `IDefaultConfigurationCreatorService` is registered transient, so the event consumers and the retry loop get fresh instances
  with `false`. Cold start: thrown at the end of `SetupTenantAsync`; lifecycle events and retries: after `RefreshTenantStateAsync` (the TenantMode
  force-refresh, which the base skips when the setup throws). **System tenant fails hard (AB#6576, Gerald's decision 2026-10-11):** without System.Files the file capability answers
  `SYSTEM_FILES_MISSING` for every tenant, so a System.Files failure in the **system tenant** is thrown (after System.UI / System.TenantMode
  were applied) - `SystemFilesSetupException`, original exception preserved, message names System.Files, `System.Files-1.0.0` and the
  system tenant. The system tenant's setup is unguarded in the host start (`DefaultConfigurationInitializationService`, collected by
  `HostedInitializer`), so the **service start fails**: the process exits before Kestrel serves anything, `/healthz/live` and `/healthz/ready` never
  answer (pod not ready, crash-loop; Kubernetes keeps the previous ReplicaSet in a rolling update). What the operator sees: a CRITICAL log line
  "System.Files could not be set up in the SYSTEM tenant ..." plus the host start error. Recovery: fix the cause named in the message (usually a
  System model older than System-2.5.0 or an unreachable/readonly tenant database) and restart/roll out again; an already installed
  `System.Files-1.0.0` is untouched and a healthy system tenant starts as before. Child tenants keep the retry path above (60 s / 10 attempts, other
  tenants and the host are not blocked).
- **Edge**: edge clusters run the communication operator and adapters only; the tenant database, asset-repo and
  platform-services are central, so edge pools use the System.Files of the central tenant. A self-contained
  installation with asset-repo but `platformServices.deploy: false` never gets System.Files
  (`SYSTEM_FILES_MISSING` in the file API) - deploy platform-services or import the model with octo-cli.
- Tests: `tests/PlatformServices.IntegrationTests` (Testcontainers MongoDB): install-once + root for a new and for
  the system tenant, idempotent repeated setup, tenant that already has the model and a root (left untouched), failing install (setup throws, retry recorded, System.UI and the TenantMode refresh still run, another tenant unaffected, retry succeeds once the model is resolvable, cold-start path, system tenant fails hard / healthy system tenant starts), concurrent pods, duplicate clean-up.

### `System.TenantMode` blueprint (1.1.0, AB#5497)

`src/SystemUiCkModel/Blueprints/System.TenantMode/` seeds the singleton `System/TenantModeConfiguration`
(`rtWellKnownName TenantMode`) on every tenant. Since **1.1.0** the seed also writes
`System/PublishWorkloadObservability` and `System/PublishCkModelObservability` = `true`: the platform
default is "observability on" (the System CK defaults both to `false` so a forgotten test tenant costs
nothing — on a platform that left every new tenant dark until an operator remembered the switch). Done in
the service-managed seed on purpose: a System CK bump forces a re-release of every externally built
adapter (AB#5491). The `ckModelDependencies` floor is `System-[2.3,3.0)` — 2.3.0 (AB#5432) is the first
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
  AB#5497's follow-up stamps the attributes `isRuntimeState` on the System CK, this is the trade-off.
- **Nightly reset bug (the AB#5497 incident).** bot-services' `AttributeValueAggregatorJob` publishes
  `PosUpdateTenant` with `TenantUpdateScope.CacheOnly` for every tenant at 00:00Z. Until
  octo-common-services honoured the scope, that ran `SetupAsync` → `RefreshTenantStateAsync` here every
  night and reset the opt-in; all `octo.workload.*` / `octo.pipeline.*` metrics went dark on prod-1.

### `System.UI.TenantCockpit` blueprint (1.3.0, AB#5558)

Seeds the `cockpit` board (`System.UI/Dashboard`, rtWellKnownName `cockpit`, 6 columns) of every
non-system tenant — the Refinery Studio's Home › Cockpit.

- **1.1.0** adds the octo-meshboard cockpit widgets (`provideCockpitWidgets()`, AB#5558):
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

### Ownership markers are enforced by the build (AB#6327)

`SystemUiCkModel.csproj` sets `OctoEnforceRuntimeStateMarkers=true`: the `CkLintRuntimeStateMarkers` task (CK MsBuildTasks
package) fails the build with `OCTO-CK001` when an attribute in `ConstructionKit/attributes/*.yaml` declares no `ownership`
(or the deprecated `isRuntimeState`). AB#6327 restated the existing behaviour: all 128 attributes got `ownership: SeedOwned`.
Same resolved ownership, so `octo-ckc ValidateVersion` reports "no bump required" and System.UI stays 2.10.0.
The 12 `Dashboard.*` / `DashboardWidget.*` attributes carry the inline comment "ownership under review in AB#6328 (audit F9,
dashboard policy decision pending)": the app blueprints seed dashboards, so a seed version bump resets a board a tenant
customised. Changing them is a behaviour change (System.UI minor) and belongs to AB#6328.
**Review question for every new attribute:** could an operator or user type this value in the product? Yes → `TenantOwned`
(or `Secret` for a credential); a service writes it → `RuntimeState`; the product ships and corrects it → `SeedOwned`.

### Deprecated `Theme.Colors` (System.UI 2.11.0, AB#6691)

`System.UI-2.0.0` defined the record `Theme.Colors` (attributes `PrimaryColor`, `SecondaryColor`, `TertiaryColor`, `NeutralColor`) and the
record-valued attribute `Theme.Colors`; a 2.x release dropped them silently (2.2.0 and every later published version lack them; superseded by
`Branding.LightTheme` / `ThemePalette`). The published, exact-pinned `EnergyCommunity-2.0.0` (and `EnergyCommunity.Registration-2.8.0` through it)
still references `System.UI-2.0.0/Theme.Colors-1`, so the cascade check (`ValidateCascade`, CI step "CK cascade check") reports `Breaks` for every
**new** System.UI version (an already published, unchanged version is skipped with `-sp`, which is why 2.10.0 never failed). The elements are
back, unchanged in shape and marked DEPRECATED (`attributes/attributes-theme-legacy.yaml`, `records/themeColors.yaml`): purely additive, so the bump
stays MINOR. Do not remove them again without a major bump; new models use `ThemePalette`.

### `System.UI/Folder` + `System.UI/FolderItem` / `System.UI/FolderParent` (System.UI 2.10.0, AB#3444)

Folders of the Refinery Studio library (concept `wave3-concepts/data-explorer-and-queries-concept.md` §3.4).
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

### `System.UI.EntityForms` blueprint (1.5.0, AB#5521 / AB#5523 / AB#5524 / AB#5547 / AB#5528 / AB#5884)

`System.UI/EntityForm` (System.UI **2.8.0**, Minor, no migration; 2.9.0 adds
`EntityFormField.ReferenceDisplayAttributes`, Minor, no migration — both shifted one minor up behind
the System 2.5 repin release System.UI 2.7.0 from `main`, AB#5528, which has no EntityForm) describes how the Refinery Studio lists,
creates and edits entities of a CK type: sections, fields, list columns and capabilities, as flat record
arrays `Sections` / `Fields` / `ListColumns` (records `EntityFormSection` / `EntityFormField` /
`EntityFormColumn`). The Studio renders generic Settings pages from it instead of one hand-written page per
configuration type (concept: `octo-frontend-refinery-studio/docs/concepts/studio-shell-and-entity-forms.md` §5).

- **Where forms live.** Delivered forms are seed data of the service-managed blueprint
  `src/SystemUiCkModel/Blueprints/System.UI.EntityForms/` (registered in `Program.cs` like the cockpits;
  applied to every tenant because of the `System.UI.` prefix). Not in `defaultData.yaml` (a leftover nothing
  reads) and not in the communication controller (it would need a System.UI dependency and race the
  System.UI install). 1.0.0 ships `form-default` (target `System/Entity`, `IncludeDerivedTypes: true`,
  `Priority: 0`, no `Category`) plus the six wave-1 forms (SFTP, Grafana, Discord, Loxone, E-Mail sender,
  E-Mail receiver). 1.1.0 adds the wave-3 forms WeClapp, EDA (exact type only) and Energy Community
  (`Category: connections`, rtIds `…20`–`…22`). Bump the blueprint version on every seed change: the next
  cold start rolls the higher embedded version forward on every tenant (no `Program.cs` change, the DI
  extension is keyed on the major version). Until then the Studio still shows these types under
  Settings › Connections › *All configurations* (form-default).
- **1.2.0 (AB#5524)** adds waves 2 and 4 and retires the last hand-written Studio configuration pages:
  `…30`–`…34` SAP, Microsoft Graph, finAPI (`form-finapi-configuration`), Helm repository, Service
  accounts (`connections`); `…40`–`…42` AI configuration, AI agent configuration, AI quota limit (`ai`;
  agent config and quota limit are created by the AI service, so `CanCreate`/`CanDelete: false`);
  `…50`–`…51` Tenant mode and Tenant configuration (`tenant`). Credentials (SAP `Password`, Graph / finAPI
  `ClientSecret`, finAPI / Helm `Password`, AI `ApiKey`, service account `ClientSecret`) are
  `Editor: password` + `Secret: true` (write-only in the Studio). Helm repository shows the inbound
  `System.Communication/HelmRepository` association as a read-only `Used by` reference field.
  - **Tenant mode** is the only singleton: `Singleton: true`, `SingletonWellKnownName: TenantMode` (the
    entity `System.TenantMode` seeds); the former page's field hints are `Help` texts.
  - **Tenant configuration** and **AI configuration** are deliberately *lists*, not singletons as the
    concept's wave 4 says: `TenantConfiguration` is the engine's key/value store (one entity per key,
    `TenantContext.SetConfigurationAsync`), and pipelines reference several named `AiConfiguration`s via
    `ApiKeyConfigurationName`. A singleton form would hide all but one entry.
  - **Service accounts** stay a hand-written Studio page (rotation flow, concept §5.9). The form gives
    them a Settings entry and list columns; `CanEdit: false` makes every generic view read-only — the
    Studio routes list / create / edit of this entry to its custom page.
  - The Studio carries built-in copies of these entries (`settings-fallback-forms.ts`) that apply only
    where its resolution would otherwise end at `form-default`, i.e. until this version is rolled out
    by a cold start. Keep both in step when changing a wave-2/4 form here: the Studio checks its
    copies against a snapshot of this seed (`entity-forms-seed-1.4.1.snapshot.ts`), which must be
    regenerated too.
- **1.3.0 (AB#5547)** moves the detail forms of the communication runtime objects into the seed:
  `…60` `form-adapter`, `…61` `form-pool`, `…62` `form-application`, `…63` `form-data-flow` (targets
  `System.Communication/Adapter|Pool|Application|DataFlow`, `IncludeDerivedTypes: true`, no `Category` —
  they shape the Studio's detail pages, not Settings entries; `CanDelete`/`CanDuplicate`/`CanExport: false`,
  deletion and moves are page actions). `GenerateRemainingFields: true` with the section title "Further
  attributes", so attributes of derived types (e.g. a mesh adapter subtype) still appear; every inherited
  runtime-state attribute (`DeploymentState`, `StatusMessage`, `LastDeploymentError*`,
  `CommunicationState*`, `ConfigurationState`, `LastConfigurationError*`, `LifecycleState`,
  `LastActivityAt`, `OnDemandCapable`, `OnDemandBlockingReasons`, adapter `LastSyncedSequenceNumber`) and
  the encrypted `Values` overrides are listed `Hidden: true`. Defaults: pool `Environment` = `Edge`,
  adapter `LifecycleMode` = `AlwaysOn`, `IdleTimeoutMinutes` = `30` (visible only for `OnDemand`).
  The pool (`ManagedBy`, role `System.Communication/Manages`) is `ReadOnly: afterCreate` on the adapter
  (Move… re-homes it) and editable on the application; applications hide `LifecycleMode` /
  `IdleTimeoutMinutes`. Attribute names are checked against `SystemCommunicationCkModel` in
  octo-communication-controller-services.
  - The Studio keeps identical built-in copies (`runtime-object-forms.ts`) as fallbacks only and checks
    them against the snapshot (`entity-forms-seed-1.4.1.snapshot.ts`).
- **1.4.0 (AB#5547)** sets `ReferenceDisplayAttributes: "repositoryUrl,channel"` on the `HelmRepository`
  reference field of `form-adapter` and `form-application`, so the delivered forms' Helm repository picker
  shows `name · URL · channel` like the Studio fallbacks. Needs **System.UI 2.9.0**
  (`EntityFormField.ReferenceDisplayAttributes`, a comma-separated String — record values in blueprint seeds must be scalars, the seed's record converter cannot read a YAML sequence; the same applies to `RecordColumns` should a seed ever set it): the dependency floor in
  `blueprint.yaml` and the seed `dependencies` are 2.9.0. Entries are target attribute names in camelCase
  (the picker passes them verbatim as GraphQL `attributeNames`); list only non-secret attributes.
- **1.4.1 (AB#5528)** only raises the System.UI floor (`blueprint.yaml` + seed `dependencies`) to 2.9.0
  after the studio-rebuild System.UI versions moved one minor up (2.7.0 is the System 2.5 repin
  release from `main`). The forms are unchanged; the patch bump rolls the new floor forward.
- **1.5.0 (AB#5884)** makes the communication detail forms work on **System.Communication 3.x and 4.x**
  (4.0.0 renamed `Pool` -> `DeploymentSite` and `Manages`/`ManagedBy` -> `Hosts`/`HostedBy`, no alias).
  `form-adapter` / `form-application` carry **both** reference fields in `general`, order 1: `ManagedBy`
  (`Pool`, role `System.Communication/Manages`) and `HostedBy` (`DeploymentSite`, role
  `System.Communication/Hosts`, label "Deployment site", same `ReadOnly` as the pool field). The Studio's
  resolver (octo-ui/entity-forms `entity-form-resolver.ts`) skips a `reference` field whose
  `AssociationRoleId` is not a role of the tenant's type, with a warning, so exactly one renders per major.
  `TargetCkTypeId` is a single type id, so `form-pool` (`…61`, unchanged, stable name) cannot also target
  `DeploymentSite`: the new `…64` `form-deployment-site` (same fields) does; only the form whose type exists
  in the tenant resolves. Neither form has an association field (the Studio fallback has none either), so
  no `Hosts`/`Manages` field on the site/pool form. Still no `System.Communication` dependency — and none may
  be added: it would have to admit both majors. The Studio snapshot `entity-forms-seed-1.4.1.snapshot.ts`
  needs refreshing to 1.5.0.
- **Naming.** `rtWellKnownName` = `form-<kebab-type>`, e.g. `form-sftp-configuration` (no colon).
- **Tenant overrides.** A tenant customises a delivered form by creating its **own** `EntityForm` for the same
  `TargetCkTypeId` (empty `rtBlueprintSource`). Never edit a delivered `form-*` entity: a blueprint re-apply
  upserts it (force re-apply rewrites every seeded attribute, see TenantMode above) and wipes the edit.
- **Resolution (one algorithm, no special case).** (1) Pick exactly one form F for type T: forms with
  `TargetCkTypeId = T`, else the nearest ancestor with a form where `IncludeDerivedTypes = true`;
  `form-default` on `System/Entity` always matches. Ties: tenant form beats seeded, then higher `Priority`.
  Forms are never merged. (2) Fields = F.Fields whose `AttributePath` exists on T (unknown paths skipped
  silently), minus `Hidden`. (3) Unless `GenerateRemainingFields = false` (absent = true), all other
  attributes of T are appended in model order into section `GeneratedSectionTitle` (default "Further
  attributes"). (4) If `form-default` is deleted, the Studio falls back to a built-in constant equal to the
  seed and logs a warning.
- **Normative defaults.** No form → `form-default`. Abstract T → `CanCreate` forced false, list shows derived
  instances, create asks for a concrete subtype. Singleton is never implied (only `Singleton` +
  `SingletonWellKnownName`). No write permission → read-only, create/edit/delete hidden regardless of F. A
  form appears on Settings home only if `Category` is set. Capability defaults when absent:
  Create/Edit/Delete true, Duplicate/Export false. Associations become reference fields only when a form
  lists them; `rtId` is never a field; system timestamps and `RtBlueprint*` are never editable; Secret
  attributes/fields are write-only.
- **String values, not CK enums.** `Editor` (auto|text|multiline|password|number|toggle|enum|datetime|url|
  email|json|yaml|cron|reference|records; unknown = auto), `ReadOnly` (never|always|afterCreate), `Width`
  (full|half) and `Display` (text|chip|date|mono) are Strings. System.UI uses no enums, and CK enums store
  numeric keys, so every new value would force a Minor bump plus schema regeneration. `Required` is an
  optional Boolean: absent = from the model's `isOptional`; `true` makes it stricter; `false` cannot loosen
  a mandatory attribute.
- **Record seed format** is the runtime import format (`ckRecordId` + `attributes`), not a `{ Key: x }`
  shorthand:

  ```yaml
  - id: System.UI/EntityForm.Sections
    value:
      - ckRecordId: System.UI/EntityFormSection
        attributes:
          - { id: System.UI/EntityFormSection.Key, value: server }
          - { id: System.UI/EntityFormSection.Title, value: Server }
          - { id: System.UI/EntityFormSection.Columns, value: 2 }
  ```
- **Verify** after import: `runtime { systemUIEntityForm { items { rtWellKnownName targetCkTypeId
  sections { key title } fields { attributePath editor secret } } } }`.

### Swagger UI / OpenAPI (AB#4388)

The service exposes Swagger UI at `/swagger` via the shared `Meshmakers.Octo.Services.Swagger`
package (same pattern as octo-mcp-service): `AddOctoApiVersioningAndDocumentation(...)` +
`.AddVersion()` in DI, `app.UseOctoApiVersioningAndDocumentation()` in the pipeline,
`ConfigureOctoOpenApiOptions` feeds the OAuth authority from `PlatformServiceUrlsOptions.AuthorityUrl`.
The UI's authorization-code+PKCE flow uses the `octo-platformServices-swagger` client seeded by the
`System.Identity.Bootstrap` blueprint (≥1.2.0, `${octo.platform.publicUrl}` — "platform" slug in
`IdentityBlueprintVariableProvider`). `wwwroot/css/swagger.css` carries the shared styling
(served via `UseStaticFiles`).

### Tenant authorization: no gate here, on purpose (AB#5051)

This service **does not** call `UseOctoTenantAuthorization()` / `AddOctoTenantAuthorization(...)`, and
that is a decision, not an oversight. The shared gate (`TenantAuthorizationMiddleware` in
octo-common-services, see that repo's CLAUDE.md) compares the `{tenantId}` **route value** against the
caller's `tenant_id` claim. Neither route shape here wants that:

| Route | Why the gate does not apply |
|---|---|
| `GET {tenantId}/_configuration` | `[AllowAnonymous]` public discovery — Studio / Office / PowerBI read it *before* they have a token. The middleware skips anonymous endpoints, so wiring it would cover nothing. |
| `GET system/v1/tenants/{tenantId}/blueprints`, `…/ck-models` | Cross-tenant **operator** routes. The tenant id is the *subject being inspected*, not the tenant being addressed; the caller is a system-tenant admin enumerating child tenants. `GetTenantId()` reads the route value regardless of the `system/` prefix, and the middleware's **user-token path is unconditional** (only the *service*-token path is staged behind `LogOnly`) — so wiring the gate would 403 exactly the use case these endpoints exist for. |

platform-services is the only service in the estate with a `system/.../{tenantId}/...` route shape;
identity, asset-repo, bot, communication-controller and MCP all keep `{tenantId}` strictly as the
addressed tenant, which is why the gate fits there and not here. There is therefore also no
`OCTO_TENANTAUTHORIZATION__SERVICETOKENENFORCEMENT` knob on this deployment — an estate-wide switch to
`Enforce` is a no-op for platform-services by design.

**If a genuinely tenant-addressed, authenticated route is ever added here**, wire the gate for that
route only — `app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/system"), b => b.UseOctoTenantAuthorization())`
plus `builder.Services.AddOctoTenantAuthorization(builder.Configuration)` — and keep the `system/v1`
operator routes outside it.

**Known gap, not closed here:** the `system/v1/*` observability endpoints require only the `octo_api`
scope (`PlatformServicesAdminPolicy`), so a token minted against **any** tenant with that scope can
enumerate every tenant and read any tenant's blueprints / CK models. That is the same authorization
shape every other service uses for its `system/v1/*` surface, so it is an estate-wide design property
rather than a platform-services outlier — closing it means requiring the caller's `tenant_id` to be
the system tenant across the estate, which needs its own work item.

### Audience validation (AB#5051)

`options.Audience = CommonConstants.OctoApi` in `Program.cs` — the JWT bearer scheme **validates**
`aud=octoAPI`. It used to be `ValidateAudience = false`, which combined with the missing tenant gate
meant every token of the authority passed the transport check.

Why turning it on is safe: every endpoint that authenticates here demands the `octo_api` scope, and
that scope exists only on the `octoAPI` ApiResource seeded by `System.Identity.Bootstrap`, so identity
stamps `aud=octoAPI` on any token that carries it — verified on live local tokens for both the
`octo-cli` user flow (`aud: ["https://localhost:5017/", "octoAPI"]`) and a `client_credentials` service
account (`aud: ["octoAPI", "https://localhost:5017/"]`). The Swagger UI client
(`octo-platformServices-swagger`) requests `octo_api` as well. identity / bot /
communication-controller / ai-services already require the same audience.

🔴 Do **not** disable it again to make a 401 go away: a token without `aud=octoAPI` was minted without
the `octo_api` scope and would fail `PlatformServicesAdminPolicy` anyway. The anonymous
`_configuration` endpoint is unaffected either way.

### CORS

Anonymous endpoint hit from browser SPAs and Excel-hosted add-ins. No credentials in flight, so the global default policy is `AllowAnyOrigin / AllowAnyHeader / AllowAnyMethod`. Even though the service now references `Meshmakers.Octo.Services.Infrastructure` (for the tenant-event host), it deliberately does **not** activate that package's per-tenant `CorsPolicyProvider` — it keeps its own `AddCors` default policy, because the provider rebuilds policies per-tenant from `IdentityClient` origins and would break Office / PowerBI access ([[cors_policy_provider_overrides_named_policy]] in memory).

### Helm Deployment

Chart values block: `services.platformServices` in `octo-helm-core/src/octo-mesh/values.yaml`. Env-var section in `templates/_env.tpl` under `else if eq .name "platformServices"`. Since Phase 4 the service is **no longer plain** — it needs broker (RabbitMQ) + MongoDB connection config and applies service-managed blueprints on tenant events, the same footprint as `communication-controller`. The deployment env must now carry the `OCTO_PLATFORMSERVICES__BROKER*` + `OCTO_SYSTEM__DATABASE*` + `OCTO_BLUEPRINTS__*` settings (helm wiring is part of AB#4261); rolling-update concurrency of the blueprint apply should be reviewed against the other blueprint-owning services before the chart change ships.

Public URI per environment:
- test-2: `platform.test.octo-mesh.com`
- staging-1: `platform.staging.octo-mesh.com`
- prod-1/2: `platform.octo-mesh.com`

### Tests

`tests/PlatformServices.ContractTests/` snapshot-locks the rendered `TenantConfigurationDto` JSON (11-field set + JSON property names + trailing slashes) plus controller tests for the observability endpoints. Any DTO drift fails CI loudly — that is intentional. The baseline reflects the Phase 4 ten-field contract (the four OAuth client fields were removed) plus the additive `mcpServices` field (AB#4381).

`tests/PlatformServices.IntegrationTests/` (xUnit v3 + Testcontainers MongoDB, needs Docker) wires the same DI as `Program.cs` against a real replica set and runs the real `DefaultConfigurationCreatorService.SetupAsync` on fresh child tenants: System.Files install + default root (AB#6480). `dotnet test Octo.PlatformServices.sln -c DebugL`.

## CI / CD

Root-level `azure-pipelines.yml` follows the Phase 4a Layer-2 pattern: pulls shared templates from `octo-pipeline-templates@tpl-v1.7.0` (includes the .NET runtime check of the final image stage, minimum 10.0.13, AB#6724), builds + tests + pushes Docker image (private always, public on release tag), tags `:main-latest` on main. No Helm chart (the chart lives in `octo-helm-core`).

Since Phase 4 / AB#4261 the pipeline also **publishes the `System.UI` CK library + catalog** (moved from the retired `octo-frontend-admin-panel`):
- the shared `validate-and-publish-ck-versions` step publishes it after the tests — `main` to the private catalog, `r*` tags to the private **and** the public one, never replacing a published version (AB#5121). The `Build` step passes `/p:OctoPublishCkModel=false`: it compiles the model and publishes it nowhere;
- `handle-artifacts.yml` is given `constructionKitLibraryPaths: src/SystemUiCkModel/bin/$(buildConfiguration)/$(artifactsFrameworkVersion)/octo-ck-libraries/SystemUiCkModel`, so the `local` build artifact carries the generated `SystemUiCkModel` library docs.

This is what lets `octo-documentation`'s `collect-docs.yml` harvester pick up the `System.UI` library docs from `octo-platform-services-CI` — it replaces the removed `octo-frontend-admin-panel-CI` (`frontendAdmin`) producer.
