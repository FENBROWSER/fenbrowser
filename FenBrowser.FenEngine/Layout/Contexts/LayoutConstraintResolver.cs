using System;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Layout.Contexts
{
    public readonly struct LayoutConstraintResolution
    {
        public LayoutConstraintResolution(float rawAvailable, float resolvedAvailable, float containingBlock, float viewport, bool unconstrained, string source)
        {
            RawAvailable = rawAvailable;
            ResolvedAvailable = resolvedAvailable;
            ContainingBlock = containingBlock;
            Viewport = viewport;
            IsUnconstrained = unconstrained;
            Source = source ?? "unknown";
        }

        public float RawAvailable { get; }

        public float ResolvedAvailable { get; }

        public float ContainingBlock { get; }

        public float Viewport { get; }

        public bool IsUnconstrained { get; }

        public string Source { get; }
    }

    public static class LayoutConstraintResolver
    {
        public static LayoutConstraintResolution ResolveWidth(LayoutState state, string owner, float emergencyFallback = 1920f)
        {
            var rawAvailable = state.AvailableSize.Width;
            var unconstrained = float.IsInfinity(rawAvailable) || float.IsNaN(rawAvailable);
            var containingBlock = state.ContainingBlockWidth;
            var viewport = state.ViewportWidth;

            float resolved;
            string source;

            if (IsFinitePositive(rawAvailable))
            {
                resolved = rawAvailable;
                source = "available";
            }
            else if (IsFinitePositive(containingBlock))
            {
                resolved = containingBlock;
                source = "containing-block";
            }
            else if (IsFinitePositive(viewport))
            {
                resolved = viewport;
                source = "viewport";
            }
            else
            {
                resolved = Math.Max(1f, emergencyFallback);
                source = "emergency-fallback";
            }

            if (DebugConfig.EnableDeepDebug && DebugConfig.LogLayoutConstraints)
            {
                FenBrowser.Core.FenLogger.Info(
                    $"[LAYOUT-CONSTRAINT] {owner} Raw={Format(rawAvailable)} Resolved={resolved:0.###} Source={source} CB={Format(containingBlock)} VP={Format(viewport)}",
                    LogCategory.Layout);
            }

            return new LayoutConstraintResolution(rawAvailable, resolved, containingBlock, viewport, unconstrained, source);
        }

        private static bool IsFinitePositive(float value)
        {
            return float.IsFinite(value) && value > 0f;
        }

        private static string Format(float value)
        {
            if (float.IsPositiveInfinity(value))
            {
                return "∞";
            }

            if (float.IsNegativeInfinity(value))
            {
                return "-∞";
            }

            if (float.IsNaN(value))
            {
                return "NaN";
            }

            return value.ToString("0.###");
        }
    }
}
