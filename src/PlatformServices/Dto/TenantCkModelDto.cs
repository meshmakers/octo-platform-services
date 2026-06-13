using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Backend.PlatformServices.Dto;

/// <summary>
///     Projection of one CK model installed on a tenant. Mirrors the
///     <see cref="Octo.Runtime.Contracts.IRuntimeRepositoryProvider.GetSchemaVersionsAsync"/>
///     return shape — model id + version string — so the dashboard can surface CK models
///     that have no blueprint wrapper too (Identity, Bot, Report, MACO; see concept §8.3).
/// </summary>
public sealed class TenantCkModelDto
{
    /// <summary>CK model id (e.g. <c>System.Communication</c>).</summary>
    [JsonPropertyName("modelId")]
    public required string ModelId { get; init; }

    /// <summary>Installed model version string (SemVer-ish, e.g. <c>3.22.0</c>).</summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }
}
