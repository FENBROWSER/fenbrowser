using System.Threading.Tasks;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// HTML rendering §15.5.15: a single-line text field is one line of its own text
    /// tall, so its content height follows its used line-height.
    /// </summary>
    public class TextInputIntrinsicHeightTests
    {
        [Fact]
        public async Task TextInput_IsItsLineHeightTall()
        {
            // YouTube's masthead search field.
            var probe = await HtmlLayoutProbe.LayoutAsync(
                "<div style='width:500px'><input id='field' style='padding:1px 0;margin:0;border:none;font-size:16px;line-height:22px;width:100%'></div>");

            Assert.Equal(24f, probe.Rect("field").Height, 1);
        }

        [Fact]
        public async Task TextInput_GrowsWithItsFont()
        {
            var probe = await HtmlLayoutProbe.LayoutAsync(
                "<input id='small' style='font-size:12px;line-height:normal;padding:0;border:none'>" +
                "<input id='large' style='font-size:36px;line-height:normal;padding:0;border:none'>");

            Assert.True(probe.Rect("large").Height > probe.Rect("small").Height * 2.5f,
                $"small {probe.Rect("small").Height}, large {probe.Rect("large").Height}");
        }
    }
}
