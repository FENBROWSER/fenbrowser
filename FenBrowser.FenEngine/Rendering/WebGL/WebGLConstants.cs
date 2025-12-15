namespace FenBrowser.FenEngine.Rendering.WebGL
{
    /// <summary>
    /// WebGL constants matching the WebGL specification
    /// </summary>
    public static class WebGLConstants
    {
        // Clearing buffers
        public const uint DEPTH_BUFFER_BIT = 0x00000100;
        public const uint STENCIL_BUFFER_BIT = 0x00000400;
        public const uint COLOR_BUFFER_BIT = 0x00004000;

        // Rendering primitives
        public const uint POINTS = 0x0000;
        public const uint LINES = 0x0001;
        public const uint LINE_LOOP = 0x0002;
        public const uint LINE_STRIP = 0x0003;
        public const uint TRIANGLES = 0x0004;
        public const uint TRIANGLE_STRIP = 0x0005;
        public const uint TRIANGLE_FAN = 0x0006;

        // Blending modes
        public const uint ZERO = 0;
        public const uint ONE = 1;
        public const uint SRC_COLOR = 0x0300;
        public const uint ONE_MINUS_SRC_COLOR = 0x0301;
        public const uint SRC_ALPHA = 0x0302;
        public const uint ONE_MINUS_SRC_ALPHA = 0x0303;
        public const uint DST_ALPHA = 0x0304;
        public const uint ONE_MINUS_DST_ALPHA = 0x0305;
        public const uint DST_COLOR = 0x0306;
        public const uint ONE_MINUS_DST_COLOR = 0x0307;
        public const uint SRC_ALPHA_SATURATE = 0x0308;
        public const uint CONSTANT_COLOR = 0x8001;
        public const uint ONE_MINUS_CONSTANT_COLOR = 0x8002;
        public const uint CONSTANT_ALPHA = 0x8003;
        public const uint ONE_MINUS_CONSTANT_ALPHA = 0x8004;

        // Blending equations
        public const uint FUNC_ADD = 0x8006;
        public const uint FUNC_SUBTRACT = 0x800A;
        public const uint FUNC_REVERSE_SUBTRACT = 0x800B;
        public const uint MIN = 0x8007;
        public const uint MAX = 0x8008;

        // Buffer objects
        public const uint ARRAY_BUFFER = 0x8892;
        public const uint ELEMENT_ARRAY_BUFFER = 0x8893;
        public const uint ARRAY_BUFFER_BINDING = 0x8894;
        public const uint ELEMENT_ARRAY_BUFFER_BINDING = 0x8895;
        public const uint STREAM_DRAW = 0x88E0;
        public const uint STATIC_DRAW = 0x88E4;
        public const uint DYNAMIC_DRAW = 0x88E8;
        public const uint BUFFER_SIZE = 0x8764;
        public const uint BUFFER_USAGE = 0x8765;

        // WebGL 2.0 buffer targets
        public const uint COPY_READ_BUFFER = 0x8F36;
        public const uint COPY_WRITE_BUFFER = 0x8F37;
        public const uint TRANSFORM_FEEDBACK_BUFFER = 0x8C8E;
        public const uint UNIFORM_BUFFER = 0x8A11;
        public const uint PIXEL_PACK_BUFFER = 0x88EB;
        public const uint PIXEL_UNPACK_BUFFER = 0x88EC;

        // Culling
        public const uint CULL_FACE = 0x0B44;
        public const uint FRONT = 0x0404;
        public const uint BACK = 0x0405;
        public const uint FRONT_AND_BACK = 0x0408;

        // Enabling/Disabling
        public const uint BLEND = 0x0BE2;
        public const uint DEPTH_TEST = 0x0B71;
        public const uint DITHER = 0x0BD0;
        public const uint POLYGON_OFFSET_FILL = 0x8037;
        public const uint SAMPLE_ALPHA_TO_COVERAGE = 0x809E;
        public const uint SAMPLE_COVERAGE = 0x80A0;
        public const uint SCISSOR_TEST = 0x0C11;
        public const uint STENCIL_TEST = 0x0B90;

        // Errors
        public const uint NO_ERROR = 0;
        public const uint INVALID_ENUM = 0x0500;
        public const uint INVALID_VALUE = 0x0501;
        public const uint INVALID_OPERATION = 0x0502;
        public const uint OUT_OF_MEMORY = 0x0505;
        public const uint INVALID_FRAMEBUFFER_OPERATION = 0x0506;
        public const uint CONTEXT_LOST_WEBGL = 0x9242;

        // Front face direction
        public const uint CW = 0x0900;
        public const uint CCW = 0x0901;

        // Hints
        public const uint DONT_CARE = 0x1100;
        public const uint FASTEST = 0x1101;
        public const uint NICEST = 0x1102;
        public const uint GENERATE_MIPMAP_HINT = 0x8192;

        // Data types
        public const uint BYTE = 0x1400;
        public const uint UNSIGNED_BYTE = 0x1401;
        public const uint SHORT = 0x1402;
        public const uint UNSIGNED_SHORT = 0x1403;
        public const uint INT = 0x1404;
        public const uint UNSIGNED_INT = 0x1405;
        public const uint FLOAT = 0x1406;
        public const uint HALF_FLOAT = 0x140B;

        // Pixel formats
        public const uint DEPTH_COMPONENT = 0x1902;
        public const uint ALPHA = 0x1906;
        public const uint RGB = 0x1907;
        public const uint RGBA = 0x1908;
        public const uint LUMINANCE = 0x1909;
        public const uint LUMINANCE_ALPHA = 0x190A;

        // Pixel types
        public const uint UNSIGNED_SHORT_4_4_4_4 = 0x8033;
        public const uint UNSIGNED_SHORT_5_5_5_1 = 0x8034;
        public const uint UNSIGNED_SHORT_5_6_5 = 0x8363;

        // Shaders
        public const uint FRAGMENT_SHADER = 0x8B30;
        public const uint VERTEX_SHADER = 0x8B31;
        public const uint COMPILE_STATUS = 0x8B81;
        public const uint DELETE_STATUS = 0x8B80;
        public const uint LINK_STATUS = 0x8B82;
        public const uint VALIDATE_STATUS = 0x8B83;
        public const uint ATTACHED_SHADERS = 0x8B85;
        public const uint ACTIVE_ATTRIBUTES = 0x8B89;
        public const uint ACTIVE_UNIFORMS = 0x8B86;
        public const uint MAX_VERTEX_ATTRIBS = 0x8869;
        public const uint MAX_VERTEX_UNIFORM_VECTORS = 0x8DFB;
        public const uint MAX_VARYING_VECTORS = 0x8DFC;
        public const uint MAX_COMBINED_TEXTURE_IMAGE_UNITS = 0x8B4D;
        public const uint MAX_VERTEX_TEXTURE_IMAGE_UNITS = 0x8B4C;
        public const uint MAX_TEXTURE_IMAGE_UNITS = 0x8872;
        public const uint MAX_FRAGMENT_UNIFORM_VECTORS = 0x8DFD;
        public const uint SHADER_TYPE = 0x8B4F;
        public const uint SHADING_LANGUAGE_VERSION = 0x8B8C;
        public const uint CURRENT_PROGRAM = 0x8B8D;

        // Depth/Stencil
        public const uint NEVER = 0x0200;
        public const uint LESS = 0x0201;
        public const uint EQUAL = 0x0202;
        public const uint LEQUAL = 0x0203;
        public const uint GREATER = 0x0204;
        public const uint NOTEQUAL = 0x0205;
        public const uint GEQUAL = 0x0206;
        public const uint ALWAYS = 0x0207;
        public const uint KEEP = 0x1E00;
        public const uint REPLACE = 0x1E01;
        public const uint INCR = 0x1E02;
        public const uint DECR = 0x1E03;
        public const uint INVERT = 0x150A;
        public const uint INCR_WRAP = 0x8507;
        public const uint DECR_WRAP = 0x8508;

        // Textures
        public const uint TEXTURE_2D = 0x0DE1;
        public const uint TEXTURE_CUBE_MAP = 0x8513;
        public const uint TEXTURE_3D = 0x806F;
        public const uint TEXTURE_2D_ARRAY = 0x8C1A;
        public const uint TEXTURE_BINDING_2D = 0x8069;
        public const uint TEXTURE_BINDING_CUBE_MAP = 0x8514;
        public const uint TEXTURE_CUBE_MAP_POSITIVE_X = 0x8515;
        public const uint TEXTURE_CUBE_MAP_NEGATIVE_X = 0x8516;
        public const uint TEXTURE_CUBE_MAP_POSITIVE_Y = 0x8517;
        public const uint TEXTURE_CUBE_MAP_NEGATIVE_Y = 0x8518;
        public const uint TEXTURE_CUBE_MAP_POSITIVE_Z = 0x8519;
        public const uint TEXTURE_CUBE_MAP_NEGATIVE_Z = 0x851A;
        public const uint MAX_CUBE_MAP_TEXTURE_SIZE = 0x851C;
        public const uint TEXTURE0 = 0x84C0;
        public const uint ACTIVE_TEXTURE = 0x84E0;

        // Texture parameters
        public const uint REPEAT = 0x2901;
        public const uint CLAMP_TO_EDGE = 0x812F;
        public const uint MIRRORED_REPEAT = 0x8370;
        public const uint TEXTURE_MAG_FILTER = 0x2800;
        public const uint TEXTURE_MIN_FILTER = 0x2801;
        public const uint TEXTURE_WRAP_S = 0x2802;
        public const uint TEXTURE_WRAP_T = 0x2803;
        public const uint TEXTURE_WRAP_R = 0x8072;
        public const uint NEAREST = 0x2600;
        public const uint LINEAR = 0x2601;
        public const uint NEAREST_MIPMAP_NEAREST = 0x2700;
        public const uint LINEAR_MIPMAP_NEAREST = 0x2701;
        public const uint NEAREST_MIPMAP_LINEAR = 0x2702;
        public const uint LINEAR_MIPMAP_LINEAR = 0x2703;

        // Uniform types
        public const uint FLOAT_VEC2 = 0x8B50;
        public const uint FLOAT_VEC3 = 0x8B51;
        public const uint FLOAT_VEC4 = 0x8B52;
        public const uint INT_VEC2 = 0x8B53;
        public const uint INT_VEC3 = 0x8B54;
        public const uint INT_VEC4 = 0x8B55;
        public const uint BOOL = 0x8B56;
        public const uint BOOL_VEC2 = 0x8B57;
        public const uint BOOL_VEC3 = 0x8B58;
        public const uint BOOL_VEC4 = 0x8B59;
        public const uint FLOAT_MAT2 = 0x8B5A;
        public const uint FLOAT_MAT3 = 0x8B5B;
        public const uint FLOAT_MAT4 = 0x8B5C;
        public const uint SAMPLER_2D = 0x8B5E;
        public const uint SAMPLER_CUBE = 0x8B60;

        // Framebuffers
        public const uint FRAMEBUFFER = 0x8D40;
        public const uint RENDERBUFFER = 0x8D41;
        public const uint RGBA4 = 0x8056;
        public const uint RGB5_A1 = 0x8057;
        public const uint RGB565 = 0x8D62;
        public const uint DEPTH_COMPONENT16 = 0x81A5;
        public const uint STENCIL_INDEX8 = 0x8D48;
        public const uint DEPTH_STENCIL = 0x84F9;
        public const uint RENDERBUFFER_WIDTH = 0x8D42;
        public const uint RENDERBUFFER_HEIGHT = 0x8D43;
        public const uint RENDERBUFFER_INTERNAL_FORMAT = 0x8D44;
        public const uint RENDERBUFFER_RED_SIZE = 0x8D50;
        public const uint RENDERBUFFER_GREEN_SIZE = 0x8D51;
        public const uint RENDERBUFFER_BLUE_SIZE = 0x8D52;
        public const uint RENDERBUFFER_ALPHA_SIZE = 0x8D53;
        public const uint RENDERBUFFER_DEPTH_SIZE = 0x8D54;
        public const uint RENDERBUFFER_STENCIL_SIZE = 0x8D55;
        public const uint FRAMEBUFFER_ATTACHMENT_OBJECT_TYPE = 0x8CD0;
        public const uint FRAMEBUFFER_ATTACHMENT_OBJECT_NAME = 0x8CD1;
        public const uint FRAMEBUFFER_ATTACHMENT_TEXTURE_LEVEL = 0x8CD2;
        public const uint FRAMEBUFFER_ATTACHMENT_TEXTURE_CUBE_MAP_FACE = 0x8CD3;
        public const uint COLOR_ATTACHMENT0 = 0x8CE0;
        public const uint DEPTH_ATTACHMENT = 0x8D00;
        public const uint STENCIL_ATTACHMENT = 0x8D20;
        public const uint DEPTH_STENCIL_ATTACHMENT = 0x821A;
        public const uint NONE = 0;
        public const uint FRAMEBUFFER_COMPLETE = 0x8CD5;
        public const uint FRAMEBUFFER_INCOMPLETE_ATTACHMENT = 0x8CD6;
        public const uint FRAMEBUFFER_INCOMPLETE_MISSING_ATTACHMENT = 0x8CD7;
        public const uint FRAMEBUFFER_INCOMPLETE_DIMENSIONS = 0x8CD9;
        public const uint FRAMEBUFFER_UNSUPPORTED = 0x8CDD;
        public const uint FRAMEBUFFER_BINDING = 0x8CA6;
        public const uint RENDERBUFFER_BINDING = 0x8CA7;
        public const uint MAX_RENDERBUFFER_SIZE = 0x84E8;

        // Sync objects
        public const uint UNSIGNALED = 0x9118;
        public const uint SIGNALED = 0x9119;
        public const uint SYNC_STATUS = 0x9114;
        public const uint SYNC_CONDITION = 0x9113;
        public const uint SYNC_FLAGS = 0x9115;
        public const uint SYNC_GPU_COMMANDS_COMPLETE = 0x9117;

        // Misc
        public const uint UNPACK_ALIGNMENT = 0x0CF5;
        public const uint PACK_ALIGNMENT = 0x0D05;
        public const uint UNPACK_FLIP_Y_WEBGL = 0x9240;
        public const uint UNPACK_PREMULTIPLY_ALPHA_WEBGL = 0x9241;
        public const uint UNPACK_COLORSPACE_CONVERSION_WEBGL = 0x9243;
        public const uint BROWSER_DEFAULT_WEBGL = 0x9244;
        public const uint MAX_TEXTURE_SIZE = 0x0D33;
        public const uint MAX_VIEWPORT_DIMS = 0x0D3A;
        public const uint SUBPIXEL_BITS = 0x0D50;
        public const uint RED_BITS = 0x0D52;
        public const uint GREEN_BITS = 0x0D53;
        public const uint BLUE_BITS = 0x0D54;
        public const uint ALPHA_BITS = 0x0D55;
        public const uint DEPTH_BITS = 0x0D56;
        public const uint STENCIL_BITS = 0x0D57;
        public const uint VENDOR = 0x1F00;
        public const uint RENDERER = 0x1F01;
        public const uint VERSION = 0x1F02;
        public const uint IMPLEMENTATION_COLOR_READ_TYPE = 0x8B9A;
        public const uint IMPLEMENTATION_COLOR_READ_FORMAT = 0x8B9B;
    }
}
