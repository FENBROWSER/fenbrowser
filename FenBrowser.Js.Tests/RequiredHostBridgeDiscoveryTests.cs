using System.Reflection;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class RequiredHostBridgeDiscoveryTests
{
    private static readonly (string TypeName, string MethodName)[] RequiredTests =
    [
        ("FenBrowser.Js.Tests.HostObjectIntegrationTests", "StaleGenerationThrowsTypeError"),
        ("FenBrowser.Js.Tests.HostObjectTableTests", "FreeMarksSlotInvalidAndRecyclesWithNewGeneration")
    ];

    [Fact]
    public void RequiredStaleHandleContracts_AreCompiledAsDiscoverableXunitTests()
    {
        var assembly = typeof(RequiredHostBridgeDiscoveryTests).Assembly;
        var missing = new List<string>();

        foreach (var required in RequiredTests)
        {
            var type = assembly.GetType(required.TypeName, throwOnError: false);
            var method = type?.GetMethod(
                required.MethodName,
                BindingFlags.Instance | BindingFlags.Public);
            var isXunitTest = method?.GetCustomAttributes(inherit: true)
                .Any(attribute => attribute is FactAttribute) == true;

            if (!isXunitTest)
            {
                missing.Add($"{required.TypeName}.{required.MethodName}");
            }
        }

        Assert.True(
            missing.Count == 0,
            "Required host-bridge tests are not compiled/discoverable: " +
            string.Join(", ", missing));
    }
}
