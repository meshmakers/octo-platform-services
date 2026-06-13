using FakeItEasy;
using Meshmakers.Octo.Backend.PlatformServices.Controllers;
using Meshmakers.Octo.Backend.PlatformServices.Dto;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Backend.PlatformServices.ContractTests.Controllers;

/// <summary>
///     Unit tests for the Phase 2 Step 6 observability tenants controller. Mocks the
///     <see cref="ISystemContext"/> + blueprint installation deps with FakeItEasy and
///     verifies DTO projection + ordering + 404 semantics. HTTP-layer auth is exercised
///     by ASP.NET's standard middleware and not duplicated here.
/// </summary>
public sealed class TenantsControllerTests
{
    private const string SystemTenantId = "octosystem";

    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly ITenantBlueprintInstallations _installations = A.Fake<ITenantBlueprintInstallations>();
    private readonly IRuntimeRepositoryProvider _repositoryProvider = A.Fake<IRuntimeRepositoryProvider>();
    private readonly IOctoAdminSession _session = A.Fake<IOctoAdminSession>();

    private TenantsController CreateSut()
    {
        A.CallTo(() => _systemContext.GetAdminSessionAsync()).Returns(_session);

        var options = Microsoft.Extensions.Options.Options.Create(new OctoSystemConfiguration { SystemDatabaseName = SystemTenantId });
        return new TenantsController(
            _systemContext, _installations, _repositoryProvider, options,
            NullLogger<TenantsController>.Instance);
    }

    private static IResultSet<OctoTenant> FakeChildren(params (string id, string db)[] children)
    {
        var resultSet = A.Fake<IResultSet<OctoTenant>>();
        var items = children
            .Select(c => new OctoTenant(c.id, c.db))
            .ToList();
        A.CallTo(() => resultSet.Items).Returns(items);
        return resultSet;
    }

    [Fact]
    public async Task ListAsync_NoSystemTenant_ReturnsEmpty()
    {
        A.CallTo(() => _systemContext.IsSystemTenantExistingAsync()).Returns(false);

        var result = await CreateSut().ListAsync(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsAssignableFrom<IReadOnlyList<TenantSummaryDto>>(ok.Value);
        Assert.Empty(dto);
    }

    [Fact]
    public async Task ListAsync_OrdersSystemFirstThenChildrenAlphabetically()
    {
        A.CallTo(() => _systemContext.IsSystemTenantExistingAsync()).Returns(true);
        A.CallTo(() => _systemContext.GetChildTenantsAsync(_session, A<int?>._, A<int?>._))
            .Returns(FakeChildren(("zulu", "zulu_db"), ("alpha", "alpha_db")));

        var result = await CreateSut().ListAsync(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsAssignableFrom<IReadOnlyList<TenantSummaryDto>>(ok.Value);
        Assert.Collection(dto,
            t =>
            {
                Assert.Equal(SystemTenantId, t.TenantId);
                Assert.True(t.IsSystemTenant);
            },
            t => Assert.Equal("alpha", t.TenantId),
            t => Assert.Equal("zulu", t.TenantId));
    }

    [Fact]
    public async Task ListBlueprintsAsync_UnknownTenant_Returns404()
    {
        A.CallTo(() => _systemContext.TryFindTenantContextAsync("ghost")).Returns(Task.FromResult<ITenantContext?>(null));

        var result = await CreateSut().ListBlueprintsAsync("ghost", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task ListBlueprintsAsync_OrdersByBlueprintName()
    {
        var ctx = A.Fake<ITenantContext>();
        A.CallTo(() => _systemContext.TryFindTenantContextAsync("acme")).Returns(Task.FromResult<ITenantContext?>(ctx));

        var now = DateTime.UtcNow;
        A.CallTo(() => _installations.GetInstalledAsync("acme", A<CancellationToken>._))
            .Returns(new[]
            {
                new BlueprintInstallation
                {
                    BlueprintId = new BlueprintId("System.Z-1.0.0"),
                    InstalledAt = now, LastUpdatedAt = now
                },
                new BlueprintInstallation
                {
                    BlueprintId = new BlueprintId("System.A-2.1.0"),
                    InstalledAt = now, LastUpdatedAt = now,
                    IsDependency = true
                }
            });

        var result = await CreateSut().ListBlueprintsAsync("acme", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsAssignableFrom<IReadOnlyList<TenantBlueprintDto>>(ok.Value);
        Assert.Collection(dto,
            b =>
            {
                Assert.Equal("System.A", b.BlueprintName);
                Assert.Equal("System.A-2.1.0", b.FullName);
                Assert.True(b.IsDependency);
            },
            b =>
            {
                Assert.Equal("System.Z", b.BlueprintName);
                Assert.False(b.IsDependency);
            });
    }

    [Fact]
    public async Task ListCkModelsAsync_OrdersByModelId()
    {
        var ctx = A.Fake<ITenantContext>();
        A.CallTo(() => _systemContext.TryFindTenantContextAsync("acme")).Returns(Task.FromResult<ITenantContext?>(ctx));

        A.CallTo(() => _repositoryProvider.GetSchemaVersionsAsync("acme", A<CancellationToken>._))
            .Returns(new Dictionary<string, string>
            {
                ["System.Z"] = "1.0.0",
                ["System.A"] = "3.22.0"
            });

        var result = await CreateSut().ListCkModelsAsync("acme", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsAssignableFrom<IReadOnlyList<TenantCkModelDto>>(ok.Value);
        Assert.Collection(dto,
            m =>
            {
                Assert.Equal("System.A", m.ModelId);
                Assert.Equal("3.22.0", m.Version);
            },
            m => Assert.Equal("System.Z", m.ModelId));
    }
}
