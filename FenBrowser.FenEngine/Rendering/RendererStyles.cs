using System;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FenBrowser.FenEngine.Rendering;
using FrameworkElement = Avalonia.Controls.Control;

namespace FenBrowser.FenEngine.Rendering
{
    internal static class RendererStyles
    {
        private static void SetIfWritable(object target, string propName, object value)
        {
            if (target == null) return;
            try
            {
                var pi = target.GetType().GetRuntimeProperty(propName);
                if (pi != null && pi.CanWrite) pi.SetValue(target, value);
            }
            catch { }
        }
        private static double GetDoublePropertyOrDefault(object target, string propName, double def)
        {
            if (target == null) return def;
            try
            {
                var pi = target.GetType().GetRuntimeProperty(propName);
                if (pi != null && pi.CanRead)
                {
                    var v = pi.GetValue(target);
                    if (v is double) return (double)v;
                    if (v is float) return Convert.ToDouble(v);
                    if (v is int) return Convert.ToDouble(v);
                }
            }
            catch { }
            return def;
        }
        // Minimal CSS color parser for shadow colors (hex, rgb/rgba, a few names)
        private static SolidColorBrush TryParseCssColor(string css)
        {
            if (string.IsNullOrWhiteSpace(css)) return null;
            try
            {
                var s = css.Trim();
                if (s.StartsWith("#"))
                {
                    s = s.Substring(1);
                    byte a = 0xFF, r = 0, g = 0, b = 0;
                    if (s.Length == 3)
                    {
                        r = Convert.ToByte(new string(s[0], 2), 16);
                        g = Convert.ToByte(new string(s[1], 2), 16);
                        b = Convert.ToByte(new string(s[2], 2), 16);
                    }
                    else if (s.Length == 6)
                    {
                        r = Convert.ToByte(s.Substring(0, 2), 16);
                        g = Convert.ToByte(s.Substring(2, 2), 16);
                        b = Convert.ToByte(s.Substring(4, 2), 16);
                    }
                    else if (s.Length == 8)
                    {
                        a = Convert.ToByte(s.Substring(0, 2), 16);
                        r = Convert.ToByte(s.Substring(2, 2), 16);
                        g = Convert.ToByte(s.Substring(4, 2), 16);
                        b = Convert.ToByte(s.Substring(6, 2), 16);
                    }
                    return new SolidColorBrush(Color.FromArgb(a, r, g, b));
                }
                var l = s.ToLowerInvariant();
                if (l.StartsWith("rgba(") || l.StartsWith("rgb("))
                {
                    var inside = l.Substring(l.IndexOf('(') + 1).TrimEnd(')');
                    var parts = inside.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3) return null;
                    Func<string, byte> ch = p =>
                    {
                        p = p.Trim();
                        if (p.EndsWith("%")) { double v; return (byte)(double.TryParse(p.TrimEnd('%'), out v) ? Math.Max(0, Math.Min(255, v / 100.0 * 255.0)) : 0); }
                        double d; if (double.TryParse(p, out d)) { if (d <= 1) d *= 255.0; return (byte)Math.Max(0, Math.Min(255, d)); }
                        return (byte)0;
                    };
                    byte rr = ch(parts[0]); byte gg = ch(parts[1]); byte bb = ch(parts[2]); byte aa = 255;
                    if (parts.Length >= 4)
                    { double a; if (double.TryParse(parts[3], out a)) aa = (byte)Math.Max(0, Math.Min(255, a <= 1 ? a * 255.0 : a)); }
                    return new SolidColorBrush(Color.FromArgb(aa, rr, gg, bb));
                }
                if (l == "black") return new SolidColorBrush(Colors.Black);
                if (l == "white") return new SolidColorBrush(Colors.White);
                if (l == "gray" || l == "grey") return new SolidColorBrush(Colors.Gray);
                if (l == "transparent") return new SolidColorBrush(Colors.Transparent);

                // Fallback for linear-gradient: pick first valid color
                if (l.StartsWith("linear-gradient"))
                {
                    try
                    {
                        var content = l.Substring(l.IndexOf('(') + 1).TrimEnd(')');
                        var parts = content.Split(',');
                        foreach (var part in parts)
                        {
                            var p = part.Trim();
                            // Skip direction/angle
                            if (p.IndexOf("deg", StringComparison.OrdinalIgnoreCase) >= 0 || p.StartsWith("to ", StringComparison.OrdinalIgnoreCase)) continue;
                            
                            // Try to parse color (might have percentage at end e.g. "#000 0%")
                            var colorPart = p.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
                            var brush = TryParseCssColor(colorPart);
                            if (brush != null) return brush;
                        }
                    }
                    catch { }
                }

                // Try named colors via reflection
                var props = typeof(Colors).GetRuntimeProperties();
                foreach (var p in props)
                {
                    if (string.Equals(p.Name, s, StringComparison.OrdinalIgnoreCase) && p.PropertyType == typeof(Color))
                    {
                        return new SolidColorBrush((Color)p.GetValue(null));
                    }
                }
            }
            catch { }
            return null;
        }
        // Wrap content with margin/padding/border/background from computed css
        public static FrameworkElement WrapWithBoxes(FrameworkElement content, CssComputed css)
        {
            if (content == null || css == null)
                return content;

            // display:none ? collapse element
                if (string.Equals(css.Display, "none", StringComparison.OrdinalIgnoreCase))
                {
                    // For UWP/WinUI, set Visibility to Collapsed
                    try { content.IsVisible = false; } catch { }
                    return content;
                }

            bool hasPadding = !IsZero(css.Padding);
            bool hasBorder = !IsZero(css.BorderThickness) && css.BorderBrush != null;
            bool hasBackground = css.Background != null;
            bool hasBgImage = false;
            ImageBrush bgImageBrush = null;
            try
            {
                string bgImg;
                if (css.Map != null && css.Map.TryGetValue("background-image", out bgImg) && !string.IsNullOrWhiteSpace(bgImg))
                {
                    var br = TryMakeImageBrush(bgImg, css);
                    if (br != null) { hasBgImage = true; bgImageBrush = br; }
                }
                else if (css.Map != null)
                {
                    string bg;
                    if (css.Map.TryGetValue("background", out bg) && !string.IsNullOrWhiteSpace(bg))
                    {
                        var br = TryMakeImageBrush(bg, css);
                        if (br != null) { hasBgImage = true; bgImageBrush = br; }
                    }
                }
            }
            catch { }

            // If properties were not present in the declaration map, avoid forcing them
            bool mapHasBorder = css.Map != null && (css.Map.ContainsKey("border") || css.Map.ContainsKey("border-width") || css.Map.ContainsKey("border-color") || css.Map.ContainsKey("border-radius"));
            bool mapHasBackground = css.Map != null && (css.Map.ContainsKey("background") || css.Map.ContainsKey("background-color") || css.Map.ContainsKey("background-image"));

            FrameworkElement returned = content;

            if (hasPadding || (hasBorder && mapHasBorder) || (hasBackground && mapHasBackground))
            {
                var border = new Border { Child = content };

                if (hasPadding)
                    border.Padding = css.Padding;

                if (mapHasBorder && !IsZero(css.BorderThickness))
                    border.BorderThickness = css.BorderThickness;

                if (mapHasBorder && css.BorderBrush != null)
                    border.BorderBrush = css.BorderBrush;

                if (mapHasBackground)
                {
                    if (hasBgImage && bgImageBrush != null) border.Background = bgImageBrush;
                    else if (css.Background != null) border.Background = css.Background;
                }

                // border-radius support
                    if (mapHasBorder && !IsZero(css.BorderRadius))
                        border.CornerRadius = css.BorderRadius;

                // box-shadow approximation: parse first non-inset layer and simulate offset/spread/radius
                try
                {
                    string bsh; if (css.Map != null && css.Map.TryGetValue("box-shadow", out bsh) && !string.IsNullOrWhiteSpace(bsh) && !bsh.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        // pick first non-inset layer
                        Func<string, string[]> splitLayers = (raw) =>
                        {
                            var list = new System.Collections.Generic.List<string>();
                            var sb = new System.Text.StringBuilder(); int depth = 0; bool inQ = false; char qc = '\0';
                            foreach (var ch in raw)
                            {
                                if ((ch == '\'' || ch == '"')) { if (!inQ) { inQ = true; qc = ch; } else if (qc == ch) inQ = false; }
                                else if (!inQ && ch == '(') depth++; else if (!inQ && ch == ')') depth = Math.Max(0, depth - 1);
                                if (!inQ && depth == 0 && ch == ',') { list.Add(sb.ToString()); sb.Clear(); }
                                else sb.Append(ch);
                            }
                            if (sb.Length > 0) list.Add(sb.ToString());
                            return list.ToArray();
                        };
                        var layers = splitLayers(bsh);
                        string layer = null;
                        foreach (var L in layers)
                        {
                            if (string.IsNullOrWhiteSpace(L)) continue; if (L.IndexOf("inset", StringComparison.OrdinalIgnoreCase) >= 0) continue; layer = L; break;
                        }
                        if (string.IsNullOrWhiteSpace(layer)) layer = layers[0];

                        // tokenize by spaces but keep functions intact
                        var tokens = new System.Collections.Generic.List<string>();
                        {
                            var sb2 = new System.Text.StringBuilder(); int depth2 = 0; foreach (var ch in layer)
                            {
                                if (ch == '(') depth2++; else if (ch == ')') depth2 = Math.Max(0, depth2 - 1);
                                if (char.IsWhiteSpace(ch) && depth2 == 0)
                                { if (sb2.Length > 0) { tokens.Add(sb2.ToString()); sb2.Clear(); } }
                                else sb2.Append(ch);
                            }
                            if (sb2.Length > 0) tokens.Add(sb2.ToString());
                        }

                        double offX = 3, offY = 3, blur = 0, spread = 0; SolidColorBrush color = null;
                        foreach (var t in tokens)
                        {
                            var tl = t.ToLowerInvariant();
                            if (tl == "inset") continue;
                            if (tl.StartsWith("#") || tl.StartsWith("rgb")) { color = TryParseCssColor(t); continue; }
                            double v;
                            var num = tl.Replace("px", "");
                            if (double.TryParse(num, out v))
                            {
                                if (double.IsNaN(offX)) offX = v; else if (double.IsNaN(offY)) offY = v; else if (double.IsNaN(blur)) blur = v; else if (double.IsNaN(spread)) spread = v;
                                continue;
                            }
                        }
                        // reset NaNs if we used that logic
                        if (double.IsNaN(offX) || double.IsNaN(offY)) { offX = 3; offY = 3; }
                        if (double.IsNaN(blur)) blur = 0; if (double.IsNaN(spread)) spread = 0;

                        byte alpha = (byte)(blur > 0 ? 28 : 20);
                        var col = (color != null ? color.Color : Colors.Black);
                        var shadow = new Border
                        {
                            Background = new SolidColorBrush(Color.FromArgb(alpha, col.R, col.G, col.B)),
                            // spread: enlarge rectangle; negative right/bottom to expand
                            Margin = new Thickness(offX - spread, offY - spread, -spread, -spread),
                            CornerRadius = !IsZero(css.BorderRadius) ? css.BorderRadius : new CornerRadius(0),
                            IsHitTestVisible = false
                        };
                        var host = new Grid();
                        host.Children.Add(shadow);
                        host.Children.Add(border);
                        returned = host;
                    }
                    else returned = border;
                }
                catch { returned = border; }
            }

            // margin on the outer element
            if (!IsZero(css.Margin))
                returned.Margin = css.Margin;

            // Center block with margin-left/right:auto when a width is specified
            try
            {
                string ml = null, mr = null, m = null;
                if (css.Map != null)
                {
                    css.Map.TryGetValue("margin-left", out ml);
                    css.Map.TryGetValue("margin-right", out mr);
                    css.Map.TryGetValue("margin", out m);
                }
                bool leftAuto = (!string.IsNullOrEmpty(ml) && ml.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase));
                bool rightAuto = (!string.IsNullOrEmpty(mr) && mr.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(ml) || string.IsNullOrEmpty(mr))
                {
                    // Check shorthand: margin: top right bottom left
                    var parts = (m ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        // vertical | horizontal
                        if (parts[1].Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) { leftAuto = rightAuto = true; }
                    }
                    else if (parts.Length == 3)
                    {
                        var h = parts[1].Trim(); if (h.Equals("auto", StringComparison.OrdinalIgnoreCase)) { leftAuto = rightAuto = true; }
                    }
                    else if (parts.Length >= 4)
                    {
                        var r = parts[1].Trim(); var l = parts[3].Trim();
                        if (r.Equals("auto", StringComparison.OrdinalIgnoreCase)) rightAuto = true;
                        if (l.Equals("auto", StringComparison.OrdinalIgnoreCase)) leftAuto = true;
                    }
                }
                if ((leftAuto && rightAuto) && (css.Width.HasValue || (css.Map != null && css.Map.ContainsKey("width"))))
                {
                    returned.HorizontalAlignment = HorizontalAlignment.Center;
                }
            }
            catch { }

