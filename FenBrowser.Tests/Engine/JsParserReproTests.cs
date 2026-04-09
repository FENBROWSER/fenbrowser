using System;
using System.IO;
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

        private Parser CreateRuntimeParser(string input, bool isModule = false)
        {
            var lexer = new Lexer(input);
            return new Parser(lexer, isModule: isModule, allowRecovery: false);
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
        public void Parse_WebpackArrowBody_WithChainedAssignment_NoErrors()
        {
            var input = "var t=new Promise(((r,t)=>n=e[a]=[r,t]));";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ForLoop_CommaSequenceBody_NoErrors()
        {
            var input = "for(var e,t,n,i=\"\",r=this.array(),o=0;o<15;)e=r[o++],t=r[o++],n=r[o++],i+=BASE64_ENCODE_CHAR[e>>2]+BASE64_ENCODE_CHAR[(3&e)<<4|t>>4]+BASE64_ENCODE_CHAR[(15&t)<<2|n>>6]+BASE64_ENCODE_CHAR[63&n];";
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
        public void Parse_SwitchCase_DefaultReturn_AfterObjectSpreadReturn_NoErrors()
        {
            var input = "function f(e,o,t){switch(e){case\"tile\":return{...o,content:M(t)};default:return}}";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_NestedSwitch_DefaultReturnDelete_ThenOuterDefault_NoErrors()
        {
            var input = "function f(e,s,m){switch(e){case 0:switch(s){case 1:return delete l[m];default:i(17,s)}default:i(17,s)}}";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ObjectLiteral_WithArrowValuedProperties_NoErrors()
        {
            var input = "n.d(t,{v:()=>k,Z:()=>({value:1})});";
            var parser = CreateParser(input);
            parser.ParseProgram();

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
        public void Parse_TemplateLiteral_AfterIifeReturningObjectLiteral_NoErrors()
        {
            var parser = CreateParser("var out = `${(() => ({ value: /x/.test(input) ? ok : fallback }))()}`;");
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_TemplateLiteral_WithRegexSourceInTemplateMiddle_NoErrors()
        {
            var parser = CreateParser("var out = new RegExp(`${o.source}${/\\/([\\s\\S]+)/.source}`);");
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_FunctionDeclaration_ReturningArray_ThenVar_NoErrors()
        {
            var input = "function collect(){return [a[Z][l(Gn)],a[Z][d(Sn)],a[Z][d(du)],a[Z][l(Ba)]]}var x,C,T,I;";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_FunctionExpression_WithNestedFunction_ReturnArray_ThenVar_NoErrors()
        {
            var input = "O=function(){function e(){this.T=new Ui}return [a[Z][l(Gn)],a[Z][d(Sn)],a[Z][d(du)],a[Z][l(Ba)]]};var x,C,T,I;";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ModuleObject_NumericFunctionProperty_UseStrict_NoErrors()
        {
            var input = "var mods={8258:function(e,t,r){\"use strict\";return void 0===l?\"\":l}};var x=1;";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_FunctionK_CommaIifeCondition_NoErrors()
        {
            var input = "function k(e,t,r,n,i,o){var a=[];if(function(e,t,r,n){e[s(cR)](t,r,n)}(e,t,r,n),a[Z]=(a[se]=l(Xi),a[de]=l(Xi),function(e,t,r,n,i){if(e)try{return e[s(ER)](t,r,n,i)[s(y_)]}catch(e){return}}(e,i,o,a[se],a[de])),a[Z])return[a[Z][l(Gn)],a[Z][d(Sn)],a[Z][d(du)],a[Z][l(Ba)]]}";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_FunctionK_FollowedByVarDeclaration_NoErrors()
        {
            var input = "function k(e,t,r,n,i,o){var a=[];if(function(e,t,r,n){e[s(cR)](t,r,n)}(e,t,r,n),a[Z]=(a[se]=l(Xi),a[de]=l(Xi),function(e,t,r,n,i){if(e)try{return e[s(ER)](t,r,n,i)[s(y_)]}catch(e){return}}(e,i,o,a[se],a[de])),a[Z])return[a[Z][l(Gn)],a[Z][d(Sn)],a[Z][d(du)],a[Z][l(Ba)]]}var x,C,T,I,O=function(){function e(){this.T=new Uint16Array(16),this.q=new Uint16Array(288)}};";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_RegexLiteralStartingWithEquals_InCallChain_NoErrors()
        {
            var input = "var out=r[se][u(Bo)](/\\+/g,Iw)[s(Ow)](/\\//g,wy)[Pw](/=+$/,ce);";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_ReactHelper_TryFinally_WithNestedIfElseAndDoWhile_NoErrors()
        {
            var input = "function U(e,t){if(!e||j)return\"\";j=!0;var r=Error.prepareStackTrace;Error.prepareStackTrace=void 0;try{if(t)if(t=function(){throw Error()},Object.defineProperty(t.prototype,\"props\",{set:function(){throw Error()}}),\"object\"==typeof Reflect&&Reflect.construct){try{Reflect.construct(t,[])}catch(e){var n=e}Reflect.construct(e,[],t)}else{try{t.call()}catch(e){n=e}e.call(t.prototype)}else{try{throw Error()}catch(e){n=e}e()}}catch(t){if(t&&n&&\"string\"==typeof t.stack){for(var i=t.stack.split(\"\\n\"),o=n.stack.split(\"\\n\"),a=i.length-1,u=o.length-1;1<=a&&0<=u&&i[a]!==o[u];)u--;for(;1<=a&&0<=u;a--,u--)if(i[a]!==o[u]){if(1!==a||1!==u)do{if(a--,0>--u||i[a]!==o[u]){var s=\"\\n\"+i[a].replace(\" at new \",\" at \");return e.displayName&&s.includes(\"<anonymous>\")&&(s=s.replace(\"<anonymous>\",e.displayName)),s}}while(1<=a&&0<=u);break}}}finally{j=!1,Error.prepareStackTrace=r}return(e=e?e.displayName||e.name:\"\")?z(e):\"\"}";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_GlobalizeFormatter_SwitchThenStringExpression_NoErrors()
        {
            var input = "var _=[\"sun\",\"mon\",\"tue\",\"wed\",\"thu\",\"fri\",\"sat\"],S=function(e,t,r){var n=[],i=r.timeSeparator;return r.pattern.replace(y,(function(o){var u,s,l,c=o.charAt(0),d=o.length;switch(\"j\"===c&&(c=r.preferredTime),\"Z\"===c&&(d<4?(c=\"x\",d=4):d<5?(c=\"O\",d=4):(c=\"X\",d=5)),\"z\"===c&&(e.isDST&&(l=e.isDST()?r.daylightTzName:r.standardTzName),l||(c=\"O\",d<4&&(d=1))),c){case\"G\":l=r.eras[e.getFullYear()<0?0:1];break;case\"y\":l=e.getFullYear(),2===d&&(l=+(l=String(l)).substr(l.length-2));break;case\"Y\":(l=new Date(e.getTime())).setDate(l.getDate()+7-p(e,r.firstDay)-r.firstDay-r.minDays),l=l.getFullYear(),2===d&&(l=+(l=String(l)).substr(l.length-2));break;case\"Q\":case\"q\":l=Math.ceil((e.getMonth()+1)/3),d>2&&(l=r.quarters[c][d][l]);break;case\"M\":case\"L\":l=e.getMonth()+1,d>2&&(l=r.months[c][d][l]);break;case\"w\":l=p(v(e,\"year\"),r.firstDay),l=Math.ceil((g(e)+l)/7)-(7-l>=r.minDays?0:1);break;case\"W\":l=p(v(e,\"month\"),r.firstDay),l=Math.ceil((e.getDate()+l)/7)-(7-l>=r.minDays?0:1);break;case\"d\":l=e.getDate();break;case\"D\":l=g(e)+1;break;case\"F\":l=Math.floor(e.getDate()/7)+1;break;case\"e\":case\"c\":if(d<=2){l=p(e,r.firstDay)+1;break}case\"E\":l=_[e.getDay()],l=r.days[c][d][l];break;case\"a\":l=r.dayPeriods[e.getHours()<12?\"am\":\"pm\"];break;case\"h\":l=e.getHours()%12||12;break;case\"H\":l=e.getHours();break;case\"K\":l=e.getHours()%12;break;case\"k\":l=e.getHours()||24;break;case\"m\":l=e.getMinutes();break;case\"s\":l=e.getSeconds();break;case\"S\":l=Math.round(e.getMilliseconds()*Math.pow(10,d-3));break;case\"A\":l=Math.round(function(e){return e-v(e,\"day\")}(e)*Math.pow(10,d-3));break;case\"z\":break;case\"v\":if(r.genericTzName){l=r.genericTzName;break}case\"V\":if(r.timeZoneName){l=r.timeZoneName;break}\"v\"===o&&(d=1);case\"O\":0===e.getTimezoneOffset()?l=r.gmtZeroFormat:(d<4?(u=e.getTimezoneOffset(),u=r.hourFormat[u%60-u%1==0?0:1]):u=r.hourFormat,l=b(e,u,i,t),l=r.gmtFormat.replace(/\\{0\\}/,l));break;case\"X\":if(0===e.getTimezoneOffset()){l=\"Z\";break}case\"x\":u=e.getTimezoneOffset(),1===d&&u%60-u%1!=0&&(d+=1),4!==d&&5!==d||u%1!=0||(d-=2),l=b(e,l=[\"+HH;-HH\",\"+HHmm;-HHmm\",\"+HH:mm;-HH:mm\",\"+HHmmss;-HHmmss\",\"+HH:mm:ss;-HH:mm:ss\"][d-1],\":\");break;case\":\":l=i;break;case\"'\":l=a(o);break;default:l=o}\"number\"==typeof l&&(l=t[d](l)),\"literal\"===(s=m[c]||\"literal\")&&n.length&&\"literal\"===n[n.length-1].type?n[n.length-1].value+=l:n.push({type:s,value:l})})),n};";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
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
        public void Parse_XVendor_NestedCompoundAssignments_NoErrors()
        {
            var input = "var r=0,n=0,e={pendingLanes:1,t:{lanes:0}}; r|=n&=e.pendingLanes,t.lanes=r;";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_DoubleBangPostfixUpdate_NoErrors()
        {
            var input = "var o=0; function next(){return{done:!!o++}}";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_RegexComputedMemberCall_AfterStrictEquals_NoErrors()
        {
            var input = "var b=Symbol.replace,C=!!/./[b]&&\"\"===/./[b](\"a\",\"$0\");";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XMain_ParenthesizedArrowCallback_WithParenStartedExpressionAfterIfBlock_NoErrors()
        {
            var input = "var out=s.map((e=>{if(e){return a}((x,y)=>y)(i,e)}));";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_FunctionExpression_Iife_WithTryCatchInBody_NoErrors()
        {
            var input = "(function(){try{return x}catch(e){return}}(a))";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_FunctionIife_WithIfTryCatchInCommaCondition_NoErrors()
        {
            var input = "function k(e,t,r,n,i,o){var a=[];if(function(e,t,r,n){e[s(cR)](t,r,n)}(e,t,r,n),a[Z]=(a[se]=l(Xi),a[de]=l(Xi),function(e,t,r,n,i){if(e)try{return e[s(ER)](t,r,n,i)[s(y_)]}catch(e){return}}(e,i,o,a[se],a[de])),a[Z])return[a[Z][l(Gn)],a[Z][d(Sn)],a[Z][d(du)],a[Z][l(Ba)]]}";
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_FunctionIife_WithTailForAndIfBlocks_BeforeCommaContinuation_NoErrors()
        {
            var input = "function q(){x&&(a=function(){if(n){for(var c=0;c<1;c++){var d=l(i[c],o[c],a);if(null!=d)return d;if(!0===r.isPropagationStopped())return}if(s)for(var f=0;f<1;f++){var h=l(i[f],o[f],u);if(null!=h)return h;if(!0===r.isPropagationStopped())return}else{var p=i[0],v=o[0];if(t.target===v)return l(p,v,u)}}}(),b)}";
            var parser = CreateParser(input);
            parser.ParseProgram();

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

        [Fact]
        public void Parse_XVendor_GlobalizeWrapper_WithDirectiveAndGroupedFactory_NoErrors()
        {
            var input = """
                !function(t,n){"use strict";"function"==typeof define&&define.amd?define(["../globalize-runtime","./number"],n):e.exports=n(r(34047),r(993146))}(0,(function(e){var t=e._formatMessage,r=e._runtimeKey,n=e._validateParameterPresence,i=e._validateParameterTypeNumber,o=function(e,r,n){var i,o,a=n.displayNames||{},u=n.unitPatterns;return i=a["displayName-count-"+r]||a["displayName-count-other"]||a.displayName||n.currency,o=u["unitPattern-count-"+r]||u["unitPattern-count-other"],t(o,[e,i])};return e._currencyFormatterFn=function(e,t,r){return t&&r?function(a){return n(a,"value"),i(a,"value"),o(e(a),t(a),r)}:function(t){return e(t)}},e._currencyNameFormat=o,e.currencyFormatter=e.prototype.currencyFormatter=function(t,n){return n=n||{},e[r("currencyFormatter",this._locale,[t,n])]},e.formatCurrency=e.prototype.formatCurrency=function(e,t,r){return n(e,"value"),i(e,"value"),this.currencyFormatter(t,r)(e)},e}))
                """;
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_UrlPrototype_ParseHost_Helper_NoErrors()
        {
            var input = """
                var x={parse:function(e,t,r){var i,o,a,u,s,l=this,c=t||ge,d=0,f="",p=!1,m=!1,y=!1;for(e=b(e),t||(l.scheme="",l.username="",l.password="",l.host=null,l.port=null,l.path=[],l.query=null,l.fragment=null,l.cannotBeABaseURL=!1,e=j(e,ne,""),e=j(e,ie,"$1")),e=j(e,oe,""),i=v(e);d<=i.length;){switch(o=i[d],c){case ge:if(!o||!D(G,o)){if(t)return W;c=ye;continue}f+=H(o),c=me;break;case me:if(o&&(D($,o)||"+"===o||"-"===o||"."===o))f+=H(o);else{if(":"!==o){if(t)return W;f="",c=ye,d=0;continue}if(t&&(l.isSpecial()!==h(fe,f)||"file"===f&&(l.includesCredentials()||null!==l.port)||"file"===l.scheme&&!l.host))return;if(l.scheme=f,t)return void(l.isSpecial()&&fe[l.scheme]===l.port&&(l.port=null));f="","file"===l.scheme?c=Ie:l.isSpecial()&&r&&r.scheme===l.scheme?c=be:l.isSpecial()?c=Ee:"/"===i[d+1]?(c=_e,d++):(l.cannotBeABaseURL=!0,z(l.path,""),c=De)}break;case Fe:o!==n&&(l.fragment+=de(o,se))}d++}},parseHost:function(e){var t,r,n;if("["===N(e,0)){if("]"!==N(e,e.length-1))return q;if(t=function(e){var t,r,n,i,o,a,u,s=[0,0,0,0,0,0,0,0],l=0,c=null,d=0,f=function(){return N(e,d)};if(":"===f()){if(":"!==N(e,1))return;d+=2,c=++l}for(;f();){if(8===l)return;if(":"!==f()){for(t=r=0;r<4&&D(ee,f());)t=16*t+O(f(),16),d++,r++;if("."===f()){if(0===r)return;if(d-=r,l>6)return;for(n=0;f();){if(i=null,n>0){if(!("."===f()&&n<4))return;d++}if(!D(Y,f()))return;for(;D(Y,f());){if(o=O(f(),10),null===i)i=o;else{if(0===i)return;i=10*i+o}if(i>255)return;d++}s[l]=256*s[l]+i,2!=++n&&4!==n||l++}if(4!==n)return;break}if(":"===f()){if(d++,!f())return}else if(f())return;s[l++]=t}else{if(null!==c)return;d++,c=++l}}if(null!==c)for(a=l-c,l=7;0!==l&&a>0;)u=s[l],s[l--]=s[c+a-1],s[c+--a]=u;else if(8!==l)return;return s}(B(e,1,-1)),!t)return q;this.host=t}};
                """;
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_ParenthesizedModuleMapProperty_WithNestedFunctionBodies_NoErrors()
        {
            var input = """
                ({1:(e,t,r)=>{const B=function(e,t,r,{pure:n,areStatesEqual:i=V,areOwnPropsEqual:o=F,areStatePropsEqual:a=F,areMergedPropsEqual:u=F,forwardRef:l=!1,context:c=f}={}){const d=c,h=function(e){return e?"function"==typeof e?I(e):O(e,"mapStateToProps"):C((()=>({})))}(e),p=function(e){return e&&"object"==typeof e?C((t=>function(e,t){const r={};for(const n in e){const i=e[n];"function"==typeof i&&(r[n]=(...e)=>t(i(...e)))}return r}(e,t))):e?"function"==typeof e?I(e):O(e,"mapDispatchToProps"):C((e=>({dispatch:e})))}(t),v=function(e){return e?"function"==typeof e?function(e){return function(t,{displayName:r,areMergedPropsEqual:n}){let i,o=!1;return function(t,r,a){const u=e(t,r,a);return o?n(u,i)||(i=u):(o=!0,i=u),i}}}(e):O(e,"mergeProps"):()=>P}(r),g=Boolean(e);return e=>{const t=e.displayName||e.name||"Component",r=`Connect(${t})`,n={shouldHandleStateChanges:g,displayName:r,wrappedComponentName:t,WrappedComponent:e,initMapStateToProps:h,initMapDispatchToProps:p,initMergeProps:v,areStatesEqual:i,areStatePropsEqual:a,areOwnPropsEqual:o,areMergedPropsEqual:u};function c(t){const[r,i,o]=s.useMemo((()=>{const{reactReduxForwardedRef:e}=t,r=(0,S.Z)(t,L);return[t.context,e,r]}),[t]),a=s.useMemo((()=>r&&r.Consumer&&(0,R.isContextConsumer)(s.createElement(r.Consumer,null))?r:d),[r,d]),u=s.useContext(a),l=Boolean(t.store)&&Boolean(t.store.getState)&&Boolean(t.store.dispatch),c=Boolean(u)&&Boolean(u.store);const f=l?t.store:u.store,h=c?u.getServerState:f.getState,p=s.useMemo((()=>function(e,t){let{initMapStateToProps:r,initMapDispatchToProps:n,initMergeProps:i}=t,o=(0,S.Z)(t,k);return x(r(e,o),n(e,o),i(e,o),e,o)}(f.dispatch,n)),[f]),[v,m]=s.useMemo((()=>{if(!g)return j;const e=N(f,l?void 0:u.subscription),t=e.notifyNestedSubs.bind(e);return[e,t]}),[f,l,u]),y=s.useMemo((()=>l?u:(0,_.Z)({},u,{subscription:v})),[l,u,v]),b=s.useRef(),w=s.useRef(o),E=s.useRef(),C=s.useRef(!1),T=(s.useRef(!1),s.useRef(!1)),I=s.useRef();D((()=>(T.current=!0,()=>{T.current=!1})),[]);const O=s.useMemo((()=>()=>E.current&&o===w.current?E.current:p(f.getState(),o)),[f,o]),P=s.useMemo((()=>e=>v?function(e,t,r,n,i,o,a,u,s,l,c){if(!e)return()=>{};let d=!1,f=null;const h=()=>{if(d||!u.current)return;const e=t.getState();let r,h;try{r=n(e,i.current)}catch(e){h=e,f=e}h||(f=null),r===o.current?a.current||l():(o.current=r,s.current=r,a.current=!0,c())};return r.onStateChange=h,r.trySubscribe(),h(),()=>{if(d=!0,r.tryUnsubscribe(),r.onStateChange=null,f)throw f}}(g,f,v,p,w,b,C,T,E,m,e):()=>{}),[v]);var A,M,F;let V;A=U,M=[w,b,C,o,E,m],D((()=>A(...M)),F);try{V=z(P,O,h?()=>p(h(),o):O)}catch(e){throw I.current&&(e.message+=`\nThe error may be correlated with this previous error:\n${I.current.stack}\n\n`),e}D((()=>{I.current=void 0,E.current=void 0,b.current=V}));const B=s.useMemo((()=>s.createElement(e,(0,_.Z)({},V,{ref:i}))),[i,e,V]);return s.useMemo((()=>g?s.createElement(a.Provider,{value:y},B):B),[a,B,y])}const f=s.memo(c);if(f.WrappedComponent=e,f.displayName=c.displayName=r,l){const t=s.forwardRef((function(e,t){return s.createElement(f,(0,_.Z)({},e,{reactReduxForwardedRef:t}))}));return t.displayName=r,t.WrappedComponent=e,E()(t,e)}return E()(f,e)}};const H=function({store:e,context:t,children:r,serverState:n,stabilityCheck:i="once",noopCheck:o="once"}){const a=s.useMemo((()=>{const t=N(e);return{store:e,subscription:t,getServerState:n?()=>n:void 0,stabilityCheck:i,noopCheck:o}}),[e,n,i,o]),u=s.useMemo((()=>e.getState()),[e]);D((()=>{const{subscription:t}=a;return t.onStateChange=t.notifyNestedSubs,t.trySubscribe(),u!==e.getState()&&t.notifyNestedSubs(),()=>{t.tryUnsubscribe(),t.onStateChange=void 0}}),[a,u]);const l=t||f;return s.createElement(l.Provider,{value:a},r)}})
                """;
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_TemplateInterpolationFollowedByBlockStatement_InReturnedArrowBody_NoErrors()
        {
            var input = """
                ({1:(e,t,r)=>{return e=>{const r=`Connect(${t})`;if(l){return y}return z}}});
                """;
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_RuntimeMode_IfWithEmptyConsequentElseBlock_NoErrors()
        {
            var input = """
                function hasInlineImage(e,t){if(!c(t))return!1;const{editorStateRaw:n}=e;if(n)for(const e of n.blocks){var i;if(null!==(i=e.inlineStyleRanges)&&void 0!==i&&i.length)return!0;if(e.type!==r.UP);else{const t=e.entityRanges;if(!Array.isArray(t)||!t.length)continue;const[i]=t,r=String(i.key),s=n.entityMap[r];if(!s)continue;if(s.type===o.Z.INLINE_IMAGE)return!0}}return p}
                """;
            var parser = CreateRuntimeParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_RuntimeMode_ArrowFunction_WithEmptyConsequentElseBlock_NoErrors()
        {
            var input = """
                const scrollTo=(e,t,r)=>{if("number"==typeof e);else{var n=e||x;e=n.x,t=n.y,r=n.animated}return [e,t,r]}
                """;
            var parser = CreateRuntimeParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_LatestVendorFile_NoErrors()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "vendor.f1dc7e4a.latest.js");
            var input = File.ReadAllText(path);
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_XVendor_AssignmentToIifeIndexedResult_NoErrors()
        {
            var input = """
                function o(e){return A[0|e]}
                function i(e){return e}
                function r(e,t){return 0}
                function u(e){var t=[];return t[se]=WK[ue][e],void 0!==t[se]?t[se]:WK[ue][e]=function(e){var t=[];t[se]=ce,t[de]=e;for(var n=se;n<t[de].length;n+=ge)e=((e=((e=((e=r(t[de].substr(n,ge),fe))^ve)&he)>>>pe|e<<fe-pe)&he)>>>Z|e<<fe-Z)&he,t[se]+=i(e);return t[se]}(e)}
                function s(e){var t=[];return t[se]=WK[le][e],void 0!==t[se]?t[se]:WK[le][e]=function(e){return e}(e)}
                """;
            var parser = CreateParser(input);
            parser.ParseProgram();

            AssertNoErrors(parser);
        }
    }
}



