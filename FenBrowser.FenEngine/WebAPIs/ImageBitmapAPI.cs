using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// ImageBitmap API implementation per W3C spec.
    /// https://www.w3.org/TR/2dcontext/#imagebitmap
    /// </summary>
    public static class ImageBitmapAPI
    {
        /// <summary>
        /// ImageBitmap represents a bitmap image drawn on a canvas.
        /// </summary>
        public sealed class ImageBitmap : FenObject
        {
            public int Width { get; }
            public int Height { get; }
            private bool _closed;

            public ImageBitmap(int width, int height)
            {
                Width = width;
                Height = height;
                _closed = false;
                InternalClass = "ImageBitmap";
            }

            public void Close()
            {
                _closed = true;
            }

            public bool IsClosed => _closed;
        }

        public static FenValue CreateImageBitmap(FenValue[] args, IExecutionContext context)
        {
            int sx = 0, sy = 0, sw = -1, sh = -1;
            FenValue options = FenValue.Undefined;

            var source = args.Length > 0 ? args[0] : FenValue.Undefined;

            if (args.Length >= 5)
            {
                sx = (int)args[1].ToNumber();
                sy = (int)args[2].ToNumber();
                sw = (int)args[3].ToNumber();
                sh = (int)args[4].ToNumber();
                if (args.Length > 5)
                    options = args[5];
            }
            else if (args.Length > 1)
            {
                options = args[1];
            }

            var executor = new FenFunction("executor", (execArgs, execThis) =>
            {
                var resolve = execArgs[0].AsFunction();
                var reject = execArgs[1].AsFunction();

                try
                {
                    int width = 100;
                    int height = 100;

                    if (source.IsObject)
                    {
                        var obj = source.AsObject();

                        if (obj is FenObject fenObj)
                        {
                            var widthVal = fenObj.Get("width");
                            var heightVal = fenObj.Get("height");

                            if (widthVal.IsNumber)
                                width = (int)widthVal.AsNumber();
                            if (heightVal.IsNumber)
                                height = (int)heightVal.AsNumber();
                        }
                    }

                    if (sw < 0) sw = width - sx;
                    if (sh < 0) sh = height - sy;
                    sw = Math.Max(1, Math.Min(sw, width - sx));
                    sh = Math.Max(1, Math.Min(sh, height - sy));

                    var bitmap = new ImageBitmap(sw, sh);

                    EventLoopCoordinator.Instance.ScheduleMicrotask(() =>
                    {
                        resolve.Invoke(new[] { FenValue.FromObject(bitmap) }, context);
                    });
                }
                catch (Exception ex)
                {
                    EventLoopCoordinator.Instance.ScheduleMicrotask(() =>
                    {
                        reject.Invoke(new[] { FenValue.FromString(ex.Message) }, context);
                    });
                }

                return FenValue.Undefined;
            });

            return FenValue.FromObject(new JsPromise(FenValue.FromFunction(executor), context));
        }
    }
}