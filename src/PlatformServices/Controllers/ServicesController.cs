using Meshmakers.Octo.Backend.PlatformServices.Dto;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.PlatformServices.Controllers;

/// <summary>
///     Drift observability for backend services whose CK models ship outside the
///     blueprint engine. Concept §8.3 decision — the API knows the small set of
///     schema-version keys as hardcoded constants and reads them read-only.
/// </summary>
[Authorize(Policy = PlatformServicesConstants.PlatformServicesAdminPolicy)]
[Route("system/v1/services")]
[ApiController]
public class ServicesController : ControllerBase
{
    private readonly ISystemContext _systemContext;
    private readonly string _systemTenantId;
    private readonly ILogger<ServicesController> _logger;

    /// <summary>Constructor.</summary>
    public ServicesController(
        ISystemContext systemContext,
        IOptions<OctoSystemConfiguration> systemOptions,
        ILogger<ServicesController> logger)
    {
        _systemContext = systemContext;
        _systemTenantId = systemOptions.Value.SystemDatabaseName;
        _logger = logger;
    }

    /// <summary>
    ///     Per-tenant schema-version coverage for the given backend service. Returns one
    ///     row per tenant in scope for the service (system tenant only for Identity,
    ///     every tenant for Bot / Report) with the installed version and a drift flag.
    /// </summary>
    [HttpGet("{serviceKey}/drift")]
    public async Task<ActionResult<ServiceDriftDto>> GetDriftAsync(
        string serviceKey, CancellationToken cancellationToken)
    {
        if (!ServiceSchemaVersions.TryGet(serviceKey, out var schema))
        {
            return NotFound(new
            {
                error = "unknown_service_key",
                knownServiceKeys = ServiceSchemaVersions.KnownServiceKeys.OrderBy(k => k, StringComparer.Ordinal)
            });
        }

        var entries = new List<ServiceDriftEntryDto>();

        if (await _systemContext.IsSystemTenantExistingAsync().ConfigureAwait(false))
        {
            entries.Add(await ReadAsync(_systemContext, _systemTenantId, schema, cancellationToken).ConfigureAwait(false));
        }

        if (schema.Scope == ServiceSchemaVersions.SchemaScope.EveryTenant)
        {
            using var adminSession = await _systemContext.GetAdminSessionAsync().ConfigureAwait(false);
            var children = await _systemContext.GetChildTenantsAsync(adminSession).ConfigureAwait(false);
            foreach (var tenant in children.Items.OrderBy(t => t.TenantId, StringComparer.Ordinal))
            {
                var childContext = await _systemContext.TryFindTenantContextAsync(tenant.TenantId).ConfigureAwait(false);
                if (childContext == null)
                {
                    _logger.LogWarning(
                        "Skipping drift row for tenant '{TenantId}' — TryFindTenantContextAsync returned null.",
                        tenant.TenantId);
                    continue;
                }

                entries.Add(await ReadAsync(childContext, tenant.TenantId, schema, cancellationToken).ConfigureAwait(false));
            }
        }

        return Ok(new ServiceDriftDto
        {
            ServiceKey = schema.ServiceKey,
            ExpectedVersion = schema.ExpectedVersion,
            ConfigKey = schema.ConfigKey,
            Entries = entries
        });
    }

    private static async Task<ServiceDriftEntryDto> ReadAsync(
        ITenantContext context, string tenantId,
        ServiceSchemaVersions.ServiceSchema schema, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        using var session = await context.GetAdminSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        // GetConfigurationAsync's default fallback signals "no row written yet": we
        // pass version=-1 so we can distinguish "not installed" from "installed at v0".
        var configuration = await context
            .GetConfigurationAsync(session, schema.ConfigKey, new DefaultConfigurationVersion { Version = -1 })
            .ConfigureAwait(false);

        int? installed = configuration is { Version: >= 0 } ? configuration.Version : null;
        var isDrifted = installed is null || installed.Value < schema.ExpectedVersion;

        return new ServiceDriftEntryDto
        {
            TenantId = tenantId,
            InstalledVersion = installed,
            IsDrifted = isDrifted
        };
    }
}
