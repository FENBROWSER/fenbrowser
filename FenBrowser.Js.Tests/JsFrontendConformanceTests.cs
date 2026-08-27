using FenBrowser.Js.Ast;
using FenBrowser.Js.Modules;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class JsFrontendConformanceTests
{
    [Fact]
    public void ThrowRejectsLineTerminatorBeforeArgument()
    {
        Assert.Throws<JsParserException>(() =>
            JsParser.ParseScript(new SourceText("throw\n1;")));
    }

    [Fact]
    public void ObjectBindingStringKeyStoresDecodedPropertyName()
    {
        var program = JsParser.ParseScript(new SourceText("const {\"a\\u0062\": value} = source;"));
        var declaration = Assert.IsType<VariableDeclarationStatementNode>(Assert.Single(program.Body));
        var pattern = Assert.IsType<ObjectBindingPatternNode>(Assert.Single(declaration.Declarators).BindingPattern);

        Assert.Equal("ab", Assert.Single(pattern.Properties).Key);
    }

    [Theory]
    [InlineData("({\"a\\u0062\": 1})")]
    [InlineData("({\"a\\u0062\"() {}})")]
    [InlineData("({get \"a\\u0062\"() {}})")]
    [InlineData("({*\"a\\u0062\"() {}})")]
    [InlineData("({async \"a\\u0062\"() {}})")]
    [InlineData("({async *\"a\\u0062\"() {}})")]
    public void ObjectLiteralStringKeysStoreDecodedPropertyName(string source)
    {
        var program = JsParser.ParseScript(new SourceText(source));
        var statement = Assert.IsType<ExpressionStatementNode>(Assert.Single(program.Body));
        var parenthesized = Assert.IsType<ParenthesizedExpressionNode>(statement.Expression);
        var literal = Assert.IsType<ObjectLiteralExpressionNode>(parenthesized.Expression);

        Assert.Equal("ab", Assert.Single(literal.Properties).Key);
    }

    [Fact]
    public void NumericPropertyNameAboveInt64UsesJavascriptNumberFormatting()
    {
        var program = JsParser.ParseScript(new SourceText("({10000000000000000000: 1})"));
        var statement = Assert.IsType<ExpressionStatementNode>(Assert.Single(program.Body));
        var parenthesized = Assert.IsType<ParenthesizedExpressionNode>(statement.Expression);
        var literal = Assert.IsType<ObjectLiteralExpressionNode>(parenthesized.Expression);

        Assert.Equal("10000000000000000000", Assert.Single(literal.Properties).Key);
    }

    [Fact]
    public void NonDecimalNumericPropertyNameAboveInt64UsesJavascriptNumberFormatting()
    {
        var program = JsParser.ParseScript(new SourceText("({0x8000000000000000: 1})"));
        var statement = Assert.IsType<ExpressionStatementNode>(Assert.Single(program.Body));
        var parenthesized = Assert.IsType<ParenthesizedExpressionNode>(statement.Expression);
        var literal = Assert.IsType<ObjectLiteralExpressionNode>(parenthesized.Expression);

        Assert.Equal("9223372036854776000", Assert.Single(literal.Properties).Key);
    }

    [Fact]
    public void SuperPropertyIsAllowedInClassMethodParameterDefault()
    {
        var program = JsParser.ParseScript(new SourceText("class Derived extends Base { method(value = super.answer) {} }"));

        Assert.Single(program.Body);
    }

    [Fact]
    public void SuperCallRemainsRejectedInClassMethodParameterDefault()
    {
        Assert.Throws<JsParserException>(() =>
            JsParser.ParseScript(new SourceText("class Derived extends Base { method(value = super()) {} }")));
    }

    [Fact]
    public void SuperPropertyRemainsRejectedInRegularFunctionParameterDefault()
    {
        Assert.Throws<JsParserException>(() =>
            JsParser.ParseScript(new SourceText("function ordinary(value = super.answer) {}")));
    }

    [Fact]
    public void ExportDefaultRejectsCommaExpression()
    {
        Assert.Throws<JsParserException>(() =>
            JsParser.ParseModule(new SourceText("export default 1, 2;")));
    }

    [Fact]
    public void ExportDefaultAllowsParenthesizedCommaExpression()
    {
        var program = JsParser.ParseModule(new SourceText("export default (1, 2);"));

        Assert.Single(program.Body);
    }

    [Fact]
    public void ModuleNamedImportsAndExportsAcceptStringNames()
    {
        var importProgram = JsParser.ParseModule(new SourceText("import { \"a\\u0062\" as local } from \"mod\";"));
        var importDeclaration = Assert.IsType<ImportDeclarationNode>(Assert.Single(importProgram.Body));
        Assert.Equal("ab", Assert.Single(importDeclaration.Entries).ImportName);

        var exportProgram = JsParser.ParseModule(new SourceText("export { \"a\\u0062\" as \"c\\u0064\" } from \"mod\";"));
        var exportDeclaration = Assert.IsType<ExportDeclarationNode>(Assert.Single(exportProgram.Body));
        var exportEntry = Assert.Single(exportDeclaration.Entries);
        Assert.Equal("ab", exportEntry.ImportName);
        Assert.Equal("cd", exportEntry.ExportName);

        var namespaceProgram = JsParser.ParseModule(new SourceText("export * as \"n\\u0073\" from \"mod\";"));
        var namespaceDeclaration = Assert.IsType<ExportDeclarationNode>(Assert.Single(namespaceProgram.Body));
        Assert.Equal("ns", Assert.Single(namespaceDeclaration.Entries).ExportName);
    }
}
