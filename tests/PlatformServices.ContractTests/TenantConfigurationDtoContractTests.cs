using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meshmakers.Octo.Backend.PlatformServices.Dto;
using Meshmakers.Octo.Backend.PlatformServices.Options;
using Xunit;

namespace Meshmakers.Octo.Backend.PlatformServices.ContractTests;

/// <summary>
///     Snapshot-locks the JSON shape of <see cref="TenantConfigurationDto" />.
/// </summary>
/// <remarks>
///     <para>
///         Phase 4 (admin-panel retirement) intentionally dropped the four OAuth client
///         fields the legacy <c>ClientDto</c> carried — <c>clientId</c>, <c>redirectUri</c>,
///         <c>postLogoutRedirectUri</c>, <c>scope</c>. They only ever described the retired
///         admin-panel UI's own OIDC client; every live consumer (Refinery Studio, Office
///         Integration, PowerBI, Power Query) brings its own client registration from its
///         bundled <c>config.json</c> and reads only the issuer + service URLs from this
///         payload. The remaining ten fields are the live contract.
///     </para>
///     <para>
///         Any further field rename, addition, or removal here forces every external consumer
///         to redeploy in lockstep — treat this baseline as the source of truth and update the
///         consumers first if you intend such a break.
///     </para>
/// </remarks>
public class TenantConfigurationDtoContractTests
{
    /// <summary>
    ///     Exact JSON property names served by the <c>_configuration</c> endpoint after the
    ///     Phase 4 OAuth-field removal. Order independent. <c>mcpServices</c> was added for
    ///     the Studio Development→Swagger link (AB#4381) — an additive, backward-compatible
    ///     extension: existing consumers ignore unknown fields.
    /// </summary>
    private static readonly string[] ExpectedJsonProperties =
    [
        "assetServices",
        "botServices",
        "communicationServices",
        "reportingServices",
        "issuer",
        "systemTenantId",
        "crateDbAdminUrl",
        "grafanaUrl",
        "meshAdapterUrl",
        "aiServices",
        "mcpServices"
    ];

    [Fact]
    public void Dto_serialises_exactly_the_eleven_live_fields()
    {
        var options = NewOptions();
        var dto = new TenantConfigurationDto(options);

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
    [InlineData(nameof(TenantConfigurationDto.SystemTenantId), "systemTenantId")]
    [InlineData(nameof(TenantConfigurationDto.CrateDbAdminUrl), "crateDbAdminUrl")]
    [InlineData(nameof(TenantConfigurationDto.GrafanaUrl), "grafanaUrl")]
    [InlineData(nameof(TenantConfigurationDto.MeshAdapterUrl), "meshAdapterUrl")]
    [InlineData(nameof(TenantConfigurationDto.AiServices), "aiServices")]
    [InlineData(nameof(TenantConfigurationDto.McpServices), "mcpServices")]
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
        var dto = new TenantConfigurationDto(options);

        Assert.EndsWith("/", dto.AssetServices);
        Assert.EndsWith("/", dto.BotServices);
        Assert.EndsWith("/", dto.CommunicationServices);
        Assert.EndsWith("/", dto.ReportingServices);
        Assert.EndsWith("/", dto.Authority);
        Assert.EndsWith("/", dto.CrateDbAdminUrl);
        Assert.EndsWith("/", dto.GrafanaUrl);
        Assert.EndsWith("/", dto.MeshAdapterUrl);
        Assert.EndsWith("/", dto.AiServices);
        Assert.EndsWith("/", dto.McpServices);
    }

    private static PlatformServiceUrlsOptions NewOptions() => new()
    {
        AssetServiceUrl = "https://assets.example.com",
        BotServiceUrl = "https://bot.example.com",
        CommunicationServiceUrl = "https://comm.example.com",
        ReportingServiceUrl = "https://reporting.example.com",
        AuthorityUrl = "https://identity.example.com",
        SystemTenantId = "octosystem",
        GrafanaUrl = "https://grafana.example.com",
        MeshAdapterUrl = "https://adapter.example.com",
        AiServicesUrl = "https://ai.example.com",
        McpServiceUrl = "https://mcp.example.com",
        CrateDbAdminUrl = "https://cratedb.example.com"
    };
}
