using System;
using FenBrowser.FenEngine.Core;

namespace FenBrowser.FenEngine.Tests
{
    class Program
    {
        static void MainTest(string[] args)
        {
            Console.WriteLine("=== FenEngine Test Suite ===\n");

            var runtime = new FenRuntime();

            // Test 1: Basic expressions
            Console.WriteLine("Test 1: Basic Arithmetic");
            Test(runtime, "5 + 3", "8");
            Test(runtime, "10 - 4", "6");
            Test(runtime, "3 * 4", "12");
            Test(runtime, "20 / 5", "4");

            // Test 2: Variable declarations
            Console.WriteLine("\nTest 2: Variable Declarations");
            Test(runtime, "let x = 5", "5");
            Test(runtime, "x", "5");
            Test(runtime, "let y = x + 3", "8");
            Test(runtime, "y", "8");

            // Test 3: String operations
            Console.WriteLine("\nTest 3: String Operations");
            Test(runtime, "let greeting = \"Hello\"", "Hello");
            Test(runtime, "let world = \" World\"", " World");
            Test(runtime, "greeting + world", "Hello World");

            // Test 4: Boolean operations
            Console.WriteLine("\nTest 4: Boolean Operations");
            Test(runtime, "true", "True");
            Test(runtime, "false", "False");
            Test(runtime, "!true", "False");
            Test(runtime, "5 > 3", "True");
            Test(runtime, "5 < 3", "False");

            // Test 5: If expressions
            Console.WriteLine("\nTest 5: If Expressions");
            Test(runtime, "if (true) { 10 }", "10");
            Test(runtime, "if (false) { 10 } else { 20 }", "20");
            Test(runtime, "if (5 > 3) { 100 } else { 200 }", "100");

            // Test 6: Functions
            Console.WriteLine("\nTest 6: Functions");
            Test(runtime, "let add = function(a, b) { return a + b }", "anonymous");
            Test(runtime, "add(5, 3)", "8");
            Test(runtime, "let multiply = function(x, y) { return x * y }", "anonymous");
            Test(runtime, "multiply(4, 5)", "20");

            // Test 7: Console.log (should print to console)
            Console.WriteLine("\nTest 7: Console.log");
            runtime.ExecuteSimple("console.log(\"FenEngine is working!\")");
            runtime.ExecuteSimple("console.log(42)");
            runtime.ExecuteSimple("console.log(true)");

            Console.WriteLine("\n=== All Tests Complete ===");
            Console.ReadLine();
        }

        static void Test(FenRuntime runtime, string code, string expected)
        {
            try
            {
                var result = runtime.ExecuteSimple(code);
                var actual = result.ToString();
                var status = actual == expected ? "✓ PASS" : "✗ FAIL";
                Console.WriteLine($"{status}: {code}");
                if (actual != expected)
                {
                    Console.WriteLine($"  Expected: {expected}");
                    Console.WriteLine($"  Got: {actual}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ ERROR: {code}");
                Console.WriteLine($"  {ex.Message}");
            }
        }
    }
}
