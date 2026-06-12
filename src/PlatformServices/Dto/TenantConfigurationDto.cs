using System.Text.Json.Serialization;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.PlatformServices.Options;
using Meshmakers.Octo.Communication.Contracts;

namespace Meshmakers.Octo.Backend.PlatformServices.Dto;

/// <summary>
///     Wire-compatible replacement for the legacy <c>ClientDto</c> served by
///     <c>octo-frontend-admin-panel</c>. All field names and JSON property names
///     match 1:1 so existing consumers (Refinery Studio, Office Integration,
///     PowerBI, Power Query) can be cut over without code changes — only the
///     <c>adminUri</c> in their <c>config.json</c> needs to point at the new
///     service.
/// </summary>
public class TenantConfigurationDto
{
    /// <summary>Builds the DTO from the supplied OAuth client id and configured URLs.</summary>
    public TenantConfigurationDto(string clientId, PlatformServiceUrlsOptions options)
    {
        ClientId = clientId;
        Authority = options.AuthorityUrl.EnsureEndsWith("/");
        AssetServices = options.AssetServiceUrl.EnsureEndsWith("/");
        CommunicationServices = options.CommunicationServiceUrl.EnsureEndsWith("/");
        ReportingServices = options.ReportingServiceUrl.EnsureEndsWith("/");
        CrateDbAdminUrl = options.CrateDbAdminUrl.EnsureEndsWith("/");
        GrafanaUrl = options.GrafanaUrl.EnsureEndsWith("/");
        MeshAdapterUrl = options.MeshAdapterUrl.EnsureEndsWith("/");
        AiServices = options.AiServicesUrl.EnsureEndsWith("/");
        BotServices = options.BotServiceUrl.EnsureEndsWith("/");
        RedirectUri = options.AdminPanelUrl.EnsureEndsWith("/");
        PostLogoutRedirectUri = options.AdminPanelUrl.EnsureEndsWith("/");
        Scope = CommonConstants.GetScopes(
            ApiScopes.OctoApiFullAccess, null,
            DefaultScopes.UserDefault | DefaultScopes.OfflineAccess);
        SystemTenantId = options.SystemTenantId;
    }

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

    /// <summary>OAuth client id used by the consuming application.</summary>
    [JsonPropertyName("clientId")] public string ClientId { get; set; }

    /// <summary>OAuth redirect URI for the consuming application.</summary>
    [JsonPropertyName("redirectUri")] public string RedirectUri { get; set; }

    /// <summary>OAuth post-logout redirect URI for the consuming application.</summary>
    [JsonPropertyName("postLogoutRedirectUri")]
    public string PostLogoutRedirectUri { get; set; }

    /// <summary>Space-separated OAuth scope string requested by the consuming application.</summary>
    [JsonPropertyName("scope")] public string Scope { get; set; }

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
}
