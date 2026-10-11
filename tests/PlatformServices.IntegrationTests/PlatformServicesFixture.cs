using System.Collections.Concurrent;
using System.Security.Cryptography;
using Meshmakers.Octo.Backend.PlatformServices.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Files.Generated.System.Files.v1;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.Configuration.DependencyInjection;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Defaults;
using Meshmakers.Octo.Runtime.Engine.Secrets;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Xunit;

namespace Meshmakers.Octo.Backend.PlatformServices.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class PlatformServicesCollection : ICollectionFixture<PlatformServicesFixture>
{
    public const string Name = "PlatformServices";
}

/// <summary>
///     One MongoDB replica-set Testcontainer, a system tenant and a service provider that is wired like
///     platform-services' Program.cs (runtime engine, System.UI + System.Files with their service-managed
///     descriptors, the real <c>DefaultConfigurationCreatorService</c>). Every test creates its own child tenant.
///     Tests do not run in parallel (xunit.runner.json), so the log capture can be assigned per test.
/// </summary>
public sealed class PlatformServicesFixture : IAsyncLifetime
{
    private const string AdminUser = "octo-system-admin";
    private const string AdminPassword = "OctoAdmin1";
    private const string DatabasePassword = "OctoUser1";

    private MongoDbContainer? _container;
    private readonly string _id = Guid.NewGuid().ToString("N");

    public string SystemTenantId => $"octosystem{_id}";

    public ServiceProvider Provider { get; private set; } = null!;

    public ISystemContext SystemContext => Provider.GetRequiredService<ISystemContext>();

    public CapturingLoggerProvider Logs { get; } = new();

    public IDefaultConfigurationCreatorService Creator => Provider.GetRequiredService<IDefaultConfigurationCreatorService>();

    public async ValueTask InitializeAsync()
    {
        _container = new MongoDbBuilder("mongo:8.0.15")
            .WithReplicaSet()
            .WithUsername(AdminUser)
            .WithPassword(AdminPassword)
            .Build();
        await _container.StartAsync();
        // The system CK DDL of the first tenants can outlive mongod's default 60 s transaction lifetime on a loaded agent.
        await new MongoClient(_container.GetConnectionString()).GetDatabase("admin").RunCommandAsync<BsonDocument>(
            new BsonDocument { { "setParameter", 1 }, { "transactionLifetimeLimitSeconds", 900 } });

        Provider = BuildProvider(includeSystemFiles: true);

        var systemContext = SystemContext;
        await systemContext.CreateSystemTenantAsync();
    }

    /// <summary>
    ///     A service provider wired like Program.cs. <paramref name="includeSystemFiles" /> false leaves out the
    ///     System.Files model and its descriptor: the service's catalog does not know the model, so every import of it fails
    ///     (the state of a tenant whose System.Files cannot be installed).
    /// </summary>
    public ServiceProvider BuildProvider(bool includeSystemFiles, string? systemTenantId = null,
        ITenantNotifications? tenantNotifications = null)
    {
        systemTenantId ??= SystemTenantId;
        var services = new ServiceCollection();
        services.AddRuntimeEngine().AddMongoDbRuntimeRepository().AddMongoBlueprintSupport();
        services.Configure<OctoSystemConfiguration>(c =>
        {
            c.SystemTenantId = systemTenantId;
            c.SystemDatabaseName = systemTenantId;
            c.DatabaseHost = $"localhost:{_container!.GetMappedPublicPort()}";
            c.AdminUser = AdminUser;
            c.AdminUserPassword = AdminPassword;
            c.DatabaseUserPassword = DatabasePassword;
            c.UseDirectConnection = true;
        });
        services.Configure<SecretEncryptionOptions>(o =>
        {
            o.Keys["test1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            o.ActiveKeyId = "test1";
        });
        services.AddSingleton(tenantNotifications ?? new DefaultTenantNotifications());
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(Logs);
        });

