# Phase 4 — platform-services becomes the System.UI owner (blueprint-based)

**Status:** Implemented (local main, not yet committed/pushed), 2026-06-29 — builds green, platform-services contract tests pass (10-field DTO), admin-panel + octo-sdk build green.
**Tracking:** AB#4261.

> **Implementation note.** The config-creator was built on `DefaultConfigurationCreatorServiceBase`
> (NOT `…Standardized`) — the Standardized base unconditionally sends an identity-data command on
> every tenant setup and requires a non-null command client; the Base carries the blueprint apply +
> `RefreshTenantStateAsync` lifecycle hook with no identity path, which is exactly the blueprint-only
> shape we want (see §4). `Program.cs` wires `AddOctoServiceInfrastructure("PlatformServices")` (no
> command-client configurator) for the `PosCreateTenant` / `PosUpdateTenant` host. The vestigial
> OAuth fields were dropped from `TenantConfigurationDto` and the `octo-admin-panel` constants removed
> from `octo-sdk` (admin-panel's sunset endpoint inlines the literal locally).
**Decision (2026-06-29, Gerald):** `octo-platform-services` becomes the owner of the
`System.UI` CK model and its service-managed blueprints — explicitly **not**
asset-repo-services. The strategic direction is to manage CK models more centrally
through platform-services going forward; concentrating service-managed CK + blueprint
ownership here is the first step. Once this lands, `octo-frontend-admin-panel` has no
runtime responsibility left and can be retired (its `_configuration` endpoint already
runs here since Phase 1).

## 1. Guiding principle: this is a blueprint move, not a rewrite

The cockpit dashboards and the tenant-mode seed are **already** service-managed
blueprints. They live in the self-contained `SystemUiCkModel` CK-model project, which
via the `BlueprintEmbed` MSBuild task + `BlueprintSourceGenerator` produces:

- the `System.UI` CK model (`AddCkModelSystemUIV2()`), and
- one `IBlueprintEmbeddedSource` + `AddBlueprint…V1()` DI extension per blueprint:
  - `System.UI.SystemCockpit-1.0.0` — `requires.octo.isSystemTenant: "true"`
  - `System.UI.TenantCockpit-1.0.0` — `requires.octo.isSystemTenant: "false"`
  - `System.TenantMode-1.0.0` — always applies, seed uses `${octo.environmentMode}`

We do **not** hand-copy YAML or re-author seeds. We move the project and re-wire the
generated DI extensions in the new host. "Which blueprint applies where" stays entirely
in the manifests' `requires:` blocks; the host holds no per-blueprint logic. Version
bumps remain manifest-only (`blueprintId`), folder names unchanged.

## 2. What platform-services already has (no new heavy deps)

The Phase-1 note in `CLAUDE.md` that platform-services "deliberately does NOT depend on
the CK runtime / MongoDB" is **already superseded** by the Phase-2 observability work.
`Program.cs` today calls:

```csharp
builder.Services.AddRuntimeEngine()
    .AddMongoDbRuntimeRepository()
    .AddMongoBlueprintSupport();   // IBlueprintService is already available
```

and the csproj already references `Runtime.Engine.MongoDb`, `Services.Infrastructure`
and `Communication.Contracts`. So the runtime/Mongo/blueprint substrate the move needs
is **in place**; what is missing is the *hosted config-creator lifecycle* and the
CK-model + blueprint sources.

## 3. What is missing in platform-services

1. The `SystemUiCkModel` project itself (CK model + 3 blueprint folders).
2. The hosted bootstrap lifecycle — `AddOctoServiceInfrastructure(...)` plus a
   registered `IDefaultConfigurationCreatorService`. platform-services currently wires
   `AddRuntimeEngine` directly but does **not** start the infrastructure host that
   drives `EnableTenant` / `StartTenant` / `ImportCkModelAsync`.
3. The blueprint-variable options binding (`OctoBlueprintVariablesOptions` from
   `OCTO_BLUEPRINTS__*`) so `${octo.environmentMode}` / `${octo.isSystemTenant}` resolve
   from the cluster's helm-injected values — this is what makes the TenantMode refresh
   correct per cluster.

No identity-data write path is needed (no `CreateIdentityDataCommandRequest` command
client) — see §4: the only thing the admin-panel config-creator wrote to Identity was
the now-dead `octo-admin-panel` OAuth client.

## 4. The `octo-admin-panel` OAuth client is dead — drop the client seeding

`AdminPanel/Services/DefaultConfigurationCreatorService.CreateClients` seeds the
`CommonConstants.OctoAdminPanelClientId` (`"octo-admin-panel"`) OIDC client into Identity.
That client was **the login client of the admin-panel UI, which no longer exists**. It is
not carried over. Verified 2026-06-29:

- **`_configuration` only echoes the constant.** `TenantConfigurationController` returns
  `clientId = OctoAdminPanelClientId` as a hardcoded string; it never looks the client up
  in Identity. The endpoint works whether or not the client exists.
- **No live consumer uses the DTO's `clientId`.** Refinery Studio authenticates as
  `octo-data-refinery-studio` and Office Integration as `octo-office-integration`, each
  read from the consumer's own bundled `config.json`. Both **ignore** the `clientId`
  field in the `_configuration` response (they only consume `issuer` and the service URLs
  from it). PowerBI / Power Query bring their own client registration likewise.

Consequences:

- The platform-services config-creator does **not** override `CreateClients` and needs
  **no** `CreateIdentityDataCommandRequest` command client / DistributionEventHub wiring.
  Its only job is applying the System.UI / TenantMode blueprints.
- The `octo-admin-panel` (+ `…-debug`) client rows already present in existing tenants are
  harmless unused OIDC clients. Removing them is an **optional** cleanup, not a blocker.
- The `clientId` / `redirectUri` / `postLogoutRedirectUri` / `scope` fields of the
  `TenantConfigurationDto` are now **vestigial** (returned, ignored by all consumers).
  Dropping them is a contract change behind the Phase-1 snapshot test and is therefore a
  **separate** follow-up, not part of this move — the endpoint keeps returning the
  constant for now.

## 5. Migration steps

Each step is independently buildable; order matters only where noted.

1. **Move the CK-model project.** Copy `src/SystemUiCkModel/` into the
   `octo-platform-services` repo (e.g. `src/SystemUiCkModel/`), add it to
   `Octo.PlatformServices.sln`, and `ProjectReference` it from `PlatformServices.csproj`.
   Keep `OctoPublishCkModel=true` — platform-services CI must now publish the `System.UI`
   CK-model catalog artifact that admin-panel CI produced.
2. **Add the hosted bootstrap.** In `Program.cs`:
   - `builder.Services.AddOctoServiceInfrastructure("PlatformServices", _ => { });` — no
     identity-data command client needed (§4).
   - bind `OctoBlueprintVariablesOptions` from `OctoBlueprintVariablesOptions.SectionName`.
   - register `IDefaultConfigurationCreatorService` → the new platform-services creator.
   - call `AddCkModelSystemUIV2()` + the three `AddBlueprintSystem…V1()` extensions.
3. **Port the config-creator (blueprint-only).** Add a `DefaultConfigurationCreatorService :
   DefaultConfigurationCreatorServiceStandardized` to platform-services that reproduces
   the admin-panel one **minus** the identity bits:
   - `ServiceManagedBlueprintPrefix => "System.UI."` and the `IsServiceManagedBlueprint`
     override that allowlists `System.TenantMode` (cross-cutting, lives outside the
     `System.UI.` namespace by design).
   - `ImportCkModelAsync` → `ApplyServiceManagedBlueprintsAsync(tenantId, throwOnFailure: true)`.
   - tenant-online refresh of `System.TenantMode` with `force: true`. **Cleanup
     opportunity:** the base now exposes a generalized `RefreshTenantStateAsync(tenantId)`
     hook (called on the non-deferred `StartTenant` branch). Prefer overriding that hook
     instead of re-implementing admin-panel's private `RefreshTenantModeAsync` +
     `DeferTenantStart` branch by hand — same semantics, less duplicated control flow.
   - **No `CreateClients` override** — the `octo-admin-panel` client is dead (§4). The
     base's default no-op identity hooks are left untouched.
4. **Decommission admin-panel's runtime.** Once the move is verified on a tenant
   (cockpit dashboards + `TenantModeConfiguration` seeded correctly, `_configuration`
   still returns the identical 14-field DTO), strip the blueprint + CK-model + config
   creator wiring from admin-panel, leaving only the sunset `_configuration` endpoint
   until its RFC 8594 date (2027-03-31), or retire the host entirely if all consumers
   are already cut over.
5. **Deployment / CI** (tracked in AB#4261): helm `_env.tpl` fallback flip,
   `services.adminPanel.deploy=false`, remove `OCTO_*__PUBLICADMINPANELURL` injections,
   MCP `values.yaml` reference, `octomesh` orchestrator stage, `octo-mesh-deployment`
   release-trains + per-cluster values + **Release-Trains wiki**, examples, repo archive.

## 6. Verification

- Build: `dotnet build Octo.PlatformServices.sln -c DebugL` (CK model + blueprints embed,
  source generators emit the `AddBlueprint…V1` extensions).
- Contract test stays green — the `_configuration` DTO is unchanged by this work.
- Functional: enable a fresh system tenant → `System.UI.SystemCockpit` seeds the cockpit
  dashboard; enable a child tenant → `System.UI.TenantCockpit` seeds the minimal tile,
  `System.TenantMode` lands `EnvironmentMode = ${octo.environmentMode}` of the running
  cluster.
- Attach/restore: attach a tenant carrying a foreign `System.TenantMode-1.0.0` install
  row → the `force:true` refresh rewrites EnvironmentMode to this cluster's value (and,
  as a known caveat, resets `MaintenanceLevel` to `Off`).

## 7. Risks / caveats

- **TenantMode `force:true` resets MaintenanceLevel** to `Off` on every tenant-online
  refresh — carried over verbatim from admin-panel; documented, accepted.
- **CK-model catalog publishing moves CI lanes.** admin-panel CI published the
  `System.UI` CK library; platform-services CI (currently "no NuGet output") must take
  over that artifact, or the catalog loses a `System.UI` version on the next bump.
- **Blueprint-variable wiring.** Forgetting the `OctoBlueprintVariablesOptions` binding
  makes `${octo.*}` fall back to defaults — cockpit `requires:` gates and TenantMode
  EnvironmentMode would silently misbehave. It is a one-liner but a load-bearing one.

## 8. Out of scope / follow-ups

- Generic central CK-model management (managing arbitrary CK models through
  platform-services, beyond `System.UI`). This plan only moves `System.UI` and
  establishes the CK-hosting capability; the general capability is a separate concept.
- **Drop the vestigial client fields from `TenantConfigurationDto`** (`clientId`,
  `redirectUri`, `postLogoutRedirectUri`, `scope`) — no consumer reads them (§4). This is
  a contract change behind the Phase-1 snapshot test; coordinate + do separately.
- **Remove the dead `octo-admin-panel` / `octo-admin-panel-debug` OIDC client rows** from
  existing tenants — optional housekeeping, harmless if left.
