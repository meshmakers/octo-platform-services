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

The endpoint returns the 10-field `TenantConfigurationDto`:

```
assetServices, botServices, communicationServices, reportingServices,
issuer, systemTenantId, crateDbAdminUrl, grafanaUrl, meshAdapterUrl, aiServices
```

All URLs are trailing-slashed via `EnsureEndsWith("/")`. Phase 4 dropped the four OAuth client fields the legacy admin-panel `ClientDto` carried (`clientId`, `redirectUri`, `postLogoutRedirectUri`, `scope`) — they only described the retired admin-panel UI's own OIDC client, which no live consumer reads (Refinery Studio, Office, PowerBI, Power Query each bring their own client registration from their bundled `config.json` and consume only the issuer + service URLs here).

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
├── Dto/TenantConfigurationDto.cs                  # 10-field response (OAuth client fields dropped in Phase 4)
├── Options/PlatformServiceUrlsOptions.cs          # bound from OCTO_PLATFORMSERVICES__* (URLs + broker)
├── Configuration/ConfigureDistributionEventHubOptions.cs  # broker wiring for the tenant-event host
├── Services/DefaultConfigurationCreatorService.cs # blueprint-only tenant bootstrap (System.UI + TenantMode)
├── Program.cs                                     # Observability + CORS + RuntimeEngine + ServiceInfrastructure + blueprints
├── Dockerfile                                     # mcr.microsoft.com/dotnet/aspnet:10.0-noble
├── appsettings.json
├── nlog.config
└── Properties/launchSettings.json                 # 5024 http / 5025 https
src/SystemUiCkModel/                               # System.UI CK model + 3 service-managed blueprints (moved from admin-panel)
├── ConstructionKit/                               # System.UI-2.1.0 model YAML
└── Blueprints/{System.UI.SystemCockpit,System.UI.TenantCockpit,System.TenantMode}/
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

### CORS

Anonymous endpoint hit from browser SPAs and Excel-hosted add-ins. No credentials in flight, so the global default policy is `AllowAnyOrigin / AllowAnyHeader / AllowAnyMethod`. Even though the service now references `Meshmakers.Octo.Services.Infrastructure` (for the tenant-event host), it deliberately does **not** activate that package's per-tenant `CorsPolicyProvider` — it keeps its own `AddCors` default policy, because the provider rebuilds policies per-tenant from `IdentityClient` origins and would break Office / PowerBI access ([[cors_policy_provider_overrides_named_policy]] in memory).

### Helm Deployment

Chart values block: `services.platformServices` in `octo-helm-core/src/octo-mesh/values.yaml`. Env-var section in `templates/_env.tpl` under `else if eq .name "platformServices"`. Since Phase 4 the service is **no longer plain** — it needs broker (RabbitMQ) + MongoDB connection config and applies service-managed blueprints on tenant events, the same footprint as `communication-controller`. The deployment env must now carry the `OCTO_PLATFORMSERVICES__BROKER*` + `OCTO_SYSTEM__DATABASE*` + `OCTO_BLUEPRINTS__*` settings (helm wiring is part of AB#4261); rolling-update concurrency of the blueprint apply should be reviewed against the other blueprint-owning services before the chart change ships.

Public URI per environment:
- test-2: `platform.test.octo-mesh.com`
- staging-1: `platform.staging.octo-mesh.com`
- prod-1/2: `platform.octo-mesh.com`

### Tests

`tests/PlatformServices.ContractTests/` snapshot-locks the rendered `TenantConfigurationDto` JSON (10-field set + JSON property names + trailing slashes) plus controller tests for the observability endpoints. Any DTO drift fails CI loudly — that is intentional. The baseline reflects the Phase 4 ten-field contract (the four OAuth client fields were removed).

## CI / CD

Root-level `azure-pipelines.yml` follows the Phase 4a Layer-2 pattern: pulls shared templates from `octo-pipeline-templates@tpl-v0.3.0`, builds + tests + pushes Docker image (private always, public on release tag), tags `:main-latest` on main. No NuGet output, no Helm chart (the chart lives in `octo-helm-core`).
