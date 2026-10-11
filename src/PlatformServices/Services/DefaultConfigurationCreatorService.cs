using System.Collections.Concurrent;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Backend.PlatformServices.Services;

/// <summary>
///     Blueprint-only tenant bootstrap for platform-services. Owns the <c>System.UI</c> CK
///     model and its service-managed blueprints (the cockpit dashboards + the cross-cutting
///     <c>System.TenantMode</c> seed), applying them on every tenant Setup / lifecycle event.
///     Moved here from the retired <c>octo-frontend-admin-panel</c> (Phase 4).
/// </summary>
/// <remarks>
///     <para>
///         Built on <see cref="DefaultConfigurationCreatorServiceBase"/> rather than
///         <see cref="DefaultConfigurationCreatorServiceStandardized"/> on purpose: this service
///         seeds <b>no identity data</b>. The Standardized base unconditionally sends a
///         <c>CreateIdentityDataCommandRequest</c> over the distribution event hub on every tenant
///         setup (even when all <c>Create*</c> hooks are no-ops) and requires a non-null command
///         client; the Base carries the service-managed blueprint apply loop + the
///         <see cref="RefreshTenantStateAsync"/> lifecycle hook without that identity path. The
///         <c>octo-admin-panel</c> OIDC client the admin-panel used to seed is dead (only the
///         removed admin-panel UI authenticated with it), so there is nothing to seed.
///     </para>
/// </remarks>
internal sealed class DefaultConfigurationCreatorService : DefaultConfigurationCreatorServiceBase
{
    /// <summary>
    ///     Cross-cutting blueprint whose seed is driven by helm-injected variables that differ
    ///     between clusters (<c>${octo.environmentMode}</c>) and therefore needs a
    ///     <c>force=true</c> re-apply every time a tenant comes online — so a restore from prod-1
    ///     onto test-2 lands EnvironmentMode=Testing, not Production. It deliberately lives outside
    ///     the <c>System.UI.</c> namespace because it is platform-wide, not UI; the
    ///     <see cref="IsServiceManagedBlueprint"/> override allowlists it.
    /// </summary>
    private const string TenantModeBlueprintName = "System.TenantMode";

    private readonly ILogger<DefaultConfigurationCreatorService> _logger;
    private readonly ISystemContext _systemContext;
    private readonly IBlueprintService _blueprintService;
    private readonly IEnumerable<IBlueprintEmbeddedSource> _embeddedBlueprintSources;
    private readonly DefaultFilesRootService _defaultFilesRootService;

    // A System.Files failure of a tenant, kept until the end of its setup (see SetupTenantAsync / RefreshTenantStateAsync).
    private static readonly ConcurrentDictionary<string, Exception> PendingFilesFailures = new();

    public DefaultConfigurationCreatorService(
        ILogger<DefaultConfigurationCreatorService> logger,
        ISystemContext systemContext,
        IBlueprintService blueprintService,
        IEnumerable<IBlueprintEmbeddedSource> embeddedBlueprintSources,
        DefaultFilesRootService defaultFilesRootService,
        ITenantSetupRetryStore tenantSetupRetryStore)
        // AB#6480: the durable setup-retry queue retries a tenant whose System.Files setup failed (the setup throws). Only
        // those: the wrapper keeps every other setup failure (System.UI, TenantMode) out of the queue, as before.
        : base(logger, blueprintService, embeddedBlueprintSources,
            new SystemFilesOnlyTenantSetupRetryStore(tenantSetupRetryStore))
    {
        _defaultFilesRootService = defaultFilesRootService;
        _logger = logger;
        _systemContext = systemContext;
        _blueprintService = blueprintService;
        _embeddedBlueprintSources = embeddedBlueprintSources;
    }

    /// <summary>
    ///     Service-managed prefix for the cockpit blueprints. Narrow (<c>System.UI.</c>) so a
    ///     foreign <c>System.*</c> blueprint that happens to be in the same DI scope is not
    ///     auto-applied here.
    /// </summary>
    protected override string? ServiceManagedBlueprintPrefix => "System.UI.";

