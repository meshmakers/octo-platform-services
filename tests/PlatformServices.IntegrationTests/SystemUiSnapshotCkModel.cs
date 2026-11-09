using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;

namespace Meshmakers.Octo.Backend.PlatformServices.IntegrationTests;

/// <summary>
///     The published <c>System.UI-2.11.0</c> (the last ckLanguage 1 release) as the embedded model of a platform-services pod
///     of the previous release. Loaded from <c>testData/ck-models/</c> (provenance: README.md there), AB#8601.
/// </summary>
internal sealed class SystemUiSnapshotCkModel(ICkSerializer ckSerializer) : ICkEmbeddedModel
{
    /// <summary>The model version of the snapshot.</summary>
    public const string ModelFullName = "System.UI-2.11.0";

    private static readonly string SnapshotPath =
        Path.Combine(AppContext.BaseDirectory, "testData", "ck-models", "ck-system.ui-2.11.0.json");

    public CkModelId ModelId => new(ModelFullName);

    public string Description => "System.UI (published 2.11.0, test snapshot of the previous release)";

    public async Task<CkCompiledModelRoot> GetCompiledModelRootAsync(OperationResult operationResult)
    {
        await using var stream = File.OpenRead(SnapshotPath);
        return await ckSerializer.DeserializeCompiledModelRootAsync(stream, SnapshotPath, operationResult);
    }
}
