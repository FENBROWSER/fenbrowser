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
        public void Parse_GroupedArrowIife_BundleHeaderShape_NoErrors()
        {
            var input = "((t,e)=>{\"object\"==typeof exports&&\"object\"==typeof module?module.exports=e():\"function\"==typeof define&&define.amd?define([],e):\"object\"==typeof exports?exports.ClipboardJS=e():t.ClipboardJS=e()})(this,function(){})";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ConditionalCommaExpression_WithAsyncArrowIife_NoErrors()
        {
            var input = "var x={available:function(){if(void 0===window.WIMB.data.client_hints_frontend_available)return void 0!==navigator.userAgentData?(window.WIMB.data.client_hints_uadata=navigator.userAgentData,window.WIMB.data.client_hints_frontend_available=!0,(async()=>{var e=await navigator.userAgentData.getHighEntropyValues([\"architecture\"]);window.WIMB.data.client_hints_frontend_architecture=e.architecture})(),window.WIMB.data.client_hints_frontend_brands=window.WIMB.data.client_hints_uadata.brands,!0):window.WIMB.data.client_hints_frontend_available=!1}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_PrivateModePromiseBranch_MinifiedSnippet_NoErrors()
        {
            var input = "var x={private_mode:{enabled:function(){return new Promise(function(e){function t(){e(!0)}function n(){e(!1)}var o,i;function r(){var e=navigator.userAgent.match(/Version\\/[0-9\\._]+.*Safari/);if(e){if(parseInt(e[1],10)<11){try{localStorage.length||(localStorage.setItem(\"inPrivate\",\"0\"),localStorage.removeItem(\"inPrivate\")),n()}catch(e){(navigator.cookieEnabled?t:n)()}return!0;return}try{window.openDatabase(null,null,null,null),n()}catch(e){t()}}return e}if(((o=/(?=.*(opera|chrome)).*/i.test(navigator.userAgent)&&navigator.storage&&navigator.storage.estimate)&&navigator.storage.estimate().then(function(e){return(e.quota<12e7?t:n)()}),!o)&&((o=\"MozAppearance\"in document.documentElement.style)&&(null==indexedDB?t():((i=indexedDB.open(\"inPrivate\")).onsuccess=n,i.onerror=t)),!o&&!r()&&((i=!window.indexedDB&&(window.PointerEvent||window.MSPointerEvent))&&t(),!i)))return n()})}}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_PrivateModePromiseCallbackBody_NoErrors()
        {
            var input = "var x=function(){return new Promise(function(e){function t(){e(!0)}function n(){e(!1)}var o,i;function r(){var e=navigator.userAgent.match(/Version\\/[0-9\\._]+.*Safari/);if(e){if(parseInt(e[1],10)<11){try{localStorage.length||(localStorage.setItem(\"inPrivate\",\"0\"),localStorage.removeItem(\"inPrivate\")),n()}catch(e){(navigator.cookieEnabled?t:n)()}return!0;return}try{window.openDatabase(null,null,null,null),n()}catch(e){t()}}return e}if(((o=/(?=.*(opera|chrome)).*/i.test(navigator.userAgent)&&navigator.storage&&navigator.storage.estimate)&&navigator.storage.estimate().then(function(e){return(e.quota<12e7?t:n)()}),!o)&&((o=\"MozAppearance\"in document.documentElement.style)&&(null==indexedDB?t():((i=indexedDB.open(\"inPrivate\")).onsuccess=n,i.onerror=t)),!o&&!r()&&((i=!window.indexedDB&&(window.PointerEvent||window.MSPointerEvent))&&t(),!i)))return n()})};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_VarInitializerObjectFollowedByVar_MinifiedSnippet_NoErrors()
        {
            var input = "var WIMB_UTIL=(window.WIMB.init(),window.WIMB.meta.js_src_url=document.currentScript.src,WIMB_UTIL||{version:\"1.0\",get_style:function(e,t){return rv=\"\",e.currentStyle?rv=e.currentStyle[t]:window.getComputedStyle&&(rv=document.defaultView.getComputedStyle(e,null).getPropertyValue(t)),rv},decode_java_version:function(e){if(!e)return!1;var t=e.split(\".\"),n=[],o=\"\";for(v=\"1\"==t[0]&&t[1]<=\"4\"?0:1;v<t.length;v++)!1!==t[v].search(/^[0-9]+[_]+[0-9]*$/)?(fragment_fragments=t[v].split(\"_\"),n.push(parseInt(fragment_fragments[0])),void 0!==fragment_fragments[1]&&(o=parseInt(fragment_fragments[1]))):(n.push(t[v]),v++);return{version:n,update:o}}});var WIMB_CAPABILITIES=WIMB_CAPABILITIES||{capabilities:{},add:function(e,t,o){o?(WIMB_CAPABILITIES.capabilities[o]||(WIMB_CAPABILITIES.capabilities[o]={}),WIMB_CAPABILITIES.capabilities[o][e]=t):WIMB_CAPABILITIES.capabilities[e]=t}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ParenthesizedConditionalResult_Call_NoErrors()
        {
            var input = "var x = function(e){ return (e.quota < 12e7 ? t : n)(); };";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_TernaryParenthesizedCommaBranch_WithLogicalAndAssignment_NoErrors()
        {
            var input = "var x = flag ? (fragment_fragments=t[v].split(\"_\"),n.push(parseInt(fragment_fragments[0])),void 0!==fragment_fragments[1]&&(o=parseInt(fragment_fragments[1]))) : (n.push(t[v]),v++);";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_NestedSingleArgumentCall_FollowedByCommaInGroup_NoErrors()
        {
            var input = "var x = (n.push(parseInt(fragment_fragments[0])), void 0);";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_PrivateModeTailGroupedCondition_NoErrors()
        {
            var input = "var x=function(){if(((o=\"MozAppearance\"in document.documentElement.style)&&(null==indexedDB?t():((i=indexedDB.open(\"inPrivate\")).onsuccess=n,i.onerror=t)),!o&&!r()&&((i=!window.indexedDB&&(window.PointerEvent||window.MSPointerEvent))&&t(),!i)))return n()};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_NewPromiseCallback_MinifiedReturnTail_NoErrors()
        {
            var input = "var x=function(){return new Promise(function(e){function t(){e(!0)}function n(){e(!1)}if(((o=\"MozAppearance\"in document.documentElement.style)&&(null==indexedDB?t():((i=indexedDB.open(\"inPrivate\")).onsuccess=n,i.onerror=t)),!o&&!r()&&((i=!window.indexedDB&&(window.PointerEvent||window.MSPointerEvent))&&t(),!i)))return n()})};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_PrivateModeHeadCondition_NoErrors()
        {
            var input = "var x=function(){if(((o=/(?=.*(opera|chrome)).*/i.test(navigator.userAgent)&&navigator.storage&&navigator.storage.estimate)&&navigator.storage.estimate().then(function(e){return(e.quota<12e7?t:n)()}),!o))return n()};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_PrivateModeSafariHelper_NoErrors()
        {
            var input = "var x=function(){function r(){var e=navigator.userAgent.match(/Version\\/[0-9\\._]+.*Safari/);if(e){if(parseInt(e[1],10)<11){try{localStorage.length||(localStorage.setItem(\"inPrivate\",\"0\"),localStorage.removeItem(\"inPrivate\")),n()}catch(e){(navigator.cookieEnabled?t:n)()}return!0;return}try{window.openDatabase(null,null,null,null),n()}catch(e){t()}}return e}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_PrivateModeCombinedCallbackBody_NoErrors()
        {
            var input = "var x=function(){function t(){e(!0)}function n(){e(!1)}var o,i;function r(){var e=navigator.userAgent.match(/Version\\/[0-9\\._]+.*Safari/);if(e){if(parseInt(e[1],10)<11){try{localStorage.length||(localStorage.setItem(\"inPrivate\",\"0\"),localStorage.removeItem(\"inPrivate\")),n()}catch(e){(navigator.cookieEnabled?t:n)()}return!0;return}try{window.openDatabase(null,null,null,null),n()}catch(e){t()}}return e}if(((o=/(?=.*(opera|chrome)).*/i.test(navigator.userAgent)&&navigator.storage&&navigator.storage.estimate)&&navigator.storage.estimate().then(function(e){return(e.quota<12e7?t:n)()}),!o)&&((o=\"MozAppearance\"in document.documentElement.style)&&(null==indexedDB?t():((i=indexedDB.open(\"inPrivate\")).onsuccess=n,i.onerror=t)),!o&&!r()&&((i=!window.indexedDB&&(window.PointerEvent||window.MSPointerEvent))&&t(),!i)))return n()};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_PrivateModeWrappedObjectLiteral_NoErrors()
        {
            var input = "var x={private_mode:{enabled:function(){function t(){e(!0)}function n(){e(!1)}var o,i;function r(){var e=navigator.userAgent.match(/Version\\/[0-9\\._]+.*Safari/);if(e){if(parseInt(e[1],10)<11){try{localStorage.length||(localStorage.setItem(\"inPrivate\",\"0\"),localStorage.removeItem(\"inPrivate\")),n()}catch(e){(navigator.cookieEnabled?t:n)()}return!0;return}try{window.openDatabase(null,null,null,null),n()}catch(e){t()}}return e}if(((o=/(?=.*(opera|chrome)).*/i.test(navigator.userAgent)&&navigator.storage&&navigator.storage.estimate)&&navigator.storage.estimate().then(function(e){return(e.quota<12e7?t:n)()}),!o)&&((o=\"MozAppearance\"in document.documentElement.style)&&(null==indexedDB?t():((i=indexedDB.open(\"inPrivate\")).onsuccess=n,i.onerror=t)),!o&&!r()&&((i=!window.indexedDB&&(window.PointerEvent||window.MSPointerEvent))&&t(),!i)))return n()}}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ObjectLiteralFunctionEndingWithIfReturn_NoErrors()
        {
            var input = "var x={private_mode:{enabled:function(){if(cond)return n()}}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ObjectLiteralFunctionDeclarationsThenIfReturn_NoErrors()
        {
            var input = "var x={private_mode:{enabled:function(){function t(){e(!0)}function n(){e(!1)}if(cond)return n()}}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ObjectLiteralFunctionReturningPromiseIfReturn_NoErrors()
        {
            var input = "var x={private_mode:{enabled:function(){return new Promise(function(e){if(cond)return n()})}}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ObjectLiteralFunctionReturningPromiseWithDeclarations_NoErrors()
        {
            var input = "var x={private_mode:{enabled:function(){return new Promise(function(e){function t(){e(!0)}function n(){e(!1)}if(cond)return n()})}}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ObjectLiteralFunction_ReturnsCommaTernaryComma_NoErrors()
        {
            var input = "var x={get_style:function(e,t){return rv=\"\",e.currentStyle?rv=e.currentStyle[t]:window.getComputedStyle&&(rv=document.defaultView.getComputedStyle(e,null).getPropertyValue(t)),rv},decode_java_version:function(e){return e}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_Function_ReturnsCommaTernaryComma_NoErrors()
        {
            var input = "var x=function(e,t){return rv=\"\",e.currentStyle?rv=e.currentStyle[t]:window.getComputedStyle&&(rv=document.defaultView.getComputedStyle(e,null).getPropertyValue(t)),rv};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ObjectLiteral_GetStyleAndDecodeVersion_Minified_NoErrors()
        {
            var input = "var x={version:\"1.0\",get_style:function(e,t){return rv=\"\",e.currentStyle?rv=e.currentStyle[t]:window.getComputedStyle&&(rv=document.defaultView.getComputedStyle(e,null).getPropertyValue(t)),rv},decode_java_version:function(e){if(!e)return!1;var t=e.split(\".\"),n=[],o=\"\";for(v=\"1\"==t[0]&&t[1]<=\"4\"?0:1;v<t.length;v++)!1!==t[v].search(/^[0-9]+[_]+[0-9]*$/)?(fragment_fragments=t[v].split(\"_\"),n.push(parseInt(fragment_fragments[0])),void 0!==fragment_fragments[1]&&(o=parseInt(fragment_fragments[1]))):(n.push(t[v]),v++);return{version:n,update:o}}};";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_GroupedInitializer_WithLogicalOrObjectLiteral_NoErrors()
        {
            var input = "var WIMB_UTIL=(window.WIMB.init(),window.WIMB.meta.js_src_url=document.currentScript.src,WIMB_UTIL||{version:\"1.0\",get_style:function(e,t){return rv=\"\",e.currentStyle?rv=e.currentStyle[t]:window.getComputedStyle&&(rv=document.defaultView.getComputedStyle(e,null).getPropertyValue(t)),rv},decode_java_version:function(e){return e}});";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_GroupedInitializer_WithLogicalOrObjectLiteral_SingleFunctionProperty_NoErrors()
        {
            var input = "var WIMB_UTIL=(window.WIMB.init(),window.WIMB.meta.js_src_url=document.currentScript.src,WIMB_UTIL||{get_style:function(e,t){return rv=\"\",e.currentStyle?rv=e.currentStyle[t]:window.getComputedStyle&&(rv=document.defaultView.getComputedStyle(e,null).getPropertyValue(t)),rv}});";
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



