using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGLES;
using Silk.NET.Windowing;
using SkiaSharp;
using FenBrowser.Host.Input;

namespace FenBrowser.Host
{
    /// <summary>
    /// Manages the native window, graphics context, and main loop.
    /// </summary>
    public class WindowManager : IDisposable
    {
        private static WindowManager _instance;
        public static WindowManager Instance => _instance ??= new WindowManager();

        private IWindow _window;
        private IInputContext _inputContext;
        private GL _gl;
        private GRContext _grContext;
        private SKSurface _surface;
        private GRBackendRenderTarget _renderTarget;

        private int _physicalWidth;
        private int _physicalHeight;
        private int _logicalWidth = 1280;
        private int _logicalHeight = 800;
        private float _dpiScale = 1.0f;
        private int _disposed;

        private readonly ConcurrentQueue<MainThreadWorkItem> _mainThreadQueue = new();
        private int _mainThreadId;

        public event Action OnLoad;
        public event Action<double> OnRender;
        public event Action<Vector2D<int>> OnResize;
        public event Action OnClose;

        public event Action<IKeyboard, Key, int> OnKeyDown;
        public event Action<IKeyboard, char> OnKeyChar;
        public event Action<IMouse, MouseButton> OnMouseDown;
        public event Action<IMouse, MouseButton> OnMouseUp;
        public event Action<IMouse, System.Numerics.Vector2> OnMouseMove;
        public event Action<IMouse, ScrollWheel> OnScroll;

        public IWindow Window => _window;
        public SKCanvas Canvas => _surface?.Canvas;
        public GRContext GraphicsContext => _grContext;
        public float DpiScale => _dpiScale;
        public int LogicalWidth => _logicalWidth;
        public int LogicalHeight => _logicalHeight;
        public bool IsMainThreadInitialized => Volatile.Read(ref _mainThreadId) != 0;
        public bool IsOnMainThread => _mainThreadId == 0 || Environment.CurrentManagedThreadId == _mainThreadId;

        private WindowManager() { }

        public void Initialize(FenBrowser.Host.Platform.IWindow platformWindow, string initialUrl, bool isHeadless = false)
        {
            ThrowIfDisposed();
            if (platformWindow?.SilkWindow == null)
            {
                throw new InvalidOperationException("WindowManager.Initialize requires a platform window with a Silk.NET handle.");
            }

            _mainThreadId = Environment.CurrentManagedThreadId;
            _window = platformWindow.SilkWindow;
            _logicalWidth = platformWindow.Size.X;
            _logicalHeight = platformWindow.Size.Y;

            AttachWindowEvents();
        }

        public void Initialize(string initialUrl, bool isHeadless = false)
        {
            ThrowIfDisposed();
            _mainThreadId = Environment.CurrentManagedThreadId;

            var options = WindowOptions.Default;
            options.Size = new Vector2D<int>(_logicalWidth, _logicalHeight);
            options.VSync = true;

            if (isHeadless)
            {
                options.WindowState = WindowState.Normal;
                options.Position = new Vector2D<int>(-32000, -32000);
                options.WindowBorder = WindowBorder.Hidden;
                EngineLogBridge.Info("[WindowManager] Initializing in HEADLESS mode (Off-screen)", LogCategory.General);
            }
            else
            {
                options.WindowState = WindowState.Maximized;
                options.WindowBorder = WindowBorder.Hidden;
            }

            options.TransparentFramebuffer = false;
            options.Title = "FenBrowser";
            options.API = new GraphicsAPI(ContextAPI.OpenGLES, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 0));

            _window = Silk.NET.Windowing.Window.Create(options);
            AttachWindowEvents();
        }

        private void AttachWindowEvents()
        {
            _window.Load += Load;
            _window.Render += Render;
            _window.Resize += Resize;
            _window.Closing += Close;
        }

        private void DetachWindowEvents()
        {
            if (_window == null)
            {
                return;
            }

            _window.Load -= Load;
            _window.Render -= Render;
            _window.Resize -= Resize;
            _window.Closing -= Close;
        }

        public void Run()
        {
            ThrowIfDisposed();
            _window?.Run();
        }

