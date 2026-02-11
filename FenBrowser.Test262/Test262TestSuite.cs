using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Testing;
using NUnit.Framework;

namespace FenBrowser.Test262
{
    [TestFixture]
    [Parallelizable(ParallelScope.Children)]
    public class Test262TestSuite
    {
        // Directory to scan for tests (relative to test262 root)
        private const string TEST_ROOT = "test/language/literals/numeric"; 
        
        [OneTimeSetUp]
        public void Setup()
        {
            // Configure harness with path to test262 root
            // We assume the test262 folder is at C:\Users\udayk\Videos\FENBROWSER\test262 based on previous steps
            // Ideally should read from settings or env var
            var suitePath = @"C:\Users\udayk\Videos\FENBROWSER\test262";
            TestHarness.Configure(suitePath);
        }

        [TestCaseSource(nameof(GetTestCases))]
        public async Task RunTest262(string testFile, string content, bool isModule, bool isAsync, string[] includes, string negativeType)
        {
            await TestHarness.ExecuteTestAsync(testFile, content, isModule, isAsync, includes, negativeType);
        }

        public static IEnumerable<TestCaseData> GetTestCases()
        {
            var suitePath = @"C:\Users\udayk\Videos\FENBROWSER\test262";
            var testRoot = Path.Combine(suitePath, TEST_ROOT);
            
            if (!Directory.Exists(testRoot))
            {
               yield break;
            }

            var files = Directory.GetFiles(testRoot, "*.js", SearchOption.AllDirectories);
            
            foreach (var file in files)
            {
                // Skip fixtures/helpers
                if (Path.GetFileName(file).StartsWith("_") || file.Contains("_FIXTURE"))
                    continue;

                // Skip Intl and AnnexB for now to focus on core language/built-ins
                if (file.Contains("intl402") || file.Contains("annexB"))
                    continue;

                string content;
                try 
                {
                    content = File.ReadAllText(file);
                }
                catch
                {
                    continue; // Skip file if cannot read
                }
                
                var meta = Test262Runner.ParseMetadata(content);
                
                // Filtering
                if (meta.Features.Contains("Atomics") || 
                    meta.Features.Contains("Temporal") ||
                    meta.Features.Contains("ShadowRealm"))
                    continue;
                    
                // Skip if negative test (expected to fail)
                // For now, we only want to ensure our engine doesn't crash or fail basic tests.
                // If it's a negative test, handling "success" is tricky in this loop without more logic.
                // We'll let them run and fail if exception matches.
                
                string negativeType = meta.Negative ? meta.NegativeType : null;
                var testCase = new TestCaseData(file, content, false, meta.IsAsync, meta.Includes.ToArray(), negativeType);
                testCase.SetName(Path.GetRelativePath(suitePath, file));
                
                // Categories
                testCase.SetCategory("Test262");
                if (meta.IsAsync) testCase.SetCategory("Async");
                
                yield return testCase;
            }
        }
    }
}
