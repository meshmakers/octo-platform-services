---
description: What the contract snapshot tests and the Testcontainers integration tests cover, and how to run them.
applies_to: tests/**
---

# Tests

## Tests

`tests/PlatformServices.ContractTests/` snapshot-locks the rendered `TenantConfigurationDto` JSON (11-field set + JSON property names + trailing slashes) plus controller tests for the observability endpoints. Any DTO drift fails CI loudly — that is intentional. The baseline reflects the Phase 4 ten-field contract (the four OAuth client fields were removed) plus the additive `mcpServices` field.

`tests/PlatformServices.IntegrationTests/` (xUnit v3 + Testcontainers MongoDB, needs Docker) wires the same DI as `Program.cs` against a real replica set and runs the real `DefaultConfigurationCreatorService.SetupAsync` on fresh child tenants: System.Files install + default root. `dotnet test Octo.PlatformServices.sln -c DebugL`.
