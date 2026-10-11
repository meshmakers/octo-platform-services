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
    ///     System.Files descriptor: a service that resolves tenants with it never installs the model (the state of
    ///     a tenant whose System.Files is not installed yet).
    /// </summary>
    public ServiceProvider BuildProvider(bool includeSystemFiles)
    {
        var systemTenantId = SystemTenantId;
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
        services.AddSingleton<ITenantNotifications, DefaultTenantNotifications>();
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
        services.AddCkModelSystemFilesV1();
        if (includeSystemFiles)
        {
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
