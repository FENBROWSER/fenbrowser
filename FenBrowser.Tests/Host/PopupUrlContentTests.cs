using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Host;
using Xunit;

namespace FenBrowser.Tests.Host;

/// <summary>
/// URL popups load their target URL into the popup window instead of sitting on
/// the "Loading {url}..." placeholder forever (window.open failure surface).
/// </summary>
public sealed class PopupUrlContentTests
{
    [Fact]
    public async Task NavigationManager_UnresolvableHost_FailsFastWithConnectionFailed()
    {
        using var http = new HttpClient();
        var resources = new ResourceManager(http, isPrivate: true);
        var navigation = new NavigationManager(resources);
        var stopwatch = Stopwatch.StartNew();

        var result = await navigation.NavigateAsync("https://popup.test/", NavigationRequestKind.Programmatic);

        stopwatch.Stop();
        Assert.NotEqual(FetchStatus.Success, result.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"Expected fast DNS failure, took {stopwatch.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public void RenderPopupErrorHtml_ConnectionFailed_RendersReachabilityError()
    {
        var html = HostDialogCoordinator.RenderPopupErrorHtml(
            "https://popup.test/",
            new FetchResult
            {
                Status = FetchStatus.ConnectionFailed,
                ErrorDetail = "No such host is known."
            });

        Assert.Contains("can't reach this page", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No such host is known.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderPopupErrorHtml_Timeout_RendersTimeoutError()
    {
        var html = HostDialogCoordinator.RenderPopupErrorHtml(
            "https://slow.test/",
            new FetchResult { Status = FetchStatus.Timeout, ErrorDetail = "timed out" });

        Assert.Contains("Connection Timed Out", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderPopupErrorHtml_Http4xxHtmlBody_RendersServerBody()
    {
        const string serverBody = "<html><body>Access denied by origin</body></html>";

        var html = HostDialogCoordinator.RenderPopupErrorHtml(
            "https://blocked.test/",
            new FetchResult
            {
                Status = FetchStatus.NotFound,
                FailureReason = FetchFailureReasonCode.HttpError,
                StatusCode = 403,
                ContentType = "text/html",
                Content = serverBody
            });

        Assert.Contains(serverBody, html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderPopupErrorHtml_Http4xxWithoutHtmlBody_RendersGenericError()
    {
        var html = HostDialogCoordinator.RenderPopupErrorHtml(
            "https://blocked.test/",
            new FetchResult
            {
                Status = FetchStatus.NotFound,
                FailureReason = FetchFailureReasonCode.HttpError,
                StatusCode = 404,
                ContentType = "application/json",
                Content = "{\"error\":\"nope\"}"
            });

        Assert.DoesNotContain("{\"error\":\"nope\"}", html, StringComparison.Ordinal);
        Assert.Contains("404 Not Found", html, StringComparison.Ordinal);
    }
}
