using System;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Testing;

namespace FenBrowser.FenEngine
{
    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "verify")
            {
                await VerificationRunner.GenerateSnapshot(args[1], "verification_output.png");
            }
            else if (args.Length >= 2 && args[0] == "test262")
            {
                string testFile = args[1];
                string rootPath = args.Length > 2 ? args[2] : @"C:\Users\udayk\test262";
                
                if (!File.Exists(testFile))
                {
                    Console.WriteLine($"Error: Test file not found: {testFile}");
                    return;
                }
                
                Console.WriteLine($"Running Test262: {Path.GetFileName(testFile)}");
                var runner = new Test262Runner(rootPath);
                var result = await runner.RunSingleTestAsync(testFile);
                
                Console.WriteLine($"Result: {(result.Passed ? "PASS" : "FAIL")}");
                if (!result.Passed)
                {
                    Console.WriteLine($"  Expected: {result.Expected}");
                    Console.WriteLine($"  Actual:   {result.Actual}");
                    if (!string.IsNullOrEmpty(result.Error))
                        Console.WriteLine($"  Error:    {result.Error}");
                }
                Console.WriteLine($"Duration: {result.Duration.TotalMilliseconds}ms");
            }
            else if (args.Length >= 1 && args[0] == "test")
            {
                FenBrowser.FenEngine.Tests.LogicTestRunner.MainTest(args);
            }
            else
            {
                Console.WriteLine("Usage: FenBrowser.FenEngine.exe verify <html_path>");
                Console.WriteLine("       FenBrowser.FenEngine.exe test262 <test_file_path> [test262_root_path]");
                Console.WriteLine("       FenBrowser.FenEngine.exe test");
            }
        }
    }
}
