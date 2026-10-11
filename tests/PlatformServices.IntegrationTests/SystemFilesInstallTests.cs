using FluentAssertions;
using Meshmakers.Octo.Backend.PlatformServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Files.Generated.System.Files.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using Xunit;

namespace Meshmakers.Octo.Backend.PlatformServices.IntegrationTests;

/// <summary>
///     AB#6480: platform-services installs System.Files (service-managed descriptor) and creates the default root
///     "Files" in the tenant setup - for new tenants, for tenants that already have the model (installed by the
///     asset repository before), repeatedly (cold start, attach, nightly re-apply) and concurrently.
/// </summary>
[Collection(PlatformServicesCollection.Name)]
public class SystemFilesInstallTests(PlatformServicesFixture fixture)
{
    private const string FilesModel = "System.Files";

    private static bool IsFilesImport(string message) =>
        message.Contains("Importing CK Model 'System.Files", StringComparison.Ordinal);

    /// <summary>
    ///     Errors about System.Files. Not all errors: re-applying the System.TenantMode seed logs an ERROR
    ///     "'&lt;rtId&gt;' already imported" for the identical seed entities (ImportRtModelCommand, AB#6460) - that is
    ///     seed noise of the blueprint apply and independent of the model install.
    /// </summary>
    private IEnumerable<string> FilesErrors() => fixture.Logs.Entries
        .Where(e => e.Level >= LogLevel.Error
                    && (e.Message.Contains("System.Files", StringComparison.Ordinal)
                        || e.Message.Contains("default file root", StringComparison.OrdinalIgnoreCase)
                        || e.Category.Contains("DefaultFilesRootService", StringComparison.Ordinal)))
        .Select(e => e.Message);

    [Fact]
    public async Task Setup_NewTenant_InstallsSystemFilesOnceAndCreatesTheDefaultRoot()
    {
        var tenantId = await fixture.CreateTenantAsync();
        fixture.Logs.Clear();

        await fixture.Creator.SetupAsync(tenantId);

        (await fixture.GetCkModelRecordsAsync(tenantId, FilesModel)).Should().ContainSingle()
            .Which.ToJson().Should().Contain("System.Files-1.0.0");
        var roots = await fixture.GetFileSystemEntitiesAsync(tenantId);
        var root = roots.Should().ContainSingle().Subject;
        root["ckTypeId"].AsString.Should().StartWith("System.Files/FolderRoot");
        root["rtWellKnownName"].AsString.Should().Be("Files");
        root["attributes"]["name"].AsString.Should().Be("Files");

        fixture.Logs.Entries.Count(e => IsFilesImport(e.Message)).Should().Be(1, "exactly one import per tenant");
        FilesErrors().Should().BeEmpty("no error is logged for System.Files (no 'already imported')");
    }

    [Fact]
    public async Task Setup_RepeatedForTheSameTenant_IsIdempotent()
    {
        var tenantId = await fixture.CreateTenantAsync();
        await fixture.Creator.SetupAsync(tenantId);
        var rootBefore = (await fixture.GetFileSystemEntitiesAsync(tenantId)).Single();
        fixture.Logs.Clear();

        // The cold-start iteration, an attach/restore (PosCreate) and a full update (PosUpdate) all end in SetupAsync;
        // the lifecycle ones also force-re-apply System.TenantMode (RefreshTenantStateAsync).
        await fixture.Creator.SetupAsync(tenantId);
        await fixture.Creator.SetupAsync(tenantId);

        (await fixture.GetCkModelRecordsAsync(tenantId, FilesModel)).Should().ContainSingle();
        var roots = await fixture.GetFileSystemEntitiesAsync(tenantId);
        roots.Should().ContainSingle().Which["_id"].Should().Be(rootBefore["_id"], "the root is not recreated");
        fixture.Logs.Entries.Count(e => IsFilesImport(e.Message)).Should().Be(0);
        FilesErrors().Should().BeEmpty();
    }

