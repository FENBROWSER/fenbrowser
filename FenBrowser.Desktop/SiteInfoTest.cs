using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Network;
using System.IO;
using System.Text;

namespace FenBrowser.Desktop
{
    public static class SiteInfoTest
    {
        public static async Task RunAsync()
        {
            var sb = new StringBuilder();
            void Log(string msg)
            {
                Console.WriteLine(msg);
                sb.AppendLine(msg);
            }

            Log("Running Site Info Tests...");

            // 1. Verify Default Settings
            if (!BrowserSettings.Instance.EnableTrackingPrevention)
            {
                Log("FAIL: EnableTrackingPrevention should be true by default.");
                File.WriteAllText("test_results.txt", sb.ToString());
                return;
            }
            Log("PASS: Default EnableTrackingPrevention is true.");

            // 2. Verify Blocking
            var resourceManager = new ResourceManager(new System.Net.Http.HttpClient());

            var blockedUrl = "http://doubleclick.net/ads";

            Log($"Initial Blocked Count: {resourceManager.BlockedRequestCount}");

            try
            {
                await resourceManager.FetchTextAsync(new Uri(blockedUrl));
            }
            catch
            {
            }
            
            if (resourceManager.BlockedRequestCount == 1)
            {
                Log("PASS: BlockedRequestCount incremented for blocked domain.");
            }
            else
            {
                Log($"FAIL: BlockedRequestCount is {resourceManager.BlockedRequestCount}, expected 1.");
            }

            // 3. Verify Disable Blocking
            BrowserSettings.Instance.EnableTrackingPrevention = false;
            Log("Disabled Tracking Prevention.");

            try
            {
                await resourceManager.FetchTextAsync(new Uri(blockedUrl));
            }
            catch { }

            if (resourceManager.BlockedRequestCount == 1)
            {
                Log("PASS: BlockedRequestCount did not increment when protection disabled.");
            }
            else
            {
                Log($"FAIL: BlockedRequestCount is {resourceManager.BlockedRequestCount}, expected 1.");
            }

            // Restore settings
            BrowserSettings.Instance.EnableTrackingPrevention = true;
            
            File.WriteAllText("test_results.txt", sb.ToString());
        }
    }
}
