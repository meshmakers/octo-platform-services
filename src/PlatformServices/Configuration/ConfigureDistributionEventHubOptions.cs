using Meshmakers.Octo.Backend.PlatformServices.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Configuration.Options;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.PlatformServices.Configuration;

/// <summary>
///     Binds the distribution event hub (RabbitMQ broker + repository) from
///     <see cref="PlatformServiceUrlsOptions"/> and <see cref="OctoSystemConfiguration"/>.
///     platform-services needs the hub since it owns the System.UI service-managed blueprints
///     and consumes <c>PosCreateTenant</c> / <c>PosUpdateTenant</c> events to seed them.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureDistributionEventHubOptions(
    IOptions<PlatformServiceUrlsOptions> platformServicesOptions,
    IOptions<OctoSystemConfiguration> octoSystemConfiguration)
    : IConfigureNamedOptions<DistributionEventHubOptions>
{
    public void Configure(DistributionEventHubOptions options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    public void Configure(string? name, DistributionEventHubOptions options)
    {
        options.InstancePrefix = platformServicesOptions.Value.InstancePrefix;
        options.BrokerHost = platformServicesOptions.Value.BrokerHost;
        options.BrokerUser = platformServicesOptions.Value.BrokerUser;
        options.BrokerPassword = platformServicesOptions.Value.BrokerPassword;
        options.RepositoryHost = octoSystemConfiguration.Value.DatabaseHost;
        options.RepositoryUser = octoSystemConfiguration.Value.DatabaseUser;
        options.RepositoryPassword = octoSystemConfiguration.Value.DatabaseUserPassword;
        options.DatabaseAuthenticationSource = octoSystemConfiguration.Value.AuthenticationDatabaseName;
    }
}
