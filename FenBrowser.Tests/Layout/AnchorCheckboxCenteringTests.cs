using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    // The reCAPTCHA anchor widget centres its "I'm not a robot" checkbox with the
    // table-cell idiom, straight from the widget's own stylesheet:
    //   .rc-anchor-center-container { display: table; height: 100% }
    //   .rc-anchor-center-item      { display: table-cell; vertical-align: middle }
    // The checkbox rendered in the top-left corner of its cell instead: the cell's
    // vertical alignment was computed correctly and then destroyed, because the
    // inline formatting context reset the atomic inline's subtree to origin and the
    // paired re-layout was answered from the layout cache.
    public sealed class AnchorCheckboxCenteringTests
    {
        [Fact]
        public async Task CheckboxInsideAnAtomicInline_IsVerticallyCentredInItsCell()
        {
            const string html = """
<!doctype html>
<html><head><style>
  body { margin: 0 }
  .rc-anchor { width: 300px; height: 74px; }
  .rc-anchor-content { display: inline-block; height: 74px; }
  .rc-inline-block { display: inline-block; height: 100%; }
  .rc-anchor-center-container { display: table; height: 100% }
  .rc-anchor-center-item { display: table-cell; vertical-align: middle }
  .rc-anchor-checkbox-holder { width: 52px; }
  .recaptcha-checkbox { display: inline-block; width: 28px; height: 28px; }
</style></head>
<body>
  <div id="anchorbox" class="rc-anchor">
    <div class="rc-anchor-content">
      <div class="rc-inline-block">
        <div class="rc-anchor-center-container">
          <div class="rc-anchor-center-item rc-anchor-checkbox-holder">
            <span id="recaptcha-anchor" class="recaptcha-checkbox"></span>
          </div>
        </div>
      </div>
    </div>
  </div>
</body></html>
""";
            var doc = new HtmlParser(html, new Uri("https://anchor.test/")).Parse();
            var root = doc.DocumentElement;
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://anchor.test/"), null);
            var boxes = LayoutTestHelper.LayoutTree(root, new Dictionary<Node, CssComputed>(computed), 800, 400);

            var holder = root.Descendants().OfType<Element>()
                .First(e => e.ClassList.Contains("rc-anchor-checkbox-holder"));
            var checkbox = doc.GetElementById("recaptcha-anchor");

            var cell = boxes[holder].BorderBox;
            var box = boxes[checkbox].BorderBox;

            Assert.Equal(74f, cell.Height, 1f);
            Assert.Equal(28f, box.Height, 1f);

            // Centred in the cell, not pinned to its top.
            var expectedTop = cell.Top + ((cell.Height - box.Height) / 2f);
            Assert.Equal(expectedTop, box.Top, 1f);
        }
    }
}
