using System.Net.Http;
using FenBrowser.Tooling;

namespace FenBrowser.Tests.Tooling;

public sealed class DebugSiteNetworkHeaderRedactionTests
{
    [Fact]
    public void CaptureHeaders_RedactsCredentialsAndPreservesSafeMetadata()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");
        request.Headers.TryAddWithoutValidation("Cookie", "session=secret");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");
        request.Headers.TryAddWithoutValidation("X-Trace-Id", "trace-123");

        var requestHeaders = Program.CaptureDebugSiteHeaders(request.Headers);

        Assert.Equal("[redacted]", requestHeaders["Cookie"]);
        Assert.Equal("[redacted]", requestHeaders["Authorization"]);
        Assert.Equal("trace-123", requestHeaders["X-Trace-Id"]);

        using var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session=secret; Secure");
        response.Headers.TryAddWithoutValidation("X-Api-Key", "secret");

        var responseHeaders = Program.CaptureDebugSiteHeaders(response.Headers);

        Assert.Equal("[redacted]", responseHeaders["Set-Cookie"]);
        Assert.Equal("[redacted]", responseHeaders["X-Api-Key"]);
    }
}
