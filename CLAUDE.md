# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Overview

**octo-platform-services** is a slim ASP.NET Core service that serves the public, tenant-scoped `_configuration` discovery endpoint for OctoMesh's external clients (Refinery Studio, Office Integration, PowerBI / Power Query). The endpoint was previously hosted by `octo-frontend-admin-panel`; this repo is Phase 1 of the `octo-platform-services` initiative, which extracted that single concern so the legacy Admin Panel can be retired.

Later phases (not yet implemented):
- **Phase 2**: consolidate CK model installation across services into a declarative registry
- **Phase 3**: blueprint orchestration + initial-data seeding as a central service (today every service reinvents `DefaultConfigurationCreatorService`)
- **Phase 4**: retire `octo-frontend-admin-panel`

## Status: Phase 1 (Contract-Preserving Extraction)

The `_configuration` endpoint must return a byte-identical response to the legacy `OidcConfigurationController` in `octo-frontend-admin-panel`. The contract is the 14-field `TenantConfigurationDto`:

```
assetServices, botServices, communicationServices, reportingServices,
issuer, clientId, redirectUri, postLogoutRedirectUri, scope,
systemTenantId, crateDbAdminUrl, grafanaUrl, meshAdapterUrl, aiServices
```

All URLs are trailing-slashed via `EnsureEndsWith("/")`. `clientId` is `CommonConstants.OctoAdminPanelClientId` (or `…Debug` when a debugger is attached) — the new service reuses the legacy OAuth client identity so consumers do not need to be re-registered in Identity.

**No DTO change goes in Phase 1.** A field rename, new field, or different trailing-slash rule would force every consumer (Refinery Studio, Office, PowerBI, Power Query) to redeploy in lockstep.

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
├── Dto/TenantConfigurationDto.cs                  # 14-field response
├── Options/PlatformServiceUrlsOptions.cs          # bound from OCTO_PLATFORMSERVICES__*
├── Program.cs                                     # Observability + CORS + Controllers
├── Dockerfile                                     # mcr.microsoft.com/dotnet/aspnet:10.0-noble
├── appsettings.json
├── nlog.config
└── Properties/launchSettings.json                 # 5024 http / 5025 https
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
- `Meshmakers.Octo.Communication.Contracts` — `CommonConstants.OctoAdminPanelClientId`, `CommonConstants.GetScopes`, `ApiScopes.OctoApiFullAccess`, `DefaultScopes`, and transitively `Meshmakers.Common.Shared` (for `EnsureEndsWith`).

Deliberately NOT depended on: `Meshmakers.Octo.Services.Infrastructure` (no RabbitMQ / event-hub), `Meshmakers.Octo.Runtime.Engine.MongoDb` (no CK runtime), `Meshmakers.Octo.Services.Swagger` (single endpoint, no docs).

### CORS

Anonymous endpoint hit from browser SPAs and Excel-hosted add-ins. No credentials in flight, so the global default policy is `AllowAnyOrigin / AllowAnyHeader / AllowAnyMethod`. Do **not** pull in `Meshmakers.Octo.Services.Infrastructure` — its `CorsPolicyProvider` rebuilds policies per-tenant from `IdentityClient` origins and would break Office / PowerBI access ([[cors_policy_provider_overrides_named_policy]] in memory).

### Helm Deployment

Chart values block: `services.platformServices` in `octo-helm-core/src/octo-mesh/values.yaml`. Env-var section in `templates/_env.tpl` under `else if eq .name "platformServices"`. The service is plain — no broker, no MongoDB, no blueprints — so the deployment template's RollingUpdate default is fine; no `recreateStrategy` needed.

Public URI per environment:
- test-2: `platform.test.octo-mesh.com`
- staging-1: `platform.staging.octo-mesh.com`
- prod-1/2: `platform.octo-mesh.com`

### Tests

`tests/PlatformServices.ContractTests/` contains a single test class that snapshot-compares the rendered `TenantConfigurationDto` JSON against the legacy admin-panel `ClientDto` shape (field set + JSON property names + trailing slashes). Any DTO drift fails CI loudly — that is intentional, Phase 1 must not change the contract.

## CI / CD

Root-level `azure-pipelines.yml` follows the Phase 4a Layer-2 pattern: pulls shared templates from `octo-pipeline-templates@tpl-v0.3.0`, builds + tests + pushes Docker image (private always, public on release tag), tags `:main-latest` on main. No NuGet output, no Helm chart (the chart lives in `octo-helm-core`).
