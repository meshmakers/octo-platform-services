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

    /// <summary>
    ///     Prefix for the OctoMesh installation instance — forwarded to the distribution
    ///     event hub so platform-services' tenant-event consumer subscribes to the same
    ///     instance-scoped queues as the rest of the services.
    /// </summary>
    public string? InstancePrefix { get; set; }

    /// <summary>RabbitMQ broker host name. Required since platform-services owns the
    ///     System.UI service-managed blueprints and must consume tenant lifecycle events
    ///     (<c>PosCreateTenant</c> / <c>PosUpdateTenant</c>) to seed them.</summary>
    public string BrokerHost { get; set; } = "localhost";

    /// <summary>RabbitMQ broker username.</summary>
    public string? BrokerUser { get; set; } = "guest";

    /// <summary>RabbitMQ broker password.</summary>
    public string? BrokerPassword { get; set; } = "guest";
}
