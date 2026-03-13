using System;
using System.Linq;
using FenBrowser.FenEngine.Core;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class JsParserReproTests
    {
        private Parser CreateParser(string input)
        {
            var lexer = new Lexer(input);
            return new Parser(lexer);
        }

        private Parser CreateModuleParser(string input)
        {
            var lexer = new Lexer(input);
            return new Parser(lexer, isModule: true);
        }

        private void AssertNoErrors(Parser parser)
        {
            if (parser.Errors.Any())
            {
                throw new Exception($"Parser errors:\n{string.Join("\n", parser.Errors)}");
            }
        }

        [Fact]
        public void Lexer_DecodesEscapedIdentifier_Exactly()
        {
            var lexer = new Lexer("var privat\\u0065 = 1;");
            // var
            var t1 = lexer.NextToken();
            // identifier
            var t2 = lexer.NextToken();
            Assert.Equal(TokenType.Identifier, t2.Type);
            Assert.Equal("private", t2.Literal);
        }

        [Fact]
        public void Parse_StrictMode_Rejects_EscapedFutureReservedWord()
        {
            var input = "\"use strict\"; var privat\\u0065 = 1;";
            var parser = CreateParser(input);
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("strict mode reserved word 'private'", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_AsyncArrowFunction_InCallExpression()
        {
            // async (x) => 1
            var input = "call(async (x) => 1)";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();
            AssertNoErrors(parser);
            
            // Validate structure
            var stmt = program.Statements.First() as ExpressionStatement;
            var call = stmt.Expression as CallExpression;
            var arrow = call.Arguments.First() as ArrowFunctionExpression;
            Assert.NotNull(arrow);
            Assert.True(arrow.IsAsync);
            Assert.Single(arrow.Parameters);
            Assert.Equal("x", arrow.Parameters[0].Value);
        }

        [Fact]
        public void Parse_AsyncArrowFunction_TwoParams()
        {
            var input = "call(async (x, y) => x + y)";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();
            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_AsyncArrowFunction_NoParams()
        {
            var input = "call(async () => 1)";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();
            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ComplexRegex_InConditional()
        {
            // The failing regex pattern from log
            // Note: C# verbatim string, \\u is passed as \u to Lexer if not double escaped? 
            // C# @"..." preserves backslashes. 
            // Code has \uD800. In C# string literal this is unicode char if not verbatim?
            // @"..." ignores escape sequences. So \uD800 is literally \ u D 8 0 0.
            // But Lexer expects valid JS string.
            // If Input has literally \ u D 8 0 0, JS string/regex parser handles it.
            var input = @"if(b&&(eaa?!a.isWellFormed():/(?:[^\uD800-\uDBFF]|^)[\uDC00-\uDFFF]|[\uD800-\uDBFF](?![\uDC00-\uDFFF])/.test(a)))throw Error('q');";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();
            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_Throw_After_Conditional()
        {
           // Context around failure
           var input = @"if(cond) throw Error('q'); a=(faa||1)";
           var parser = CreateParser(input);
           var program = parser.ParseProgram();
           AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_TemplateLiteral_InvalidHexEscape_ShouldFail()
        {
            var parser = CreateParser("`bad: \\xG1`");
            parser.ParseProgram();
            Assert.NotEmpty(parser.Errors);
        }

        [Fact]
        public void Parse_TemplateLiteral_InvalidUnicodeCodePointEscape_ShouldFail()
        {
            var parser = CreateParser("`bad: \\u{110000}`");
            parser.ParseProgram();
            Assert.NotEmpty(parser.Errors);
        }

        [Fact]
        public void Parse_TemplateLiteral_InvalidLegacyOctalEscape_ShouldFail()
        {
            var parser = CreateParser("`bad: \\08`");
            parser.ParseProgram();
            Assert.NotEmpty(parser.Errors);
        }
        [Fact]
        public void Parse_StrictMode_WithStatement_ShouldFail()
        {
            var parser = CreateParser("\"use strict\"; with ({ x: 1 }) { x; }");
            parser.ParseProgram();
            Assert.Contains(parser.Errors, e => e.Contains("with statement", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_TemplateLiteral_InvalidUnicodeEscape_ShouldFail()
        {
            var parser = CreateParser("`bad: \\u00G0`");
            parser.ParseProgram();
            Assert.NotEmpty(parser.Errors);
        }

        [Fact]
        public void Parse_TaggedTemplate_AfterParenthesizedExpression_NoErrors()
        {
            var parser = CreateParser("var out = (0, tag)``;");
            var program = parser.ParseProgram();

            AssertNoErrors(parser);

            var statement = Assert.IsType<LetStatement>(program.Statements.Single());
            var tagged = Assert.IsType<TaggedTemplateExpression>(statement.Value);
            Assert.Single(tagged.Strings);
            Assert.Equal(string.Empty, tagged.Strings[0]);
        }

        [Fact]
        public void Parse_AnonymousClass_WithAdjacentMethods_AndComputedIterator_NoErrors()
        {
            var input = @"
                var UrlParams = class {
                    get(a) { var b = this.wa.get(a); return b || []; }
                    set(a, b) { this.Aa = null; this.wa.set(a, [b]); this.oa.set(a, this.Ba.Bc(b, a)); }
                    append(a, b) { const c = this.wa.get(a) || []; c.push(b); this.wa.set(a, c); }
                    [Symbol.iterator]() { const a = []; return a[Symbol.iterator](); }
                };
            ";

            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_WithStatement_SingleStatementClassDeclaration_ShouldFail()
        {
            var parser = CreateParser("with ({}) class C {}");
            parser.ParseProgram();
            Assert.Contains(parser.Errors, e => e.Contains("Declaration not allowed in with statement", StringComparison.OrdinalIgnoreCase));
        }
        [Fact]
        public void Parse_CompoundAssignment_PlusAssign()
        {
            var input = "var x = 1; x += 2;";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();
            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ConstObjectDestructuringDeclaration_WithInitializer_NoErrors()
        {
            var input = "const { feature_description, test } = feature_descriptionOrObject;";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);

            var declaration = Assert.IsType<LetStatement>(program.Statements.First());
            Assert.NotNull(declaration.DestructuringPattern);
            Assert.IsType<ObjectLiteral>(declaration.DestructuringPattern);
            Assert.NotNull(declaration.Value);
            Assert.IsType<Identifier>(declaration.Value);
        }

        [Fact]
        public void Parse_DestructuringParameter_WithOuterDefault_NoErrors()
        {
            var input = "function pick({ x } = { x: 5 }) { return x; }";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);

            var declaration = Assert.IsType<FunctionDeclarationStatement>(program.Statements.First());
            var parameter = Assert.Single(declaration.Function.Parameters);
            Assert.NotNull(parameter.DestructuringPattern);
            Assert.IsType<ObjectLiteral>(parameter.DestructuringPattern);
            Assert.NotNull(parameter.DefaultValue);
            Assert.IsType<ObjectLiteral>(parameter.DefaultValue);
        }

        [Fact]
        public void Parse_ArrowFunction_UseStrict_WithNonSimpleParams_ShouldFail()
        {
            var parser = CreateParser("var f = (a = 0) => { \"use strict\"; };");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("non-simple parameter list", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_AsyncArrowFunction_AwaitBindingIdentifier_ShouldFail()
        {
            var parser = CreateParser("async (await) => {}");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("await", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_AsyncArrowFunction_AwaitVarBindingInBody_ShouldFail()
        {
            var parser = CreateParser("async () => { var await; }");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("await", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_ArrowFunction_RestParameterWithDefault_ShouldFail()
        {
            var parser = CreateParser("(...args = []) => {}");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Rest parameter cannot have a default initializer", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_ClassStaticBlock_AwaitIdentifierReference_ShouldFail()
        {
            var parser = CreateParser("class C { static { await; } }");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("class static block", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_ClassStaticBlock_AwaitBindingIdentifier_ShouldFail()
        {
            var parser = CreateParser("class C { static { class await {} } }");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("class static block", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_ForOf_ObjectRestNotLastInAssignmentTarget_ShouldFail()
        {
            var parser = CreateParser("for ({ ...rest, value } of items) {}");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Rest element must be last in object binding pattern", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_ClassAccessor_ComputedNameWithInInsideForHead_NoErrors()
        {
            var input = @"
                var empty = {};
                var C, value;
                for (C = class { get ['x' in empty]() { return 'via get'; } }; ; ) {
                    value = C.prototype.false;
                    break;
                }
            ";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ObjectAccessor_ComputedNameWithInInsideForHead_NoErrors()
        {
            var input = @"
                var empty = {};
                var obj, value;
                for (obj = { get ['x' in empty]() { return 'via get'; } }; ; ) {
                    value = obj.false;
                    break;
                }
            ";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_StrictModeLegacyOctalStringEscape_ShouldFail()
        {
            var parser = CreateParser("\"use strict\"; '\\1';");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Legacy octal literals are not allowed in strict mode", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_NonStrictLegacyOctalStringEscape_ZeroZero_UsesSingleNulCodeUnit()
        {
            var parser = CreateParser("'\\00';");
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            var statement = Assert.IsType<ExpressionStatement>(Assert.Single(program.Statements));
            var literal = Assert.IsType<StringLiteral>(statement.Expression);
            Assert.Equal("\0", literal.Value);
        }

        [Fact]
        public void Parse_StrictModeNonOctalDecimalStringEscape_ShouldFail()
        {
            var parser = CreateParser("\"use strict\"; '\\8';");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Legacy octal literals are not allowed in strict mode", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_StringLiteral_InvalidUnicodeEscape_ShouldFail()
        {
            var parser = CreateParser("'\\u';");
            parser.ParseProgram();

            Assert.NotEmpty(parser.Errors);
        }

        [Fact]
        public void Parse_StringLiteral_Allows_LineSeparatorLiteral()
        {
            var parser = CreateParser("\"\u2028\";");
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            var statement = Assert.IsType<ExpressionStatement>(Assert.Single(program.Statements));
            var literal = Assert.IsType<StringLiteral>(statement.Expression);
            Assert.Equal("\u2028", literal.Value);
        }

        [Fact]
        public void Parse_StrictModeTemplateExpressionLegacyOctalStringEscape_ShouldFail()
        {
            var parser = CreateParser("\"use strict\"; `${'\\07'}`;");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Legacy octal literals are not allowed in strict mode", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_ClassField_SameLineWithoutSeparator_ShouldFail()
        {
            var parser = CreateParser("class C { x y }");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Class fields must be separated", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_ClassField_NameConstructor_ShouldFail()
        {
            var parser = CreateParser("class C { constructor; }");
            parser.ParseProgram();

            Assert.NotEmpty(parser.Errors);
        }

        [Fact]
        public void Parse_StaticClassField_NamePrototype_ShouldFail()
        {
            var parser = CreateParser("class C { static 'prototype'; }");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Static class fields cannot be named 'prototype'", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_LegacyOctalIntegerLiteral_NonStrict_UsesOctalValue()
        {
            var parser = CreateParser("070;");
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            var statement = Assert.IsType<ExpressionStatement>(Assert.Single(program.Statements));
            var literal = Assert.IsType<IntegerLiteral>(statement.Expression);
            Assert.Equal(56L, literal.Value);
        }

        [Fact]
        public void Parse_NonOctalDecimalIntegerLiteral_NonStrict_RemainsDecimal()
        {
            var parser = CreateParser("078;");
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
            var statement = Assert.IsType<ExpressionStatement>(Assert.Single(program.Statements));
            var literal = Assert.IsType<IntegerLiteral>(statement.Expression);
            Assert.Equal(78L, literal.Value);
        }

        [Fact]
        public void Parse_UsingDeclaration_InIfSingleStatement_ShouldFail()
        {
            var parser = CreateParser("if (true) using x = null;");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("single-statement body", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_AwaitUsingDeclaration_WithoutInitializer_ShouldFail()
        {
            var parser = CreateParser("for (;false;) await using x;");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Missing initializer in const declaration", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_Module_DuplicateExportedName_ShouldFail()
        {
            var parser = CreateModuleParser("var x; export { x }; export { x };");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Duplicate export", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_Module_DuplicateTopLevelLexicalName_ShouldFail()
        {
            var parser = CreateModuleParser("function x() {} async function x() {}");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Duplicate declaration 'x' in module scope", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_Module_ExportedBindingMustBeDeclared_ShouldFail()
        {
            var parser = CreateModuleParser("export { Number };");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("not declared in module scope", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_Module_IllFormedStringExportName_ShouldFail()
        {
            var parser = CreateModuleParser("export {Moon as \"\\uD83C\"} from \"./mod.js\"; function Moon() {}");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Ill-formed Unicode string", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_Module_HtmlOpenComment_ShouldFail()
        {
            var parser = CreateModuleParser("<!--");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("HTML-like comments", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_Module_HtmlCloseComment_ShouldFail()
        {
            var parser = CreateModuleParser("-->");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("HTML-like comments", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_Module_DuplicateLabel_ShouldFail()
        {
            var parser = CreateModuleParser("label: { label: 0; }");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Duplicate label 'label'", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_Module_UndefinedBreakTarget_ShouldFail()
        {
            var parser = CreateModuleParser("while (false) { break undef; }");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Undefined break target 'undef'", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_Module_UndefinedContinueTarget_ShouldFail()
        {
            var parser = CreateModuleParser("while (false) { continue undef; }");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Undefined continue target 'undef'", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_Module_ExportStarAsStringName_ShouldSucceed()
        {
            var parser = CreateModuleParser("export * as \"All\" from \"./mod.js\";");
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_Module_QuotedStarExport_UnpairedSurrogate_ShouldFail()
        {
            var parser = CreateModuleParser("export \"*\" as \"\\uD83D\" from \"./mod.js\";");
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("Ill-formed Unicode string", StringComparison.OrdinalIgnoreCase));
        }
    }
}



