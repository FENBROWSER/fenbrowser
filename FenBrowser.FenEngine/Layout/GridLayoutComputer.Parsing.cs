using System;
using System.Collections.Generic;
using System.Linq;

namespace FenBrowser.FenEngine.Layout
{
    public static partial class GridLayoutComputer
    {
        private static List<GridTrack> ParseTracks(string template, float containerSize, float gap)
        {
            var tracks = new List<GridTrack>();
            if (string.IsNullOrWhiteSpace(template)) return tracks;
            
            // Normalize spaces around parenthesis
            template = template.Replace("(", " ( ").Replace(")", " ) ").Replace(",", " , ");
            
            var tokens = template.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int index = 0;

            while (index < tokens.Length)
            {
                ParseTrackDefinition(tokens, ref index, tracks, containerSize, gap);
            }

            // Convert % to Px if container size is known
            foreach (var t in tracks)
            {
                if (t.MinLimit.IsPercent) t.MinLimit = GridTrackSize.FromPx(containerSize * t.MinLimit.Value / 100f);
                if (t.MaxLimit.IsPercent) t.MaxLimit = GridTrackSize.FromPx(containerSize * t.MaxLimit.Value / 100f);
                if (t.MaxLimit.Type == GridUnitType.FitContent && t.MaxLimit.FitContentIsPercent)
                {
                    float resolvedLimit = containerSize > 0 ? (containerSize * t.MaxLimit.FitContentLimit / 100f) : 0;
                    t.MaxLimit = GridTrackSize.FromFitContent(resolvedLimit);
                }
                
                // Initialize BaseSize to MinLimit (if fixed)
                if (t.MinLimit.IsPx) t.BaseSize = t.MinLimit.Value;
            }

            return tracks;
        }

        private static void ParseTrackDefinition(string[] tokens, ref int index, List<GridTrack> tracks, float containerSize, float gap)
        {
            if (index >= tokens.Length) return;
            string t = tokens[index];

            if (t == "," || t == ")")
            {
                index++;
                return;
            }

            if (t.Equals("repeat", StringComparison.OrdinalIgnoreCase))
            {
                index++; // consume 'repeat'
                Consume(tokens, ref index, "(");
                
                string countStr = index < tokens.Length ? tokens[index++] : "1";
                Consume(tokens, ref index, ",");
                
                int count = 1;
                bool isAutoRepeat = false;
                AutoRepeatMode repeatMode = AutoRepeatMode.None;
                if (countStr.Equals("auto-fill", StringComparison.OrdinalIgnoreCase) || countStr.Equals("auto-fit", StringComparison.OrdinalIgnoreCase))
                {
                    isAutoRepeat = true;
                    repeatMode = countStr.Equals("auto-fit", StringComparison.OrdinalIgnoreCase)
                        ? AutoRepeatMode.Fit
                        : AutoRepeatMode.Fill;
                }
                else if (!int.TryParse(countStr, out count) || count < 1)
                {
                    count = 1;
                }

                var repeatedTracks = new List<GridTrack>();
                while (index < tokens.Length && tokens[index] != ")")
                {
                   ParseTrackDefinition(tokens, ref index, repeatedTracks, containerSize, gap);
                }
                Consume(tokens, ref index, ")");

                if (repeatedTracks.Count == 0)
                {
                    return;
                }

                if (isAutoRepeat)
                {
                    count = ResolveAutoRepeatCount(repeatedTracks, containerSize, gap);
                }
                
                for (int i = 0; i < count; i++)
                {
                    foreach (var baseTrack in repeatedTracks)
                    {
                        var clonedTrack = CloneTrack(baseTrack);
                        clonedTrack.AutoRepeatMode = repeatMode;
                        tracks.Add(clonedTrack);
                    }
                }
            }
            else if (t.Equals("minmax", StringComparison.OrdinalIgnoreCase))
            {
                index++; // consume minmax
                Consume(tokens, ref index, "(");
                var min = ParseSimpleSize(tokens[index++]);
                Consume(tokens, ref index, ",");
                var max = ParseSimpleSize(tokens[index++]);
                Consume(tokens, ref index, ")");
                
                tracks.Add(new GridTrack { MinLimit = min, MaxLimit = max });
            }
            else if (t.Equals("fit-content", StringComparison.OrdinalIgnoreCase))
            {
                index++; // consume fit-content
                Consume(tokens, ref index, "(");
                var limit = ParseSimpleSize(tokens[index++]);
                Consume(tokens, ref index, ")");
                
                // fit-content(limit) is effectively min(max-content, max(auto, limit))
                // We represent it as a track with MaxLimit = FitContent(limit)
                // MinLimit should be Auto (or 0?).
                var track = new GridTrack();
                track.MinLimit = GridTrackSize.Auto;
                
                // If limit is Px, we store it. If limit is Percent, we convert if possible?
                // limit usually <length> or <percentage>.
                if (limit.IsPx) track.MaxLimit = GridTrackSize.FromFitContent(limit.Value);
                else if (limit.IsPercent) track.MaxLimit = GridTrackSize.FromFitContent(limit.Value, isPercent: true);
                // Assuming ParseSimpleSize returns Px or Percent.
                // If Px, value is absolute. If Percent, value is % number.
                // We'll store value logic in FromFitContent.
                
                tracks.Add(track);
            }
            else
            {
                // Single simple track
                var size = ParseSimpleSize(tokens[index++]);
                
                var track = new GridTrack();
                if (size.IsPx || size.IsPercent)
                {
                    track.MinLimit = size;
                    track.MaxLimit = size;
                }
                else if (size.IsFlex)
                {
                    track.MinLimit = GridTrackSize.Auto;
                    track.MaxLimit = size;
                }
                else // Auto, MinContent, MaxContent
                {
                    track.MinLimit = size;
                    track.MaxLimit = size;
                }
                tracks.Add(track);
            }
        }

