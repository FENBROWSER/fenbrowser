using System;
using System.Collections.Generic;
using System.Globalization;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;

namespace FenBrowser.FenEngine.Media
{
    /// <summary>What the user agent controls show (HTML §4.8.13 "user interface").</summary>
    public sealed record MediaControlsState(
        bool Paused,
        bool Ended,
        double CurrentTime,
        double Duration,
        bool Muted,
        double Volume,
        double BufferedEnd);

    /// <summary>What a click or key on the controls asks the element to do.</summary>
    public enum MediaControlAction
    {
        None,
        TogglePlay,
        Seek,
        ToggleMute,
    }

    /// <summary>The rectangles of the controls, in the same space as the element's content box.</summary>
    public readonly record struct MediaControlsGeometry(SKRect Bar, SKRect PlayButton, SKRect Timeline, SKRect MuteButton, SKPoint TimeTextOrigin, float FontSize)
    {
        public static readonly MediaControlsGeometry Empty = default;

        public bool IsEmpty => Bar.Width <= 0 || Bar.Height <= 0;
    }

    /// <summary>
    /// The user agent's media controls: a bar along the bottom of a video (or filling an
    /// audio element) with a play/pause button, the current time and duration, a timeline
    /// that shows the buffered and played extents and seeks on click, and a mute button.
    /// One geometry serves paint, hit testing and the accessibility tree, so what is drawn
    /// is what is clicked and what is announced.
    /// </summary>
    public static class MediaControls
    {
        public const float BarHeight = 32f;
        private const float ButtonSize = 24f;
        private const float Padding = 6f;
        private const float TimelineHeight = 4f;
        private const float MinimumWidthForTime = 180f;

        private static readonly SKColor BarColor = new(20, 20, 20, 200);
        private static readonly SKColor IconColor = new(240, 240, 240);
        private static readonly SKColor TrackColor = new(255, 255, 255, 60);
        private static readonly SKColor BufferedColor = new(255, 255, 255, 110);
        private static readonly SKColor PlayedColor = new(255, 255, 255, 230);

        private static readonly SKColor HoverColor = new(255, 255, 255, 48);

        // The control under the pointer, per element, for the hover highlight. Written from
        // input handling, read by paint; a weak table so a removed element takes it along.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Element, System.Runtime.CompilerServices.StrongBox<MediaControlAction>> s_hovered = new();

        public static bool ShowsControls(Element element) => element != null && element.HasAttribute("controls");

        /// <summary>The control the pointer is over on <paramref name="element"/>, or None.</summary>
        public static MediaControlAction GetHovered(Element element) =>
            element != null && s_hovered.TryGetValue(element, out var box) ? box.Value : MediaControlAction.None;

        /// <summary>Records the hovered control; true when that changes what is painted.</summary>
        public static bool SetHovered(Element element, MediaControlAction action)
        {
            if (element == null)
                return false;
            var box = s_hovered.GetValue(element, static _ => new System.Runtime.CompilerServices.StrongBox<MediaControlAction>(MediaControlAction.None));
            if (box.Value == action)
                return false;
            box.Value = action;
            return true;
        }

        /// <summary>Lays the controls out inside <paramref name="contentBox"/>.</summary>
        public static MediaControlsGeometry Layout(SKRect contentBox, bool isVideo)
        {
            if (contentBox.Width <= 0 || contentBox.Height <= 0)
                return MediaControlsGeometry.Empty;

            float barHeight = isVideo ? Math.Min(BarHeight, contentBox.Height) : contentBox.Height;
            var bar = new SKRect(contentBox.Left, contentBox.Bottom - barHeight, contentBox.Right, contentBox.Bottom);
            float buttonSize = Math.Min(ButtonSize, barHeight - 2 * Padding);
            if (buttonSize <= 0)
                buttonSize = barHeight;
            float mid = bar.MidY;

            var play = new SKRect(bar.Left + Padding, mid - buttonSize / 2, bar.Left + Padding + buttonSize, mid + buttonSize / 2);
            var mute = new SKRect(bar.Right - Padding - buttonSize, mid - buttonSize / 2, bar.Right - Padding, mid + buttonSize / 2);

            float fontSize = Math.Clamp(barHeight * 0.36f, 9f, 13f);
            bool showTime = bar.Width >= MinimumWidthForTime;
            float timeWidth = showTime ? fontSize * 6.5f : 0f;
            float timelineLeft = play.Right + Padding + timeWidth + (showTime ? Padding : 0f);
            float timelineRight = mute.Left - Padding;
            var timeline = timelineRight > timelineLeft
                ? new SKRect(timelineLeft, mid - TimelineHeight * 2, timelineRight, mid + TimelineHeight * 2)
                : SKRect.Empty;

            var timeOrigin = new SKPoint(play.Right + Padding, mid + fontSize * 0.35f);
            return new MediaControlsGeometry(bar, play, timeline, mute, showTime ? timeOrigin : new SKPoint(float.NaN, float.NaN), fontSize);
        }