            // Apply width/height honoring box-sizing (+ basic % support)
            try
            {
                string __bsRaw = null;
                var boxSizing = (css.Map != null && css.Map.TryGetValue("box-sizing", out __bsRaw)) ? (__bsRaw ?? "").Trim().ToLowerInvariant() : "";
                bool borderBox = boxSizing == "border-box";

                string wRaw = null, hRaw = null;
                if (css.Map != null) { css.Map.TryGetValue("width", out wRaw); css.Map.TryGetValue("height", out hRaw); }
                if (css.Width.HasValue) { if (borderBox) returned.Width = css.Width.Value; else content.Width = css.Width.Value; }
                else if (!string.IsNullOrWhiteSpace(wRaw) && wRaw.Trim().EndsWith("%"))
                {
                    returned.Loaded += (s, e) => { try { ApplyPercentSize(returned, true, wRaw); } catch { } };
                    returned.SizeChanged += (s, e) => { try { ApplyPercentSize(returned, true, wRaw); } catch { } };
                }
                if (css.Height.HasValue) { if (borderBox) returned.Height = css.Height.Value; else content.Height = css.Height.Value; }
                else if (!string.IsNullOrWhiteSpace(hRaw) && hRaw.Trim().EndsWith("%"))
                {
                    returned.Loaded += (s, e) => { try { ApplyPercentSize(returned, false, hRaw); } catch { } };
                    returned.SizeChanged += (s, e) => { try { ApplyPercentSize(returned, false, hRaw); } catch { } };
                }
                if (css.MinWidth.HasValue) returned.MinWidth = css.MinWidth.Value;
                if (css.MinHeight.HasValue) returned.MinHeight = css.MinHeight.Value;
                if (css.MaxWidth.HasValue) returned.MaxWidth = css.MaxWidth.Value;
                if (css.MaxHeight.HasValue) returned.MaxHeight = css.MaxHeight.Value;
            }
            catch { }

