using System;
using System.IO;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// DOM Standard 4.4: <c>Node.childNodes</c> returns the same live NodeList every time.
/// bing.com's <c>sj_appHTML</c> captures it once and drains it with
/// <c>for (;f.length;) fragment.appendChild(f[0])</c>; a snapshot never shrinks, so the
/// loop spun until the page's script budget ran out and the load hung for minutes.
/// </summary>
public sealed class LiveChildNodesTests
{
    private const string Html = """
<!doctype html>
<html>
<body>
  <div id="target"></div>
  <div id="list"><span>a</span><span>b</span><span>c</span></div>
  <script>
    function appHtml(n, t) {
      var u = document.createElement('div'), f, h, guard = 0;
      u.innerHTML = '<br>' + t;
      f = u.childNodes;
      u.removeChild(f[0]);
      for (h = document.createDocumentFragment(); f.length && guard < 1000; guard++) h.appendChild(f[0]);
      n.appendChild(h);
      return guard;
    }
    var spins = appHtml(document.getElementById('target'), '<p>one</p><p>two</p><p>three</p>');
    var target = document.getElementById('target');
    var list = document.getElementById('list');
    var live = list.childNodes;
    var before = live.length;
    list.appendChild(document.createElement('i'));
    var seen = [];
    live.forEach(function (node) { seen.push(node.nodeName); });
    var iterated = 0;
    for (var node of live) iterated++;
    document.body.setAttribute('data-result', [
      spins,
      target.childNodes.length,
      target.textContent,
      before,
      live.length,
      String(list.childNodes === live),
      live.item(3) ? live.item(3).nodeName : 'null',
      String(live.item(99)),
      seen.join(','),
      iterated
    ].join('|'));
  </script>
</body>
</html>
""";

    [Fact]
    public async Task ChildNodes_IsTheSameLiveList_SoDrainLoopsTerminate()
    {
        using var host = new BrowserHost(isPrivate: true);
        var renderer = new SkiaDomRenderer();
        host.EnableJavaScript = true;
        host.SetActiveRenderer(renderer);
        host.Engine.SetExternalRenderer(renderer);

        await host.Engine.RenderAsync(
            Html,
            new Uri("https://live-nodes.test/"),
            _ => Task.FromResult(string.Empty),
            _ => Task.FromResult<Stream>(null),
            _ => { },
            800,
            600,
            forceJavascript: true);

        var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
        var result = root.OwnerDocument?.Body?.GetAttribute("data-result");

        Assert.Equal("3|3|onetwothree|3|4|true|I|null|SPAN,SPAN,SPAN,I|4", result);
    }
}
