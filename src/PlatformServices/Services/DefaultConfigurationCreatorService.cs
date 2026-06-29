using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
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

    public DefaultConfigurationCreatorService(
        ILogger<DefaultConfigurationCreatorService> logger,
        ISystemContext systemContext,
        IBlueprintService blueprintService,
        IEnumerable<IBlueprintEmbeddedSource> embeddedBlueprintSources)
        : base(logger, blueprintService, embeddedBlueprintSources)
    {
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

        // After a tenant restore the DB exists but the CK cache has not been loaded yet; the
        // blueprint apply needs it to resolve types / well-known names.
        await tenantContext.LoadCacheForTenantAsync().ConfigureAwait(false);

        // throwOnFailure: true — a failed seed on Setup must surface immediately, not leave a tenant
        // half-provisioned.
        await ApplyServiceManagedBlueprintsAsync(tenantId, throwOnFailure: true).ConfigureAwait(false);
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
    ///     Caveat carried over from admin-panel: <c>force=true</c> with <c>ImportStrategy.Upsert</c>
    ///     rewrites every attribute in the seed, including <c>MaintenanceLevel</c> which is reset to
    ///     Off. Attach / restore is rare enough that this is acceptable; the operator re-flips
    ///     MaintenanceLevel after the refresh if needed. Failures are logged, never propagated — the
    ///     tenant is already operational, a stale EnvironmentMode is a degradation, not a hard failure.
    /// </remarks>
    protected override async Task RefreshTenantStateAsync(string tenantId)
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
