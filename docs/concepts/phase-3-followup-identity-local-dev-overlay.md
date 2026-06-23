# Phase 3 follow-up — Identity local-dev URI overlay

**Status:** Draft, 2026-06-19 — design agreed; §7 #7 (migration strategy) resolved 2026-06-19; an implementation spike on Step 1 surfaced the deviations recorded in §11 and was reverted before commit. Ready for Step 0 (engine-side `WrapScalarInRecord` transform) then Step 1.
**Author:** Gerald Lochner + Claude (concept skeleton).
**Tracking:** `AB#4209`.
**Scope:** `octo-identity-services` (CK model + pre-cleanup gate), `octo-tools` (overlay file + cmdlet + Start-Octo integration), `octo-cli` (DumpTenant filter mode). Builds directly on Phase 3 (`phase-3-identity-as-blueprint.md`); does **not** introduce a new template engine — that already shipped 2026-06-15.

## 1. Why this follow-up

Phase 3 turned the Identity tenant seed into a service-managed blueprint and wired `IdentityBlueprintVariableProvider` so client URIs can be authored with `${octo.identity.refineryStudioUrl}/...` placeholders that resolve at install/update time. End result on test-2 / staging-1 / prod is exactly what the design called for: one declarative seed file, one resolved URI per environment, baked into the DB, fast OIDC roundtrips.

The local-developer scenario, however, was never closed. The Communication-Operator's `{{domain.default}}` style works because the Helm values layer resolves to *the* domain the workload runs against. Identity URIs in a restored backup carry whichever environment's resolved URIs were in that backup — and the developer needs `http://localhost:<port>` URIs on top, *without* destroying the restored production URIs and *without* polluting subsequent re-exports.

Today the workaround is hand-editing Mongo documents per backup, every time. This is:

- **Destructive.** Once the test URIs are overwritten with localhost URIs, re-exporting the tenant produces a contaminated seed.
- **Unrepeatable.** Every developer's edits look different; no shared baseline.
- **Easy to forget.** When the developer is done debugging, the leftover localhost URIs from a partially-edited backup leak into the next test-2 import.

We need an additive, idempotent overlay layer that adds local-dev URIs alongside the resolved production URIs, marks them as overlay-source so the cleanup gate and export filter can distinguish them, and survives Phase 3's existing `force: true` re-applies untouched.

## 2. Concrete pain points

### P1 — Restored backup loses prod URIs on first OIDC roundtrip attempt

A developer restores a tenant dump from test-2 and tries to log into the locally-running Refinery Studio (`http://localhost:4200`). Duende's redirect-URI validator rejects the request because the client only has `https://refinery.test.octo-mesh.com/...` on its allow-list. The developer edits the Mongo document, replaces `https://refinery.test...` with `http://localhost:4200/...`. Login works. When the developer later runs `DumpTenant` to ship a reproducer back to the team, the dump contains `localhost:4200` in the seed — useless to anyone else and silently broken if re-imported.

### P2 — Phase 3 cleanup gate would re-destroy hand-edited URIs

