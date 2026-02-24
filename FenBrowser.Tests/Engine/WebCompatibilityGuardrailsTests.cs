using System;
using System.IO;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class WebCompatibilityGuardrailsTests
    {
        [Fact]
        public void CoreEnginePaths_MustNotContainSiteSpecificCompatibilitySignatures()
        {
            string root = FindRepositoryRoot();
            string cssLoaderPath = Path.Combine(root, "FenBrowser.FenEngine", "Rendering", "Css", "CssLoader.cs");
            string layoutPath = Path.Combine(root, "FenBrowser.FenEngine", "Layout", "MinimalLayoutComputer.cs");
            string uaStylePath = Path.Combine(root, "FenBrowser.FenEngine", "Rendering", "UserAgent", "UAStyleProvider.cs");

            Assert.True(File.Exists(cssLoaderPath), $"Missing file: {cssLoaderPath}");
            Assert.True(File.Exists(layoutPath), $"Missing file: {layoutPath}");
            Assert.True(File.Exists(uaStylePath), $"Missing file: {uaStylePath}");

            string cssLoader = File.ReadAllText(cssLoaderPath);
            string layout = File.ReadAllText(layoutPath);
            string uaStyle = File.ReadAllText(uaStylePath);

            string[] forbidden =
            {
                "whatismybrowser",
                "google.com",
                "FPdoLc",
                "lJ9FBc",
                "XDyW0e",
                "nDcEnd",
                "BKRPef",
                "pHiOh",
                "ayzqOc",
                "c93Gbe",
                "lnXdpd",
                "gb_A",
                "Sign in"
            };

            foreach (string token in forbidden)
            {
                Assert.DoesNotContain(token, cssLoader, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(token, layout, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(token, uaStyle, StringComparison.OrdinalIgnoreCase);
            }
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
