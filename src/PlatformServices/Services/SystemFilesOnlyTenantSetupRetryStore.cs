using System.Collections.Concurrent;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;

namespace Meshmakers.Octo.Backend.PlatformServices.Services;

/// <summary>
///     Limits the durable setup-retry queue (AB#4690) to System.Files failures (AB#6480). The base creator records <b>every</b>
///     exception of <c>SetupTenantAsync</c> in the store it is given; handing it the engine's store as is would also retry and
///     re-record System.UI blueprint failures and every other setup error at each cold start - a behaviour change beyond Files.
///     This wrapper forwards everything unchanged (claim, release, clear, list) except <see cref="RecordFailureAsync" />:
///     that is forwarded only for a System.Files failure, or for a tenant that is already in the queue (a retry in progress -
///     the attempt must still be counted and the lease released). Other failures keep the earlier behaviour: logged, not retried.
/// </summary>
/// <remarks>
///     The store only receives the exception text, so a System.Files failure is identified by a marker the creator sets for the
///     tenant right before it throws the <see cref="SystemFilesSetupException" /> (typed), with the message prefix of that
///     exception as a fallback.
/// </remarks>
internal sealed class SystemFilesOnlyTenantSetupRetryStore(ITenantSetupRetryStore inner) : ITenantSetupRetryStore
{
    private static readonly ConcurrentDictionary<string, bool> FilesFailureMarkers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Marks the next recorded failure of the tenant as a System.Files failure (typed check).</summary>
    public static void MarkFilesFailure(string tenantId) => FilesFailureMarkers[tenantId] = true;

    /// <inheritdoc />
    public async Task RecordFailureAsync(string serviceId, string tenantId, string error,
        CancellationToken cancellationToken = default)
    {
        var marked = FilesFailureMarkers.TryRemove(tenantId, out _);
        if (marked || error.StartsWith(SystemFilesSetupException.MessagePrefix, StringComparison.Ordinal)
                   || await IsQueuedAsync(serviceId, tenantId, cancellationToken).ConfigureAwait(false))
        {
            await inner.RecordFailureAsync(serviceId, tenantId, error, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> IsQueuedAsync(string serviceId, string tenantId, CancellationToken cancellationToken) =>
        (await inner.ListAsync(serviceId, cancellationToken).ConfigureAwait(false))
        .Any(r => string.Equals(r.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public Task ClearAsync(string serviceId, string tenantId, CancellationToken cancellationToken = default) =>
        inner.ClearAsync(serviceId, tenantId, cancellationToken);

    /// <inheritdoc />
    public Task<long> ClearAllForTenantAsync(string tenantId, CancellationToken cancellationToken = default) =>
        inner.ClearAllForTenantAsync(tenantId, cancellationToken);

    /// <inheritdoc />
    public Task<TenantSetupRetryRecord?> TryClaimAsync(string serviceId, string leaseOwner, TimeSpan leaseDuration,
        TimeSpan minRetryInterval, int maxAttempts, CancellationToken cancellationToken = default) =>
        inner.TryClaimAsync(serviceId, leaseOwner, leaseDuration, minRetryInterval, maxAttempts, cancellationToken);

    /// <inheritdoc />
    public Task ReleaseLeaseAsync(string serviceId, string tenantId, string leaseOwner,
        CancellationToken cancellationToken = default) =>
        inner.ReleaseLeaseAsync(serviceId, tenantId, leaseOwner, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<TenantSetupRetryRecord>> ListAsync(string? serviceId = null,
        CancellationToken cancellationToken = default) =>
        inner.ListAsync(serviceId, cancellationToken);
}
