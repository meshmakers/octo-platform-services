using FakeItEasy;
using Meshmakers.Octo.Backend.PlatformServices.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Xunit;

namespace Meshmakers.Octo.Backend.PlatformServices.ContractTests;

/// <summary>
///     AB#6480: the setup-retry queue of platform-services only takes System.Files failures; every other store operation is
///     forwarded unchanged.
/// </summary>
public class SystemFilesOnlyTenantSetupRetryStoreTests
{
    private const string Service = "svc";
    private readonly ITenantSetupRetryStore _inner = A.Fake<ITenantSetupRetryStore>();
    private readonly SystemFilesOnlyTenantSetupRetryStore _store;

    public SystemFilesOnlyTenantSetupRetryStoreTests()
    {
        A.CallTo(() => _inner.ListAsync(A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<TenantSetupRetryRecord>>([]));
        _store = new SystemFilesOnlyTenantSetupRetryStore(_inner);
    }

    [Fact]
    public async Task OtherFailure_IsNotRecorded()
    {
        await _store.RecordFailureAsync(Service, "t1", "Blueprint System.UI.TenantCockpit failed", TestContext.Current.CancellationToken);

        A.CallTo(() => _inner.RecordFailureAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task FilesFailure_ByMarker_IsRecordedOnce()
    {
        SystemFilesOnlyTenantSetupRetryStore.MarkFilesFailure("t2");

        await _store.RecordFailureAsync(Service, "t2", "any text", TestContext.Current.CancellationToken);
        await _store.RecordFailureAsync(Service, "t2", "any other text", TestContext.Current.CancellationToken);

        A.CallTo(() => _inner.RecordFailureAsync(Service, "t2", "any text", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.RecordFailureAsync(Service, "t2", "any other text", A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task FilesFailure_ByMessagePrefix_IsRecorded()
    {
        var error = new SystemFilesSetupException("t3", "boom").Message;

        await _store.RecordFailureAsync(Service, "t3", error, TestContext.Current.CancellationToken);

        A.CallTo(() => _inner.RecordFailureAsync(Service, "t3", error, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task OtherFailure_OfATenantAlreadyInTheQueue_IsRecordedSoTheAttemptIsCounted()
    {
        A.CallTo(() => _inner.ListAsync(Service, A<CancellationToken>._)).Returns(Task.FromResult<IReadOnlyList<TenantSetupRetryRecord>>(
            [new TenantSetupRetryRecord { ServiceId = Service, TenantId = "t4" }]));

        await _store.RecordFailureAsync(Service, "t4", "another error", TestContext.Current.CancellationToken);

        A.CallTo(() => _inner.RecordFailureAsync(Service, "t4", "another error", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ClaimClearReleaseList_AreForwardedUnchanged()
    {
        var record = new TenantSetupRetryRecord { ServiceId = Service, TenantId = "t5" };
        A.CallTo(() => _inner.TryClaimAsync(Service, "owner", A<TimeSpan>._, A<TimeSpan>._, 10, A<CancellationToken>._))
            .Returns(Task.FromResult<TenantSetupRetryRecord?>(record));
        var ct = TestContext.Current.CancellationToken;

        var claimed = await _store.TryClaimAsync(Service, "owner", TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(60), 10, ct);
        await _store.ClearAsync(Service, "t5", ct);
        await _store.ClearAllForTenantAsync("t5", ct);
        await _store.ReleaseLeaseAsync(Service, "t5", "owner", ct);
        await _store.ListAsync(Service, ct);

        Assert.Same(record, claimed);
        A.CallTo(() => _inner.ClearAsync(Service, "t5", ct)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.ClearAllForTenantAsync("t5", ct)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.ReleaseLeaseAsync(Service, "t5", "owner", ct)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.ListAsync(Service, ct)).MustHaveHappened();
    }
}