    /// <summary>
    ///     Accepts everything matched by <see cref="ServiceManagedBlueprintPrefix"/> plus the
    ///     explicit allowlist entry for the cross-cutting <see cref="TenantModeBlueprintName"/>.
    /// </summary>
    protected override bool IsServiceManagedBlueprint(BlueprintId blueprintId) =>
        base.IsServiceManagedBlueprint(blueprintId)
        || string.Equals(blueprintId.Name, TenantModeBlueprintName, StringComparison.Ordinal);

    /// <summary>
    ///     Applies every service-managed blueprint (System.UI.* + System.TenantMode) on the tenant.
    ///     The System.UI CK model + the per-tenant cockpit seed entities are packaged together in the
    ///     SystemCockpit (requires octo.isSystemTenant=true) and TenantCockpit
    ///     (requires octo.isSystemTenant=false) blueprints; applying all of them lets each manifest's
    ///     <c>requires:</c> decide which one matches per tenant. Runs on cold-start tenant iteration
    ///     and on every PosCreate / PosUpdate lifecycle event, so it doubles as the version
    ///     roll-forward path.
    /// </summary>
    protected override async Task SetupTenantAsync(string tenantId)
    {
        // Do nothing until the system tenant exists — Identity creates it, and a PosTenantCreated
        // event drives us once it is ready. Mirrors the guard the Standardized base used.
        if (!await _systemContext.IsSystemTenantExistingAsync().ConfigureAwait(false))
        {
            _logger.LogInformation(
                "System tenant does not exist yet. Skipping System.UI setup for tenant '{TenantId}'.",
                tenantId);
            return;
        }

        var tenantContext = tenantId == _systemContext.TenantId
            ? _systemContext
            : await _systemContext.GetChildTenantContextAsync(tenantId).ConfigureAwait(false);

        PendingFilesFailures.TryRemove(tenantId, out _);

        // AB#6480: System.Files is installed by the engine's service-managed descriptor when the tenant is resolved
        // and the default root "Files" is ensured right after. A failure here is FATAL for the tenant's setup (the
        // setup throws, the durable retry queue retries), but it must not block the other steps: System.UI and
        // System.TenantMode are still applied below, and the failure is thrown only at the end - after the TenantMode
        // refresh of RefreshTenantStateAsync on lifecycle events (the refresh is skipped when the setup throws).
        Exception? filesFailure = null;
        try
        {
            await _defaultFilesRootService.EnsureInstalledWithDefaultRootAsync(tenantId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            filesFailure = ex;
            _logger.LogError(ex,
                "System.Files setup failed for tenant '{TenantId}'; the other setup steps continue and the setup is retried",
                tenantId);
        }

        // After a tenant restore the DB exists but the CK cache has not been loaded yet; the
        // blueprint apply needs it to resolve types / well-known names.
        await tenantContext.LoadCacheForTenantAsync().ConfigureAwait(false);

        // throwOnFailure: true — a failed seed on Setup must surface immediately, not leave a tenant
        // half-provisioned.
        await ApplyServiceManagedBlueprintsAsync(tenantId, throwOnFailure: true).ConfigureAwait(false);

        if (filesFailure != null)
        {
            // AB#6576: the system tenant fails hard. Without System.Files the file capability answers
            // SYSTEM_FILES_MISSING for EVERY tenant, so a silent failure here would hide a broken release. The
            // system tenant's setup is deliberately unguarded in the host start (DefaultConfigurationInitializationService),
            // so the throw fails the service start visibly (host not ready, crash-loop instead of a rollout that looks
            // healthy). System.UI and System.TenantMode were applied above. The original exception is preserved; child
            // tenants keep the retry path below.
            if (tenantId == _systemContext.TenantId)
            {
                _logger.LogCritical(filesFailure,
                    "System.Files could not be set up in the SYSTEM tenant '{TenantId}'; failing the setup so the service start fails visibly",
                    tenantId);
                MarkAsFilesFailure(tenantId, filesFailure);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(filesFailure).Throw();
                return; // not reached: Throw() rethrows with the original stack trace
            }

            if (DeferTenantStart)
            {
                // Cold start: no RefreshTenantStateAsync follows, so this is the last step.
                MarkAsFilesFailure(tenantId, filesFailure);
                throw filesFailure;
            }

            PendingFilesFailures[tenantId] = filesFailure;
        }
    }

    /// <summary>
    ///     Tenant-online refresh: force-re-applies <see cref="TenantModeBlueprintName"/> so the
    ///     seed's <c>${octo.environmentMode}</c> resolves against the CURRENT cluster's helm value.
    ///     Called by the base from <c>SetupAsync</c> on every non-deferred path (PosCreate from
    ///     attach / restore, PosUpdate, manual Enable) — i.e. exactly when a tenant just arrived or
    ///     changed lifecycle state, and never on the cold-start deferred loop (so a pod restart does
    ///     not reset a maintenance-window MaintenanceLevel an operator just set).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>force=true</c> with <c>ImportStrategy.Upsert</c> rewrites EVERY seeded attribute
    ///         that is not marked <c>isRuntimeState</c> in the System CK — operator edits do NOT
    ///         survive it: <c>MaintenanceLevel</c> goes back to Off and, since 1.1.0, the two
    ///         observability switches go back to the seed default (true). Attach / restore / Enable
    ///         are rare enough that this is acceptable; the operator re-flips the values after the
    ///         refresh if needed.
    ///     </para>
    ///     <para>
    ///         AB#5497: this hook also ran for every nightly <c>PosUpdateTenant</c> with scope
    ///         <c>CacheOnly</c> (the bot-services autocomplete aggregation), which reset the
    ///         observability opt-in on every tenant every night. The shared
    ///         <c>PosCreatePosUpdateTenantConsumer</c> now skips <c>SetupAsync</c> for CacheOnly, so
    ///         only genuine lifecycle events reach this path. The structural fix — stamping the
    ///         operator-editable attributes <c>isRuntimeState</c> so the Upsert preserves them — is
    ///         tracked in AB#5497 for the next planned System CK bump.
    ///     </para>
    ///     <para>
    ///         Failures are logged, never propagated — the tenant is already operational, a stale
    ///         EnvironmentMode is a degradation, not a hard failure.
    ///     </para>
    /// </remarks>
    protected override async Task RefreshTenantStateAsync(string tenantId)
    {
        try
        {
            await RefreshTenantModeAsync(tenantId).ConfigureAwait(false);
        }
        catch
        {
            // The refresh's own failure wins; the Files failure is retried with the whole setup anyway.
            PendingFilesFailures.TryRemove(tenantId, out _);
            throw;
        }

        // AB#6480: the System.Files failure of SetupTenantAsync is thrown after the TenantMode refresh.
        if (PendingFilesFailures.TryRemove(tenantId, out var filesFailure))
        {
            MarkAsFilesFailure(tenantId, filesFailure);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(filesFailure).Throw();
        }
    }

    /// <summary>Tells the retry-store wrapper that the failure the base is about to record is the System.Files one.</summary>
    private static void MarkAsFilesFailure(string tenantId, Exception failure)
    {
        if (failure is SystemFilesSetupException)
        {
            SystemFilesOnlyTenantSetupRetryStore.MarkFilesFailure(tenantId);
        }
    }

    private async Task RefreshTenantModeAsync(string tenantId)
    {
        var tenantMode = _embeddedBlueprintSources
            .Where(s => string.Equals(s.BlueprintId.Name, TenantModeBlueprintName, StringComparison.Ordinal))
            .OrderByDescending(s => s.BlueprintId.Version)
            .FirstOrDefault();

        if (tenantMode == null)
        {
            _logger.LogWarning(
                "Tenant-online refresh skipped: {BlueprintName} is not embedded.",
                TenantModeBlueprintName);
            return;
        }

        _logger.LogInformation(
            "Refreshing {BlueprintId} on tenant {TenantId} (tenant came online — force-re-apply to sync EnvironmentMode with the cluster's ${{octo.environment}})",
            tenantMode.BlueprintId.FullName, tenantId);

        var result = await _blueprintService
            .ApplyBlueprintAsync(tenantId, tenantMode.BlueprintId, force: true)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            _logger.LogError(
                "Failed to refresh {BlueprintId} on tenant {TenantId}: {Messages}",
                tenantMode.BlueprintId.FullName, tenantId,
                string.Join("; ", result.OperationResult.GetMessages()));
        }
    }
}
