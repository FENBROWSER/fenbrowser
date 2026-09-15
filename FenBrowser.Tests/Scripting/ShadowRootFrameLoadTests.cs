using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    // DOM 4.2.3 "insert": the insertion steps run for each shadow-including inclusive
    // descendant, so an iframe inside a shadow root is loaded like any other. Frame
    // loading walked only the light tree and ignored insertions into a shadow root, so
    // Cloudflare Turnstile's challenge iframe (in a closed root) was never requested
    // and bing.com's "One last step" page showed no challenge.
    public sealed class ShadowRootFrameLoadTests
    {
        private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(5);

        [Theory]
        [InlineData("open")]
        [InlineData("closed")]
        public async Task IframeAppendedToAConnectedShadowRoot_IsLoaded(string mode)
        {
            var (engine, requested) = await CreateEngineAsync();

            engine.Evaluate(
                "var host = document.createElement('div');" +
                "document.body.appendChild(host);" +
                $"var root = host.attachShadow({{ mode: '{mode}' }});" +
                "var frame = document.createElement('iframe');" +
                "frame.src = 'https://frame.test/inside-shadow';" +
                "root.appendChild(frame);");

            Assert.True(
                await WaitForRequestAsync(requested, "https://frame.test/inside-shadow"),
                $"iframe inside a {mode} shadow root was never loaded");
        }

        [Fact]
        public async Task IframeInsideAShadowRoot_IsLoadedWhenItsHostIsConnected()
        {
            var (engine, requested) = await CreateEngineAsync();

            engine.Evaluate(
                "var host = document.createElement('div');" +
                "var root = host.attachShadow({ mode: 'closed' });" +
                "var frame = document.createElement('iframe');" +
                "frame.src = 'https://frame.test/host-connected';" +
                "root.appendChild(frame);" +
                "document.body.appendChild(host);");

            Assert.True(
                await WaitForRequestAsync(requested, "https://frame.test/host-connected"),
                "iframe in a shadow root was not loaded when its host was connected");
        }

        private static async Task<(FenJsBrowserScriptEngine Engine, List<string> Requested)> CreateEngineAsync()
        {
            var baseUri = new Uri("https://parent.test/page");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var requested = new List<string>();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll,
                FrameElementLoader = (_, uri) =>
                {
                    lock (requested)
                    {
                        requested.Add(uri.AbsoluteUri);
                    }

                    return Task.CompletedTask;
                }
            };
            await engine.SetDomAsync(document.DocumentElement, baseUri);
            return (engine, requested);
        }

        private static async Task<bool> WaitForRequestAsync(List<string> requested, string url)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < LoadTimeout)
            {
                lock (requested)
                {
                    if (requested.Contains(url))
                    {
                        return true;
                    }
                }

                await Task.Delay(25);
            }

            return false;
        }

        private static JsHostAdapter CreateHost() =>
            new JsHostAdapter(navigate: _ => { }, post: (_, __) => { }, status: _ => { }, log: _ => { });
    }
}
