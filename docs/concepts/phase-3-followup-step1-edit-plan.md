# AB#4209 Step 1 — concrete edit plan (Identity integration sites)

**Companion to:** `phase-3-followup-identity-local-dev-overlay.md` §5 row 1.
**Status:** Draft, 2026-06-19. Built from a complete read of every file. Numbers are concrete (not grep estimates).
**Prerequisite:** Step 0 (engine-side `WrapScalarInRecord` transform) merged + propagated via `invoke-buildall`.
**Branch:** Integrated into `dev/4208-mcp-clients-in-bootstrap-blueprint` (the AB#4208 PR #97 branch) per Gerald's 2026-06-19 decision. AB#4208 + AB#4209 land together — the MCP server can't start without a client definition AND the URI shape must match the new schema, so a split-branch approach blocks both work items. Seed-data conflict is benign: PR #97 ADDS clients (660…33 + 660…34) with URIs in the old format; this edit-plan REWRITES every URI entry across all 5 clients into the new `ClientUriEntry` shape in one pass.

## TL;DR

The original "~44 call sites across 6 files" was a grep over-count. Actual concrete work:

| File | Concrete edits | Notes |
|---|---|---|
| `src/IdentityServices/MapperProfile.cs` | **+1 new `CreateMap<RtClient, Client>`** | Today the Duende projection works via AutoMapper *convention* (`IList<string>` ↔ `ICollection<string>`). After the schema change the convention breaks (`IList<ClientUriEntry>` is not assignment-compatible with `ICollection<string>`). The fix is **one** explicit map with three `ForMember` projections, not 6 file edits. |
| `src/IdentityServices/TenantApi/v1/Controllers/ClientsController.cs` | 6 lines (2 in `CreateClientDto`, 4 in `ApplyToClient`) | DTO ↔ entity at the public API boundary. |
| `src/IdentityServices/Consumers/CreateIdentityDataCommandRequestConsumer.cs` | 3 lines | Distribution-event-hub identity push. |
| `src/IdentityServices/Services/IdentityCorsPolicyProvider.cs` | 1 line | `client.AllowedCorsOrigins` iteration → project `.Uri`. |
| `tests/Shared.TestUtilities/Builders/RtClientBuilder.cs` | 6 lines (3 init + 3 `With…` helpers) | Shared test builder. Once fixed, all 15 test sites compile clean. |
| `src/IdentityServerPersistence/SystemStores/ClientStore.cs` | **0 lines** | Uses `_mapper.Map<Client>(rtClient)` — the fix lives in the AutoMapper profile, not here. |
| `src/IdentityServerPersistence/Services/ClientMirrorProvisioningService.cs` | **0 lines** | Entity-to-entity copy: `RedirectUris = parentClient.RedirectUris`. Both sides are `IList<ClientUriEntry>` after the schema change. Compiles as-is. |

**Total: ~17 concrete lines + 1 new AutoMapper config block. 5 files touched.**

The Step 1 PR also lands:
- `ck-clientUriEntry.yaml` record (per §11 spike, the file is already designed)
- `Uri` + `Source` attribute declarations in `identity-attributes.yaml`
- `RedirectUris` / `PostLogoutRedirectUris` / `AllowedCorsOrigins` flipped from `StringArray` to `RecordArray` of `ClientUriEntry`
- `ckModel.yaml` `System.Identity-2.8.0 → 2.9.0`
- Seed-data 9 populated URI list entries lifted to the explicit `ckRecordId` + `attributes` form
- `ConstructionKit/migrations/2.8.0-to-2.9.0.yaml` calling the Step 0 `WrapScalarInRecord` action (3 steps, one per URI attribute)

## Source taxonomy — decided 2026-06-19

`ClientUriEntry.Source ∈ { "base", "api", "overlay:<name>" }`. Full table in the concept doc §4.5. The relevant rules for Step 1:

- Blueprint seed-data + `CreateIdentityDataCommandRequestConsumer` write `"base"`.
- `ClientsController.ApplyToClient` (REST API) writes `"api"`.
- `Apply-IdentityOverlay` (octo-tools cmdlet, Step 3) writes `"overlay:<name>"`.
- Cleanup gate (Step 2) deletes only `source == "base"` entries that aren't in the current seed. `"api"` and `"overlay:*"` survive every re-apply.

C# constants live next to the `ClientUriEntry` declaration:

```csharp
public static class ClientUriSources
{
    public const string Base = "base";
    public const string Api = "api";
    // Overlay sources are runtime-named ("overlay:local-dev" etc.); no constant.
}
```

## Per-file detail

### 1. `src/IdentityServices/MapperProfile.cs` — add explicit `RtClient → Client` map

Today (lines 7-50): the profile declares maps for `IdentityProvider` subtypes + `EmailDomainGroupRule`. There is **no** explicit `RtClient ↔ Duende.IdentityServer.Models.Client` map; the Duende projection in `ClientStore.FindClientByIdAsync` / `GetAllClientsAsync` (`_mapper.Map<Client>(rtClient)`) relies on AutoMapper's name-convention auto-mapping.

After Step 1, the auto-map breaks for three properties (`RedirectUris`, `PostLogoutRedirectUris`, `AllowedCorsOrigins`) because the source type changes from `IList<string>` to `IList<ClientUriEntry>` and the destination Duende `Client` stays `ICollection<string>`. Add:

```csharp
CreateMap<RtClient, Client>()
    .ForMember(dest => dest.RedirectUris,
        opt => opt.MapFrom(src => src.RedirectUris.Select(e => e.Uri).ToList()))
    .ForMember(dest => dest.PostLogoutRedirectUris,
        opt => opt.MapFrom(src => src.PostLogoutRedirectUris.Select(e => e.Uri).ToList()))
    .ForMember(dest => dest.AllowedCorsOrigins,
        opt => opt.MapFrom(src => src.AllowedCorsOrigins.Select(e => e.Uri).ToList()));
```

Verify after adding: every other `RtClient` field has a name-matched Duende `Client` field, so auto-map continues to handle them. The new explicit map is `IgnoreUnmapped`-friendly — AutoMapper auto-fills the rest.

### 2. `src/IdentityServices/TenantApi/v1/Controllers/ClientsController.cs`

**Read direction (`CreateClientDto`, lines 216-218):** today directly assigns the IList<string> to a List<string> property. Change to projection:

```csharp
// Before:
RedirectUris = applicationClient.RedirectUris,
PostLogoutRedirectUris = applicationClient.PostLogoutRedirectUris,
AllowedCorsOrigins = applicationClient.AllowedCorsOrigins,

// After:
RedirectUris = applicationClient.RedirectUris.Select(e => e.Uri).ToList(),
PostLogoutRedirectUris = applicationClient.PostLogoutRedirectUris.Select(e => e.Uri).ToList(),
AllowedCorsOrigins = applicationClient.AllowedCorsOrigins.Select(e => e.Uri).ToList(),
```

**Write direction (`ApplyToClient`, lines 261-277):** today wraps `clientDto.RedirectUris` into `AttributeStringValueList`. Change to wrap as record list with `Source = ClientUriSources.Api`. The exact wrapping type comes from the generated CK code — likely `AttributeRecordValueList<ClientUriEntry>` or similar; confirm at edit-time. Pseudo-code:

```csharp
applicationClient.RedirectUris = new AttributeRecordValueList<ClientUriEntry>(
    (clientDto.RedirectUris ?? Enumerable.Empty<string>())
        .Select(uri => new ClientUriEntry { Uri = uri, Source = ClientUriSources.Api })
        .ToList());
```

Three identical blocks for the three URI lists.

### 3. `src/IdentityServices/Consumers/CreateIdentityDataCommandRequestConsumer.cs`

**Lines 145-147:** same wrap as `ClientsController.ApplyToClient`. The DTO source here is `Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ClientDto` (or `DistClientDto` — confirm), which still carries plain `List<string>` URIs over the wire. We wrap on the receiving side:

```csharp
RedirectUris = new AttributeRecordValueList<ClientUriEntry>(
    distClientDto.RedirectUris.Select(u => new ClientUriEntry { Uri = u, Source = ClientUriSources.Base }).ToList()),
PostLogoutRedirectUris = new AttributeRecordValueList<ClientUriEntry>(
    distClientDto.PostLogoutRedirectUris.Select(u => new ClientUriEntry { Uri = u, Source = ClientUriSources.Base }).ToList()),
AllowedCorsOrigins = new AttributeRecordValueList<ClientUriEntry>(
    distClientDto.AllowedCorsOrigins.Select(u => new ClientUriEntry { Uri = u, Source = ClientUriSources.Base }).ToList()),
```

Note the source value: **`Base`** here, NOT `Api` like the REST controller. Reason: this consumer handles the distribution-event-hub `CreateIdentityDataCommandRequest`, which is the cross-service identity-bootstrap path that mirrors blueprint-managed clients into child tenants. Conceptually that's blueprint-seeded data flowing across services, so `Base` is correct (and these entries DO get rewritten on the next blueprint re-apply — which is fine because the next event-hub message will re-create them).

### 4. `src/IdentityServices/Services/IdentityCorsPolicyProvider.cs`

**Line 119:** `foreach (var origin in client.AllowedCorsOrigins) { origins.Add(origin); }` becomes `foreach (var entry in client.AllowedCorsOrigins) { origins.Add(entry.Uri); }`. One line.

### 5. `tests/Shared.TestUtilities/Builders/RtClientBuilder.cs`

**Init (lines 19-21):** swap the `AttributeStringValueList` initializers for `AttributeRecordValueList<ClientUriEntry>`.

**`WithRedirectUris(params string[])` / `WithPostLogoutRedirectUris(params string[])` / `WithAllowedCorsOrigins(params string[])` (lines 61-78):** each wraps the input strings as `ClientUriEntry` records with `Source = "base"`:

```csharp
public RtClientBuilder WithRedirectUris(params string[] uris)
{
    _client.RedirectUris = new AttributeRecordValueList<ClientUriEntry>(
        uris.Select(u => new ClientUriEntry { Uri = u, Source = ClientUriSources.Base }).ToList());
    return this;
}
```

Once these three helpers are fixed, every test site that calls `.WithRedirectUris(...)` etc. compiles without change (15 hits in `TestClients.cs`, `IntegrationTestBase.cs`, `ClientStoreTests.cs` etc.).

### 6. Test fixtures with direct assertions on URI lists (post-edit grep)

After landing the changes, grep `tests/` once more for any direct property access on `RtClient.RedirectUris.Add(...)` / `.Count` / `.Contains(...)`:

```bash
grep -rn "RedirectUris\.\|PostLogoutRedirectUris\.\|AllowedCorsOrigins\." \
    octo-identity-services/tests --include="*.cs" | grep -v "/bin/\|/obj/"
```

Hits expected ≈ 0 once the shared builder is fixed (the builder hides the storage shape). Any straggler gets a `.Uri` projection or a `Source = "base"` wrap.

## Suggested commit order

A single PR per the original "Steps 1 + 2 land together" rule:

1. Generated-code prep: `ck-clientUriEntry.yaml` + attribute declarations + `RedirectUris` flip + ckModel bump. Build will FAIL at this point — the C# generated code now has `IList<ClientUriEntry>` but the integration sites still pass `IList<string>`. Don't commit yet.
2. AutoMapper profile: explicit `RtClient → Client` map. Restores Duende projection.
3. Five concrete edits across `ClientsController`, `CreateIdentityDataCommandRequestConsumer`, `IdentityCorsPolicyProvider`, `RtClientBuilder`. Restores compile.
4. Seed-data lift (9 entries).
5. Migration YAML `ConstructionKit/migrations/2.8.0-to-2.9.0.yaml` calling Step 0's `WrapScalarInRecord`.
6. Step 2: `PreBlueprintCleanupMigration` predicate update + the `PreBlueprintCleanupMigration_PreservesOverlayUris` test class (per concept §6 first bullet).
7. Local validation per CLAUDE.md: `dotnet build Octo.Identity.sln -c DebugL` + `dotnet test Octo.Identity.sln -c Release`. Both green before commit.

If the engine-side Step 0 hasn't propagated through `invoke-buildall` by the time step 5 above is written, the migration runner will fail unknown-action; that's the integration check that confirms Step 0 is correctly published.
