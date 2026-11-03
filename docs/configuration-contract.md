---
description: The public _configuration discovery endpoint: the 11-field TenantConfigurationDto, empty-string semantics and the contract snapshot test.
applies_to: src/PlatformServices/Controllers/TenantConfigurationController.cs, src/PlatformServices/Dto/TenantConfigurationDto.cs, src/PlatformServices/Options/**, tests/PlatformServices.ContractTests/TenantConfigurationDtoContractTests.cs
---

# The `_configuration` contract

The endpoint returns the 11-field `TenantConfigurationDto`:

```
assetServices, botServices, communicationServices, reportingServices,
issuer, systemTenantId, crateDbAdminUrl, grafanaUrl, meshAdapterUrl, aiServices,
mcpServices
```

Configured URLs are trailing-slashed via `EnsureEndsWith("/")`; an **empty string is served verbatim** and means "service not part of this installation" (AB#4884) — consumers key on it (e.g. the Refinery Studio Tenant Features panel shows "Not installed"). The optional services Reporting, AI and MCP default to empty: a URL only appears when the deployment announces it (`OCTO_PLATFORMSERVICES__REPORTINGSERVICEURL` etc. — Helm `externalUris.reporting` / `services.aiServices.publicUri`, locally Start-Octo's per-service announcements). A localhost default must never reach a browser. Phase 4 dropped the four OAuth client fields the legacy admin-panel `ClientDto` carried (`clientId`, `redirectUri`, `postLogoutRedirectUri`, `scope`) — they only described the retired admin-panel UI's own OIDC client, which no live consumer reads (Refinery Studio, Office, PowerBI, Power Query each bring their own client registration from their bundled `config.json` and consume only the issuer + service URLs here).

**Any DTO change forces every consumer to redeploy in lockstep.** The contract snapshot test (`tests/PlatformServices.ContractTests/`) fails CI loudly on field-set / property-name / trailing-slash drift — keep it as the source of truth and update consumers first if you intend a break.
