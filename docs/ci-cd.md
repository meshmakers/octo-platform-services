---
description: Helm deployment of the service, the CI pipeline and the explicit release decision for publishing the System.UI CK model.
applies_to: azure-pipelines.yml, src/PlatformServices/Dockerfile
---

# Deployment and CI / CD

## Helm Deployment

Chart values block: `services.platformServices` in `octo-helm-core/src/octo-mesh/values.yaml`. Env-var section in `templates/_env.tpl` under `else if eq .name "platformServices"`. Since Phase 4 the service is **no longer plain** — it needs broker (RabbitMQ) + MongoDB connection config and applies service-managed blueprints on tenant events, the same footprint as `communication-controller`. The deployment env must now carry the `OCTO_PLATFORMSERVICES__BROKER*` + `OCTO_SYSTEM__DATABASE*` + `OCTO_BLUEPRINTS__*` settings (helm wiring is part of AB#4261); rolling-update concurrency of the blueprint apply should be reviewed against the other blueprint-owning services before the chart change ships.

Public URI per environment:
- test-2: `platform.test.octo-mesh.com`
- staging-1: `platform.staging.octo-mesh.com`
- prod-1/2: `platform.octo-mesh.com`

## CI / CD

Root-level `azure-pipelines.yml` follows the Phase 4a Layer-2 pattern: pulls shared templates from `octo-pipeline-templates@tpl-v1.11.0` (includes the .NET runtime check of the final image stage, minimum 10.0.13, AB#6724; the Octo package-set checks, AB#7857; and the CK intermediate-version sync, AB#7974), builds + tests + pushes Docker image (private always, public on release tag), tags `:main-latest` on main. No Helm chart (the chart lives in `octo-helm-core`).

Since Phase 4 / AB#4261 the pipeline also **publishes the `System.UI` CK library + catalog** (moved from the retired `octo-frontend-admin-panel`):
- the shared `validate-and-publish-ck-versions` step publishes it after the tests — `main` to the private catalog, `r*` tags to the private **and** the public one, never replacing a published version (AB#5121). The `Build` step passes `/p:OctoPublishCkModel=false`: it compiles the model and publishes it nowhere;
- `handle-artifacts.yml` is given `constructionKitLibraryPaths: src/SystemUiCkModel/bin/$(buildConfiguration)/$(artifactsFrameworkVersion)/octo-ck-libraries/SystemUiCkModel`, so the `local` build artifact carries the generated `SystemUiCkModel` library docs.

This is what lets `octo-documentation`'s `collect-docs.yml` harvester pick up the `System.UI` library docs from `octo-platform-services-CI` — it replaces the removed `octo-frontend-admin-panel-CI` (`frontendAdmin`) producer.

## Publishing the system CK model: automatic from main (AB#7433, default true since AB#8718)

A merge to `main` publishes `System.UI` to the private CK catalog automatically (AB#8718, decision 2026-11-10). The pipeline
parameter `publishSystemCkModels` (boolean, **default true**; gate introduced as default false by AB#7343/AB#7432/AB#7433) feeds
`publishOnMain` of `validate-and-publish-ck-versions.yml` (`tpl-v1.8.0` and later; pinned: `tpl-v1.11.0`). ValidateVersion, the schema compile and the cascade check
run on every build and refuse a breaking change declared as minor; an already-published version is a no-op. Default true because CK v2
range retention (AB#4696) and the completed re-pin (AB#5688, AB#6708) removed the exact-pin breakage that motivated default false.

**To build main without publishing:** Azure DevOps -> pipeline `octo-platform-services-CI` -> *Run pipeline* -> branch `main` -> untick
*Publish System CK models to the CK catalogs (main)*; CLI: `az pipelines run --id 188 --branch main --parameters publishSystemCkModels=false`.
`r*` tags (release train) publish as before; `dev/*`/`test/*` builds never publish to the shared catalogs.
