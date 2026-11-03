# AGENTS.md

Instructions for coding agents in this repository. Detail lives in `docs/`; read the matching file
before changing the code it covers.

## Repository Overview

**octo-platform-services** is an ASP.NET Core service that (1) serves the public, tenant-scoped `_configuration` discovery endpoint for OctoMesh's external clients (Refinery Studio, Office Integration, PowerBI / Power Query), (2) exposes read-only tenant/blueprint/drift observability endpoints, and (3) — since Phase 4 — **owns the `System.UI` CK model and its service-managed blueprints** (the cockpit dashboards + the cross-cutting `System.TenantMode` seed), applying them on every tenant lifecycle event, and installs the `System.Files` CK model + its default root (AB#6480). All three concerns were previously hosted by `octo-frontend-admin-panel`; this repo is the consolidation point so the legacy Admin Panel can be retired.

Phase history:
- **Phase 1 (done)**: extracted the `_configuration` discovery endpoint.
- **Phase 2 (done)**: tenant / blueprint / service-drift observability endpoints (`/system/v1/...`).
- **Phase 3 (done elsewhere)**: identity-data seeding moved to the `System.Identity.Bootstrap` blueprint in `octo-identity-services`.
- **Phase 4 (this repo, in progress — AB#4261)**: own the `System.UI` CK model + service-managed blueprints so `octo-frontend-admin-panel` can be retired. The strategic direction is to manage CK models more centrally through platform-services going forward.

## What changed in Phase 4

The Phase-1 "slim, single-endpoint" framing no longer holds. Owning the blueprints means platform-services now:
- depends on the CK runtime + MongoDB (`AddRuntimeEngine().AddMongoDbRuntimeRepository().AddMongoBlueprintSupport()` — already present for the Phase-2 observability endpoints), and
- runs the **distribution event hub** tenant-lifecycle host (`AddOctoServiceInfrastructure`) so it consumes `PosCreateTenant` / `PosUpdateTenant` events and seeds the blueprints on tenant create / attach / restore. This is the same runtime footprint the admin-panel backend had.

It deliberately seeds **no identity data**: the `octo-admin-panel` OIDC client the admin-panel used to seed is dead (only the removed admin-panel UI authenticated with it), so `DefaultConfigurationCreatorService` is built on `DefaultConfigurationCreatorServiceBase` (no identity-data command client) rather than `…Standardized`. See `docs/concepts/phase-4-system-ui-ownership.md`.

## Read before you change

<!-- >>> generated: routing -->
| When you change | Read first |
|---|---|
| `src/PlatformServices/Program.cs`, `src/PlatformServices/PlatformServices.csproj`, `src/PlatformServices/Properties/launchSettings.json` | `docs/architecture.md` |
| `azure-pipelines.yml`, `src/PlatformServices/Dockerfile` | `docs/ci-cd.md` |
| `src/PlatformServices/Controllers/TenantConfigurationController.cs`, `src/PlatformServices/Dto/TenantConfigurationDto.cs`, `src/PlatformServices/Options/**`, `tests/PlatformServices.ContractTests/TenantConfigurationDtoContractTests.cs` | `docs/configuration-contract.md` |
| `src/SystemUiCkModel/Blueprints/System.UI.EntityForms/**` | `docs/entity-forms.md` |
| `src/PlatformServices/Program.cs`, `src/PlatformServices/Configuration/ConfigureOctoOpenApiOptions.cs`, `src/PlatformServices/wwwroot/**` | `docs/http-surface.md` |
| `src/SystemUiCkModel/Blueprints/System.TenantMode/**`, `src/SystemUiCkModel/Blueprints/System.UI.TenantCockpit/**`, `src/SystemUiCkModel/Blueprints/System.UI.SystemCockpit/**`, `src/PlatformServices/Services/DefaultConfigurationCreatorService.cs` | `docs/service-managed-blueprints.md` |
| `src/PlatformServices/Services/**`, `tests/PlatformServices.IntegrationTests/**`, `tests/PlatformServices.ContractTests/SystemFilesOnlyTenantSetupRetryStoreTests.cs` | `docs/system-files.md` |
| `src/SystemUiCkModel/ConstructionKit/**`, `src/SystemUiCkModel/SystemUiCkModel.csproj` | `docs/system-ui-ck-model.md` |
| `src/PlatformServices/Program.cs`, `src/PlatformServices/Controllers/**` | `docs/tenant-authorization.md` |
| `tests/**` | `docs/testing.md` |
<!-- <<< end generated: routing -->

Background, not path-triggered: `docs/concepts/` holds the phase concepts (Phase 2 to 4) this service
grew out of.

## Build & test

```bash
# Local dev — always DebugL, uses NuGet packages from ../nuget/
dotnet build Octo.PlatformServices.sln -c DebugL

# Run locally (ports 5024 http / 5025 https)
dotnet run --project src/PlatformServices/PlatformServices.csproj -c DebugL --urls=http://localhost:5024

# Smoke test
curl http://localhost:5024/octosystem/_configuration
```

After modifying shared DTOs in `octo-sdk`, run `invoke-buildall -branch main -configuration DebugL -excludeFrontend $true -excludeAdditional $true` from `octo-tools` to distribute the updated NuGets before rebuilding this repo.

## Before you commit

1. `dotnet build Octo.PlatformServices.sln -c DebugL` succeeds with zero warnings
   (`TreatWarningsAsErrors=true` in `Directory.Build.props`).
2. `dotnet test Octo.PlatformServices.sln -c DebugL` reports 0 failed. The integration tests need Docker
   (Testcontainers MongoDB); see `docs/testing.md`.
3. Update the doc under `docs/` that covers the code you changed (the routing table above names it).
4. If you touched `AGENTS.md` or any file in `docs/`: `Test-OctoAgentDocs -Fix` reports 0 errors.
   It rewrites the generated routing table, so run it before you stage, not after.

## Rules

- **Any change to `TenantConfigurationDto` is a breaking change for every consumer.** Update the
  consumers first; the contract snapshot test is the source of truth (`docs/configuration-contract.md`).
- **Bump the blueprint version on every seed change** of a service-managed blueprint; the next cold start
  rolls it forward (`docs/service-managed-blueprints.md`, `docs/entity-forms.md`).
- **Every new System.UI CK attribute declares its `ownership`**; the build fails with `OCTO-CK001`
  otherwise (`docs/system-ui-ck-model.md`).
- **Do not wire the tenant authorization gate for the whole app, and do not disable audience
  validation** (`docs/tenant-authorization.md`).
- **Keep the service's own CORS default policy**; do not activate the per-tenant `CorsPolicyProvider`
  (`docs/http-surface.md`).
- **A merge to `main` does not publish `System.UI`** to the CK catalogs; publishing is an explicit
  release decision (`docs/ci-cd.md`).
