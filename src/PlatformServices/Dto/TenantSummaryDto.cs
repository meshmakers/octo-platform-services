using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Backend.PlatformServices.Dto;

/// <summary>
///     Per-tenant summary projected by <c>GET system/v1/tenants</c>. Carries only the
///     identification fields the observability layer needs; deeper detail (blueprints,
///     CK models, schema versions) is on separate endpoints to keep the list response
///     cheap and stable.
/// </summary>
public sealed class TenantSummaryDto
{
    /// <summary>Tenant id (mongo route segment used by every service).</summary>
    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    /// <summary>Underlying MongoDB database name.</summary>
    [JsonPropertyName("databaseName")]
    public required string DatabaseName { get; init; }

    /// <summary>
    ///     True for the system tenant (default <c>octosystem</c>). Convenient flag so
    ///     dashboards can hide it from drift / coverage views.
    /// </summary>
    [JsonPropertyName("isSystemTenant")]
    public required bool IsSystemTenant { get; init; }
}
