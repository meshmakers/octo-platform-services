# Phase 3 follow-up — Identity local-dev URI overlay

**Status:** Draft, 2026-06-19 — design agreed, nothing implemented yet.
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
// New CK type fragment (System.Identity-2.9.0 → 2.10.0)
public class ClientUriEntry
{
    public string Uri { get; set; } = "";
    public string Source { get; set; } = "base"; // "base" | "overlay:<name>"
}

// RtClient
public IList<ClientUriEntry> RedirectUris { get; set; } = [];
public IList<ClientUriEntry> PostLogoutRedirectUris { get; set; } = [];
public IList<ClientUriEntry> AllowedCorsOrigins { get; set; } = [];
```

CK-model bump: `System.Identity-2.9.0 → 2.10.0`. Migration `21 → 22` walks every `RtClient`, replaces each string URI entry with `{ uri, source: "base" }`. The migration is idempotent (re-running on already-lifted documents is a no-op via type-check on each entry). See [[ck_model_project_setup]] for the standard CK-attribute-add gotchas.

Blueprint seed-data (`System.Identity.Bootstrap-1.0.0/seed-data/entities.yaml`) continues to author URIs as plain string lists; the blueprint engine's apply marks them `source = "base"` when projecting onto the new schema. No YAML edit needed in the seed file.

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

### 4.5 Pre-cleanup gate update — `octo-identity-services`

`PreBlueprintCleanupMigration` (Phase 3 §5a #3, gated on `Client.ClientId` whitelist per [[feedback_preblueprint_cleanup_gate]]) walks every `RtClient` URI and deletes entries that fall outside the blueprint's stable rtId range. With the schema change, the predicate becomes:

```csharp
// Old: delete if not in blueprint's stable rtId range
// New: delete if not in blueprint's stable rtId range AND source == "base"
bool shouldDelete = entry.Source == "base" && !IsInBlueprintRtIdRange(entry);
```

`source != "base"` entries are preserved unconditionally. The next `RefreshTenantStateAsync` re-apply (Phase 3 §4.4) similarly only overwrites `source == "base"` slots; overlay slots survive.

This is the **load-bearing rule** that makes the whole follow-up safe across Phase 3's lifecycle events. Reviewer check: any new gate or apply path that touches client URIs must respect it.

### 4.6 `DumpTenant --clean` mode — `octo-cli`

New flag on the existing `octo-cli DumpTenant` command:

- `--clean`: filter `source != "base"` from every URI list before serialising. Result is a tenant dump safe to re-import as Blueprint seed material.
- Default (no flag): full dump, includes overlay-source URIs. Existing behaviour for real backups.

Only `RtClient` URIs are filtered today; the filter location is the runtime-model serialiser path that handles `ClientUriEntry`. If we ever add `source` to other entities, the same flag generalises.

## 5. Migration plan

Five steps, each independently revertable. Order matters: schema change before overlay, gate update before any blueprint re-apply runs against the new schema.

| Step | Repo | What | Risk |
|---|---|---|---|
| 1 | octo-identity-services | CK-model bump `System.Identity-2.9.0 → 2.10.0`. Add `ClientUriEntry`. Migration `21 → 22` lifts existing string URIs to `{ uri, source: "base" }`. IdentityServer integration layer projects `.Uri`. No behaviour change to OIDC flows. | low — additive schema, idempotent migration |
| 2 | octo-identity-services | Update `PreBlueprintCleanupMigration` predicate to skip `source != "base"`. Update any other gate / apply path that touches URI lists. Tests: round-trip a fake overlay URI through `RefreshTenantStateAsync` and assert it survives. | **medium** — the load-bearing rule (§4.5). Identity blast radius. |
| 3 | octo-tools | Add `Apply-IdentityOverlay` cmdlet + `overlays/identity-local-dev.yaml`. Standalone — runnable without step 4. | low |
| 4 | octo-tools | Wire `Start-Octo` to auto-invoke `Apply-IdentityOverlay` after Identity health check passes. Add `-SkipOverlay` flag. | low — opt-out path is one line |
| 5 | octo-cli | Add `DumpTenant --clean` filter for `source != "base"` on URI entries. | low — additive flag, default behaviour unchanged |

Each step ships in its own PR. Steps 1 + 2 land together (the gate update needs the schema). Steps 3–5 land independently in any order after.

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

---

Concept dated 2026-06-19. Ready for step 1 (CK-model bump + migration) when prioritised against the active Pipeline-YAML-Migration and OctoMesh-AI workstreams.
