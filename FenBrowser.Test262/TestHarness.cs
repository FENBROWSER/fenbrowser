using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Types;
using NUnit.Framework;

namespace FenBrowser.Test262
{
    public class TestHarness
    {
        private static string _suiteDirectory;
        private static string _harnessDirectory;

        public static void Configure(string suiteDirectory)
        {
            _suiteDirectory = suiteDirectory;
            _harnessDirectory = Path.Combine(_suiteDirectory, "harness");
        }

        public static async Task ExecuteTestAsync(string testFile, string content, bool isModule, bool isAsync, string[] includes, string negativeType = null)
        {
            // 1. Create a fresh runtime (Realm)
            var runtime = new FenRuntime();
            
            // 2. Setup $262 and print
            SetupGlobals(runtime);

            // 3. Load Harness
            // assert.js and sta.js are standard unless "raw" (but we'll handle raw in caller or here)
            // For now assuming standard test
            await LoadHarnessFile(runtime, "assert.js");
            await LoadHarnessFile(runtime, "sta.js");

            // 4. Load Includes
            if (includes != null)
            {
                foreach (var include in includes)
                {
                    await LoadHarnessFile(runtime, include);
                }
            }

            // 5. Execute Test
            bool isError = false;
            string errorMsg = "";

            try 
            {
                if (isAsync)
                {
                   // Hook $DONE
                   var doneTcs = new TaskCompletionSource<bool>();
                   
                   var doneFn = new FenFunction("$DONE", (args, thisVal) => 
                   {
                       if (args.Length > 0 && !args[0].IsUndefined && !args[0].IsNull)
                       {
                           var err = args[0].ToString();
                           doneTcs.TrySetException(new Exception(err));
                       }
                       else
                       {
                           doneTcs.TrySetResult(true);
                       }
                       return FenValue.Undefined;
                   });
                   runtime.GlobalEnv.Set("$DONE", FenValue.FromFunction(doneFn));
                   
                   var res = runtime.ExecuteSimple(content, testFile);
                   
                   // Check for synchronous error (e.g. syntax) in async test
                   if (res is FenValue fvAsync && fvAsync.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Error)
                   {
                        throw new Exception(res.ToString());
                   }
                   
                   // Wait for done
                   // Add timeout
                   var completedTask = await Task.WhenAny(doneTcs.Task, Task.Delay(1500));
                   if (completedTask != doneTcs.Task)
                   {
                       throw new TimeoutException("Test timed out waiting for $DONE");
                   }
                   await doneTcs.Task; // Propagate exception if any
                }
                else
                {
                   var result = runtime.ExecuteSimple(content, testFile);
                   if (result is FenValue fvSync && fvSync.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Error)
                   {
                       isError = true;
                       errorMsg = result.ToString();
                   }
                }
            }
            catch (Exception ex)
            {
                isError = true;
                errorMsg = ex.Message;
            }

            // Validation
            if (!string.IsNullOrEmpty(negativeType))
            {
                if (!isError)
                {
                     throw new Exception($"Negative test failed: Expected error '{negativeType}' but no error occurred.");
                }
                // Check if error message contains the expected type
                if (!errorMsg.Contains(negativeType))
                {
                     throw new Exception($"Negative test failed: Expected error '{negativeType}' but got '{errorMsg}'");
                }
                return; // Passed
            }

            if (isError)
            {
                throw new Exception($"Test failed: {errorMsg}");
            }
        }

        private static async Task LoadHarnessFile(FenRuntime runtime, string fileName)
        {
            var path = Path.Combine(_harnessDirectory, fileName);
            if (File.Exists(path))
            {
                var code = await File.ReadAllTextAsync(path);
                runtime.ExecuteSimple(code, fileName);
            }
        }

        private static void SetupGlobals(FenRuntime runtime)
        {
            // print function
            var printFn = new FenFunction("print", (args, thisVal) => 
            {
                var msg = args.Length > 0 ? args[0].ToString() : "";
                TestContext.Out.WriteLine(msg); // NUnit output
                return FenValue.Undefined;
            });
            runtime.GlobalEnv.Set("print", FenValue.FromFunction(printFn));

            // $262 object
            var d262 = new FenObject();
            
            // $262.createRealm
            d262.Set("createRealm", FenValue.FromFunction(new FenFunction("createRealm", (args, thisVal) => 
            {
                // We need to return an object that has .evalScript, .global, .createRealm
                // This implies a nested runtime or simulating one.
                // For simplicity/MVP, we might throw or return a stub if tests rely on it heavily.
                // Many simple tests dont use it.
                throw new NotImplementedException("$262.createRealm not yet implemented");
            })));
            
            // $262.global - reference to global object
            // FenRuntime initializes window/globalThis. We can try to retrieve it or use the GlobalEnv.
            // Since GlobalEnv.Get returns FenValue, we can keys to find the global object reference if strictly needed,
            // but usually usage is `var g = $262.global; g.foo = 1;`. 
            // The global environment record in FenEngine might not expose the "binding object" directly as a named property except via `globalThis`.
            var globalThis = runtime.GlobalEnv.Get("globalThis");
            if (globalThis.IsObject)
            {
                 d262.Set("global", globalThis);
            }
            else
            {
                 // Fallback if globalThis isn't set yet (though it should be)
                 d262.Set("global", FenValue.Undefined); 
            }
            
            // $262.evalScript
             d262.Set("evalScript", FenValue.FromFunction(new FenFunction("evalScript", (args, thisVal) => 
            {
                if (args.Length > 0)
                {
                    var script = args[0].ToString();
                    return (FenValue)runtime.ExecuteSimple(script);
                }
                return FenValue.Undefined;
            })));
            
            // $262.gc
             d262.Set("gc", FenValue.FromFunction(new FenFunction("gc", (args, thisVal) => 
             {
                 GC.Collect();
                 return FenValue.Undefined;
             })));
             
             // $262.detachArrayBuffer
             d262.Set("detachArrayBuffer", FenValue.FromFunction(new FenFunction("detachArrayBuffer", (args, thisVal) => 
             {
                 // Implement if FenObject supports it
                 return FenValue.Undefined;
             })));

            runtime.GlobalEnv.Set("$262", FenValue.FromObject(d262));
            runtime.GlobalEnv.Set("$262", FenValue.FromObject(d262)); // Set explicitly
        }
    }
}
