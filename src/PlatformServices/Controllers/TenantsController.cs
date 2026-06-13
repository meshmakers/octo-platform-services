using Meshmakers.Octo.Backend.PlatformServices.Dto;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.PlatformServices.Controllers;

/// <summary>
///     Read-only tenant observability endpoints. Operator-only; every action requires
///     the <see cref="PlatformServicesConstants.PlatformServicesAdminPolicy"/>.
/// </summary>
[Authorize(Policy = PlatformServicesConstants.PlatformServicesAdminPolicy)]
[Route("system/v1/tenants")]
[ApiController]
public class TenantsController : ControllerBase
{
    private readonly ISystemContext _systemContext;
    private readonly ITenantBlueprintInstallations _installations;
    private readonly IRuntimeRepositoryProvider _runtimeRepositoryProvider;
    private readonly string _systemTenantId;
    private readonly ILogger<TenantsController> _logger;

    /// <summary>Constructor.</summary>
    public TenantsController(
        ISystemContext systemContext,
        ITenantBlueprintInstallations installations,
        IRuntimeRepositoryProvider runtimeRepositoryProvider,
        IOptions<OctoSystemConfiguration> systemOptions,
        ILogger<TenantsController> logger)
    {
        _systemContext = systemContext;
        _installations = installations;
        _runtimeRepositoryProvider = runtimeRepositoryProvider;
        _systemTenantId = systemOptions.Value.SystemDatabaseName;
        _logger = logger;
    }

    /// <summary>
    ///     Lists every tenant (system + child) with id + DB summary.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TenantSummaryDto>>> ListAsync(
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        if (!await _systemContext.IsSystemTenantExistingAsync().ConfigureAwait(false))
        {
            _logger.LogWarning(
                "ListTenants invoked but the system tenant '{SystemTenantId}' does not yet exist; returning empty list.",
                _systemTenantId);
            return Ok(Array.Empty<TenantSummaryDto>());
        }

        var result = new List<TenantSummaryDto>
        {
            new()
            {
                TenantId = _systemTenantId,
                DatabaseName = _systemTenantId,
                IsSystemTenant = true
            }
        };

        using var adminSession = await _systemContext.GetAdminSessionAsync().ConfigureAwait(false);
        var children = await _systemContext.GetChildTenantsAsync(adminSession).ConfigureAwait(false);

        foreach (var tenant in children.Items.OrderBy(t => t.TenantId, StringComparer.Ordinal))
        {
            result.Add(new TenantSummaryDto
            {
                TenantId = tenant.TenantId,
                DatabaseName = tenant.DatabaseName,
                IsSystemTenant = false
            });
        }

        return Ok(result);
    }

    /// <summary>
    ///     Lists every blueprint currently installed on the given tenant. Wraps
    ///     <see cref="ITenantBlueprintInstallations.GetInstalledAsync"/>.
    /// </summary>
    [HttpGet("{tenantId:tenantId}/blueprints")]
    public async Task<ActionResult<IReadOnlyList<TenantBlueprintDto>>> ListBlueprintsAsync(
        string tenantId, CancellationToken cancellationToken)
    {
        if (!await TenantExistsAsync(tenantId).ConfigureAwait(false))
        {
            return NotFound();
        }

        var installations = await _installations
            .GetInstalledAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        var dtos = installations
            .OrderBy(i => i.BlueprintId.Name, StringComparer.Ordinal)
            .Select(i => new TenantBlueprintDto
            {
                BlueprintName = i.BlueprintId.Name,
                FullName = i.BlueprintId.FullName,
                Version = i.BlueprintId.Version.ToString(),
                InstalledAt = i.InstalledAt,
                LastUpdatedAt = i.LastUpdatedAt,
                IsDependency = i.IsDependency
            })
            .ToList();

        return Ok(dtos);
    }

    /// <summary>
    ///     Lists installed CK models for the given tenant by reading
    ///     <see cref="IRuntimeRepositoryProvider.GetSchemaVersionsAsync"/>. Surfaces
    ///     models that were imported directly (no blueprint wrapper) too — see concept §8.3.
    /// </summary>
    [HttpGet("{tenantId:tenantId}/ck-models")]
    public async Task<ActionResult<IReadOnlyList<TenantCkModelDto>>> ListCkModelsAsync(
        string tenantId, CancellationToken cancellationToken)
    {
        if (!await TenantExistsAsync(tenantId).ConfigureAwait(false))
        {
            return NotFound();
        }

        var schemas = await _runtimeRepositoryProvider
            .GetSchemaVersionsAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        var dtos = schemas
            .Select(kv => new TenantCkModelDto { ModelId = kv.Key, Version = kv.Value })
            .OrderBy(m => m.ModelId, StringComparer.Ordinal)
            .ToList();

        return Ok(dtos);
    }

    private async Task<bool> TenantExistsAsync(string tenantId)
    {
        if (string.Equals(tenantId, _systemTenantId, StringComparison.Ordinal))
        {
            return await _systemContext.IsSystemTenantExistingAsync().ConfigureAwait(false);
        }

        var context = await _systemContext.TryFindTenantContextAsync(tenantId).ConfigureAwait(false);
        return context != null;
    }
}
