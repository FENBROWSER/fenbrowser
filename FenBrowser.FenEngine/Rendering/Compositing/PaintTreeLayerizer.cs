using System;
using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Compositor-facing layer metadata derived from the immutable paint tree.
    /// </summary>
    public sealed class CompositedLayer
    {
        public int LayerId { get; init; }

        public Node SourceNode { get; init; }

        public SKRect Bounds { get; init; }

        public float Opacity { get; init; } = 1f;

        public SKMatrix? Transform { get; init; }

        public IReadOnlyList<string> PromotionReasons { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Layerization output used by frame telemetry.
    /// </summary>
    public sealed class LayerizationResult
    {
        public static readonly LayerizationResult Empty = new()
        {
            Layers = Array.Empty<CompositedLayer>(),
            PromotedLayerCount = 0
        };

        public IReadOnlyList<CompositedLayer> Layers { get; init; } = Array.Empty<CompositedLayer>();

        public int PromotedLayerCount { get; init; }
    }

    /// <summary>
    /// Converts paint-tree nodes into compositor-layer metadata and applies
    /// promotion heuristics for transform/opacity/will-change paths.
    /// </summary>
    public sealed class PaintTreeLayerizer
    {
        private static readonly string[] WillChangePromotionHints =
        {
            "transform",
            "opacity",
            "scroll-position"
        };

        public LayerizationResult Layerize(ImmutablePaintTree tree, IReadOnlyDictionary<Node, CssComputed> styles)
        {
            if (tree == null || tree.NodeCount == 0 || tree.Roots == null || tree.Roots.Count == 0)
            {
                return LayerizationResult.Empty;
            }

            var bySource = new Dictionary<Node, MutableLayer>();
            var synthetic = new List<MutableLayer>();

            tree.Traverse(node =>
            {
                if (node == null)
                {
                    return;
                }

                var reasons = CollectPromotionReasons(node, styles);
                if (reasons.Count == 0)
                {
                    return;
                }

                var sourceNode = node.SourceNode;
                if (sourceNode == null)
                {
                    synthetic.Add(MutableLayer.FromNode(node, reasons));
                    return;
                }

                if (!bySource.TryGetValue(sourceNode, out var layer))
                {
                    bySource[sourceNode] = MutableLayer.FromNode(node, reasons);
                    return;
                }

                layer.Merge(node, reasons);
            });

            if (bySource.Count == 0 && synthetic.Count == 0)
            {
                return LayerizationResult.Empty;
            }

            var orderedLayers = new List<MutableLayer>(bySource.Values.Count + synthetic.Count);
            orderedLayers.AddRange(bySource.Values);
            orderedLayers.AddRange(synthetic);
            orderedLayers.Sort(static (a, b) =>
            {
                var top = a.Bounds.Top.CompareTo(b.Bounds.Top);
                if (top != 0) return top;

                var left = a.Bounds.Left.CompareTo(b.Bounds.Left);
                if (left != 0) return left;

                return string.CompareOrdinal(a.PrimaryReason, b.PrimaryReason);
            });

            var layers = new List<CompositedLayer>(orderedLayers.Count);
            var promoted = 0;
            for (var i = 0; i < orderedLayers.Count; i++)
            {
                var layer = orderedLayers[i].ToImmutable(i + 1);
                if (layer.PromotionReasons.Count > 0)
                {
                    promoted++;
                }

                layers.Add(layer);
            }

            return new LayerizationResult
            {
                Layers = layers,
                PromotedLayerCount = promoted
            };
        }

        private static HashSet<string> CollectPromotionReasons(PaintNodeBase node, IReadOnlyDictionary<Node, CssComputed> styles)
        {
            var reasons = new HashSet<string>(StringComparer.Ordinal);

            if (node.Transform.HasValue)
            {
                reasons.Add("transform");
            }

            if (node.Opacity < 0.999f)
            {
                reasons.Add("opacity");
            }

            if (node is StackingContextPaintNode)
            {
                reasons.Add("stacking-context");
            }

            if (node is OpacityGroupPaintNode)
            {
                reasons.Add("opacity-group");
            }

            if (node is ScrollPaintNode)
            {
                reasons.Add("scroll");
            }

            if (node.SourceNode != null && styles != null && styles.TryGetValue(node.SourceNode, out var computed))
            {
                var willChange = computed?.WillChange;
                if (!string.IsNullOrWhiteSpace(willChange) &&
                    !string.Equals(willChange.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
                {
                    var tokens = willChange.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var token in tokens)
                    {
                        var normalized = token.Trim().ToLowerInvariant();
                        for (var i = 0; i < WillChangePromotionHints.Length; i++)
                        {
                            if (normalized.Contains(WillChangePromotionHints[i], StringComparison.Ordinal))
                            {
                                reasons.Add($"will-change:{WillChangePromotionHints[i]}");
                            }
                        }
                    }
                }
            }

            return reasons;
        }

        private sealed class MutableLayer
        {
            private readonly HashSet<string> _reasons;

            private MutableLayer(Node sourceNode, SKRect bounds, float opacity, SKMatrix? transform, HashSet<string> reasons)
            {
                SourceNode = sourceNode;
                Bounds = bounds;
                Opacity = opacity;
                Transform = transform;
                _reasons = reasons;
            }

            public Node SourceNode { get; }

            public SKRect Bounds { get; private set; }

            public float Opacity { get; private set; }

            public SKMatrix? Transform { get; private set; }

            public string PrimaryReason
            {
                get
                {
                    foreach (var reason in _reasons)
                    {
                        return reason;
                    }

                    return string.Empty;
                }
            }

            public static MutableLayer FromNode(PaintNodeBase node, HashSet<string> reasons)
            {
                return new MutableLayer(
                    node.SourceNode,
                    NormalizeBounds(node.Bounds),
                    Math.Clamp(node.Opacity, 0f, 1f),
                    node.Transform,
                    reasons);
            }

            public void Merge(PaintNodeBase node, HashSet<string> reasons)
            {
                Bounds = Union(Bounds, NormalizeBounds(node.Bounds));
                Opacity = Math.Min(Opacity, Math.Clamp(node.Opacity, 0f, 1f));
                Transform ??= node.Transform;
                _reasons.UnionWith(reasons);
            }

            public CompositedLayer ToImmutable(int id)
            {
                var orderedReasons = new List<string>(_reasons);
                orderedReasons.Sort(StringComparer.Ordinal);
                return new CompositedLayer
                {
                    LayerId = id,
                    SourceNode = SourceNode,
                    Bounds = Bounds,
                    Opacity = Opacity,
                    Transform = Transform,
                    PromotionReasons = orderedReasons
                };
            }

            private static SKRect Union(SKRect a, SKRect b)
            {
                if (a.Width <= 0 || a.Height <= 0)
                {
                    return b;
                }

                if (b.Width <= 0 || b.Height <= 0)
                {
                    return a;
                }

                return new SKRect(
                    Math.Min(a.Left, b.Left),
                    Math.Min(a.Top, b.Top),
                    Math.Max(a.Right, b.Right),
                    Math.Max(a.Bottom, b.Bottom));
            }

            private static SKRect NormalizeBounds(SKRect bounds)
            {
                if (!float.IsFinite(bounds.Left) || !float.IsFinite(bounds.Top) ||
                    !float.IsFinite(bounds.Right) || !float.IsFinite(bounds.Bottom))
                {
                    return SKRect.Empty;
                }

                if (bounds.Right < bounds.Left || bounds.Bottom < bounds.Top)
                {
                    return SKRect.Empty;
                }

                return bounds;
            }
        }
    }
}
