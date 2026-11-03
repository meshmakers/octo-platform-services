---
description: Project layout, local-dev port table and package dependencies of platform-services.
applies_to: src/PlatformServices/Program.cs, src/PlatformServices/PlatformServices.csproj, src/PlatformServices/Properties/launchSettings.json
---

# Architecture

## Project Layout

```
src/PlatformServices/
├── Controllers/TenantConfigurationController.cs   # GET {tenantId}/_configuration
├── Controllers/{Tenants,Blueprints,Services}Controller.cs  # /system/v1 observability (admin-only)
├── Dto/TenantConfigurationDto.cs                  # 11-field response (OAuth client fields dropped in Phase 4; mcpServices added later)
├── Options/PlatformServiceUrlsOptions.cs          # bound from OCTO_PLATFORMSERVICES__* (URLs + broker)
├── Configuration/ConfigureDistributionEventHubOptions.cs  # broker wiring for the tenant-event host
├── Services/DefaultConfigurationCreatorService.cs # blueprint-only tenant bootstrap (System.UI.* + TenantMode)
├── Program.cs                                     # Observability + CORS + RuntimeEngine + ServiceInfrastructure + blueprints
├── Dockerfile                                     # mcr.microsoft.com/dotnet/aspnet:10.0-noble
├── appsettings.json
├── nlog.config
└── Properties/launchSettings.json                 # 5024 http / 5025 https
src/SystemUiCkModel/                               # System.UI CK model + 4 service-managed blueprints (moved from admin-panel)
├── ConstructionKit/                               # System.UI-2.12.0 model YAML (ckLanguage 2 since 2.12.0), System floor [2.5,3.0) since 2.7.0 (wave-1 System 2.5.0 repin): UIElement, Dashboard, ProcessDiagram, SymbolLibrary/SymbolDefinition, Branding, TreeNavigationConfiguration (Roles + Perspectives), MappingCoverageConfiguration (per-tenant source-catalogue types for the data-mappings Orphan Sources tab, singleton rtWellKnownName 'MappingCoverage'), EntityForm (+ records EntityFormSection/Field/Column), Folder + association roles FolderItem/FolderParent (Studio library folders, 2.10.0)
└── Blueprints/{System.UI.SystemCockpit,System.UI.TenantCockpit,System.UI.EntityForms,System.TenantMode}/
```

## Local-dev ports

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

## Dependencies

- `Meshmakers.Octo.Services.Observability` — `/healthz/live`, `/healthz/ready`, Prometheus scrape, OpenTelemetry. The 15-second startup grace before `/healthz/ready` flips to 200 is shared with every Octo service (`StartupBackgroundService`).
- `Meshmakers.Octo.Communication.Contracts` — `CommonConstants.OctoApiFullAccess` (admin policy), `CommonConstants.GetScopes`, and transitively `Meshmakers.Common.Shared` (for `EnsureEndsWith`).
- `Meshmakers.Octo.Runtime.Engine.MongoDb` — CK runtime + Mongo blueprint support (`IBlueprintService`, `ISystemContext`, `ITenantBlueprintInstallations`) for both the observability endpoints and the System.UI blueprint apply.
- `Meshmakers.Octo.Services.Infrastructure` — `AddOctoServiceInfrastructure` (distribution event hub tenant-event host + `IDefaultConfigurationCreatorService` lifecycle) and `InfrastructureCommon.ClaimScope`.
