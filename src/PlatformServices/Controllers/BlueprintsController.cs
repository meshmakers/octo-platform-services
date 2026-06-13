using Meshmakers.Octo.Backend.PlatformServices.Dto;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.PlatformServices.Controllers;

/// <summary>
///     Cross-tenant blueprint observability endpoints. Operator-only; every action
///     requires the <see cref="PlatformServicesConstants.PlatformServicesAdminPolicy"/>.
/// </summary>
[Authorize(Policy = PlatformServicesConstants.PlatformServicesAdminPolicy)]
[Route("system/v1/blueprints")]
[ApiController]
public class BlueprintsController : ControllerBase
{
    private readonly ISystemContext _systemContext;
    private readonly ITenantBlueprintInstallations _installations;
    private readonly string _systemTenantId;

    /// <summary>Constructor.</summary>
    public BlueprintsController(
        ISystemContext systemContext,
        ITenantBlueprintInstallations installations,
        IOptions<OctoSystemConfiguration> systemOptions)
    {
        _systemContext = systemContext;
        _installations = installations;
        _systemTenantId = systemOptions.Value.SystemDatabaseName;
    }

    /// <summary>
    ///     Per-tenant coverage of the given blueprint name. For every tenant known to
    ///     the system, returns the installed version (or null if no install row exists).
    ///     Drives the "which tenants are behind on this blueprint?" view.
    /// </summary>
    [HttpGet("{blueprintName}/coverage")]
    public async Task<ActionResult<BlueprintCoverageDto>> GetCoverageAsync(
        string blueprintName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(blueprintName))
        {
            return BadRequest("blueprintName is required.");
        }

        var entries = new List<BlueprintCoverageEntryDto>();

        if (await _systemContext.IsSystemTenantExistingAsync().ConfigureAwait(false))
        {
            entries.Add(await BuildEntryAsync(_systemTenantId, blueprintName, cancellationToken).ConfigureAwait(false));
        }

        using (var adminSession = await _systemContext.GetAdminSessionAsync().ConfigureAwait(false))
        {
            var children = await _systemContext.GetChildTenantsAsync(adminSession).ConfigureAwait(false);
            foreach (var tenant in children.Items.OrderBy(t => t.TenantId, StringComparer.Ordinal))
            {
                entries.Add(await BuildEntryAsync(tenant.TenantId, blueprintName, cancellationToken).ConfigureAwait(false));
            }
        }

        return Ok(new BlueprintCoverageDto
        {
            BlueprintName = blueprintName,
            Entries = entries
        });
    }

    private async Task<BlueprintCoverageEntryDto> BuildEntryAsync(
        string tenantId, string blueprintName, CancellationToken cancellationToken)
    {
        var installations = await _installations
            .GetInstalledAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        var match = installations.FirstOrDefault(i =>
            string.Equals(i.BlueprintId.Name, blueprintName, StringComparison.Ordinal));

        return new BlueprintCoverageEntryDto
        {
            TenantId = tenantId,
            InstalledVersion = match?.BlueprintId.Version.ToString(),
            LastUpdatedAt = match?.LastUpdatedAt
        };
    }
}
