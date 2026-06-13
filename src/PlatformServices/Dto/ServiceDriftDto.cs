using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Backend.PlatformServices.Dto;

/// <summary>
///     Drift summary for one backend service whose CK model ships without a blueprint
///     wrapper (Identity, Bot, Report — see concept §8.3). One row per tenant in scope
///     for the service, with the installed schema version (or null when no row exists)
///     and a boolean flag the dashboard surfaces directly.
/// </summary>
public sealed class ServiceDriftDto
{
    /// <summary>Service key the caller asked for, echoed for convenience.</summary>
    [JsonPropertyName("serviceKey")]
    public required string ServiceKey { get; init; }

    /// <summary>The version every tenant should be on according to the running platform.</summary>
    [JsonPropertyName("expectedVersion")]
    public required int ExpectedVersion { get; init; }

    /// <summary>Configuration key the service uses to write its installed schema version.</summary>
    [JsonPropertyName("configKey")]
    public required string ConfigKey { get; init; }

    /// <summary>
    ///     Per-tenant rows. Single-row for services scoped to the system tenant only
    ///     (Identity); one row per tenant for everything else.
    /// </summary>
    [JsonPropertyName("entries")]
    public required IReadOnlyList<ServiceDriftEntryDto> Entries { get; init; }
}

/// <summary>Per-tenant drift row.</summary>
public sealed class ServiceDriftEntryDto
{
    /// <summary>Target tenant.</summary>
    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    /// <summary>
    ///     Installed schema version the service has written to this tenant's config row,
    ///     or <c>null</c> when no row exists yet.
    /// </summary>
    [JsonPropertyName("installedVersion")]
    public int? InstalledVersion { get; init; }

    /// <summary>
    ///     True when the installed version is null or strictly less than
    ///     <see cref="ServiceDriftDto.ExpectedVersion"/>.
    /// </summary>
    [JsonPropertyName("isDrifted")]
    public required bool IsDrifted { get; init; }
}
