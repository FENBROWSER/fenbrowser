using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Full CSS 3D Transform implementation per W3C CSS Transforms Level 2.
    /// Supports all 2D and 3D transform functions.
    /// </summary>
    public class CssTransform3D
    {
        // Transform components (accumulated)
        private readonly List<TransformFunction> _functions = new();

        /// <summary>
        /// Parse a CSS transform string into a 3D transform
        /// </summary>
        public static CssTransform3D Parse(string value)
        {
            var transform = new CssTransform3D();
            if (string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
                return transform;

            // Match all transform functions: name(args)
            var funcMatches = Regex.Matches(value, @"(\w+)\s*\(([^)]+)\)");
            
            foreach (Match m in funcMatches)
            {
                string func = m.Groups[1].Value.ToLowerInvariant();
                string args = m.Groups[2].Value;
                var rawValues = SplitArgs(args);
                var values = ParseArgs(rawValues);

                var tf = new TransformFunction { Name = func, Values = values, RawValues = rawValues };
                transform._functions.Add(tf);
            }

            return transform;
        }

        /// <summary>
        /// Get the combined 2D matrix for SkiaSharp rendering
        /// </summary>
        public SKMatrix ToSKMatrix()
        {
            return ToSKMatrix(SKRect.Empty);
        }

        /// <summary>
        /// Get the combined 2D matrix for SkiaSharp rendering using the element bounds
        /// to resolve percentage-based translation values.
        /// </summary>
        public SKMatrix ToSKMatrix(SKRect referenceBox)
        {
            var matrix = SKMatrix.Identity;

            foreach (var func in _functions)
            {
                var m = GetFunctionMatrix(func, referenceBox);
                matrix = matrix.PreConcat(m);
            }

            return matrix;
        }

        /// <summary>
        /// Get the combined 3D matrix (4x4) for full 3D transforms
        /// </summary>
        public Matrix44 ToMatrix44()
        {
            var matrix = Matrix44.Identity();

            foreach (var func in _functions)
            {
                var m = GetFunctionMatrix44(func);
                matrix = matrix.Multiply(m);
            }

            return matrix;
        }

        /// <summary>
        /// Check if this transform has any 3D components
        /// </summary>
        public bool Is3D
        {
            get
            {
                foreach (var func in _functions)
                {
                    if (func.Name.Contains("3d") || 
                        func.Name == "rotatex" || func.Name == "rotatey" || func.Name == "rotatez" ||
                        func.Name == "translatez" || func.Name == "scalez" ||
                        func.Name == "perspective" || func.Name == "matrix3d")
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Check if transform is effectively identity (no transforms)
        /// </summary>
        public bool IsIdentity => _functions.Count == 0;

        /// <summary>
        /// Check if this transform has any effect (not identity)
        /// </summary>
        public bool HasTransform => _functions.Count > 0;

        private static SKMatrix GetFunctionMatrix(TransformFunction func, SKRect referenceBox)
        {
            var v = func.Values;
            float deg2rad = (float)(Math.PI / 180.0);

            switch (func.Name)
            {
                // 2D Translate
                case "translate":
                    return SKMatrix.CreateTranslation(
                        ResolveTranslateValue(func, 0, referenceBox.Width),
                        ResolveTranslateValue(func, 1, referenceBox.Height));
                case "translatex":
                    return SKMatrix.CreateTranslation(ResolveTranslateValue(func, 0, referenceBox.Width), 0);
                case "translatey":
                    return SKMatrix.CreateTranslation(0, ResolveTranslateValue(func, 0, referenceBox.Height));
                case "translatez":
                case "translate3d":
                    // For 2D fallback, ignore Z
                    return SKMatrix.CreateTranslation(
                        ResolveTranslateValue(func, 0, referenceBox.Width),
                        ResolveTranslateValue(func, 1, referenceBox.Height));

                // 2D Scale
                case "scale":
                    return SKMatrix.CreateScale(
                        v.Count > 0 ? v[0] : 1,
                        v.Count > 1 ? v[1] : (v.Count > 0 ? v[0] : 1));
                case "scalex":
                    return SKMatrix.CreateScale(v.Count > 0 ? v[0] : 1, 1);
                case "scaley":
                    return SKMatrix.CreateScale(1, v.Count > 0 ? v[0] : 1);
                case "scalez":
                case "scale3d":
                    // For 2D fallback
                    return SKMatrix.CreateScale(
                        v.Count > 0 ? v[0] : 1,
                        v.Count > 1 ? v[1] : (v.Count > 0 ? v[0] : 1));

                // 2D Rotate
                case "rotate":
                case "rotatez":
                    return SKMatrix.CreateRotationDegrees(v.Count > 0 ? v[0] : 0);
                case "rotatex":
                case "rotatey":
                case "rotate3d":
                    // For 2D fallback, approximate as no rotation
                    return SKMatrix.Identity;

                // Skew
                case "skew":
                    return SKMatrix.CreateSkew(
                        (float)Math.Tan((v.Count > 0 ? v[0] : 0) * deg2rad),
                        (float)Math.Tan((v.Count > 1 ? v[1] : 0) * deg2rad));
                case "skewx":
                    return SKMatrix.CreateSkew((float)Math.Tan((v.Count > 0 ? v[0] : 0) * deg2rad), 0);
                case "skewy":
                    return SKMatrix.CreateSkew(0, (float)Math.Tan((v.Count > 0 ? v[0] : 0) * deg2rad));

                // Matrix
                case "matrix":
                    if (v.Count >= 6)
                    {
                        // CSS matrix(a, b, c, d, tx, ty) -> SKMatrix
                        return new SKMatrix(
                            v[0], v[2], v[4],  // ScaleX, SkewX, TransX
                            v[1], v[3], v[5],  // SkewY, ScaleY, TransY
                            0, 0, 1);          // Persp
                    }
                    return SKMatrix.Identity;

                case "matrix3d":
                    // Convert 4x4 matrix to 2D approximation
                    if (v.Count >= 16)
                    {
                        return new SKMatrix(
                            v[0], v[1], v[12],
                            v[4], v[5], v[13],
                            0, 0, 1);
                    }
                    return SKMatrix.Identity;

                // Perspective (no 2D equivalent)
                case "perspective":
                    return SKMatrix.Identity;

                default:
                    return SKMatrix.Identity;
            }
        }

        private static Matrix44 GetFunctionMatrix44(TransformFunction func)
        {
            var v = func.Values;
            float deg2rad = (float)(Math.PI / 180.0);

            switch (func.Name)
            {
                case "translate":
                case "translate3d":
                    return Matrix44.CreateTranslation(
                        v.Count > 0 ? v[0] : 0,
                        v.Count > 1 ? v[1] : 0,
                        v.Count > 2 ? v[2] : 0);
                case "translatex":
                    return Matrix44.CreateTranslation(v.Count > 0 ? v[0] : 0, 0, 0);
                case "translatey":
                    return Matrix44.CreateTranslation(0, v.Count > 0 ? v[0] : 0, 0);
                case "translatez":
                    return Matrix44.CreateTranslation(0, 0, v.Count > 0 ? v[0] : 0);

                case "scale":
                case "scale3d":
                    return Matrix44.CreateScale(
                        v.Count > 0 ? v[0] : 1,
                        v.Count > 1 ? v[1] : (v.Count > 0 ? v[0] : 1),
                        v.Count > 2 ? v[2] : 1);
                case "scalex":
                    return Matrix44.CreateScale(v.Count > 0 ? v[0] : 1, 1, 1);
                case "scaley":
                    return Matrix44.CreateScale(1, v.Count > 0 ? v[0] : 1, 1);
                case "scalez":
                    return Matrix44.CreateScale(1, 1, v.Count > 0 ? v[0] : 1);

                case "rotate":
                case "rotatez":
                    return Matrix44.CreateRotationZ((v.Count > 0 ? v[0] : 0) * deg2rad);
                case "rotatex":
                    return Matrix44.CreateRotationX((v.Count > 0 ? v[0] : 0) * deg2rad);
                case "rotatey":
                    return Matrix44.CreateRotationY((v.Count > 0 ? v[0] : 0) * deg2rad);
                case "rotate3d":
                    if (v.Count >= 4)
                    {
                        // rotate3d(x, y, z, angle)
                        return Matrix44.CreateRotation(v[0], v[1], v[2], v[3] * deg2rad);
                    }
                    return Matrix44.Identity();

                case "skew":
                    return Matrix44.CreateSkew(
                        (v.Count > 0 ? v[0] : 0) * deg2rad,
                        (v.Count > 1 ? v[1] : 0) * deg2rad);
                case "skewx":
                    return Matrix44.CreateSkew((v.Count > 0 ? v[0] : 0) * deg2rad, 0);
                case "skewy":
                    return Matrix44.CreateSkew(0, (v.Count > 0 ? v[0] : 0) * deg2rad);

                case "matrix":
                    if (v.Count >= 6)
                    {
                        return Matrix44.From2DMatrix(v[0], v[1], v[2], v[3], v[4], v[5]);
                    }
                    return Matrix44.Identity();

                case "matrix3d":
                    if (v.Count >= 16)
                    {
                        return new Matrix44(
                            v[0], v[1], v[2], v[3],
                            v[4], v[5], v[6], v[7],
                            v[8], v[9], v[10], v[11],
                            v[12], v[13], v[14], v[15]);
                    }
                    return Matrix44.Identity();

                case "perspective":
                    if (v.Count > 0 && v[0] != 0)
                    {
                        return Matrix44.CreatePerspective(v[0]);
                    }
                    return Matrix44.Identity();

                default:
                    return Matrix44.Identity();
            }
        }

        private static List<string> SplitArgs(string args)
        {
            return new List<string>(args.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static List<float> ParseArgs(IEnumerable<string> parts)
        {
            var result = new List<float>();

            foreach (var part in parts)
            {
                var clean = part.Trim().ToLowerInvariant();
                
                // Handle various units
                float value = 0;
                if (clean.EndsWith("deg"))
                {
                    float.TryParse(clean.Replace("deg", ""), out value);
                }
                else if (clean.EndsWith("rad"))
                {
                    float.TryParse(clean.Replace("rad", ""), out var rad);
                    value = (float)(rad * 180 / Math.PI);
                }
                else if (clean.EndsWith("turn"))
                {
                    float.TryParse(clean.Replace("turn", ""), out var turn);
                    value = turn * 360;
                }
                else if (clean.EndsWith("grad"))
                {
                    float.TryParse(clean.Replace("grad", ""), out var grad);
                    value = grad * 0.9f;
                }
                else if (clean.EndsWith("px"))
                {
                    float.TryParse(clean.Replace("px", ""), out value);
                }
                else if (clean.EndsWith("%"))
                {
                    float.TryParse(clean.Replace("%", ""), out value);
                    value /= 100f;
                }
                else if (clean.EndsWith("em") || clean.EndsWith("rem"))
                {
                    float.TryParse(clean.Replace("em", "").Replace("r", ""), out value);
                    value *= 16; // Approximate 1em = 16px
                }
                else
                {
                    float.TryParse(clean, out value);
                }

                result.Add(value);
            }

            return result;
        }

        private static float ResolveTranslateValue(TransformFunction func, int index, float referenceLength)
        {
            if (func.RawValues != null && index < func.RawValues.Count)
            {
                var raw = func.RawValues[index].Trim().ToLowerInvariant();
                if (raw.EndsWith("%", StringComparison.Ordinal) &&
                    float.TryParse(raw.Substring(0, raw.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                {
                    return referenceLength * (percent / 100f);
                }
            }

            return index < func.Values.Count ? func.Values[index] : 0f;
        }

        private class TransformFunction
        {
            public string Name { get; set; }
            public List<float> Values { get; set; }
            public List<string> RawValues { get; set; }
        }
    }

    /// <summary>
    /// 4x4 matrix for 3D transforms
    /// </summary>
    public struct Matrix44
    {
        public float M11, M12, M13, M14;
        public float M21, M22, M23, M24;
        public float M31, M32, M33, M34;
        public float M41, M42, M43, M44;

        public Matrix44(
            float m11, float m12, float m13, float m14,
            float m21, float m22, float m23, float m24,
            float m31, float m32, float m33, float m34,
            float m41, float m42, float m43, float m44)
        {
            M11 = m11; M12 = m12; M13 = m13; M14 = m14;
            M21 = m21; M22 = m22; M23 = m23; M24 = m24;
            M31 = m31; M32 = m32; M33 = m33; M34 = m34;
            M41 = m41; M42 = m42; M43 = m43; M44 = m44;
        }

        public static Matrix44 Identity() => new Matrix44(
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1);

        public static Matrix44 CreateTranslation(float x, float y, float z) => new Matrix44(
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            x, y, z, 1);

        public static Matrix44 CreateScale(float x, float y, float z) => new Matrix44(
            x, 0, 0, 0,
            0, y, 0, 0,
            0, 0, z, 0,
            0, 0, 0, 1);

        public static Matrix44 CreateRotationX(float radians)
        {
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);
            return new Matrix44(
                1, 0, 0, 0,
                0, cos, sin, 0,
                0, -sin, cos, 0,
                0, 0, 0, 1);
        }

        public static Matrix44 CreateRotationY(float radians)
        {
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);
            return new Matrix44(
                cos, 0, -sin, 0,
                0, 1, 0, 0,
                sin, 0, cos, 0,
                0, 0, 0, 1);
        }

        public static Matrix44 CreateRotationZ(float radians)
        {
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);
            return new Matrix44(
                cos, sin, 0, 0,
                -sin, cos, 0, 0,
                0, 0, 1, 0,
                0, 0, 0, 1);
        }

        public static Matrix44 CreateRotation(float x, float y, float z, float radians)
        {
            // Normalize axis
            float len = (float)Math.Sqrt(x * x + y * y + z * z);
            if (len < 0.0001f) return Identity();
            x /= len; y /= len; z /= len;

            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);
            float t = 1 - cos;

            return new Matrix44(
                t * x * x + cos, t * x * y + sin * z, t * x * z - sin * y, 0,
                t * x * y - sin * z, t * y * y + cos, t * y * z + sin * x, 0,
                t * x * z + sin * y, t * y * z - sin * x, t * z * z + cos, 0,
                0, 0, 0, 1);
        }

        public static Matrix44 CreateSkew(float xRadians, float yRadians) => new Matrix44(
            1, (float)Math.Tan(yRadians), 0, 0,
            (float)Math.Tan(xRadians), 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1);

        public static Matrix44 CreatePerspective(float d)
        {
            if (Math.Abs(d) < 0.0001f) return Identity();
            return new Matrix44(
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, -1f / d,
                0, 0, 0, 1);
        }

        public static Matrix44 From2DMatrix(float a, float b, float c, float d, float tx, float ty) => new Matrix44(
            a, b, 0, 0,
            c, d, 0, 0,
            0, 0, 1, 0,
            tx, ty, 0, 1);

        public Matrix44 Multiply(Matrix44 m)
        {
            return new Matrix44(
                M11 * m.M11 + M12 * m.M21 + M13 * m.M31 + M14 * m.M41,
                M11 * m.M12 + M12 * m.M22 + M13 * m.M32 + M14 * m.M42,
                M11 * m.M13 + M12 * m.M23 + M13 * m.M33 + M14 * m.M43,
                M11 * m.M14 + M12 * m.M24 + M13 * m.M34 + M14 * m.M44,

                M21 * m.M11 + M22 * m.M21 + M23 * m.M31 + M24 * m.M41,
                M21 * m.M12 + M22 * m.M22 + M23 * m.M32 + M24 * m.M42,
                M21 * m.M13 + M22 * m.M23 + M23 * m.M33 + M24 * m.M43,
                M21 * m.M14 + M22 * m.M24 + M23 * m.M34 + M24 * m.M44,

                M31 * m.M11 + M32 * m.M21 + M33 * m.M31 + M34 * m.M41,
                M31 * m.M12 + M32 * m.M22 + M33 * m.M32 + M34 * m.M42,
                M31 * m.M13 + M32 * m.M23 + M33 * m.M33 + M34 * m.M43,
                M31 * m.M14 + M32 * m.M24 + M33 * m.M34 + M34 * m.M44,

                M41 * m.M11 + M42 * m.M21 + M43 * m.M31 + M44 * m.M41,
                M41 * m.M12 + M42 * m.M22 + M43 * m.M32 + M44 * m.M42,
                M41 * m.M13 + M42 * m.M23 + M43 * m.M33 + M44 * m.M43,
                M41 * m.M14 + M42 * m.M24 + M43 * m.M34 + M44 * m.M44);
        }

        /// <summary>
        /// Convert to 2D SKMatrix (projecting Z onto 2D)
        /// </summary>
        public SKMatrix ToSKMatrix()
        {
            // Project the 3D matrix to 2D by assuming z=0
            return new SKMatrix(
                M11, M12, M41,
                M21, M22, M42,
                M14, M24, M44);
        }
    }
}
