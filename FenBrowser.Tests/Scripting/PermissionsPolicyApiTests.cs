using System;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

// Permissions Policy: document.permissionsPolicy and iframe.permissionsPolicy.
[Collection("Engine Tests")]
public sealed class PermissionsPolicyApiTests : IDisposable
{
    public PermissionsPolicyApiTests()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    public void Dispose()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    private static FenJsBrowserScriptEngine Load(string header)
    {
        var baseUri = new Uri("https://policy.test/page");
        var document = new HtmlParser("<!doctype html><html><body></body></html>", baseUri).Parse();
        var policy = FenBrowser.Core.Security.PermissionsPolicy.Parse(header);
        var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { }))
        {
            Sandbox = SandboxPolicy.AllowAll,
            PermissionsPolicyProvider = () => policy
        };
        engine.SetDomAsync(document.DocumentElement, baseUri).GetAwaiter().GetResult();
        return engine;
    }

    [Fact]
    public void TheDocumentPolicyFollowsTheHeaderAndDefaultAllowlists()
    {
        var engine = Load("fullscreen=(), geolocation=(self \"https://other.test\")");

        Assert.Equal("false|true|true|false|https://policy.test,https://other.test|*|false", engine.Evaluate(@"
            var p = document.permissionsPolicy;
            [p.allowsFeature('fullscreen'),
             p.allowsFeature('geolocation'),
             p.allowsFeature('geolocation', 'https://other.test'),
             p.allowsFeature('geolocation', 'https://third.test'),
             p.getAllowlistForFeature('geolocation').join(','),
             p.getAllowlistForFeature('picture-in-picture').join(','),
             p.allowedFeatures().indexOf('fullscreen') >= 0].join('|')")?.ToString());
    }

    [Fact]
    public void AnIframePolicyFollowsItsAllowAttributeLive()
    {
        var engine = Load(string.Empty);

        Assert.Equal("true|false|true|true", engine.Evaluate(@"
            var f = document.createElement('iframe');
            f.src = 'https://cross.test/frame.html';
            document.body.appendChild(f);
            var p = f.permissionsPolicy;
            var sameOriginDefault = document.createElement('iframe').permissionsPolicy.allowsFeature('camera');
            var crossDefault = p.allowsFeature('camera');
            f.setAttribute('allow', 'camera');
            var crossAllowed = p.allowsFeature('camera');
            f.removeAttribute('allow');
            f.setAttribute('allowfullscreen', '');
            [sameOriginDefault, crossDefault, crossAllowed, p.allowsFeature('fullscreen')].join('|')")?.ToString());
    }
}
