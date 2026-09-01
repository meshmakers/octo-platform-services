using System.Text.Json.Serialization;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.PlatformServices.Options;

namespace Meshmakers.Octo.Backend.PlatformServices.Dto;

/// <summary>
///     Tenant-scoped environment discovery payload served at <c>_configuration</c>.
///     Successor of the legacy <c>ClientDto</c> served by <c>octo-frontend-admin-panel</c>.
/// </summary>
/// <remarks>
///     The OAuth client fields the legacy DTO carried (<c>clientId</c>, <c>redirectUri</c>,
///     <c>postLogoutRedirectUri</c>, <c>scope</c>) were dropped in Phase 4: they only ever
///     described the retired admin-panel UI's own OIDC client. Every live consumer (Refinery
///     Studio, Office Integration, PowerBI / Power Query) brings its own client registration
///     from its bundled <c>config.json</c> and reads only the issuer + service URLs from this
///     payload, so the fields were vestigial.
/// </remarks>
public class TenantConfigurationDto
{
    /// <summary>Builds the DTO from the configured environment URLs.</summary>
    public TenantConfigurationDto(PlatformServiceUrlsOptions options)
    {
        Authority = NormalizeUrl(options.AuthorityUrl);
        AssetServices = NormalizeUrl(options.AssetServiceUrl);
        CommunicationServices = NormalizeUrl(options.CommunicationServiceUrl);
        ReportingServices = NormalizeUrl(options.ReportingServiceUrl);
        CrateDbAdminUrl = NormalizeUrl(options.CrateDbAdminUrl);
        GrafanaUrl = NormalizeUrl(options.GrafanaUrl);
        MeshAdapterUrl = NormalizeUrl(options.MeshAdapterUrl);
        AiServices = NormalizeUrl(options.AiServicesUrl);
        McpServices = NormalizeUrl(options.McpServiceUrl);
        BotServices = NormalizeUrl(options.BotServiceUrl);
        SystemTenantId = options.SystemTenantId;
    }

    /// <summary>
    ///     Configured URLs are trailing-slashed; an empty value is served verbatim, because it
    ///     means "service not part of this installation" (AB#4884) and consumers key on the
    ///     empty string. Plain <c>EnsureEndsWith("/")</c> would turn it into "/", which older
    ///     frontends only tolerate by accident (the auth interceptor skips both).
    /// </summary>
    private static string NormalizeUrl(string url) =>
        string.IsNullOrEmpty(url) ? string.Empty : url.EnsureEndsWith("/");

    /// <summary>Public URL of the asset repository service.</summary>
    [JsonPropertyName("assetServices")] public string AssetServices { get; set; }

    /// <summary>Public URL of the bot service.</summary>
    [JsonPropertyName("botServices")] public string BotServices { get; set; }

    /// <summary>Public URL of the communication controller service.</summary>
    [JsonPropertyName("communicationServices")] public string CommunicationServices { get; set; }

    /// <summary>Public URL of the reporting service.</summary>
    [JsonPropertyName("reportingServices")] public string ReportingServices { get; set; }

    /// <summary>OIDC issuer (identity service public URL).</summary>
    [JsonPropertyName("issuer")] public string Authority { get; set; }

    /// <summary>System tenant identifier (default <c>octosystem</c>).</summary>
    [JsonPropertyName("systemTenantId")] public string SystemTenantId { get; set; }

    /// <summary>Public URL of the CrateDB admin console.</summary>
    [JsonPropertyName("crateDbAdminUrl")] public string CrateDbAdminUrl { get; set; }

    /// <summary>Public URL of the Grafana dashboard.</summary>
    [JsonPropertyName("grafanaUrl")] public string GrafanaUrl { get; set; }

    /// <summary>Public URL of the Mesh Adapter.</summary>
    [JsonPropertyName("meshAdapterUrl")] public string MeshAdapterUrl { get; set; }

    /// <summary>Public URL of the AI adapter (octo-ai-services).</summary>
    [JsonPropertyName("aiServices")] public string AiServices { get; set; }

    /// <summary>Public URL of the MCP service (octo-mcp-service).</summary>
    [JsonPropertyName("mcpServices")] public string McpServices { get; set; }
}
