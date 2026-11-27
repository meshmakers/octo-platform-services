using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace Meshmakers.Octo.Backend.PlatformServices.IntegrationTests;

/// <summary>
///     AB#6054: this host has no tenant gate (AB#5051), so it adds the agent-token refusal of octo-common-services on its
///     own, right after authorization and before the controllers. The refusal itself (403 AgentTokenNotAccepted for a
///     token with scope <c>octo_agent</c>) is tested in octo-common-services (<c>AgentTokenRefusalPipelineTests</c>).
/// </summary>
public class AgentTokenRefusalWiringTests
{
    [Fact]
    public async Task Program_AddsTheAgentTokenRefusal_AfterAuthorization_BeforeTheControllers()
    {
        var program = await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot(), "src", "PlatformServices", "Program.cs"),
            TestContext.Current.CancellationToken);

        var authorization = program.IndexOf("app.UseAuthorization();", StringComparison.Ordinal);
        var refusal = program.IndexOf("app.UseOctoAgentTokenRefusal();", StringComparison.Ordinal);
        var controllers = program.IndexOf("app.MapControllers();", StringComparison.Ordinal);

        authorization.Should().BePositive();
        refusal.Should().BeGreaterThan(authorization);
        controllers.Should().BeGreaterThan(refusal);
    }

    private static string RepositoryRoot([CallerFilePath] string path = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Octo.PlatformServices.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
