# Octo Platform Services

A consolidation service that hosts cross-cutting platform concerns for OctoMesh: the public `_configuration` discovery endpoint that external clients (Refinery Studio, Office Integration, PowerBI / Power Query) use to find every other OctoMesh service, read-only tenant / blueprint / service-drift observability endpoints, and (since Phase 4) ownership of the `System.UI` Construction Kit model and its service-managed blueprints. Picking up these concerns from the now-retired `octo-frontend-admin-panel`.

## Overview

`Meshmakers.Octo.Backend.PlatformServices` is an ASP.NET Core service that runs alongside the rest of the OctoMesh backend. It:

- Serves a tenant-scoped `_configuration` endpoint (`GET /{tenantId}/_configuration`) returning the 10-field `TenantConfigurationDto` (asset / bot / communication / reporting / mesh-adapter / AI service URLs, issuer, system tenant id, CrateDB admin URL, Grafana URL).
- Exposes read-only `/system/v1/...` controllers for tenant, blueprint, and service-drift observability (admin-only).
- Owns the `System.UI` CK model and applies the cockpit dashboards + the cross-cutting `System.TenantMode` seed as service-managed blueprints on every tenant lifecycle event (consumes `PosCreateTenant` / `PosUpdateTenant` from the distribution event hub).

The service deliberately seeds **no identity data** — the dead `octo-admin-panel` OIDC client was removed alongside the legacy admin panel. See `docs/concepts/phase-4-system-ui-ownership.md` for the consolidation history.

## Published packages

The repository produces one NuGet package (the service host project itself is `IsPackable=false` and ships as a container image):

- **Meshmakers.Octo.ConstructionKit.Models.System.Ui** — the `System.UI` construction kit model owned by this service.

## Project structure

| Project | Description |
| --- | --- |
| `src/PlatformServices` | ASP.NET Core service host (not packable). |
| `src/SystemUiCkModel` | The `System.UI` construction kit model (packable). |
| `tests/PlatformServices.ContractTests` | Snapshot tests for the `TenantConfigurationDto` shape — CI fails on any field-set / property-name / trailing-slash drift. |

## Build

```bash
dotnet build Octo.PlatformServices.sln
```

For local development with monorepo-built dependencies, use the `DebugL` configuration (consumes packages from `../nuget`):

```bash
dotnet build Octo.PlatformServices.sln -c DebugL
```

## Test

```bash
dotnet test Octo.PlatformServices.sln
```

## Run locally

```bash
dotnet run --project src/PlatformServices/PlatformServices.csproj -c DebugL --urls=http://localhost:5024
curl http://localhost:5024/octosystem/_configuration
```

Configuration is environment-variable driven with the `OCTO_` prefix; the `PlatformServiceUrlsOptions` block under `OCTO_PLATFORMSERVICES__*` carries every downstream service URL the discovery endpoint hands out.

## Documentation

The complete OctoMesh documentation is available at https://docs.meshmakers.cloud.

## License

Released under the MIT License.
