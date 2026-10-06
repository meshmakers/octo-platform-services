# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Overview

**octo-platform-services** is an ASP.NET Core service that (1) serves the public, tenant-scoped `_configuration` discovery endpoint for OctoMesh's external clients (Refinery Studio, Office Integration, PowerBI / Power Query), (2) exposes read-only tenant/blueprint/drift observability endpoints, and (3) — since Phase 4 — **owns the `System.UI` CK model and its service-managed blueprints** (the cockpit dashboards + the cross-cutting `System.TenantMode` seed), applying them on every tenant lifecycle event. All three concerns were previously hosted by `octo-frontend-admin-panel`; this repo is the consolidation point so the legacy Admin Panel can be retired.

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
├── ConstructionKit/                               # System.UI-2.8.0 model YAML: UIElement, Dashboard, ProcessDiagram, SymbolLibrary/SymbolDefinition, Branding, TreeNavigationConfiguration (Roles + Perspectives), MappingCoverageConfiguration (per-tenant source-catalogue types for the data-mappings Orphan Sources tab, singleton rtWellKnownName 'MappingCoverage'), EntityForm (+ records EntityFormSection/Field/Column, AB#5521)
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

### `System.UI.TenantCockpit` blueprint (1.1.1, AB#5558)

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
- **1.1.1** gives "Needs attention" rows 1–2 (rowSpan 2: one 200 px row cut off the finding
  cards' action links), KPIs row 3, pie rows 4–5 (fixture in octo-meshboard updated alongside),
  and replaces the technical board description with a user-facing one ("Status of this
  tenant at a glance"); `System.UI.SystemCockpit` **1.0.1** likewise ("Status of the OctoMesh
  installation at a glance"). The Studio's Home shows neither name nor description (greeting +
  board tabs, the board embedded with `headerMode: 'compact'`); UI › MeshBoards and the board
  manager still do, so descriptions are user-facing text — never implementation notes.
- **Roll-forward** as for TenantMode: the minor bump re-imports the seed on the next cold start;
  the Upsert rewrites the seeded widgets including their position, widgets the tenant added stay.
  A tenant widget placed where a new seeded widget lands is not moved by the import;
  octo-meshboard resolves the overlap when the board loads (`resolveOverlaps` pushes a widget
  down, display only until the board is saved via Customise board).
- The Studio keeps its hard-wired Home strips only as a fallback for boards without any cockpit
  widget (tenants before the roll-out, or a tenant that removed them).

### `System.UI.EntityForms` blueprint (1.4.0, AB#5521 / AB#5523 / AB#5524 / AB#5547)

`System.UI/EntityForm` (System.UI **2.7.0**, Minor, no migration; 2.8.0 adds
`EntityFormField.ReferenceDisplayAttributes`, Minor, no migration) describes how the Refinery Studio lists,
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
    copies against a snapshot of this seed (`entity-forms-seed-1.4.0.snapshot.ts`), which must be
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
    them against the snapshot (`entity-forms-seed-1.4.0.snapshot.ts`).
- **1.4.0 (AB#5547)** sets `ReferenceDisplayAttributes: "repositoryUrl,channel"` on the `HelmRepository`
  reference field of `form-adapter` and `form-application`, so the delivered forms' Helm repository picker
  shows `name · URL · channel` like the Studio fallbacks. Needs **System.UI 2.8.0**
  (`EntityFormField.ReferenceDisplayAttributes`, a comma-separated String — record values in blueprint seeds must be scalars, the seed's record converter cannot read a YAML sequence; the same applies to `RecordColumns` should a seed ever set it): the dependency floor in
  `blueprint.yaml` and the seed `dependencies` are 2.8.0. Entries are target attribute names in camelCase
  (the picker passes them verbatim as GraphQL `attributeNames`); list only non-secret attributes.
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

## CI / CD

Root-level `azure-pipelines.yml` follows the Phase 4a Layer-2 pattern: pulls shared templates from `octo-pipeline-templates@tpl-v0.4.11`, builds + tests + pushes Docker image (private always, public on release tag), tags `:main-latest` on main. No Helm chart (the chart lives in `octo-helm-core`).

Since Phase 4 / AB#4261 the build also **publishes the `System.UI` CK library + catalog** (moved from the retired `octo-frontend-admin-panel`):
- the `Build` step passes `/p:OctoPublishCatalog="$(effectivePublishCatalog)"` so the CK catalog is published to the schema registry on release builds (same GitHub-PAT credentials already wired for the build);
- `handle-artifacts.yml` is given `constructionKitLibraryPaths: src/SystemUiCkModel/bin/$(buildConfiguration)/$(artifactsFrameworkVersion)/octo-ck-libraries/SystemUiCkModel`, so the `local` build artifact carries the generated `SystemUiCkModel` library docs.

This is what lets `octo-documentation`'s `collect-docs.yml` harvester pick up the `System.UI` library docs from `octo-platform-services-CI` — it replaces the removed `octo-frontend-admin-panel-CI` (`frontendAdmin`) producer.
