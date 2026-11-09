using FluentAssertions;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Xunit;

namespace Meshmakers.Octo.Backend.PlatformServices.IntegrationTests;

/// <summary>
///     AB#8601: System.UI 2.12.0 is the first ckLanguage 2 release. A tenant that a platform-services pod of the previous
///     release set up (published System.UI 2.11.0, ckLanguage 1, with the cockpit, entity-form and tenant-mode seeds) moves to
///     2.12.0 by a service restart alone: the service-managed descriptor imports the newer embedded version, no model ends in
///     <c>ResolveFailed</c>, the seeded UI entities stay, and the explicit <c>derivable: Any</c> survives the MongoDB round
///     trip (the CK cache is rebuilt from the stored model, not from the compiled file; a lost value would fall back to the
///     ckLanguage 2 default <c>Model</c> and break every model deriving from a System.UI type).
/// </summary>
[Collection(PlatformServicesCollection.Name)]
public class SystemUiCkLanguage2UpgradeTests(PlatformServicesFixture fixture)
{
    private const string PreviousVersion = "System.UI-2.11.0";
    private const string CurrentVersion = "System.UI-2.12.0";

    private static string Id(BsonDocument record) => record["_id"].ToString()!;

    private static int State(BsonDocument record) => record["modelState"].ToInt32();

    [Fact]
    public async Task TenantOfThePreviousRelease_MovesToCkLanguage2_ByAServiceRestart_WithoutResolveFailed()
    {
        var tenantId = await fixture.CreateTenantAsync();

        await using (var previousPod = fixture.BuildProvider(includeSystemFiles: true, previousSystemUi: true))
        {
            await previousPod.GetRequiredService<IDefaultConfigurationCreatorService>().SetupAsync(tenantId);
        }

        var before = await fixture.GetCkModelRecordsAsync(tenantId, "System.UI");
        before.Select(Id).Should().Contain(PreviousVersion).And.NotContain(CurrentVersion);
        var uiEntitiesBefore = await fixture.GetRtEntityIdsAsync(tenantId, "System.UI/");
        uiEntitiesBefore.Should().NotBeEmpty("the previous pod seeded the cockpit and the entity forms");

        // Service restart: a pod of this release (fresh process state) sets the tenant up, and once more (second restart).
        foreach (var restart in new[] { "first restart", "second restart" })
        {
            await using var pod = fixture.BuildProvider(includeSystemFiles: true);
            await pod.GetRequiredService<IDefaultConfigurationCreatorService>().SetupAsync(tenantId);

            var records = await fixture.GetAllCkModelRecordsAsync(tenantId);
            records.Where(r => Id(r).StartsWith("System.UI-", StringComparison.Ordinal)).Select(Id)
                .Should().Contain(CurrentVersion, restart);
            records.Where(r => State(r) == (int)ModelState.ResolveFailed).Select(Id)
                .Should().BeEmpty($"{restart}: no model is ResolveFailed (LibraryStatus)");
            State(records.Single(r => Id(r) == CurrentVersion)).Should().Be((int)ModelState.Available, restart);

            var cache = pod.GetRequiredService<ICkCacheService>();
            foreach (var typeId in new[] { "System.UI/UIElement", "System.UI/Dashboard", "System.UI/EntityForm" })
            {
                cache.GetRtCkType(tenantId, new RtCkId<CkTypeId>(typeId)).Derivable
                    .Should().Be(CkDerivableDto.Any, $"{restart}: {typeId}");
            }
        }

        (await fixture.GetRtEntityIdsAsync(tenantId, "System.UI/"))
            .Should().BeEquivalentTo(uiEntitiesBefore, "the upgrade keeps the seeded UI entities");
    }
}
