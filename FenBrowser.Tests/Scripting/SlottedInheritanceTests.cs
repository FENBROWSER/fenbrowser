using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// CSS Scoping 1 §3.2: a slotted element inherits from its slot - its parent in the
/// flat tree - not from the shadow host it is a DOM child of. A list item slotted
/// into a shadow &lt;ol&gt; takes the list's decimal markers.
/// </summary>
[Collection("Engine Tests")]
public sealed class SlottedInheritanceTests
{
    [Fact]
    public async Task SlottedChildrenInheritFromTheirSlot()
    {
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync(
                "data:text/html,<div id='host' style='color: red; list-style-type: square'>" +
                "<span id='s' slot='named'>s</span><li id='li'>li</li></div>"));

            var result = await browser.ExecuteScriptAsync(@"
                (function () {
                    var host = document.getElementById('host');
                    var root = host.attachShadow({ mode: 'closed' });
                    root.innerHTML = '<div style=""color: green""><slot name=""named""></slot></div><ol><slot></slot></ol>';
                    var s = getComputedStyle(document.getElementById('s'));
                    var li = getComputedStyle(document.getElementById('li'));
                    return [s.color, li.listStyleType, li.color].join('|');
                })();");

            Assert.Equal("rgb(0, 128, 0)|decimal|rgb(255, 0, 0)", result?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }
}