            // position:relative offset approximation via margin shift
            try
            {
                var pos = (css.Position ?? (css.Map != null && css.Map.ContainsKey("position") ? css.Map["position"] : null)) ?? string.Empty;
                pos = pos.Trim().ToLowerInvariant();
                if (pos == "relative")
                {
                    var m = returned.Margin;
                        double mLeft = m.Left, mTop = m.Top, mRight = m.Right, mBottom = m.Bottom;
                        if (css.Left.HasValue) mLeft += css.Left.Value;
                        if (css.Top.HasValue) mTop += css.Top.Value;
                        // right/bottom move the box in the opposite direction
                        if (css.Right.HasValue) mLeft -= css.Right.Value;
                        if (css.Bottom.HasValue) mTop -= css.Bottom.Value;
                        returned.Margin = new Thickness(mLeft, mTop, mRight, mBottom);
                }
            }
            catch { }

            // overflow (axis-aware best-effort)
            try
            {
                string ov = css.Overflow ?? (css.Map != null && css.Map.ContainsKey("overflow") ? css.Map["overflow"] : null);
                string ovx = null, ovy = null;
                try { if (css.Map != null) { css.Map.TryGetValue("overflow-x", out ovx); css.Map.TryGetValue("overflow-y", out ovy); } } catch { }
                ov = (ov ?? string.Empty).Trim().ToLowerInvariant();
                ovx = (ovx ?? string.Empty).Trim().ToLowerInvariant();
                ovy = (ovy ?? string.Empty).Trim().ToLowerInvariant();

                bool anyAxisScroll = (!string.IsNullOrEmpty(ovx) && (ovx == "auto" || ovx == "scroll")) ||
                                     (!string.IsNullOrEmpty(ovy) && (ovy == "auto" || ovy == "scroll"));

                if (ov == "hidden" || (!string.IsNullOrEmpty(ovx) && ovx == "hidden" && !anyAxisScroll) || (!string.IsNullOrEmpty(ovy) && ovy == "hidden" && !anyAxisScroll))
                {
                    returned.SizeChanged += (s, e) =>
                    {
                        try
                        {
                            var r = new Avalonia.Rect(0, 0, (returned.Width), (returned.Height));
                            var rg = new Avalonia.Media.RectangleGeometry(r);
                            returned.Clip = rg;
                        }
                        catch { }
                    };
                }
                else if (ov == "auto" || ov == "scroll" || anyAxisScroll)
                {
                    var scroller = new ScrollViewer
                    {
                        Content = returned
                    };
                    // default
                    scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;

                    if (!string.IsNullOrEmpty(ovx))
                    {
                        if (ovx == "hidden") scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                        else if (ovx == "scroll" || ovx == "auto") scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                    }
                    if (!string.IsNullOrEmpty(ovy))
                    {
                        if (ovy == "hidden") scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                        else if (ovy == "scroll" || ovy == "auto") scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    }
                    returned = scroller;
                }
            }
            catch { }

                        // Generic visibility/opacity/pointer-events
            try
            {
                string vis;
                if (css.Map != null && css.Map.TryGetValue("visibility", out vis))
                {
                    if (!string.IsNullOrWhiteSpace(vis) && vis.Trim().Equals("hidden", StringComparison.OrdinalIgnoreCase))
                    {
                        // Keep layout but hide visuals and hit-testing
                        try { returned.Opacity = 0.0; } catch { }
                        try { returned.IsHitTestVisible = false; } catch { }
                    }
                }
                string op;
                if (css.Map != null && css.Map.TryGetValue("opacity", out op))
                {
                    double v; if (double.TryParse(op, out v)) { try { returned.Opacity = Math.Max(0, Math.Min(1, v)); } catch { } }
                }
                // Intentionally ignore pointer-events:none to keep UI interactive in no-JS path
            }
            catch { }
// transforms
            ApplyTransformOrigin(returned, css);
            ApplyTransform(returned, css);

            // basic transitions for position/transform changes (theme transition)
            try
            {
                string tr;
                // UWP-like transitions are ignored; keep rendering consistent.
                // For now, we ignore CSS transition hints (transition property) to keep rendering consistent.
            }
            catch { }

