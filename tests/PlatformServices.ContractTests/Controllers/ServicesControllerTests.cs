using FakeItEasy;
using Meshmakers.Octo.Backend.PlatformServices.Controllers;
using Meshmakers.Octo.Backend.PlatformServices.Dto;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Backend.PlatformServices.ContractTests.Controllers;

/// <summary>Unit tests for the service drift controller (Phase 2 Step 6).</summary>
public sealed class ServicesControllerTests
{
    private const string SystemTenantId = "octosystem";

    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly IOctoAdminSession _adminSession = A.Fake<IOctoAdminSession>();

    private ServicesController CreateSut()
    {
        A.CallTo(() => _systemContext.GetAdminSessionAsync()).Returns(_adminSession);
        var options = Microsoft.Extensions.Options.Options.Create(new OctoSystemConfiguration { SystemDatabaseName = SystemTenantId });
        return new ServicesController(_systemContext, options, NullLogger<ServicesController>.Instance);
    }

    private static void StubTenantConfig(ITenantContext context, string configKey, int? version)
    {
        A.CallTo(() => context.GetAdminSessionAsync()).Returns(A.Fake<IOctoAdminSession>());

        var fallbackResponse = version.HasValue
            ? new DefaultConfigurationVersion { Version = version.Value }
            : new DefaultConfigurationVersion { Version = -1 };

        A.CallTo(() => context.GetConfigurationAsync(
                A<IOctoAdminSession>._,
                configKey,
                A<DefaultConfigurationVersion?>._))
            .Returns(fallbackResponse);
    }

    private static IResultSet<OctoTenant> FakeChildren(params string[] ids)
    {
        var resultSet = A.Fake<IResultSet<OctoTenant>>();
        A.CallTo(() => resultSet.Items).Returns(ids.Select(id => new OctoTenant(id, id + "_db")).ToList());
        return resultSet;
    }

    [Fact]
    public async Task GetDriftAsync_UnknownServiceKey_Returns404WithKnownKeys()
    {
        var result = await CreateSut().GetDriftAsync("nope", CancellationToken.None);
        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.NotNull(notFound.Value);
    }

    [Fact]
    public async Task GetDriftAsync_SystemTenantScopeService_ReturnsOnlySystemRow()
    {
        // Identity is SystemTenant-scoped per ServiceSchemaVersions.
        A.CallTo(() => _systemContext.IsSystemTenantExistingAsync()).Returns(true);
        StubTenantConfig(_systemContext, "IdentityService", 17);

        var result = await CreateSut().GetDriftAsync("identity", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ServiceDriftDto>(ok.Value);

        Assert.Equal("identity", dto.ServiceKey);
        Assert.Equal(17, dto.ExpectedVersion);
        Assert.Equal("IdentityService", dto.ConfigKey);

        var entry = Assert.Single(dto.Entries);
        Assert.Equal(SystemTenantId, entry.TenantId);
        Assert.Equal(17, entry.InstalledVersion);
        Assert.False(entry.IsDrifted);
    }

    [Fact]
    public async Task GetDriftAsync_EveryTenantScopeService_IteratesChildrenAndFlagsDrift()
    {
        // Bot is EveryTenant-scoped. Expected version is 2.
        A.CallTo(() => _systemContext.IsSystemTenantExistingAsync()).Returns(true);
        StubTenantConfig(_systemContext, "BotServicesIdentityData", 2);

        var acmeContext = A.Fake<ITenantContext>();
        var betaContext = A.Fake<ITenantContext>();
        StubTenantConfig(acmeContext, "BotServicesIdentityData", 1);   // behind → drift
        StubTenantConfig(betaContext, "BotServicesIdentityData", null); // never installed → drift

        A.CallTo(() => _systemContext.GetChildTenantsAsync(_adminSession, A<int?>._, A<int?>._))
            .Returns(FakeChildren("acme", "beta"));
        A.CallTo(() => _systemContext.TryFindTenantContextAsync("acme")).Returns(Task.FromResult<ITenantContext?>(acmeContext));
        A.CallTo(() => _systemContext.TryFindTenantContextAsync("beta")).Returns(Task.FromResult<ITenantContext?>(betaContext));

        var result = await CreateSut().GetDriftAsync("bot", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ServiceDriftDto>(ok.Value);

        Assert.Equal("bot", dto.ServiceKey);
        Assert.Collection(dto.Entries,
            e =>
            {
                Assert.Equal(SystemTenantId, e.TenantId);
                Assert.Equal(2, e.InstalledVersion);
                Assert.False(e.IsDrifted);
            },
            e =>
            {
                Assert.Equal("acme", e.TenantId);
                Assert.Equal(1, e.InstalledVersion);
                Assert.True(e.IsDrifted);
            },
            e =>
            {
                Assert.Equal("beta", e.TenantId);
                Assert.Null(e.InstalledVersion);
                Assert.True(e.IsDrifted);
            });
    }

    [Fact]
    public void ServiceSchemaVersions_KnownKeys_ContainsTheCoreThree()
    {
        var keys = ServiceSchemaVersions.KnownServiceKeys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "bot", "identity", "report" }, keys);
    }
}
