using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelCatalogs;
using Meshmakers.Octo.ConstructionKit.Engine.ModelCatalogs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meshmakers.Octo.Backend.PlatformServices.ContractTests.CkModels;

/// <summary>
///     AB#8601 (Feature AB#5697 F4.3, Epic AB#5584): System.UI 2.12.0 is the first ckLanguage 2 release of the model and a
///     MINOR against the published 2.11.0. The tests read the compiled model this service embeds (the one it imports into
///     every tenant on resolve) and guard the properties the Minor depends on. The SemVer verdict itself is checked by the
///     compile gate of the build (baseline = the published 2.11.0) and by <c>octo-ckc ValidateVersion</c>.
/// </summary>
public class SystemUiCkLanguage2Tests
{
    private static readonly string[] ExpectedTypes =
    [
        "Branding", "Dashboard", "DashboardWidget", "EntityForm", "Folder", "MappingCoverageConfiguration", "ProcessDiagram",
        "SymbolDefinition", "SymbolLibrary", "TreeNavigationConfiguration", "UIElement"
    ];

    private static readonly string[] ExpectedRecords =
    [
        "EntityFormColumn", "EntityFormField", "EntityFormSection", "Theme.Colors", "ThemeGradient", "ThemePalette",
        "TreeNavigationRoleConfig", "TreePerspectiveConfig"
    ];

    private static async Task<CkCompiledModelRoot> LoadEmbeddedSystemUiAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRuntimeEngine();
        services.AddCkModelSystemUIV2();
        await using var provider = services.BuildServiceProvider();

        var embedded = provider.GetServices<ICkEmbeddedModel>().Single(m => m.ModelId.Name == "System.UI");
        var operationResult = new OperationResult();
        var root = await embedded.GetCompiledModelRootAsync(operationResult);
        Assert.False(operationResult.HasErrors, string.Join(Environment.NewLine, operationResult.Messages));
        return root;
    }

    [Fact]
    public async Task EmbeddedModel_IsSystemUi_2_12_0_OnCkLanguage2_WithTheCkV2MinEngineVersion_AndV3Only()
    {
        var root = await LoadEmbeddedSystemUiAsync();

        Assert.Equal("System.UI-2.12.0", root.ModelId.ToString());
        Assert.Equal(2, root.EffectiveCkLanguage);
        // A ckLanguage 2 model always carries a minEngineVersion, so an engine without CK v2 refuses it instead of reading it
        // as a classic model; the compiler stamps it, the sources never set it. Pinned exactly: a higher value means every
        // service below it can no longer import System.UI. A change that raises it is a deliberate rollout decision and
        // updates this assertion.
        Assert.Equal("3.5.1", root.MinEngineVersion);
        Assert.True(CkCatalogLayout.RequiresV3(root));
        Assert.Equal(CkCatalogLayout.V3Root, CkCatalogLayout.GetPublishRoot(root));
    }

    /// <summary>
    ///     The dependency stays <c>System-[2.5,3.0)</c>: the System interfaces (<c>Named-1</c>, <c>Describable-1</c>,
    ///     <c>Hierarchical-1</c>) need System 2.7, and raising the floor beyond the System 2.5.0 that 2.11.0 resolved is a MAJOR
    ///     (row D1 of ck-semver-rules.md).
    /// </summary>
    [Fact]
    public async Task SystemDependency_StaysInTheRange_2_5_To_3_0()
    {
        var root = await LoadEmbeddedSystemUiAsync();

        var range = Assert.Single(root.DependencyRanges ?? []);
        Assert.Equal("System-[2.5,3.0)", range.Range.ToString());
    }

    /// <summary>
    ///     ckLanguage 2 changes the default of <c>derivable</c> from Any to Model (row T6). Without the explicit
    ///     <c>derivable: Any</c> every pre-existing type and record would be a MAJOR change (OCTO-CK200), and a model deriving
    ///     from <c>UIElement</c> or using one of the records would stop compiling (message 113).
    /// </summary>
    [Fact]
    public async Task EveryType_AndRecord_DeclaresDerivableAny_AndStaysPublic()
    {
        var root = await LoadEmbeddedSystemUiAsync();

        Assert.Equal(ExpectedTypes, (root.Types ?? []).Select(t => t.TypeId.Name).Order(StringComparer.Ordinal));
        Assert.Equal(ExpectedRecords, (root.Records ?? []).Select(r => r.RecordId.Name).Order(StringComparer.Ordinal));
        Assert.All(root.Types!, t => Assert.True(t.Derivable == CkDerivableDto.Any, $"{t.TypeId.Name}: {t.Derivable}"));
        Assert.All(root.Records!, r => Assert.True(r.Derivable == CkDerivableDto.Any, $"{r.RecordId.Name}: {r.Derivable}"));
        Assert.All(root.Types!, t => Assert.Equal(CkVisibilityDto.Public, CkModifiers.ResolveVisibility(t.Visibility)));
        Assert.All(root.Records!, r => Assert.Equal(CkVisibilityDto.Public, CkModifiers.ResolveVisibility(r.Visibility)));
    }

    /// <summary>
    ///     2.12.0 tightens nothing, implements no interface and declares no method. Every System.UI attribute is
    ///     <c>SeedOwned</c> and written by Refinery Studio (meshboards, process diagrams, symbol libraries, folders, tree
    ///     navigation, entity forms) or the blueprints through the generic API; none is <c>securitySensitive</c>, so a
    ///     tightening would be a MAJOR (row T7). Changing that later is a deliberate, versioned decision that updates this test.
    /// </summary>
    [Fact]
    public async Task NoAttributeIsTightened_NoInterfaceImplemented_NoMethodDeclared()
    {
        var root = await LoadEmbeddedSystemUiAsync();

        var assignments = (root.Types ?? []).SelectMany(t => (t.Attributes ?? []).Select(a => (Owner: t.TypeId.Name, a)))
            .Concat((root.Records ?? []).SelectMany(r => (r.Attributes ?? []).Select(a => (Owner: r.RecordId.Name, a))));
        var tightened = assignments
            .Where(x => x.a.Access is not null && x.a.Access != CkAttributeAccessDto.ReadWrite)
            .Select(x => $"{x.Owner}.{x.a.AttributeName}")
            .ToList();

        Assert.Empty(tightened);
        Assert.Empty((root.Attributes ?? []).Where(a => a.SecuritySensitive == true).Select(a => a.AttributeId.ToString()));
        Assert.Empty((root.Types ?? []).Where(t => t.Implements is { Count: > 0 }).Select(t => t.TypeId.Name));
        Assert.Empty((root.Types ?? []).Where(t => t.Methods is { Count: > 0 }).Select(t => t.TypeId.Name));
        Assert.Empty(root.Interfaces ?? []);
    }
}
