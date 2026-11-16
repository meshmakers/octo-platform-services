---
description: Why platform-services has no tenant authorization gate, the known system/v1 gap, and why JWT audience validation stays on.
applies_to: src/PlatformServices/Program.cs, src/PlatformServices/Controllers/**
---

# Tenant authorization and audience validation

## Tenant authorization: no gate here, on purpose

This service **does not** call `UseOctoTenantAuthorization()` / `AddOctoTenantAuthorization(...)`, and
that is a decision, not an oversight. The shared gate (`TenantAuthorizationMiddleware` in
octo-common-services, see `octo-common-services/docs/tenant-authorization.md`) compares the `{tenantId}` **route value** against the
caller's `tenant_id` claim. Neither route shape here wants that:

| Route | Why the gate does not apply |
|---|---|
| `GET {tenantId}/_configuration` | `[AllowAnonymous]` public discovery — Studio / Office / PowerBI read it *before* they have a token. The middleware skips anonymous endpoints, so wiring it would cover nothing. |
| `GET system/v1/tenants/{tenantId}/blueprints`, `…/ck-models` | Cross-tenant **operator** routes. The tenant id is the *subject being inspected*, not the tenant being addressed; the caller is meant to be a system-tenant operator enumerating child tenants; `PlatformServicesAdminPolicy` itself checks only an authenticated user with the `octo_api` scope, not the caller's tenant. `GetTenantId()` reads the route value regardless of the `system/` prefix, and the middleware's **user-token path is unconditional** (only the *service*-token path is staged behind `LogOnly`) — so wiring the gate would 403 exactly the use case these endpoints exist for. |

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

## Audience validation

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
