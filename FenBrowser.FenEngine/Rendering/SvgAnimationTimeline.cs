using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// The SMIL document timeline of an outermost svg element, the time container of
    /// every animation beneath it (SVG 1.1 §19.2.2, SVGSVGElement pauseAnimations,
    /// unpauseAnimations, getCurrentTime, setCurrentTime), and the instance times that
    /// SVGAnimationElement.beginElement and endElement add. Like the zoom-and-pan
    /// state it sits beside the DOM, never in it: painting samples the renderer's
    /// SMIL model at the timeline's current time and adds the DOM instance times to
    /// the serialized copy it renders.
    /// </summary>
    public static class SvgAnimationTimeline
    {
        /// <summary>DOM-added instance times kept per list; the oldest are dropped first.</summary>
        public const int MaxInstanceTimes = 64;

        /// <summary>Latest seekable document time, in seconds (about 31 years).</summary>
        public const double MaxDocumentTime = 1_000_000_000d;

        private sealed class Timeline
        {
            public long OriginTimestamp = Stopwatch.GetTimestamp();
            public bool Paused;
            public double PausedAt;

            public double CurrentTime => Paused
                ? PausedAt
                : Math.Min(Stopwatch.GetElapsedTime(OriginTimestamp).TotalSeconds, MaxDocumentTime);
        }

        private sealed class InstanceTimes
        {
            public readonly List<double> Begins = new();
            public readonly List<double> Ends = new();
        }

        private static readonly ConditionalWeakTable<Element, Timeline> Timelines = new();
        private static readonly ConditionalWeakTable<Element, InstanceTimes> Instances = new();
        private static readonly object Gate = new();

        private static readonly HashSet<string> AnimationElementNames = new(StringComparer.Ordinal)
        {
            "animate", "set", "animateMotion", "animateTransform", "animateColor"
        };

        /// <summary>The SMIL animation elements (SVG 2 §19 / SVG Animations §2).</summary>
        public static bool IsAnimationElement(Element element) =>
            element?.NamespaceUri == Namespaces.Svg && AnimationElementNames.Contains(element.LocalName);

        /// <summary>
        /// The time container of <paramref name="element"/>: the outermost svg element
        /// at or above it, or null outside SVG content.
        /// </summary>
        public static Element TimeContainerOf(Element element)
        {
            Element container = null;
            for (var current = element; current != null; current = current.ParentElement)
            {
                if (current.LocalName == "svg" && current.NamespaceUri == Namespaces.Svg)
                {
                    container = current;
                }
            }
            return container;
        }

        /// <summary>Starts the timeline now if it has not started (the document begin).</summary>
        public static void Begin(Element container)
        {
            if (container != null)
            {
                lock (Gate) Timelines.GetValue(container, _ => new Timeline());
            }
        }

        public static double CurrentTime(Element container)
        {
            if (container == null) return 0d;
            lock (Gate) return Timelines.GetValue(container, _ => new Timeline()).CurrentTime;
        }

        public static bool IsPaused(Element container)
        {
            if (container == null) return false;
            lock (Gate) return Timelines.TryGetValue(container, out var timeline) && timeline.Paused;
        }

        public static void Pause(Element container)
        {
            if (container == null) return;
            lock (Gate)
            {
                var timeline = Timelines.GetValue(container, _ => new Timeline());
                if (!timeline.Paused)
                {
                    timeline.PausedAt = timeline.CurrentTime;
                    timeline.Paused = true;
                }
            }
        }

        public static void Unpause(Element container)
        {
            if (container == null) return;
            lock (Gate)
            {
                var timeline = Timelines.GetValue(container, _ => new Timeline());
                if (timeline.Paused)
                {
                    timeline.OriginTimestamp = TimestampSecondsAgo(timeline.PausedAt);
                    timeline.Paused = false;
                }
            }
        }

        /// <summary>Seeks the timeline; negative and non-finite times clamp into range.</summary>
        public static void Seek(Element container, double seconds)
        {
            if (container == null) return;
            double time = double.IsFinite(seconds) ? Math.Clamp(seconds, 0d, MaxDocumentTime) : 0d;
            lock (Gate)
            {
                var timeline = Timelines.GetValue(container, _ => new Timeline());
                if (timeline.Paused)
                {
                    timeline.PausedAt = time;
                }
                else
                {
                    timeline.OriginTimestamp = TimestampSecondsAgo(time);
                }
            }
        }

        /// <summary>Adds a begin (or end) instance time for an animation element.</summary>
        public static void AddInstanceTime(Element animation, double documentTime, bool isEnd)
        {
            if (animation == null || !double.IsFinite(documentTime)) return;
            double time = Math.Clamp(documentTime, -MaxDocumentTime, MaxDocumentTime);
            lock (Gate)
            {
                var times = Instances.GetValue(animation, _ => new InstanceTimes());
                var list = isEnd ? times.Ends : times.Begins;
                if (list.Count >= MaxInstanceTimes)
                {
                    list.RemoveAt(0);
                }
                list.Add(time);
            }
        }

        /// <summary>
        /// The timing attribute to render for an animation: its authored list plus its
        /// DOM instance times as clock values. Null when there is nothing to add.
        /// An authored "indefinite" stays; it only waits for these instance times.
        /// </summary>
        public static string ComposeTimingAttribute(Element animation, bool isEnd)
        {
            if (animation == null) return null;
            double[] added;
            lock (Gate)
            {
                if (!Instances.TryGetValue(animation, out var times)) return null;
                var list = isEnd ? times.Ends : times.Begins;
                if (list.Count == 0) return null;
                added = list.ToArray();
            }

            string authored = animation.GetAttribute(isEnd ? "end" : "begin");
            var text = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(authored))
            {
                text.Append(authored.Trim());
            }
            else if (!isEnd)
            {
                text.Append('0');   // the begin attribute's default
            }
            foreach (double time in added)
            {
                // The timing grammar has no negative clock values; a time before the
                // document begin (beginElementAt with a negative offset) starts at 0.
                if (text.Length > 0) text.Append(';');
                text.Append(Math.Max(time, 0d).ToString("0.######", CultureInfo.InvariantCulture)).Append('s');
            }
            return text.ToString();
        }

        /// <summary>True when the subtree holds an animation element, so time matters to it.</summary>
        public static bool HasAnimations(Element root)
        {
            if (root == null) return false;
            foreach (var node in root.SelfAndDescendants())
            {
                if (node is Element element && IsAnimationElement(element))
                {
                    return true;
                }
            }
            return false;
        }

        private static long TimestampSecondsAgo(double seconds) =>
            Stopwatch.GetTimestamp() - (long)(seconds * Stopwatch.Frequency);
    }
}