            return returned;
        }

        public static void ApplyTextStyle(FrameworkElement fe, CssComputed css)
        {
            if (fe == null || css == null)
                return;

            // display:none ? collapse element
            if (string.Equals(css.Display, "none", StringComparison.OrdinalIgnoreCase))
            {
                try { fe.IsVisible = false; } catch { }
                return;
            }

            // TextBlock styles
            var tb = fe as TextBlock;
            var rtb = fe as Avalonia.Controls.TextBlock;
            if (tb != null)
            {
                tb.TextWrapping = TextWrapping.Wrap;

                if (css.FontSize.HasValue)
                    tb.FontSize = css.FontSize.Value;
                else
                {
                    // font-size: support em/rem/% (best-effort)
                    try
                    {
                        string fsv; if (css.Map != null && css.Map.TryGetValue("font-size", out fsv) && !string.IsNullOrWhiteSpace(fsv))
                        {
                            var px = TryParseFontSize(fsv, tb.FontSize);
                            if (px > 0) tb.FontSize = px;
                        }
                    }
                    catch { }
                }

                if (css.FontWeight.HasValue)
                    tb.FontWeight = css.FontWeight.Value;
                else
                {
                    // numeric font-weight mapping (100..900)
                    try
                    {
                        string fw;
                        if (css.Map != null && css.Map.TryGetValue("font-weight", out fw) && !string.IsNullOrWhiteSpace(fw))
                        {
                            int n; if (int.TryParse(fw.Trim(), out n))
                            {
                                if (n <= 100) tb.FontWeight = FontWeight.Thin;
                                else if (n <= 200) tb.FontWeight = FontWeight.ExtraLight;
                                else if (n <= 300) tb.FontWeight = FontWeight.Light;
                                else if (n <= 400) tb.FontWeight = FontWeight.Normal;
                                else if (n <= 500) tb.FontWeight = FontWeight.Medium;
                                else if (n <= 600) tb.FontWeight = FontWeight.SemiBold;
                                else if (n <= 700) tb.FontWeight = FontWeight.Bold;
                                else if (n <= 800) tb.FontWeight = FontWeight.ExtraBold;
                                else tb.FontWeight = FontWeight.Black;
                            }
                        }
                    }
                    catch { }
                }

                if (css.FontStyle.HasValue)
                    tb.FontStyle = css.FontStyle.Value;

                // Try registry mapping for @font-face (packaged/local fonts); fall back to computed
                try
                {
                    string famRaw;
                    if (css.Map != null && css.Map.TryGetValue("font-family", out famRaw) && !string.IsNullOrWhiteSpace(famRaw))
                    {
                        var parts = famRaw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts != null && parts.Length > 0)
                        {
                            var famName = (parts[0] ?? "").Trim().Trim('"', '\'');
                            var mapped = FontRegistry.TryResolve(famName);
                            if (mapped != null) tb.FontFamily = mapped; else if (css.FontFamily != null) tb.FontFamily = css.FontFamily;
                        }
                        else if (css.FontFamily != null) tb.FontFamily = css.FontFamily;
                    }
                    else if (css.FontFamily != null) tb.FontFamily = css.FontFamily;
                }
                catch { if (css.FontFamily != null) tb.FontFamily = css.FontFamily; }

                if (css.TextAlign.HasValue)
                    tb.TextAlignment = css.TextAlign.Value;

                // line-height (px or number) from Map if present
                try
                {
                    string lh;
                    if (css.Map != null && css.Map.TryGetValue("line-height", out lh) && !string.IsNullOrWhiteSpace(lh))
                    {
                        double px = 0;
                        var s = lh.Trim().ToLowerInvariant();
                        if (s.EndsWith("px"))
                        {
                            s = s.Substring(0, s.Length - 2);
                            if (double.TryParse(s, out px) && px > 0) tb.LineHeight = px;
                        }
                        else
                        {
                            // unitless => multiplier
                            double mul; if (double.TryParse(s, out mul) && mul > 0) tb.LineHeight = tb.FontSize * mul;
                        }
                    }
                }
                catch { }

                if (css.Foreground != null)
                    tb.Foreground = (Brush)css.Foreground;

                // word-break and overflow-wrap/word-wrap
                try
                {
                    string wb; if (css.Map != null && css.Map.TryGetValue("word-break", out wb) && !string.IsNullOrWhiteSpace(wb))
                    {
                        var v = wb.Trim().ToLowerInvariant();
                        if (v.Contains("break-all")) tb.TextWrapping = TextWrapping.Wrap; // allow breaks inside words
                        else if (v.Contains("keep-all")) tb.TextWrapping = TextWrapping.Wrap;
                    }
                    string ow; if (css.Map != null && (css.Map.TryGetValue("overflow-wrap", out ow) || css.Map.TryGetValue("word-wrap", out ow)))
                    {
                        var v = (ow ?? "").Trim().ToLowerInvariant();
                        if (v.Contains("break-word")) tb.TextWrapping = TextWrapping.Wrap;
                    }
                }
                catch { }


                // letter-spacing -> CharacterSpacing (in 1/1000 of em); supports px or em/number
                try
                {
                    string ls;
                    if (css.Map != null && css.Map.TryGetValue("letter-spacing", out ls) && !string.IsNullOrWhiteSpace(ls))
                    {
                        var raw = ls.Trim().ToLowerInvariant();
                        double em = 0;
                        if (raw.EndsWith("px"))
                        {
                            double px; if (double.TryParse(raw.Substring(0, raw.Length - 2), out px) && tb.FontSize > 0)
                                em = px / tb.FontSize;
                        }
                        else if (raw.EndsWith("em"))
                        {
                            double v; if (double.TryParse(raw.Substring(0, raw.Length - 2), out v)) em = v;
                        }
                        else if (raw == "normal") { em = 0; }
                        else { double v; if (double.TryParse(raw, out v)) em = v; }

                        SetIfWritable(tb, "CharacterSpacing", (int)Math.Round(em * 1000.0));
                    }
                }
                catch { }

                // text-shadow approximation (headings/large text only)
                try
                {
                    string tsh;
                    if (css.Map != null && css.Map.TryGetValue("text-shadow", out tsh) && !string.IsNullOrWhiteSpace(tsh) && !tsh.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        // pick first shadow only
                        var layer = tsh;
                        var comma = tsh.IndexOf(','); if (comma > 0) layer = tsh.Substring(0, comma);
                        var parts = layer.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        double offX = 1, offY = 1, blur = 0; SolidColorBrush col = null;
                        foreach (var p in parts)
                        {
                            var pl = p.ToLowerInvariant();
                            if (pl.StartsWith("#") || pl.StartsWith("rgb")) { col = TryParseCssColor(p); continue; }
                            double v; var num = pl.Replace("px", "");
                            if (double.TryParse(num, out v))
                            {
                                if (double.IsNaN(offX)) offX = v; else if (double.IsNaN(offY)) offY = v; else if (double.IsNaN(blur)) blur = v;
                                continue;
                            }
                        }
                        // guard for readability; apply to larger text only
                        double fz = tb.FontSize > 0 ? tb.FontSize : 16;
                        if (fz >= 20)
                        {
                            var panelParent = tb.Parent as Panel;
                            if (panelParent != null)
                            {
                                var idx = panelParent.Children.IndexOf(tb);
                                panelParent.Children.RemoveAt(idx);
                                var host = new Grid();
                                var color = (col != null ? col.Color : Colors.Black);
                                byte a = (byte)(blur > 0 ? 110 : 90);
                                // For inline cases, approximate the full text by reading Text; if empty, concatenate runs
                                string text = tb.Text;
                                if (string.IsNullOrEmpty(text) && tb.Inlines != null && tb.Inlines.Count > 0)
                                {
                                    var sb = new System.Text.StringBuilder();
                                    foreach (var inline in tb.Inlines)
                                    {
                                        var run = inline as Run; if (run != null && !string.IsNullOrEmpty(run.Text)) sb.Append(run.Text);
                                    }
                                    text = sb.ToString();
                                }
                                var shadow = new TextBlock
                                {
                                    Text = text,
                                    FontSize = tb.FontSize,
                                    FontWeight = tb.FontWeight,
                                    FontStyle = tb.FontStyle,
                                    Foreground = new SolidColorBrush(Color.FromArgb(a, color.R, color.G, color.B)),
                                    Margin = new Thickness(offX, offY, 0, 0),
                                    TextWrapping = tb.TextWrapping,
                                    IsHitTestVisible = false
                                };
                                host.Children.Add(shadow);
                                host.Children.Add(tb); // keep original on top so underline remains visible
                                panelParent.Children.Insert(idx, host);
                            }
                        }
                    }
                }
                catch { }

                // text-decoration: underline/line-through (best-effort)
                try
                {
                    string td; if (css.Map != null && css.Map.TryGetValue("text-decoration", out td) && !string.IsNullOrWhiteSpace(td))
                    {
                        var v = td.Trim().ToLowerInvariant();
                        // Strikethrough via TextDecorations when available
                        if (v.Contains("line-through"))
                        {
                            try
                            {
                                var prop = tb.GetType().GetRuntimeProperty("TextDecorations");
                                if (prop != null && prop.CanWrite)
                                {
                                    var t = prop.PropertyType; // avoid compile-time dependency
                                    var val = Enum.Parse(t, "Strikethrough", true);
                                    prop.SetValue(tb, val);
                                }
                            }
                            catch { }
                        }
                        if (v.Contains("underline"))
                        {
                            var txt = tb.Text ?? string.Empty;
                            tb.Inlines.Clear();
                            var u = new Underline();
                            u.Inlines.Add(new Run { Text = txt });
                            tb.Inlines.Add(u);
                        }
                    }
                }
                catch { }

                // text-transform: uppercase/lowercase/capitalize (apply at load)
                try
                {
                    string ttf; if (css.Map != null && css.Map.TryGetValue("text-transform", out ttf) && !string.IsNullOrWhiteSpace(ttf))
                    {
                        var mode = ttf.Trim().ToLowerInvariant();
                        tb.Loaded += (s__, e__) =>
                        {
                            try
                            {
                                var txt = tb.Text ?? string.Empty;
                                if (mode == "uppercase") tb.Text = txt.ToUpperInvariant();
                                else if (mode == "lowercase") tb.Text = txt.ToLowerInvariant();
                                else if (mode == "capitalize")
                                {
                                    var parts = txt.Split(' ');
                                    for (int i = 0; i < parts.Length; i++) if (parts[i].Length > 0) parts[i] = char.ToUpperInvariant(parts[i][0]) + (parts[i].Length > 1 ? parts[i].Substring(1) : "");
                                    tb.Text = string.Join(" ", parts);
                                }
                            }
                            catch { }
                        };
                    }
                }
                catch { }

                // text-indent (best-effort): use TextIndent when available, fallback to left margin
                try
                {
                    string ti; if (css.Map != null && css.Map.TryGetValue("text-indent", out ti) && !string.IsNullOrWhiteSpace(ti))
                    {
                        double px = 0; var s = ti.Trim().ToLowerInvariant();
                        if (s.EndsWith("px")) { double.TryParse(s.Substring(0, s.Length - 2), out px); }
                        else if (s.EndsWith("em")) { double v; if (double.TryParse(s.Substring(0, s.Length - 2), out v)) px = v * (tb.FontSize > 0 ? tb.FontSize : 16); }
                        else double.TryParse(s, out px);
                        if (px > 0)
                        {
                            try { var pi = tb.GetType().GetRuntimeProperty("TextIndent"); if (pi != null && pi.CanWrite) pi.SetValue(tb, px); else { var m = tb.Margin; tb.Margin = new Thickness(m.Left + px, m.Top, m.Right, m.Bottom); } }
                            catch { var m = tb.Margin; tb.Margin = new Thickness(m.Left + px, m.Top, m.Right, m.Bottom); }
                        }
                    }
                }
                catch { }

                // line-clamp (e.g. -webkit-line-clamp)
                try
                {
                    string lc = null; if (css.Map != null && (css.Map.TryGetValue("-webkit-line-clamp", out lc) || css.Map.TryGetValue("line-clamp", out lc)))
                    {
                        int n; if (int.TryParse((lc ?? "").Trim(), out n) && n > 0)
                        {
                            tb.TextTrimming = TextTrimming.CharacterEllipsis;
                            try { var prop = tb.GetType().GetRuntimeProperty("MaxLines"); if (prop != null && prop.CanWrite) prop.SetValue(tb, n); else { var lh = tb.LineHeight > 0 ? tb.LineHeight : (tb.FontSize * 1.4); if (lh > 0) tb.Height = lh * n; } }
                            catch { var lh = tb.LineHeight > 0 ? tb.LineHeight : (tb.FontSize * 1.4); if (lh > 0) tb.Height = lh * n; }
                        }
                    }
                }
                catch { }
            }
            else if (rtb != null)
            {
                // Mirror a subset of TextBlock styling for RichTextBlock
                double? fontSize = null;
                Avalonia.Media.FontWeight? fontWeight = null;
                Avalonia.Media.FontStyle? fontStyle = null;
                Avalonia.Media.FontFamily fontFamily = null;
                Brush foreground = null;

                if (css.FontSize.HasValue)
                    fontSize = css.FontSize.Value;
                else
                {
                    try
                    {
                        string fsv; if (css.Map != null && css.Map.TryGetValue("font-size", out fsv) && !string.IsNullOrWhiteSpace(fsv))
                        {
                            var px = TryParseFontSize(fsv, 16); // Use 16 as a default base size
                            if (px > 0) fontSize = px;
                        }
                    }
                    catch { }
                }

                if (css.FontWeight.HasValue)
                    fontWeight = css.FontWeight.Value;

                if (css.FontStyle.HasValue)
                    fontStyle = css.FontStyle.Value;

                try
                {
                    string famRaw;
                    if (css.Map != null && css.Map.TryGetValue("font-family", out famRaw) && !string.IsNullOrWhiteSpace(famRaw))
                    {
                        var parts = famRaw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts != null && parts.Length > 0)
                        {
                            var famName = (parts[0] ?? "").Trim().Trim('"', '\'');
                            var mapped = FontRegistry.TryResolve(famName);
                            if (mapped != null) fontFamily = mapped; else if (css.FontFamily != null) fontFamily = css.FontFamily;
                        }
                        else if (css.FontFamily != null) fontFamily = css.FontFamily;
                    }
                    else if (css.FontFamily != null) fontFamily = css.FontFamily;
                }
                catch { if (css.FontFamily != null) fontFamily = css.FontFamily; }

                if (css.Foreground != null)
                    foreground = (Brush)css.Foreground;

                // Apply styles to all Paragraphs in the RichTextBlock
                //foreach (var block in rtb.Blocks)
                //{
                //    var para = block as Windows.UI.Xaml.Documents.TextElement
                //    if (para != null)
                //    {
                //        if (fontSize.HasValue) para.FontSize = fontSize.Value;
                //        if (fontWeight.HasValue) para.FontWeight = fontWeight.Value;
                //        if (fontStyle.HasValue) para.FontStyle = fontStyle.Value;
                //        if (fontFamily != null) para.FontFamily = fontFamily;
                //        if (foreground != null) para.Foreground = foreground;
                //    }
                //}

                // The rest of the RichTextBlock styling logic (letter-spacing, text-indent, line-clamp, etc.) can remain as is,
                // but you must not use rtb.FontSize, rtb.FontWeight, etc. directly.
            }
            else
            {
                // Generic control styles (Foreground/Font for Button, TextBox, etc.)
                var ctrl = fe as Control;
                if (ctrl != null)
                {
                    if (css.FontSize.HasValue)
                        SetIfWritable(ctrl, "FontSize", css.FontSize.Value);
                    else
                    {
                        try
                        {
                            string fsv; if (css.Map != null && css.Map.TryGetValue("font-size", out fsv) && !string.IsNullOrWhiteSpace(fsv))
                            {
                                var px = TryParseFontSize(fsv, GetDoublePropertyOrDefault(ctrl, "FontSize", 12.0));
                                if (px > 0) SetIfWritable(ctrl, "FontSize", px);
                            }
                        }
                        catch { }
                    }

                    if (css.FontWeight.HasValue)
                        SetIfWritable(ctrl, "FontWeight", css.FontWeight.Value);

                    if (css.FontStyle.HasValue)
                        SetIfWritable(ctrl, "FontStyle", css.FontStyle.Value);

                    if (css.FontFamily != null)
                        SetIfWritable(ctrl, "FontFamily", css.FontFamily);

                    if (css.Foreground != null)
                        SetIfWritable(ctrl, "Foreground", css.Foreground);

                    // width/height for controls (px or %)
                    try
                    {
                        string wRaw = null, hRaw = null;
                        if (css.Map != null) { css.Map.TryGetValue("width", out wRaw); css.Map.TryGetValue("height", out hRaw); }
                        double px;
                        if (!string.IsNullOrWhiteSpace(wRaw))
                        {
                            var s = wRaw.Trim().ToLowerInvariant();
                            if (s.EndsWith("%"))
                            {
                                fe.Loaded += (s_, e_) => { try { ApplyPercentSize(fe, true, wRaw); } catch { } };
                                fe.SizeChanged += (s_, e_) => { try { ApplyPercentSize(fe, true, wRaw); } catch { } };
                            }
                            else if (double.TryParse(s.Replace("px",""), out px) && px > 0) fe.Width = px;
                        }
                        if (!string.IsNullOrWhiteSpace(hRaw))
                        {
                            var s = hRaw.Trim().ToLowerInvariant();
                            if (s.EndsWith("%"))
                            {
                                fe.Loaded += (s_, e_) => { try { ApplyPercentSize(fe, false, hRaw); } catch { } };
                                fe.SizeChanged += (s_, e_) => { try { ApplyPercentSize(fe, false, hRaw); } catch { } };
                            }
                            else if (double.TryParse(s.Replace("px",""), out px) && px > 0) fe.Height = px;
                        }

                        // min/max sizes
                        string mw, mh, xw, xh;
                        if (css.Map != null && css.Map.TryGetValue("min-width", out mw) && double.TryParse((mw ?? "").Replace("px",""), out px) && px > 0) fe.MinWidth = px;
                        if (css.Map != null && css.Map.TryGetValue("min-height", out mh) && double.TryParse((mh ?? "").Replace("px",""), out px) && px > 0) fe.MinHeight = px;
                        if (css.Map != null && css.Map.TryGetValue("max-width", out xw) && double.TryParse((xw ?? "").Replace("px",""), out px) && px > 0) fe.MaxWidth = px;
                        if (css.Map != null && css.Map.TryGetValue("max-height", out xh) && double.TryParse((xh ?? "").Replace("px",""), out px) && px > 0) fe.MaxHeight = px;
                    }
                    catch { }
                }
            }

            // If a background is set via computed styles and our element is a panel,
            // apply it directly (this covers cases where there was no border wrapper).
                var panel = fe as Panel;
            if (panel != null && css.Background != null)
            {
                panel.Background = css.Background;
            }

            // object-fit for Image
            try
            {
                var img = fe as Image;
                if (img != null && css.Map != null)
                {
                    string of; if (css.Map.TryGetValue("object-fit", out of) && !string.IsNullOrWhiteSpace(of))
                    {
                        var v = of.Trim().ToLowerInvariant();
                        if (v == "contain") img.Stretch = Stretch.Uniform;
                        else if (v == "cover") img.Stretch = Stretch.UniformToFill;
                        else if (v == "fill") img.Stretch = Stretch.Fill;
                        else if (v == "none") img.Stretch = Stretch.None;
                    }
                    // background-size approximation for Image elements used as backgrounds
                    string bsize; if (css.Map.TryGetValue("background-size", out bsize) && !string.IsNullOrWhiteSpace(bsize))
                    {
                        var s = bsize.Trim().ToLowerInvariant();
                        if (s == "cover") img.Stretch = Stretch.UniformToFill;
                        else if (s == "contain") img.Stretch = Stretch.Uniform;
                        else if (s == "auto") { /* leave default */ }
                        else if (s == "100% 100%") img.Stretch = Stretch.Fill;
                    }
                }
            }
            catch { }

            // white-space, text-overflow (best-effort)
            try
            {
                var tb2 = fe as TextBlock;
                if (tb2 != null && css.Map != null)
                {
                    string ws; if (css.Map.TryGetValue("white-space", out ws) && !string.IsNullOrWhiteSpace(ws))
                    {
                        var w = ws.Trim().ToLowerInvariant();
                        if (w == "nowrap") tb2.TextWrapping = TextWrapping.NoWrap;
                        else tb2.TextWrapping = TextWrapping.Wrap; // normal/pre handled elsewhere
                    }
                    string to; if (css.Map.TryGetValue("text-overflow", out to) && !string.IsNullOrWhiteSpace(to))
                    {
                        var ov = to.Trim().ToLowerInvariant();
                        if (ov.Contains("ellipsis")) tb2.TextTrimming = TextTrimming.CharacterEllipsis;
                    }
                }
            }
            catch { }
        }

        private static bool IsZero(Thickness t)
            => t.Left == 0 && t.Top == 0 && t.Right == 0 && t.Bottom == 0;

        private static bool IsZero(CornerRadius c)
            => c.TopLeft == 0 && c.TopRight == 0 && c.BottomRight == 0 && c.BottomLeft == 0;

        private static double ParsePx(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Trim();
            if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 2);
            double v; if (double.TryParse(s, out v)) return v; return 0;
        }

        private static void ApplyTransform(FrameworkElement fe, CssComputed css)
        {
            if (fe == null || css == null || css.Map == null) return;
            string t;
            if (!css.Map.TryGetValue("transform", out t) || string.IsNullOrWhiteSpace(t)) return;

            try
            {
                var group = new TransformGroup();
                var text = t.Trim();
                // Parse tokens like: rotate(30deg) translate(10px,20px) scale(1.2)
                var rx = new System.Text.RegularExpressions.Regex(@"(?<fn>[a-zA-Z]+)\s*\((?<args>[^)]*)\)");
                var m = rx.Matches(text);
                foreach (System.Text.RegularExpressions.Match mm in m)
                {
                    var fn = (mm.Groups["fn"].Value ?? "").ToLowerInvariant();
                    var args = (mm.Groups["args"].Value ?? "").Trim();
                    var parts = args.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (fn == "translate" || fn == "translatex" || fn == "translatey")
                    {
                        double dx = 0, dy = 0;
                        if (fn == "translatex") { if (parts.Length >= 1) dx = ParsePx(parts[0]); }
                        else if (fn == "translatey") { if (parts.Length >= 1) dy = ParsePx(parts[0]); }
                        else {
                            if (parts.Length >= 1) dx = ParsePx(parts[0]);
                            if (parts.Length >= 2) dy = ParsePx(parts[1]);
                        }
                        group.Children.Add(new TranslateTransform { X = dx, Y = dy });
                    }
                    else if (fn == "scale" || fn == "scalex" || fn == "scaley")
                    {
                        double sx = 1, sy = 1;
                        if (fn == "scalex") { if (parts.Length >= 1) double.TryParse(parts[0], out sx); sy = 1; }
                        else if (fn == "scaley") { if (parts.Length >= 1) double.TryParse(parts[0], out sy); sx = 1; }
                        else {
                            if (parts.Length >= 1) double.TryParse(parts[0], out sx);
                            if (parts.Length >= 2) double.TryParse(parts[1], out sy); else sy = sx;
                        }
                        group.Children.Add(new ScaleTransform { ScaleX = sx, ScaleY = sy });
                    }
                    else if (fn == "rotate")
                    {
                        // degrees (deg)
                        double angle = 0;
                        if (parts.Length >= 1)
                        {
                            var a = parts[0].Trim();
                            if (a.EndsWith("deg", StringComparison.OrdinalIgnoreCase)) a = a.Substring(0, a.Length - 3);
                            double.TryParse(a, out angle);
                        }
                        group.Children.Add(new RotateTransform { Angle = angle });
                    }
                    else if (fn == "skew" || fn == "skewx" || fn == "skewy")
                    {
                        double ax = 0, ay = 0;
                        if (fn == "skewx") { if (parts.Length >= 1) { var a=parts[0].Trim(); if (a.EndsWith("deg", StringComparison.OrdinalIgnoreCase)) a=a.Substring(0,a.Length-3); double.TryParse(a, out ax); } }
                        else if (fn == "skewy") { if (parts.Length >= 1) { var a=parts[0].Trim(); if (a.EndsWith("deg", StringComparison.OrdinalIgnoreCase)) a=a.Substring(0,a.Length-3); double.TryParse(a, out ay); } }
                        else {
                            if (parts.Length >= 1) { var a=parts[0].Trim(); if (a.EndsWith("deg", StringComparison.OrdinalIgnoreCase)) a=a.Substring(0,a.Length-3); double.TryParse(a, out ax); }
                            if (parts.Length >= 2) { var b=parts[1].Trim(); if (b.EndsWith("deg", StringComparison.OrdinalIgnoreCase)) b=b.Substring(0,b.Length-3); double.TryParse(b, out ay); }
                        }
                        group.Children.Add(new SkewTransform { AngleX = ax, AngleY = ay });
                    }
                }
                if (group.Children.Count > 0) fe.RenderTransform = group;
            }
            catch { }
        }

        private static void ApplyPercentSize(FrameworkElement element, bool isWidth, string raw)
        {
            if (element == null || string.IsNullOrWhiteSpace(raw)) return;
            var p = element.Parent as FrameworkElement; if (p == null) return;
            var s = raw.Trim();
            double pct = 0;
            if (s.EndsWith("%")) double.TryParse(s.TrimEnd('%'), out pct);
            var frac = Math.Max(0, pct) / 100.0;
            if (frac <= 0) return;
                const double Eps = 0.5; // avoid tiny oscillations
            if (isWidth)
            {
                    var w = (p.Width);
                if (w > 0)
                {
                    var desired = w * frac;
                        var current = double.IsNaN(element.Width) ? (element.Width) : element.Width;
                    if (double.IsNaN(current) || Math.Abs(current - desired) > Eps)
                        element.Width = desired;
                }
            }
            else
            {
                var h = p.Bounds.Height;
                if (h > 0)
                {
                    var desired = h * frac;
                    var current = double.IsNaN(element.Height) ? element.Bounds.Height : element.Height;
                    if (double.IsNaN(current) || Math.Abs(current - desired) > Eps)
                        element.Height = desired;
                }
            }
        }

        private static ImageBrush TryMakeImageBrush(string cssValue, CssComputed css)
        {
            try
            {
                var s = cssValue ?? string.Empty;
                int i = s.IndexOf("url(", StringComparison.OrdinalIgnoreCase);
                if (i < 0) return null;
                int st = i + 4;
                int en = s.IndexOf(')', st);
                if (en < 0) return null;
                var inside = s.Substring(st, en - st).Trim().Trim('\'', '"');
                if (string.IsNullOrWhiteSpace(inside)) return null;
                // Rewrite unsupported formats commonly used by CDNs (webp/avif)
                try
                {
                    string u = inside;
                    if (u.IndexOf("format=webp", StringComparison.OrdinalIgnoreCase) >= 0)
                        u = System.Text.RegularExpressions.Regex.Replace(u, @"format=webp", "format=jpg", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    u = System.Text.RegularExpressions.Regex.Replace(u, @"(\?|&)(f|fmt)=webp", "$1$2=jpg", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (System.Text.RegularExpressions.Regex.IsMatch(u, @"\.(webp|avif)(\?.*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        u = System.Text.RegularExpressions.Regex.Replace(u, @"\.(webp|avif)(\?.*)?$", ".jpg$2", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    inside = u;
                }
                catch { }
                var brush = new ImageBrush { Source = null };
                // background-repeat approximation
                try
                {
                    string rep; if (css != null && css.Map != null && css.Map.TryGetValue("background-repeat", out rep) && !string.IsNullOrWhiteSpace(rep))
                    {
                        var rv = rep.Trim().ToLowerInvariant();
                        if (rv.Contains("no-repeat")) brush.Stretch = Stretch.None;
                        else if (rv.Contains("repeat-x")) brush.Stretch = Stretch.Fill; // approximate: fill horizontally
                        else if (rv.Contains("repeat-y")) brush.Stretch = Stretch.Fill; // approximate: fill vertically
                        else if (rv.Contains("repeat")) brush.Stretch = Stretch.Fill; // full repeat approx via fill
                    }
                }
                catch { }
                // background-size
                string bs; if (css != null && css.Map != null && css.Map.TryGetValue("background-size", out bs) && !string.IsNullOrWhiteSpace(bs))
                {
                    var v = bs.Trim().ToLowerInvariant();
                    if (v == "cover") brush.Stretch = Stretch.UniformToFill;
                    else if (v == "contain") brush.Stretch = Stretch.Uniform;
                    else if (v == "auto") brush.Stretch = Stretch.None;
                    else
                    {
                        // best-effort: map common percent cases
                        var partsSz = v.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (partsSz.Length == 2 && partsSz[0].EndsWith("%") && partsSz[1].EndsWith("%"))
                        {
                            // 100% 100% => fill (approximate)
                            if (string.Equals(partsSz[0], "100%", StringComparison.OrdinalIgnoreCase) && string.Equals(partsSz[1], "100%", StringComparison.OrdinalIgnoreCase))
                                brush.Stretch = Stretch.Fill;
                        }
                        else if (partsSz.Length == 2 && ((partsSz[0] == "100%" && partsSz[1] == "auto") || (partsSz[1] == "100%" && partsSz[0] == "auto")))
                        {
                            brush.Stretch = Stretch.Fill;
                        }
                    }
                }
                // background-position keywords (left/center/right, top/center/bottom)
                string bp; if (css != null && css.Map != null && css.Map.TryGetValue("background-position", out bp) && !string.IsNullOrWhiteSpace(bp))
                {
                    var vv = bp.Trim().ToLowerInvariant();
                    // keyword mapping
                    if (vv.Contains("left")) brush.AlignmentX = AlignmentX.Left; else if (vv.Contains("right")) brush.AlignmentX = AlignmentX.Right; else if (vv.Contains("center")) brush.AlignmentX = AlignmentX.Center;
                    if (vv.Contains("top")) brush.AlignmentY = AlignmentY.Top; else if (vv.Contains("bottom")) brush.AlignmentY = AlignmentY.Bottom; else if (vv.Contains("center")) brush.AlignmentY = AlignmentY.Center;

                    // percent mapping (best-effort)
                    var parts = vv.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1 && parts[0].EndsWith("%"))
                    {
                        double px; if (double.TryParse(parts[0].TrimEnd('%'), out px))
                        {
                            if (px <= 25) brush.AlignmentX = AlignmentX.Left; else if (px >= 75) brush.AlignmentX = AlignmentX.Right; else brush.AlignmentX = AlignmentX.Center;
                        }
                    }
                    if (parts.Length >= 2 && parts[1].EndsWith("%"))
                    {
                        double py; if (double.TryParse(parts[1].TrimEnd('%'), out py))
                        {
                            if (py <= 25) brush.AlignmentY = AlignmentY.Top; else if (py >= 75) brush.AlignmentY = AlignmentY.Bottom; else brush.AlignmentY = AlignmentY.Center;
                        }
                    }
                }
                return brush;
            }
            catch { return null; }
        }

        private static void ApplyTransformOrigin(FrameworkElement fe, CssComputed css)
        {
            if (fe == null || css == null || css.Map == null) return;
            string to;
            if (!css.Map.TryGetValue("transform-origin", out to) || string.IsNullOrWhiteSpace(to)) return;
            try
            {
                var s = to.Trim().ToLowerInvariant();
                var parts = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                Func<string, double?> parse = (p) =>
                {
                    if (string.IsNullOrWhiteSpace(p)) return null;
                    p = p.Trim().ToLowerInvariant();
                    if (p == "left" || p == "top") return 0.0;
                    if (p == "center") return 0.5;
                    if (p == "right" || p == "bottom") return 1.0;
                    if (p.EndsWith("%")) { double v; if (double.TryParse(p.TrimEnd('%'), out v)) return Math.Max(0, Math.Min(100, v)) / 100.0; }
                    return null;
                };
                double ox = 0.5, oy = 0.5; // default center
                if (parts.Length == 1)
                {
                    var v = parse(parts[0]); if (v.HasValue) ox = v.Value; // y remains 0.5
                }
                else if (parts.Length >= 2)
                {
                    var vx = parse(parts[0]); var vy = parse(parts[1]);
                    if (vx.HasValue) ox = vx.Value;
                    if (vy.HasValue) oy = vy.Value;
                }
                fe.RenderTransformOrigin = new Avalonia.RelativePoint(ox, oy, Avalonia.RelativeUnit.Relative);
            }
            catch { }
        }

        private static double TryParseFontSize(string raw, double currentPx)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            var s = raw.Trim().ToLowerInvariant();
            double basePx = currentPx > 0 ? currentPx : 16; // default base
            if (s.EndsWith("px")) { double v; if (double.TryParse(s.Substring(0, s.Length - 2), out v)) return v; return 0; }
            if (s.EndsWith("rem")) { double v; if (double.TryParse(s.Substring(0, s.Length - 3), out v)) return v * 16; return 0; }
            if (s.EndsWith("em")) { double v; if (double.TryParse(s.Substring(0, s.Length - 2), out v)) return v * basePx; return 0; }
            if (s.EndsWith("%")) { double p; if (double.TryParse(s.TrimEnd('%'), out p)) return basePx * (p / 100.0); return 0; }
            // keyword sizes (best-effort)
            switch (s)
            {
                case "xx-small": return basePx * 0.6;
                case "x-small": return basePx * 0.75;
                case "small": return basePx * 0.875;
                case "medium": return basePx; // default
                case "large": return basePx * 1.125;
                case "x-large": return basePx * 1.25;
                case "xx-large": return basePx * 1.5;
                case "smaller": return Math.Max(1, basePx * 0.9);
                case "larger": return basePx * 1.1;
            }
            double n; if (double.TryParse(s, out n)) return n; // treat bare number as px
            return 0;
        }
    }
}