        /// <summary>Which control a point hits; <paramref name="seekFraction"/> is the timeline position for a seek.</summary>
        public static MediaControlAction HitTest(in MediaControlsGeometry geometry, float x, float y, out double seekFraction)
        {
            seekFraction = 0;
            if (geometry.IsEmpty || !geometry.Bar.Contains(x, y))
                return MediaControlAction.None;
            // Buttons grow a little for fat fingers; the timeline grows more vertically (it
            // is a thin line) and just enough sideways to reach its ends.
            if (Inflate(geometry.PlayButton, Padding / 2).Contains(x, y))
                return MediaControlAction.TogglePlay;
            if (Inflate(geometry.MuteButton, Padding / 2).Contains(x, y))
                return MediaControlAction.ToggleMute;
            var timelineHit = new SKRect(geometry.Timeline.Left - Padding / 2, geometry.Bar.Top, geometry.Timeline.Right + Padding / 2, geometry.Bar.Bottom);
            if (geometry.Timeline.Width > 0 && timelineHit.Contains(x, y))
            {
                seekFraction = Math.Clamp((x - geometry.Timeline.Left) / geometry.Timeline.Width, 0, 1);
                return MediaControlAction.Seek;
            }

            return MediaControlAction.None;
        }

        private static SKRect Inflate(SKRect rect, float by) => new(rect.Left - by, rect.Top - by, rect.Right + by, rect.Bottom + by);

        /// <summary>The "m:ss" / "h:mm:ss" the time display uses.</summary>
        public static string FormatTime(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
                return "0:00";
            long total = (long)Math.Floor(seconds);
            long h = total / 3600, m = (total % 3600) / 60, s = total % 60;
            return h > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, s)
                : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", m, s);
        }

