namespace Meshmakers.Octo.Backend.PlatformServices;

/// <summary>
///     Per-service schema-version registry surfaced by
///     <c>GET system/v1/services/{serviceKey}/drift</c>. Concept §8.3 decision:
///     CK models that ship without a blueprint wrapper (Identity, Bot, Report) are
///     surfaced by reading the per-service schema-version config key the service writes
///     into its tenant config row during <c>SetupTenantAsync</c>. The mapping is
///     hardcoded here so the observability API stays read-only and decoupled from each
///     service's runtime.
/// </summary>
/// <remarks>
///     Whenever a service bumps its own <c>VersionValue</c> constant the bump must be
///     mirrored here, otherwise the drift endpoint reports stale "in sync" rows. There
///     is no automation closing this; the cost of a missed bump is a stale dashboard,
///     not a runtime regression.
/// </remarks>
internal static class ServiceSchemaVersions
{
    /// <summary>
    ///     Where a service's schema-version row lives — see <see cref="ServiceSchema"/>.
    /// </summary>
    public enum SchemaScope
    {
        /// <summary>
        ///     Version is written into the system tenant's configuration only — the service
        ///     either has no per-child-tenant identity data (Identity itself bootstraps the
        ///     system tenant on its own DB) or the per-child-tenant path is keyed off
        ///     <c>ChildTenant</c> below.
        /// </summary>
        SystemTenant,

        /// <summary>
        ///     Version is written into every tenant's configuration (system + child). The drift
        ///     endpoint iterates every tenant and reports the per-tenant installed version.
        /// </summary>
        EveryTenant
    }

    public readonly record struct ServiceSchema(
        string ServiceKey,
        string ConfigKey,
        int ExpectedVersion,
        SchemaScope Scope);

    private static readonly Dictionary<string, ServiceSchema> KnownByKey =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Identity is the foundation — its schema-version row is written into the system
            // tenant only (the EnsureIdentityDataInChildTenantAsync write-through is gated by
            // an entity-presence check, not a version field). The known version value is
            // sourced from IdentityServerPersistence.IdentityServiceConstants.
            ["identity"] = new("identity", "IdentityService", 17, SchemaScope.SystemTenant),

            // Bot Service: version row written per tenant via Standardized's CheckSetupIdentityDataAsync.
            // Sourced from BotServices.BotServiceConstants.BotServiceIdentityDataVersionKey / Value.
            ["bot"] = new("bot", "BotServicesIdentityData", 2, SchemaScope.EveryTenant),

            // Report Service: same pattern. Sourced from
            // ReportingServices.Constants.ReportingServiceIdentityDataVersionKey / Value.
            ["report"] = new("report", "ReportingServicesIdentityData", 3, SchemaScope.EveryTenant)

            // MACO intentionally omitted for now — its CK models are tenant-app-managed and
            // do not fit the "platform service" mental model the drift endpoint surfaces.
            // Add here if/when MACO emits a per-tenant schema-version config row.
        };

    public static IReadOnlyCollection<string> KnownServiceKeys => KnownByKey.Keys;

    public static bool TryGet(string serviceKey, out ServiceSchema schema) =>
        KnownByKey.TryGetValue(serviceKey, out schema);
}
