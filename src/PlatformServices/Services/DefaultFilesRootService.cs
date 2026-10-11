using System.Text.RegularExpressions;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Files.Generated.System.Files.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.PlatformServices.Services;

/// <summary>
///     Ensures the default root <c>Files</c> of the platform file system (System.Files, AB#6171 D8 / AB#6480)
///     in a tenant. Moved here from the asset repository together with the System.Files install: platform-services
///     installs the model (service-managed descriptor) and creates the root right after, in
///     <see cref="DefaultConfigurationCreatorService.SetupTenantAsync" /> (tenant create, attach, restore, full
///     update, cold start).
/// </summary>
/// <remarks>
///     <para>
///         The root is a plain <c>System.Files/FolderRoot</c> named <c>Files</c> with the well-known name
///         <c>Files</c>; there is no protection flag on the entity (the asset repository's mutation guard protects
///         roots by their type). Identical to what the asset repository created at its tenant start before.
///     </para>
///     <para>
///         Idempotent: one indexed query when the root exists (matched case-insensitively on the well-known name,
///         as the file API resolves roots). Never throws - a tenant whose System model is too old for System.Files
///         (the install failed) logs a warning and is retried by the next lifecycle event or start.
///     </para>
///     <para>
///         Every pod runs the tenant setup (broadcast events), so two pods can seed <c>Files</c> at the same time
///         (no unique index on well-known names). The root wins that the file API answers (exact-case spelling first,
///         then the oldest - the order of the asset repository's <c>FindRootAsync</c>); empty others are erased, a duplicate with content is kept and logged.
///     </para>
/// </remarks>
internal sealed class DefaultFilesRootService(ISystemContext systemContext, ILogger<DefaultFilesRootService> logger)
{
    /// <summary>Display name of the default root.</summary>
    internal const string DefaultRootName = "Files";

    /// <summary>Well-known name of the default root.</summary>
    internal const string DefaultRootWellKnownName = "Files";

    private static readonly RtCkId<CkTypeId> FolderRootType = new("System.Files/FolderRoot");
    private static readonly RtCkId<CkAssociationRoleId> ParentChildRole = new("System/ParentChild");

    private static readonly RtCkId<CkTypeId>[] ContainerChildTypes =
        [new("System.Files/Folder"), new("System.Files/FileSystemItem")];

    /// <summary>
    ///     <see cref="EnsureDefaultRootAsync(ITenantContext)" /> for a tenant id. Resolving the tenant through the
    ///     system context triggers the service-managed System.Files install (once per process and tenant).
    ///     Never throws.
    /// </summary>
    public async Task<bool> EnsureDefaultRootAsync(string tenantId)
    {
        try
        {
            return await EnsureDefaultRootAsync(await systemContext.FindTenantContextAsync(tenantId).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Default file root 'Files': tenant '{TenantId}' not resolvable", tenantId);
            return false;
        }
    }

    /// <summary>
    ///     Creates the root <c>Files</c> when it is missing. Never throws; answers whether the root exists.
    /// </summary>
    public async Task<bool> EnsureDefaultRootAsync(ITenantContext tenantContext)
    {
        try
        {
            var repository = tenantContext.GetTenantRepositoryAsAdmin();
            using var session = await tenantContext.GetAdminSessionAsync().ConfigureAwait(false);
            var existing = await FindRootsAsync(repository, session).ConfigureAwait(false);
            if (existing.Count > 1)
            {
                // Left over from a concurrent seed whose cleanup did not finish (or two pods of different versions).
                await RemoveConcurrentDuplicatesAsync(repository, tenantContext).ConfigureAwait(false);
            }

            if (existing.Count > 0)
            {
                return true;
            }

            var root = await repository.CreateTransientRtEntityAsync<RtFolderRoot>().ConfigureAwait(false);
            root.Name = DefaultRootName;
            root.RtWellKnownName = DefaultRootWellKnownName;
            session.StartTransaction();
            await repository.InsertOneRtEntityAsync(session, root).ConfigureAwait(false);
            await session.CommitTransactionAsync().ConfigureAwait(false);
            logger.LogInformation("Created the default file root 'Files' for tenant '{TenantId}'",
                tenantContext.TenantId);

            await RemoveConcurrentDuplicatesAsync(repository, tenantContext).ConfigureAwait(false);
            return true;
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "Default file root 'Files' could not be ensured for tenant '{TenantId}' (is System.Files installed?); retried at the next tenant setup",
                tenantContext.TenantId);
            return false;
        }
    }

    private static async Task<IReadOnlyList<RtEntity>> FindRootsAsync(ITenantRepository repository,
        IOctoSession session)
    {
        var options = RtEntityQueryOptions.Create();
        options.FieldMatchRegex(nameof(RtEntity.RtWellKnownName), $"(?i)^{Regex.Escape(DefaultRootWellKnownName)}$");
        var result = await repository.GetRtEntitiesByTypeAsync(session, FolderRootType, options).ConfigureAwait(false);
        // Deterministic when duplicates exist, and the same order as the asset repository's FindRootAsync (the root the
        // file API answers must be the one that is kept): exact-case spelling first, then the oldest.
        return result.Items
            .OrderByDescending(r => string.Equals(r.RtWellKnownName, DefaultRootWellKnownName, StringComparison.Ordinal))
            .ThenBy(r => r.RtCreationDateTime ?? DateTime.MaxValue)
            .ThenBy(r => r.RtId.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    private async Task RemoveConcurrentDuplicatesAsync(ITenantRepository repository, ITenantContext tenantContext)
    {
        List<RtEntity> duplicates;
        using (var querySession = await tenantContext.GetAdminSessionAsync().ConfigureAwait(false))
        {
            duplicates = (await FindRootsAsync(repository, querySession).ConfigureAwait(false)).Skip(1).ToList();
        }

        foreach (var duplicate in duplicates)
        {
            // A session of its own per duplicate: after a write conflict the previous one cannot start a new transaction.
            using var session = await tenantContext.GetAdminSessionAsync().ConfigureAwait(false);
            var id = new RtEntityId(FolderRootType, duplicate.RtId);
            var children = await repository.GetRtAssociationTargetsAsync(session, [id.RtId], id.CkTypeId,
                    ParentChildRole, ContainerChildTypes, GraphDirections.Inbound, null, RtEntityQueryOptions.Create())
                .ConfigureAwait(false);
            if (children.TryGetValue(id, out var content) && content.Items.Any())
            {
                logger.LogWarning("Duplicate default file root '{RtId}' in tenant '{TenantId}' has content; kept",
                    duplicate.RtId, tenantContext.TenantId);
                continue;
            }

            try
            {
                var result = new OperationResult();
                session.StartTransaction();
                await repository.ApplyChangesAsync(session, [EntityUpdateInfo<RtEntity>.CreateDelete(id)],
                    DeleteOptions.Erase, result).ConfigureAwait(false);
                await session.CommitTransactionAsync().ConfigureAwait(false);
                logger.LogInformation("Removed duplicate default file root '{RtId}' in tenant '{TenantId}'",
                    duplicate.RtId, tenantContext.TenantId);
            }
            catch (Exception e)
            {
                // A pod that raced us deleted it first (write conflict) - the root exists either way, and the next
                // setup finds the leftover. Must not fail the ensure.
                logger.LogWarning(e, "Duplicate default file root '{RtId}' in tenant '{TenantId}' could not be removed",
                    duplicate.RtId, tenantContext.TenantId);
            }
        }
    }
}
