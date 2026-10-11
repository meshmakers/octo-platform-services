using FluentAssertions;
using Meshmakers.Octo.Backend.PlatformServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Files.Generated.System.Files.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
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
        (await rootService.EnsureDefaultRootAsync(tenantContext)).Should().BeTrue();
        var rootBefore = (await fixture.GetFileSystemEntitiesAsync(tenantId)).Single();
        fixture.Logs.Clear();

        await fixture.Creator.SetupAsync(tenantId);

        (await fixture.GetCkModelRecordsAsync(tenantId, FilesModel)).Should().ContainSingle();
        (await fixture.GetFileSystemEntitiesAsync(tenantId)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(rootBefore, "no entity changes");
        fixture.Logs.Entries.Count(e => IsFilesImport(e.Message)).Should().Be(0, "no second import");
        FilesErrors().Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureDefaultRoot_WhileSystemFilesIsMissing_ReturnsFalseAndTheSetupLaterCreatesTheRoot()
    {
        var tenantId = await fixture.CreateTenantAsync();
        await using (var withoutFiles = fixture.BuildProvider(includeSystemFiles: false))
        {
            var context = await withoutFiles.GetRequiredService<ISystemContext>().FindTenantContextAsync(tenantId);
            (await withoutFiles.GetRequiredService<DefaultFilesRootService>().EnsureDefaultRootAsync(context))
                .Should().BeFalse("the model is not installed: log and report, never throw");
            (await fixture.GetCkModelRecordsAsync(tenantId, FilesModel)).Should().BeEmpty();
        }

        await fixture.Creator.SetupAsync(tenantId);

        (await fixture.GetCkModelRecordsAsync(tenantId, FilesModel)).Should().ContainSingle();
        (await fixture.GetFileSystemEntitiesAsync(tenantId)).Should().ContainSingle();
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
            (await pods[0].GetRequiredService<DefaultFilesRootService>().EnsureDefaultRootAsync(later)).Should().BeTrue();

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

        (await rootService.EnsureDefaultRootAsync(context)).Should().BeTrue();

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
