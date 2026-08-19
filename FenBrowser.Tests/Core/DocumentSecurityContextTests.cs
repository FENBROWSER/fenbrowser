using System;
using FenBrowser.Core;
using FenBrowser.Core.Security;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class DocumentSecurityContextTests
{
    [Fact]
    public void SharedMemoryCapabilityRequiresSecureCrossOriginIsolation()
    {
        var notIsolated = DocumentSecurityContext.CreateTopLevel(
            new Uri("https://app.example/"),
            null,
            PermissionsPolicy.None,
            new CrossOriginIsolationPolicy());
        var isolatedPolicy = new CrossOriginIsolationPolicy();
        isolatedPolicy.ParseCoopHeader("same-origin");
        isolatedPolicy.ParseCoepHeader("require-corp");
        var isolated = DocumentSecurityContext.CreateTopLevel(
            new Uri("https://app.example/"),
            null,
            PermissionsPolicy.None,
            isolatedPolicy);

        Assert.False(notIsolated.Allows(SandboxFeature.SharedArrayBuffer));
        Assert.True(isolated.Allows(SandboxFeature.SharedArrayBuffer));
    }
}
