---
description: How platform-services installs the System.Files CK model and seeds the default root 'Files', including the retry and the system-tenant fail-hard rule.
applies_to: src/PlatformServices/Services/**, tests/PlatformServices.IntegrationTests/**, tests/PlatformServices.ContractTests/SystemFilesOnlyTenantSetupRetryStoreTests.cs
---

# System.Files install and default root

## `System.Files` CK model install and the default root (AB#6480)

platform-services installs the **System.Files** CK model (platform file system; source in
octo-construction-kit-engine, package `Meshmakers.Octo.ConstructionKit.Models.System.Files` from the libs train)
into every tenant and creates its default root `Files`. asset-repo and report-services import nothing for it.

- **Install = `ServiceManagedCkModelDescriptor` only** (`Program.cs`, same mechanism as System.UI): the engine imports
  the model at its embedded version on tenant resolve, once per process and tenant, with a downgrade guard. A tenant
  that already has `System.Files-1.0.0` (installed by asset-repo before) is skipped: no second import, no ERROR, no
  entity change. System.Files has no seed, so there is no blueprint. There is deliberately **no** explicit install in
  `SetupTenantAsync`: nothing in asset-repo needs the model during its own setup, so the order of the services at a
  tenant start is irrelevant.
- **Default root = `DefaultFilesRootService`**, called from `DefaultConfigurationCreatorService.SetupTenantAsync`
  right after the tenant is resolved (that resolve installs the model), before the blueprint apply: a plain
  `System.Files/FolderRoot` with `name` and well-known name `Files` (no protection flag on the entity - the asset-repo
  mutation guard protects roots by type; owner = the admin session). It is what asset-repo created at its tenant start
  before. `SetupTenantAsync` runs for new tenants, attach/restore (PosCreate), full updates (PosUpdate), and the
  cold-start iteration of **all** tenants including the system tenant; a CacheOnly update skips the setup (shared
  consumer, AB#5497) and the nightly TenantMode re-apply only force-applies the `System.TenantMode` blueprint, so
  neither touches System.Files.
- **Observable**: `DefaultFilesRootService` logs at Information `Ensuring default file root 'Files' for tenant ...` at the start, `Default file root already exists for tenant ...: N root(s), rtId ...` on the exists path and `Created the default file root ...` when it seeds one, so a tenant without a root can be told from a healthy one in the logs (AB#6482 finding on test-2).
- **Idempotent**: one indexed query when the root exists (matched case-insensitively on the well-known name, as the
  file API resolves roots). Every pod runs the setup, so two pods can seed `Files` at once (no unique index on
  well-known names): the root wins that the file API answers (exact-case `Files` first, then the oldest - the order of
  asset-repo's `FindRootAsync`), empty others are erased (also on a later run), a duplicate with content is kept and
  logged; a failed clean-up only logs.
- **A failed install is fatal for that tenant's setup (Gerald's decision).** The engine's descriptor import logs its
  cause and does not throw (missing dependency, `ResolveFailed`), so `DefaultFilesRootService` checks
  `IsCkModelSatisfiedAsync(System.Files-1.0.0)` afterwards, imports again if it is missing (`ImportCkModelWithDowngradeGuardAsync`:
  the descriptor's per-process guard is spent after one failed attempt, so a retry that only re-checked would heal nothing)
  and only then throws `SystemFilesSetupException` ("System.Files could not
  be set up for tenant 'x': <cause, e.g. the tenant's System model is older than System-2.5.0>. Model: System.Files-1.0.0").
  The creator passes the engine's `ITenantSetupRetryStore` to the base, so `SetupAsync` records the failure durably and the
  background loop (`FailedTenantRetryBackgroundService`: first run 30 s after start, every 30 s) re-runs the setup: constant
  60 s between attempts per tenant, **10 attempts**, a 5 min lease (single-flight across pods), 10 tenants per tick; after the
  budget the entry stays for an operator (a new tenant event, a restart or a successful setup clears/resets it). No
  exponential backoff, no unbounded retry. One tenant never blocks another (the cold-start loop catches per tenant and
  records the retry). A tenant that no longer exists keeps its bare `TenantException` (`IsTenantNotFound`), so the retry loop
  drops the entry (AB#4829) instead of retrying it 10 times.
- **Only System.Files failures are retried.** The base records *every* exception of `SetupTenantAsync` in the store it gets, so
  the creator passes a `SystemFilesOnlyTenantSetupRetryStore` wrapper: `RecordFailureAsync` is forwarded for a System.Files
  failure (typed marker set right before the `SystemFilesSetupException` is thrown; the exception's message prefix as fallback)
  and for a tenant that is already queued (a retry in progress still counts its attempt and releases the lease); claim, clear,
  release and list are forwarded unchanged. Not retried, exactly as before: System.UI / TenantMode blueprint failures and every
  other setup error (logged, thrown, picked up by the next event or restart). Bounds of the retry: first tick 30 s after start,
  tick every 30 s, 60 s between attempts per tenant, 10 attempts, then the entry waits for an operator.
- **The other steps are not blocked.** In `SetupTenantAsync` the Files failure is caught, the System.UI/TenantMode blueprint
  apply still runs, and the failure is thrown last. Both paths are real: `DeferTenantStart` is a per-instance flag, set true only on the instance of
  `DefaultConfigurationInitializationService` (cold start; never reset - only the Standardized base resets it) while
  `IDefaultConfigurationCreatorService` is registered transient, so the event consumers and the retry loop get fresh instances
  with `false`. Cold start: thrown at the end of `SetupTenantAsync`; lifecycle events and retries: after `RefreshTenantStateAsync` (the TenantMode
  force-refresh, which the base skips when the setup throws). **System tenant fails hard (AB#6576, Gerald's decision 2026-10-11):** without System.Files the file capability answers
  `SYSTEM_FILES_MISSING` for every tenant, so a System.Files failure in the **system tenant** is thrown (after System.UI / System.TenantMode
  were applied) - `SystemFilesSetupException`, original exception preserved, message names System.Files, `System.Files-1.0.0` and the
  system tenant. The system tenant's setup is unguarded in the host start (`DefaultConfigurationInitializationService`, collected by
  `HostedInitializer`), so the **service start fails**: the process exits before Kestrel serves anything, `/healthz/live` and `/healthz/ready` never
  answer (pod not ready, crash-loop; Kubernetes keeps the previous ReplicaSet in a rolling update). What the operator sees: a CRITICAL log line
  "System.Files could not be set up in the SYSTEM tenant ..." plus the host start error. Recovery: fix the cause named in the message (usually a
  System model older than System-2.5.0 or an unreachable/readonly tenant database) and restart/roll out again; an already installed
  `System.Files-1.0.0` is untouched and a healthy system tenant starts as before. Child tenants keep the retry path above (60 s / 10 attempts, other
  tenants and the host are not blocked).
- **Edge**: edge clusters run the communication operator and adapters only; the tenant database, asset-repo and
  platform-services are central, so edge pools use the System.Files of the central tenant. A self-contained
  installation with asset-repo but `platformServices.deploy: false` never gets System.Files
  (`SYSTEM_FILES_MISSING` in the file API) - deploy platform-services or import the model with octo-cli.
- Tests: `tests/PlatformServices.IntegrationTests` (Testcontainers MongoDB): install-once + root for a new and for
  the system tenant, idempotent repeated setup, tenant that already has the model and a root (left untouched), failing install (setup throws, retry recorded, System.UI and the TenantMode refresh still run, another tenant unaffected, retry succeeds once the model is resolvable, cold-start path, system tenant fails hard / healthy system tenant starts), concurrent pods, duplicate clean-up.