        private void Load()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            EngineLogBridge.Info("[WindowManager] Window loaded, initializing Graphics...", LogCategory.General);

            _gl = _window.CreateOpenGLES();
            InitializeInput();
            InitializeSkia();

            OnLoad?.Invoke();
            LoadWindowIcon();
        }

        private void LoadWindowIcon()
        {
            try
            {
                var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.png");
                if (!System.IO.File.Exists(iconPath))
                {
                    return;
                }

                using var sourceBitmap = SKBitmap.Decode(iconPath);
                if (sourceBitmap == null)
                {
                    return;
                }

                var icons = new System.Collections.Generic.List<Silk.NET.Core.RawImage>();
                int[] sizes = { 256, 128, 64, 48, 32, 16 };

                foreach (var size in sizes)
                {
                    if (sourceBitmap.Width < size || sourceBitmap.Height < size)
                    {
                        continue;
                    }

                    var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
                    using var scaledBitmap = new SKBitmap(info);
                    using var canvas = new SKCanvas(scaledBitmap);
                    canvas.Clear(SKColors.Transparent);

                    float minDim = Math.Min(sourceBitmap.Width, sourceBitmap.Height);
                    float cropSize = minDim / 1.75f;
                    float centerX = sourceBitmap.Width / 2.0f;
                    float centerY = sourceBitmap.Height / 2.0f;
                    float cropX = centerX - cropSize / 2.0f;
                    float cropY = centerY - cropSize / 2.0f;

                    var srcRect = new SKRect(cropX, cropY, cropX + cropSize, cropY + cropSize);
                    var destRect = new SKRect(0, 0, size, size);
                    using var paint = new SKPaint { IsAntialias = true };
                    canvas.DrawBitmap(sourceBitmap, srcRect, destRect, SKSamplingOptions.Default, paint);
                    canvas.Flush();

                    // SKBitmap.Bytes materializes managed storage, so the RawImage
                    // remains valid after the temporary SKBitmap is disposed.
                    var pixels = scaledBitmap.Bytes;
                    icons.Add(new Silk.NET.Core.RawImage(size, size, new Memory<byte>(pixels)));
                }

                if (icons.Count == 0)
                {
                    var info = new SKImageInfo(sourceBitmap.Width, sourceBitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                    using var rgbaBitmap = new SKBitmap(info);
                    if (sourceBitmap.ScalePixels(rgbaBitmap, new SKSamplingOptions(SKFilterMode.Linear)))
                    {
                        var pixels = rgbaBitmap.Bytes;
                        icons.Add(new Silk.NET.Core.RawImage(rgbaBitmap.Width, rgbaBitmap.Height, new Memory<byte>(pixels)));
                    }
                }

                if (icons.Count > 0)
                {
                    _window.SetWindowIcon(icons.ToArray());
                }
            }
            catch (Exception ex)
            {
                EngineLogBridge.Warn($"[WindowManager] Failed to set window icon: {ex.Message}", LogCategory.General);
            }
        }

        private void InitializeInput()
        {
            _inputContext?.Dispose();
            _inputContext = _window.CreateInput();

            foreach (var keyboard in _inputContext.Keyboards)
            {
                keyboard.KeyDown += (k, key, code) => OnKeyDown?.Invoke(k, key, code);
                keyboard.KeyChar += (k, c) => OnKeyChar?.Invoke(k, c);
            }

            foreach (var mouse in _inputContext.Mice)
            {
                mouse.MouseDown += (m, b) => OnMouseDown?.Invoke(m, b);
                mouse.MouseUp += (m, b) => OnMouseUp?.Invoke(m, b);
                mouse.MouseMove += (m, pos) => OnMouseMove?.Invoke(m, pos);
                mouse.Scroll += (m, w) => OnScroll?.Invoke(m, w);
                InputManager.Instance.Mouse = mouse;
            }
        }

        private void InitializeSkia()
        {
            using var glInterface = GRGlInterface.CreateGles(
                name => _window.GLContext.TryGetProcAddress(name, out var addr) ? addr : IntPtr.Zero);

            if (glInterface == null)
            {
                throw new InvalidOperationException(
                    "GPU initialization failed: GRGlInterface.CreateGles returned null. " +
                    "Verify the OpenGL ES driver/loader and ANGLE deployment.");
            }

            _grContext = GRContext.CreateGl(glInterface);
            if (_grContext == null)
            {
                throw new InvalidOperationException(
                    "GPU initialization failed: GRContext.CreateGl returned null. " +
                    "The GL context exists but Skia could not bind to it.");
            }

            SyncDimensions();
            CreateRenderTarget();
        }

        private void SyncDimensions()
        {
            _physicalWidth = Math.Max(1, _window.FramebufferSize.X);
            _physicalHeight = Math.Max(1, _window.FramebufferSize.Y);
            _logicalWidth = Math.Max(1, _window.Size.X);
            _logicalHeight = Math.Max(1, _window.Size.Y);
            _dpiScale = (float)_physicalWidth / _logicalWidth;
        }

        private void CreateRenderTarget()
        {
            _surface?.Dispose();
            _surface = null;
            _renderTarget?.Dispose();
            _renderTarget = null;

            if (_gl == null || _grContext == null)
            {
                return;
            }

            _gl.GetInteger(GLEnum.FramebufferBinding, out int framebuffer);
            _gl.GetInteger(GLEnum.Stencil, out int stencil);
            _gl.GetInteger(GLEnum.Samples, out int samples);

            var fbInfo = new GRGlFramebufferInfo((uint)framebuffer, SKColorType.Rgba8888.ToGlSizedFormat());
            _renderTarget = new GRBackendRenderTarget(_physicalWidth, _physicalHeight, samples, stencil, fbInfo);
            _surface = SKSurface.Create(_grContext, _renderTarget, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);

            if (_surface == null)
            {
                _renderTarget.Dispose();
                _renderTarget = null;
                EngineLogBridge.Error("[WindowManager] Failed to create SKSurface", LogCategory.General);
            }
        }

        private void Resize(Vector2D<int> size)
        {
            if (Volatile.Read(ref _disposed) != 0 || size.X <= 0 || size.Y <= 0)
            {
                return;
            }

            SyncDimensions();
            _gl?.Viewport(0, 0, (uint)_physicalWidth, (uint)_physicalHeight);
            CreateRenderTarget();

            EngineLogBridge.Info($"[WindowManager] Resized: Logical={_logicalWidth}x{_logicalHeight}, DPI={_dpiScale:F2}", LogCategory.General);
            OnResize?.Invoke(size);
        }

        private void Render(double deltaTime)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            ProcessMainThreadQueue();
            if (_surface == null)
            {
                return;
            }

            OnRender?.Invoke(deltaTime);
            _surface.Canvas.Flush();
            _grContext?.Flush();
        }

        private void Close()
        {
            EngineLogBridge.Info("[WindowManager] Closing...", LogCategory.General);
            try
            {
                OnClose?.Invoke();
            }
            finally
            {
                Dispose();
            }
        }

        public SKBitmap CaptureScreenshot()
        {
            ThrowIfDisposed();
            if (_surface == null)
            {
                EngineLogBridge.Error("[WindowManager] CaptureScreenshot: Surface is NULL", LogCategory.General);
                return null;
            }

            _grContext?.Flush();
            var bitmap = new SKBitmap(_physicalWidth, _physicalHeight);
            if (_surface.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
            {
                return bitmap;
            }

            bitmap.Dispose();
            EngineLogBridge.Error("[WindowManager] CaptureScreenshot: ReadPixels failed", LogCategory.General);
            return null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            DetachWindowEvents();
            FailPendingMainThreadWork(new ObjectDisposedException(nameof(WindowManager)));

            _inputContext?.Dispose();
            _inputContext = null;
            InputManager.Instance.Mouse = null;

            _surface?.Dispose();
            _surface = null;
            _renderTarget?.Dispose();
            _renderTarget = null;
            _grContext?.Dispose();
            _grContext = null;
            _gl?.Dispose();
            _gl = null;
        }

        public Task<T> RunOnMainThread<T>(Func<T> func)
        {
            ArgumentNullException.ThrowIfNull(func);
            if (Volatile.Read(ref _disposed) != 0)
            {
                return Task.FromException<T>(new ObjectDisposedException(nameof(WindowManager)));
            }

            if (IsOnMainThread)
            {
                try
                {
                    return Task.FromResult(func());
                }
                catch (Exception ex)
                {
                    return Task.FromException<T>(ex);
                }
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var workItem = new MainThreadWorkItem(
                execute: () =>
                {
                    try { tcs.TrySetResult(func()); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                },
                reject: ex => tcs.TrySetException(ex));

            _mainThreadQueue.Enqueue(workItem);
            if (Volatile.Read(ref _disposed) != 0 && _mainThreadQueue.TryDequeue(out var stranded))
            {
                stranded.Reject(new ObjectDisposedException(nameof(WindowManager)));
            }
            return tcs.Task;
        }

        public Task RunOnMainThread(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (Volatile.Read(ref _disposed) != 0)
            {
                return Task.FromException(new ObjectDisposedException(nameof(WindowManager)));
            }

            if (IsOnMainThread)
            {
                try
                {
                    action();
                    return Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    return Task.FromException(ex);
                }
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workItem = new MainThreadWorkItem(
                execute: () =>
                {
                    try { action(); tcs.TrySetResult(); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                },
                reject: ex => tcs.TrySetException(ex));

            _mainThreadQueue.Enqueue(workItem);
            if (Volatile.Read(ref _disposed) != 0 && _mainThreadQueue.TryDequeue(out var stranded))
            {
                stranded.Reject(new ObjectDisposedException(nameof(WindowManager)));
            }
            return tcs.Task;
        }

        private void ProcessMainThreadQueue()
        {
            int max = 50;
            while (max-- > 0 && _mainThreadQueue.TryDequeue(out var workItem))
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    workItem.Reject(new ObjectDisposedException(nameof(WindowManager)));
                    continue;
                }

                workItem.Execute();
            }
        }

        private void FailPendingMainThreadWork(Exception exception)
        {
            while (_mainThreadQueue.TryDequeue(out var workItem))
            {
                workItem.Reject(exception);
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        }

        public void CopyToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            try
            {
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    WindowsClipboard.SetText(text);
                }
                else
                {
                    EngineLogBridge.Warn("[WindowManager] Clipboard not supported on this platform", LogCategory.General);
                }
            }
            catch (Exception ex)
            {
                EngineLogBridge.Error($"[WindowManager] Clipboard error: {ex.Message}", LogCategory.General);
            }
        }

        private sealed class MainThreadWorkItem
        {
            public MainThreadWorkItem(Action execute, Action<Exception> reject)
            {
                Execute = execute ?? throw new ArgumentNullException(nameof(execute));
                Reject = reject ?? throw new ArgumentNullException(nameof(reject));
            }

            public Action Execute { get; }
            public Action<Exception> Reject { get; }
        }
    }

    internal static class WindowsClipboard
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        public static void SetText(string text)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                return;
            }

            IntPtr hMem = IntPtr.Zero;
            var ownershipTransferred = false;
            try
            {
                if (!EmptyClipboard())
                {
                    return;
                }

                var bytes = checked((text.Length + 1) * sizeof(char));
                hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                if (hMem == IntPtr.Zero)
                {
                    return;
                }

                var pMem = GlobalLock(hMem);
                if (pMem == IntPtr.Zero)
                {
                    return;
                }

                try
                {
                    var chars = text.ToCharArray();
                    System.Runtime.InteropServices.Marshal.Copy(chars, 0, pMem, chars.Length);
                    System.Runtime.InteropServices.Marshal.WriteInt16(pMem, text.Length * sizeof(char), 0);
                }
                finally
                {
                    GlobalUnlock(hMem);
                }

                if (SetClipboardData(CF_UNICODETEXT, hMem) != IntPtr.Zero)
                {
                    ownershipTransferred = true;
                }
            }
            finally
            {
                if (hMem != IntPtr.Zero && !ownershipTransferred)
                {
                    GlobalFree(hMem);
                }
                CloseClipboard();
            }
        }
    }
}
