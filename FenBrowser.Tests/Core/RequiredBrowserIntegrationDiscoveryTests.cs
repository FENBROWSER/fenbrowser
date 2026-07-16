using System.Reflection;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class RequiredBrowserIntegrationDiscoveryTests
{
    private static readonly (string TypeName, string MethodName)[] RequiredTests =
    [
        ("FenBrowser.Tests.Engine.EventLoopTraceTests", "FenJsBrowserTimers_WriteTaskTimerRafAndMicrotaskTrace"),
        ("FenBrowser.Tests.Scripting.CallbackFailureDiagnosticsTests", "ThrowingTimer_PreservesTypedFailureProvenance"),
        ("FenBrowser.Tests.Scripting.CallbackFailureDiagnosticsTests", "ThrowingEventListener_PreservesTypedFailureProvenance"),
        ("FenBrowser.Tests.Scripting.CallbackFailureDiagnosticsTests", "RejectedPromise_PreservesTypedFailureProvenance"),
        ("FenBrowser.Tests.Scripting.CallbackFailureDiagnosticsTests", "HotTimerHelper_InOperatorAcceptsHostObjectAfterJitTierUp"),
        ("FenBrowser.Tests.Scripting.CallbackFailureDiagnosticsTests", "RepeatingTimerFailures_PreserveTimerIdentityAndOrder"),
        ("FenBrowser.Tests.Scripting.CallbackFailureDiagnosticsTests", "CallbackFailureRecords_AreBoundedWithoutLosingTotalCount"),
        ("FenBrowser.Tests.Scripting.CallbackFailureDiagnosticsTests", "SecretLikeCallbackFailureText_IsRedacted"),
        ("FenBrowser.Tests.Scripting.CallbackFailureDiagnosticsTests", "CallbackFailureMessageAndStacks_AreBounded"),
        ("FenBrowser.Tests.Logging.EngineLogSettingsTests", "Flush_DrainsAcceptedEventsBeforeArtifactCopy"),
        ("FenBrowser.Tests.Scripting.BrowserLifecycleDetailTests", "TimedOutEventLoopSample_IsExplicitlyHistorical"),
        ("FenBrowser.Tests.Scripting.BrowserFormInteractionAcceptanceTests", "WebDriverClickTypeAndPreventedSubmit_UsesOrdinaryInputPipeline"),
        ("FenBrowser.Tests.Scripting.BrowserFormInteractionAcceptanceTests", "WebDriverSubmitWithoutCancellation_NavigatesWithSuccessfulControls"),
        ("FenBrowser.Tests.Core.RendererIpcMetadataTests", "BrokerAllowlist_AcceptsMetadataChanged"),
        ("FenBrowser.Tests.Core.RendererIsolationPoliciesTests", "RendererTabIsolationRegistry_UnexpectedExit_SchedulesRestartWithReplay"),
        ("FenBrowser.Tests.Architecture.RendererChildLoopIoTests", "ReadLineWithTimeoutAsync_ReusesPendingRead_AcrossTimeoutPolls")
    ];

    [Fact]
    public void RequiredBrowserIntegrationContracts_AreCompiledAsDiscoverableXunitTests()
    {
        var assembly = typeof(RequiredBrowserIntegrationDiscoveryTests).Assembly;
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
            "Required browser-integration tests are not compiled/discoverable: " +
            string.Join(", ", missing));
    }
}
