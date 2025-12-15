using System;
using System.Collections.Generic;
using SkiaSharp;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Rendering.WebGL
{
    /// <summary>
    /// WebGL 2.0 Rendering Context implementation extending WebGL 1.0.
    /// Adds VAOs, uniform buffer objects, transform feedback, instanced rendering, and more.
    /// </summary>
    public class WebGL2RenderingContext : WebGLRenderingContext
    {
        // WebGL 2.0 specific objects
        private Dictionary<uint, WebGLVertexArrayObject> _vaos = new();
        private Dictionary<uint, WebGLSampler> _samplers = new();
        private Dictionary<uint, WebGLTransformFeedback> _transformFeedbacks = new();
        private Dictionary<uint, WebGLQuery> _queries = new();
        private Dictionary<uint, WebGLSync> _syncs = new();
        
        // Current bindings
        private WebGLVertexArrayObject _currentVao;
        private WebGLBuffer _uniformBuffer;
        private WebGLBuffer _copyReadBuffer;
        private WebGLBuffer _copyWriteBuffer;
        private WebGLBuffer _transformFeedbackBuffer;
        private WebGLBuffer _pixelPackBuffer;
        private WebGLBuffer _pixelUnpackBuffer;
        private WebGLTransformFeedback _currentTransformFeedback;
        
        // WebGL 2.0 state
        private int[] _drawBuffers;
        private WebGLSampler[] _samplerUnits = new WebGLSampler[32];

        public WebGL2RenderingContext(int width, int height) : base(width, height)
        {
            // Create default VAO
            var defaultVao = new WebGLVertexArrayObject();
            _vaos[defaultVao.Id] = defaultVao;
            _currentVao = defaultVao;
            
            FenLogger.Debug($"[WebGL2] Created context {width}x{height}", LogCategory.Rendering);
        }

        // ========== Vertex Array Objects ==========
        
        public WebGLVertexArrayObject CreateVertexArray()
        {
            var vao = new WebGLVertexArrayObject();
            _vaos[vao.Id] = vao;
            return vao;
        }
        
        public void DeleteVertexArray(WebGLVertexArrayObject vao)
        {
            if (vao == null) return;
            _vaos.Remove(vao.Id);
            if (_currentVao == vao) _currentVao = null;
        }
        
        public bool IsVertexArray(WebGLVertexArrayObject vao)
        {
            return vao != null && _vaos.ContainsKey(vao.Id);
        }
        
        public void BindVertexArray(WebGLVertexArrayObject vao)
        {
            _currentVao = vao;
        }
        
        // ========== Extended Buffer Targets ==========
        
        public new void BindBuffer(uint target, WebGLBuffer buffer)
        {
            if (buffer != null) buffer.Target = target;
            
            switch (target)
            {
                case WebGLConstants.ARRAY_BUFFER:
                    _arrayBuffer = buffer;
                    break;
                case WebGLConstants.ELEMENT_ARRAY_BUFFER:
                    _elementArrayBuffer = buffer;
                    if (_currentVao != null) _currentVao.ElementArrayBuffer = buffer;
                    break;
                case WebGLConstants.UNIFORM_BUFFER:
                    _uniformBuffer = buffer;
                    break;
                case WebGLConstants.COPY_READ_BUFFER:
                    _copyReadBuffer = buffer;
                    break;
                case WebGLConstants.COPY_WRITE_BUFFER:
                    _copyWriteBuffer = buffer;
                    break;
                case WebGLConstants.TRANSFORM_FEEDBACK_BUFFER:
                    _transformFeedbackBuffer = buffer;
                    break;
                case WebGLConstants.PIXEL_PACK_BUFFER:
                    _pixelPackBuffer = buffer;
                    break;
                case WebGLConstants.PIXEL_UNPACK_BUFFER:
                    _pixelUnpackBuffer = buffer;
                    break;
                default:
                    SetError(WebGLConstants.INVALID_ENUM);
                    break;
            }
        }
        
        public void BindBufferBase(uint target, uint index, WebGLBuffer buffer)
        {
            BindBuffer(target, buffer);
            // Also bind to indexed binding point
        }
        
        public void BindBufferRange(uint target, uint index, WebGLBuffer buffer, int offset, int size)
        {
            BindBuffer(target, buffer);
            // Also bind range to indexed binding point
        }
        
        // ========== Buffer Copy ==========
        
        public void CopyBufferSubData(uint readTarget, uint writeTarget, int readOffset, int writeOffset, int size)
        {
            WebGLBuffer readBuffer = GetBufferForTarget(readTarget);
            WebGLBuffer writeBuffer = GetBufferForTarget(writeTarget);
            
            if (readBuffer?.Data == null || writeBuffer?.Data == null)
            {
                SetError(WebGLConstants.INVALID_OPERATION);
                return;
            }
            
            if (readOffset < 0 || writeOffset < 0 || size < 0 ||
                readOffset + size > readBuffer.Data.Length ||
                writeOffset + size > writeBuffer.Data.Length)
            {
                SetError(WebGLConstants.INVALID_VALUE);
                return;
            }
            
            Array.Copy(readBuffer.Data, readOffset, writeBuffer.Data, writeOffset, size);
        }
        
        private WebGLBuffer GetBufferForTarget(uint target)
        {
            return target switch
            {
                WebGLConstants.ARRAY_BUFFER => _arrayBuffer,
                WebGLConstants.ELEMENT_ARRAY_BUFFER => _elementArrayBuffer,
                WebGLConstants.UNIFORM_BUFFER => _uniformBuffer,
                WebGLConstants.COPY_READ_BUFFER => _copyReadBuffer,
                WebGLConstants.COPY_WRITE_BUFFER => _copyWriteBuffer,
                WebGLConstants.TRANSFORM_FEEDBACK_BUFFER => _transformFeedbackBuffer,
                WebGLConstants.PIXEL_PACK_BUFFER => _pixelPackBuffer,
                WebGLConstants.PIXEL_UNPACK_BUFFER => _pixelUnpackBuffer,
                _ => null
            };
        }
        
        // ========== Uniform Buffer Objects ==========
        
        public uint GetUniformBlockIndex(WebGLProgram program, string uniformBlockName)
        {
            // Return index of uniform block in program
            return 0; // Simplified
        }
        
        public void UniformBlockBinding(WebGLProgram program, uint uniformBlockIndex, uint uniformBlockBinding)
        {
            // Bind uniform block to binding point
        }
        
        public object GetActiveUniformBlockParameter(WebGLProgram program, uint uniformBlockIndex, uint pname)
        {
            return null; // Simplified
        }
        
        public string GetActiveUniformBlockName(WebGLProgram program, uint uniformBlockIndex)
        {
            return ""; // Simplified
        }
        
        // ========== Transform Feedback ==========
        
        public WebGLTransformFeedback CreateTransformFeedback()
        {
            var tf = new WebGLTransformFeedback();
            _transformFeedbacks[tf.Id] = tf;
            return tf;
        }
        
        public void DeleteTransformFeedback(WebGLTransformFeedback tf)
        {
            if (tf == null) return;
            _transformFeedbacks.Remove(tf.Id);
            if (_currentTransformFeedback == tf) _currentTransformFeedback = null;
        }
        
        public bool IsTransformFeedback(WebGLTransformFeedback tf)
        {
            return tf != null && _transformFeedbacks.ContainsKey(tf.Id);
        }
        
        public void BindTransformFeedback(uint target, WebGLTransformFeedback tf)
        {
            _currentTransformFeedback = tf;
        }
        
        public void BeginTransformFeedback(uint primitiveMode)
        {
            if (_currentTransformFeedback != null)
            {
                _currentTransformFeedback.Active = true;
                _currentTransformFeedback.Paused = false;
            }
        }
        
        public void EndTransformFeedback()
        {
            if (_currentTransformFeedback != null)
            {
                _currentTransformFeedback.Active = false;
            }
        }
        
        public void PauseTransformFeedback()
        {
            if (_currentTransformFeedback != null)
                _currentTransformFeedback.Paused = true;
        }
        
        public void ResumeTransformFeedback()
        {
            if (_currentTransformFeedback != null)
                _currentTransformFeedback.Paused = false;
        }
        
        public void TransformFeedbackVaryings(WebGLProgram program, string[] varyings, uint bufferMode)
        {
            // Store transform feedback varyings for program
        }
        
        public WebGLActiveInfo GetTransformFeedbackVarying(WebGLProgram program, uint index)
        {
            return null; // Simplified
        }
        
        // ========== Instanced Rendering ==========
        
        public void DrawArraysInstanced(uint mode, int first, int count, int instanceCount)
        {
            for (int i = 0; i < instanceCount; i++)
            {
                DrawArrays(mode, first, count);
            }
        }
        
        public void DrawElementsInstanced(uint mode, int count, uint type, int offset, int instanceCount)
        {
            for (int i = 0; i < instanceCount; i++)
            {
                DrawElements(mode, count, type, offset);
            }
        }
        
        public void DrawRangeElements(uint mode, uint start, uint end, int count, uint type, int offset)
        {
            DrawElements(mode, count, type, offset);
        }
        
        public void VertexAttribDivisor(uint index, uint divisor)
        {
            if (index < _vertexAttribs.Length)
                _vertexAttribs[index].Divisor = (int)divisor;
        }
        
        // ========== Multiple Render Targets ==========
        
        public void DrawBuffers(uint[] buffers)
        {
            _drawBuffers = new int[buffers.Length];
            for (int i = 0; i < buffers.Length; i++)
                _drawBuffers[i] = (int)buffers[i];
        }
        
        public void ClearBufferfv(uint buffer, int drawbuffer, float[] values)
        {
            if (buffer == WebGLConstants.COLOR_BUFFER_BIT && values.Length >= 4)
            {
                ClearColor(values[0], values[1], values[2], values[3]);
                Clear(WebGLConstants.COLOR_BUFFER_BIT);
            }
        }
        
        public void ClearBufferiv(uint buffer, int drawbuffer, int[] values)
        {
            if (buffer == WebGLConstants.STENCIL_BUFFER_BIT && values.Length >= 1)
            {
                ClearStencil(values[0]);
                Clear(WebGLConstants.STENCIL_BUFFER_BIT);
            }
        }
        
        public void ClearBufferuiv(uint buffer, int drawbuffer, uint[] values) { }
        
        public void ClearBufferfi(uint buffer, int drawbuffer, float depth, int stencil)
        {
            ClearDepth(depth);
            ClearStencil(stencil);
            Clear(WebGLConstants.DEPTH_BUFFER_BIT | WebGLConstants.STENCIL_BUFFER_BIT);
        }
        
        // ========== Sampler Objects ==========
        
        public WebGLSampler CreateSampler()
        {
            var sampler = new WebGLSampler();
            _samplers[sampler.Id] = sampler;
            return sampler;
        }
        
        public void DeleteSampler(WebGLSampler sampler)
        {
            if (sampler == null) return;
            _samplers.Remove(sampler.Id);
            for (int i = 0; i < _samplerUnits.Length; i++)
                if (_samplerUnits[i] == sampler) _samplerUnits[i] = null;
        }
        
        public bool IsSampler(WebGLSampler sampler)
        {
            return sampler != null && _samplers.ContainsKey(sampler.Id);
        }
        
        public void BindSampler(uint unit, WebGLSampler sampler)
        {
            if (unit < _samplerUnits.Length)
                _samplerUnits[unit] = sampler;
        }
        
        public void SamplerParameteri(WebGLSampler sampler, uint pname, int param)
        {
            if (sampler == null) return;
            
            switch (pname)
            {
                case WebGLConstants.TEXTURE_MIN_FILTER:
                    sampler.MinFilter = (uint)param;
                    break;
                case WebGLConstants.TEXTURE_MAG_FILTER:
                    sampler.MagFilter = (uint)param;
                    break;
                case WebGLConstants.TEXTURE_WRAP_S:
                    sampler.WrapS = (uint)param;
                    break;
                case WebGLConstants.TEXTURE_WRAP_T:
                    sampler.WrapT = (uint)param;
                    break;
                case WebGLConstants.TEXTURE_WRAP_R:
                    sampler.WrapR = (uint)param;
                    break;
            }
        }
        
        public void SamplerParameterf(WebGLSampler sampler, uint pname, float param)
        {
            if (sampler == null) return;
            
            switch (pname)
            {
                case 0x813A: // GL_TEXTURE_MIN_LOD
                    sampler.MinLod = param;
                    break;
                case 0x813B: // GL_TEXTURE_MAX_LOD
                    sampler.MaxLod = param;
                    break;
            }
        }
        
        public object GetSamplerParameter(WebGLSampler sampler, uint pname)
        {
            if (sampler == null) return null;
            
            return pname switch
            {
                WebGLConstants.TEXTURE_MIN_FILTER => sampler.MinFilter,
                WebGLConstants.TEXTURE_MAG_FILTER => sampler.MagFilter,
                WebGLConstants.TEXTURE_WRAP_S => sampler.WrapS,
                WebGLConstants.TEXTURE_WRAP_T => sampler.WrapT,
                WebGLConstants.TEXTURE_WRAP_R => sampler.WrapR,
                _ => null
            };
        }
        
        // ========== Query Objects ==========
        
        public WebGLQuery CreateQuery()
        {
            var query = new WebGLQuery();
            _queries[query.Id] = query;
            return query;
        }
        
        public void DeleteQuery(WebGLQuery query)
        {
            if (query == null) return;
            _queries.Remove(query.Id);
        }
        
        public bool IsQuery(WebGLQuery query)
        {
            return query != null && _queries.ContainsKey(query.Id);
        }
        
        public void BeginQuery(uint target, WebGLQuery query)
        {
            if (query != null)
            {
                query.Target = target;
                query.Available = false;
            }
        }
        
        public void EndQuery(uint target)
        {
            // Mark query as available
        }
        
        public object GetQueryParameter(WebGLQuery query, uint pname)
        {
            if (query == null) return null;
            
            return pname switch
            {
                0x8867 => query.Available, // GL_QUERY_RESULT_AVAILABLE
                0x8866 => query.Result,    // GL_QUERY_RESULT
                _ => null
            };
        }
        
        // ========== Sync Objects ==========
        
        public WebGLSync FenceSync(uint condition, uint flags)
        {
            var sync = new WebGLSync();
            _syncs[sync.Id] = sync;
            return sync;
        }
        
        public void DeleteSync(WebGLSync sync)
        {
            if (sync == null) return;
            _syncs.Remove(sync.Id);
        }
        
        public bool IsSync(WebGLSync sync)
        {
            return sync != null && _syncs.ContainsKey(sync.Id);
        }
        
        public uint ClientWaitSync(WebGLSync sync, uint flags, ulong timeout)
        {
            // In software rendering, sync is always signaled immediately
            if (sync != null) sync.Signaled = true;
            return WebGLConstants.SIGNALED;
        }
        
        public void WaitSync(WebGLSync sync, uint flags, long timeout)
        {
            // GPU wait - no-op in software
            if (sync != null) sync.Signaled = true;
        }
        
        public object GetSyncParameter(WebGLSync sync, uint pname)
        {
            if (sync == null) return null;
            
            return pname switch
            {
                WebGLConstants.SYNC_STATUS => sync.Signaled ? WebGLConstants.SIGNALED : WebGLConstants.UNSIGNALED,
                WebGLConstants.SYNC_CONDITION => WebGLConstants.SYNC_GPU_COMMANDS_COMPLETE,
                WebGLConstants.SYNC_FLAGS => 0u,
                _ => null
            };
        }
        
        // ========== 3D Textures ==========
        
        public void TexImage3D(uint target, int level, int internalformat, int width, int height, int depth, int border, uint format, uint type, byte[] pixels)
        {
            // 3D texture support
        }
        
        public void TexSubImage3D(uint target, int level, int xoffset, int yoffset, int zoffset, int width, int height, int depth, uint format, uint type, byte[] pixels)
        {
            // 3D texture sub-image
        }
        
        public void CopyTexSubImage3D(uint target, int level, int xoffset, int yoffset, int zoffset, int x, int y, int width, int height)
        {
            // Copy to 3D texture
        }
        
        public void CompressedTexImage3D(uint target, int level, uint internalformat, int width, int height, int depth, int border, byte[] data)
        {
            // Compressed 3D texture
        }
        
        public void CompressedTexSubImage3D(uint target, int level, int xoffset, int yoffset, int zoffset, int width, int height, int depth, uint format, byte[] data)
        {
            // Compressed 3D texture sub-image
        }
        
        // ========== Texture Storage ==========
        
        public void TexStorage2D(uint target, int levels, uint internalformat, int width, int height)
        {
            // Immutable texture storage
        }
        
        public void TexStorage3D(uint target, int levels, uint internalformat, int width, int height, int depth)
        {
            // Immutable 3D texture storage
        }
        
        // ========== Integer Uniforms ==========
        
        public void Uniform1ui(WebGLUniformLocation location, uint v0) { }
        public void Uniform2ui(WebGLUniformLocation location, uint v0, uint v1) { }
        public void Uniform3ui(WebGLUniformLocation location, uint v0, uint v1, uint v2) { }
        public void Uniform4ui(WebGLUniformLocation location, uint v0, uint v1, uint v2, uint v3) { }
        public void Uniform1uiv(WebGLUniformLocation location, uint[] value) { }
        public void Uniform2uiv(WebGLUniformLocation location, uint[] value) { }
        public void Uniform3uiv(WebGLUniformLocation location, uint[] value) { }
        public void Uniform4uiv(WebGLUniformLocation location, uint[] value) { }
        
        // ========== Additional Matrix Uniforms ==========
        
        public void UniformMatrix2x3fv(WebGLUniformLocation location, bool transpose, float[] value) { }
        public void UniformMatrix3x2fv(WebGLUniformLocation location, bool transpose, float[] value) { }
        public void UniformMatrix2x4fv(WebGLUniformLocation location, bool transpose, float[] value) { }
        public void UniformMatrix4x2fv(WebGLUniformLocation location, bool transpose, float[] value) { }
        public void UniformMatrix3x4fv(WebGLUniformLocation location, bool transpose, float[] value) { }
        public void UniformMatrix4x3fv(WebGLUniformLocation location, bool transpose, float[] value) { }
        
        // ========== Integer Vertex Attributes ==========
        
        public void VertexAttribI4i(uint index, int x, int y, int z, int w) { }
        public void VertexAttribI4ui(uint index, uint x, uint y, uint z, uint w) { }
        public void VertexAttribI4iv(uint index, int[] values) { }
        public void VertexAttribI4uiv(uint index, uint[] values) { }
        public void VertexAttribIPointer(uint index, int size, uint type, int stride, int offset)
        {
            if (index >= _vertexAttribs.Length) return;
            
            _vertexAttribs[index].Buffer = _arrayBuffer;
            _vertexAttribs[index].Size = size;
            _vertexAttribs[index].Type = type;
            _vertexAttribs[index].Normalized = false;
            _vertexAttribs[index].Stride = stride;
            _vertexAttribs[index].Offset = offset;
        }
        
        // ========== Framebuffer Operations ==========
        
        public void BlitFramebuffer(int srcX0, int srcY0, int srcX1, int srcY1, int dstX0, int dstY0, int dstX1, int dstY1, uint mask, uint filter)
        {
            // Blit framebuffer contents
        }
        
        public void InvalidateFramebuffer(uint target, uint[] attachments) { }
        public void InvalidateSubFramebuffer(uint target, uint[] attachments, int x, int y, int width, int height) { }
        
        public void ReadBuffer(uint src) { }
        
        // ========== Renderbuffer Storage ==========
        
        public void RenderbufferStorageMultisample(uint target, int samples, uint internalformat, int width, int height)
        {
            if (_currentRenderbuffer == null) return;
            _currentRenderbuffer.InternalFormat = internalformat;
            _currentRenderbuffer.Width = width;
            _currentRenderbuffer.Height = height;
        }
        
        public object GetInternalformatParameter(uint target, uint internalformat, uint pname)
        {
            return null; // Simplified
        }
        
        // ========== Get Parameter Extensions ==========
        
        public new object GetParameter(uint pname)
        {
            switch (pname)
            {
                case WebGLConstants.VERSION:
                    return "WebGL 2.0";
                case WebGLConstants.SHADING_LANGUAGE_VERSION:
                    return "WebGL GLSL ES 3.00";
                default:
                    return base.GetParameter(pname);
            }
        }
        
        public object GetIndexedParameter(uint target, uint index)
        {
            return null; // Simplified
        }
        
        public uint[] GetUniformIndices(WebGLProgram program, string[] uniformNames)
        {
            return new uint[uniformNames.Length]; // Simplified
        }
        
        public object GetActiveUniforms(WebGLProgram program, uint[] uniformIndices, uint pname)
        {
            return null; // Simplified
        }
        
        // ========== Miscellaneous ==========
        
        public string GetFragDataLocation(WebGLProgram program, string name)
        {
            return null; // Simplified - would return frag data location
        }
        
        public byte[] GetBufferSubData(uint target, int srcByteOffset, int length)
        {
            var buffer = GetBufferForTarget(target);
            if (buffer?.Data == null) return null;
            
            if (srcByteOffset < 0 || srcByteOffset + length > buffer.Data.Length)
                return null;
            
            var result = new byte[length];
            Array.Copy(buffer.Data, srcByteOffset, result, 0, length);
            return result;
        }
    }
}
