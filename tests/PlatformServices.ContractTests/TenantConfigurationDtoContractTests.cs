using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meshmakers.Octo.Backend.PlatformServices.Dto;
using Meshmakers.Octo.Backend.PlatformServices.Options;
using Xunit;

namespace Meshmakers.Octo.Backend.PlatformServices.ContractTests;

/// <summary>
///     Snapshot-locks the JSON shape of <see cref="TenantConfigurationDto" /> against
///     the legacy <c>ClientDto</c> served by <c>octo-frontend-admin-panel</c>.
///     Phase 1 of the platform-services initiative is contract-preserving: any
///     field rename, addition, or removal here forces every external consumer
///     (Refinery Studio, Office Integration, PowerBI, Power Query) to redeploy
///     in lockstep. If you intend such a break, bump to Phase 2 and update the
///     consumers first, then update this baseline.
/// </summary>
public class TenantConfigurationDtoContractTests
{
    /// <summary>
    ///     Exact JSON property names returned by the legacy admin-panel
    ///     <c>OidcConfigurationController</c>. Order independent.
    /// </summary>
    private static readonly string[] ExpectedJsonProperties =
    [
        "assetServices",
        "botServices",
        "communicationServices",
        "reportingServices",
        "issuer",
        "clientId",
        "redirectUri",
        "postLogoutRedirectUri",
        "scope",
        "systemTenantId",
        "crateDbAdminUrl",
        "grafanaUrl",
        "meshAdapterUrl",
        "aiServices"
    ];

    [Fact]
    public void Dto_serialises_exactly_the_legacy_fourteen_fields()
    {
        var options = NewOptions();
        var dto = new TenantConfigurationDto("test-client", options);

        var json = JsonSerializer.Serialize(dto);
        using var doc = JsonDocument.Parse(json);

        var actual = doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        var expected = ExpectedJsonProperties.OrderBy(n => n).ToArray();

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(nameof(TenantConfigurationDto.AssetServices), "assetServices")]
    [InlineData(nameof(TenantConfigurationDto.BotServices), "botServices")]
    [InlineData(nameof(TenantConfigurationDto.CommunicationServices), "communicationServices")]
    [InlineData(nameof(TenantConfigurationDto.ReportingServices), "reportingServices")]
    [InlineData(nameof(TenantConfigurationDto.Authority), "issuer")]
    [InlineData(nameof(TenantConfigurationDto.ClientId), "clientId")]
    [InlineData(nameof(TenantConfigurationDto.RedirectUri), "redirectUri")]
    [InlineData(nameof(TenantConfigurationDto.PostLogoutRedirectUri), "postLogoutRedirectUri")]
    [InlineData(nameof(TenantConfigurationDto.Scope), "scope")]
    [InlineData(nameof(TenantConfigurationDto.SystemTenantId), "systemTenantId")]
    [InlineData(nameof(TenantConfigurationDto.CrateDbAdminUrl), "crateDbAdminUrl")]
    [InlineData(nameof(TenantConfigurationDto.GrafanaUrl), "grafanaUrl")]
    [InlineData(nameof(TenantConfigurationDto.MeshAdapterUrl), "meshAdapterUrl")]
    [InlineData(nameof(TenantConfigurationDto.AiServices), "aiServices")]
    public void Property_carries_the_expected_JsonPropertyName(string clrName, string jsonName)
    {
        var prop = typeof(TenantConfigurationDto).GetProperty(clrName,
                       BindingFlags.Public | BindingFlags.Instance)
                   ?? throw new InvalidOperationException($"Property {clrName} missing");
        var attribute = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal(jsonName, attribute!.Name);
    }

    [Fact]
    public void Every_url_value_ends_with_slash()
    {
        var options = NewOptions(); // no trailing slashes on any input
        var dto = new TenantConfigurationDto("test-client", options);

        Assert.EndsWith("/", dto.AssetServices);
        Assert.EndsWith("/", dto.BotServices);
        Assert.EndsWith("/", dto.CommunicationServices);
        Assert.EndsWith("/", dto.ReportingServices);
        Assert.EndsWith("/", dto.Authority);
        Assert.EndsWith("/", dto.RedirectUri);
        Assert.EndsWith("/", dto.PostLogoutRedirectUri);
        Assert.EndsWith("/", dto.CrateDbAdminUrl);
        Assert.EndsWith("/", dto.GrafanaUrl);
        Assert.EndsWith("/", dto.MeshAdapterUrl);
        Assert.EndsWith("/", dto.AiServices);
    }

    [Fact]
    public void Scope_matches_legacy_admin_panel_default()
    {
        var options = NewOptions();
        var dto = new TenantConfigurationDto("test-client", options);

        // The legacy ClientDto built scope from
        //   ApiScopes.OctoApiFullAccess
        //   DefaultScopes.UserDefault | DefaultScopes.OfflineAccess
        // Locked against the admin-panel response observed during the
        // platform-services extraction (2026-06-12).
        Assert.Equal("openid profile email role offline_access octo_api", dto.Scope);
    }

    [Fact]
    public void ClientId_is_propagated_verbatim()
    {
        var options = NewOptions();
        var dto = new TenantConfigurationDto("some-client-id", options);

        Assert.Equal("some-client-id", dto.ClientId);
    }

    private static PlatformServiceUrlsOptions NewOptions() => new()
    {
        AssetServiceUrl = "https://assets.example.com",
        BotServiceUrl = "https://bot.example.com",
        CommunicationServiceUrl = "https://comm.example.com",
        ReportingServiceUrl = "https://reporting.example.com",
        AuthorityUrl = "https://identity.example.com",
        AdminPanelUrl = "https://adminpanel.example.com",
        SystemTenantId = "octosystem",
        GrafanaUrl = "https://grafana.example.com",
        MeshAdapterUrl = "https://adapter.example.com",
        AiServicesUrl = "https://ai.example.com",
        CrateDbAdminUrl = "https://cratedb.example.com"
    };
}
