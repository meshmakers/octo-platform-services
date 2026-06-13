using FakeItEasy;
using Meshmakers.Octo.Backend.PlatformServices.Controllers;
using Meshmakers.Octo.Backend.PlatformServices.Dto;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Backend.PlatformServices.ContractTests.Controllers;

/// <summary>Unit tests for the cross-tenant blueprint coverage controller.</summary>
public sealed class BlueprintsControllerTests
{
    private const string SystemTenantId = "octosystem";

    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly ITenantBlueprintInstallations _installations = A.Fake<ITenantBlueprintInstallations>();
    private readonly IOctoAdminSession _session = A.Fake<IOctoAdminSession>();

    private BlueprintsController CreateSut()
    {
        A.CallTo(() => _systemContext.GetAdminSessionAsync()).Returns(_session);
        var options = Microsoft.Extensions.Options.Options.Create(new OctoSystemConfiguration { SystemDatabaseName = SystemTenantId });
        return new BlueprintsController(_systemContext, _installations, options);
    }

    private static IResultSet<OctoTenant> FakeChildren(params string[] childIds)
    {
        var resultSet = A.Fake<IResultSet<OctoTenant>>();
        A.CallTo(() => resultSet.Items)
            .Returns(childIds.Select(id => new OctoTenant(id, id + "_db")).ToList());
        return resultSet;
    }

    [Fact]
    public async Task GetCoverageAsync_EmptyName_ReturnsBadRequest()
    {
        var result = await CreateSut().GetCoverageAsync("   ", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetCoverageAsync_ReturnsSystemThenChildEntries_WithInstalledVersionOrNull()
    {
        const string blueprintName = "System.Communication.Release";

        A.CallTo(() => _systemContext.IsSystemTenantExistingAsync()).Returns(true);
        A.CallTo(() => _systemContext.GetChildTenantsAsync(_session, A<int?>._, A<int?>._))
            .Returns(FakeChildren("acme", "beta"));

        var now = DateTime.UtcNow;

        A.CallTo(() => _installations.GetInstalledAsync(SystemTenantId, A<CancellationToken>._))
            .Returns(new[]
            {
                new BlueprintInstallation
                {
                    BlueprintId = new BlueprintId(blueprintName + "-1.5.0"),
                    InstalledAt = now, LastUpdatedAt = now
                }
            });

        A.CallTo(() => _installations.GetInstalledAsync("acme", A<CancellationToken>._))
            .Returns(new[]
            {
                new BlueprintInstallation
                {
                    BlueprintId = new BlueprintId(blueprintName + "-1.4.0"),
                    InstalledAt = now, LastUpdatedAt = now
                }
            });

        A.CallTo(() => _installations.GetInstalledAsync("beta", A<CancellationToken>._))
            .Returns(Array.Empty<BlueprintInstallation>());

        var result = await CreateSut().GetCoverageAsync(blueprintName, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<BlueprintCoverageDto>(ok.Value);

        Assert.Equal(blueprintName, dto.BlueprintName);
        Assert.Collection(dto.Entries,
            e =>
            {
                Assert.Equal(SystemTenantId, e.TenantId);
                Assert.Equal("1.5.0", e.InstalledVersion);
            },
            e =>
            {
                Assert.Equal("acme", e.TenantId);
                Assert.Equal("1.4.0", e.InstalledVersion);
            },
            e =>
            {
                Assert.Equal("beta", e.TenantId);
                Assert.Null(e.InstalledVersion);
                Assert.Null(e.LastUpdatedAt);
            });
    }
}