    [Fact]
    public async Task Setup_TenantWithSystemFilesAndRootFromTheAssetRepository_IsLeftUntouched()
    {
        // State of test-2, staging-1, prod-1 and prod-2 today: the asset repository (r3.5.11) imported System.Files-1.0.0
        // and seeded the root with a random rtId.
        var tenantId = await fixture.CreateTenantAsync();
        var tenantContext = await fixture.SystemContext.FindTenantContextAsync(tenantId); // installs via the descriptor
        (await fixture.GetCkModelRecordsAsync(tenantId, FilesModel)).Should().ContainSingle();
        var rootService = fixture.Provider.GetRequiredService<DefaultFilesRootService>();
        await rootService.EnsureDefaultRootAsync(tenantContext);
        var rootBefore = (await fixture.GetFileSystemEntitiesAsync(tenantId)).Single();
        fixture.Logs.Clear();

        await fixture.Creator.SetupAsync(tenantId);

        (await fixture.GetCkModelRecordsAsync(tenantId, FilesModel)).Should().ContainSingle();
        (await fixture.GetFileSystemEntitiesAsync(tenantId)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(rootBefore, "no entity changes");
        fixture.Logs.Entries.Count(e => IsFilesImport(e.Message)).Should().Be(0, "no second import");
        FilesErrors().Should().BeEmpty();
    }

    private const string ServiceId = "Meshmakers.Octo.Backend.PlatformServices";

    /// <summary>
    ///     A service whose CK model imports fail from the second one on: per tenant resolve the descriptor imports System.UI
    ///     (call 1, passes) and then System.Files (call 2+, fails) - a persistent failure of the System.Files import.
    /// </summary>
    private ServiceProvider FailingFilesImportPod(out FlakyTenantNotifications flaky, string? systemTenantId = null)
    {
        flaky = new FlakyTenantNotifications { FailFromCall = 2 };
        return fixture.BuildProvider(includeSystemFiles: true, systemTenantId, flaky);
    }

    [Fact]
    public async Task Setup_WhenTheImportLeavesNoModel_Throws_RecordsTheRetry_AndDoesNotBlockTheOtherSteps()
    {
        // A service whose descriptor does not install System.Files stands for a failed import (System model too old,
        // ResolveFailed): the model stays missing after the tenant resolve.
        var failing = await fixture.CreateTenantAsync();
        var healthy = await fixture.CreateTenantAsync();
        await using var withoutFiles = FailingFilesImportPod(out _);
        var creator = withoutFiles.GetRequiredService<IDefaultConfigurationCreatorService>();
        // The other tenant already has the model (e.g. installed by asset-repo before).
        await (await fixture.SystemContext.FindTenantContextAsync(healthy)).ImportCkModelWithDowngradeGuardAsync(SystemFilesCkIds.CkModelId);
        fixture.Logs.Clear();

        var failure = await Assert.ThrowsAsync<SystemFilesSetupException>(() => creator.SetupAsync(failing));
        failure.Message.Should().Contain(failing).And.Contain("System.Files-1.0.0").And.Contain("System.Files could not be set up");

        // Recorded for the durable retry queue (the background loop re-runs SetupAsync).
        var store = withoutFiles.GetRequiredService<ITenantSetupRetryStore>();
        (await store.ListAsync(ServiceId, TestContext.Current.CancellationToken)).Should()
            .ContainSingle(r => r.TenantId == failing && r.AttemptCount >= 1)
            .Which.LastError.Should().Contain("System.Files");

        // The other steps ran: System.UI is installed, the TenantMode blueprint was force-refreshed (lifecycle event), no root.
        (await fixture.GetCkModelRecordsAsync(failing, "System.UI")).Should().NotBeEmpty();
        (await fixture.GetCkModelRecordsAsync(failing, FilesModel)).Should().BeEmpty();
        fixture.Logs.Entries.Should().Contain(e => e.Message.Contains("Refreshing System.TenantMode", StringComparison.Ordinal)
                                                  && e.Message.Contains(failing, StringComparison.Ordinal),
            "the failure is thrown after the TenantMode refresh, which the throw would otherwise skip");

        // Another tenant is unaffected.
        await creator.SetupAsync(healthy);
        (await fixture.GetFileSystemEntitiesAsync(healthy)).Should().ContainSingle();
    }

    [Fact]
    public async Task Setup_RetrySucceedsOnceTheModelIsResolvable_AndClearsTheRetryRecord()
    {
        var tenantId = await fixture.CreateTenantAsync();
        await using var withoutFiles = FailingFilesImportPod(out _);
        var creator = withoutFiles.GetRequiredService<IDefaultConfigurationCreatorService>();
        await Assert.ThrowsAsync<SystemFilesSetupException>(() => creator.SetupAsync(tenantId));
        var store = withoutFiles.GetRequiredService<ITenantSetupRetryStore>();
        (await store.ListAsync(ServiceId, TestContext.Current.CancellationToken)).Should().Contain(r => r.TenantId == tenantId);

        // The model becomes resolvable (here: another service installs it; in production the catalog / System model heals).
        await (await fixture.SystemContext.FindTenantContextAsync(tenantId)).ImportCkModelWithDowngradeGuardAsync(SystemFilesCkIds.CkModelId);

        await creator.SetupAsync(tenantId); // what the retry loop does

        (await fixture.GetFileSystemEntitiesAsync(tenantId)).Should().ContainSingle();
        (await store.ListAsync(ServiceId, TestContext.Current.CancellationToken)).Should().NotContain(r => r.TenantId == tenantId);
    }

    [Fact]
    public async Task Setup_FirstImportFailsTransiently_TheRetryReimportsAndHeals()
    {
        // The engine's service-managed import is guarded per process and tenant and only runs once: when it hits a
        // transient error the guard is spent and no later resolve imports again (a retry that merely re-checked would
        // see the same state until a restart). The retry therefore imports explicitly.
        var tenantId = await fixture.CreateTenantAsync();
        await using var pod = FailingFilesImportPod(out var flaky);
        var creator = pod.GetRequiredService<IDefaultConfigurationCreatorService>();

        // Attempt 1: the descriptor import and the explicit re-import both hit the transient error.
        var failure = await Assert.ThrowsAsync<SystemFilesSetupException>(() => creator.SetupAsync(tenantId));
        failure.Message.Should().Contain("re-import failed");
        (await fixture.GetCkModelRecordsAsync(tenantId, FilesModel)).Should().BeEmpty("the import failed");
        (await fixture.GetCkModelRecordsAsync(tenantId, "System.UI")).Should().NotBeEmpty("the failure hit System.Files only");

        // The error is gone; the descriptor's guard is spent for this process and tenant.
        flaky.FailFromCall = 0;
        await creator.SetupAsync(tenantId); // attempt 2 = the retry loop

        (await fixture.GetCkModelRecordsAsync(tenantId, FilesModel)).Should().ContainSingle();
        (await fixture.GetFileSystemEntitiesAsync(tenantId)).Should().ContainSingle();
        var store = pod.GetRequiredService<ITenantSetupRetryStore>();
        (await store.ListAsync(ServiceId, TestContext.Current.CancellationToken)).Should().NotContain(r => r.TenantId == tenantId);
    }

    [Fact]
    public async Task Setup_OtherFailure_IsNotRecordedInTheRetryQueue_AsBefore()
    {
        // A setup failure that is not about System.Files (here: the tenant does not exist) throws as before and is not queued;
        // only System.Files failures take part in the durable retry (AB#6480).
        var missing = $"gone{Guid.NewGuid():N}";
        var creator = fixture.Creator;

        await Assert.ThrowsAsync<TenantException>(() => creator.SetupAsync(missing));

        var store = fixture.Provider.GetRequiredService<ITenantSetupRetryStore>();
        (await store.ListAsync(ServiceId, TestContext.Current.CancellationToken)).Should().NotContain(r => r.TenantId == missing);
    }

    [Fact]
    public async Task Setup_DeletedTenant_KeepsTheNotFoundSemanticsForTheRetryLoop()
    {
        var rootService = fixture.Provider.GetRequiredService<DefaultFilesRootService>();

        var ex = await Assert.ThrowsAsync<TenantException>(
            () => rootService.EnsureInstalledWithDefaultRootAsync($"gone{Guid.NewGuid():N}"));

        ex.IsTenantNotFound.Should().BeTrue("the retry loop drops entries of tenants that no longer exist (AB#4829); a wrapper would hide it");
    }

    [Fact]
    public async Task Setup_ColdStart_FailureIsThrownAfterTheOtherSteps()
    {
        var tenantId = await fixture.CreateTenantAsync();
        await using var withoutFiles = FailingFilesImportPod(out _);
        var creator = withoutFiles.GetRequiredService<IDefaultConfigurationCreatorService>();
        creator.DeferTenantStart = true; // the cold-start iteration: no TenantMode refresh follows

        await Assert.ThrowsAsync<SystemFilesSetupException>(() => creator.SetupAsync(tenantId));

        (await fixture.GetCkModelRecordsAsync(tenantId, "System.UI")).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Setup_SystemTenantWithoutTheModel_DoesNotThrow()
    {
        // A throw would fail the unguarded system-tenant step of the host start and take every other step down.
        // Own system tenant (own database); its System.Files import fails.
        var systemTenantId = $"octosystem{Guid.NewGuid():N}";
        await using (var creating = fixture.BuildProvider(includeSystemFiles: false, systemTenantId))
        {
            await creating.GetRequiredService<ISystemContext>().CreateSystemTenantAsync();
        }

        await using var isolated = FailingFilesImportPod(out _, systemTenantId);
        var systemContext = isolated.GetRequiredService<ISystemContext>();

        await isolated.GetRequiredService<IDefaultConfigurationCreatorService>().SetupAsync(systemContext.TenantId);

        (await systemContext.IsCkModelSatisfiedAsync(SystemFilesCkIds.CkModelId)).Should().BeFalse();
    }

    [Fact]
    public async Task EnsureDefaultRoot_ConcurrentPods_LeavesExactlyOneRoot()
    {
        var tenantId = await fixture.CreateTenantAsync();
        await fixture.SystemContext.FindTenantContextAsync(tenantId); // install once
        // Every pod runs the tenant setup: separate service providers stand for separate pods.
        var pods = Enumerable.Range(0, 3).Select(_ => fixture.BuildProvider(includeSystemFiles: true)).ToList();
        try
        {
            await Task.WhenAll(pods.Select(async pod =>
            {
                var context = await pod.GetRequiredService<ISystemContext>().FindTenantContextAsync(tenantId);
                await pod.GetRequiredService<DefaultFilesRootService>().EnsureDefaultRootAsync(context);
            }));

            // A later run removes what a racing cleanup left behind.
            var later = await pods[0].GetRequiredService<ISystemContext>().FindTenantContextAsync(tenantId);
            await pods[0].GetRequiredService<DefaultFilesRootService>().EnsureDefaultRootAsync(later);

            (await fixture.GetFileSystemEntitiesAsync(tenantId)).Should().ContainSingle();
        }
        finally
        {
            foreach (var pod in pods)
            {
                await pod.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task EnsureDefaultRoot_DuplicatesLeftBehind_KeepsTheOldestAndNeverFails()
    {
        var tenantId = await fixture.CreateTenantAsync();
        var context = await fixture.SystemContext.FindTenantContextAsync(tenantId);
        var rootService = fixture.Provider.GetRequiredService<DefaultFilesRootService>();
        await rootService.EnsureDefaultRootAsync(context);
        var oldest = (await fixture.GetFileSystemEntitiesAsync(tenantId)).Single();

        // Two younger, empty duplicates (seeded by a racing pod).
        var repository = context.GetTenantRepositoryAsAdmin();
        for (var i = 0; i < 2; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            using var session = await context.GetAdminSessionAsync();
            var duplicate = await repository.CreateTransientRtEntityAsync<RtFolderRoot>();
            duplicate.Name = "Files";
            duplicate.RtWellKnownName = "Files";
            session.StartTransaction();
            await repository.InsertOneRtEntityAsync(session, duplicate);
            await session.CommitTransactionAsync();
        }

        (await fixture.GetFileSystemEntitiesAsync(tenantId)).Should().HaveCount(3);

        await rootService.EnsureDefaultRootAsync(context);

        (await fixture.GetFileSystemEntitiesAsync(tenantId)).Should().ContainSingle()
            .Which["_id"].Should().Be(oldest["_id"]);
    }

    [Fact]
    public async Task Setup_SystemTenant_AlsoGetsSystemFilesAndTheRoot()
    {
        await fixture.Creator.SetupAsync(fixture.SystemContext.TenantId);

        (await fixture.GetCkModelRecordsAsync(fixture.SystemContext.TenantId, FilesModel)).Should().ContainSingle();
        (await fixture.GetFileSystemEntitiesAsync(fixture.SystemContext.TenantId)).Should().ContainSingle();
    }
}
