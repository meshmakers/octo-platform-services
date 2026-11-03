---
description: Swagger UI / OpenAPI wiring and the CORS policy of the anonymous discovery endpoint.
applies_to: src/PlatformServices/Program.cs, src/PlatformServices/Configuration/ConfigureOctoOpenApiOptions.cs, src/PlatformServices/wwwroot/**
---

# HTTP surface: Swagger and CORS

## Swagger UI / OpenAPI

The service exposes Swagger UI at `/swagger` via the shared `Meshmakers.Octo.Services.Swagger`
package (same pattern as octo-mcp-service): `AddOctoApiVersioningAndDocumentation(...)` +
`.AddVersion()` in DI, `app.UseOctoApiVersioningAndDocumentation()` in the pipeline,
`ConfigureOctoOpenApiOptions` feeds the OAuth authority from `PlatformServiceUrlsOptions.AuthorityUrl`.
The UI's authorization-code+PKCE flow uses the `octo-platformServices-swagger` client seeded by the
`System.Identity.Bootstrap` blueprint (≥1.2.0, `${octo.platform.publicUrl}` — "platform" slug in
`IdentityBlueprintVariableProvider`). `wwwroot/css/swagger.css` carries the shared styling
(served via `UseStaticFiles`).

## CORS

Anonymous endpoint hit from browser SPAs and Excel-hosted add-ins. No credentials in flight, so the global default policy is `AllowAnyOrigin / AllowAnyHeader / AllowAnyMethod`. Even though the service now references `Meshmakers.Octo.Services.Infrastructure` (for the tenant-event host), it deliberately does **not** activate that package's per-tenant `CorsPolicyProvider` — it keeps its own `AddCors` default policy, because the provider rebuilds policies per-tenant from `IdentityClient` origins and would break Office / PowerBI access.
