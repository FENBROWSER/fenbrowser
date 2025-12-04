using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using FenBrowser.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using FenBrowser.Core;
using FenBrowser.FenEngine.Scripting;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
namespace FenBrowser.FenEngine.Rendering
{
    public partial class DomBasicRenderer
    {
        private Dictionary<string, Func<LiteElement, Uri, Action<Uri>, JavaScriptEngine, CancellationToken, Task<Control>>> _tagHandlers;
        public DomBasicRenderer()
        {
            InitializeTagHandlers();
        }
        private void InitializeTagHandlers()
        {
            _tagHandlers = new Dictionary<string, Func<LiteElement, Uri, Action<Uri>, JavaScriptEngine, CancellationToken, Task<Control>>>(StringComparer.OrdinalIgnoreCase);
            // Block elements
            Func<LiteElement, Uri, Action<Uri>, JavaScriptEngine, CancellationToken, Task<Control>> blockHandler = new Func<LiteElement, Uri, Action<Uri>, JavaScriptEngine, CancellationToken, Task<Control>>(RenderBlockAsync);
            // Map common block level tags to the same handler
            string[] blockTags = new[] { "div", "p", "section", "article", "header", "footer", "nav", "main", "aside", "address", "h1", "h2", "h3", "h4", "h5", "h6", "pre", "figure", "figcaption" };
            foreach (var bt in blockTags) _tagHandlers[bt] = blockHandler;
            // Tables
            // _tagHandlers["table"] = RenderTableAsync;
            // Lists
            _tagHandlers["ul"] = (n, b, on, j, c) => MakeListAsync(n, false, b, on, j, c);
            _tagHandlers["ol"] = (n, b, on, j, c) => MakeListAsync(n, true, b, on, j, c);
            // Images
            _tagHandlers["img"] = async (n, b, on, j, c) => await MakeImageAsync(n, b, c);
            _tagHandlers["picture"] = async (n, b, on, j, c) => await MakePictureAsync(n, b, c);
            // Forms
            _tagHandlers["textarea"] = (n, b, on, j, c) => Task.FromResult(MakeTextarea(n));
            _tagHandlers["select"] = (n, b, on, j, c) => Task.FromResult(MakeSelect(n));
            _tagHandlers["button"] = MakeButtonAsync;
            _tagHandlers["button"] = MakeButtonAsync;
            _tagHandlers["a"] = MakeLink;
            _tagHandlers["svg"] = (n, b, on, j, c) => Task.FromResult(RenderInlineSvg(n));

            // Non-visual tags - suppress rendering
            Func<LiteElement, Uri, Action<Uri>, JavaScriptEngine, CancellationToken, Task<Control>> nullHandler = (n, b, on, j, c) => Task.FromResult<Control>(null);
            string[] hiddenTags = new[] { "head", "meta", "link", "style", "script", "title", "noscript", "iframe", "template", "base" };
            foreach (var t in hiddenTags) _tagHandlers[t] = nullHandler;
        }
        private static readonly HashSet<string> FlexDisplayKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "flex", "inline-flex",
            "inline-flex",
            "flexbox",
            "inline-flexbox",
            "-ms-flexbox",
            "-ms-inline-flexbox",
            "-webkit-flex",
            "-webkit-inline-flex"
        };
        private IBrush LinkBrush = new SolidColorBrush(Colors.Blue);
        private static string GetDisplayValue(CssComputed css)
        {
            if (css == null) return null;
            var display = css.Display;
            if (string.IsNullOrWhiteSpace(display) && css.Map != null)
            {
                string raw;
                if (css.Map.TryGetValue("display", out raw))
                {
                    display = raw;
                }
            }
            return display;
        }
        private static string NormalizeDisplayValue(string display)
        {
            return string.IsNullOrWhiteSpace(display) ? null : display.Trim().ToLowerInvariant();
        }
        private static bool IsFlexContainer(CssComputed css)
        {
            var normalized = NormalizeDisplayValue(GetDisplayValue(css));
            if (string.IsNullOrEmpty(normalized)) return false;
            if (FlexDisplayKeywords.Contains(normalized)) return true;
            // Legacy keywords occasionally appear without being in the allow-list (e.g., vendor shorthands)
            return normalized.EndsWith("flex", StringComparison.Ordinal) || normalized.EndsWith("flexbox", StringComparison.Ordinal);
        }
        private static bool IsInlineFlex(CssComputed css)
        {
            var normalized = NormalizeDisplayValue(GetDisplayValue(css));
            if (string.IsNullOrEmpty(normalized)) return false;
            return normalized == "inline-flex" || normalized == "inline-flexbox" || normalized == "-ms-inline-flexbox" || normalized == "-webkit-inline-flex";
        }
        private static bool IsGridContainer(CssComputed css)
        {
            var normalized = NormalizeDisplayValue(GetDisplayValue(css));
            return normalized == "grid" || normalized == "inline-grid";
        }
        // Some event args types on WP8.1 don't expose Handled. Use reflection to set when available.
        private static void TrySetHandled(object e)
        {
            try
            {
                if (e == null) return;
                var t = e.GetType();
                System.Reflection.PropertyInfo p = null;
                try { p = t.GetProperty("Handled"); } catch { }
                if (p == null)
                {
                    try { var ti = t; if (ti != null) p = ti.GetProperty("Handled"); } catch { }
                }
                if (p != null && p.CanWrite && p.PropertyType == typeof(bool))
                {
                    try { p.SetValue(e, true); } catch { }
                }
            }
            catch { }
        }
        // ---------- Engine integration ----------
        public event Action<string> StatusMessage;
        public event Action<Uri> LinkLongPressed;
        public event EventHandler<FormSubmitEventArgs> FormSubmit;
        // These are still optional and respected by the renderer
        public Dictionary<LiteElement, CssComputed> ComputedStyles { get; set; }
        public JavaScriptEngine Js { get; set; }
        // Optional loader to fetch HTML for iframes or embedded documents
        public Func<Uri, Task<string>> HtmlLoader { get; set; }
        public Action<Uri, string> OnPost { get; set; }
        public Func<Uri, Task<Stream>> ImageLoader { get; set; }
        private static readonly string[] HiddenTags = { "script", "style", "head", "title", "meta", "link" };
        private Uri _baseUriForResources; // set during BuildAsync
        private Uri _documentUri;
        private readonly System.Collections.Generic.List<System.Tuple<Control, CssComputed>> _pendingFixed = new System.Collections.Generic.List<System.Tuple<Control, CssComputed>>();
        private Canvas _fixedBehind;
        private Canvas _fixedFront;
        private class SvgRenderState
        {
            public IBrush Fill { get; set; }
            public IBrush Stroke { get; set; }
            public double StrokeWidth { get; set; }
            public double Opacity { get; set; } = 1.0;

        public SvgRenderState Clone()
        {
            return new SvgRenderState
            {
                Fill = this.Fill,
                Stroke = this.Stroke,
                StrokeWidth = this.StrokeWidth,
                Opacity = this.Opacity
            };
        }
        }
        private Control RenderInlineSvg(LiteElement svg)
{
    if (svg == null) return null;
    var state = new SvgRenderState
    {
        Fill = new SolidColorBrush(Colors.Black),
        StrokeWidth = 1.0
    };
    ApplySvgStateOverrides(svg, state);
    double width = ParseSvgLength(DictGet(svg.Attr, "width"));
    double height = ParseSvgLength(DictGet(svg.Attr, "height"));
    double vbX = 0, vbY = 0, vbW = 0, vbH = 0;
    bool hasViewBox = false;
    if (TryGetAttr(svg, "viewBox", out var viewBox) || TryGetAttr(svg, "viewbox", out viewBox))
    {
        var parts = (viewBox ?? string.Empty).Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 4)
        {
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out vbX) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out vbY) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out vbW) &&
                double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out vbH))
            {
                hasViewBox = vbW > 0 && vbH > 0;
            }
        }
    }
    var canvas = new Canvas { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    if (hasViewBox)
    {
        canvas.Width = vbW;
        canvas.Height = vbH;
        if (Math.Abs(vbX) > double.Epsilon || Math.Abs(vbY) > double.Epsilon)
        {
            canvas.RenderTransform = new TranslateTransform { X = -vbX, Y = -vbY };
        }
    }
    else
    {
        if (!double.IsNaN(width) && width > 0) canvas.Width = width;
        if (!double.IsNaN(height) && height > 0) canvas.Height = height;
    }
    if (svg.Children != null)
        foreach (var child in svg.Children)
        {
            AppendSvgElement(canvas, child, state);
        }
    Control result = canvas;
    if (hasViewBox || !double.IsNaN(width) || !double.IsNaN(height))
    {
        var vb = new Viewbox { Stretch = Stretch.Uniform, Child = canvas };
        
        // If explicit width exists, use it.
        if (!double.IsNaN(width) && width > 0) vb.Width = width;
        // If explicit width is MISSING but we have a viewBox, use the viewBox width as the default intrinsic size.
        // This prevents icons from exploding to fill the screen width.
        else if (hasViewBox && vbW > 0) vb.Width = vbW;

        if (!double.IsNaN(height) && height > 0) vb.Height = height;
        else if (hasViewBox && vbH > 0) vb.Height = vbH;

        result = vb;
    }
    return result;
}
private void AppendSvgElement(Canvas canvas, LiteElement node, SvgRenderState inherited)
{
    if (canvas == null || node == null) return;
    if (node.IsText) return;
    var state = inherited?.Clone() ?? new SvgRenderState();
    ApplySvgStateOverrides(node, state);
    if (string.Equals(node.Tag, "g", StringComparison.OrdinalIgnoreCase))
    {
        if (node.Children != null)
            foreach (var child in node.Children)
            {
                AppendSvgElement(canvas, child, state);
            }
        return;
    }
    if (string.Equals(node.Tag, "path", StringComparison.OrdinalIgnoreCase))
    {
        if (!TryGetAttr(node, "d", out var data)) return;
        try
        {
            TryGetAttr(node, "fill-rule", out var fillRuleAttr);
            var geom = CreateSvgPathGeometry(data, fillRuleAttr);
            if (geom == null) return;
            var path = new Avalonia.Controls.Shapes.Path
            {
                Data = geom,
                Opacity = Clamp(state.Opacity, 0, 1),
                Stretch = Stretch.None
            };
            path.Fill = state.Fill;
            path.Stroke = state.Stroke;
            if (path.Stroke != null)
            {
                path.StrokeThickness = state.StrokeWidth > 0 ? state.StrokeWidth : 1;
            }
            canvas.Children.Add(path);
        }
        catch { }
        return;
    }
    if (node.Children != null)
        foreach (var child in node.Children)
        {
            AppendSvgElement(canvas, child, state);
        }
}
private static void ApplySvgStateOverrides(LiteElement node, SvgRenderState state)
{
    if (node == null || state == null) return;
    if (TryGetAttr(node, "fill", out var fill))
    {
        var brush = CreateSvgBrush(fill, allowNone: true);
        if (brush != null || string.Equals((fill ?? string.Empty).Trim(), "none", StringComparison.OrdinalIgnoreCase))
            state.Fill = brush;
    }
    if (TryGetAttr(node, "stroke", out var stroke))
    {
        var brush = CreateSvgBrush(stroke, allowNone: true);
        if (brush != null || string.Equals((stroke ?? string.Empty).Trim(), "none", StringComparison.OrdinalIgnoreCase))
            state.Stroke = brush;
    }
    if (TryGetAttr(node, "stroke-width", out var strokeWidth))
    {
        var w = ParseSvgLength(strokeWidth);
        if (!double.IsNaN(w) && w >= 0) state.StrokeWidth = w;
    }
    if (TryGetAttr(node, "opacity", out var opacity))
    {
        double o;
        if (double.TryParse(opacity, NumberStyles.Float, CultureInfo.InvariantCulture, out o)) state.Opacity = Clamp(o, 0, 1);
    }
    if (TryGetAttr(node, "fill-opacity", out var fillOpacity))
    {
        double fo;
        if (double.TryParse(fillOpacity, NumberStyles.Float, CultureInfo.InvariantCulture, out fo))
        {
            var brush = state.Fill as SolidColorBrush;
            if (brush != null)
            {
                var col = brush.Color; // col.A = (byte)(Clamp(fo, 0, 1) * 255); // Read-only in Avalonia
                state.Fill = new SolidColorBrush(col);
            }
        }
    }
    if (TryGetAttr(node, "stroke-opacity", out var strokeOpacity))
    {
        double so;
        if (double.TryParse(strokeOpacity, NumberStyles.Float, CultureInfo.InvariantCulture, out so))
        {
            var brush = state.Stroke as SolidColorBrush;
            if (brush != null)
            {
                var col = brush.Color; // col.A = (byte)(Clamp(so, 0, 1) * 255); // Read-only in Avalonia
                state.Stroke = new SolidColorBrush(col);
            }
        }
    }
}
private static void RegisterDomVisualSafe(LiteElement node, Control fe)
{
    try
    {
        var t = typeof(JavaScriptEngine);
        var m = t.GetMethod("RegisterDomVisual", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
        if (m != null) m.Invoke(null, new object[] { node, fe });
    }
    catch { }
}
private static double ParseSvgLength(string raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return double.NaN;
    var s = raw.Trim();
    if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 2);
    double val;
    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out val) ? val : double.NaN;
}
private static IBrush CreateSvgBrush(string value, bool allowNone)
{
    if (string.IsNullOrWhiteSpace(value)) return allowNone ? null : new SolidColorBrush(Colors.Black);
    var s = value.Trim();
    if (allowNone && string.Equals(s, "none", StringComparison.OrdinalIgnoreCase)) return null;
    var parsed = CssParser.ParseColor(s);
    if (parsed.HasValue) return new SolidColorBrush(parsed.Value);
    return allowNone ? null : new SolidColorBrush(Colors.Black);
}
private static double Clamp(double v, double min, double max)
{
    if (v < min) return min;
    if (v > max) return max;
    return v;
}
private static Geometry CreateSvgPathGeometry(string data, string fillRule)
{
    if (string.IsNullOrWhiteSpace(data)) return null;
    try
    {
        var escaped = data.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("'", "&apos;").Replace("<", "&lt;").Replace(">", "&gt;");
        var xaml = "<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data=\"" + escaped + "\" />";
        // var obj = Avalonia.Markup.Xaml.AvaloniaRuntimeXamlLoader.Load(xaml) as Avalonia.Controls.Shapes.Path;
        // if (obj == null) return null;
        // var geom = obj?.Data;
        // var pg = geom as PathGeometry;
        // if (pg != null)
        {
            // pg.FillRule = // !string.IsNullOrWhiteSpace(fillRule) && string.Equals(fillRule.Trim(), "evenodd", StringComparison.OrdinalIgnoreCase)
                // ? FillRule.EvenOdd // : FillRule.NonZero;
        }
        return null;
    }
    catch { return null; }
}
// ---------- Flexbox (legacy subset) ----------
private async Task<Control> MakeGridFallbackAsync(LiteElement n, Uri baseUri, Action<Uri> onNavigate, JavaScriptEngine js, CancellationToken ct)
{
    var css = TryGetCss(n);
    double columnGap = 0, rowGap = 0;
    if (css != null)
    {
        if (css.ColumnGap.HasValue) columnGap = css.ColumnGap.Value;
        if (css.RowGap.HasValue) rowGap = css.RowGap.Value;
        if (css.Gap.HasValue)
        {
            if (columnGap <= 0) columnGap = css.Gap.Value;
            if (rowGap <= 0) rowGap = css.Gap.Value;
        }
    }
    if (columnGap <= 0 && rowGap > 0) columnGap = rowGap;
    if (rowGap <= 0 && columnGap > 0) rowGap = columnGap;
    bool isGrid = IsGridContainer(css);
    var wrapPanel = new FlexPanel
    {
        Orientation = isGrid ? Orientation.Vertical : Orientation.Horizontal,
        ColumnGap = Math.Max(0, columnGap),
        RowGap = Math.Max(0, rowGap),
        FlexWrap = isGrid ? "wrap" : "nowrap" // Default to wrap for grid to prevent explosion
    };
    
    // Handle flex-direction
    if (css != null && css.Map != null)
    {
        if (css.Map.TryGetValue("flex-direction", out var fdir) && !string.IsNullOrWhiteSpace(fdir))
        {
            fdir = fdir.Trim().ToLowerInvariant();
            if (fdir.Contains("column")) wrapPanel.Orientation = Orientation.Vertical;
            else if (fdir.Contains("row")) wrapPanel.Orientation = Orientation.Horizontal;
        }

        if (css.Map.TryGetValue("flex-wrap", out var fwrap) && !string.IsNullOrWhiteSpace(fwrap))
        {
            wrapPanel.FlexWrap = fwrap.Trim().ToLowerInvariant();
        }
    }

    wrapPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
    if (css != null)
    {
        var disp = NormalizeDisplayValue(GetDisplayValue(css));
        if (disp != null && (disp.Contains("inline") || disp == "inline-flex" || disp == "inline-grid"))
        {
            wrapPanel.HorizontalAlignment = HorizontalAlignment.Left;
            wrapPanel.VerticalAlignment = VerticalAlignment.Top;
        }
    }
    try
    {
        string justify;
        if (css != null && css.Map != null && css.Map.TryGetValue("justify-content", out justify) && !string.IsNullOrWhiteSpace(justify))
            wrapPanel.JustifyContent = justify.Trim().ToLowerInvariant();
        string alignContent;
        if (css != null && css.Map != null && css.Map.TryGetValue("align-content", out alignContent) && !string.IsNullOrWhiteSpace(alignContent))
        {
            wrapPanel.AlignContent = alignContent.Trim().ToLowerInvariant();
            var ac = wrapPanel.AlignContent;
            if (ac.Contains("center")) wrapPanel.VerticalAlignment = VerticalAlignment.Center;
            else if (ac.Contains("flex-end") || ac.Contains("end")) wrapPanel.VerticalAlignment = VerticalAlignment.Bottom;
            else if (ac.Contains("flex-start") || ac.Contains("start")) wrapPanel.VerticalAlignment = VerticalAlignment.Top;
            else wrapPanel.VerticalAlignment = VerticalAlignment.Stretch;
        }
    }
    catch { }
    var absoluteItems = new List<Tuple<Control, CssComputed>>();
    var children = n.Children ?? new List<LiteElement>();
    double minColumnWidth = ExtractMinColumnWidth(css);
    if (double.IsNaN(minColumnWidth) || minColumnWidth < 0) minColumnWidth = 0;
    foreach (var child in children)
    {
        ct.ThrowIfCancellationRequested();
        var cssChild = TryGetCss(child);
        bool isAbs = cssChild != null && string.Equals(cssChild.Position, "absolute", StringComparison.OrdinalIgnoreCase);
        bool isFixed = cssChild != null && string.Equals(cssChild.Position, "fixed", StringComparison.OrdinalIgnoreCase);
        bool isSticky = cssChild != null && string.Equals(cssChild.Position, "sticky", StringComparison.OrdinalIgnoreCase);
        if (isAbs)
        {
            var absElt = await RenderNodeAsync(child, baseUri, onNavigate, js, ct);
            if (absElt != null) absoluteItems.Add(Tuple.Create(absElt, cssChild));
            continue;
        }
        if (isFixed)
        {
            var fixedElt = await RenderNodeAsync(child, baseUri, onNavigate, js, ct);
            if (fixedElt != null) _pendingFixed.Add(System.Tuple.Create(fixedElt, cssChild));
            continue;
        }
        if (isSticky)
        {
            var stickyElt = await RenderNodeAsync(child, baseUri, onNavigate, js, ct);
            if (stickyElt != null)
            {
                // try { AttachStickyBehavior(stickyElt, cssChild); } catch { } // TODO: Convert CssComputed to double
            }
            continue;
        }
        var element = await RenderNodeAsync(child, baseUri, onNavigate, js, ct);
        if (element == null) continue;
        ApplyGridItemSizing(element, cssChild, minColumnWidth, wrapPanel.ColumnGap);
        wrapPanel.Children.Add(element);
    }
    Control container = wrapPanel;
    if (absoluteItems.Count > 0)
    {
        var grid = new Grid();
        grid.Children.Add(wrapPanel);
        foreach (var tuple in absoluteItems)
        {
            var fe = tuple.Item1;
            grid.Children.Add(fe);
            try { ApplyRelativeOffset(fe, tuple.Item2, wrapPanel); } catch { }
        }
        container = grid;
    }
    return container;
}
private static void ApplyGridItemSizing(Control element, CssComputed css, double minColumnWidth, double columnGap)
{
    if (element == null) return;
    if (minColumnWidth > 0 && element.MinWidth < minColumnWidth)
    {
        element.MinWidth = minColumnWidth;
    }
    if (css == null || css.Map == null) return;
    try
    {
        string gridColumn;
        if (css.Map.TryGetValue("grid-column", out gridColumn) && !string.IsNullOrWhiteSpace(gridColumn) && minColumnWidth > 0)
        {
            var match = Regex.Match(gridColumn, @"span\s+(\d+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                int span;
                if (int.TryParse(match.Groups[1].Value, out span) && span > 1)
                {
                    double width = span * minColumnWidth;
                    if (columnGap > 0) width += (span - 1) * columnGap;
                    if (width > element.MinWidth) element.MinWidth = width;
                }
            }
        }
    }
    catch { }
}
private static double ExtractMinColumnWidth(CssComputed css)
{
    if (css == null || css.Map == null) return double.NaN;
    try
    {
        string template;
        if (css.Map.TryGetValue("grid-template-columns", out template))
        {
            double fromTemplate = ParsePreferredTrackSize(template);
            if (!double.IsNaN(fromTemplate)) return fromTemplate;
        }
        string auto;
        if (css.Map.TryGetValue("grid-auto-columns", out auto))
        {
            double px;
            if (TryPx(auto, out px)) return px;
        }
    }
    catch { }
    return double.NaN;
}
private static double ParsePreferredTrackSize(string template)
{
    if (string.IsNullOrWhiteSpace(template)) return double.NaN;
    try
    {
        var minmax = Regex.Match(template, @"minmax\(\s*([^,]+),", RegexOptions.IgnoreCase);
        if (minmax.Success)
        {
            double px;
            if (TryPx(minmax.Groups[1].Value.Trim(), out px)) return px;
        }
        var repeat = Regex.Match(template, @"repeat\(\s*[^,]+,\s*([^\)]+)\)", RegexOptions.IgnoreCase);
        if (repeat.Success)
        {
            var inner = repeat.Groups[1].Value;
            double px;
            if (TryPx(inner.Trim(), out px)) return px;
            var innerMin = Regex.Match(inner, @"minmax\(\s*([^,]+),", RegexOptions.IgnoreCase);
            if (innerMin.Success && TryPx(innerMin.Groups[1].Value.Trim(), out px)) return px;
        }
        var simple = Regex.Match(template, @"(?<num>[-+]?[0-9]*\.?[0-9]+(?:px|rem|em)?)", RegexOptions.IgnoreCase);
        if (simple.Success)
        {
            double px;
            if (TryPx(simple.Groups["num"].Value, out px)) return px;
        }
    }
    catch { }
    return double.NaN;
}
// ---------- Image helpers (no WebView) ----------
private static readonly string[] _supportedImgMime =
{
            "image/png","image/jpeg","image/jpg","image/gif","image/svg+xml"
        };
