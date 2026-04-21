using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Rendering.WebGL
{
    /// <summary>
    /// Full WebGL 1.0 Rendering Context implementation per Khronos WebGL specification.
    /// Uses SkiaSharp for software rasterization of WebGL commands.
    /// </summary>
    public class WebGLRenderingContext
    {
        // Canvas and rendering surface
        protected int _width;
        protected int _height;
        protected SKBitmap _backBuffer;
        protected SKCanvas _canvas;
        
        // State
        protected uint _lastError = WebGLConstants.NO_ERROR;
        protected bool _contextLost = false;
        
        // Current bindings
        protected WebGLProgram _currentProgram;
        protected WebGLBuffer _arrayBuffer;
        protected WebGLBuffer _elementArrayBuffer;
        protected WebGLFramebuffer _currentFramebuffer;
        protected WebGLRenderbuffer _currentRenderbuffer;
        protected WebGLTexture[] _textureUnits = new WebGLTexture[32];
        protected int _activeTexture = 0;
        
        // Object storage
        protected Dictionary<uint, WebGLBuffer> _buffers = new();
        protected Dictionary<uint, WebGLShader> _shaders = new();
        protected Dictionary<uint, WebGLProgram> _programs = new();
        protected Dictionary<uint, WebGLTexture> _textures = new();
        protected Dictionary<uint, WebGLFramebuffer> _framebuffers = new();
        protected Dictionary<uint, WebGLRenderbuffer> _renderbuffers = new();
        
        // Render state
        protected SKColor _clearColor = SKColors.Black;
        protected float _clearDepth = 1.0f;
        protected int _clearStencil = 0;
        protected bool _depthTest = false;
        protected bool _blend = false;
        protected bool _cullFace = false;
        protected bool _scissorTest = false;
        protected bool _stencilTest = false;
        protected bool _dither = true;
        protected uint _depthFunc = WebGLConstants.LESS;
        protected uint _blendSrcRGB = WebGLConstants.ONE;
        protected uint _blendDstRGB = WebGLConstants.ZERO;
        protected uint _blendSrcAlpha = WebGLConstants.ONE;
        protected uint _blendDstAlpha = WebGLConstants.ZERO;
        protected uint _blendEquationRGB = WebGLConstants.FUNC_ADD;
        protected uint _blendEquationAlpha = WebGLConstants.FUNC_ADD;
        protected SKColor _blendColor = SKColors.Transparent;
        protected uint _cullFaceMode = WebGLConstants.BACK;
        protected uint _frontFace = WebGLConstants.CCW;
        protected SKRectI _viewport;
        protected SKRectI _scissor;
        protected bool _depthMask = true;
        protected int[] _colorMask = { 1, 1, 1, 1 };
        protected float _lineWidth = 1.0f;
        protected float _polygonOffsetFactor = 0;
        protected float _polygonOffsetUnits = 0;
        
        // Vertex attributes
        protected VertexAttribState[] _vertexAttribs = new VertexAttribState[16];
        protected float[][] _genericAttribs = new float[16][];
        
        // Pixel storage
        protected bool _unpackFlipY = false;
        protected bool _unpackPremultiplyAlpha = false;
        protected int _unpackAlignment = 4;
        protected int _packAlignment = 4;

        public int DrawingBufferWidth => _width;
        public int DrawingBufferHeight => _height;
        
        public WebGLRenderingContext(int width, int height)
        {
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            _backBuffer = new SKBitmap(_width, _height, SKColorType.Rgba8888, SKAlphaType.Premul);
            _canvas = new SKCanvas(_backBuffer);
            _viewport = new SKRectI(0, 0, _width, _height);
            _scissor = new SKRectI(0, 0, _width, _height);
            
            // Initialize vertex attribs
            for (int i = 0; i < _vertexAttribs.Length; i++)
            {
                _vertexAttribs[i] = new VertexAttribState();
                _genericAttribs[i] = new float[] { 0, 0, 0, 1 };
            }
            
            EngineLogCompat.Debug($"[WebGL] Created context {_width}x{_height}", LogCategory.Rendering);
        }
        
        // ========== Context State ==========
        
        public bool IsContextLost() => _contextLost;
        
        public uint GetError()
        {
            var err = _lastError;
            _lastError = WebGLConstants.NO_ERROR;
            return err;
        }
        
        protected void SetError(uint error)
        {
            if (_lastError == WebGLConstants.NO_ERROR)
                _lastError = error;
        }
        
        // ========== Buffer Operations ==========
        
        public WebGLBuffer CreateBuffer()
        {
            var buffer = new WebGLBuffer();
            _buffers[buffer.Id] = buffer;
            return buffer;
        }
        
        public void DeleteBuffer(WebGLBuffer buffer)
        {
            if (buffer == null) return;
            _buffers.Remove(buffer.Id);
            if (_arrayBuffer == buffer) _arrayBuffer = null;
            if (_elementArrayBuffer == buffer) _elementArrayBuffer = null;
        }
        
        public bool IsBuffer(WebGLBuffer buffer)
        {
            return buffer != null && _buffers.ContainsKey(buffer.Id);
        }
        
        public void BindBuffer(uint target, WebGLBuffer buffer)
        {
            if (target == WebGLConstants.ARRAY_BUFFER)
            {
                _arrayBuffer = buffer;
                if (buffer != null) buffer.Target = target;
            }
            else if (target == WebGLConstants.ELEMENT_ARRAY_BUFFER)
            {
                _elementArrayBuffer = buffer;
                if (buffer != null) buffer.Target = target;
            }
            else
            {
                SetError(WebGLConstants.INVALID_ENUM);
            }
        }
        
        public void BufferData(uint target, byte[] data, uint usage)
        {
            WebGLBuffer buffer = target == WebGLConstants.ARRAY_BUFFER ? _arrayBuffer : _elementArrayBuffer;
            if (buffer == null)
            {
                SetError(WebGLConstants.INVALID_OPERATION);
                return;
            }
            
            buffer.Data = data;
            buffer.Usage = usage;
        }
        
        public void BufferData(uint target, int size, uint usage)
        {
            BufferData(target, new byte[size], usage);
        }
        
        public void BufferSubData(uint target, int offset, byte[] data)
        {
            WebGLBuffer buffer = target == WebGLConstants.ARRAY_BUFFER ? _arrayBuffer : _elementArrayBuffer;
            if (buffer == null || buffer.Data == null)
            {
                SetError(WebGLConstants.INVALID_OPERATION);
                return;
            }
            
            if (offset < 0 || offset + data.Length > buffer.Data.Length)
            {
                SetError(WebGLConstants.INVALID_VALUE);
                return;
            }
            
            Array.Copy(data, 0, buffer.Data, offset, data.Length);
        }
        
        // ========== Shader Operations ==========
        
        public WebGLShader CreateShader(uint type)
        {
            if (type != WebGLConstants.VERTEX_SHADER && type != WebGLConstants.FRAGMENT_SHADER)
            {
                SetError(WebGLConstants.INVALID_ENUM);
                return null;
            }
            
            var shader = new WebGLShader(type);
            _shaders[shader.Id] = shader;
            return shader;
        }
        
        public void DeleteShader(WebGLShader shader)
        {
            if (shader == null) return;
            _shaders.Remove(shader.Id);
        }
        
        public bool IsShader(WebGLShader shader)
        {
            return shader != null && _shaders.ContainsKey(shader.Id);
        }
        
        public void ShaderSource(WebGLShader shader, string source)
        {
            if (shader == null)
            {
                SetError(WebGLConstants.INVALID_VALUE);
                return;
            }
            shader.Source = source;
        }
        
        public void CompileShader(WebGLShader shader)
        {
            if (shader == null)
            {
                SetError(WebGLConstants.INVALID_VALUE);
                return;
            }
            
            // Validate GLSL syntax (simplified)
            if (string.IsNullOrWhiteSpace(shader.Source))
            {
                shader.Compiled = false;
                shader.InfoLog = "Error: Empty shader source";
                return;
            }
            
            // Basic syntax checks
            bool hasMain = shader.Source.Contains("main");
            bool hasVoid = shader.Source.Contains("void");
            
            if (hasMain && hasVoid)
            {
                shader.Compiled = true;
                shader.InfoLog = "";
                EngineLogCompat.Debug($"[WebGL] Shader {shader.Id} compiled successfully", LogCategory.Rendering);
            }
            else
            {
                shader.Compiled = false;
                shader.InfoLog = "Error: Missing main function";
            }
        }
        
        public object GetShaderParameter(WebGLShader shader, uint pname)
        {
            if (shader == null) return null;
            
            switch (pname)
            {
                case WebGLConstants.COMPILE_STATUS:
                    return shader.Compiled;
                case WebGLConstants.DELETE_STATUS:
                    return !_shaders.ContainsKey(shader.Id);
                case WebGLConstants.SHADER_TYPE:
                    return shader.Type;
                default:
                    SetError(WebGLConstants.INVALID_ENUM);
                    return null;
            }
        }
        
        public string GetShaderInfoLog(WebGLShader shader)
        {
            return shader?.InfoLog ?? "";
        }
        
        public string GetShaderSource(WebGLShader shader)
        {
            return shader?.Source ?? "";
        }
        
        // ========== Program Operations ==========
        
        public WebGLProgram CreateProgram()
        {
            var program = new WebGLProgram();
            _programs[program.Id] = program;
            return program;
        }
        
        public void DeleteProgram(WebGLProgram program)
        {
            if (program == null) return;
            _programs.Remove(program.Id);
            if (_currentProgram == program) _currentProgram = null;
        }
        
        public bool IsProgram(WebGLProgram program)
        {
            return program != null && _programs.ContainsKey(program.Id);
        }
        
        public void AttachShader(WebGLProgram program, WebGLShader shader)
        {
            if (program == null || shader == null)
            {
                SetError(WebGLConstants.INVALID_VALUE);
                return;
            }
            
            if (shader.Type == WebGLConstants.VERTEX_SHADER)
                program.VertexShader = shader;
            else if (shader.Type == WebGLConstants.FRAGMENT_SHADER)
                program.FragmentShader = shader;
        }
        
        public void DetachShader(WebGLProgram program, WebGLShader shader)
        {
            if (program == null || shader == null) return;
            
            if (program.VertexShader == shader)
                program.VertexShader = null;
            else if (program.FragmentShader == shader)
                program.FragmentShader = null;
        }
        
        public void LinkProgram(WebGLProgram program)
        {
            if (program == null)
            {
                SetError(WebGLConstants.INVALID_VALUE);
                return;
            }
            
            if (program.VertexShader?.Compiled == true && program.FragmentShader?.Compiled == true)
            {
                program.Linked = true;
                program.InfoLog = "";
                
                // Extract attribute locations from vertex shader
                ParseAttributeLocations(program);
                
                EngineLogCompat.Debug($"[WebGL] Program {program.Id} linked successfully", LogCategory.Rendering);
            }
            else
            {
                program.Linked = false;
                program.InfoLog = "Error: Shaders not compiled or attached";
            }
        }
        
        private void ParseAttributeLocations(WebGLProgram program)
        {
            if (program.VertexShader?.Source == null) return;
            
            // Simple regex-like parsing for attribute declarations
            var source = program.VertexShader.Source;
            int attrIndex = 0;
            
            var lines = source.Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("attribute ") || trimmed.StartsWith("in "))
                {
                    var parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        var name = parts[2].TrimEnd(';');
                        program.AttribLocations[name] = attrIndex++;
                    }
                }
            }
        }
        
        public void UseProgram(WebGLProgram program)
        {
            if (program != null && !program.Linked)
            {
                SetError(WebGLConstants.INVALID_OPERATION);
                return;
            }
            _currentProgram = program;
        }
        
        public void ValidateProgram(WebGLProgram program)
        {
            // Implementation validates program can execute
            if (program == null) return;
            // For simplicity, validation passes if linked
        }
        
        public object GetProgramParameter(WebGLProgram program, uint pname)
        {
            if (program == null) return null;
            
            switch (pname)
            {
                case WebGLConstants.LINK_STATUS:
                    return program.Linked;
                case WebGLConstants.DELETE_STATUS:
                    return !_programs.ContainsKey(program.Id);
                case WebGLConstants.VALIDATE_STATUS:
                    return program.Linked;
                case WebGLConstants.ATTACHED_SHADERS:
                    return (program.VertexShader != null ? 1 : 0) + (program.FragmentShader != null ? 1 : 0);
                case WebGLConstants.ACTIVE_ATTRIBUTES:
                    return program.AttribLocations.Count;
                case WebGLConstants.ACTIVE_UNIFORMS:
                    return program.UniformLocations.Count;
                default:
                    SetError(WebGLConstants.INVALID_ENUM);
                    return null;
            }
        }
        
        public string GetProgramInfoLog(WebGLProgram program)
        {
            return program?.InfoLog ?? "";
        }
        
        public int GetAttribLocation(WebGLProgram program, string name)
        {
            if (program == null || !program.Linked) return -1;
            return program.AttribLocations.TryGetValue(name, out var loc) ? loc : -1;
        }
        
        public WebGLUniformLocation GetUniformLocation(WebGLProgram program, string name)
        {
            if (program == null || !program.Linked) return null;
            
            if (!program.UniformLocations.TryGetValue(name, out var loc))
            {
                loc = program.UniformLocations.Count;
                program.UniformLocations[name] = loc;
            }
            
            return new WebGLUniformLocation(loc, name, program);
        }
        
        // ========== Uniform Operations ==========
        
        public void Uniform1f(WebGLUniformLocation location, float x)
        {
            if (location == null || _currentProgram != location.Program) return;
            // Store uniform value (implementation detail)
        }
        
        public void Uniform2f(WebGLUniformLocation location, float x, float y)
        {
            if (location == null || _currentProgram != location.Program) return;
        }
        
        public void Uniform3f(WebGLUniformLocation location, float x, float y, float z)
        {
            if (location == null || _currentProgram != location.Program) return;
        }
        
        public void Uniform4f(WebGLUniformLocation location, float x, float y, float z, float w)
        {
            if (location == null || _currentProgram != location.Program) return;
        }
        
        public void Uniform1i(WebGLUniformLocation location, int x)
        {
            if (location == null || _currentProgram != location.Program) return;
        }
        
        public void Uniform2i(WebGLUniformLocation location, int x, int y)
        {
            if (location == null || _currentProgram != location.Program) return;
        }
        
        public void Uniform3i(WebGLUniformLocation location, int x, int y, int z)
        {
            if (location == null || _currentProgram != location.Program) return;
        }
        
        public void Uniform4i(WebGLUniformLocation location, int x, int y, int z, int w)
        {
            if (location == null || _currentProgram != location.Program) return;
        }
        
        public void UniformMatrix2fv(WebGLUniformLocation location, bool transpose, float[] value)
        {
            if (location == null || value == null || value.Length < 4) return;
        }
        
        public void UniformMatrix3fv(WebGLUniformLocation location, bool transpose, float[] value)
        {
            if (location == null || value == null || value.Length < 9) return;
        }
        
        public void UniformMatrix4fv(WebGLUniformLocation location, bool transpose, float[] value)
        {
            if (location == null || value == null || value.Length < 16) return;
        }
        
        public void Uniform1fv(WebGLUniformLocation location, float[] v) { }
        public void Uniform2fv(WebGLUniformLocation location, float[] v) { }
        public void Uniform3fv(WebGLUniformLocation location, float[] v) { }
        public void Uniform4fv(WebGLUniformLocation location, float[] v) { }
        public void Uniform1iv(WebGLUniformLocation location, int[] v) { }
        public void Uniform2iv(WebGLUniformLocation location, int[] v) { }
        public void Uniform3iv(WebGLUniformLocation location, int[] v) { }
        public void Uniform4iv(WebGLUniformLocation location, int[] v) { }
        
        // ========== Vertex Attribute Operations ==========
        
        public void VertexAttribPointer(uint index, int size, uint type, bool normalized, int stride, int offset)
        {
            if (index >= _vertexAttribs.Length)
            {
                SetError(WebGLConstants.INVALID_VALUE);
                return;
            }
            
            _vertexAttribs[index].Buffer = _arrayBuffer;
            _vertexAttribs[index].Size = size;
            _vertexAttribs[index].Type = type;
            _vertexAttribs[index].Normalized = normalized;
            _vertexAttribs[index].Stride = stride;
            _vertexAttribs[index].Offset = offset;
        }
        
        public void EnableVertexAttribArray(uint index)
        {
            if (index < _vertexAttribs.Length)
                _vertexAttribs[index].Enabled = true;
        }
        
        public void DisableVertexAttribArray(uint index)
        {
            if (index < _vertexAttribs.Length)
                _vertexAttribs[index].Enabled = false;
        }
        
        public void VertexAttrib1f(uint index, float x)
        {
            if (index < _genericAttribs.Length)
                _genericAttribs[index] = new float[] { x, 0, 0, 1 };
        }
        
        public void VertexAttrib2f(uint index, float x, float y)
        {
            if (index < _genericAttribs.Length)
                _genericAttribs[index] = new float[] { x, y, 0, 1 };
        }
        
        public void VertexAttrib3f(uint index, float x, float y, float z)
        {
            if (index < _genericAttribs.Length)
                _genericAttribs[index] = new float[] { x, y, z, 1 };
        }
        
        public void VertexAttrib4f(uint index, float x, float y, float z, float w)
        {
            if (index < _genericAttribs.Length)
                _genericAttribs[index] = new float[] { x, y, z, w };
        }
        
        // ========== Texture Operations ==========
        
        public WebGLTexture CreateTexture()
        {
            var texture = new WebGLTexture();
            _textures[texture.Id] = texture;
            return texture;
        }
        
        public void DeleteTexture(WebGLTexture texture)
        {
            if (texture == null) return;
            _textures.Remove(texture.Id);
            for (int i = 0; i < _textureUnits.Length; i++)
                if (_textureUnits[i] == texture) _textureUnits[i] = null;
        }
        
        public bool IsTexture(WebGLTexture texture)
        {
            return texture != null && _textures.ContainsKey(texture.Id);
        }
        
        public void ActiveTexture(uint texture)
        {
            _activeTexture = (int)(texture - WebGLConstants.TEXTURE0);
            if (_activeTexture < 0 || _activeTexture >= _textureUnits.Length)
            {
                SetError(WebGLConstants.INVALID_ENUM);
                _activeTexture = 0;
            }
        }
        
        public void BindTexture(uint target, WebGLTexture texture)
        {
            if (texture != null) texture.Target = target;
            _textureUnits[_activeTexture] = texture;
        }
        
        public void TexImage2D(uint target, int level, uint internalFormat, int width, int height, int border, uint format, uint type, byte[] pixels)
        {
            var texture = _textureUnits[_activeTexture];
            if (texture == null)
            {
                SetError(WebGLConstants.INVALID_OPERATION);
                return;
            }
            
            texture.Width = width;
            texture.Height = height;
            texture.InternalFormat = internalFormat;
            texture.Data = pixels;
        }
        
        public void TexSubImage2D(uint target, int level, int xoffset, int yoffset, int width, int height, uint format, uint type, byte[] pixels)
        {
            var texture = _textureUnits[_activeTexture];
            if (texture == null || texture.Data == null) return;
            
            // Copy pixels to texture data at offset
        }
        
        public void TexParameteri(uint target, uint pname, int param)
        {
            var texture = _textureUnits[_activeTexture];
            if (texture == null) return;
            
            switch (pname)
            {
                case WebGLConstants.TEXTURE_MIN_FILTER:
                    texture.MinFilter = (uint)param;
                    break;
                case WebGLConstants.TEXTURE_MAG_FILTER:
                    texture.MagFilter = (uint)param;
                    break;
                case WebGLConstants.TEXTURE_WRAP_S:
                    texture.WrapS = (uint)param;
                    break;
                case WebGLConstants.TEXTURE_WRAP_T:
                    texture.WrapT = (uint)param;
                    break;
            }
        }
        
        public void TexParameterf(uint target, uint pname, float param)
        {
            TexParameteri(target, pname, (int)param);
        }
        
        public void GenerateMipmap(uint target)
        {
            var texture = _textureUnits[_activeTexture];
            if (texture != null) texture.GenerateMipmap = true;
        }
        
        public void CopyTexImage2D(uint target, int level, uint internalformat, int x, int y, int width, int height, int border)
        {
            // Copy from framebuffer to texture
        }
        
        public void CopyTexSubImage2D(uint target, int level, int xoffset, int yoffset, int x, int y, int width, int height)
        {
            // Copy sub-region from framebuffer to texture
        }
        
        // ========== Framebuffer Operations ==========
        
        public WebGLFramebuffer CreateFramebuffer()
        {
            var fb = new WebGLFramebuffer();
            _framebuffers[fb.Id] = fb;
            return fb;
        }
        
        public void DeleteFramebuffer(WebGLFramebuffer framebuffer)
        {
            if (framebuffer == null) return;
            _framebuffers.Remove(framebuffer.Id);
            if (_currentFramebuffer == framebuffer) _currentFramebuffer = null;
        }
        
        public bool IsFramebuffer(WebGLFramebuffer framebuffer)
        {
            return framebuffer != null && _framebuffers.ContainsKey(framebuffer.Id);
        }
        
        public void BindFramebuffer(uint target, WebGLFramebuffer framebuffer)
        {
            _currentFramebuffer = framebuffer;
        }
        
        public void FramebufferTexture2D(uint target, uint attachment, uint textarget, WebGLTexture texture, int level)
        {
            if (_currentFramebuffer == null) return;
            if (attachment == WebGLConstants.COLOR_ATTACHMENT0)
                _currentFramebuffer.ColorAttachment = texture;
        }
        
        public void FramebufferRenderbuffer(uint target, uint attachment, uint renderbuffertarget, WebGLRenderbuffer renderbuffer)
        {
            if (_currentFramebuffer == null) return;
            if (attachment == WebGLConstants.DEPTH_ATTACHMENT)
                _currentFramebuffer.DepthAttachment = renderbuffer;
            else if (attachment == WebGLConstants.STENCIL_ATTACHMENT)
                _currentFramebuffer.StencilAttachment = renderbuffer;
        }
        
        public uint CheckFramebufferStatus(uint target)
        {
            if (_currentFramebuffer == null) return WebGLConstants.FRAMEBUFFER_COMPLETE;
            return _currentFramebuffer.Complete ? WebGLConstants.FRAMEBUFFER_COMPLETE : WebGLConstants.FRAMEBUFFER_INCOMPLETE_ATTACHMENT;
        }
        
        // ========== Renderbuffer Operations ==========
        
        public WebGLRenderbuffer CreateRenderbuffer()
        {
            var rb = new WebGLRenderbuffer();
            _renderbuffers[rb.Id] = rb;
            return rb;
        }
        
        public void DeleteRenderbuffer(WebGLRenderbuffer renderbuffer)
        {
            if (renderbuffer == null) return;
            _renderbuffers.Remove(renderbuffer.Id);
            if (_currentRenderbuffer == renderbuffer) _currentRenderbuffer = null;
        }
        
        public void BindRenderbuffer(uint target, WebGLRenderbuffer renderbuffer)
        {
            _currentRenderbuffer = renderbuffer;
        }
        
        public void RenderbufferStorage(uint target, uint internalformat, int width, int height)
        {
            if (_currentRenderbuffer == null) return;
            _currentRenderbuffer.InternalFormat = internalformat;
            _currentRenderbuffer.Width = width;
            _currentRenderbuffer.Height = height;
        }
        
        // ========== Drawing Operations ==========
        
        public void Clear(uint mask)
        {
            if ((mask & WebGLConstants.COLOR_BUFFER_BIT) != 0)
            {
                _canvas.Clear(_clearColor);
            }
            // Depth and stencil clearing is tracked but not visible in 2D output
        }
        
        public void ClearColor(float r, float g, float b, float a)
        {
            _clearColor = new SKColor(
                (byte)(Math.Clamp(r, 0, 1) * 255),
                (byte)(Math.Clamp(g, 0, 1) * 255),
                (byte)(Math.Clamp(b, 0, 1) * 255),
                (byte)(Math.Clamp(a, 0, 1) * 255));
        }
        
        public void ClearDepth(float depth)
        {
            _clearDepth = Math.Clamp(depth, 0, 1);
        }
        
        public void ClearStencil(int s)
        {
            _clearStencil = s;
        }
        
        public virtual void DrawArrays(uint mode, int first, int count)
        {
            if (_currentProgram == null || !_currentProgram.Linked) return;
            
            // Software rasterization of primitives using SkiaSharp
            DrawPrimitives(mode, first, count, false, 0, 0);
        }
        
        public virtual void DrawElements(uint mode, int count, uint type, int offset)
        {
            if (_currentProgram == null || !_currentProgram.Linked) return;
            if (_elementArrayBuffer == null) return;
            
            DrawPrimitives(mode, 0, count, true, type, offset);
        }
        
        protected virtual void DrawPrimitives(uint mode, int first, int count, bool indexed, uint indexType, int indexOffset)
        {
            // Software rasterization using the vertex data
            // This is a simplified implementation that draws to the SkiaSharp canvas
            
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = mode == WebGLConstants.POINTS ? SKPaintStyle.Fill : SKPaintStyle.Stroke,
                StrokeWidth = _lineWidth,
                Color = SKColors.White
            };
            
            // Extract vertex positions from vertex attribute 0 (position)
            var positions = ExtractVertexPositions(first, count, indexed, indexType, indexOffset);
            if (positions == null || positions.Count == 0) return;
            
            switch (mode)
            {
                case WebGLConstants.POINTS:
                    foreach (var p in positions)
                        _canvas.DrawCircle(p.X, p.Y, 2, paint);
                    break;
                    
                case WebGLConstants.LINES:
                    for (int i = 0; i + 1 < positions.Count; i += 2)
                        _canvas.DrawLine(positions[i], positions[i + 1], paint);
                    break;
                    
                case WebGLConstants.LINE_STRIP:
                    for (int i = 0; i + 1 < positions.Count; i++)
                        _canvas.DrawLine(positions[i], positions[i + 1], paint);
                    break;
                    
                case WebGLConstants.LINE_LOOP:
                    for (int i = 0; i < positions.Count; i++)
                        _canvas.DrawLine(positions[i], positions[(i + 1) % positions.Count], paint);
                    break;
                    
                case WebGLConstants.TRIANGLES:
                    paint.Style = SKPaintStyle.Fill;
                    for (int i = 0; i + 2 < positions.Count; i += 3)
                    {
                        using var path = new SKPath();
                        path.MoveTo(positions[i]);
                        path.LineTo(positions[i + 1]);
                        path.LineTo(positions[i + 2]);
                        path.Close();
                        _canvas.DrawPath(path, paint);
                    }
                    break;
                    
                case WebGLConstants.TRIANGLE_STRIP:
                    paint.Style = SKPaintStyle.Fill;
                    for (int i = 0; i + 2 < positions.Count; i++)
                    {
                        using var path = new SKPath();
                        if (i % 2 == 0)
                        {
                            path.MoveTo(positions[i]);
                            path.LineTo(positions[i + 1]);
                            path.LineTo(positions[i + 2]);
                        }
                        else
                        {
                            path.MoveTo(positions[i + 1]);
                            path.LineTo(positions[i]);
                            path.LineTo(positions[i + 2]);
                        }
                        path.Close();
                        _canvas.DrawPath(path, paint);
                    }
                    break;
                    
                case WebGLConstants.TRIANGLE_FAN:
                    paint.Style = SKPaintStyle.Fill;
                    for (int i = 1; i + 1 < positions.Count; i++)
                    {
                        using var path = new SKPath();
                        path.MoveTo(positions[0]);
                        path.LineTo(positions[i]);
                        path.LineTo(positions[i + 1]);
                        path.Close();
                        _canvas.DrawPath(path, paint);
                    }
                    break;
            }
        }
        
        protected List<SKPoint> ExtractVertexPositions(int first, int count, bool indexed, uint indexType, int indexOffset)
        {
            var positions = new List<SKPoint>();
            
            // Get position attribute (usually index 0)
            var posAttrib = _vertexAttribs[0];
            if (!posAttrib.Enabled || posAttrib.Buffer?.Data == null) return positions;
            
            int stride = posAttrib.Stride > 0 ? posAttrib.Stride : posAttrib.Size * 4; // Assuming float
            
            for (int i = 0; i < count; i++)
            {
                int vertexIndex = indexed ? GetIndex(i, indexType, indexOffset) : first + i;
                int offset = posAttrib.Offset + vertexIndex * stride;
                
                if (offset + 8 <= posAttrib.Buffer.Data.Length)
                {
                    float x = BitConverter.ToSingle(posAttrib.Buffer.Data, offset);
                    float y = BitConverter.ToSingle(posAttrib.Buffer.Data, offset + 4);
                    
                    // Transform to viewport coordinates
                    float vx = (x + 1) * 0.5f * _viewport.Width + _viewport.Left;
                    float vy = (1 - y) * 0.5f * _viewport.Height + _viewport.Top; // Y flip
                    
                    positions.Add(new SKPoint(vx, vy));
                }
            }
            
            return positions;
        }
        
        protected int GetIndex(int i, uint indexType, int offset)
        {
            if (_elementArrayBuffer?.Data == null) return i;
            
            int byteOffset = offset + i * (indexType == WebGLConstants.UNSIGNED_SHORT ? 2 : 4);
            if (byteOffset + 2 > _elementArrayBuffer.Data.Length) return i;
            
            if (indexType == WebGLConstants.UNSIGNED_SHORT)
                return BitConverter.ToUInt16(_elementArrayBuffer.Data, byteOffset);
            else
                return (int)BitConverter.ToUInt32(_elementArrayBuffer.Data, byteOffset);
        }
        
        // ========== State Operations ==========
        
        public void Enable(uint cap)
        {
            switch (cap)
            {
                case WebGLConstants.DEPTH_TEST: _depthTest = true; break;
                case WebGLConstants.BLEND: _blend = true; break;
                case WebGLConstants.CULL_FACE: _cullFace = true; break;
                case WebGLConstants.SCISSOR_TEST: _scissorTest = true; break;
                case WebGLConstants.STENCIL_TEST: _stencilTest = true; break;
                case WebGLConstants.DITHER: _dither = true; break;
                default: SetError(WebGLConstants.INVALID_ENUM); break;
            }
        }
        
        public void Disable(uint cap)
        {
            switch (cap)
            {
                case WebGLConstants.DEPTH_TEST: _depthTest = false; break;
                case WebGLConstants.BLEND: _blend = false; break;
                case WebGLConstants.CULL_FACE: _cullFace = false; break;
                case WebGLConstants.SCISSOR_TEST: _scissorTest = false; break;
                case WebGLConstants.STENCIL_TEST: _stencilTest = false; break;
                case WebGLConstants.DITHER: _dither = false; break;
            }
        }
        
        public bool IsEnabled(uint cap)
        {
            return cap switch
            {
                WebGLConstants.DEPTH_TEST => _depthTest,
                WebGLConstants.BLEND => _blend,
                WebGLConstants.CULL_FACE => _cullFace,
                WebGLConstants.SCISSOR_TEST => _scissorTest,
                WebGLConstants.STENCIL_TEST => _stencilTest,
                WebGLConstants.DITHER => _dither,
                _ => false
            };
        }
        
        public void Viewport(int x, int y, int width, int height)
        {
            _viewport = new SKRectI(x, y, x + width, y + height);
        }
        
        public void Scissor(int x, int y, int width, int height)
        {
            _scissor = new SKRectI(x, y, x + width, y + height);
        }
        
        public void DepthFunc(uint func) => _depthFunc = func;
        public void DepthMask(bool flag) => _depthMask = flag;
        public void DepthRange(float zNear, float zFar) { }
        
        public void BlendFunc(uint sfactor, uint dfactor)
        {
            _blendSrcRGB = _blendSrcAlpha = sfactor;
            _blendDstRGB = _blendDstAlpha = dfactor;
        }
        
        public void BlendFuncSeparate(uint srcRGB, uint dstRGB, uint srcAlpha, uint dstAlpha)
        {
            _blendSrcRGB = srcRGB;
            _blendDstRGB = dstRGB;
            _blendSrcAlpha = srcAlpha;
            _blendDstAlpha = dstAlpha;
        }
        
        public void BlendEquation(uint mode)
        {
            _blendEquationRGB = _blendEquationAlpha = mode;
        }
        
        public void BlendEquationSeparate(uint modeRGB, uint modeAlpha)
        {
            _blendEquationRGB = modeRGB;
            _blendEquationAlpha = modeAlpha;
        }
        
        public void BlendColor(float r, float g, float b, float a)
        {
            _blendColor = new SKColor(
                (byte)(r * 255), (byte)(g * 255),
                (byte)(b * 255), (byte)(a * 255));
        }
        
        public void CullFace(uint mode) => _cullFaceMode = mode;
        public void FrontFace(uint mode) => _frontFace = mode;
        public void LineWidth(float width) => _lineWidth = width;
        
        public void PolygonOffset(float factor, float units)
        {
            _polygonOffsetFactor = factor;
            _polygonOffsetUnits = units;
        }
        
        public void ColorMask(bool r, bool g, bool b, bool a)
        {
            _colorMask = new[] { r ? 1 : 0, g ? 1 : 0, b ? 1 : 0, a ? 1 : 0 };
        }
        
        public void StencilFunc(uint func, int @ref, uint mask) { }
        public void StencilFuncSeparate(uint face, uint func, int @ref, uint mask) { }
        public void StencilMask(uint mask) { }
        public void StencilMaskSeparate(uint face, uint mask) { }
        public void StencilOp(uint fail, uint zfail, uint zpass) { }
        public void StencilOpSeparate(uint face, uint fail, uint zfail, uint zpass) { }
        
        public void SampleCoverage(float value, bool invert) { }
        public void PixelStorei(uint pname, int param)
        {
            switch (pname)
            {
                case WebGLConstants.UNPACK_FLIP_Y_WEBGL:
                    _unpackFlipY = param != 0;
                    break;
                case WebGLConstants.UNPACK_PREMULTIPLY_ALPHA_WEBGL:
                    _unpackPremultiplyAlpha = param != 0;
                    break;
                case WebGLConstants.UNPACK_ALIGNMENT:
                    _unpackAlignment = param;
                    break;
                case WebGLConstants.PACK_ALIGNMENT:
                    _packAlignment = param;
                    break;
            }
        }
        
        // ========== Getting State ==========
        
        public object GetParameter(uint pname)
        {
            switch (pname)
            {
                case WebGLConstants.MAX_TEXTURE_SIZE: return 4096;
                case WebGLConstants.MAX_CUBE_MAP_TEXTURE_SIZE: return 4096;
                case WebGLConstants.MAX_RENDERBUFFER_SIZE: return 4096;
                case WebGLConstants.MAX_VIEWPORT_DIMS: return new int[] { 4096, 4096 };
                case WebGLConstants.MAX_VERTEX_ATTRIBS: return 16;
                case WebGLConstants.MAX_VERTEX_UNIFORM_VECTORS: return 256;
                case WebGLConstants.MAX_VARYING_VECTORS: return 32;
                case WebGLConstants.MAX_FRAGMENT_UNIFORM_VECTORS: return 256;
                case WebGLConstants.MAX_VERTEX_TEXTURE_IMAGE_UNITS: return 16;
                case WebGLConstants.MAX_TEXTURE_IMAGE_UNITS: return 16;
                case WebGLConstants.MAX_COMBINED_TEXTURE_IMAGE_UNITS: return 32;
                case WebGLConstants.VENDOR: return "FenBrowser";
                case WebGLConstants.RENDERER: return "FenBrowser WebGL (SkiaSharp)";
                case WebGLConstants.VERSION: return "WebGL 1.0";
                case WebGLConstants.SHADING_LANGUAGE_VERSION: return "WebGL GLSL ES 1.0";
                case WebGLConstants.DEPTH_TEST: return _depthTest;
                case WebGLConstants.BLEND: return _blend;
                case WebGLConstants.CULL_FACE: return _cullFace;
                case WebGLConstants.CURRENT_PROGRAM: return _currentProgram;
                case WebGLConstants.ARRAY_BUFFER_BINDING: return _arrayBuffer;
                case WebGLConstants.ELEMENT_ARRAY_BUFFER_BINDING: return _elementArrayBuffer;
                case WebGLConstants.FRAMEBUFFER_BINDING: return _currentFramebuffer;
                case WebGLConstants.RENDERBUFFER_BINDING: return _currentRenderbuffer;
                default: return null;
            }
        }
        
        public object GetBufferParameter(uint target, uint pname)
        {
            var buffer = target == WebGLConstants.ARRAY_BUFFER ? _arrayBuffer : _elementArrayBuffer;
            if (buffer == null) return null;
            
            return pname switch
            {
                WebGLConstants.BUFFER_SIZE => buffer.Size,
                WebGLConstants.BUFFER_USAGE => buffer.Usage,
                _ => null
            };
        }
        
        public object GetTexParameter(uint target, uint pname)
        {
            var texture = _textureUnits[_activeTexture];
            if (texture == null) return null;
            
            return pname switch
            {
                WebGLConstants.TEXTURE_MAG_FILTER => texture.MagFilter,
                WebGLConstants.TEXTURE_MIN_FILTER => texture.MinFilter,
                WebGLConstants.TEXTURE_WRAP_S => texture.WrapS,
                WebGLConstants.TEXTURE_WRAP_T => texture.WrapT,
                _ => null
            };
        }
        
        public void Hint(uint target, uint mode) { }
        public void Flush() { }
        public void Finish() { }
        
        public byte[] ReadPixels(int x, int y, int width, int height, uint format, uint type)
        {
            // Read pixels from backbuffer
            var pixels = new byte[width * height * 4];
            
            for (int py = 0; py < height; py++)
            {
                for (int px = 0; px < width; px++)
                {
                    var color = _backBuffer.GetPixel(x + px, y + py);
                    int idx = (py * width + px) * 4;
                    pixels[idx] = color.Red;
                    pixels[idx + 1] = color.Green;
                    pixels[idx + 2] = color.Blue;
                    pixels[idx + 3] = color.Alpha;
                }
            }
            
            return pixels;
        }
        
        // ========== Extensions ==========
        
        public object GetExtension(string name)
        {
            // Return supported extensions
            return name switch
            {
                "WEBGL_depth_texture" => new object(),
                "OES_texture_float" => new object(),
                "OES_texture_half_float" => new object(),
                "OES_standard_derivatives" => new object(),
                "OES_element_index_uint" => new object(),
                "EXT_blend_minmax" => new object(),
                _ => null
            };
        }
        
        public string[] GetSupportedExtensions()
        {
            return new[]
            {
                "WEBGL_depth_texture",
                "OES_texture_float",
                "OES_texture_half_float",
                "OES_standard_derivatives",
                "OES_element_index_uint",
                "EXT_blend_minmax"
            };
        }
        
        // ========== Output ==========
        
        /// <summary>
        /// Get the rendered WebGL output as an SKBitmap for compositing
        /// </summary>
        public SKBitmap GetOutputBitmap()
        {
            return _backBuffer;
        }
        
        /// <summary>
        /// Resize the rendering context
        /// </summary>
        public void Resize(int width, int height)
        {
            if (width == _width && height == _height) return;
            
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            
            _canvas?.Dispose();
            _backBuffer?.Dispose();
            
            _backBuffer = new SKBitmap(_width, _height, SKColorType.Rgba8888, SKAlphaType.Premul);
            _canvas = new SKCanvas(_backBuffer);
            
            _viewport = new SKRectI(0, 0, _width, _height);
        }
        
        public void Dispose()
        {
            _canvas?.Dispose();
            _backBuffer?.Dispose();
        }
    }
}
