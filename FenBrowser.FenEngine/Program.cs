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
            else
            {
                Console.WriteLine("Usage: FenBrowser.FenEngine.exe verify <html_path>");
            }
        }
    }
}