private static bool IsSupportedImageType(string typeOrUrl)
{
    if (string.IsNullOrWhiteSpace(typeOrUrl)) return false;
    // MIME?
    if (typeOrUrl.IndexOf("/", StringComparison.Ordinal) > 0)
        return _supportedImgMime.Any(m => typeOrUrl.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0);
    // extension?
    var u = typeOrUrl.ToLowerInvariant();
    return u.EndsWith(".png") || u.EndsWith(".jpg") || u.EndsWith(".jpeg") ||
           u.EndsWith(".gif") || u.EndsWith(".svg");
}
private static string RewriteImageUrlIfNeeded(string url)
{
    if (string.IsNullOrWhiteSpace(url)) return url;
    // twitter/x pattern: ...?format=webp -> ?format=jpg
    if (url.IndexOf("format=webp", StringComparison.OrdinalIgnoreCase) >= 0)
        url = Regex.Replace(url, @"format=webp", "format=jpg", RegexOptions.IgnoreCase);
    // also skip "f=webp" style flags
    url = Regex.Replace(url, @"(\?|&)(f|fmt)=webp", "$1$2=jpg", RegexOptions.IgnoreCase);
    // common extension swap: .webp/.avif -> .png (safer alpha support)
    if (Regex.IsMatch(url, @"\.(webp|avif)(\?.*)?$", RegexOptions.IgnoreCase))
        url = Regex.Replace(url, @"\.(webp|avif)(\?.*)?$", ".png$2", RegexOptions.IgnoreCase);
    return url;
}
// support both width (NNw) and density (Nx) descriptors
private static string PickSrcFromSrcset(string srcset, double deviceWidth, double deviceScale = 1.0)
{
    if (string.IsNullOrWhiteSpace(srcset)) return null;
    var widthCands = new List<Tuple<string, int>>();
    var dppxCands = new List<Tuple<string, double>>();
    foreach (var part in srcset.Split(','))
    {
        var p = part.Trim();
        if (p.Length == 0) continue;
        var sp = p.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (sp.Length == 0) continue;
        var url = sp[0];
        if (sp.Length >= 2)
        {
            var d = sp[1].Trim().ToLowerInvariant();
            int w;
            double x;
            if (d.EndsWith("w") && int.TryParse(d.TrimEnd('w'), out w))
                widthCands.Add(Tuple.Create(url, w));
            else if (d.EndsWith("x") && double.TryParse(d.TrimEnd('x'), out x))
                dppxCands.Add(Tuple.Create(url, x));
            else
                widthCands.Add(Tuple.Create(url, 0));
        }
        else
        {
            widthCands.Add(Tuple.Create(url, 0));
        }
    }
    if (dppxCands.Count > 0)
    {
        var bests = dppxCands
            .OrderBy(c => Math.Abs(c.Item2 - Math.Max(1.0, deviceScale)))
            .First().Item1;
        return bests;
    }
    if (widthCands.Count == 0) return null;
    int dw = (int)Math.Max(320, deviceWidth <= 0 ? 360 : deviceWidth);
    var sorted = widthCands.OrderBy(c => c.Item2 == 0 ? int.MaxValue : c.Item2).ToList();
    // Prefer the largest candidate up to ~1.5x device width to balance quality
    var limit = (int)Math.Round(dw * Math.Max(1.0, deviceScale) * 1.5);
    Tuple<string, int> best = null;
    foreach (var c in sorted)
    {
        if (c.Item2 == 0) { best = c; continue; }
        if (c.Item2 <= limit) best = c; else break;
    }
    var chosen = (best ?? sorted.Last()).Item1;
    return chosen;
}
private Task<Control> MakeImageAsync(LiteElement n, Uri baseUri)
    => MakeImageAsync(n, baseUri, System.Threading.CancellationToken.None);
private async Task<Control> MakePictureAsync(LiteElement n, Uri baseUri, CancellationToken ct)
{
    if (n == null || n.Children == null) return null;
    var img = n.Children.FirstOrDefault(c => string.Equals(c.Tag, "img", StringComparison.OrdinalIgnoreCase));
    if (img != null)
    {
        return await MakeImageAsync(img, baseUri, ct);
    }
    return null;
}
private async Task<Control> MakeImageAsync(LiteElement n, Uri baseUri, CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();
    if (n == null) return null;
    try { System.IO.File.AppendAllText("debug_log.txt", $"[MakeImageAsyncEntry] {n.Tag}\r\n"); } catch { }
    var candidates = ResolveImageUriCandidates(n, baseUri);
    if (candidates == null || candidates.Count == 0)
    {
        var altOnly = DictGet(n.Attr, "alt");
        if (!string.IsNullOrWhiteSpace(altOnly))
        {
            var tb = RenderPlainTextBlock(altOnly);
            if (tb != null) return tb;
        }
        return null;
    }
    var img = new Image { Stretch = Stretch.Uniform };
    var alt = DictGet(n.Attr, "alt");
    int reqW = GetIntAttr(n, "width", 0);
    int current = 0;
    bool switching = false;
    Action<string, Uri> log = (msg, u) =>
    {
        try { System.IO.File.AppendAllText("debug_log.txt", $"[MakeImageAsync] {msg} {u}\r\n"); } catch { }
#if DEBUG
        System.Diagnostics.Debug.WriteLine($"{msg} {u}");
#endif
    };
    Func<int, Task<bool>> applyCandidateAsync = null;
    applyCandidateAsync = async (index) =>
    {
        if (index >= candidates.Count) return false;
        current = index;
        var uri = candidates[index];
        if (uri == null) return await applyCandidateAsync(index + 1);
        if (LooksSvg(uri.ToString()) && SvgType == null)
        {
            log("[ImgSkipSvg]", uri);
            return await applyCandidateAsync(index + 1);
        }
        try
        {
            log("[ImgTry]", uri);
            var source = await LoadIImageAsync(uri.AbsoluteUri.ToString(), reqW);
            if (source == null)
            {
                log("[ImgTryNull]", uri);
                return await applyCandidateAsync(index + 1);
            }
            try { img.Source = source; } catch { }
            return true;
        }
        catch (Exception ex)
        {
            log("[ImgTryError] " + ex.Message, uri);
            return await applyCandidateAsync(index + 1);
        }
    };
    if (!await applyCandidateAsync(0))
    {
        if (!string.IsNullOrWhiteSpace(alt))
        {
            var tbAlt = RenderPlainTextBlock(alt);
            if (tbAlt != null) return tbAlt;
        }
        return null;
    }
    if (n.Attr != null)
    {
        string w;
        if (n.Attr.TryGetValue("width", out w))
        {
            double wd;
            if (double.TryParse(w, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out wd))
                img.Width = wd;
        }
        string h;
        if (n.Attr.TryGetValue("height", out h))
        {
            double hd;
            if (double.TryParse(h, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out hd))
                img.Height = hd;
        }
    }
    ApplyComputedStyles(img, n);
    ApplyInlineStyles(img, n);
    return img;
}

private async Task<Control> MakeVideoAsync(LiteElement n, Uri baseUri, CancellationToken ct)
{
    Uri src = null;
    string poster = null;
    bool controls = true;
    try
    {
        if (n.Attr != null)
        {
            string s; if (n.Attr.TryGetValue("src", out s)) src = ResolveUri(baseUri, s);
            string c; controls = !(n.Attr.TryGetValue("controls", out c) && c == null);
            string p; if (n.Attr.TryGetValue("poster", out p)) poster = p;
        }
    }
    catch { }

    if (src == null)
    {
        try
        {
            if (n.Children != null)
                foreach (var ch in n.Children)
                {
                    if (string.Equals(ch.Tag, "source", StringComparison.OrdinalIgnoreCase) && ch.Attr != null)
                    {
                        string t = null; ch.Attr.TryGetValue("type", out t);
                        string u = null; ch.Attr.TryGetValue("src", out u);
                        if (string.IsNullOrWhiteSpace(u)) continue;
                        if (string.IsNullOrWhiteSpace(t) || t.IndexOf("mp4", StringComparison.OrdinalIgnoreCase) >= 0)
                        { src = ResolveUri(baseUri, u); break; }
                    }
                }
        }
        catch { }
    }
    var host = new VideoHost();
    host.SetControls(controls);
    if (src != null) host.SetSource(src);
    var wrapped = RendererStyles.WrapWithBoxes(host, TryGetCss(n));
    return wrapped ?? (Control)host;
}
private async Task<Control> RenderPictureAsync(LiteElement picture, Uri baseUri, CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();
    if (picture == null) return null;
    // Try <source> elements: prefer supported types; pick from srcset with device width
    foreach (var s in picture.Children.Where(c => c.Tag == "source"))
    {
        if (s.Attr == null) continue;
        string type = null; s.Attr.TryGetValue("type", out type);
        string srcset = null; s.Attr.TryGetValue("srcset", out srcset);
        if (!string.IsNullOrWhiteSpace(type) && !IsSupportedImageType(type))
            continue;
        var candidate = PickSrcFromSrcset(srcset, 360.0);
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            candidate = RewriteImageUrlIfNeeded(candidate);
            var abs = ResolveUri(baseUri, candidate);
            if (abs != null)
            {
                var img = new Image { Stretch = Stretch.Uniform };
                var src = await LoadIImageAsync(abs.AbsoluteUri.ToString());
                if (src != null) { img.Source = src; return ApplyBoxesAndText(img, picture); }
            }
        }
    }
    // Fallback to inner <img>
    var innerImg = picture.Children.FirstOrDefault(c => c.Tag == "img");
    if (innerImg != null)
        return await MakeImageAsync(innerImg, baseUri, ct);
    // Nothing usable ? keep layout stable
    return ApplyBoxesAndText(new Border { Height = 1, Opacity = 0 }, picture);
}
// ---------- Entry point ----------
// Back-compat: existing signature
public Task<Control> BuildAsync(LiteElement root, Uri baseUri, Action<Uri> onNavigate, JavaScriptEngine js = null)
    => BuildAsync(root, baseUri, onNavigate, js, CancellationToken.None);
public async Task<Control> BuildAsync(LiteElement root, Uri baseUri, Action<Uri> onNavigate, JavaScriptEngine js, CancellationToken ct)
{
    // UI-thread guard: if we're off the UI thread, marshal and re-enter BuildAsync there.
    try
    {
        var disp = UiThreadHelper.TryGetDispatcher();
        if (disp != null && !UiThreadHelper.HasThreadAccess(disp))
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<Control>();
            // var tcs = new System.Threading.Tasks.TaskCompletionSource<Control>();
            // await UiThreadHelper.RunAsyncAwaitable(disp, Windows priority, async () =>
                                // try { var fe = await BuildAsync(root, baseUri, onNavigate, js, ct); tcs.TrySetResult(fe); }
                // catch (Exception ex) { tcs.TrySetException(ex); }
            // });
            // return await tcs.Task;
            // });
            // return await tcs.Task;
        } // end if
    }
    catch { }
    ct.ThrowIfCancellationRequested();
    _renderedOneSearchForm = false;
    _baseUriForResources = baseUri;
    // expose computed styles statically for helper lookups (list-style-type bullets)
    try { _computedStylesStatic = this.ComputedStyles; } catch { }
    // Honor <base href>
    var baseTag = root.Descendants().FirstOrDefault(n => n.Tag == "base");
    if (baseTag != null && baseTag.Attr != null)
    {
        string b;
        if (baseTag.Attr.TryGetValue("href", out b))
        {
            Uri bu;
            if (Uri.TryCreate(b, UriKind.Absolute, out bu)) baseUri = bu;
            else if (baseUri != null && Uri.TryCreate(baseUri, b, out bu)) baseUri = bu;
        }
    }
    // <meta http-equiv="refresh">
    var refresh = root.Descendants().FirstOrDefault(n =>
        n.Tag == "meta" && n.Attr != null &&
        n.Attr.ContainsKey("http-equiv") &&
        string.Equals(n.Attr["http-equiv"], "refresh", StringComparison.OrdinalIgnoreCase) &&
        n.Attr.ContainsKey("content"));
    if (refresh != null && onNavigate != null)
    {
        var content = refresh.Attr["content"] ?? "";
        // Common forms: "0; URL=/path" or "0;url('/path')"
        string extracted = null;
        try
        {
            var m = Regex.Match(content, "url\\s*=\\s*([^;]+)", RegexOptions.IgnoreCase);
            if (m.Success) extracted = m.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(extracted))
            {
                m = Regex.Match(content, "url\\s*\\(\\s*['\\\"]?([^'\\\")]+)['\\\"]?\\s*\\)", RegexOptions.IgnoreCase);
                if (m.Success) extracted = m.Groups[1].Value;
            }
        }
        catch { }
        var part = (extracted ?? string.Empty).Trim().Trim('\'', '\"', ' ', ';');
        if (!string.IsNullOrWhiteSpace(part))
        {
            var target = ResolveUri(baseUri, part);
            if (target != null)
            {
                try
                {
                    onNavigate(target);
                }
                catch { onNavigate(target); }
                return new StackPanel { Margin = new Thickness(8, 8, 8, 8) };
            }
        }
    }
    var body = root.Descendants().FirstOrDefault(n => n.Tag == "body") ?? root;
    var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8, 8, 8, 24) };
    try { System.Diagnostics.Debug.WriteLine("[BuildAsync] start nodes=" + (body.Children != null ? body.Children.Count : 0)); } catch { }
    if (body.Children != null)
    {
        foreach (var child in body.Children)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var elt = await RenderNodeAsync(child, baseUri, onNavigate, js, ct);
                if (elt != null) panel.Children.Add(elt);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("RenderNodeAsync failed: " + ex); }
        }
    }
    // Apply computed + inline styles to body container
    try
    {
        ApplyComputedStyles(panel, body);
        ApplyInlineStyles(panel, body);
    }
    catch { }
    // Wrap body/html background if present
    Control rootVisual = panel;
    try
    {
        IBrush bg = null;
        if (ComputedStyles != null)
        {
            CssComputed cssBody;
            if (body != null && ComputedStyles.TryGetValue(body, out cssBody) &&
                cssBody != null && cssBody.Background != null)
            {
                bg = cssBody.Background;
            }
            else
            {
                var bodyKey = ComputedStyles.Keys.FirstOrDefault(k => string.Equals(k.Tag, "body", StringComparison.OrdinalIgnoreCase));
                if (bodyKey != null && ComputedStyles.TryGetValue(bodyKey, out cssBody) &&
                    cssBody != null && cssBody.Background != null)
                    bg = cssBody.Background;
                else
                {
                    var htmlKey = ComputedStyles.Keys.FirstOrDefault(k => string.Equals(k.Tag, "html", StringComparison.OrdinalIgnoreCase));
                    CssComputed cssHtml;
                    if (htmlKey != null && ComputedStyles.TryGetValue(htmlKey, out cssHtml) &&
                        cssHtml != null && cssHtml.Background != null)
                        bg = cssHtml.Background;
                }
            }
        }
        if (bg == null)
        {
            var panelBg = (panel as Panel)?.Background;
            if (panelBg != null) bg = panelBg;
        }
        if (bg != null)
        {
            var wrapper = new Border
            {
                Background = bg,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Padding = panel.Margin
            };
            panel.Margin = new Thickness(0, 0, 0, 0);
            wrapper.Child = panel;
            rootVisual = wrapper;
        }
    }
    catch { }
    // Synthetic search bar only if there's no visible form and the host is a search engine
    bool hasUsableForm = body.Descendants().Any(d => d.Tag == "form" && !IsHidden(d));
    if (!hasUsableForm && IsSearchHost(baseUri))
    {
        var synthetic = MakeSyntheticSearch(baseUri, onNavigate);
        if (synthetic != null) panel.Children.Add(synthetic);
    }
    // If nothing rendered, show OpenGraph preview (JS-only pages)
    if (panel.Children.Count == 0)
    {
        var og = CreateOpenGraphPreview(root, baseUri, onNavigate);
        if (og != null) panel.Children.Add(og);
    }
    // Wrap with a grid that hosts fixed-position overlays (behind and front)
    try
    {
        var grid = new Grid();
        _fixedBehind = new Canvas { IsHitTestVisible = false, Background = null };
        _fixedFront = new Canvas { IsHitTestVisible = false, Background = null };
        grid.Children.Add(_fixedBehind);
        grid.Children.Add(rootVisual);
        grid.Children.Add(_fixedFront);
        // add pending fixed items to appropriate overlay based on z-index
        foreach (var kv in _pendingFixed)
        {
            try
            {
                var fe = kv.Item1; var css = kv.Item2;
                var target = (css != null && css.ZIndex.HasValue && css.ZIndex.Value < 0) ? _fixedBehind : _fixedFront;
                target.Children.Add(fe);
                // PositionOnCanvas(fe, css, grid); // TODO: Fix arguments
            }
            catch { }
        }
        _pendingFixed.Clear();
        try { System.Diagnostics.Debug.WriteLine("[BuildAsync] done children=" + panel.Children.Count); } catch { }
        
        // Wrap in ScrollViewer for scrolling support
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = grid
        };
        return scroller;
    }
    catch { }
    
    // Fallback wrap
    return new ScrollViewer
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Content = rootVisual
    };
}
private static void ApplyFlexChildAlignment(Control fe, bool isRow, string align)
{
    try
    {
        if (isRow)
        {
            if (align == "center") fe.VerticalAlignment = VerticalAlignment.Center;
            else if (align == "flex-start" || align == "start") fe.VerticalAlignment = VerticalAlignment.Top;
            else if (align == "flex-end" || align == "end") fe.VerticalAlignment = VerticalAlignment.Bottom;
            else fe.VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            if (align == "center") fe.HorizontalAlignment = HorizontalAlignment.Center;
            else if (align == "flex-start" || align == "start") fe.HorizontalAlignment = HorizontalAlignment.Left;
            else if (align == "flex-end" || align == "end") fe.HorizontalAlignment = HorizontalAlignment.Right;
            else fe.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
    }
    catch { }
}
private static void ApplyJustify(Control cont, bool isRow, string justify)
{
    try
    {
        if (isRow)
        {
            if (justify == "center") cont.HorizontalAlignment = HorizontalAlignment.Center;
            else if (justify == "flex-end" || justify == "end") cont.HorizontalAlignment = HorizontalAlignment.Right;
            else cont.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            if (justify == "center") cont.VerticalAlignment = VerticalAlignment.Center;
            else if (justify == "flex-end" || justify == "end") cont.VerticalAlignment = VerticalAlignment.Bottom;
            else cont.VerticalAlignment = VerticalAlignment.Stretch;
        }
    }
    catch { }
}
private static Control BuildJustifiedLine(List<Control> elems, bool isRow, string mode, string align)
{
    try
    {
        var grid = new Grid();
        if (elems == null || elems.Count == 0) return grid;
        if (isRow)
        {
            bool around = string.Equals(mode, "space-around", StringComparison.OrdinalIgnoreCase);
            if (around) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < elems.Count; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                if (i < elems.Count - 1) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            if (around) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int col = around ? 1 : 0;
            for (int i = 0; i < elems.Count; i++)
            {
                var fe = elems[i]; ApplyFlexChildAlignment(fe, true, align);
                Grid.SetColumn(fe, col); grid.Children.Add(fe);
                col += 2; // skip spacer
            }
        }
        else
        {
            bool around = string.Equals(mode, "space-around", StringComparison.OrdinalIgnoreCase);
            if (around) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < elems.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                if (i < elems.Count - 1) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            }
            if (around) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            int row = around ? 1 : 0;
            for (int i = 0; i < elems.Count; i++)
            {
                var fe = elems[i]; ApplyFlexChildAlignment(fe, false, align);
                Grid.SetRow(fe, row); grid.Children.Add(fe);
                row += 2;
            }
            }

        return grid;
    }
    catch { return new Grid(); }
}