        private static int ResolveAutoRepeatCount(List<GridTrack> repeatedTracks, float containerSize, float gap)
        {
            if (repeatedTracks == null || repeatedTracks.Count == 0)
            {
                return 1;
            }

            if (containerSize <= 0 || float.IsNaN(containerSize) || float.IsInfinity(containerSize))
            {
                return 1;
            }

            float safeGap = Math.Max(0, gap);
            float trackSpan = 0;
            foreach (var track in repeatedTracks)
            {
                if (!TryResolveAutoRepeatTrackMinBreadth(track, containerSize, out float breadth))
                {
                    // Unresolved intrinsic/flex minima: keep deterministic single-repeat fallback.
                    return 1;
                }

                trackSpan += Math.Max(0, breadth);
            }

            int tracksPerRepeat = Math.Max(1, repeatedTracks.Count);
            float denominator = trackSpan + (tracksPerRepeat * safeGap);
            if (denominator <= 0)
            {
                return 1;
            }

            // Total width for N repeats with K tracks each:
            // N * trackSpan + (N*K - 1) * gap <= containerSize
            // N * (trackSpan + K*gap) <= containerSize + gap
            int repeatCount = (int)Math.Floor((containerSize + safeGap) / denominator);
            return Math.Max(1, repeatCount);
        }

        private static bool TryResolveAutoRepeatTrackMinBreadth(GridTrack track, float containerSize, out float breadth)
        {
            breadth = 0;

            if (track == null)
            {
                return false;
            }

            var min = track.MinLimit;
            if (TryResolveDefiniteBreadth(min, containerSize, out breadth))
            {
                return true;
            }

            // For cases like minmax(auto, 120px), the max track sizing function is definite
            // and can be used to derive repeat breadth for auto-repeat count computation.
            var max = track.MaxLimit;
            if (TryResolveDefiniteBreadth(max, containerSize, out breadth))
            {
                return true;
            }

            return false;
        }

        private static bool TryResolveDefiniteBreadth(GridTrackSize size, float containerSize, out float breadth)
        {
            breadth = 0;
            switch (size.Type)
            {
                case GridUnitType.Px:
                    breadth = size.Value;
                    return true;
                case GridUnitType.Percent:
                    breadth = containerSize * size.Value / 100f;
                    return true;
                case GridUnitType.FitContent:
                    breadth = size.FitContentLimit > 0 ? size.FitContentLimit : size.Value;
                    return true;
                default:
                    return false;
            }
        }

        private static GridTrackSize ParseSimpleSize(string val)
        {
            val = val.Trim().ToLowerInvariant();
            if (val == "min-content") return GridTrackSize.MinContent;
            if (val == "max-content") return GridTrackSize.MaxContent;
            
            if (val.EndsWith("fr"))
            {
                 if (!float.TryParse(val.Replace("fr", ""), out float fr)) fr = 1;
                 return GridTrackSize.FromFr(fr);
            }
            if (val.EndsWith("px"))
            {
                 float.TryParse(val.Replace("px", ""), out float px);
                 return GridTrackSize.FromPx(px);
            }
            if (val.EndsWith("%"))
            {
                 float.TryParse(val.Replace("%", ""), out float pct);
                 return GridTrackSize.FromPercent(pct);
            }
            return GridTrackSize.Auto;
        }

        private static void Consume(string[] tokens, ref int index, string expected)
        {
             if (index < tokens.Length && tokens[index] == expected) index++;
        }
    }
}
