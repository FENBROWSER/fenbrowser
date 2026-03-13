using System;
using System.IO;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Testing;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class Test262HostIntegrationTests
    {
        [Fact]
        public async Task RunSingleTestAsync_IsHtmlDdaTypeofSemantics_Passes()
        {
            var runner = new Test262Runner(GetTest262Root(), timeoutMs: 20_000);
            string testFile = Path.Combine(GetTest262Root(), "test", "annexB", "language", "expressions", "typeof", "emulates-undefined.js");

            var result = await runner.RunSingleTestAsync(testFile);

            Assert.True(result.Passed, result.Error);
        }

        [Fact]
        public async Task RunSingleTestAsync_CreateRealmCtorRealmPrototypeSelection_Passes()
        {
            var runner = new Test262Runner(GetTest262Root(), timeoutMs: 20_000);
            string testFile = Path.Combine(GetTest262Root(), "test", "built-ins", "Boolean", "proto-from-ctor-realm.js");

            var result = await runner.RunSingleTestAsync(testFile);

            Assert.True(result.Passed, result.Error);
        }

        [Fact]
        public async Task RunSingleTestAsync_EvalScriptExceptionMapping_Passes()
        {
            string test262Root = GetTest262Root();
            string localDir = Path.Combine(test262Root, "test", "local-host");
            string testFile = Path.Combine(localDir, $"eval-script-{Guid.NewGuid():N}.js");
            Directory.CreateDirectory(localDir);

            File.WriteAllText(testFile, """
/*---
description: host evalScript should surface JS-visible exception types
---*/

assert.throws(TypeError, function() {
  $262.evalScript("throw new TypeError('boom')");
});
""");

            try
            {
                var runner = new Test262Runner(test262Root, timeoutMs: 20_000);
                var result = await runner.RunSingleTestAsync(testFile);

                Assert.True(result.Passed, result.Error);
            }
            finally
            {
                try { File.Delete(testFile); } catch { }
            }
        }

        [Fact]
        public async Task RunSingleTestAsync_GlobalEvalScriptFunctionDeclaration_CreatesGlobalProperty()
        {
            var runner = new Test262Runner(GetTest262Root(), timeoutMs: 20_000);
            string testFile = Path.Combine(GetTest262Root(), "test", "language", "global-code", "script-decl-func.js");

            var result = await runner.RunSingleTestAsync(testFile);

            Assert.True(result.Passed, result.Error);
        }

        [Fact]
        public async Task RunSingleTestAsync_GlobalEvalScriptLexicalCollision_ThrowsSyntaxErrorWithoutLeakingBindings()
        {
            var runner = new Test262Runner(GetTest262Root(), timeoutMs: 20_000);
            string testFile = Path.Combine(GetTest262Root(), "test", "annexB", "language", "global-code", "script-decl-lex-collision.js");

            var result = await runner.RunSingleTestAsync(testFile);

            Assert.True(result.Passed, result.Error);
        }

        [Fact]
        public async Task RunSingleTestAsync_ArrayFromAsync_ArrayLikePromiseValues_Passes()
        {
            var runner = new Test262Runner(GetTest262Root(), timeoutMs: 20_000);
            string testFile = Path.Combine(
                GetTest262Root(),
                "test",
                "built-ins",
                "Array",
                "fromAsync",
                "asyncitems-arraylike-promise.js");

            var result = await runner.RunSingleTestAsync(testFile);

            Assert.True(result.Passed, result.Error);
        }

        [Fact]
        public async Task RunSingleTestAsync_ArrayFrom_IsHtmlDdaIteratorMethod_ThrowsTypeError()
        {
            var runner = new Test262Runner(GetTest262Root(), timeoutMs: 20_000);
            string testFile = Path.Combine(
                GetTest262Root(),
                "test",
                "annexB",
                "built-ins",
                "Array",
                "from",
                "iterator-method-emulates-undefined.js");

            var result = await runner.RunSingleTestAsync(testFile);

            Assert.True(result.Passed, result.Error);
        }

        [Fact]
        public async Task RunSingleTestAsync_AnnexB_GlobalIfDeclElseStmtEvalGlobalBlockScoping_Passes()
        {
            var runner = new Test262Runner(GetTest262Root(), timeoutMs: 20_000);
            string testFile = Path.Combine(
                GetTest262Root(),
                "test",
                "annexB",
                "language",
                "eval-code",
                "direct",
                "global-if-decl-else-stmt-eval-global-block-scoping.js");

            var result = await runner.RunSingleTestAsync(testFile);

            Assert.True(result.Passed, result.Error);
        }

        [Fact]
        public async Task RunSingleTestAsync_RegExpLegacyAccessor_LeftContext_CrossRealmConstructor_Passes()
        {
            var runner = new Test262Runner(GetTest262Root(), timeoutMs: 20_000);
            string testFile = Path.Combine(
                GetTest262Root(),
                "test",
                "annexB",
                "built-ins",
                "RegExp",
                "legacy-accessors",
                "leftContext",
                "this-cross-realm-constructor.js");

            var result = await runner.RunSingleTestAsync(testFile);

            Assert.True(result.Passed, result.Error);
        }

        [Fact]
        public async Task RunSingleTestAsync_RegExpLegacyAccessor_InvalidReceiver_ThrowsTypeError()
        {
            var runner = new Test262Runner(GetTest262Root(), timeoutMs: 20_000);
            string testFile = Path.Combine(
                GetTest262Root(),
                "test",
                "annexB",
                "built-ins",
                "RegExp",
                "legacy-accessors",
                "index",
                "this-not-regexp-constructor.js");

            var result = await runner.RunSingleTestAsync(testFile);

            Assert.True(result.Passed, result.Error);
        }

        [Fact]
        public void DiscoverTests_ExcludesLocalDebugFiles()
        {
            var runner = new Test262Runner(GetTest262Root(), timeoutMs: 20_000);

            var tests = runner.DiscoverTests();

            Assert.DoesNotContain(tests, path => path.EndsWith(Path.Combine("annexB", "debug_bold.js"), StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(tests, path => path.EndsWith(Path.Combine("annexB", "built-ins", "Date", "prototype", "getYear", "custom-test.js"), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(tests, path => path.EndsWith(Path.Combine("annexB", "built-ins", "Array", "from", "iterator-method-emulates-undefined.js"), StringComparison.OrdinalIgnoreCase));
        }

        private static string GetTest262Root()
        {
            string root = FindRepositoryRoot();
            string test262Root = Path.Combine(root, "test262");
            if (!Directory.Exists(test262Root))
            {
                throw new InvalidOperationException($"Missing Test262 root: {test262Root}");
            }

            return test262Root;
        }

        private static string FindRepositoryRoot()
        {
            string current = AppContext.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                if (File.Exists(Path.Combine(current, "FenBrowser.sln")))
                {
                    return current;
                }

                var parent = Directory.GetParent(current);
                if (parent == null)
                {
                    break;
                }

                current = parent.FullName;
            }

            throw new InvalidOperationException("Could not locate repository root from test base directory.");
        }
    }
}
