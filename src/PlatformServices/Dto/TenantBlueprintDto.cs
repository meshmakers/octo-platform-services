using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Backend.PlatformServices.Dto;

/// <summary>
///     Projection of a <c>BlueprintInstallation</c> row for a single tenant. The fields
///     are the minimum a drift dashboard needs to ask "what is installed where, at what
///     version, when did it last change."
/// </summary>
public sealed class TenantBlueprintDto
{
    /// <summary>Blueprint name (e.g. <c>System.Communication.Release</c>).</summary>
    [JsonPropertyName("blueprintName")]
    public required string BlueprintName { get; init; }

    /// <summary>Fully-qualified blueprint id including version (e.g. <c>System.Communication.Release-1.5.0</c>).</summary>
    [JsonPropertyName("fullName")]
    public required string FullName { get; init; }

    /// <summary>SemVer string of the installed blueprint version.</summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>When the blueprint was first applied to the tenant (UTC).</summary>
    [JsonPropertyName("installedAt")]
    public required DateTime InstalledAt { get; init; }

    /// <summary>When the installation was last touched (Apply / ReApply / Update) (UTC).</summary>
    [JsonPropertyName("lastUpdatedAt")]
    public required DateTime LastUpdatedAt { get; init; }

    /// <summary>
    ///     True when the installation was created as a transitive dependency of another
    ///     blueprint rather than a direct install. Useful so the dashboard can collapse
    ///     dependency rows by default.
    /// </summary>
    [JsonPropertyName("isDependency")]
    public required bool IsDependency { get; init; }
}