`PreBlueprintCleanupMigration` (Phase 3 §5a #3) and the subsequent `RefreshTenantStateAsync` → `ApplyServiceManagedBlueprintsAsync(force: true)` rewrite the entire URI list on the blueprint's stable rtIds. A developer who hand-edited URIs and then ran any startup-triggered refresh loses the edits silently. There is no signal that something was overwritten.

### P3 — No shared baseline for "what URIs does a local dev need?"

Every developer reinvents the localhost URI set when they first hit the problem. The Start-Octo port allocation is canonical and shared (see `octo-platform-services/CLAUDE.md` port table); the URIs derived from it should be too. Today they are not.

## 3. Goal

After this follow-up:

1. The Identity CK model represents URIs as small objects `{ uri, source: "base" | "overlay:<name>" }`. Existing string-typed URIs lift in a one-shot CK migration with `source = "base"`.
2. `octo-tools/overlays/identity-local-dev.yaml` defines the canonical localhost URI set for every client that ships in `System.Identity.Bootstrap-1.0.0`.
3. A new `Apply-IdentityOverlay` cmdlet applies the overlay idempotently. Start-Octo invokes it automatically after Identity bootstrap finishes (`-SkipOverlay` opt-out).
4. `PreBlueprintCleanupMigration` and any blueprint `force: true` apply preserve URIs with `source != "base"`. Overlay URIs survive every Phase 3 lifecycle event.
5. `octo-cli DumpTenant --clean` filters `source != "base"` from the export, producing template-clean seed material.

Non-goals:

- Building a second template engine. Phase 3's `${...}` interpolation stays as-is; the overlay file is plain literal URIs, no second layer of substitution.
- Extending overlay support to `IdentityProvider`, `ApiResource`, `ApiScope`. None has surfaced a local-dev need.
- Generalising overlays to other services in this iteration. Identity is the only service today where the local-dev backup-restore scenario bites.
- Per-developer overlay files. The Start-Octo port table is shared; the localhost URI set is too.

## 4. Design

```mermaid
flowchart TB
    subgraph Phase3["Phase 3 (shipped 2026-06-15)"]
        BP[System.Identity.Bootstrap-1.0.0<br/>seed-data with ${octo.identity.refineryStudioUrl}]
        VAR[IdentityBlueprintVariableProvider<br/>resolves to env-specific URL]
        ENG[Blueprint Engine<br/>ApplyServiceManagedBlueprintsAsync force:true]
        GATE[PreBlueprintCleanupMigration<br/>deletes off-blueprint URIs]
    end

    subgraph New["This follow-up"]
        SCHEMA[CK migration: URI string -> { uri, source }<br/>octo-identity-services]
        OVL[octo-tools/overlays/identity-local-dev.yaml]
        CMD[Apply-IdentityOverlay cmdlet<br/>octo-tools]
        SO[Start-Octo auto-invokes after Identity bootstrap<br/>-SkipOverlay opt-out]
        DUMP[octo-cli DumpTenant --clean<br/>filters source != base]
        GATE2[Gate update: preserve source != base<br/>octo-identity-services]
    end

    BP --> VAR --> ENG --> GATE
    SCHEMA --> GATE2
    GATE2 -.replaces.-> GATE
    OVL --> CMD --> SO
    CMD --> SCHEMA
    SCHEMA --> DUMP
```

### 4.1 CK model schema change — `octo-identity-services`

The three URI-list attributes on `RtClient` lift from `IList<string>` to `IList<ClientUriEntry>`:

```csharp
// New CK type fragment (System.Identity-2.8.0 → 2.9.0)
public class ClientUriEntry
{
    public string Uri { get; set; } = "";
    public string Source { get; set; } = "base"; // "base" | "api" | "overlay:<name>"
}

// RtClient
public IList<ClientUriEntry> RedirectUris { get; set; } = [];
public IList<ClientUriEntry> PostLogoutRedirectUris { get; set; } = [];
public IList<ClientUriEntry> AllowedCorsOrigins { get; set; } = [];
```

CK-model bump: `System.Identity-2.8.0 → 2.9.0` (the concept's earlier "2.9.0 → 2.10.0" numbers were drafted against an anticipated state; the repo today still ships 2.8.0). Migration `20 → 21` walks every `RtClient`, replaces each string URI entry with `{ uri, source: "base" }`. The migration is idempotent (re-running on already-lifted documents is a no-op via type-check on each entry). See [[ck_model_project_setup]] for the standard CK-attribute-add gotchas.

Blueprint seed-data (`System.Identity.Bootstrap-1.0.0/seed-data/entities.yaml`) **must be lifted to the new record shape**. The earlier draft of this concept claimed the blueprint engine projects strings to `ClientUriEntry` automatically — it does not (the engine's `AttributeValueConverter.RecordArray` branch expects either `RtRecord[]`, `IAttributeRecordValueList`, or `IEnumerable<RtRecord>`; plain strings have no fallback). The seed-data spike covered 9 populated URI list entries across 3 clients (Refinery Studio, Identity Swagger, MCP Swagger); the 6 empty `value: []` entries on OctoTool + McpDevice survive unchanged. Each populated entry becomes:

```yaml
- id: System.Identity/RedirectUris
  value:
    - ckRecordId: System.Identity/ClientUriEntry
      attributes:
        - id: System.Identity/Uri
          value: '${octo.identity.refineryStudioUrl}/'
        - id: System.Identity/Source
          value: base
```

The IdentityServer integration layer (`OctoClientStore`, redirect-URI lookup) is adjusted to project `ClientUriEntry.Uri` for the IdentityServer4 `Client` POCO. Source is internal — IdentityServer never sees it.

### 4.2 Overlay file — `octo-tools/overlays/identity-local-dev.yaml`

Single shared file, checked into `octo-tools`. URIs and ports hardcoded against the Start-Octo port allocation. Example shape:

```yaml
$schema: https://schemas.meshmakers.cloud/identity-overlay.schema.json
overlayName: local-dev
clients:
  - clientId: octo-data-refinery-studio
    redirectUris:
      - http://localhost:4200/auth-callback
      - http://localhost:4200/silent-renew
    postLogoutRedirectUris:
      - http://localhost:4200/
    allowedCorsOrigins:
      - http://localhost:4200
  - clientId: OctoTool
    redirectUris:
      - http://localhost:5000/callback
  - clientId: octo-identity-services-swagger
    redirectUris:
      - https://localhost:5003/swagger/oauth2-redirect.html
    allowedCorsOrigins:
      - https://localhost:5003
```

Conventions:

- Identify clients by `clientId` (not `rtId`) — the overlay file is human-authored, the stable `660…30..` rtIds from the blueprint are not.
- No template variables in the overlay. The ports are canonical; the file is a literal source of truth.
- One overlay file per overlay name. `local-dev` is the default; future overlays (e.g. `gerald-laptop` for personal exceptions) are separate files.

### 4.3 `Apply-IdentityOverlay` cmdlet — `octo-tools`

```powershell
Apply-IdentityOverlay `
    -OverlayFile <path> `
    [-TenantId <id>] `         # default: all tenants
    [-OverlayName <name>] `    # default: derived from file's overlayName field
    [-DryRun]
```

Behaviour:

1. Connect to the Identity service via `octo-cli` (uses ambient AuthStatus; see [[octo_cli_authstatus_refreshes]]).
2. For each `client` in the overlay file:
   - Look up the `RtClient` by `clientId` on the target tenant(s).
   - For each URI in each list:
     - If an entry with the same `uri` exists (regardless of source): no-op.
     - Otherwise: append `{ uri, source: "overlay:<name>" }`.
3. Log `[base]` / `[overlay:<name>]` / `[skip-duplicate]` per URI; one summary line per client.

Idempotency: re-running on the same DB is a no-op (all overlay URIs already present, no duplicates). Conflict policy: base wins (overlay providing a URI already in base = skip-duplicate, not error).

The cmdlet does not delete anything. Removal of stale overlay URIs is the cleanup gate's job (§4.5).

### 4.4 Start-Octo integration

After Identity bootstrap finishes (the loop in `Start-Octo.ps1` that waits for `/healthz/ready`), invoke `Apply-IdentityOverlay -OverlayFile <repo>/octo-tools/overlays/identity-local-dev.yaml`. New flag `-SkipOverlay` on `Start-Octo` for the rare case a developer wants the raw restored state.

Failures are logged as warnings, not fatal. Identity comes up either way; the developer just gets the OIDC-rejection signal if the overlay didn't run.

### 4.5 Source taxonomy — three values

`ClientUriEntry.Source` carries one of three values; the cleanup gate and the overlay apply both branch on it.

| Source | Producer | Survives blueprint re-apply? | Filtered by `DumpTenant --clean`? |
|---|---|---|---|
| `"base"` | Blueprint seed (`System.Identity.Bootstrap-1.0.0/seed-data/entities.yaml`) + `CreateIdentityDataCommandRequestConsumer` (cross-service identity bootstrap) | NO — rewritten on every re-apply to match current seed | NO — preserved (it IS the template-clean material) |
| `"api"` | REST API operator-add (`ClientsController.ApplyToClient` → `PATCH/POST /v1/clients/...`) — Studio Client-UI is the typical caller | YES — survives every re-apply / cleanup pass | NO — included in `--clean` export (an operator-blessed permanent config; the export consumer can decide whether to keep it) |
| `"overlay:<name>"` | `Apply-IdentityOverlay` cmdlet (octo-tools) — typical name `local-dev` | YES — survives every re-apply / cleanup pass | YES — filtered out of `--clean` export (dev-only state, not template material) |

The `"api"` value was added 2026-06-19 after the Step-1 edit-plan spike surfaced the gap: without it, REST-API-added URIs would carry `"base"` by default and get destroyed on the next blueprint re-apply — a silent regression on top of the existing Studio Client-UI flow. The three-value taxonomy keeps the cleanup gate's rule trivial (`source == "base"` deletes), the overlay rule trivial (`source != "base"` survives), and the export rule explicit (`--clean` filters `overlay:*` only, keeping `api` because those entries are not dev-only state).

### 4.6 Pre-cleanup gate update — `octo-identity-services`

`PreBlueprintCleanupMigration` (Phase 3 §5a #3, gated on `Client.ClientId` whitelist per [[feedback_preblueprint_cleanup_gate]]) walks every `RtClient` URI and deletes entries that fall outside the blueprint's stable rtId range. With the schema change, the predicate becomes:

```csharp
// Old: delete if not in blueprint's stable rtId range
// New: delete if not in blueprint's stable rtId range AND source == "base"
bool shouldDelete = entry.Source == "base" && !IsInBlueprintRtIdRange(entry);
```

`source != "base"` entries are preserved unconditionally. The next `RefreshTenantStateAsync` re-apply (Phase 3 §4.4) similarly only overwrites `source == "base"` slots; overlay slots survive.

This is the **load-bearing rule** that makes the whole follow-up safe across Phase 3's lifecycle events. Reviewer check: any new gate or apply path that touches client URIs must respect it.

### 4.7 `DumpTenant --clean` mode — `octo-cli`

New flag on the existing `octo-cli DumpTenant` command:

- `--clean`: filter `source` values matching `overlay:*` from every URI list before serialising. `"base"` and `"api"` entries survive (per §4.5 table). Result is a tenant dump safe to re-import as Blueprint seed material.
- Default (no flag): full dump, includes overlay-source URIs. Existing behaviour for real backups.

Only `RtClient` URIs are filtered today; the filter location is the runtime-model serialiser path that handles `ClientUriEntry`. If we ever add `source` to other entities, the same flag generalises.

## 5. Migration plan

Six steps now (Step 0 added per §7 #7 decision), each independently revertable. Order matters: engine-side transform first, schema change before overlay, gate update before any blueprint re-apply runs against the new schema.

| Step | Repo | What | Risk |
|---|---|---|---|
| 0 | octo-construction-kit-engine (+ octo-construction-kit-engine-mongodb if needed) | Add `WrapScalarInRecord` action to the CK-migration YAML schema (`ck-migration.schema.json`) + parser + executor in `CkModelMigrationService`. Inputs: `sourceAttribute`, `targetRecordCkRecordId`, `recordValueAttribute`, `recordDefaults`. Unit + integration coverage. Publish via `OctoVersion` bump so DebugL nuget consumers pick it up. | low — additive engine surface; first consumer (Identity) shipped in Step 1 exercises it end-to-end. |
| 1 | octo-identity-services | CK-model bump `System.Identity-2.8.0 → 2.9.0`. Add `ClientUriEntry` record + `Uri` / `Source` attributes. Convert `RedirectUris` / `PostLogoutRedirectUris` / `AllowedCorsOrigins` from `StringArray` to `RecordArray<ClientUriEntry>`. Lift the 9 populated URI list entries in `System.Identity.Bootstrap-1.0.0/seed-data/entities.yaml`. Author `ConstructionKit/migrations/2.8.0-to-2.9.0.yaml` using the Step 0 `WrapScalarInRecord` action (one step per URI attribute, `Uri ← original`, `Source = "base"`). IdentityServer integration layer projects `.Uri` (~44 call sites in 6 files: `ClientsController`, `ClientMirrorProvisioningService`, `IdentityCorsPolicyProvider`, `CreateIdentityDataCommandRequestConsumer`, `ClientStore`, AutoMapper `MapperProfile`). | **medium** — additive at the CK level, but the typed-API breaking change cascades through every IdentityServer integration site. Requires Step 0 to be merged + propagated via `invoke-buildall` first. |
| 2 | octo-identity-services (or octo-construction-kit-engine) | **Move the load-bearing rule to the blueprint-apply path, NOT to `PreBlueprintCleanupMigration`.** The Step-1 implementation surfaced (2026-06-19) that the current `PreBlueprintCleanupMigration` (17 → 18) does entity-level deletion by name whitelist and never touches URI lists — so the concept's original "update the predicate" framing didn't apply. The actual load-bearing site is the per-startup `BlueprintService.ApplyBlueprintAsync` / `DefaultConfigurationCreatorService.SetupTenantAsync` path that replaces URI lists on every Identity boot. Step 2 adds a pre-apply hook that captures `source != "base"` URI entries off each blueprint-managed client, lets the apply rewrite the list, and re-injects the captured entries afterwards. Tests: round-trip a fake `api`-sourced + `overlay:local-dev`-sourced URI through a full apply cycle and assert both survive. | **medium** — Identity blast radius (see [[phase3_identity_test2_incident]]). Becomes load-bearing once Steps 3+4 ship overlay URIs; until then the world is at status-quo (API-added URIs reset on every restart). |
| 3 | octo-tools | Add `Apply-IdentityOverlay` cmdlet + `overlays/identity-local-dev.yaml`. Standalone — runnable without step 4. | low |
| 4 | octo-tools | Wire `Start-Octo` to auto-invoke `Apply-IdentityOverlay` after Identity health check passes. Add `-SkipOverlay` flag. | low — opt-out path is one line |
| 5 | octo-cli | Add `DumpTenant --clean` filter for `source != "base"` on URI entries. | low — additive flag, default behaviour unchanged |

Step 0 + the engine-side AB#4208 work (PR #193 `octo.scheme` / `octo.domain` Blueprint variables) ship together on `octo-construction-kit-engine` dev/4209-wrap-scalar-in-record-migration as a combined PR (merge done 2026-06-19). Step 1 lands on `octo-identity-services` dev/4208-mcp-clients-in-bootstrap-blueprint as a combined PR closing both AB#4208 (PR #97 MCP-clients seed) and AB#4209 Step 1 (URI shape lift). Step 2 ships as a separate PR after Step 1 lands (decoupled 2026-06-19 because the original "update PreBlueprintCleanupMigration predicate" framing didn't match the code — Step 2 is now scoped against the blueprint-apply path instead). Steps 3–5 land independently in any order after Step 2 ships.

## 6. Risks

- **Cleanup-gate regression.** Step 2 is the load-bearing change. A future contributor who forgets to check the `Source` field re-introduces the destructive behaviour. Mitigation: a dedicated test class `PreBlueprintCleanupMigration_PreservesOverlayUris` with one assertion per URI list type. The test is the docstring.
- **IdentityServer integration layer drift.** `OctoClientStore` projects `ClientUriEntry.Uri` into IdentityServer's `Client` POCO. If the IdentityServer4 upgrade ever changes the URI representation (it has been stable for years), the projection point is the single fix.
- **Overlay file drifts from Start-Octo ports.** If a future port reallocation in Start-Octo isn't mirrored into `identity-local-dev.yaml`, local OIDC silently breaks until someone notices. Mitigation: a small `octo-tools` test that reads both files and asserts the ports line up. The test runs in CI and on `invoke-buildall`.
- **Re-import of `--clean` dump on top of a tenant that already has overlay URIs.** Today's `DumpTenant` round-trip semantics treat re-import as a replace. If we re-import a clean dump on top of a dev tenant, the existing overlay URIs survive on the source-filter (they are `source != "base"`, not in the dump). This is the intent. Worth a unit test on the import path to lock it down.

## 7. Open decisions — resolved

1. **Overlay storage location** → **decided: zentral in `octo-tools/overlays/`.** One shared file, geshared across all developers. Per-developer files were rejected for consistency reasons.
2. **Port-Quelle im Overlay-YAML** → **decided: hardcoded.** No second template-substitution layer. Mirrors the Start-Octo port table directly.
3. **Source tracking granularity** → **decided: URI-Ebene** (each URI entry has its own `source` field). Allows base + overlay URIs to coexist within the same client.
4. **Scope** → **decided: Client only.** IdentityProvider / ApiResource / ApiScope deferred until a pain signal arrives.
5. **Conflict policy** → **decided: dedupe, base wins.** Overlay providing a URI already in base is a harmless no-op.
6. **Apply-Trigger in Start-Octo** → **decided: auto with `-SkipOverlay` opt-out.** Default dev path works zero-config; opt-out for rare cases.
7. **Runtime-data migration strategy for the `StringArray → RecordArray<ClientUriEntry>` lift** → **decided: (b) CK-model migration YAML with a new `TransformValueToRecord` transform type, 2026-06-19.** The engine ships no built-in transform for "rewrite every entry of an attribute value from scalar to record" — the existing CK-model migration YAML supports `RenameAttribute` + `ChangeCkType` only, and the per-tenant `IMigration<T>` framework runs *after* the new CK model is loaded into the cache, by which point the typed `RtClient.RedirectUris` property is `IList<ClientUriEntry>` and BSON deserialisation of the old `redirectUris: ["str", ...]` shape against it is undefined.

    The rejected alternative was **(a) raw-BSON document rewrite via a new `IRuntimeRepository.RewriteAttributeValueForMigrationAsync` API + a service-side `IMigration` class.** (a) keeps the change surface inside Identity (lower blast-radius), but (b) was picked because:

    - The transform is declarative and lives next to the CK model — visible to the same reader who sees `RedirectUris: RecordArray` in `identity-attributes.yaml`.
    - Future scalar→record lifts (e.g. operator value-overrides, application config keys) get the transform for free instead of reinventing the migration class.
    - Runs as part of CK-model import — the typed-API timing problem is solved by construction, not by careful ordering.

    Concrete shape: extend `ck-migration.schema.json` with a `WrapScalarInRecord` (or similar) action that takes `sourceAttribute`, `targetRecordCkRecordId`, `recordValueAttribute` (which record-attribute receives the wrapped scalar), and `recordDefaults` (other record attributes filled with literal defaults). Implementation in `CkModelMigrationService` (engine) + the Identity CK script `ConstructionKit/migrations/2.8.0-to-2.9.0.yaml`. Engine PR ships first, propagates via DebugL nuget, Identity follows. Sequencing: do the engine work in `octo-construction-kit-engine` (+ `octo-construction-kit-engine-mongodb` if a Mongo-side hook is needed) as its own merged PR with unit + integration coverage, then bring it into Identity via `invoke-buildall -branch main -configuration DebugL -excludeFrontend $true -excludeAdditional $true`.


## 11. Step 1 implementation spike — 2026-06-19 findings

A short Step 1 spike was run, then reverted before any commit. Captured here so the next attempt starts from a corrected baseline.

- **Version numbers in the original draft were stale.** `octo-identity-services` ships `System.Identity-2.8.0` today (CLAUDE.md mentions 2.7.0; both are behind the actual `ckModel.yaml`). The highest declared `[Migration(...)]` in `IdentityServerPersistence/Services/Migrations/` is `19 → 20`. Step 1 therefore bumps to **2.9.0** and adds migration **20 → 21**.
- **The "no YAML edit needed in the seed file" claim was wrong.** The blueprint engine's `AttributeValueConverter.RecordArray` branch only accepts `RtRecord[]`, `IAttributeRecordValueList`, or `IEnumerable<RtRecord>`; plain strings are not lifted. The seed has 15 URI-list occurrences across 5 clients (9 populated entries on Refinery Studio, Identity Swagger, MCP Swagger × 3 attributes; 6 empty `value: []` entries on OctoTool + McpDevice). Each populated entry needs the explicit `ckRecordId` + `attributes` form.
- **Integration-layer adaptation is larger than "low risk" suggested.** ~44 references to `RedirectUris` / `PostLogoutRedirectUris` / `AllowedCorsOrigins` live across `ClientsController` (DTO ↔ entity mapping), `ClientMirrorProvisioningService` (parent → child client copy), `IdentityCorsPolicyProvider` (Duende policy generation), `CreateIdentityDataCommandRequestConsumer` (distribution-event-hub identity push), `ClientStore` / `OctoClientStore` (Duende `Client` POCO projection), AutoMapper `MapperProfile`. Every one of those needs to project `ClientUriEntry.Uri` into the consumer (a string-typed surface) and wrap incoming strings as `{ Uri = s, Source = "base" }` on the entity side.
- **Runtime-data migration strategy is not obvious.** Resolved in §7 #7 on the same day: option (b), a new CK-migration YAML `WrapScalarInRecord` action shipped from the engine as Step 0. Step 1 picks it up via `invoke-buildall`.
- **Identity blast-radius asymmetry.** This service was the source of the two 2026-06-15 test-2 outages documented in [[phase3_identity_test2_incident]]. The original §6 risk note still applies: Step 2 (the cleanup-gate update) is the single load-bearing rule that makes overlays survive Phase 3's lifecycle events, and Step 1 (this concept piece) is its prerequisite. A staged Step 1 that lands the schema + seed but leaves the migration + integration sites for a second commit would produce a non-deployable in-between state, so the original "Steps 1 + 2 land together" rule stays.

What the spike actually produced (and discarded):

- New `ck-clientUriEntry.yaml` record (`Uri`, `Source` attributes).
- New `Uri` + `Source` attribute declarations in `identity-attributes.yaml`.
- `RedirectUris` / `PostLogoutRedirectUris` / `AllowedCorsOrigins` converted from `StringArray` to `RecordArray` referencing `ClientUriEntry`.
- `ckModel.yaml` bumped to `System.Identity-2.9.0`.
- Seed-data `entities.yaml` 9 populated URI-list entries rewritten to the explicit `ckRecordId` + `attributes` form.

All five edits were reverted before commit. With §7 #7 resolved on 2026-06-19, the next attempt is Step 0 (engine-side `WrapScalarInRecord` transform), then Step 1 picks the previous spike work back up — schema + seed-data + new CK migration script + integration adaptation — as a single atomic change.

---

Concept dated 2026-06-19. Ready for **Step 0** (engine-side `WrapScalarInRecord` transform), then Step 1, when prioritised against the active Pipeline-YAML-Migration and OctoMesh-AI workstreams.
