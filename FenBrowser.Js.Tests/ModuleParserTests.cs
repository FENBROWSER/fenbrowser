using System.Collections.Generic;
using System.Linq;
using FenBrowser.Js.Ast;
using FenBrowser.Js.Modules;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ModuleParserTests
{
    private static ImportDeclarationNode ParseImport(string source)
    {
        var program = JsParser.ParseModule(new SourceText(source));
        return Assert.IsType<ImportDeclarationNode>(program.Body[0]);
    }

    private static ExportDeclarationNode ParseExport(string source)
    {
        var program = JsParser.ParseModule(new SourceText(source));
        return Assert.IsType<ExportDeclarationNode>(program.Body[0]);
    }

    [Fact]
    public void SideEffectImportProducesNoEntries()
    {
        var decl = ParseImport("import 'side-effect';");
        Assert.Equal("side-effect", decl.ModuleRequest);
        Assert.Empty(decl.Entries);
    }

    [Fact]
    public void DefaultImportProducesOneDefaultEntry()
    {
        var decl = ParseImport("import x from 'mod';");
        var entry = Assert.Single(decl.Entries);
        Assert.Equal("mod", entry.ModuleRequest);
        Assert.Equal(ImportEntry.DefaultImport, entry.ImportName);
        Assert.Equal("x", entry.LocalName);
    }

    [Fact]
    public void NamespaceImportProducesNamespaceEntry()
    {
        var decl = ParseImport("import * as ns from 'mod';");
        var entry = Assert.Single(decl.Entries);
        Assert.True(entry.IsNamespaceImport);
        Assert.Equal("ns", entry.LocalName);
    }

    [Fact]
    public void NamedImportsProduceOneEntryEach()
    {
        var decl = ParseImport("import { a, b as c } from 'mod';");
        Assert.Equal(2, decl.Entries.Count);
        Assert.Equal("a", decl.Entries[0].ImportName);
        Assert.Equal("a", decl.Entries[0].LocalName);
        Assert.Equal("b", decl.Entries[1].ImportName);
        Assert.Equal("c", decl.Entries[1].LocalName);
    }

    [Fact]
    public void CombinedDefaultAndNamedImports()
    {
        var decl = ParseImport("import x, { a } from 'mod';");
        Assert.Equal(2, decl.Entries.Count);
        Assert.True(decl.Entries[0].IsDefaultImport);
        Assert.Equal("x", decl.Entries[0].LocalName);
        Assert.Equal("a", decl.Entries[1].LocalName);
    }

    [Fact]
    public void ExportDefaultExpressionProducesDefaultEntry()
    {
        var decl = ParseExport("export default 42;");
        Assert.NotNull(decl.DefaultExpression);
        var entry = Assert.Single(decl.Entries);
        Assert.Equal(ImportEntry.DefaultImport, entry.ExportName);
        Assert.Equal(ExportEntry.DefaultLocalName, entry.LocalName);
    }

    [Fact]
    public void ExportVarProducesLocalEntries()
    {
        var decl = ParseExport("export var x = 1, y = 2;");
        Assert.NotNull(decl.LocalDeclaration);
        Assert.Equal(2, decl.Entries.Count);
        Assert.Equal("x", decl.Entries[0].ExportName);
        Assert.Equal("x", decl.Entries[0].LocalName);
        Assert.Equal("y", decl.Entries[1].ExportName);
    }

    [Fact]
    public void ExportFunctionProducesNamedEntry()
    {
        var decl = ParseExport("export function f() {}");
        var entry = Assert.Single(decl.Entries);
        Assert.Equal("f", entry.ExportName);
        Assert.Equal("f", entry.LocalName);
    }

    [Fact]
    public void ExportClassProducesNamedEntry()
    {
        var decl = ParseExport("export class C {}");
        var entry = Assert.Single(decl.Entries);
        Assert.Equal("C", entry.ExportName);
    }

    [Fact]
    public void ExportNamedListProducesOneEntryEach()
    {
        var decl = ParseExport("export { a, b as c };");
        Assert.Equal(2, decl.Entries.Count);
        Assert.Equal("a", decl.Entries[0].ExportName);
        Assert.Equal("a", decl.Entries[0].LocalName);
        Assert.Equal("c", decl.Entries[1].ExportName);
        Assert.Equal("b", decl.Entries[1].LocalName);
        Assert.Null(decl.Entries[0].ModuleRequest);
    }

    [Fact]
    public void ReexportNamedFromModuleEmitsModuleRequest()
    {
        var decl = ParseExport("export { a } from 'mod';");
        var entry = Assert.Single(decl.Entries);
        Assert.True(entry.IsReexport);
        Assert.Equal("mod", entry.ModuleRequest);
        Assert.Equal("a", entry.ImportName);
        Assert.Equal("a", entry.ExportName);
        Assert.Null(entry.LocalName);
    }

    [Fact]
    public void StarReexportEmitsAllSentinel()
    {
        var decl = ParseExport("export * from 'mod';");
        var entry = Assert.Single(decl.Entries);
        Assert.True(entry.IsStarReexport);
        Assert.Equal("mod", entry.ModuleRequest);
    }

    [Fact]
    public void NamespaceReexportEmitsAllButDefaultSentinel()
    {
        var decl = ParseExport("export * as ns from 'mod';");
        var entry = Assert.Single(decl.Entries);
        Assert.True(entry.IsNamespaceReexport);
        Assert.Equal("ns", entry.ExportName);
    }
}
