using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Backend.PlatformServices.Dto;

/// <summary>
///     Cross-tenant coverage of a single blueprint name. Returned by
///     <c>GET system/v1/blueprints/{blueprintName}/coverage</c> — one row per tenant
///     known to the system, with the version installed (or null if the tenant has
///     no installation row for this blueprint).
/// </summary>
public sealed class BlueprintCoverageDto
{
    /// <summary>Blueprint name being queried (echoed for convenience).</summary>
    [JsonPropertyName("blueprintName")]
    public required string BlueprintName { get; init; }

    /// <summary>Per-tenant coverage entries — system tenant first, then children alphabetical.</summary>
    [JsonPropertyName("entries")]
    public required IReadOnlyList<BlueprintCoverageEntryDto> Entries { get; init; }
}

/// <summary>Per-tenant coverage row for <see cref="BlueprintCoverageDto"/>.</summary>
public sealed class BlueprintCoverageEntryDto
{
    /// <summary>Target tenant.</summary>
    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    /// <summary>
    ///     Installed blueprint version on this tenant, or <c>null</c> if there is no
    ///     installation row for the queried blueprint name. A null in a tenant that
    ///     should have the blueprint is the signal a dashboard surfaces as "missing".
    /// </summary>
    [JsonPropertyName("installedVersion")]
    public string? InstalledVersion { get; init; }

    /// <summary>
    ///     When the installation was last touched (UTC). Null when
    ///     <see cref="InstalledVersion"/> is null.
    /// </summary>
    [JsonPropertyName("lastUpdatedAt")]
    public DateTime? LastUpdatedAt { get; init; }
}
