using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.PlatformServices.Options;
using Meshmakers.Octo.Services.Swagger.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.PlatformServices.Configuration;

/// <summary>
///     Feeds the Swagger UI's OAuth authority from the platform-services options so the
///     authorization-code flow targets the same identity service the JWT bearer handler
///     validates against. Mirrors the octo-mcp-service pattern (AB#4388).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureOctoOpenApiOptions(IOptions<PlatformServiceUrlsOptions> platformOptions)
    : IConfigureNamedOptions<OctoOpenApiOptions>
{
    public void Configure(OctoOpenApiOptions options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    public void Configure(string? name, OctoOpenApiOptions options)
    {
        options.AuthorityUrl = platformOptions.Value.AuthorityUrl.EnsureEndsWith("/");
    }
}
