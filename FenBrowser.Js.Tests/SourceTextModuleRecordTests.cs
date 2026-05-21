using FenBrowser.Js.Modules;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class SourceTextModuleRecordTests
{
    [Fact]
    public void GetExportedNamesIncludesLocalsAndIndirects()
    {
        var module = new SourceTextModuleRecord(
            realmId: 0,
            parsedCode: null,
            importEntries: Array.Empty<ImportEntry>(),
            localExportEntries: new[]
            {
                new ExportEntry("a", null, null, "a"),
                new ExportEntry("default", null, null, ExportEntry.DefaultLocalName),
            },
            indirectExportEntries: new[]
            {
                new ExportEntry("b", "dep", "b", null),
            },
            starExportEntries: Array.Empty<ExportEntry>());

        var names = module.GetExportedNames();

        Assert.Contains("a", names);
        Assert.Contains("default", names);
        Assert.Contains("b", names);
    }

    [Fact]
    public void GetExportedNamesUnionsStarReexportsExcludingDefault()
    {
        SourceTextModuleRecord? dep = null;
        dep = new SourceTextModuleRecord(
            realmId: 0,
            parsedCode: null,
            importEntries: Array.Empty<ImportEntry>(),
            localExportEntries: new[]
            {
                new ExportEntry("x", null, null, "x"),
                new ExportEntry("default", null, null, ExportEntry.DefaultLocalName),
            },
            indirectExportEntries: Array.Empty<ExportEntry>(),
            starExportEntries: Array.Empty<ExportEntry>());

        var module = new SourceTextModuleRecord(
            realmId: 0,
            parsedCode: null,
            importEntries: Array.Empty<ImportEntry>(),
            localExportEntries: Array.Empty<ExportEntry>(),
            indirectExportEntries: Array.Empty<ExportEntry>(),
            starExportEntries: new[]
            {
                new ExportEntry(null, "dep", ExportEntry.AllExports, null),
            },
            resolveModuleRequest: request => request == "dep" ? dep : null);

        var names = module.GetExportedNames();

        Assert.Contains("x", names);
        Assert.DoesNotContain("default", names);
    }

    [Fact]
    public void GetExportedNamesBreaksStarCycles()
    {
        SourceTextModuleRecord? a = null;
        SourceTextModuleRecord? b = null;

        a = new SourceTextModuleRecord(
            realmId: 0, parsedCode: null,
            importEntries: Array.Empty<ImportEntry>(),
            localExportEntries: new[] { new ExportEntry("fromA", null, null, "fromA") },
            indirectExportEntries: Array.Empty<ExportEntry>(),
            starExportEntries: new[] { new ExportEntry(null, "b", ExportEntry.AllExports, null) },
            resolveModuleRequest: request => request == "b" ? b : null);
        b = new SourceTextModuleRecord(
            realmId: 0, parsedCode: null,
            importEntries: Array.Empty<ImportEntry>(),
            localExportEntries: new[] { new ExportEntry("fromB", null, null, "fromB") },
            indirectExportEntries: Array.Empty<ExportEntry>(),
            starExportEntries: new[] { new ExportEntry(null, "a", ExportEntry.AllExports, null) },
            resolveModuleRequest: request => request == "a" ? a : null);

        var namesA = a.GetExportedNames();

        Assert.Contains("fromA", namesA);
        Assert.Contains("fromB", namesA);
        // No infinite loop and no duplicate from the cycle.
        Assert.Equal(namesA.Distinct().Count(), namesA.Count);
    }

    [Fact]
    public void ResolveExportResolvesLocalExportToItself()
    {
        var module = new SourceTextModuleRecord(
            realmId: 0, parsedCode: null,
            importEntries: Array.Empty<ImportEntry>(),
            localExportEntries: new[] { new ExportEntry("x", null, null, "x") },
            indirectExportEntries: Array.Empty<ExportEntry>(),
            starExportEntries: Array.Empty<ExportEntry>());

        var binding = module.ResolveExport("x");

        Assert.NotNull(binding);
        Assert.Same(module, binding!.Value.Module);
        Assert.Equal("x", binding.Value.BindingName);
    }

    [Fact]
    public void ResolveExportFollowsIndirectReexportsAcrossDependencies()
    {
        SourceTextModuleRecord? dep = null;
        dep = new SourceTextModuleRecord(
            realmId: 0, parsedCode: null,
            importEntries: Array.Empty<ImportEntry>(),
            localExportEntries: new[] { new ExportEntry("realName", null, null, "realName") },
            indirectExportEntries: Array.Empty<ExportEntry>(),
            starExportEntries: Array.Empty<ExportEntry>());

        var module = new SourceTextModuleRecord(
            realmId: 0, parsedCode: null,
            importEntries: Array.Empty<ImportEntry>(),
            localExportEntries: Array.Empty<ExportEntry>(),
            indirectExportEntries: new[] { new ExportEntry("alias", "dep", "realName", null) },
            starExportEntries: Array.Empty<ExportEntry>(),
            resolveModuleRequest: request => request == "dep" ? dep : null);

        var binding = module.ResolveExport("alias");

        Assert.NotNull(binding);
        Assert.Same(dep, binding!.Value.Module);
        Assert.Equal("realName", binding.Value.BindingName);
    }

    [Fact]
    public void ResolveExportReturnsNullForUnknownName()
    {
        var module = new SourceTextModuleRecord(
            realmId: 0, parsedCode: null,
            importEntries: Array.Empty<ImportEntry>(),
            localExportEntries: Array.Empty<ExportEntry>(),
            indirectExportEntries: Array.Empty<ExportEntry>(),
            starExportEntries: Array.Empty<ExportEntry>());

        Assert.Null(module.ResolveExport("nope"));
    }

    [Fact]
    public void ResolveExportNeverResolvesDefaultThroughStarReexport()
    {
        SourceTextModuleRecord? dep = null;
        dep = new SourceTextModuleRecord(
            realmId: 0, parsedCode: null,
            importEntries: Array.Empty<ImportEntry>(),
            localExportEntries: new[] { new ExportEntry("default", null, null, ExportEntry.DefaultLocalName) },
            indirectExportEntries: Array.Empty<ExportEntry>(),
            starExportEntries: Array.Empty<ExportEntry>());

        var module = new SourceTextModuleRecord(
            realmId: 0, parsedCode: null,
            importEntries: Array.Empty<ImportEntry>(),
            localExportEntries: Array.Empty<ExportEntry>(),
            indirectExportEntries: Array.Empty<ExportEntry>(),
            starExportEntries: new[] { new ExportEntry(null, "dep", ExportEntry.AllExports, null) },
            resolveModuleRequest: request => request == "dep" ? dep : null);

        Assert.Null(module.ResolveExport("default"));
    }

    [Fact]
    public void LinkInstallsModuleEnvironmentAndTransitionsStatus()
    {
        var module = new SourceTextModuleRecord(
            realmId: 0, parsedCode: null,
            importEntries: Array.Empty<ImportEntry>(),
            localExportEntries: Array.Empty<ExportEntry>(),
            indirectExportEntries: Array.Empty<ExportEntry>(),
            starExportEntries: Array.Empty<ExportEntry>());

        module.Link();

        Assert.NotNull(module.Environment);
        Assert.Equal(ModuleStatus.Linked, module.Status);
    }

    [Fact]
    public void EvaluateTransitionsThroughEvaluatingToEvaluated()
    {
        var module = new SourceTextModuleRecord(
            realmId: 0, parsedCode: null,
            importEntries: Array.Empty<ImportEntry>(),
            localExportEntries: Array.Empty<ExportEntry>(),
            indirectExportEntries: Array.Empty<ExportEntry>(),
            starExportEntries: Array.Empty<ExportEntry>());

        module.Link();
        var result = module.Evaluate();

        Assert.Equal(ModuleStatus.Evaluated, module.Status);
        Assert.Equal(JsValueTag.Undefined, result.Tag);
        Assert.False(module.HasEvaluationError);
    }
}
