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
///         as the file API resolves roots). A failed install or root creation is fatal for the tenant's setup: it
///         throws a <see cref="SystemFilesSetupException" /> (model, tenant, cause) so that the durable setup-retry
///         queue retries it; the caller isolates the other setup steps from that failure.
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
    ///     Resolves the tenant (which triggers the service-managed System.Files install, once per process and
    ///     tenant), verifies the model is usable and ensures the default root. Throws
    ///     <see cref="SystemFilesSetupException" /> when the model is not installed or the root cannot be created.
    /// </summary>
    public async Task EnsureInstalledWithDefaultRootAsync(string tenantId)
    {
        ITenantContext tenantContext;
        try
        {
            tenantContext = await systemContext.FindTenantContextAsync(tenantId).ConfigureAwait(false);
        }
        catch (TenantException e) when (e.IsTenantNotFound)
        {
            // Not wrapped: the setup-retry loop drops an entry for a tenant that no longer exists (AB#4829).
            throw;
        }
        catch (Exception e)
        {
            throw new SystemFilesSetupException(tenantId,
                $"the tenant could not be resolved, so the service-managed import did not run ({e.Message})", e);
        }

        await EnsureDefaultRootAsync(tenantContext).ConfigureAwait(false);
    }

    /// <summary>
    ///     Creates the root <c>Files</c> when it is missing. Throws <see cref="SystemFilesSetupException" /> when
    ///     System.Files is not installed (with the likely cause) or the root cannot be created.
    /// </summary>
    public async Task EnsureDefaultRootAsync(ITenantContext tenantContext)
    {
        await EnsureModelInstalledAsync(tenantContext).ConfigureAwait(false);

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
                return;
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
        }
        catch (TenantException e) when (e.IsTenantNotFound)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new SystemFilesSetupException(tenantContext.TenantId,
                $"the default root '{DefaultRootName}' could not be created ({e.Message})", e);
        }
    }

    private async Task EnsureModelInstalledAsync(ITenantContext tenantContext)
    {
        if (await IsInstalledAsync(tenantContext).ConfigureAwait(false))
        {
            return;
        }

        // The engine's service-managed import runs once per process and tenant and only logs a failure (a transient
        // error stays unnoticed until a restart), so a retry that merely re-checked would see the same state.
        // Attempt the import again here - the call has no per-process guard and the downgrade guard keeps it safe.
        logger.LogWarning("System.Files is not installed in tenant '{TenantId}' after the tenant resolve; importing it again",
            tenantContext.TenantId);
        Exception? importError = null;
        try
        {
            await tenantContext.ImportCkModelWithDowngradeGuardAsync(SystemFilesCkIds.CkModelId).ConfigureAwait(false);
        }
        catch (TenantException e) when (e.IsTenantNotFound)
        {
            throw;
        }
        catch (Exception e)
        {
            importError = e;
        }

        if (await IsInstalledAsync(tenantContext).ConfigureAwait(false))
        {
            logger.LogInformation("System.Files imported into tenant '{TenantId}' by the retry", tenantContext.TenantId);
            return;
        }

        // Name the most likely cause so the retry record is actionable.
        var systemFloor = new CkModelId(SystemFloorModelId);
        var cause = await tenantContext.IsCkModelSatisfiedAsync(systemFloor).ConfigureAwait(false)
            ? "the import did not leave an Available model (see the engine log of the import for the cause, e.g. ResolveFailed)"
            : $"the tenant's System model is older than {SystemFloorModelId} (System.Files depends on System-[2.5,3.0))";
        throw new SystemFilesSetupException(tenantContext.TenantId,
            importError == null ? cause : $"{cause}; the re-import failed: {importError.Message}", importError);
    }

    private static async Task<bool> IsInstalledAsync(ITenantContext tenantContext)
    {
        try
        {
            return await tenantContext.IsCkModelSatisfiedAsync(SystemFilesCkIds.CkModelId).ConfigureAwait(false);
        }
        catch (TenantException e) when (e.IsTenantNotFound)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new SystemFilesSetupException(tenantContext.TenantId,
                $"the installed state of the model could not be read ({e.Message})", e);
        }
    }

    /// <summary>Lowest System version System.Files accepts (<c>System-[2.5,3.0)</c>).</summary>
    private const string SystemFloorModelId = "System-2.5.0";

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

/// <summary>
///     System.Files could not be installed or its default root could not be created for a tenant. Fatal for that
///     tenant's setup: the durable setup-retry queue retries it (constant 60 s interval, 10 attempts, then it waits for
///     an operator, a tenant event or a restart).
/// </summary>
internal sealed class SystemFilesSetupException : Exception
{
    /// <summary>Start of every message; the retry store wrapper uses it as the fallback marker of a System.Files failure.</summary>
    public const string MessagePrefix = "System.Files could not be set up for tenant";

    public SystemFilesSetupException(string tenantId, string cause, Exception? inner = null)
        : base($"{MessagePrefix} '{tenantId}': {cause}. Model: {SystemFilesCkIds.CkModelId}.",
            inner)
    {
        TenantId = tenantId;
    }

    public string TenantId { get; }
}
