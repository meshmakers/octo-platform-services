using System.Diagnostics;
using Meshmakers.Octo.Backend.PlatformServices.Dto;
using Meshmakers.Octo.Backend.PlatformServices.Options;
using Meshmakers.Octo.Communication.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.PlatformServices.Controllers;

/// <summary>
///     Serves the public tenant-scoped <c>_configuration</c> discovery endpoint.
///     Wire-compatible replacement for the same endpoint previously hosted by
///     <c>octo-frontend-admin-panel</c>.
/// </summary>
[AllowAnonymous]
public class TenantConfigurationController(IOptions<PlatformServiceUrlsOptions> options) : ControllerBase
{
    private readonly PlatformServiceUrlsOptions _options = options.Value;

    /// <summary>
    ///     Returns the OctoMesh environment configuration for the given tenant.
    ///     The <paramref name="tenantId"/> is part of the contract but currently
    ///     ignored — all tenants in an environment receive the same configuration.
    /// </summary>
    /// <param name="tenantId">Tenant identifier — accepted for contract compatibility, value is not consulted.</param>
    [HttpGet("{tenantId}/_configuration")]
    public IActionResult GetTenantConfiguration(string tenantId)
    {
        _ = tenantId;
        var clientId = Debugger.IsAttached
            ? CommonConstants.OctoAdminPanelClientIdDebug
            : CommonConstants.OctoAdminPanelClientId;

        var dto = new TenantConfigurationDto(clientId, _options);
        return Ok(dto);
    }
}
