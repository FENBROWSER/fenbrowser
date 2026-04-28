using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.DOM;
using FenBrowser.FenEngine.Security;
using Xunit;

namespace FenBrowser.Tests.DOM
{
    public class CanvasRenderingContext2DTests
    {
        [Fact]
        public void ElementWrapper_GetContext2D_DrawRequestsRenderFromExecutionContext()
        {
            var canvas = new Element("canvas");
            canvas.SetAttribute("width", "32");
            canvas.SetAttribute("height", "16");

            var permissions = new PermissionManager(JsPermissions.DomRead | JsPermissions.DomWrite | JsPermissions.DomEvents);
            var context = new FenBrowser.FenEngine.Core.ExecutionContext(permissions);
            var renderRequests = 0;
            context.SetRequestRender(() => renderRequests++);

            var wrapper = new ElementWrapper(canvas, context);
            var getContext = wrapper.Get("getContext", context).AsFunction();

            var contextValue = getContext.Invoke(new[] { FenValue.FromString("2d") }, context);
            var canvasContext = Assert.IsType<FenBrowser.FenEngine.Scripting.CanvasRenderingContext2D>(contextValue.AsObject());
            var fillRect = canvasContext.Get("fillRect", context).AsFunction();

            fillRect.Invoke(
                new[]
                {
                    FenValue.FromNumber(1),
                    FenValue.FromNumber(2),
                    FenValue.FromNumber(3),
                    FenValue.FromNumber(4)
                },
                context);

            Assert.Equal(1, renderRequests);
        }
    }
}