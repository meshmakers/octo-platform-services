namespace Meshmakers.Octo.Backend.PlatformServices.Options;

/// <summary>
///     Bound from <c>PlatformServices</c> appsettings section and overridable via
///     <c>OCTO_PLATFORMSERVICES__*</c> environment variables. Surfaces the
///     known service endpoints of an OctoMesh environment to external clients
///     (Refinery Studio, Office Integration, PowerBI / Power Query) through the
///     tenant-scoped <c>_configuration</c> discovery endpoint.
/// </summary>
public class PlatformServiceUrlsOptions
{
    /// <summary>Public URL of the asset repository service.</summary>
    public string AssetServiceUrl { get; set; } = "https://localhost:5001";

    /// <summary>Public URL of the bot service.</summary>
    public string BotServiceUrl { get; set; } = "https://localhost:5009";

    /// <summary>Public URL of the communication controller service.</summary>
    public string CommunicationServiceUrl { get; set; } = "https://localhost:5015";

    /// <summary>Public URL of the reporting service.</summary>
    public string ReportingServiceUrl { get; set; } = "https://localhost:5007";

    /// <summary>Public URL of the identity service (OIDC issuer).</summary>
    public string AuthorityUrl { get; set; } = "https://localhost:5003";

    /// <summary>
    ///     URL of the legacy Admin Panel host. Surfaced as <c>redirectUri</c> /
    ///     <c>postLogoutRedirectUri</c> of the configuration DTO so the existing
    ///     OAuth client (<c>OctoAdminPanelClient</c>) remains usable for clients
    ///     that still authenticate against it. Will be retired together with the
    ///     Admin Panel itself (Phase 4 of the platform-services initiative).
    /// </summary>
    public string AdminPanelUrl { get; set; } = "https://localhost:5005";

    /// <summary>System tenant identifier (default <c>octosystem</c>).</summary>
    public string SystemTenantId { get; set; } = "octosystem";

    /// <summary>Public URL of the Grafana dashboard.</summary>
    public string GrafanaUrl { get; set; } = "http://localhost:3000";

    /// <summary>Public URL of the Mesh Adapter.</summary>
    public string MeshAdapterUrl { get; set; } = "https://localhost:5020";

    /// <summary>Public URL of the AI adapter (octo-ai-services).</summary>
    public string AiServicesUrl { get; set; } = "https://localhost:5019";

    /// <summary>Public URL of the CrateDB admin console.</summary>
    public string CrateDbAdminUrl { get; set; } = "http://localhost:4201";
}