private async Task<Control> RenderNodeAsync(LiteElement n, Uri baseUri, Action<Uri> onNavigate, JavaScriptEngine js, CancellationToken ct)
{
    if (n == null) return null;
    try { System.IO.File.AppendAllText("debug_log.txt", $"[RenderNode] {n.Tag}\r\n"); } catch { }
    ct.ThrowIfCancellationRequested();
    if (n.IsText)
    {
        var txt = CollapseWs(n.Text);
        if (string.IsNullOrWhiteSpace(txt)) return null;
        return new TextBlock { Text = txt, TextWrapping = TextWrapping.Wrap, FontSize = 15, Foreground = new SolidColorBrush(Colors.Black) };
    }
    if (_tagHandlers != null && _tagHandlers.ContainsKey(n.Tag))
    {
        return await _tagHandlers[n.Tag](n, baseUri, onNavigate, js, ct);
    }
    return await RenderGenericContainerAsync(n, baseUri, onNavigate, js, ct);
}
private async Task<Control> RenderBlockAsync(LiteElement n, Uri baseUri, Action<Uri> onNavigate, JavaScriptEngine js, CancellationToken ct)
{
    return await RenderGenericContainerAsync(n, baseUri, onNavigate, js, ct);
}
private async Task<Control> RenderGenericContainerAsync(LiteElement n, Uri baseUri, Action<Uri> onNavigate, JavaScriptEngine js, CancellationToken ct)
{
    // Check for Flexbox/Grid
    if (ComputedStyles != null && ComputedStyles.TryGetValue(n, out var cssCheck))
    {
        if (IsFlexContainer(cssCheck) || IsGridContainer(cssCheck))
        {
            return await MakeGridFallbackAsync(n, baseUri, onNavigate, js, ct);
        }
    }

    var panel = new StackPanel { Orientation = Orientation.Vertical };
    try { ApplyComputedStyles(panel, n); } catch { }
    try { ApplyInlineStyles(panel, n); } catch { }
    if (n.Children != null)
    {
        foreach (var child in n.Children)
        {
            ct.ThrowIfCancellationRequested();
            var elt = await RenderNodeAsync(child, baseUri, onNavigate, js, ct);
            if (elt != null) panel.Children.Add(elt);
        }
    }
    // Sticky support
    if (ComputedStyles != null && ComputedStyles.TryGetValue(n, out var css))
    {
        if (string.Equals(css.Position, "sticky", StringComparison.OrdinalIgnoreCase))
        {
            // AttachStickyBehavior(panel, css); // TODO: Fix arguments
        }
    }
    return ApplyBoxesAndText(panel, n);
}
private async Task<Control> MakeButtonAsync(LiteElement n, Uri baseUri, Action<Uri> onNavigate, JavaScriptEngine js, CancellationToken ct)
{
    var btn = new Button();
    var panel = new StackPanel { Orientation = Orientation.Horizontal };
    if (n.Children != null)
    {
        foreach (var child in n.Children)
        {
            var elt = await RenderNodeAsync(child, baseUri, onNavigate, js, ct);
            if (elt != null) panel.Children.Add(elt);
        }
    }
    if (panel.Children.Count == 0 && !string.IsNullOrWhiteSpace(n.Text))
    {
        panel.Children.Add(new TextBlock { Text = n.Text });
    }
    btn.Content = panel;
    try { ApplyComputedStyles(btn, n); } catch { }
    try { ApplyInlineStyles(btn, n); } catch { }
    if (n.Attr != null && n.Attr.TryGetValue("onclick", out var code) && !string.IsNullOrWhiteSpace(code))
    {
        string id = null;
        n.Attr.TryGetValue("id", out id);
        btn.Click += (s, e) =>
        {
            try
            {
                js.RunInline(code, null, "click", id);
            }
            catch { }
        };
    }
    return btn;
}
private Control MakePre(LiteElement n)
{
    var raw = n.IsText ? (n.Text ?? "") : GatherText(n);
    raw = WebUtility.HtmlDecode(raw);
    var tb = new TextBlock
    {
        FontFamily = new FontFamily("Consolas"),
        FontSize = 14,
        Text = raw,
        TextWrapping = TextWrapping.NoWrap,
        // IsTextSelectionEnabled = true // Not available in Avalonia
    };
    var scroller = new ScrollViewer
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = tb
    };
    var box = new Border
    {
        Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
        Child = scroller,
        Padding = new Thickness(8)
    };
    return box;
}
private async Task<Control> MakeListAsync(LiteElement n, bool ordered, Uri baseUri, Action<Uri> onNavigate, JavaScriptEngine js, CancellationToken ct)
{
    bool listStyleNone = false;
    bool inlineItems = false;
    try
    {
        var cssUl = TryGetCss(n);
        if (cssUl != null && cssUl.Map != null)
        {
            string ls; if (cssUl.Map.TryGetValue("list-style", out ls) && !string.IsNullOrWhiteSpace(ls) && ls.ToLowerInvariant().Contains("none")) listStyleNone = true;
            string lst; if (cssUl.Map.TryGetValue("list-style-type", out lst) && !string.IsNullOrWhiteSpace(lst) && lst.ToLowerInvariant().Contains("none")) listStyleNone = true;
            string disp; if (cssUl.Map.TryGetValue("display", out disp) && !string.IsNullOrWhiteSpace(disp) && disp.ToLowerInvariant().Contains("flex")) inlineItems = true;
        }
    }
    catch { }
    var liNodes = (n.Children?.Where(c => c.Tag == "li") ?? Enumerable.Empty<LiteElement>()).ToList();
    foreach (var li in liNodes)
    {
        try
        {
            var cssLi = TryGetCss(li);
            if (cssLi != null && cssLi.Map != null)
            {
                string disp; if (cssLi.Map.TryGetValue("display", out disp) && !string.IsNullOrWhiteSpace(disp))
                {
                    var d = disp.ToLowerInvariant(); if (d.Contains("inline")) inlineItems = true;
                }
                string ls; if (cssLi.Map.TryGetValue("list-style", out ls) && !string.IsNullOrWhiteSpace(ls) && ls.ToLowerInvariant().Contains("none")) listStyleNone = true;
                string lst; if (cssLi.Map.TryGetValue("list-style-type", out lst) && !string.IsNullOrWhiteSpace(lst) && lst.ToLowerInvariant().Contains("none")) listStyleNone = true;
            }
        }
        catch { }
    }
    if (inlineItems)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        bool first = true;
        foreach (var li in liNodes)
        {
            ct.ThrowIfCancellationRequested();
            var content = await RenderNodeAsync(li, baseUri, onNavigate, js, ct);
            if (content == null) content = new TextBlock { Text = CollapseWs(GatherText(li)) };
            if (!first) content.Margin = new Thickness(12, 0, 0, 0);
            first = false;
            row.Children.Add(content);
        }
        return Finish(row, n);
    }
    else
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 6, 0, 6) };
        int i = 1;
        try { if (ordered && n.Attr != null) { string st; if (n.Attr.TryGetValue("start", out st)) { int si; if (int.TryParse(st, out si) && si > 0) i = si; } } } catch { }
        foreach (var li in liNodes)
        {
            ct.ThrowIfCancellationRequested();
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            try { if (ordered && li != null && li.Attr != null) { string v; if (li.Attr.TryGetValue("value", out v)) { int vi; if (int.TryParse(v, out vi) && vi > 0) i = vi; } } } catch { }
            if (!listStyleNone)
            {
                var bullet = new TextBlock { Text = (ordered ? (GetOrderedBulletString(n, li, i)) : GetUnorderedBulletChar(n, li)) + " ", Width = 22, HorizontalAlignment = HorizontalAlignment.Left };
                row.Children.Add(bullet);
            }
            // Render children of li into a WrapPanel to ensure inline layout
            var itemContent = new WrapPanel { Orientation = Orientation.Horizontal };
            if (li.Children != null)
            {
                foreach (var child in li.Children)
                {
                    var childControl = await RenderNodeAsync(child, baseUri, onNavigate, js, ct);
                    if (childControl != null)
                    {
                        // Add some spacing for text nodes if needed, or rely on natural spacing
                        if (childControl is TextBlock tb)
                        {
                            tb.VerticalAlignment = VerticalAlignment.Center;
                            tb.Margin = new Thickness(0, 0, 4, 0);
                        }
                        else
                        {
                            childControl.VerticalAlignment = VerticalAlignment.Center;
                            childControl.Margin = new Thickness(0, 0, 4, 0);
                        }
                        itemContent.Children.Add(childControl);
                    }
                }
            }
            // If no children but has text
            if (itemContent.Children.Count == 0 && li.IsText)
            {
                 var txt = CollapseWs(li.Text);
                 if (!string.IsNullOrWhiteSpace(txt))
                     itemContent.Children.Add(new TextBlock { Text = txt, TextWrapping = TextWrapping.Wrap });
            }
            
            row.Children.Add(itemContent);
            stack.Children.Add(row);
            i++;
        }
        return Finish(stack, n);
    }
}
private Control MakeInput(LiteElement n)
{
    var type = (DictGet(n.Attr, "type") ?? "text").ToLowerInvariant();
    var val = DictGet(n.Attr, "value") ?? "";
    var placeholder = DictGet(n.Attr, "placeholder") ?? "";
            if (type == "password")
            {
                var pb = new TextBox { PasswordChar = '*', Text = val };
                if (!string.IsNullOrEmpty(placeholder))
                {
                    // TextBox /* was PasswordBox */ doesn't support PlaceholderText directly in all versions, 
                    // but we can try setting a tooltip or just leave it.
                    ToolTip.SetTip(pb, placeholder);
                }
                return pb;
            }
            else if (type == "checkbox")
            {
                var cb = new CheckBox { Content = val, IsChecked = n.Attr != null && n.Attr.ContainsKey("checked") };
                return cb;
            }
            else if (type == "radio")
            {
                var rb = new RadioButton { Content = val, IsChecked = n.Attr != null && n.Attr.ContainsKey("checked") };
                if (n.Attr != null && n.Attr.ContainsKey("name")) rb.GroupName = n.Attr["name"];
                return rb;
            }
            else if (type == "submit" || type == "button" || type == "reset")
            {
                var btn = new Button { Content = string.IsNullOrEmpty(val) ? (type == "submit" ? "Submit" : "Button") : val };
                return btn;
            }
            else if (type == "date")
            {
                try { return new DatePicker(); } catch { return new TextBox { Text = val /* Watermark skipped */ }; }
            }
            else if (type == "time")
            {
                try { return new TimePicker(); } catch { return new TextBox { Text = val /* Watermark skipped */ }; }
            }
            return new TextBox { Text = val /* Watermark skipped */ };
        }
        private Control MakeTextarea(LiteElement n)
{
    var val = n.IsText ? n.Text : GatherText(n);
    var placeholder = DictGet(n.Attr, "placeholder") ?? "";
    return new TextBox { Text = val ?? "", /* Watermark skipped */ AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
}
private Control MakeSelect(LiteElement n)
{
    var cb = new ComboBox();
    if (n.Children != null)
    {
        foreach (var child in n.Children)
        {
            if (child.Tag == "option")
            {
                var item = new ComboBoxItem { Content = GatherText(child) };
                if (child.Attr != null && child.Attr.ContainsKey("selected")) item.IsSelected = true;
                cb.Items.Add(item);
            }
        }
    }
    if (cb.Items.Count > 0 && cb.SelectedIndex < 0) cb.SelectedIndex = 0;
    return cb;
}
private async Task<Control> MakeLink(LiteElement n, Uri baseUri, Action<Uri> onNavigate, JavaScriptEngine js, CancellationToken ct)
{
    string text = null;
    var href = DictGet(n.Attr, "href");
    Uri abs = ResolveUri(baseUri, href);
    
    // Recursive rendering for link content
    var contentPanel = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
    if (n.Children != null)
    {
        foreach (var child in n.Children)
        {
            var elt = await RenderNodeAsync(child, baseUri, onNavigate, js, ct);
            if (elt != null) contentPanel.Children.Add(elt);
        }
    }
    // Fallback if empty
    if (contentPanel.Children.Count == 0)
    {
        text = !string.IsNullOrWhiteSpace(n.Text) ? n.Text : (!string.IsNullOrEmpty(href) ? href : "(link)");
        contentPanel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
    }

    if (abs == null && !string.IsNullOrWhiteSpace(href))
    {
         try { System.IO.File.AppendAllText("debug_log.txt", $"[MakeLink] Failed to resolve: {href} Base: {baseUri}\r\n"); } catch { }
    }

    var hb = new Button
    {
        Content = contentPanel,
        Foreground = LinkBrush,
        Background = Brushes.Transparent, // Fix: Make it look like a link, not a button
        BorderThickness = new Thickness(0), // Fix: Remove button border
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 0, 0, 0),
        Padding = new Thickness(0)
    };
    try { Avalonia.Automation.AutomationProperties.SetName(hb, text); } catch { }
    try { hb.IsTabStop = true; } catch { }
    // Key handling falls through to Click via default behaviors
    ToolTip.SetTip(hb, abs != null ? (object)abs.AbsoluteUri : href);
    // If CSS computed style indicates display:block (or list-item), treat this link as a block
    try
    {
        var css = TryGetCss(n);
        var disp = css != null ? (css.Map != null && css.Map.ContainsKey("display") ? (css.Map["display"] ?? "").ToLowerInvariant() : (css.Display ?? "").ToLowerInvariant()) : string.Empty;
        if (disp.Contains("block") || disp.Contains("list-item"))
        {
            hb.HorizontalAlignment = HorizontalAlignment.Stretch;
            hb.Margin = new Thickness(0, 6, 0, 6);
        }
    }
    catch { }
    if (!string.IsNullOrWhiteSpace(href) && href.Trim().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
    {
        abs = null; // strip unsafe javascript: URLs
    }
    // Additional google redirector forms (e.g., /url=/httpservice/retry/enablejs or /url=<absolute>)
    try
    {
        if (abs == null && !string.IsNullOrWhiteSpace(href) && baseUri != null)
        {
            var host = baseUri.Host ?? string.Empty;
            var trimmed = href.Trim();
            if (host.IndexOf("google.", StringComparison.OrdinalIgnoreCase) >= 0 && trimmed.StartsWith("/url=", StringComparison.OrdinalIgnoreCase))
            {
                var after = trimmed.Substring(5); // after '/url='
                                                  // If it looks like an absolute URL, use it; otherwise, ignore (enablejs etc.)
                Uri t;
                if (Uri.TryCreate(after, UriKind.Absolute, out t)) abs = t;
                else if (after.StartsWith("/httpservice/retry/enablejs", StringComparison.OrdinalIgnoreCase)) abs = null; // ignore JS-enabler stubs
            }
        }
    }
    catch { }
    // Normalize Google redirectors for absolute links too (https://google.com/url?... or /url=...)
    try
    {
        if (abs != null)
        {
            var host = abs.Host ?? string.Empty;
            if (host.IndexOf("google.", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var path = abs.AbsolutePath ?? string.Empty;
                Uri resolvedTarget = null;
                if (string.Equals(path, "/url", StringComparison.OrdinalIgnoreCase))
                {
                    var q = (abs.Query ?? string.Empty).TrimStart('?');
                    string target = null;
                    var parts = q.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < parts.Length; i++)
                    {
                        var kv = parts[i].Split(new[] { '=' }, 2);
                        if (kv.Length == 2)
                        {
                            var name = kv[0];
                            var val = Uri.UnescapeDataString(kv[1] ?? "");
                            if (string.Equals(name, "url", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "q", StringComparison.OrdinalIgnoreCase))
                            { target = val; break; }
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        // Ignore JS enabler
                        if (target.StartsWith("/httpservice/retry/enablejs", StringComparison.OrdinalIgnoreCase)) { resolvedTarget = null; }
                        else
                        {
                            Uri t;
                            if (Uri.TryCreate(target, UriKind.Absolute, out t)) resolvedTarget = t;
                            else if (baseUri != null && Uri.TryCreate(baseUri, target, out t)) resolvedTarget = t;
                        }
                    }
                }
                else if (path.StartsWith("/url=", StringComparison.OrdinalIgnoreCase))
                {
                    var target = Uri.UnescapeDataString(path.Substring(5));
                    if (!string.IsNullOrWhiteSpace(target) && !target.StartsWith("/httpservice/retry/enablejs", StringComparison.OrdinalIgnoreCase))
                    {
                        Uri t;
                        if (Uri.TryCreate(target, UriKind.Absolute, out t)) resolvedTarget = t;
                        else if (baseUri != null && Uri.TryCreate(baseUri, target, out t)) resolvedTarget = t;
                    }
                }
                if (resolvedTarget != null) abs = resolvedTarget;
                else if (!string.Equals(path, "/url", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("/url=", StringComparison.OrdinalIgnoreCase)) { /* keep abs */ }
                else if (resolvedTarget == null) { abs = null; }
            }
        }
    }
    catch { }
    if (abs != null && onNavigate != null)
        hb.Click += (s, e) =>
        {
            try { System.IO.File.AppendAllText("debug_log.txt", $"[LinkClick] Href={href} Abs={abs}\r\n"); } catch { }
            string elId = null; try { if (n.Attr != null) n.Attr.TryGetValue("id", out elId); } catch { }
            bool cancel = false;
            // Inline JS handler may cancel default ("return false;")
            string onclick;
            if (n.Attr != null && n.Attr.TryGetValue("onclick", out onclick) && Js != null)
            {
                var script = PreprocessInlineHandler(onclick, elId);
                cancel = Js.RunInline(script, new JsContext { BaseUri = baseUri }, "click", elId);
            }
            // Fire addEventListener listeners synchronously to honor preventDefault/stopPropagation
            bool cancelDom = false;
            try { if (!string.IsNullOrWhiteSpace(elId) && Js != null) cancelDom = Js.RaiseElementEventSync(elId, "click"); } catch { cancelDom = false; }
            if (cancel || cancelDom)
            {
                TrySetHandled(e);
                return;
            }
            if (abs != null) onNavigate(abs);
        };
    if (abs != null)
    {
        hb.PointerEntered += (s, e) => { StatusMessage?.Invoke(abs.AbsoluteUri); };
        hb.PointerExited += (s, e) => { StatusMessage?.Invoke(string.Empty); };
        hb.Holding += (s, e) =>
        {
            // if (e.HoldingState == Windows.UI.Input.HoldingState.Started) LinkLongPressed?.Invoke(abs); // Windows namespace N/A
        };
    }
    return hb;
}
private Control RenderLooseInput(LiteElement n)
{
    string type = null; if (n.Attr != null) n.Attr.TryGetValue("type", out type);
    type = (type ?? "text").ToLowerInvariant();
    if (n.Tag == "textarea")
        return new TextBox { AcceptsReturn = true, Height = 80, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6), Background = new SolidColorBrush(Colors.White), Foreground = new SolidColorBrush(Colors.Black), BorderBrush = new SolidColorBrush(Color.FromArgb(255, 220, 220, 220)), Padding = new Thickness(8, 4, 8, 4) };
    if (type == "submit" || n.Tag == "button")
    {
        var btn = new Button { Content = CollapseWs(GatherText(n)).Length > 0 ? CollapseWs(GatherText(n)) : "Submit", Margin = new Thickness(0, 4, 0, 4) };
        // Add click event handling
        btn.Click += (s, e) =>
        {
            bool cancel = false;
            try
            {
                if (Js != null)
                {
                    string id = null; try { if (n.Attr != null) n.Attr.TryGetValue("id", out id); } catch { }
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        cancel = Js.RaiseElementEventSync(id, "click");
                    }
                }
            }
            catch { }
            if (cancel) { TrySetHandled(e); }
        };
        return btn;
    }
    return new TextBox { Margin = new Thickness(0, 4, 0, 4), Background = new SolidColorBrush(Colors.White), Foreground = new SolidColorBrush(Colors.Black), BorderBrush = new SolidColorBrush(Color.FromArgb(255, 220, 220, 220)), Padding = new Thickness(8, 4, 8, 4), Height = 36 };
}
internal sealed class OptionItem
{
    public string Text { get; set; }
    public string Value { get; set; }
    public override string ToString() => Text ?? Value ?? "";
}
private async Task<Control> MakeFormAsync(LiteElement form, Uri baseUri, Action<Uri> onNavigate, JavaScriptEngine js, CancellationToken ct)
{
    var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 2, 0, 8) };
    var inputsSingle = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);
    var inputsMulti = new Dictionary<string, List<Control>>(StringComparer.OrdinalIgnoreCase);
    var staticInputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string submitText = "Submit";
    var textBoxes = new List<TextBox>();
    // Flatten descendants
    var all = new List<LiteElement>();
    var stack = new Stack<LiteElement>();
    foreach (var ch in form.Children) stack.Push(ch);
    while (stack.Count > 0)
    {
        var cur = stack.Pop();
        all.Add(cur);
        for (int i = cur.Children.Count - 1; i >= 0; i--) stack.Push(cur.Children[i]);
    }
    foreach (var child in all)
    {
        ct.ThrowIfCancellationRequested();
        if (child.Attr == null) continue;
        // Skip disabled
        if (child.Attr.ContainsKey("disabled")) continue;
        string name;
        if (child.Tag == "input" && child.Attr.TryGetValue("name", out name))
        {
            string type; child.Attr.TryGetValue("type", out type);
            type = (type ?? "text").ToLowerInvariant();
            if (type == "hidden")
            {
                string val; child.Attr.TryGetValue("value", out val);
                staticInputs[name] = val ?? "";
                continue;
            }
            if (type == "checkbox")
            {
                var cb = new CheckBox { Margin = new Thickness(0, 4, 0, 4) };
                string v; child.Attr.TryGetValue("value", out v);
                cb.Content = CollapseWs(DictGet(child.Attr, "label") ?? child.Text ?? name);
                panel.Children.Add(cb);
                if (!inputsMulti.ContainsKey(name)) inputsMulti[name] = new List<Control>();
                inputsMulti[name].Add(cb);
                continue;
            }
            if (type == "radio")
            {
                var rb = new RadioButton { Margin = new Thickness(0, 4, 0, 4), GroupName = name };
                string v; child.Attr.TryGetValue("value", out v);
                rb.Content = CollapseWs(DictGet(child.Attr, "label") ?? child.Text ?? (v ?? name));
                panel.Children.Add(rb);
                if (!inputsMulti.ContainsKey(name)) inputsMulti[name] = new List<Control>();
                inputsMulti[name].Add(rb);
                continue;
            }
            if (type == "text" || type == "search" || type == "email" || type == "url" || type == "password")
            {
                var tb = new TextBox { Margin = new Thickness(0, 4, 0, 4) };
                string val; if (child.Attr.TryGetValue("value", out val)) tb.Text = val;
                panel.Children.Add(tb);
                inputsSingle[name] = tb;
                textBoxes.Add(tb);
                continue;
            }
            if (type == "submit")
            {
                string value;
                if (child.Attr.TryGetValue("value", out value) && !string.IsNullOrWhiteSpace(value))
                    submitText = value;
                continue;
            }
        }
        else if (child.Tag == "textarea" && child.Attr.TryGetValue("name", out name))
        {
            var tb = new TextBox { AcceptsReturn = true, Height = 80, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
            panel.Children.Add(tb);
            inputsSingle[name] = tb;
            textBoxes.Add(tb);
        }
        else if (child.Tag == "select" && child.Attr.TryGetValue("name", out name))
        {
            bool multiple = child.Attr.ContainsKey("multiple");
            var options = child.Children.Where(c => c.Tag == "option").ToList();
            if (multiple)
            {
                var lb = new ListBox { SelectionMode = SelectionMode.Multiple, Margin = new Thickness(0, 4, 0, 4) };
                foreach (var opt in options)
                {
                    var it = new OptionItem
                    {
                        Text = CollapseWs(opt.Text ?? DictGet(opt.Attr, "label") ?? DictGet(opt.Attr, "value")),
                        Value = DictGet(opt.Attr, "value") ?? CollapseWs(opt.Text)
                    };
                    lb.Items.Add(it);
                }
                panel.Children.Add(lb);
                if (!inputsMulti.ContainsKey(name)) inputsMulti[name] = new List<Control>();
                inputsMulti[name].Add(lb);
            }
            else
            {
                var combo = new ComboBox { Margin = new Thickness(0, 4, 0, 4) };
                foreach (var opt in options)
                {
                    var it = new OptionItem
                    {
                        Text = CollapseWs(opt.Text ?? DictGet(opt.Attr, "label") ?? DictGet(opt.Attr, "value")),
                        Value = DictGet(opt.Attr, "value") ?? CollapseWs(opt.Text)
                    };
                    combo.Items.Add(it);
                }
                panel.Children.Add(combo);
                inputsSingle[name] = combo;
            }
        }
    }
    if (inputsSingle.Count == 0 && inputsMulti.Count == 0)
    {
        var tb = new TextBox { Margin = new Thickness(0, 4, 0, 4), Watermark = "Search?", Background = new SolidColorBrush(Colors.White), Foreground = new SolidColorBrush(Colors.Black), BorderBrush = new SolidColorBrush(Color.FromArgb(255, 220, 220, 220)), Padding = new Thickness(8, 4, 8, 4), Height = 36 };
        panel.Children.Add(tb);
        inputsSingle["q"] = tb;
        submitText = "Search";
        textBoxes.Add(tb);
    }
    var submitBtn = new Button
    {
        Content = submitText,
        Margin = new Thickness(0, 6, 0, 6),
        Background = new SolidColorBrush(Color.FromArgb(255, 245, 245, 245)),
        Foreground = new SolidColorBrush(Colors.Black),
        BorderBrush = new SolidColorBrush(Color.FromArgb(255, 210, 210, 210)),
        Padding = new Thickness(12, 6, 12, 6),
        MinHeight = 32,
        HorizontalAlignment = HorizontalAlignment.Left
    };
    // Heuristic: widen primary text inputs (search box)
    try
    {
        double vw = 0; try { if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null) vw = desktop.MainWindow.Width /* .Bounds not available */; } catch { }
        if (vw <= 0) vw = 360;
        double minSearch = Math.Min(720, Math.Max(220, vw * 0.75));
        double maxSearch = Math.Min(800, Math.Max(minSearch, vw * 0.95));
        // If there is exactly one text box, treat it as the main input
        if (textBoxes.Count == 1)
        {
            var tb0 = textBoxes[0];
            if (tb0 != null)
            {
                if (double.IsNaN(tb0.Width) || tb0.Width <= 0) tb0.MinWidth = minSearch;
                tb0.MaxWidth = maxSearch;
                tb0.HorizontalAlignment = HorizontalAlignment.Left;
            }
        }
        else
        {
            foreach (var tbx in textBoxes)
            {
                if (tbx == null) continue;
                if (double.IsNaN(tbx.Width) || tbx.Width <= 0) tbx.MinWidth = Math.Min(560, Math.Max(180, vw * 0.55));
                tbx.MaxWidth = Math.Min(720, Math.Max(tbx.MinWidth, vw * 0.9));
                tbx.HorizontalAlignment = HorizontalAlignment.Left;
            }
        }
    }
    catch { }
    // Focus first textbox if any
    TextBox firstTb = null;
    foreach (var ctrl in inputsSingle.Values) { var tb = ctrl as TextBox; if (tb != null) { firstTb = tb; break; } }
    if (firstTb != null) firstTb.Loaded += (s, e) => firstTb.Focus();
    // Submit action
    Action submit = () =>
    {
        // onsubmit JS (may cancel via return false)
        string onsubmit;
        if (form.Attr != null && form.Attr.TryGetValue("onsubmit", out onsubmit) && Js != null)
        {
            string formId = null; try { if (form.Attr != null) form.Attr.TryGetValue("id", out formId); } catch { }
            bool cancel = Js.RunInline(onsubmit, new JsContext { BaseUri = baseUri }, "submit", formId);
            if (cancel) return;
        }
        // Property-based handler (form.onsubmit = function(){...})
        try
        {
            /* property onsubmit not canceling in JS-0 */
        }
        catch { }
        string action = null; if (form.Attr != null) form.Attr.TryGetValue("action", out action);
        string method = null; if (form.Attr != null) form.Attr.TryGetValue("method", out method);
        method = (method ?? "get").Trim().ToLowerInvariant();
        var qs = new System.Text.StringBuilder();
        Action<string, string> append = (k, v) =>
        {
            if (qs.Length > 0) qs.Append("&");
            qs.Append(Uri.EscapeDataString(k)).Append("=").Append(Uri.EscapeDataString(v ?? ""));
        };
        try
        {
            // Compatibility hint: Google results render better in Basic HTML mode under limited JS engines
            string host = baseUri != null ? (baseUri.Host ?? "").ToLowerInvariant() : "";
            if (host.Contains("google."))
            {
                append("hl", "en");
                append("gbv", "1");
            }
        }
        catch { }
        foreach (var kv in staticInputs) append(kv.Key, kv.Value);
        foreach (var kv in inputsSingle)
        {
            var ctrl = kv.Value;
            var tbx = ctrl as TextBox;
            var combo = ctrl as ComboBox;
            if (tbx != null) append(kv.Key, tbx.Text ?? "");
            else if (combo != null)
            {
                var it = combo.SelectedItem as OptionItem;
                append(kv.Key, it?.Value ?? it?.Text ?? "");
            }
        }
        foreach (var kv in inputsMulti)
        {
            foreach (var ctrl in kv.Value)
            {
                var cb = ctrl as CheckBox;
                var rb = ctrl as RadioButton;
                var lb = ctrl as ListBox;
                if (cb != null)
                {
                    if (cb.IsChecked == true)
                    {
                        // value attribute if provided, else "on"
                        append(kv.Key, "on");
                    }
                }
                else if (rb != null)
                {
                    if (rb.IsChecked == true)
                    {
                        append(kv.Key, (rb.Content as string) ?? "on");
                    }
                }
                else if (lb != null)
                {
                    foreach (var sel in lb.SelectedItems)
                    {
                        var it = sel as OptionItem;
                        append(kv.Key, it?.Value ?? it?.Text ?? "");
                    }
                }
            }
        }
        string forcedAction = null;
        if (string.IsNullOrEmpty(action) && baseUri != null)
        {
            var host = baseUri.Host.ToLowerInvariant();
            if (host.Contains("google.")) forcedAction = baseUri.Scheme + "://www.google.com/search";
            else if (host.Contains("bing.")) forcedAction = baseUri.Scheme + "://www.bing.com/search";
            else if (host.Contains("duckduckgo.")) forcedAction = baseUri.Scheme + "://duckduckgo.com/";
        }
        Uri target = null;
        string actionOrForced = !string.IsNullOrEmpty(action) ? action : forcedAction;
        if (string.IsNullOrEmpty(actionOrForced)) target = baseUri;
        else
        {
            Uri abs;
            if (Uri.TryCreate(actionOrForced, UriKind.Absolute, out abs)) target = abs;
            else if (baseUri != null && Uri.TryCreate(baseUri, actionOrForced, out abs)) target = abs;
        }
        if (target == null) return;
        if (method == "get")
        {
            if (onNavigate != null)
            {
                string sep = (target.Query != null && target.Query.Length > 0) ? "&" : "?";
                var nav = new Uri(target.AbsoluteUri + sep + qs.ToString());
                onNavigate(nav);
            }
            return;
        }
        if (method == "post")
        {
            if (OnPost != null)
            {
                OnPost(target, qs.ToString());
                return;
            }
            // fallback to GET append
            if (onNavigate != null)
            {
                string sep = (target.Query != null && target.Query.Length > 0) ? "&" : "?";
                var nav = new Uri(target.AbsoluteUri + sep + qs.ToString());
                onNavigate(nav);
            }
            return;
        }
        // Otherwise, safe GET fallback
        if (onNavigate != null)
        {
            string sep = (target.Query != null && target.Query.Length > 0) ? "&" : "?";
            var nav = new Uri(target.AbsoluteUri + sep + qs.ToString());
            onNavigate(nav);
        }
    };
    foreach (var tb in textBoxes)
    {
        tb.KeyDown += (s, e) =>
        {
            // if (!tb.AcceptsReturn && e.Key == Windows.System.VirtualKey.Enter) // Windows namespace N/A
            {
                TrySetHandled(e);
                submit();
            }
        };
    }
    submitBtn.Click += (s, e) =>
    {
        bool cancel = false;
        try
        {
            if (Js != null)
            {
                string id = null; try { if (form.Attr != null) form.Attr.TryGetValue("id", out id); } catch { }
                if (!string.IsNullOrWhiteSpace(id))
                {
                    cancel = Js.RaiseElementEventSync(id, "click");
                }
            }
        }
        catch { }
        if (cancel) { TrySetHandled(e); return; }
        submit();
    };
    panel.Children.Add(submitBtn);
    // Center narrow single-form pages for visibility
    try
    {
        if (textBoxes.Count <= 1 && inputsSingle.Count + inputsMulti.Count <= 2)
        {
            var wrap = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            // Pull content up a bit to reduce empty top area
            try
            {
                if (baseUri != null && (baseUri.Host ?? "").ToLowerInvariant().Contains("google."))
                    wrap.Margin = new Thickness(0, 12, 0, 0);
                else
                    wrap.Margin = new Thickness(0, 8, 0, 0);
            }
            catch { }
            wrap.Children.Add(panel);
            return Finish(wrap, form);
        }
    }
    catch { }
    return Finish(panel, form);
}
// ---------- Tables ----------
internal sealed class CellPlacement
{
    public LiteElement Cell;
    public int Row;
    public int Col;
    public int RowSpan = 1;
    public int ColSpan = 1;
}
private static int GetIntAttr(LiteElement n, string name, int fallback = 1)
{
    if (n?.Attr == null) return fallback;
    string v; if (!n.Attr.TryGetValue(name, out v)) return fallback;
    int i; return int.TryParse(v, out i) && i > 0 ? i : fallback;
}
private GridLength? ParseColWidth(LiteElement col)
{
    if (col == null) return null;
    // 1. Try attribute
    if (col.Attr != null)
    {
        string w;
        if (col.Attr.TryGetValue("width", out w) && !string.IsNullOrWhiteSpace(w))
        {
            w = w.Trim();
            if (w == "*") return new GridLength(1, GridUnitType.Star);
            if (w.EndsWith("%"))
            {
                double d;
                if (double.TryParse(w.TrimEnd('%'), out d))
                    return new GridLength(d, GridUnitType.Star);
            }
            double px;
            if (double.TryParse(w.Replace("px", ""), out px))
                return new GridLength(px);
        }
    }
    // 2. Try CSS
    try
    {
        var css = TryGetCss(col);
        if (css != null && css.Width.HasValue) return new GridLength(css.Width.Value);
    }
    catch { }
    return null;
}
private async Task<Control> MakeTableAsync(LiteElement table, Uri baseUri, Action<Uri> onNavigate, JavaScriptEngine js, CancellationToken ct)
{
    var rows = new List<LiteElement>();
    if (table.Children != null)
    {
        foreach (var ch in table.Children)
        {
            if (ch.Tag == "tr") rows.Add(ch);
            else if (ch.Tag == "thead" || ch.Tag == "tbody" || ch.Tag == "tfoot")
            {
                if (ch.Children != null)
                {
                    foreach (var tr in ch.Children) if (tr.Tag == "tr") rows.Add(tr);
                }
            }
        }
    }
    if (rows.Count == 0)
    {
        var emptyPanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 6, 0, 6) };
        if (table.Children != null)
        {
            foreach (var ch in table.Children)
            {
                var elt = await RenderNodeAsync(ch, baseUri, onNavigate, js, ct);
                if (elt != null) emptyPanel.Children.Add(elt);
            }
        }
        return Finish(emptyPanel, table);
    }
    // Compute max columns considering colspan
    int maxCols = 1;
    foreach (var r in rows)
    {
        int sum = 0;
        foreach (var c in r.Children)
            if (c.Tag == "td" || c.Tag == "th")
                sum += GetIntAttr(c, "colspan", 1);
        if (sum > maxCols) maxCols = sum;
    }
    // Occupancy grid to place cells with row/col spans
    int rowCount = rows.Count;
    var occ = new bool[rowCount, maxCols];
    var placements = new List<CellPlacement>();
    for (int r = 0; r < rowCount; r++)
    {
        int cIndex = 0;
        foreach (var cell in rows[r].Children.Where(c => c.Tag == "td" || c.Tag == "th"))
        {
            int colspan = GetIntAttr(cell, "colspan", 1);
            int rowspan = GetIntAttr(cell, "rowspan", 1);
            // find first free slot
            while (cIndex < maxCols && occ[r, cIndex]) cIndex++;
            if (cIndex >= maxCols) cIndex = maxCols - 1;
            placements.Add(new CellPlacement
            {
                Cell = cell,
                Row = r,
                Col = cIndex,
                RowSpan = Math.Max(1, Math.Min(rowspan, rowCount - r)),
                ColSpan = Math.Max(1, Math.Min(colspan, maxCols - cIndex))
            });
            // mark occupied
            for (int rr = r; rr < r + rowspan && rr < rowCount; rr++)
                for (int cc = cIndex; cc < cIndex + colspan && cc < maxCols; cc++)
                    occ[rr, cc] = true;
            cIndex += colspan;
        }
    }
    // Determine header rows (contiguous from top) by <thead> or all-<th> rows
    int headerRows = 0;
    for (int r = 0; r < rowCount; r++)
    {
        var row = rows[r];
        bool inThead = (row.Parent != null && string.Equals(row.Parent.Tag, "thead", StringComparison.OrdinalIgnoreCase));
        bool allTh = true;
        foreach (var c in row.Children) { if (!(c.Tag == "th")) { allTh = false; break; } }
        if (inThead || allTh) headerRows++;
        else break;
    }
    // Parse colgroup/col for width hints
    var colWidths = new GridLength[maxCols];
    for (int i = 0; i < maxCols; i++) colWidths[i] = GridLength.Auto;
    try
    {
        int colIdx = 0;
        if (table.Children != null)
        {
            foreach (var ch in table.Children)
            {
                if (ch.Tag == "colgroup")
                {
                    if (ch.Children != null)
                    {
                        foreach (var c in ch.Children)
                        {
                            if (c.Tag == "col")
                            {
                                int span = GetIntAttr(c, "span", 1);
                                var w = ParseColWidth(c);
                                for (int k = 0; k < span && colIdx < maxCols; k++)
                                {
                                    if (w.HasValue) colWidths[colIdx] = w.Value;
                                    colIdx++;
                                }
                            }
                        }
                    }
                }
                else if (ch.Tag == "col")
                {
                    int span = GetIntAttr(ch, "span", 1);
                    var w = ParseColWidth(ch);
                    for (int k = 0; k < span && colIdx < maxCols; k++)
                    {
                        if (w.HasValue) colWidths[colIdx] = w.Value;
                        colIdx++;
                    }
                }
            }
        }
    }
    catch { }
    // Build header/body grids
    var headerGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
    var bodyGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
    for (int c = 0; c < maxCols; c++)
    {
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = colWidths[c] });
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = colWidths[c] });
    }
    for (int r = 0; r < headerRows; r++) headerGrid.RowDefinitions.Add(new RowDefinition());
    for (int r = 0; r < (rowCount - headerRows); r++) bodyGrid.RowDefinitions.Add(new RowDefinition());
    // Helper to add a cell to a grid
    Func<Grid, int, int, int, int, Control, Border> addCell = (g, row, col, rs, cs, content) =>
    {
        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Colors.DimGray),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(6, 4, 6, 4),
            Child = content
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, col);
        if (rs > 1) Grid.SetRowSpan(border, rs);
        if (cs > 1) Grid.SetColumnSpan(border, cs);
        g.Children.Add(border);
        return border;
    };
    foreach (var place in placements)
    {
        ct.ThrowIfCancellationRequested();
        var content = await RenderNodeAsync(place.Cell, baseUri, onNavigate, js, ct);
        if (content == null)
        {
            content = new TextBlock
            {
                Text = CollapseWs(GatherText(place.Cell)),
                TextWrapping = TextWrapping.Wrap
            };
        }
        var tb = content as TextBlock;
        if (place.Cell.Tag == "th" && tb != null) tb.SetFontWeight(FontWeight.SemiBold);
        if (place.Row < headerRows)
        {
            // Header cell (clamp row span inside header)
            int localRow = place.Row;
            int rs = Math.Min(place.RowSpan, Math.Max(1, headerRows - localRow));
            var b = addCell(headerGrid, localRow, place.Col, rs, place.ColSpan, content);
            try { b.Background = new SolidColorBrush(Colors.LightGray); } catch { }
        }
        else
        {
            // Body cell (offset rows)
            int localRow = place.Row - headerRows;
            int rs = Math.Min(place.RowSpan, Math.Max(1, (rowCount - headerRows) - localRow));
            addCell(bodyGrid, localRow, place.Col, rs, place.ColSpan, content);
        }
    }
    // Root container with sticky-like header + scrollable body
    var root = new Grid();
    root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    Grid.SetRow(headerGrid, 0);
    root.Children.Add(headerGrid);
    Control bodyHost;
    {
        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = bodyGrid
        };
        bodyHost = scroller;
    }
    Grid.SetRow(bodyHost, 1);
    root.Children.Add(bodyHost);
    // Synchronize column widths between header and body
    bool syncing = false;
    Action syncCols = () =>
    {
        if (syncing) return;
        syncing = true;
        try
        {
            for (int i = 0; i < maxCols; i++)
            {
                var target = Math.Max(headerGrid.ColumnDefinitions[i].ActualWidth, bodyGrid.ColumnDefinitions[i].ActualWidth);
                if (double.IsNaN(target) || target <= 0) continue;
                var hCur = headerGrid.ColumnDefinitions[i].Width;
                var bCur = bodyGrid.ColumnDefinitions[i].Width;
                bool changeH = hCur.IsAuto || Math.Abs(hCur.Value - target) > 0.5;
                bool changeB = bCur.IsAuto || Math.Abs(bCur.Value - target) > 0.5;
                if (changeH) headerGrid.ColumnDefinitions[i].Width = new GridLength(target);
                if (changeB) bodyGrid.ColumnDefinitions[i].Width = new GridLength(target);
            }
        }
        catch { }
        finally { syncing = false; }
    };
    headerGrid.Loaded += (s, e) => { try { syncCols(); } catch { } };
    bodyGrid.Loaded += (s, e) => { try { syncCols(); } catch { } };
    // Reduce risk of layout loops: listen to one SizeChanged and throttle via guard
    bodyGrid.SizeChanged += (s, e) => { try { syncCols(); } catch { } };
    return Finish(root, table);
}
// ---------- Blockquote ----------
private async Task<Control> MakeBlockquote(LiteElement n, Uri baseUri, Action<Uri> onNavigate, JavaScriptEngine js, CancellationToken ct)
{
    var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 8, 0, 8) };
    if (n.Children != null)
        foreach (var ch in n.Children)
        {
            ct.ThrowIfCancellationRequested();
            var elt = await RenderNodeAsync(ch, baseUri, onNavigate, js, ct);
            if (elt != null) stack.Children.Add(elt);
        }
    var adorn = new Border
    {
        BorderThickness = new Thickness(0, 0, 0, 0),
        Margin = new Thickness(0, 0, 0, 0),
        Child = stack
    };
    var wrap = new Grid { Margin = new Thickness(0, 4, 0, 4) };
    wrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
    wrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    var bar = new Border { Background = new SolidColorBrush(Color.FromArgb(80, 180, 180, 180)), Margin = new Thickness(0, 2, 8, 2) };
    Grid.SetColumn(bar, 0);
    Grid.SetColumn(adorn, 1);
    wrap.Children.Add(bar);
    wrap.Children.Add(adorn);
    return Finish(wrap, n);
}
// ---------- CSS hookup & wrapping ----------
private CssComputed TryGetCss(LiteElement n)
{
    if (ComputedStyles == null || n == null) return null;
    CssComputed css;
    if (ComputedStyles.TryGetValue(n, out css) && css != null) return css;
    try
    {
        var k = ComputedStyles.Keys.FirstOrDefault(k2 => k2 != null &&
            string.Equals(k2.Tag, n.Tag, StringComparison.OrdinalIgnoreCase));
        if (k != null && ComputedStyles.TryGetValue(k, out css) && css != null) return css;
    }
    catch { }
    return null;
}
// Back-compat shim for older call sites that still call ApplyBoxesAndText.
// It delegates to the newer Finish(...) pipeline so you keep all styling behavior.
private Control ApplyBoxesAndText(Control inner, LiteElement n)
{
    return Finish(inner, n);
}
private void ApplyComputedLayout(Control fe, CssComputed css)
{
    if (fe == null || css == null) return;

    // Box-Sizing Logic
    // Avalonia Width/Height is effectively "border-box" (total size).
    // CSS default is "content-box" (content only).
    // If CSS is content-box, we must ADD padding/border to the CSS width to get the Avalonia Width.
    // If CSS is border-box, we use the CSS width directly.
    bool isBorderBox = string.Equals(css.BoxSizing, "border-box", StringComparison.OrdinalIgnoreCase);

    if (css.Width.HasValue)
    {
        double w = css.Width.Value;
        if (!isBorderBox)
        {
            w += css.Padding.Left + css.Padding.Right + css.BorderThickness.Left + css.BorderThickness.Right;
        }
        fe.Width = Math.Max(0, w);
    }
    if (css.Height.HasValue)
    {
        double h = css.Height.Value;
        if (!isBorderBox)
        {
            h += css.Padding.Top + css.Padding.Bottom + css.BorderThickness.Top + css.BorderThickness.Bottom;
        }
        fe.Height = Math.Max(0, h);
    }

    // Min/Max also need adjustment if content-box
    if (css.MinWidth.HasValue)
    {
        double mw = css.MinWidth.Value;
        if (!isBorderBox) mw += css.Padding.Left + css.Padding.Right + css.BorderThickness.Left + css.BorderThickness.Right;
        fe.MinWidth = Math.Max(0, mw);
    }
    if (css.MinHeight.HasValue)
    {
        double mh = css.MinHeight.Value;
        if (!isBorderBox) mh += css.Padding.Top + css.Padding.Bottom + css.BorderThickness.Top + css.BorderThickness.Bottom;
        fe.MinHeight = Math.Max(0, mh);
    }
    if (css.MaxWidth.HasValue)
    {
        double mw = css.MaxWidth.Value;
        if (!isBorderBox) mw += css.Padding.Left + css.Padding.Right + css.BorderThickness.Left + css.BorderThickness.Right;
        fe.MaxWidth = Math.Max(0, mw);
    }
    if (css.MaxHeight.HasValue)
    {
        double mh = css.MaxHeight.Value;
        if (!isBorderBox) mh += css.Padding.Top + css.Padding.Bottom + css.BorderThickness.Top + css.BorderThickness.Bottom;
        fe.MaxHeight = Math.Max(0, mh);
    }

    if (css.Margin.Left != 0 || css.Margin.Top != 0 || css.Margin.Right != 0 || css.Margin.Bottom != 0)
    {
        fe.Margin = css.Margin;
    }
}
private Control Finish(Control content, LiteElement n)
{
    var css = TryGetCss(n);
    // Fallback: if we have a background color but the element didn't support it (e.g. TextBlock), wrap it
    // This runs if WrapWithBoxes didn't wrap it (e.g. css was null or background was missing in css but present in inline style)
    if (n.Attr != null && n.Attr.ContainsKey("style"))
    {
        var kv = ParseInlineStyle(n.Attr["style"]);
        string bg = null;
        if (kv.TryGetValue("background-color", out bg) || kv.TryGetValue("background", out bg))
        {
            var brush = TryBrush(bg);
            if (brush != null && !(content is Border) && !(content is Panel))
            {
                var b = new Border { Child = content, Background = brush };
                // Move margin to the wrapper so background doesn't cover the margin
                b.Margin = content.Margin;
                content.Margin = new Thickness(0);
                content = b;
            }
        }
    }
    try { RegisterDomVisualSafe(n, content); } catch { }
    // Minimal event bridge to JS for elements with explicit id
    string id = null;
    if (Js != null && n?.Attr != null && n.Attr.TryGetValue("id", out id) && !string.IsNullOrWhiteSpace(id))
    {
        // Taps -> "click"
        content.Tapped += (s, e) =>
        {
            try
            {
                var pos = e.GetPosition(content);
                // Raise synchronously so we can honor preventDefault
                bool cancel = false;
                try { cancel = Js.RaiseElementEventSync(id, "click", null, null, pos.X, pos.Y); } catch { cancel = false; }
                if (cancel) { TrySetHandled(e); return; }
            }
            catch { }
        };
        // Text input -> "input"
        var tb = content as TextBox;
        if (tb != null)
        {
            tb.TextChanged += (s, e) => { try { Js.RaiseElementEvent(id, "input", tb.Text ?? ""); } catch { } };
            tb.LostFocus += (s, e) => { try { Js.RaiseElementEvent(id, "change", tb.Text ?? ""); } catch { } };
        }
        // Combo / Listbox / CheckBox -> "change"
        var combo = content as ComboBox;
        if (combo != null) combo.SelectionChanged += (s, e) =>
        {
            try
            {
                var it = combo.SelectedItem;
                string txt = "";
                var feIt = it as Control;
                if (feIt != null)
                {
                    var cc = feIt as ContentControl;
                    if (cc != null)
                    {
                        var cs = cc.Content as string;
                        txt = cs ?? (cc.Content != null ? cc.Content.ToString() : "");
                    }
                    else
                    {
                        txt = feIt.ToString();
                    }
                }
                else if (it != null)
                {
                    txt = it.ToString();
                }
                Js.RaiseElementEvent(id, "change", txt);
            }
            catch { try { Js.RaiseElementEventById(id, "change"); } catch { } }
        };
        var list = content as ListBox;
        if (list != null) list.SelectionChanged += (s, e) =>
        {
            try
            {
                var it = list.SelectedItem;
                string txt = "";
                var feIt = it as Control;
                if (feIt != null)
                {
                    var cc = feIt as ContentControl;
                    if (cc != null)
                    {
                        var cs = cc.Content as string;
                        txt = cs ?? (cc.Content != null ? cc.Content.ToString() : "");
                    }
                    else
                    {
                        txt = feIt.ToString();
                    }
                }
                else if (it != null)
                {
                    txt = it.ToString();
                }
                Js.RaiseElementEvent(id, "change", txt);
            }
            catch { try { Js.RaiseElementEventById(id, "change"); } catch { } }
        };
        var chk = content as CheckBox;
        if (chk != null) chk.Click += (s, e) => { try { Js.RaiseElementEvent(id, "change", (chk.IsChecked == true).ToString().ToLower()); } catch { try { Js.RaiseElementEventById(id, "change"); } catch { } } };
    }
    // Inline on* attribute handlers (onclick/oninput/onchange)
    if (Js != null && n?.Attr != null)
    {
        string on;
        // onclick -> Tapped/Click
        if (n.Attr.TryGetValue("onclick", out on) && !string.IsNullOrWhiteSpace(on))
        {
            var btn = content as Button;
            var hbtn = content as Button;
            var code = PreprocessInlineHandler(on, id);
            if (btn != null) btn.Click += (s, e) => { try { Js.RunInline(code, new JsContext { BaseUri = _baseUriForResources }, "click", id); } catch { } };
            else if (hbtn != null) hbtn.Click += (s, e) => { try { Js.RunInline(code, new JsContext { BaseUri = _baseUriForResources }, "click", id); } catch { } };
            else content.Tapped += (s, e) => { try { Js.RunInline(code, new JsContext { BaseUri = _baseUriForResources }, "click", id); } catch { } };
        }
        // oninput -> TextChanged for TextBox
        if (n.Attr.TryGetValue("oninput", out on) && !string.IsNullOrWhiteSpace(on))
        {
            var tb = content as TextBox;
            var code = PreprocessInlineHandler(on, id);
            if (tb != null) tb.TextChanged += (s, e) => { try { Js.RunInline(code, new JsContext { BaseUri = _baseUriForResources }, "input", id); } catch { } };
        }
        // onchange -> LostFocus for TextBox; SelectionChanged for ComboBox/ListBox; Click for CheckBox/Radio
        if (n.Attr.TryGetValue("onchange", out on) && !string.IsNullOrWhiteSpace(on))
        {
            var code = PreprocessInlineHandler(on, id);
            var tb = content as TextBox; if (tb != null) tb.LostFocus += (s, e) => { try { Js.RunInline(code, new JsContext { BaseUri = _baseUriForResources }, "change", id); } catch { } };
            var combo = content as ComboBox; if (combo != null) combo.SelectionChanged += (s, e) => { try { Js.RunInline(code, new JsContext { BaseUri = _baseUriForResources }, "change", id); } catch { } };
            var list = content as ListBox; if (list != null) list.SelectionChanged += (s, e) => { try { Js.RunInline(code, new JsContext { BaseUri = _baseUriForResources }, "change", id); } catch { } };
            var chk = content as CheckBox; if (chk != null) chk.Click += (s, e) => { try { Js.RunInline(code, new JsContext { BaseUri = _baseUriForResources }, "change", id); } catch { } };
        }
    }
    return content;
}
private static string PreprocessInlineHandler(string code, string id)
{
    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(id)) return code;
    try
    {
        // Replace leading 'this.' tokens with document.getElementById('id').
        return Regex.Replace(code, @"\bthis\.", "document.getElementById('" + id.Replace("'", "\\'") + "').");
    }
    catch { return code; }
}
private void ApplyComputedStyles(Control fe, LiteElement n)
{
    if (ComputedStyles == null || n == null) return;
    CssComputed st = null;
    if (!ComputedStyles.TryGetValue(n, out st) || st == null)
    {
        try
        {
            var byTag = ComputedStyles.Keys.FirstOrDefault(k => k != null && string.Equals(k.Tag, n.Tag, StringComparison.OrdinalIgnoreCase));
            if (byTag != null) ComputedStyles.TryGetValue(byTag, out st);
        }
        catch { }
        if (st == null) return;
    }
    try
    {
        if (!string.IsNullOrEmpty(st.Display) && string.Equals(st.Display, "none", StringComparison.OrdinalIgnoreCase))
        {
            fe.IsVisible = false;
            return;
        }
    }
    catch { }
    if (st.Foreground != null)
    {
        var c1 = fe as Control; // keep as Control when needing Control-specific APIs via reflection
        var tb = fe as TextBlock;
        if (c1 != null) c1.SetForeground(st.Foreground);
        else if (tb != null) tb.SetForeground(st.Foreground);
    }
    if (st.FontSize.HasValue)
    {
        double px = st.FontSize.Value;
        var c2 = fe as Control;
        var tb2 = fe as TextBlock;
        if (c2 != null) { if (c2 is TextBlock tbSet) tbSet.SetFontSize(px); }
        else if (tb2 != null) tb2.SetFontSize(px);
    }
    if (st.FontWeight.HasValue)
    {
        var fw = st.FontWeight.Value;
        var c3 = fe as Control;
        var tb3 = fe as TextBlock;
        if (c3 != null) c3.SetFontWeight(fw);
        else if (tb3 != null) tb3.SetFontWeight(fw);
    }
    if (st.TextAlign.HasValue)
    {
        var tb = fe as TextBlock;
        if (tb != null) tb.TextAlignment = st.TextAlign.Value;
    }
    if (st.Background != null)
    {
        var border = fe as Border;
        if (border != null) border.Background = st.Background;
        else if (fe is Panel) ((Panel)fe).Background = st.Background;
    }
    else if (st.Map != null && st.Map.TryGetValue("background-image", out var bgImg) && !string.IsNullOrWhiteSpace(bgImg))
    {
        // Handle background-image from CSS if not already parsed into st.Background
        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(bgImg, @"url\(['""]?(?<url>[^'"")]+)['""]?\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var url = match.Groups["url"].Value;
                var uri = ResolveUri(url);
                if (uri != null)
                {
                    LoadIImageAsync(uri.AbsoluteUri).ContinueWith((Task<IImage> t) =>
                    {
                        if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                        {
                            var img = t.Result;
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                try
                                {
                                    if (img is IImageBrushSource src)
                                    {
                                        var brush = new ImageBrush { Source = src, Stretch = Stretch.UniformToFill };
                                        if (fe is Panel p) p.Background = brush;
                                        else if (fe is Border b) b.Background = brush;
                                    }
                                }
                                catch { }
                            });
                        }
                    });
                }
            }
        }
        catch { }
    }
    if (st.Opacity.HasValue)
    {
        fe.Opacity = st.Opacity.Value;
    }

    // New properties
    if (st.LineHeight.HasValue)
    {
        var tb = fe as TextBlock;
        if (tb != null) tb.LineHeight = st.LineHeight.Value;
    }
    if (!string.IsNullOrEmpty(st.WhiteSpace))
    {
        var tb = fe as TextBlock;
        if (tb != null)
        {
            var ws = st.WhiteSpace.Trim().ToLowerInvariant();
            if (ws == "nowrap") tb.TextWrapping = TextWrapping.NoWrap;
            else if (ws == "pre") tb.TextWrapping = TextWrapping.NoWrap; 
            else tb.TextWrapping = TextWrapping.Wrap;
        }
    }
    if (!string.IsNullOrEmpty(st.TextOverflow))
    {
        var tb = fe as TextBlock;
        if (tb != null)
        {
            var to = st.TextOverflow.Trim().ToLowerInvariant();
            if (to == "ellipsis") tb.TextTrimming = TextTrimming.CharacterEllipsis;
        }
    }
    if (!string.IsNullOrEmpty(st.Cursor))
    {
        var c = fe as Control;
        if (c != null)
        {
             var cur = st.Cursor.Trim().ToLowerInvariant();
             if (cur == "pointer") c.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
             else if (cur == "text") c.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Ibeam);
             else if (cur == "wait") c.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Wait);
             else if (cur == "crosshair") c.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Cross);
             else if (cur == "help") c.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Help);
        }
    }
    if (!string.IsNullOrEmpty(st.VerticalAlign))
    {
        var va = st.VerticalAlign.Trim().ToLowerInvariant();
        VerticalAlignment? align = null;
        if (va == "top") align = VerticalAlignment.Top;
        else if (va == "bottom") align = VerticalAlignment.Bottom;
        else if (va == "middle") align = VerticalAlignment.Center;
        
        if (align.HasValue)
        {
            fe.VerticalAlignment = align.Value;
        }
    }
}
private void ApplyInlineStyles(Control fe, LiteElement n)
{
    if (n?.Attr == null) return;
    string style;
    if (!n.Attr.TryGetValue("style", out style) || string.IsNullOrWhiteSpace(style)) return;
    var kv = ParseInlineStyle(style);
    foreach (var pair in kv)
    {
        var key = pair.Key;
        var val = pair.Value;
        if (key == "color")
        {
            var brush = TryBrush(val);
            var c1 = fe as Control;
            var tb = fe as TextBlock;
            if (c1 != null && brush != null) c1.SetForeground(brush);
            else if (tb != null && brush != null) tb.SetForeground(brush);
        }
        else if (key == "background-image" || (key == "background" && val.Contains("url(")))
        {
            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(val, @"url\(['""]?(?<url>[^'"")]+)['""]?\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var url = match.Groups["url"].Value;
                    var uri = ResolveUri(url);
                    if (uri != null)
                    {
                        LoadIImageAsync(uri.AbsoluteUri).ContinueWith((Task<IImage> t) =>
                        {
                            if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                            {
                                var img = t.Result;
                                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                {
                                    try
                                    {
                                        // Cast to IImageBrushSource as required by Avalonia 11+
                                        if (img is IImageBrushSource src)
                                        {
                                            var brush = new ImageBrush { Source = src, Stretch = Stretch.UniformToFill };
                                            if (fe is Panel p) p.Background = brush;
                                            else if (fe is Border b) b.Background = brush;
                                            else if (fe is Decorator d) { /* Decorator doesn't have Background */ }
                                            else if (fe is ContentControl cc) cc.Background = brush;
                                        }
                                    }
                                    catch { }
                                });
                            }
                        });
                    }
                }
            }
            catch { }
        }
        else if (key == "font-size")
        {
            double px;
            if (TryPx(val, out px))
            {
                var c2 = fe as Control;
                var tb2 = fe as TextBlock;
                if (c2 != null) { if (c2 is TextBlock tbSet) tbSet.SetFontSize(px); }
                else if (tb2 != null) tb2.SetFontSize(px);
            }
        }
        else if (key == "font-weight")
        {
            bool bold = string.Equals((val ?? "").Trim(), "bold", StringComparison.OrdinalIgnoreCase);
            var c3 = fe as Control;
            var tb3 = fe as TextBlock;
            if (c3 != null && bold) c3.SetFontWeight(FontWeight.Bold);
            else if (tb3 != null && bold) tb3.SetFontWeight(FontWeight.Bold);
        }
        else if (key == "font-style")
        {
            ApplyFontStyle(fe, val);
        }
        else if (key == "margin")
        {
            Thickness th;
            if (TryThickness(val, out th)) fe.Margin = th;
        }
        else if (key == "margin-top")
        {
            double mt;
            if (TryPx(val, out mt)) fe.Margin = new Thickness(fe.Margin.Left, mt, fe.Margin.Right, fe.Margin.Bottom);
        }
        else if (key == "margin-bottom")
        {
            double mb;
            if (TryPx(val, out mb)) fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top, fe.Margin.Right, mb);
        }
        else if (key == "margin-left")
        {
            double ml;
            if (TryPx(val, out ml)) fe.Margin = new Thickness(ml, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
        }
        else if (key == "margin-right")
        {
            double mr;
            if (TryPx(val, out mr)) fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top, mr, fe.Margin.Bottom);
        }
        else if (key == "padding")
        {
            Thickness th;
            if (TryThickness(val, out th))
            {
                // Clamp padding to non-negative
                th = new Thickness(Math.Max(0, th.Left), Math.Max(0, th.Top), Math.Max(0, th.Right), Math.Max(0, th.Bottom));
                var ctrl = fe as Control;
                if (ctrl is Decorator dec) dec.Padding = th; else if (ctrl is Border bor) bor.Padding = th;
                else
                {
                    var border = fe as Border;
                    if (border != null) border.Padding = th;
                }
            }
        }
        else if (key == "padding-top")
        {
            double pt;
            if (TryPx(val, out pt))
            {
                var ctrl = fe as Control;
                if (ctrl != null)
                {
                    var pad = (ctrl as Decorator)?.Padding ?? (ctrl as Border)?.Padding ?? new Thickness(0);
                    if (ctrl is Decorator dec2) dec2.Padding = new Thickness(pad.Left, pt, pad.Right, pad.Bottom);
                }
                else
                {
                    var border = fe as Border;
                    if (border != null)
                    {
                        var pad = border.Padding;
                        border.Padding = new Thickness(pad.Left, pt, pad.Right, pad.Bottom);
                    }
                }
            }
        }
        else if (key == "padding-bottom")
        {
            double pb;
            if (TryPx(val, out pb))
            {
                var ctrl = fe as Control;
                if (ctrl != null)
                {
                    var pad = (ctrl as Decorator)?.Padding ?? (ctrl as Border)?.Padding ?? new Thickness(0);
                    if (ctrl is Decorator dec2) dec2.Padding = new Thickness(pad.Left, pad.Top, pad.Right, pb);
                }
                else
                {
                    var border = fe as Border;
                    if (border != null)
                    {
                        var pad = border.Padding;
                        border.Padding = new Thickness(pad.Left, pad.Top, pad.Right, pb);
                    }
                }
            }
        }
        else if (key == "padding-left")
        {
            double pl;
            if (TryPx(val, out pl))
            {
                var ctrl = fe as Control;
                if (ctrl != null)
                {
                    var pad = (ctrl as Decorator)?.Padding ?? (ctrl as Border)?.Padding ?? new Thickness(0);
                    if (ctrl is Decorator dec2) dec2.Padding = new Thickness(pl, pad.Top, pad.Right, pad.Bottom);
                }
                else
                {
                    var border = fe as Border;
                    if (border != null)
                    {
                        var pad = border.Padding;
                        border.Padding = new Thickness(pl, pad.Top, pad.Right, pad.Bottom);
                    }
                }
            }
        }
        else if (key == "padding-right")
        {
            double pr;
            if (TryPx(val, out pr))
            {
                var ctrl = fe as Control;
                if (ctrl != null)
                {
                    var pad = (ctrl as Decorator)?.Padding ?? (ctrl as Border)?.Padding ?? new Thickness(0);
                    if (ctrl is Decorator dec2) dec2.Padding = new Thickness(pad.Left, pad.Top, pr, pad.Bottom);
                }
                else
                {
                    var border = fe as Border;
                    if (border != null)
                    {
                        var pad = border.Padding;
                        border.Padding = new Thickness(pad.Left, pad.Top, pr, pad.Bottom);
                    }
                }
            }
        }
        else if (key == "text-align")
        {
            var tb = fe as TextBlock;
            if (tb != null)
            {
                var v = (val ?? "").Trim().ToLowerInvariant();
                if (v == "center") tb.TextAlignment = TextAlignment.Center;
                else if (v == "right") tb.TextAlignment = TextAlignment.Right;
                else if (v == "justify") tb.TextAlignment = TextAlignment.Justify;
                else tb.TextAlignment = TextAlignment.Left;
            }
        }
        else if (key == "background" || key == "background-color")
        {
            var brush = TryBrush(val);
            var border = fe as Border;
            if (border != null && brush != null) border.Background = brush;
            else if (fe is Panel && brush != null) ((Panel)fe).Background = brush;
        }
        else if (key == "line-height")
        {
            var tb = fe as TextBlock;
            double px;
            if (tb != null && TryPx(val, out px)) { tb.LineHeight = px; }
        }
        else if (key == "text-transform")
        {
            var tb = fe as TextBlock;
            if (tb != null)
            {
                var v = (val ?? "").Trim().ToLowerInvariant();
                var t = tb.Text ?? "";
                if (v == "uppercase") tb.Text = t.ToUpperInvariant();
                else if (v == "lowercase") tb.Text = t.ToLowerInvariant();
                else if (v == "capitalize") tb.Text = CapitalizeWords(t);
            }
        }
        else if (key == "text-decoration")
        {
            ApplyTextDecorations(fe, val);
        }
        else if (key == "letter-spacing")
        {
            ApplyLetterSpacing(fe, val);
        }
        else if (key == "opacity")
        {
            ApplyOpacity(fe, val);
        }
        else if (key == "display")
        {
            var v = (val ?? "").Trim().ToLowerInvariant();
            if (v == "none") fe.IsVisible = false;
        }
        else if (key == "border")
        {
            ApplyBorderShorthand(fe, val);
        }
        else if (key == "border-color")
        {
            var brush = TryBrush(val);
            if (brush != null)
            {
                var border = fe as Border;
                if (border != null) border.SetBorderBrush(brush);
                var ctrl = fe as Control;
                if (ctrl != null) ctrl.SetBorderBrush(brush);
            }
        }
        else if (key == "border-width")
        {
            Thickness th;
            if (TryThickness(val, out th))
            {
                var border = fe as Border;
                if (border != null) border.SetBorderThickness(th);
                var ctrl = fe as Control;
                if (ctrl != null) ctrl.SetBorderThickness(th);
            }
        }
        else if (key == "border-style")
        {
            var v = (val ?? "").Trim().ToLowerInvariant();
            if (v == "none" || v == "hidden")
            {
                var border = fe as Border;
                if (border != null) border.SetBorderThickness(new Thickness(0, 0, 0, 0));
                var ctrl = fe as Control;
                if (ctrl != null) ctrl.SetBorderThickness(new Thickness(0, 0, 0, 0));
            }
        }
        else if (key == "border-radius")
        {
            var border = fe as Border;
            if (border != null)
            {
                CornerRadius cr;
                if (TryCornerRadius(val, out cr)) border.SetCornerRadius(cr);
            }
        }
        else if (key == "border-top-left-radius")
        {
            var border = fe as Border;
            if (border != null)
            {
                double px;
                if (TryPx(val, out px))
                {
                    var cr = border.CornerRadius;
                    // cr.TopLeft = px; // Read-only
                    border.SetCornerRadius(cr);
                }
            }
        }
        else if (key == "border-top-right-radius")
        {
            var border = fe as Border;
            if (border != null)
            {
                double px;
                if (TryPx(val, out px))
                {
                    var cr = border.CornerRadius;
                    // cr.TopRight = px; // Read-only
                    border.SetCornerRadius(cr);
                }
            }
        }
        else if (key == "border-bottom-right-radius")
        {
            var border = fe as Border;
            if (border != null)
            {
                double px;
                if (TryPx(val, out px))
                {
                    var cr = border.CornerRadius;
                    // cr.BottomRight = px; // Read-only
                    border.SetCornerRadius(cr);
                }
            }
        }
        else if (key == "border-bottom-left-radius")
        {
            var border = fe as Border;
            if (border != null)
            {
                double px;
                if (TryPx(val, out px))
                {
                    var cr = border.CornerRadius;
                    // cr.BottomLeft = px; // Read-only
                    border.SetCornerRadius(cr);
                }
            }
        }
        else if (key == "background-image")
        {
            var m = Regex.Match(val ?? "", @"url\(['""]?(?<u>[^)'""]+)['""]?\)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                // Placeholder for background image logic
            }
        }
        else if (key == "white-space")
        {
            var tb = fe as TextBlock;
            if (tb != null)
            {
                var ws = (val ?? "").Trim().ToLowerInvariant();
                if (ws == "nowrap") tb.TextWrapping = TextWrapping.NoWrap;
                else if (ws == "pre") tb.TextWrapping = TextWrapping.NoWrap;
                else tb.TextWrapping = TextWrapping.Wrap;
            }
        }
        else if (key == "text-overflow")
        {
            var tb = fe as TextBlock;
            if (tb != null)
            {
                var to = (val ?? "").Trim().ToLowerInvariant();
                if (to == "ellipsis") tb.TextTrimming = TextTrimming.CharacterEllipsis;
            }
        }
        else if (key == "cursor")
        {
             var cur = (val ?? "").Trim().ToLowerInvariant();
             if (cur == "pointer") fe.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
             else if (cur == "text") fe.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Ibeam);
             else if (cur == "wait") fe.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Wait);
             else if (cur == "crosshair") fe.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Cross);
             else if (cur == "help") fe.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Help);
        }
        else if (key == "vertical-align")
        {
            var va = (val ?? "").Trim().ToLowerInvariant();
            if (va == "top") fe.VerticalAlignment = VerticalAlignment.Top;
            else if (va == "bottom") fe.VerticalAlignment = VerticalAlignment.Bottom;
            else if (va == "middle") fe.VerticalAlignment = VerticalAlignment.Center;
        }
    }
}
private static Control ApplyInlineOverflow(Control content, LiteElement n)
{
    if (content == null || n == null || n.Attr == null) return content;
    string style;
    if (!n.Attr.TryGetValue("style", out style) || string.IsNullOrWhiteSpace(style)) return content;
    var kv = ParseInlineStyle(style);
    string ov, ovx, ovy;
    kv.TryGetValue("overflow", out ov);
    kv.TryGetValue("overflow-x", out ovx);
    kv.TryGetValue("overflow-y", out ovy);
    Func<string, string> normalize = s => string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim().ToLowerInvariant();
    var overflow = normalize(ov);
    var overflowX = normalize(ovx);
    var overflowY = normalize(ovy);
    bool anyAxisScroll = (overflowX == "auto" || overflowX == "scroll") ||
                         (overflowY == "auto" || overflowY == "scroll");
    if (overflow == "hidden" || (overflowX == "hidden" && !anyAxisScroll) || (overflowY == "hidden" && !anyAxisScroll))
    {
        Action<Control> applyClip = fe =>
        {
            if (fe == null) return;
            try
            {
                var rect = new Rect(0, 0, fe.Width /* .Bounds not available */, (fe.Height));
                fe.Clip = new RectangleGeometry { Rect = rect };
            }
            catch { }
        };
        var target = content;
        applyClip(target);
        target.SizeChanged += (s, e) => applyClip(s as Control);
        return target;
    }
    else if (overflow == "auto" || overflow == "scroll" || anyAxisScroll)
    {
        ScrollViewer scroller;
        var existing = content as ScrollViewer;
        if (existing != null)
        {
            scroller = existing;
        }
        else
        {
            scroller = new ScrollViewer { Content = content };
        }
        scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        if (!string.IsNullOrEmpty(overflowX))
        {
            if (overflowX == "hidden") scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            else if (overflowX == "scroll" || overflowX == "auto") scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        if (!string.IsNullOrEmpty(overflowY))
        {
            if (overflowY == "hidden") scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            else if (overflowY == "scroll" || overflowY == "auto") scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        return scroller;
    }
    return content;
}
private static bool TryPx(string s, out double px)
{
    px = 0;
    if (string.IsNullOrWhiteSpace(s)) return false;
    s = s.Trim().ToLowerInvariant();
    if (s.EndsWith("px")) s = s.Substring(0, s.Length - 2);
    double v;
    if (double.TryParse(s, out v)) { px = v; return true; }
    return false;
}
private static bool TryThickness(string s, out Thickness th)
{
    th = new Thickness(0, 0, 0, 0);
    if (string.IsNullOrWhiteSpace(s)) return false;
    var parts = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 1)
    {
        double v; TryPx(parts[0], out v);
        th = new Thickness(v); return true;
    }
    if (parts.Length == 2)
    {
        double y; TryPx(parts[0], out y);
        double x; TryPx(parts[1], out x);
        th = new Thickness(x, y, x, y); return true;
    }
    if (parts.Length == 3)
    {
        double t; TryPx(parts[0], out t);
        double x; TryPx(parts[1], out x);
        double b; TryPx(parts[2], out b);
        th = new Thickness(x, t, x, b); return true;
    }
    if (parts.Length >= 4)
    {
        double top; TryPx(parts[0], out top);
        double right; TryPx(parts[1], out right);
        double bottom; TryPx(parts[2], out bottom);
        double left; TryPx(parts[3], out left);
        th = new Thickness(left, top, right, bottom); return true;
    }
    return false;
}
private static bool TryCornerRadius(string s, out CornerRadius cr)
{
    cr = new CornerRadius(0);
    if (string.IsNullOrWhiteSpace(s)) return false;
    var parts = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) return false;
    double tl;
    if (!TryPx(parts[0], out tl)) return false;
    double tr, br, bl;
    if (parts.Length == 1)
    {
        tr = br = bl = tl;
    }
    else if (parts.Length == 2)
    {
        if (!TryPx(parts[1], out tr)) return false;
        br = tl;
        bl = tr;
    }
    else if (parts.Length == 3)
    {
        if (!TryPx(parts[1], out tr)) return false;
        if (!TryPx(parts[2], out br)) return false;
        bl = tr;
    }
    else
    {
        if (!TryPx(parts[1], out tr)) return false;
        if (!TryPx(parts[2], out br)) return false;
        if (!TryPx(parts[3], out bl)) return false;
    }
    cr = new CornerRadius(tl, tr, br, bl);
    return true;
}
private static void ApplyTextDecorations(Control fe, string value)
{
    if (!true /* SupportsTextDecorations */ || string.IsNullOrWhiteSpace(value)) return;
    var tb = fe as TextBlock;
    if (tb == null) return;
    var tokens = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
    if (tokens.Length == 0) return;
    bool underline = false;
    bool strike = false;
    bool explicitNone = false;
    foreach (var token in tokens)
    {
        var part = token.Trim().ToLowerInvariant();
        if (part == "none")
        {
            explicitNone = true;
            break;
        }
        else if (part == "underline") underline = true;
        else if (part == "line-through") strike = true;
    }
    if (explicitNone)
    {
        tb.TextDecorations = null;
        return;
    }
    var col = new TextDecorationCollection();
    if (underline) col.AddRange(TextDecorations.Underline);
    if (strike) col.AddRange(TextDecorations.Strikethrough);
    
    if (col.Count > 0) tb.TextDecorations = col;
}
private static void ApplyLetterSpacing(Control fe, string value)
{
    if (string.IsNullOrWhiteSpace(value)) return;
            double spacingValue;
            double fontSize = 0;
            var tb = fe as TextBlock;
            if (tb != null) fontSize = tb.FontSize;
            var ctrl = fe as Control;
            if (ctrl != null && fontSize <= 0) fontSize = ((ctrl as TextBlock)?.FontSize ?? (ctrl as TemplatedControl)?.FontSize ?? 0);
            if (fontSize <= 0) fontSize = 14;
            bool handled = false;
            var trimmed = value.Trim().ToLowerInvariant();
            if (TryPx(trimmed, out spacingValue))
            {
                spacingValue = (spacingValue / Math.Max(1, fontSize)) * 1000.0;
                handled = true;
            }
            else if (trimmed.EndsWith("em"))
            {
                double em;
                if (double.TryParse(trimmed.Substring(0, trimmed.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out em))
                {
                    spacingValue = em * 1000.0;
                    handled = true;
                }
            }
            else if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out spacingValue))
            {
                spacingValue = (spacingValue / Math.Max(1, fontSize)) * 1000.0;
                handled = true;
            }
            if (!handled) return;
            var characterSpacing = (int)Math.Round(spacingValue);
            if (tb != null) tb.LetterSpacing = characterSpacing;
        }
        private static void ApplyFontStyle(Control fe, string value)
{
    if (string.IsNullOrWhiteSpace(value)) return;
    var normalized = value.Trim().ToLowerInvariant();
    var fontStyle = (normalized == "italic" || normalized == "oblique") ? FontStyle.Italic : FontStyle.Normal;
    var ctrl = fe as Control;
    if (ctrl != null) ctrl.SetFontStyle(fontStyle);
    var tb = fe as TextBlock;
    if (tb != null) tb.SetFontStyle(fontStyle);
}
private static void ApplyOpacity(Control fe, string value)
{
    if (string.IsNullOrWhiteSpace(value) || fe == null) return;
    double parsed;
    if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return;
    if (parsed < 0) parsed = 0;
    if (parsed > 1) parsed = 1;
    fe.Opacity = parsed;
}
private static void ApplyBorderShorthand(Control fe, string value)
{
    if (fe == null || string.IsNullOrWhiteSpace(value)) return;
    var border = fe as Border;
    var ctrl = fe as Control;
    if (border == null && ctrl == null) return;
    double width = double.NaN;
    IBrush brush = null;
    var tokens = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
    foreach (var raw in tokens)
    {
        var token = raw.Trim();
        if (token.Length == 0) continue;
        double px;
        if (TryPx(token, out px))
        {
            width = px;
            continue;
        }
        var lower = token.ToLowerInvariant();
        if (lower == "thin") { width = 1; continue; }
        if (lower == "medium") { width = 2; continue; }
        if (lower == "thick") { width = 3; continue; }
        if (lower == "none" || lower == "hidden") { width = 0; continue; }
        if (lower == "solid" || lower == "dashed" || lower == "dotted" || lower == "double" ||
            lower == "groove" || lower == "ridge" || lower == "inset" || lower == "outset")
        {
            continue;
        }
        var candidate = TryBrush(token);
        if (candidate != null) brush = candidate;
    }
    if (!double.IsNaN(width))
    {
        var thickness = new Thickness(width);
        if (border != null) border.SetBorderThickness(thickness);
        if (ctrl != null) ctrl.SetBorderThickness(thickness);
    }
    if (brush != null)
    {
        if (border != null) border.SetBorderBrush(brush);
        if (ctrl != null) ctrl.SetBorderBrush(brush);
    }
}
private static IBrush TryBrush(string css)
{
    try
    {
        var c = CssParser.ParseColor(css);
        return c.HasValue ? new SolidColorBrush(c.Value) : null;
    }
    catch { return null; }
}
private static Color FromHex(string hex)
{
    hex = hex.TrimStart('#');
    if (hex.Length == 3)
    {
        // #rgb
        byte r = Convert.ToByte(new string(hex[0], 2), 16);
        byte g = Convert.ToByte(new string(hex[1], 2), 16);
        byte b = Convert.ToByte(new string(hex[2], 2), 16);
        return Color.FromArgb(255, r, g, b);
    }
    if (hex.Length == 4)
    {
        // #argb
        byte a = Convert.ToByte(new string(hex[0], 2), 16);
        byte r = Convert.ToByte(new string(hex[1], 2), 16);
        byte g = Convert.ToByte(new string(hex[2], 2), 16);
        byte b = Convert.ToByte(new string(hex[3], 2), 16);
        return Color.FromArgb(a, r, g, b);
    }
    if (hex.Length == 6)
    {
        byte r = Convert.ToByte(hex.Substring(0, 2), 16);
        byte g = Convert.ToByte(hex.Substring(2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(4, 2), 16);
        return Color.FromArgb(255, r, g, b);
    }
    if (hex.Length == 8)
    {
        byte a = Convert.ToByte(hex.Substring(0, 2), 16);
        byte r = Convert.ToByte(hex.Substring(2, 2), 16);
        byte g = Convert.ToByte(hex.Substring(4, 2), 16);
        byte b = Convert.ToByte(hex.Substring(6, 2), 16);
        return Color.FromArgb(a, r, g, b);
    }
    return Colors.Black;
}
private static Dictionary<string, string> ParseInlineStyle(string style)
{
    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var parts = style.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
    for (int i = 0; i < parts.Length; i++)
    {
        var part = parts[i];
        var kv = part.Split(new[] { ':' }, 2);
        if (kv.Length == 2)
        {
            var k = kv[0].Trim().ToLowerInvariant();
            var v = kv[1].Trim();
            if (!dict.ContainsKey(k)) dict[k] = v;
        }
    }
    return dict;
}
// ---------- Visibility / search helpers ----------
// Debug: allow rendering nodes even if inline CSS hides them
private static bool DebugShowHidden = true;
private static bool IsHidden(LiteElement n)
{
    if (DebugShowHidden) return false;
    if (n == null || n.Attr == null) return false;
    string v;
    if (n.Attr.ContainsKey("hidden")) return true;
    if (n.Attr.TryGetValue("aria-hidden", out v) && v != null &&
        v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
    if (n.Attr.TryGetValue("style", out v) && !string.IsNullOrWhiteSpace(v))
    {
        var s = v.ToLowerInvariant();
        if (s.Contains("display:none") || s.Contains("visibility:hidden")) return true;
    }
    return false;
}
private static bool IsSearchHost(Uri u)
{
    if (u == null) return false;
    var h = u.Host.ToLowerInvariant();
    return h.Contains("google.") || h.Contains("bing.") || h.Contains("duckduckgo.");
}
private Control MakeSyntheticSearch(Uri baseUri, Action<Uri> onNavigate)
{
    if (baseUri == null || onNavigate == null) return null;
    string endpoint = null;
    var hostName = baseUri.Host.ToLowerInvariant();
    if (hostName.Contains("google.")) endpoint = baseUri.Scheme + "://www.google.com/search";
    else if (hostName.Contains("bing.")) endpoint = baseUri.Scheme + "://www.bing.com/search";
    else if (hostName.Contains("duckduckgo.")) endpoint = baseUri.Scheme + "://duckduckgo.com/";
    else return null;
    // Centered, visible synthetic search box
    var container = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 12) };
    var vstack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
    var caption = new TextBlock { Text = "Basic search (JS disabled)", Opacity = 0.7, Margin = new Thickness(0, 0, 0, 6) };
    vstack.Children.Add(caption);
    var panel = new StackPanel { Orientation = Orientation.Horizontal };
    var box = new Border
    {
        Background = new SolidColorBrush(Colors.White),
        BorderBrush = new SolidColorBrush(Colors.Gray),
        BorderThickness = new Thickness(2, 2, 2, 2),
        Padding = new Thickness(8, 8, 8, 8),
        Child = panel
    };
    vstack.Children.Add(box);
    container.Children.Add(vstack);
    var tb = new TextBox { Width = 260, Margin = new Thickness(0, 0, 8, 0), Watermark = "Search the web" };
    // Make the input clearly visible even without focus
    try { tb.Background = new SolidColorBrush(Colors.White); } catch { }
    try { tb.SetForeground(new SolidColorBrush(Colors.Black)); } catch { }
    try { tb.SetBorderBrush(new SolidColorBrush(Colors.Gray)); tb.SetBorderThickness(new Thickness(1, 1, 1, 1)); } catch { }
    try { tb.Height = 36; } catch { }
    // Focus automatically so user sees caret immediately
    tb.Loaded += (s, e) => { try { tb.Focus(); } catch { } };
    var btn = new Button { Content = "Search", Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
    btn.Click += (s, e) =>
    {
        var q = Uri.EscapeDataString(tb.Text ?? "");
        Uri nav;
        char sep = endpoint.Contains("?") ? '&' : '?';
        if (hostName.Contains("duckduckgo."))
        {
            nav = new Uri(endpoint + sep + "q=" + q);
        }
        else if (hostName.Contains("google."))
        {
            nav = new Uri(endpoint + sep + "q=" + q + "&hl=en");
        }
        else
        {
            nav = new Uri(endpoint + "?q=" + q);
        }
        onNavigate(nav);
    };
    panel.Children.Add(tb);
    panel.Children.Add(btn);
    return container;
}
private static bool LooksLikeSearchInput(LiteElement input)
{
    if (input == null || input.Attr == null || input.Tag != "input") return false;
    string type; input.Attr.TryGetValue("type", out type);
    type = (type ?? "text").Trim().ToLowerInvariant();
    if (!(type == "text" || type == "search" || type == "url" || type == "email" || type == "password"))
        return false;
    string name; input.Attr.TryGetValue("name", out name);
    if (string.IsNullOrWhiteSpace(name)) return false;
    var key = name.Trim().ToLowerInvariant();
    return key == "q" || key == "query" || key == "search" || key == "s" || key == "keywords" || key == "term" || key == "text";
}
private static bool IsSearchForm(LiteElement form)
{
    if (form == null || form.Tag != "form") return false;
    foreach (var d in form.Descendants())
        if (LooksLikeSearchInput(d)) return true;
    return false;
}
private bool _renderedOneSearchForm = false;
// ---------- OpenGraph preview ----------
private Control CreateOpenGraphPreview(LiteElement doc, Uri baseUri, Action<Uri> onNavigate)
{
    string title = GetMeta(doc, "og:title");
    string desc = GetMeta(doc, "og:description");
    string img = GetMeta(doc, "og:image");
    string url = GetMeta(doc, "og:url");
    if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(desc) && string.IsNullOrWhiteSpace(img))
        return null;
    var wrap = new Grid { Margin = new Thickness(8, 12, 8, 12) };
    wrap.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    wrap.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    var card = new Border
    {
        Padding = new Thickness(12, 12, 12, 12),
        BorderThickness = new Thickness(1, 1, 1, 1),
        BorderBrush = new SolidColorBrush(Colors.DimGray),
        Background = new SolidColorBrush(Color.FromArgb(12, 255, 255, 255)),
        CornerRadius = new CornerRadius(4)
    };
    var stack = new StackPanel { Orientation = Orientation.Vertical };
    if (!string.IsNullOrWhiteSpace(title))
        stack.Children.Add(new TextBlock { Text = title.Trim(), FontSize = 18, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap });
    if (!string.IsNullOrWhiteSpace(desc))
        stack.Children.Add(new TextBlock { Text = desc.Trim(), Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
    if (!string.IsNullOrWhiteSpace(img))
    {
        var u = ResolveUri(baseUri, img);
        var image = new Image { Stretch = Stretch.Uniform, MaxHeight = 280, MaxWidth = 480 };
        if (u != null)
        {
            try { /* image.Source = new Bitmap(u); */ } catch { }
        }
        stack.Children.Add(image);
    }
    if (!string.IsNullOrWhiteSpace(url))
    {
        var abs = ResolveUri(baseUri, url);
        var btn = new Button
        {
            Content = "Open",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        };
        if (abs != null && onNavigate != null)
            btn.Click += (s, e) => onNavigate(abs);
        stack.Children.Add(btn);
    }
    card.Child = stack;
    Grid.SetRow(card, 0);
    wrap.Children.Add(card);
    var previewMessage = "This page requires JavaScript. Showing preview.";
    if (Js != null && Js.AllowExternalScripts)
    {
        previewMessage = "This page relies on advanced scripting and may not render fully. Showing preview.";
    }
    var lbl = new TextBlock
    {
        Text = previewMessage,
        Margin = new Thickness(0, 8, 0, 0),
        Opacity = 0.7
    };
    Grid.SetRow(lbl, 1);
    wrap.Children.Add(lbl);
    return wrap;
}
private static string GetMeta(LiteElement root, string propertyValue)
{
    try
    {
        var m = root.Descendants().FirstOrDefault(n =>
            n.Tag == "meta" && n.Attr != null &&
            ((n.Attr.ContainsKey("property") && string.Equals(n.Attr["property"], propertyValue, StringComparison.OrdinalIgnoreCase)) ||
             (n.Attr.ContainsKey("name") && string.Equals(n.Attr["name"], propertyValue, StringComparison.OrdinalIgnoreCase)))
            && n.Attr.ContainsKey("content"));
        if (m != null) return m.Attr["content"];
    }
    catch { }
    return null;
}
// ---------- Material Icons ----------
private static bool IsMaterialIcon(LiteElement n)
{
    if (n == null || n.Attr == null) return false;
    string cls; if (!n.Attr.TryGetValue("class", out cls) || string.IsNullOrWhiteSpace(cls)) return false;
    return cls.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
              .Any(c => string.Equals(c, "material-icons", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(c, "material-symbols-outlined", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(c, "material-symbols-rounded", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(c, "material-symbols-sharp", StringComparison.OrdinalIgnoreCase));
}
private Control MakeMaterialIcon(LiteElement n)
{
    var name = (CollapseWs(GatherText(n)) ?? "").Trim().ToLowerInvariant();
    string glyph;
    switch (name)
    {
        case "menu": glyph = "\u2261"; break;                 // =
        case "search": glyph = "\uD83D\uDD0D"; break;         // ??
        case "clear":
        case "close": glyph = "\u2715"; break;                 // ?
        case "more_vert": glyph = "\u22EE"; break;            // ?
        case "more_horiz": glyph = "\u22EF"; break;           // ?
        case "chevron_left": glyph = "\u2039"; break;         // ?
        case "chevron_right": glyph = "\u203A"; break;        // ?
        case "arrow_back": glyph = "\u2190"; break;           // ?
        case "arrow_forward": glyph = "\u2192"; break;        // ?
        case "play_arrow": glyph = "\u25B6"; break;           // ?
        case "pause": glyph = "\u275A\u275A"; break;          // ??
        case "home": glyph = "\u2302"; break;                 // ?
        case "share": glyph = "\u21AA"; break;                // ?
        default: glyph = name; break;                          // show token if unknown
    }
    var tb = new TextBlock
    {
        Text = glyph,
        FontSize = 20,
        Margin = new Thickness(0, 2, 0, 2),
        VerticalAlignment = VerticalAlignment.Center
    };
    ApplyComputedStyles(tb, n);
    ApplyInlineStyles(tb, n);
    return tb;
}
private static string CapitalizeWords(string s)
{
    if (string.IsNullOrEmpty(s)) return s;
    var chars = s.ToCharArray();
    bool capNext = true;
    for (int i = 0; i < chars.Length; i++)
    {
        char c = chars[i];
        if (char.IsLetter(c))
        {
            if (capNext) chars[i] = char.ToUpperInvariant(c);
            capNext = false;
        }
        else
        {
            capNext = char.IsWhiteSpace(c) || c == '-' || c == '_' || c == '/';
        }
    }
    return new string(chars);
}
// Wrapper: ensure all UI-affinitized image object creation occurs on the UI thread.
private Task<IImage> LoadIImageAsync(string abs, int decodeWidthHint = 0)
{
    try { System.IO.File.AppendAllText("debug_log.txt", $"[LoadIImageAsync] {abs} Thread:{System.Threading.Thread.CurrentThread.ManagedThreadId}\r\n"); } catch { }
    // Run directly to avoid deadlock. Bitmap(Stream) should be safe on background thread.
    return LoadIImageUiThreadAsync(new Uri(abs), decodeWidthHint);
}

// Performs actual image source construction (must run on UI thread).
private async Task<IImage> LoadIImageUiThreadAsync(Uri abs, int decodeWidthHint)
{
    if (abs == null) return null;
    
    // Raster path
    try
    {
        if (ImageLoader != null)
        {
            using (var s = await ImageLoader(abs))
            {
                if (s != null)
                {
                    if (decodeWidthHint > 0)
                    {
                        return Bitmap.DecodeToWidth(s, decodeWidthHint);
                    }
                    return new Bitmap(s);
                }
            }
        }
        else
        {
            // Fallback: load directly if no loader provided (e.g. http/file)
            using (var client = new System.Net.Http.HttpClient())
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserSettings.GetUserAgentString(BrowserSettings.Instance.SelectedUserAgent));
                try 
                {
                    System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;
                    try { System.IO.File.AppendAllText("debug_log.txt", $"[ImageLoadStart] {abs}\r\n"); } catch { }
                    
                    var bytes = await client.GetByteArrayAsync(abs);
                    
                    try { System.IO.File.AppendAllText("debug_log.txt", $"[ImageLoadDownloaded] {abs} ({bytes.Length} bytes)\r\n"); } catch { }

                    using (var ms = new System.IO.MemoryStream(bytes))
                    {
                        if (decodeWidthHint > 0)
                        {
                            var b = Bitmap.DecodeToWidth(ms, decodeWidthHint);
                            try { System.IO.File.AppendAllText("debug_log.txt", $"[ImageLoadSuccess] {abs}\r\n"); } catch { }
                            return b;
                        }
                        var bmp = new Bitmap(ms);
                        try { System.IO.File.AppendAllText("debug_log.txt", $"[ImageLoadSuccess] {abs}\r\n"); } catch { }
                        return bmp;
                    }
                }
                catch (Exception ex)
                {
                    try { System.IO.File.AppendAllText("debug_log.txt", $"[ImageLoadError] {abs}: {ex.Message}\r\n"); } catch { }
                    return null;
                }
            }
        }
        return null;
    }
    catch (Exception ex)
    {
        try { System.IO.File.AppendAllText("debug_log.txt", $"[ImageLoadCritical] {abs}: {ex.Message}\r\n"); } catch { }
        return null;
    }
}
private IReadOnlyList<Uri> ResolveImageUriCandidates(LiteElement n, Uri baseUri)
{
    var results = new List<Uri>();
    if (n == null) return results;
    string src = null;
    string srcset = null;
    if (n.Attr != null)
    {
        n.Attr.TryGetValue("src", out src);
        if (string.IsNullOrWhiteSpace(src))
        {
            string v;
            if (n.Attr.TryGetValue("data-src", out v) && !string.IsNullOrWhiteSpace(v)) src = v;
            else if (n.Attr.TryGetValue("data-original", out v) && !string.IsNullOrWhiteSpace(v)) src = v;
            else if (n.Attr.TryGetValue("data-lazy", out v) && !string.IsNullOrWhiteSpace(v)) src = v;
            else if (n.Attr.TryGetValue("data-src-small", out v) && !string.IsNullOrWhiteSpace(v)) src = v;
        }
        if (!n.Attr.TryGetValue("srcset", out srcset) || string.IsNullOrWhiteSpace(srcset))
        {
            string ds;
            if (n.Attr.TryGetValue("data-srcset", out ds) && !string.IsNullOrWhiteSpace(ds))
                srcset = ds;
        }
    }
    var candidateStrings = new List<string>();
    // gather explicit srcset entries (order preserved)
    if (!string.IsNullOrWhiteSpace(srcset))
    {
        var tokens = new List<string>();
        foreach (var part in srcset.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            var firstSpace = trimmed.IndexOf(' ');
            var url = firstSpace >= 0 ? trimmed.Substring(0, firstSpace) : trimmed;
            if (!string.IsNullOrWhiteSpace(url)) tokens.Add(url);
        }
        var dw = 360.0;
        try 
        { 
                if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
                dw = desktop.MainWindow.Width /* .Bounds not available */;
        } 
        catch { }
        var preferred = PickSrcFromSrcset(srcset, dw);
        if (!string.IsNullOrWhiteSpace(preferred)) candidateStrings.Add(preferred);
        foreach (var token in tokens)
        {
            if (!string.IsNullOrWhiteSpace(token)) candidateStrings.Add(token);
        }
    }
    if (!string.IsNullOrWhiteSpace(src))
        candidateStrings.Add(src);
    var seenStrings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var seenUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // Helper in method scope (C# 6/7 compatible): add candidate URI if valid and not duplicate
    Action<string> tryAdd = candidate =>
    {
        if (string.IsNullOrWhiteSpace(candidate)) return;
        candidate = RewriteImageUrlIfNeeded(candidate);
        Uri abs = ResolveUri(baseUri, candidate);
        if (abs == null) return;
        var key = abs.ToString();
        if (seenUris.Add(key)) results.Add(abs);
    };
    foreach (var s in candidateStrings) tryAdd(s);
    return results;
}
private string PickSrcFromSrcset(string srcset, double containerWidth)
{
    if (string.IsNullOrWhiteSpace(srcset)) return null;
    try
    {
        var candidates = new List<Tuple<string, double>>();
        var parts = srcset.Split(',');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            var space = trimmed.LastIndexOf(' ');
            if (space > 0)
            {
                var url = trimmed.Substring(0, space);
                var desc = trimmed.Substring(space + 1);
                if (desc.EndsWith("w"))
                {
                    double w;
                    if (double.TryParse(desc.TrimEnd('w'), NumberStyles.Float, CultureInfo.InvariantCulture, out w))
                    {
                        candidates.Add(Tuple.Create(url, w));
                    }
                }
            }
        }
        if (candidates.Count == 0) return null;
        candidates.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        foreach (var c in candidates)
        {
            if (c.Item2 >= containerWidth) return c.Item1;
        }
        return candidates.Last().Item1;
    }
    catch { return null; }
}
private void ApplyRelativeOffset(Control fe, CssComputed css, Control container)
{
    try
    {
        TranslateTransform tt = null;
        Action place = () =>
        {
            try
            {
                double cw = 0, ch = 0;
                try { if (container != null) { cw = container.Width /* .Bounds not available */; ch = (container.Height); } } catch { }
                Func<string, double?, double> pxOrPercent = (name, containerSize) =>
                {
                    try
                    {
                        string raw;
                        if (css.Map != null && css.Map.TryGetValue(name, out raw) && !string.IsNullOrWhiteSpace(raw))
                        {
                            raw = raw.Trim();
                            if (raw.EndsWith("%"))
                            {
                                double p; if (double.TryParse(raw.TrimEnd('%'), out p) && containerSize.HasValue)
                                    return (p / 100.0) * containerSize.Value;
                            }
                            double px; if (double.TryParse(raw.Replace("px", "").Trim(), out px)) return px;
                        }
                    }
                    catch { }
                    return double.NaN;
                };
                double l = double.NaN, r = double.NaN, t = double.NaN, b = double.NaN;
                if (css.Left.HasValue) l = css.Left.Value; else l = pxOrPercent("left", cw);
                if (css.Right.HasValue) r = css.Right.Value; else r = pxOrPercent("right", cw);
                if (css.Top.HasValue) t = css.Top.Value; else t = pxOrPercent("top", ch);
                if (css.Bottom.HasValue) b = css.Bottom.Value; else b = pxOrPercent("bottom", ch);
                double dx = 0, dy = 0;
                if (!double.IsNaN(l)) dx += l;
                if (!double.IsNaN(r)) dx -= r;
                if (!double.IsNaN(t)) dy += t;
                if (!double.IsNaN(b)) dy -= b;
                if (tt == null)
                {
                    tt = new TranslateTransform();
                    fe.RenderTransform = tt;
                }
                tt.X = dx; tt.Y = dy;
                // z-index affects stacking where overlapping occurs (mostly in Canvas); respect if provided
                if (css.ZIndex.HasValue) fe.ZIndex = css.ZIndex.Value;
            }
            catch { }
        };
        fe.Loaded += (s, e) => { try { place(); } catch { } };
        fe.SizeChanged += (s, e) => { try { place(); } catch { } };
        if (container != null) container.SizeChanged += (s, e) => { try { place(); } catch { } };
        place();
    }
    catch { }
}
// ---- List bullet helpers and CSS lookup ----
private static string ToUnorderedBullet(LiteElement list, LiteElement li)
{
    try
    {
        // Attribute 'type' wins over CSS for compatibility
        string type = null;
        if (list != null && list.Attr != null && list.Attr.TryGetValue("type", out type) && !string.IsNullOrWhiteSpace(type)) { }
        else if (li != null && li.Attr != null && li.Attr.TryGetValue("type", out type) && !string.IsNullOrWhiteSpace(type)) { }
        else { var cssUl = TryGetCssStatic(list); if (cssUl != null && cssUl.Map != null) cssUl.Map.TryGetValue("list-style-type", out type); }
        var cssLi = TryGetCssStatic(li); if (cssLi != null && cssLi.Map != null) { string tmp; if (cssLi.Map.TryGetValue("list-style-type", out tmp) && !string.IsNullOrWhiteSpace(tmp)) type = tmp; }
        var t = (type ?? "").Trim().ToLowerInvariant();
        if (t.Contains("circle")) return "?";
        if (t.Contains("square")) return "?";
        if (t.Contains("none")) return string.Empty;
        return "?";
    }
    catch { return "?"; }
}
private static string ToOrderedBullet(LiteElement list, LiteElement li, int idx)
{
    try
    {
        // HTML 'type' attribute (A/a/I/i/1) has priority
        string type = null;
        if (list != null && list.Attr != null && list.Attr.TryGetValue("type", out type) && !string.IsNullOrWhiteSpace(type)) { }
        else if (li != null && li.Attr != null && li.Attr.TryGetValue("type", out type) && !string.IsNullOrWhiteSpace(type)) { }
        else { var cssOl = TryGetCssStatic(list); if (cssOl != null && cssOl.Map != null) cssOl.Map.TryGetValue("list-style-type", out type); }
        var cssLi = TryGetCssStatic(li); if (cssLi != null && cssLi.Map != null) { string tmp; if (cssLi.Map.TryGetValue("list-style-type", out tmp) && !string.IsNullOrWhiteSpace(tmp)) type = tmp; }
        var t = (type ?? "").Trim().ToLowerInvariant();
        // HTML types
        if (t == "a" || t.Contains("lower-alpha") || t.Contains("lower-latin")) return ToAlpha(idx, false) + ".";
        if (t == "A" || t.Contains("upper-alpha") || t.Contains("upper-latin")) return ToAlpha(idx, true) + ".";
        if (t == "i" || t.Contains("lower-roman")) return ToRoman(idx).ToLowerInvariant() + ".";
        if (t == "I" || t.Contains("upper-roman")) return ToRoman(idx).ToUpperInvariant() + ".";
        if (t == "1" || t.Contains("decimal")) return idx.ToString() + ".";
        return idx.ToString() + ".";
    }
    catch { return idx.ToString() + "."; }
}
private static string ToRoman(int n)
{
    if (n <= 0) return n.ToString();
    int[] vals = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
    string[] syms = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
    int i = 0; var sb = new System.Text.StringBuilder(); int x = n;
    while (x > 0) { if (x >= vals[i]) { sb.Append(syms[i]); x -= vals[i]; } else i++; }
    return sb.ToString();
}
private static string ToAlpha(int n, bool upper)
{
    if (n <= 0) return n.ToString();
    n--; var sb = new System.Text.StringBuilder();
    do { int rem = n % 26; char ch = (char)((upper ? 'A' : 'a') + rem); sb.Insert(0, ch); n = n / 26 - 1; } while (n >= 0);
    return sb.ToString();
}
private static CssComputed TryGetCssStatic(LiteElement el)
{
    try { return (_computedStylesStatic != null && el != null && _computedStylesStatic.ContainsKey(el)) ? _computedStylesStatic[el] : null; } catch { return null; }
}
private static System.Collections.Generic.Dictionary<LiteElement, CssComputed> _computedStylesStatic;
private static string ApplyHyphens(string text, CssComputed css)
{
    if (string.IsNullOrEmpty(text)) return text;
    // Default (manual): preserve soft hyphens.
    // None: strip them.
    // Auto: not supported, treated as manual.
    if (css != null && string.Equals(css.Hyphens, "none", StringComparison.OrdinalIgnoreCase))
    {
        return text.Replace("\u00AD", "");
    }
    return text;
}
private static string NormalizeTextNodeContent(string raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return null;
    int lt = raw.IndexOf('<');
    int gt = raw.LastIndexOf('>');
    if (lt >= 0 && gt >= lt)
    {
        var after = raw.Substring(gt + 1).Trim();
        if (!string.IsNullOrEmpty(after)) raw = after;
    }
    return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
}
// Heuristic: ignore text nodes that are actually CSS/JS blobs from noscript fallbacks.
private static bool ShouldSuppressTextNode(string raw)
{
#if DEBUG
    bool debugEnabled = true; // flip to false to silence diagnostics quickly
#else
            const bool debugEnabled = false;
#endif
    string reason = null;
    if (string.IsNullOrWhiteSpace(raw)) { reason = "empty"; return true; }
    var trimmed = raw.Trim();
    if (trimmed.Length <= 1) return false; // allow single char labels like � or �
    int braceCount = 0, semicolonCount = 0, angleCount = 0;
    int letterDigitCount = 0, whitespaceCount = 0, otherCount = 0;
    int symbolRun = 0, maxSymbolRun = 0;
    for (int i = 0; i < trimmed.Length; i++)
    {
        var ch = trimmed[i];
        if (ch == '{' || ch == '}') braceCount++;
        else if (ch == ';') semicolonCount++;
        else if (ch == '<' || ch == '>') angleCount++;
        if (char.IsLetterOrDigit(ch))
        {
            letterDigitCount++;
            symbolRun = 0;
        }
        else if (char.IsWhiteSpace(ch))
        {
            whitespaceCount++;
            symbolRun = 0;
        }
        else
        {
            otherCount++;
            symbolRun++;
            if (symbolRun > maxSymbolRun) maxSymbolRun = symbolRun;
        }
    }
    int symbolScore = braceCount + semicolonCount + angleCount;
    if (symbolScore >= Math.Max(8, trimmed.Length / 5)) reason = "symbol-score";
    else if (trimmed.Length >= 20 && maxSymbolRun >= 6) reason = "symbol-run";
    else if (trimmed.Length >= 40)
    {
        int effectiveLen = Math.Max(1, trimmed.Length - whitespaceCount);
        double letterRatio = letterDigitCount / (double)effectiveLen;
        if (letterDigitCount > 0 && letterRatio < 0.3 && otherCount > letterDigitCount) reason = "low-letter-ratio";
        else if (letterDigitCount == 0 && otherCount > 0) reason = "no-letters";
    }
    // Embedded tags/attributes only suppress if they dominate (high symbolScore)
    if (reason == null && (trimmed.Contains("</") || trimmed.Contains("/>")) && symbolScore > 4) reason = "embedded-tags";
    if (reason == null && (trimmed.Contains("=\"") || trimmed.Contains("='")) && symbolScore > 4) reason = "attributes-inline";
    var lower = trimmed.ToLowerInvariant();
    int attrHits = 0;
    if (lower.Contains(" alt=") || lower.StartsWith("alt=")) attrHits++;
    if (lower.Contains("height=")) attrHits++;
    if (lower.Contains("width=")) attrHits++;
    if (lower.Contains("style=")) attrHits++;
    if ((lower.Contains("display:") || lower.Contains("border:"))) attrHits++;
    if (reason == null && attrHits >= 2 && symbolScore > 3) reason = "attr-dump";
    bool hasImgName = lower.Contains(".png") || lower.Contains(".jpg") || lower.Contains(".jpeg") || lower.Contains(".gif") || lower.Contains("image/");
    if (reason == null && hasImgName && attrHits >= 1 && symbolScore > 4) reason = "image-meta";
    if (reason == null && lower.Contains("<img")) reason = "img-fragment";
    if (reason == null && (lower.Contains("function(") || lower.Contains("var ") || lower.Contains("let ") || lower.Contains("const "))) reason = "js-snippet";
    if (reason == null && (lower.Contains("</style>") || lower.Contains("<style") || lower.Contains("</script>"))) reason = "style-script-marker";
    if (reason == null && (lower.StartsWith("/*") || lower.StartsWith("//"))) reason = "comment";
    if (reason == null && braceCount >= 3 && lower.Contains(".")) reason = "css-js-mix";
    if (reason == null && trimmed.Length >= 80 && otherCount >= Math.Max(15, trimmed.Length / 2)) reason = "dense-symbols";
    bool suppress = reason != null;
    if (debugEnabled && suppress)
    {
        try { System.Diagnostics.Debug.WriteLine("[TextSuppress] reason=" + reason + " len=" + trimmed.Length + " sample=" + trimmed.Substring(0, Math.Min(60, trimmed.Length)).Replace('\n', ' ').Replace('\r', ' ')); } catch { }
    }
    return suppress;
}
private TextBlock RenderPlainTextBlock(string text)
{
    try
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return new TextBlock
        {
            Text = text.Trim(),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Colors.Black),
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 4)
        };
    }
    catch
    {
        return null;
    }
}
        private async Task RenderTableAsync(LiteElement table, Control container, CssComputed style)
        {
             // Placeholder for table rendering
             var grid = new Grid();
             // ... implementation ...
             // For now just add a text block
             var tb = new TextBlock { Text = "Table (Not implemented)" };
             if (container is Panel p) p.Children.Add(tb);
             else if (container is Border b) b.Child = tb;
        }
        private string DictGet(Dictionary<string, string> dict, string key)
        {
            if (dict != null && dict.ContainsKey(key)) return dict[key];
            return null;
        }
        private static bool TryGetAttr(LiteElement node, string attr, out string value)
        {
            value = null;
            if (node == null || node.Attr == null) return false;
            return node.Attr.TryGetValue(attr, out value);
        }
        private string CollapseWs(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        }
        private string GatherText(LiteElement node)
        {
            if (node == null) return "";
            if (node.IsText) return node.Text;
            var sb = new System.Text.StringBuilder();
            if (node.Children != null)
            {
                foreach (var child in node.Children) sb.Append(GatherText(child));
            }
            return sb.ToString();
        }
        private Uri ResolveUri(string href)
        {
             return ResolveUri((_baseUriForResources ?? _documentUri ?? new Uri("about:blank")), href);
        }
        private static Uri ResolveUri(Uri baseUri, string href)
        {
            if (string.IsNullOrWhiteSpace(href)) return null;
            href = href.Trim();
            if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null; 
            if (href.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase)) return new Uri(href);
            if (href.StartsWith("//")) return new Uri((baseUri?.Scheme ?? "https") + ":" + href);
            if (Uri.TryCreate(href, UriKind.Absolute, out var abs)) return abs;
            if (baseUri != null) return new Uri(baseUri, href);
            return null;
        }
        private void AttachStickyBehavior(Control element, double topOffset)
        {
            // Placeholder
        }
        private void PositionOnCanvas(Control element, double x, double y)
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
        }
        private bool LooksSvg(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
        }
        private string SvgType(string url)
        {
            return "image/svg+xml";
        }
        private string GetUnorderedBulletChar(LiteElement ul, LiteElement li)
        {
            return "•";
        }

        private string GetOrderedBulletString(LiteElement ol, LiteElement li, int index)
        {
            return $"{index}.";
        }
    }
        public sealed class FormSubmitEventArgs : EventArgs
    {
        public string Method { get; set; }
        public Uri Action { get; set; }
        public Dictionary<string, string> Fields { get; set; }
    }
    internal static class ControlExtensions
    {
        public static void SetPadding(this Control control, Thickness padding)
        {
            if (control is Decorator d) d.Padding = padding;
            else if (control is TemplatedControl tc) tc.Padding = padding;
            else if (control is TextBlock tb) tb.Padding = padding;
        }
        public static void SetForeground(this Control control, IBrush brush)
        {
            if (control is TextBlock tb) tb.Foreground = brush;
            else if (control is TemplatedControl tc) tc.Foreground = brush;
        }
        public static void SetFontSize(this Control control, double size)
        {
             if (control is TextBlock tb) tb.FontSize = size;
             else if (control is TemplatedControl tc) tc.FontSize = size;
        }
        public static void SetFontWeight(this Control control, FontWeight weight)
        {
             if (control is TextBlock tb) tb.FontWeight = weight;
             else if (control is TemplatedControl tc) tc.FontWeight = weight;
        }
        
        public static void SetFontStyle(this Control control, FontStyle style)
        {
             if (control is TextBlock tb) tb.FontStyle = style;
             else if (control is TemplatedControl tc) tc.FontStyle = style;
        }
        public static void SetBorderBrush(this Control control, IBrush brush)
        {
            if (control is Border b) b.BorderBrush = brush;
            else if (control is TemplatedControl tc) tc.BorderBrush = brush;
        }
        public static void SetBorderThickness(this Control control, Thickness thickness)
        {
            if (control is Border b) b.BorderThickness = thickness;
            else if (control is TemplatedControl tc) tc.BorderThickness = thickness;
        }
        
        public static void SetCornerRadius(this Control control, CornerRadius radius)
        {
            if (control is Border b) b.CornerRadius = radius;
            else if (control is TemplatedControl tc) tc.CornerRadius = radius;
        }
    }
    }