        // The same registrations as Program.cs.
        services.AddTransient<IDefaultConfigurationCreatorService, DefaultConfigurationCreatorService>();
        services.AddCkModelSystemUIV2();
        services.AddSingleton<IServiceManagedCkModelDescriptor>(_ =>
            new ServiceManagedCkModelDescriptor(SystemUiCkModel.Generated.System.UI.v2.SystemUICkIds.CkModelId));
        if (includeSystemFiles)
        {
            services.AddCkModelSystemFilesV1();
            services.AddSingleton<IServiceManagedCkModelDescriptor>(_ =>
                new ServiceManagedCkModelDescriptor(SystemFilesCkIds.CkModelId));
        }

        services.AddSingleton<DefaultFilesRootService>();
        services.AddBlueprintSystemUISystemCockpitV1();
        services.AddBlueprintSystemUITenantCockpitV1();
        services.AddBlueprintSystemUIEntityFormsV1();
        services.AddBlueprintSystemTenantModeV1();

        return services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        if (Provider != null)
        {
            await Provider.DisposeAsync();
        }

        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Creates a new child tenant (a fresh database with the System CK only).</summary>
    public async Task<string> CreateTenantAsync()
    {
        var tenantId = $"t{Guid.NewGuid():N}";
        using var session = await SystemContext.GetAdminSessionAsync();
        session.StartTransaction();
        try
        {
            await SystemContext.CreateChildTenantAsync(session, tenantId, tenantId);
            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }

        return tenantId;
    }

    /// <summary>The installed CK model records named <paramref name="modelName" /> in the tenant database.</summary>
    public async Task<IReadOnlyList<BsonDocument>> GetCkModelRecordsAsync(string tenantId, string modelName)
    {
        var database = new MongoClient(_container!.GetConnectionString()).GetDatabase(tenantId);
        var all = await database.GetCollection<BsonDocument>("CkModel").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        return all.Where(d => d.ToJson().Contains($"\"{modelName}-")
                              || (d.TryGetValue("_id", out var id) && id.ToString()!.StartsWith($"{modelName}-", StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>The runtime entities of the System.Files collection of a tenant.</summary>
    public async Task<IReadOnlyList<BsonDocument>> GetFileSystemEntitiesAsync(string tenantId)
    {
        var database = new MongoClient(_container!.GetConnectionString()).GetDatabase(tenantId);
        return await database.GetCollection<BsonDocument>("RtEntity_SystemFilesFileSystemEntity")
            .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
    }
}

/// <summary>Collects the log lines of the services under test.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<(LogLevel Level, string Category, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Category, string Message)> Entries => _entries.ToArray();

    public void Clear() => _entries.Clear();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<(LogLevel, string, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (exception != null)
            {
                message += " " + exception.Message;
            }

            entries.Enqueue((logLevel, category, message));
        }
    }
}

/// <summary>
///     Fails <c>NotifyPreTenantUpdateAsync</c> calls (counted from arming) once (<see cref="FailOnCall" />) or persistently (<see cref="FailFromCall" />) (a transient error during
///     a CK model import: the engine notifies before it imports). Everything else does nothing.
/// </summary>
public sealed class FlakyTenantNotifications : ITenantNotifications
{
    private int _calls;

    /// <summary>1-based call number that throws once; 0 = never.</summary>
    public int FailOnCall { get; set; }

    /// <summary>1-based call number from which every call throws (a persistent failure); 0 = never.</summary>
    public int FailFromCall { get; set; }

    public Task NotifyPreTenantUpdateAsync(string tenantId, Guid correlationId)
    {
        if (FailOnCall == 0 && FailFromCall == 0)
        {
            return Task.CompletedTask;
        }

        var call = Interlocked.Increment(ref _calls);
        if (call == FailOnCall || (FailFromCall > 0 && call >= FailFromCall))
        {
            throw new InvalidOperationException("simulated transient failure during the CK model import");
        }

        return Task.CompletedTask;
    }

    public Task NotifyPreTenantCreateAsync(string tenantId, Guid correlationId) => Task.CompletedTask;
    public Task NotifyPosTenantCreateAsync(string tenantId, Guid correlationId) => Task.CompletedTask;
    public Task NotifyPosTenantUpdateAsync(string tenantId, Guid correlationId) => Task.CompletedTask;
    public Task NotifyPreTenantDeleteAsync(string tenantId, Guid correlationId) => Task.CompletedTask;
    public Task NotifyPosTenantDeleteAsync(string tenantId, Guid correlationId) => Task.CompletedTask;
}
