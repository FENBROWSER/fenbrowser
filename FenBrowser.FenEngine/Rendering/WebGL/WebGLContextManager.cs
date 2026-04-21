using System;
using System.Collections.Generic;
using SkiaSharp;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Rendering.WebGL
{
    /// <summary>
    /// Manages WebGL contexts for canvas elements.
    /// Provides the bridge between JavaScript canvas.getContext("webgl") and our WebGL implementation.
    /// </summary>
    public class WebGLContextManager
    {
        private static readonly Dictionary<string, WebGLRenderingContext> _contexts = new();
        private static readonly Dictionary<string, SKBitmap> _canvasBitmaps = new();
        
        /// <summary>
        /// Get or create a WebGL context for a canvas element
        /// </summary>
        public static WebGLRenderingContext GetContext(string canvasId, int width, int height, bool webgl2 = false)
        {
            if (string.IsNullOrEmpty(canvasId))
                return null;
            
            string key = $"{canvasId}_{(webgl2 ? "2" : "1")}";
            
            if (_contexts.TryGetValue(key, out var existing))
            {
                // Resize if dimensions changed
                if (existing.DrawingBufferWidth != width || existing.DrawingBufferHeight != height)
                {
                    existing.Resize(width, height);
                }
                return existing;
            }
            
            WebGLRenderingContext context;
            if (webgl2)
            {
                context = new WebGL2RenderingContext(width, height);
            }
            else
            {
                context = new WebGLRenderingContext(width, height);
            }
            
            _contexts[key] = context;
            EngineLogCompat.Debug($"[WebGLContextManager] Created {(webgl2 ? "WebGL2" : "WebGL")} context for canvas '{canvasId}' ({width}x{height})", LogCategory.Rendering);
            
            return context;
        }
        
        /// <summary>
        /// Get the output bitmap for a canvas (for compositing into the page)
        /// </summary>
        public static SKBitmap GetCanvasBitmap(string canvasId, bool webgl2 = false)
        {
            string key = $"{canvasId}_{(webgl2 ? "2" : "1")}";
            
            if (_contexts.TryGetValue(key, out var context))
            {
                return context.GetOutputBitmap();
            }
            
            return null;
        }
        
        /// <summary>
        /// Destroy a WebGL context
        /// </summary>
        public static void DestroyContext(string canvasId, bool webgl2 = false)
        {
            string key = $"{canvasId}_{(webgl2 ? "2" : "1")}";
            
            if (_contexts.TryGetValue(key, out var context))
            {
                context.Dispose();
                _contexts.Remove(key);
                EngineLogCompat.Debug($"[WebGLContextManager] Destroyed context for canvas '{canvasId}'", LogCategory.Rendering);
            }
        }
        
        /// <summary>
        /// Dispose all contexts (cleanup on page unload)
        /// </summary>
        public static void DisposeAll()
        {
            foreach (var context in _contexts.Values)
            {
                try { context.Dispose(); } catch (Exception ex) { EngineLogCompat.Warn($"[WebGLContextManager] Context dispose failed: {ex.Message}", LogCategory.Rendering); }
            }
            _contexts.Clear();
            _canvasBitmaps.Clear();
        }
        
        /// <summary>
        /// Check if WebGL is supported (always true in our implementation)
        /// </summary>
        public static bool IsWebGLSupported() => true;
        
        /// <summary>
        /// Check if WebGL 2.0 is supported (always true in our implementation)
        /// </summary>
        public static bool IsWebGL2Supported() => true;
        
        /// <summary>
        /// Get context attributes for context creation
        /// </summary>
        public static WebGLContextAttributes GetDefaultContextAttributes()
        {
            return new WebGLContextAttributes
            {
                Alpha = true,
                Depth = true,
                Stencil = false,
                Antialias = true,
                PremultipliedAlpha = true,
                PreserveDrawingBuffer = false,
                PowerPreference = "default",
                FailIfMajorPerformanceCaveat = false
            };
        }
    }
    
    /// <summary>
    /// WebGL context creation attributes
    /// </summary>
    public class WebGLContextAttributes
    {
        public bool Alpha { get; set; } = true;
        public bool Depth { get; set; } = true;
        public bool Stencil { get; set; } = false;
        public bool Antialias { get; set; } = true;
        public bool PremultipliedAlpha { get; set; } = true;
        public bool PreserveDrawingBuffer { get; set; } = false;
        public string PowerPreference { get; set; } = "default"; // "default", "high-performance", "low-power"
        public bool FailIfMajorPerformanceCaveat { get; set; } = false;
        public bool DesynchronizedHint { get; set; } = false; // WebGL 2.0
    }
    
    /// <summary>
    /// Extension methods for JavaScript integration
    /// </summary>
    public static class WebGLJavaScriptBindings
    {
        /// <summary>
        /// Create a JavaScript wrapper object for the WebGL context
        /// </summary>
        public static object CreateJSWrapper(WebGLRenderingContext context)
        {
            if (context == null) return null;
            
            // Return dictionary of methods that can be called from JavaScript
            return new Dictionary<string, object>
            {
                // Buffer methods
                { "createBuffer", new Func<WebGLBuffer>(() => context.CreateBuffer()) },
                { "deleteBuffer", new Action<WebGLBuffer>(b => context.DeleteBuffer(b)) },
                { "bindBuffer", new Action<uint, WebGLBuffer>((t, b) => context.BindBuffer(t, b)) },
                { "bufferData", new Action<uint, byte[], uint>((t, d, u) => context.BufferData(t, d, u)) },
                
                // Shader methods
                { "createShader", new Func<uint, WebGLShader>(t => context.CreateShader(t)) },
                { "deleteShader", new Action<WebGLShader>(s => context.DeleteShader(s)) },
                { "shaderSource", new Action<WebGLShader, string>((s, src) => context.ShaderSource(s, src)) },
                { "compileShader", new Action<WebGLShader>(s => context.CompileShader(s)) },
                { "getShaderParameter", new Func<WebGLShader, uint, object>((s, p) => context.GetShaderParameter(s, p)) },
                { "getShaderInfoLog", new Func<WebGLShader, string>(s => context.GetShaderInfoLog(s)) },
                
                // Program methods
                { "createProgram", new Func<WebGLProgram>(() => context.CreateProgram()) },
                { "deleteProgram", new Action<WebGLProgram>(p => context.DeleteProgram(p)) },
                { "attachShader", new Action<WebGLProgram, WebGLShader>((p, s) => context.AttachShader(p, s)) },
                { "linkProgram", new Action<WebGLProgram>(p => context.LinkProgram(p)) },
                { "useProgram", new Action<WebGLProgram>(p => context.UseProgram(p)) },
                { "getProgramParameter", new Func<WebGLProgram, uint, object>((p, n) => context.GetProgramParameter(p, n)) },
                { "getProgramInfoLog", new Func<WebGLProgram, string>(p => context.GetProgramInfoLog(p)) },
                
                // Uniform methods
                { "getUniformLocation", new Func<WebGLProgram, string, WebGLUniformLocation>((p, n) => context.GetUniformLocation(p, n)) },
                { "uniform1f", new Action<WebGLUniformLocation, float>((l, x) => context.Uniform1f(l, x)) },
                { "uniform2f", new Action<WebGLUniformLocation, float, float>((l, x, y) => context.Uniform2f(l, x, y)) },
                { "uniform3f", new Action<WebGLUniformLocation, float, float, float>((l, x, y, z) => context.Uniform3f(l, x, y, z)) },
                { "uniform4f", new Action<WebGLUniformLocation, float, float, float, float>((l, x, y, z, w) => context.Uniform4f(l, x, y, z, w)) },
                { "uniform1i", new Action<WebGLUniformLocation, int>((l, x) => context.Uniform1i(l, x)) },
                { "uniformMatrix4fv", new Action<WebGLUniformLocation, bool, float[]>((l, t, v) => context.UniformMatrix4fv(l, t, v)) },
                
                // Attribute methods
                { "getAttribLocation", new Func<WebGLProgram, string, int>((p, n) => context.GetAttribLocation(p, n)) },
                { "vertexAttribPointer", new Action<uint, int, uint, bool, int, int>((i, s, t, n, st, o) => context.VertexAttribPointer(i, s, t, n, st, o)) },
                { "enableVertexAttribArray", new Action<uint>(i => context.EnableVertexAttribArray(i)) },
                { "disableVertexAttribArray", new Action<uint>(i => context.DisableVertexAttribArray(i)) },
                
                // Texture methods
                { "createTexture", new Func<WebGLTexture>(() => context.CreateTexture()) },
                { "deleteTexture", new Action<WebGLTexture>(t => context.DeleteTexture(t)) },
                { "bindTexture", new Action<uint, WebGLTexture>((t, tex) => context.BindTexture(t, tex)) },
                { "activeTexture", new Action<uint>(t => context.ActiveTexture(t)) },
                { "texImage2D", new Action<uint, int, uint, int, int, int, uint, uint, byte[]>((t, l, i, w, h, b, f, ty, p) => context.TexImage2D(t, l, i, w, h, b, f, ty, p)) },
                { "texParameteri", new Action<uint, uint, int>((t, p, v) => context.TexParameteri(t, p, v)) },
                { "generateMipmap", new Action<uint>(t => context.GenerateMipmap(t)) },
                
                // Framebuffer methods
                { "createFramebuffer", new Func<WebGLFramebuffer>(() => context.CreateFramebuffer()) },
                { "deleteFramebuffer", new Action<WebGLFramebuffer>(f => context.DeleteFramebuffer(f)) },
                { "bindFramebuffer", new Action<uint, WebGLFramebuffer>((t, f) => context.BindFramebuffer(t, f)) },
                { "checkFramebufferStatus", new Func<uint, uint>(t => context.CheckFramebufferStatus(t)) },
                
                // Drawing methods
                { "clear", new Action<uint>(m => context.Clear(m)) },
                { "clearColor", new Action<float, float, float, float>((r, g, b, a) => context.ClearColor(r, g, b, a)) },
                { "clearDepth", new Action<float>(d => context.ClearDepth(d)) },
                { "drawArrays", new Action<uint, int, int>((m, f, c) => context.DrawArrays(m, f, c)) },
                { "drawElements", new Action<uint, int, uint, int>((m, c, t, o) => context.DrawElements(m, c, t, o)) },
                
                // State methods
                { "enable", new Action<uint>(c => context.Enable(c)) },
                { "disable", new Action<uint>(c => context.Disable(c)) },
                { "viewport", new Action<int, int, int, int>((x, y, w, h) => context.Viewport(x, y, w, h)) },
                { "depthFunc", new Action<uint>(f => context.DepthFunc(f)) },
                { "blendFunc", new Action<uint, uint>((s, d) => context.BlendFunc(s, d)) },
                { "cullFace", new Action<uint>(m => context.CullFace(m)) },
                
                // Query methods
                { "getParameter", new Func<uint, object>(p => context.GetParameter(p)) },
                { "getError", new Func<uint>(() => context.GetError()) },
                { "isContextLost", new Func<bool>(() => context.IsContextLost()) },
                { "getExtension", new Func<string, object>(n => context.GetExtension(n)) },
                { "getSupportedExtensions", new Func<string[]>(() => context.GetSupportedExtensions()) },
                
                // Pixel methods
                { "readPixels", new Func<int, int, int, int, uint, uint, byte[]>((x, y, w, h, f, t) => context.ReadPixels(x, y, w, h, f, t)) },
                { "pixelStorei", new Action<uint, int>((p, v) => context.PixelStorei(p, v)) },
                
                // Constants (expose as properties)
                { "ARRAY_BUFFER", WebGLConstants.ARRAY_BUFFER },
                { "ELEMENT_ARRAY_BUFFER", WebGLConstants.ELEMENT_ARRAY_BUFFER },
                { "STATIC_DRAW", WebGLConstants.STATIC_DRAW },
                { "DYNAMIC_DRAW", WebGLConstants.DYNAMIC_DRAW },
                { "VERTEX_SHADER", WebGLConstants.VERTEX_SHADER },
                { "FRAGMENT_SHADER", WebGLConstants.FRAGMENT_SHADER },
                { "COMPILE_STATUS", WebGLConstants.COMPILE_STATUS },
                { "LINK_STATUS", WebGLConstants.LINK_STATUS },
                { "FLOAT", WebGLConstants.FLOAT },
                { "UNSIGNED_SHORT", WebGLConstants.UNSIGNED_SHORT },
                { "UNSIGNED_INT", WebGLConstants.UNSIGNED_INT },
                { "TRIANGLES", WebGLConstants.TRIANGLES },
                { "LINES", WebGLConstants.LINES },
                { "POINTS", WebGLConstants.POINTS },
                { "COLOR_BUFFER_BIT", WebGLConstants.COLOR_BUFFER_BIT },
                { "DEPTH_BUFFER_BIT", WebGLConstants.DEPTH_BUFFER_BIT },
                { "STENCIL_BUFFER_BIT", WebGLConstants.STENCIL_BUFFER_BIT },
                { "DEPTH_TEST", WebGLConstants.DEPTH_TEST },
                { "BLEND", WebGLConstants.BLEND },
                { "CULL_FACE", WebGLConstants.CULL_FACE },
                { "TEXTURE_2D", WebGLConstants.TEXTURE_2D },
                { "TEXTURE0", WebGLConstants.TEXTURE0 },
                { "RGBA", WebGLConstants.RGBA },
                { "RGB", WebGLConstants.RGB },
                { "UNSIGNED_BYTE", WebGLConstants.UNSIGNED_BYTE },
                { "LINEAR", WebGLConstants.LINEAR },
                { "NEAREST", WebGLConstants.NEAREST },
                { "TEXTURE_MIN_FILTER", WebGLConstants.TEXTURE_MIN_FILTER },
                { "TEXTURE_MAG_FILTER", WebGLConstants.TEXTURE_MAG_FILTER },
                { "TEXTURE_WRAP_S", WebGLConstants.TEXTURE_WRAP_S },
                { "TEXTURE_WRAP_T", WebGLConstants.TEXTURE_WRAP_T },
                { "CLAMP_TO_EDGE", WebGLConstants.CLAMP_TO_EDGE },
                { "REPEAT", WebGLConstants.REPEAT },
                { "SRC_ALPHA", WebGLConstants.SRC_ALPHA },
                { "ONE_MINUS_SRC_ALPHA", WebGLConstants.ONE_MINUS_SRC_ALPHA },
                { "ONE", WebGLConstants.ONE },
                { "ZERO", WebGLConstants.ZERO },
                { "LESS", WebGLConstants.LESS },
                { "LEQUAL", WebGLConstants.LEQUAL },
                { "BACK", WebGLConstants.BACK },
                { "FRONT", WebGLConstants.FRONT },
                { "FRAMEBUFFER", WebGLConstants.FRAMEBUFFER },
                { "FRAMEBUFFER_COMPLETE", WebGLConstants.FRAMEBUFFER_COMPLETE }
            };
        }
    }
}

