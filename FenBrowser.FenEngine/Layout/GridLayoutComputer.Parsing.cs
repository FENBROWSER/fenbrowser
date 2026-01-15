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
                
                // Initialize BaseSize to MinLimit (if fixed)
                if (t.MinLimit.IsPx) t.BaseSize = t.MinLimit.Value;
            }

            return tracks;
        }

        private static void ParseTrackDefinition(string[] tokens, ref int index, List<GridTrack> tracks, float containerSize, float gap)
        {
            if (index >= tokens.Length) return;
            string t = tokens[index];

            if (t.Equals("repeat", StringComparison.OrdinalIgnoreCase))
            {
                index++; // consume 'repeat'
                Consume(tokens, ref index, "(");
                
                string countStr = tokens[index++];
                Consume(tokens, ref index, ",");
                
                int count = 1;
                bool isAutoFill = false;
                if (countStr.Equals("auto-fill", StringComparison.OrdinalIgnoreCase) || countStr.Equals("auto-fit", StringComparison.OrdinalIgnoreCase))
                {
                    isAutoFill = true;
                    // For now, default to 1 as placeholder until auto-fill calc is added
                    count = 1; 
                }
                else int.TryParse(countStr, out count);

                var repeatedTracks = new List<GridTrack>();
                while (index < tokens.Length && tokens[index] != ")")
                {
                   ParseTrackDefinition(tokens, ref index, repeatedTracks, containerSize, gap);
                }
                Consume(tokens, ref index, ")");

                if (isAutoFill)
                {
                    // Calculate minimum size of repeated block
                    float minBlockSize = 0;
                    foreach (var rpt in repeatedTracks)
                    {
                        if (rpt.MinLimit.IsPx) minBlockSize += rpt.MinLimit.Value;
                        else if (rpt.BaseSize > 0) minBlockSize += rpt.BaseSize;
                        // Treat auto/fr minimum as 0? Or 1px?
                        // Spec requires min-track-list-size. If 0, we risk infinite.
                        // For auto-fill, assume at least 1px if 0 to avoid div/0 or infinite.
                        else minBlockSize += 1; // Safety fallback
                    }
                    
                    if (minBlockSize > 0 && containerSize > 0)
                    {
                        // Formula: N * size + (N-1) * gap <= available
                        // N * (size + gap) <= available + gap
                        float repeatedSize = minBlockSize + gap;
                        count = (int)((containerSize + gap) / repeatedSize);
                        if (count < 1) count = 1;
                    }
                }
                
                for (int i = 0; i < count; i++)
                {
                    foreach (var baseTrack in repeatedTracks)
                    {
                        tracks.Add(CloneTrack(baseTrack));
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
                else if (limit.IsPercent) track.MaxLimit = GridTrackSize.FromFitContent(limit.Value); // Value is raw %? Need context.
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