        /// <summary>Paint nodes for the controls over <paramref name="contentBox"/>.</summary>
        public static void BuildPaintNodes(Element element, SKRect contentBox, bool isVideo, MediaControlsState state, List<PaintNodeBase> nodes)
        {
            var geometry = Layout(contentBox, isVideo);
            if (geometry.IsEmpty)
                return;
            state ??= new MediaControlsState(true, false, 0, double.NaN, false, 1, 0);

            nodes.Add(new BackgroundPaintNode { Bounds = geometry.Bar, SourceNode = element, Color = BarColor });

            // Hover: a soft disc behind the button under the pointer.
            var hovered = GetHovered(element);
            var hoveredButton = hovered switch
            {
                MediaControlAction.TogglePlay => geometry.PlayButton,
                MediaControlAction.ToggleMute => geometry.MuteButton,
                _ => SKRect.Empty,
            };
            if (!hoveredButton.IsEmpty)
            {
                nodes.Add(new CustomPaintNode
                {
                    Bounds = hoveredButton,
                    SourceNode = element,
                    PaintAction = (canvas, bounds) =>
                    {
                        using var paint = new SKPaint { Color = HoverColor, Style = SKPaintStyle.Fill, IsAntialias = true };
                        canvas.DrawCircle(bounds.MidX, bounds.MidY, bounds.Width * 0.6f, paint);
                    },
                });
            }

            // Play / pause icon.
            var play = geometry.PlayButton;
            nodes.Add(new CustomPaintNode
            {
                Bounds = play,
                SourceNode = element,
                PaintAction = (canvas, bounds) =>
                {
                    using var paint = new SKPaint { Color = IconColor, Style = SKPaintStyle.Fill, IsAntialias = true };
                    float inset = bounds.Width * 0.22f;
                    if (state.Paused || state.Ended)
                    {
                        using var path = Rendering.PathBuilderHelper.Build(builder =>
                        {
                            builder.MoveTo(bounds.Left + inset, bounds.Top + inset * 0.8f);
                            builder.LineTo(bounds.Right - inset * 0.6f, bounds.MidY);
                            builder.LineTo(bounds.Left + inset, bounds.Bottom - inset * 0.8f);
                            builder.Close();
                        });
                        canvas.DrawPath(path, paint);
                    }
                    else
                    {
                        float barWidth = bounds.Width * 0.18f;
                        canvas.DrawRect(new SKRect(bounds.Left + inset, bounds.Top + inset * 0.8f, bounds.Left + inset + barWidth, bounds.Bottom - inset * 0.8f), paint);
                        canvas.DrawRect(new SKRect(bounds.Right - inset - barWidth, bounds.Top + inset * 0.8f, bounds.Right - inset, bounds.Bottom - inset * 0.8f), paint);
                    }
                },
            });

            // Time text.
            if (!float.IsNaN(geometry.TimeTextOrigin.X))
            {
                string text = FormatTime(state.CurrentTime) + " / " + FormatTime(state.Duration);
                nodes.Add(new TextPaintNode
                {
                    Bounds = new SKRect(geometry.TimeTextOrigin.X, geometry.Bar.Top, geometry.Timeline.Left, geometry.Bar.Bottom),
                    SourceNode = element,
                    Typeface = TextLayoutHelper.ResolveTypeface("sans-serif", text),
                    FontSize = geometry.FontSize,
                    Color = IconColor,
                    FallbackText = text,
                    TextOrigin = geometry.TimeTextOrigin,
                });
            }

            // Timeline: track, buffered, played.
            if (geometry.Timeline.Width > 0)
            {
                var timeline = geometry.Timeline;
                float y0 = timeline.MidY - TimelineHeight / 2, y1 = timeline.MidY + TimelineHeight / 2;
                nodes.Add(new BackgroundPaintNode { Bounds = new SKRect(timeline.Left, y0, timeline.Right, y1), SourceNode = element, Color = TrackColor });
                double duration = state.Duration;
                if (duration > 0 && !double.IsInfinity(duration))
                {
                    float buffered = (float)Math.Clamp(state.BufferedEnd / duration, 0, 1);
                    float played = (float)Math.Clamp(state.CurrentTime / duration, 0, 1);
                    if (buffered > 0)
                        nodes.Add(new BackgroundPaintNode { Bounds = new SKRect(timeline.Left, y0, timeline.Left + timeline.Width * buffered, y1), SourceNode = element, Color = BufferedColor });
                    if (played > 0)
                        nodes.Add(new BackgroundPaintNode { Bounds = new SKRect(timeline.Left, y0, timeline.Left + timeline.Width * played, y1), SourceNode = element, Color = PlayedColor });
                    float knob = TimelineHeight * (hovered == MediaControlAction.Seek ? 2.2f : 1.5f);
                    float kx = timeline.Left + timeline.Width * played;
                    nodes.Add(new BackgroundPaintNode
                    {
                        Bounds = new SKRect(kx - knob, timeline.MidY - knob, kx + knob, timeline.MidY + knob),
                        SourceNode = element,
                        Color = PlayedColor,
                        BorderRadius = new[] { new SKPoint(knob, knob), new SKPoint(knob, knob), new SKPoint(knob, knob), new SKPoint(knob, knob) },
                    });
                }
            }

            // Mute icon: a speaker, with a strike when muted.
            var mute = geometry.MuteButton;
            bool muted = state.Muted || state.Volume <= 0;
            nodes.Add(new CustomPaintNode
            {
                Bounds = mute,
                SourceNode = element,
                PaintAction = (canvas, bounds) =>
                {
                    using var paint = new SKPaint { Color = IconColor, Style = SKPaintStyle.Fill, IsAntialias = true };
                    float w = bounds.Width, h = bounds.Height;
                    using var speaker = Rendering.PathBuilderHelper.Build(builder =>
                    {
                        builder.MoveTo(bounds.Left + w * 0.2f, bounds.Top + h * 0.38f);
                        builder.LineTo(bounds.Left + w * 0.38f, bounds.Top + h * 0.38f);
                        builder.LineTo(bounds.Left + w * 0.58f, bounds.Top + h * 0.2f);
                        builder.LineTo(bounds.Left + w * 0.58f, bounds.Top + h * 0.8f);
                        builder.LineTo(bounds.Left + w * 0.38f, bounds.Top + h * 0.62f);
                        builder.LineTo(bounds.Left + w * 0.2f, bounds.Top + h * 0.62f);
                        builder.Close();
                    });
                    canvas.DrawPath(speaker, paint);
                    using var stroke = new SKPaint { Color = IconColor, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.5f, w * 0.08f), IsAntialias = true };
                    if (muted)
                    {
                        canvas.DrawLine(bounds.Left + w * 0.64f, bounds.Top + h * 0.34f, bounds.Left + w * 0.86f, bounds.Top + h * 0.66f, stroke);
                        canvas.DrawLine(bounds.Left + w * 0.86f, bounds.Top + h * 0.34f, bounds.Left + w * 0.64f, bounds.Top + h * 0.66f, stroke);
                    }
                    else
                    {
                        canvas.DrawArc(new SKRect(bounds.Left + w * 0.5f, bounds.Top + h * 0.3f, bounds.Left + w * 0.86f, bounds.Top + h * 0.7f), -45, 90, false, stroke);
                    }
                },
            });
        }
    }
}
