using System;
using System.Collections.Generic;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.WebGL
{
    /// <summary>
    /// WebGL buffer object wrapper
    /// </summary>
    public class WebGLBuffer
    {
        private static uint _nextId = 1;
        
        public uint Id { get; }
        public uint Target { get; set; } // GL_ARRAY_BUFFER, GL_ELEMENT_ARRAY_BUFFER
        public byte[] Data { get; set; }
        public uint Usage { get; set; } // GL_STATIC_DRAW, GL_DYNAMIC_DRAW, etc.
        public int Size => Data?.Length ?? 0;

        public WebGLBuffer()
        {
            Id = _nextId++;
        }
    }

    /// <summary>
    /// WebGL shader object wrapper
    /// </summary>
    public class WebGLShader
    {
        private static uint _nextId = 1;

        public uint Id { get; }
        public uint Type { get; } // GL_VERTEX_SHADER or GL_FRAGMENT_SHADER
        public string Source { get; set; }
        public bool Compiled { get; set; }
        public string InfoLog { get; set; } = "";

        public WebGLShader(uint type)
        {
            Id = _nextId++;
            Type = type;
        }
    }

    /// <summary>
    /// WebGL program object wrapper
    /// </summary>
    public class WebGLProgram
    {
        private static uint _nextId = 1;

        public uint Id { get; }
        public WebGLShader VertexShader { get; set; }
        public WebGLShader FragmentShader { get; set; }
        public bool Linked { get; set; }
        public string InfoLog { get; set; } = "";
        public Dictionary<string, int> UniformLocations { get; } = new();
        public Dictionary<string, int> AttribLocations { get; } = new();

        public WebGLProgram()
        {
            Id = _nextId++;
        }
    }

    /// <summary>
    /// WebGL texture object wrapper
    /// </summary>
    public class WebGLTexture
    {
        private static uint _nextId = 1;

        public uint Id { get; }
        public uint Target { get; set; } // GL_TEXTURE_2D, GL_TEXTURE_CUBE_MAP
        public int Width { get; set; }
        public int Height { get; set; }
        public uint InternalFormat { get; set; }
        public byte[] Data { get; set; }
        public bool GenerateMipmap { get; set; }
        
        // Texture parameters
        public uint MinFilter { get; set; } = WebGLConstants.LINEAR;
        public uint MagFilter { get; set; } = WebGLConstants.LINEAR;
        public uint WrapS { get; set; } = WebGLConstants.REPEAT;
        public uint WrapT { get; set; } = WebGLConstants.REPEAT;

        public WebGLTexture()
        {
            Id = _nextId++;
        }
    }

    /// <summary>
    /// WebGL framebuffer object wrapper
    /// </summary>
    public class WebGLFramebuffer
    {
        private static uint _nextId = 1;

        public uint Id { get; }
        public WebGLTexture ColorAttachment { get; set; }
        public WebGLRenderbuffer DepthAttachment { get; set; }
        public WebGLRenderbuffer StencilAttachment { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Complete { get; set; }

        public WebGLFramebuffer()
        {
            Id = _nextId++;
        }
    }

    /// <summary>
    /// WebGL renderbuffer object wrapper
    /// </summary>
    public class WebGLRenderbuffer
    {
        private static uint _nextId = 1;

        public uint Id { get; }
        public uint InternalFormat { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public WebGLRenderbuffer()
        {
            Id = _nextId++;
        }
    }

    /// <summary>
    /// WebGL uniform location wrapper
    /// </summary>
    public class WebGLUniformLocation
    {
        public int Location { get; set; }
        public string Name { get; set; }
        public WebGLProgram Program { get; set; }

        public WebGLUniformLocation(int location, string name, WebGLProgram program)
        {
            Location = location;
            Name = name;
            Program = program;
        }
    }

    /// <summary>
    /// WebGL active info (for uniform/attribute queries)
    /// </summary>
    public class WebGLActiveInfo
    {
        public string Name { get; set; }
        public int Size { get; set; }
        public uint Type { get; set; }
    }

    /// <summary>
    /// WebGL shader precision format
    /// </summary>
    public class WebGLShaderPrecisionFormat
    {
        public int RangeMin { get; set; }
        public int RangeMax { get; set; }
        public int Precision { get; set; }
    }

    /// <summary>
    /// WebGL 2.0 Vertex Array Object
    /// </summary>
    public class WebGLVertexArrayObject
    {
        private static uint _nextId = 1;

        public uint Id { get; }
        public Dictionary<uint, VertexAttribState> Attributes { get; } = new();
        public WebGLBuffer ElementArrayBuffer { get; set; }

        public WebGLVertexArrayObject()
        {
            Id = _nextId++;
        }
    }

    /// <summary>
    /// State for a single vertex attribute
    /// </summary>
    public class VertexAttribState
    {
        public bool Enabled { get; set; }
        public WebGLBuffer Buffer { get; set; }
        public int Size { get; set; }
        public uint Type { get; set; }
        public bool Normalized { get; set; }
        public int Stride { get; set; }
        public int Offset { get; set; }
        public int Divisor { get; set; } // For instanced rendering
    }

    /// <summary>
    /// WebGL 2.0 Sampler Object
    /// </summary>
    public class WebGLSampler
    {
        private static uint _nextId = 1;

        public uint Id { get; }
        public uint MinFilter { get; set; } = WebGLConstants.LINEAR;
        public uint MagFilter { get; set; } = WebGLConstants.LINEAR;
        public uint WrapS { get; set; } = WebGLConstants.REPEAT;
        public uint WrapT { get; set; } = WebGLConstants.REPEAT;
        public uint WrapR { get; set; } = WebGLConstants.REPEAT;
        public uint CompareMode { get; set; }
        public uint CompareFunc { get; set; }
        public float MinLod { get; set; } = -1000;
        public float MaxLod { get; set; } = 1000;

        public WebGLSampler()
        {
            Id = _nextId++;
        }
    }

    /// <summary>
    /// WebGL 2.0 Transform Feedback Object
    /// </summary>
    public class WebGLTransformFeedback
    {
        private static uint _nextId = 1;

        public uint Id { get; }
        public bool Active { get; set; }
        public bool Paused { get; set; }
        public Dictionary<int, WebGLBuffer> BoundBuffers { get; } = new();

        public WebGLTransformFeedback()
        {
            Id = _nextId++;
        }
    }

    /// <summary>
    /// WebGL 2.0 Query Object
    /// </summary>
    public class WebGLQuery
    {
        private static uint _nextId = 1;

        public uint Id { get; }
        public uint Target { get; set; }
        public bool Available { get; set; }
        public ulong Result { get; set; }

        public WebGLQuery()
        {
            Id = _nextId++;
        }
    }

    /// <summary>
    /// WebGL 2.0 Sync Object
    /// </summary>
    public class WebGLSync
    {
        private static uint _nextId = 1;

        public uint Id { get; }
        public uint Status { get; set; } = WebGLConstants.UNSIGNALED;
        public bool Signaled { get; set; }

        public WebGLSync()
        {
            Id = _nextId++;
        }
    }
}
