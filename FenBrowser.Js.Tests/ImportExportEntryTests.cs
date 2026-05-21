using FenBrowser.Js.Modules;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ImportExportEntryTests
{
    [Fact]
    public void NamedImportEntryClassifiesAsRegularBinding()
    {
        var entry = new ImportEntry("mod", "y", "z");

        Assert.False(entry.IsNamespaceImport);
        Assert.False(entry.IsDefaultImport);
    }

    [Fact]
    public void NamespaceImportEntryIsRecognized()
    {
        var entry = new ImportEntry("mod", ImportEntry.NamespaceImport, "ns");

        Assert.True(entry.IsNamespaceImport);
        Assert.False(entry.IsDefaultImport);
    }

    [Fact]
    public void DefaultImportEntryIsRecognized()
    {
        var entry = new ImportEntry("mod", ImportEntry.DefaultImport, "x");

        Assert.True(entry.IsDefaultImport);
        Assert.False(entry.IsNamespaceImport);
    }

    [Fact]
    public void LocalExportEntryHasNoModuleRequest()
    {
        var entry = new ExportEntry(
            ExportName: "x",
            ModuleRequest: null,
            ImportName: null,
            LocalName: "x");

        Assert.True(entry.IsLocalExport);
        Assert.False(entry.IsReexport);
        Assert.False(entry.IsStarReexport);
        Assert.False(entry.IsNamespaceReexport);
    }

    [Fact]
    public void DefaultLocalExportUsesDefaultLocalNameSentinel()
    {
        var entry = new ExportEntry(
            ExportName: "default",
            ModuleRequest: null,
            ImportName: null,
            LocalName: ExportEntry.DefaultLocalName);

        Assert.True(entry.IsLocalExport);
        Assert.Equal("*default*", entry.LocalName);
    }

    [Fact]
    public void NamedReexportEntryRecognized()
    {
        var entry = new ExportEntry(
            ExportName: "x",
            ModuleRequest: "mod",
            ImportName: "x",
            LocalName: null);

        Assert.False(entry.IsLocalExport);
        Assert.True(entry.IsReexport);
        Assert.False(entry.IsStarReexport);
        Assert.False(entry.IsNamespaceReexport);
    }

    [Fact]
    public void StarReexportEntryRecognized()
    {
        var entry = new ExportEntry(
            ExportName: null,
            ModuleRequest: "mod",
            ImportName: ExportEntry.AllExports,
            LocalName: null);

        Assert.True(entry.IsStarReexport);
    }

    [Fact]
    public void NamespaceReexportEntryRecognized()
    {
        var entry = new ExportEntry(
            ExportName: "ns",
            ModuleRequest: "mod",
            ImportName: ExportEntry.AllButDefaultExports,
            LocalName: null);

        Assert.True(entry.IsNamespaceReexport);
        Assert.False(entry.IsStarReexport);
    }
}
