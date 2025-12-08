using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Layout;
using Avalonia.Threading;
using System.Net.Http;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Handles efficient rendering of large Render Trees by only creating UI elements for visible nodes.
    /// </summary>
    public class VirtualizingRenderer
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private readonly RenderObject _root;
        private readonly ScrollViewer _scrollViewer;
        private readonly Canvas _canvas;
        private readonly List<Control> _recycledElements = new List<Control>();
        private readonly Action<Uri> _onNavigate;
        private readonly Uri _baseUri;

        public VirtualizingRenderer(RenderObject root, Uri baseUri, Action<Uri> onNavigate)
        {
            _root = root;
            _baseUri = baseUri;
            _onNavigate = onNavigate;
            
            _canvas = new Canvas();
            if (_root != null)
            {
                _canvas.Width = EnsureValid(_root.Bounds.Width);
                _canvas.Height = EnsureValid(_root.Bounds.Height);
            }

            _scrollViewer = new ScrollViewer
            {
                Content = _canvas,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            _scrollViewer.ScrollChanged += OnScrollChanged;
            _scrollViewer.SizeChanged += OnSizeChanged;
            
            // Initial Paint
            UpdateView();
        }

        public Control GetRootElement()
        {
            return _scrollViewer;
        }

        private bool _updateScheduled;

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            ScheduleUpdate();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ScheduleUpdate();
        }

        private void ScheduleUpdate()
        {
            if (_updateScheduled) return;
            _updateScheduled = true;
            Dispatcher.UIThread.Post(UpdateView, DispatcherPriority.Render);
        }

        private void UpdateView()
        {
            _updateScheduled = false;
            if (_root == null) return;

            // Calculate visible rect
            double horizontalOffset = _scrollViewer.Offset.X;
            double verticalOffset = _scrollViewer.Offset.Y;
            double viewportWidth = _scrollViewer.Viewport.Width;
            double viewportHeight = _scrollViewer.Viewport.Height;

            // Handle initial state where viewport might be 0
            if (viewportWidth == 0) viewportWidth = _scrollViewer.Bounds.Width;
            if (viewportHeight == 0) viewportHeight = _scrollViewer.Bounds.Height;
            if (viewportWidth == 0) viewportWidth = 800; // Fallback
            if (viewportHeight == 0) viewportHeight = 600; // Fallback

            // Add a buffer to avoid flickering
            double buffer = 200;
            
            // Sanitize values before creating Rect
            horizontalOffset = EnsureValid(horizontalOffset);
            verticalOffset = EnsureValid(verticalOffset);
            viewportWidth = EnsureValid(viewportWidth);
            viewportHeight = EnsureValid(viewportHeight);
            
            var visibleRect = new Rect(
                horizontalOffset - buffer, 
                verticalOffset - buffer, 
                viewportWidth + 2 * buffer, 
                viewportHeight + 2 * buffer);

            // Recycle current children
            // In a real implementation, we would pool them by type.
            // For now, we just clear and rebuild. 
            // Optimization: Smart diffing or pooling.
            _canvas.Children.Clear();

            // Traverse and find visible nodes
            PaintVisible(_root, visibleRect, 0, 0);
        }

        private void PaintVisible(RenderObject node, Rect visibleRect, double parentX, double parentY)
        {
            double absX = parentX + node.Bounds.X;
            double absY = parentY + node.Bounds.Y;
            
            // Check intersection
            var nodeRect = new Rect(
                EnsureValid(absX), 
                EnsureValid(absY), 
                EnsureValid(node.Bounds.Width), 
                EnsureValid(node.Bounds.Height));
            
            bool isVisible = visibleRect.Intersects(nodeRect);

            if (isVisible)
            {
                // Safety Check: Ensure values are valid
                if (double.IsNaN(absX) || double.IsInfinity(absX)) absX = 0;
                if (double.IsNaN(absY) || double.IsInfinity(absY)) absY = 0;

                    // Create Visual
                    Control visual = CreateVisual(node);
                if (visual != null)
                {
                    Canvas.SetLeft(visual, absX);
                    Canvas.SetTop(visual, absY);
                    _canvas.Children.Add(visual);
                }
            }

            // Recurse
            // Skip recursion for controls that handle their own content (Button, Input, etc)
            var tag = node.Node?.Tag?.ToUpperInvariant();
            bool isLeafControl = tag == "BUTTON" || tag == "INPUT" || tag == "IMG" || tag == "SELECT" || tag == "TEXTAREA";
            
            if (!isLeafControl)
            {
                foreach (var child in node.Children)
                {
                    PaintVisible(child, visibleRect, absX, absY);
                }
            }
        }

        private double EnsureValid(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
            return Math.Max(0, v);
        }

        private Control CreateVisual(RenderObject node)
        {
            Control element = null;

            // Re-use logic from Painter.cs, but instance-based
            if (node is RenderBox box)
            {
                // Check Tag for Form Controls
                var tag = node.Node?.Tag?.ToUpperInvariant();
                
                if (tag == "IMG")
                {
                    // Container for Image + Alt Text
                    var grid = new Grid
                    {
                        Width = EnsureValid(node.Bounds.Width),
                        Height = EnsureValid(node.Bounds.Height)
                    };

                    // 1. Alt Text (Background)
                    var altText = node.Node.Attr != null && node.Node.Attr.ContainsKey("alt") ? node.Node.Attr["alt"] : "Image";
                    var tbAlt = new TextBlock
                    {
                        Text = altText,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = new SolidColorBrush(Colors.Gray),
                        FontSize = 12
                    };
                    grid.Children.Add(tbAlt);

                    // 2. Image (Foreground)
                    var img = new Image
                    {
                        Width = EnsureValid(node.Bounds.Width),
                        Height = EnsureValid(node.Bounds.Height),
                        Stretch = Stretch.Uniform
                    };

                    // Load Image Source
                    var src = node.Node.Attr != null && node.Node.Attr.ContainsKey("src") ? node.Node.Attr["src"] : null;
                    if (!string.IsNullOrEmpty(src))
                    {
                        // Check for Data URI
                        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        {
                            LoadDataUriAsync(img, src);
                        }
                        else
                        {
                            var uri = ResolveUri(_baseUri, src);
                            if (uri != null)
                            {
                                try
                                {
                                    // Async loading handled by Avalonia usually, but we might need manual fetch
                                    // For now, assume simple URL loading works if supported, or implement custom loader
                                    // Avalonia Image doesn't load from URI directly easily without helpers.
                                    // We'll use a placeholder or custom loader.
                                    // For this refactor, we'll skip complex image loading implementation details
                                    // and just set up the structure.
                                    // In a real app, we'd use HttpClient to get stream -> Bitmap.
                                    LoadImageFromUrlAsync(img, uri);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[IMG FAIL] {uri}: {ex.Message}");
                                }
                            }
                        }
                    }

                    grid.Children.Add(img);
                    element = grid;
                }
                else if (tag == "SELECT")
                {
                    var cb = new ComboBox
                    {
                        Width = EnsureValid(node.Bounds.Width),
                        Height = Math.Max(EnsureValid(node.Bounds.Height), 32), // Ensure minimum height
                        FontSize = node.Style?.FontSize ?? 14,
                        FontWeight = node.Style?.FontWeight ?? FontWeight.Normal,
                        FontStyle = node.Style?.FontStyle ?? FontStyle.Normal,
                        Background = node.Style?.Background ?? new SolidColorBrush(Colors.White),
                        Foreground = node.Style?.Foreground ?? new SolidColorBrush(Colors.Black),
                        BorderBrush = node.Style?.BorderBrush ?? new SolidColorBrush(Color.Parse("#C8C8C8")),
                        BorderThickness = node.Style?.BorderThickness ?? new Thickness(1),
                        Padding = node.Style?.Padding ?? new Thickness(8, 6, 8, 6)
                    };

                    // Populate options
                    if (node.Children != null)
                    {
                        foreach (var child in node.Children)
                        {
                            if (child.Node != null && string.Equals(child.Node.Tag, "OPTION", StringComparison.OrdinalIgnoreCase))
                            {
                                var text = child.Node.Text ?? "";
                                // If text is empty, check first child text node (common in HTML parser)
                                if (string.IsNullOrEmpty(text) && child.Children.Count > 0 && child.Children[0] is RenderText rt)
                                {
                                    text = rt.Text;
                                }
                                cb.Items.Add(text);
                                if (child.Node.Attr != null && child.Node.Attr.ContainsKey("selected"))
                                {
                                    cb.SelectedItem = text;
                                }
                            }
                        }
                    }
                    if (cb.SelectedIndex < 0 && cb.Items.Count > 0) cb.SelectedIndex = 0;

                    element = cb;
                }
                else if (tag == "INPUT")
                {
                    var type = node.Node.Attr != null && node.Node.Attr.ContainsKey("type") ? node.Node.Attr["type"].ToLowerInvariant() : "text";
                    if (type == "submit" || type == "button" || type == "reset")
                    {
                        var btn = new Button
                        {
                            Content = node.Node.Attr != null && node.Node.Attr.ContainsKey("value") ? node.Node.Attr["value"] : "Submit",
                            Width = EnsureValid(node.Bounds.Width),
                            Height = EnsureValid(node.Bounds.Height),
                            Background = node.Style?.Background ?? new SolidColorBrush(Colors.LightGray),
                            Foreground = node.Style?.Foreground ?? new SolidColorBrush(Colors.Black),
                            BorderBrush = node.Style?.BorderBrush ?? new SolidColorBrush(Colors.Gray),
                            BorderThickness = node.Style?.BorderThickness ?? new Thickness(1),
                            Padding = node.Style?.Padding ?? new Thickness(4),
                            FontSize = node.Style?.FontSize ?? 14,
                            FontWeight = node.Style?.FontWeight ?? FontWeight.Normal,
                            FontStyle = node.Style?.FontStyle ?? FontStyle.Normal,
                        };
                        element = btn;
                    }
                    else if (type == "checkbox")
                    {
                        var chk = new CheckBox
                        {
                            IsChecked = node.Node.Attr != null && node.Node.Attr.ContainsKey("checked"),
                            Width = EnsureValid(node.Bounds.Width),
                            Height = EnsureValid(node.Bounds.Height),
                            Background = node.Style?.Background ?? new SolidColorBrush(Colors.White),
                            BorderBrush = node.Style?.BorderBrush ?? new SolidColorBrush(Colors.Gray),
                            BorderThickness = node.Style?.BorderThickness ?? new Thickness(1)
                        };
                        element = chk;
                    }
                    else
                    {
                        var tb = new TextBox
                        {
                            Text = node.Node.Attr != null && node.Node.Attr.ContainsKey("value") ? node.Node.Attr["value"] : "",
                            Watermark = node.Node.Attr != null && node.Node.Attr.ContainsKey("placeholder") ? node.Node.Attr["placeholder"] : "",
                            Width = EnsureValid(node.Bounds.Width),
                            Height = (EnsureValid(node.Bounds.Height) > 32) ? EnsureValid(node.Bounds.Height) : 32, // Ensure minimum height
                            FontSize = node.Style?.FontSize ?? 14,
                            FontWeight = node.Style?.FontWeight ?? FontWeight.Normal,
                            FontStyle = node.Style?.FontStyle ?? FontStyle.Normal,
                            Background = node.Style?.Background ?? new SolidColorBrush(Colors.White),
                            Foreground = node.Style?.Foreground ?? new SolidColorBrush(Colors.Black),
                            BorderBrush = node.Style?.BorderBrush ?? new SolidColorBrush(Color.Parse("#C8C8C8")),
                            BorderThickness = node.Style?.BorderThickness ?? new Thickness(1),
                            Padding = node.Style?.Padding ?? new Thickness(8, 6, 8, 6)
                        };
                        element = tb;
                    }
                }
                else if (tag == "BUTTON")
                {
                    var btn = new Button
                    {
                        Content = node.Node.CollectText() ?? "Button",
                        Width = EnsureValid(node.Bounds.Width),
                        Height = EnsureValid(node.Bounds.Height),
                        Background = node.Style?.Background ?? new SolidColorBrush(Colors.LightGray),
                        Foreground = node.Style?.Foreground ?? new SolidColorBrush(Colors.Black),
                        BorderBrush = node.Style?.BorderBrush ?? new SolidColorBrush(Colors.Gray),
                        BorderThickness = node.Style?.BorderThickness ?? new Thickness(1),
                        Padding = node.Style?.Padding ?? new Thickness(4),
                        FontSize = node.Style?.FontSize ?? 14,
                        FontWeight = node.Style?.FontWeight ?? FontWeight.Normal,
                        FontStyle = node.Style?.FontStyle ?? FontStyle.Normal,
                    };
                    element = btn;
                }
                else
                {
                    var bg = node.Style?.Background;
                    var borderBrush = node.Style?.BorderBrush;
                    var borderThick = node.Style?.BorderThickness ?? new Thickness(0);

                    if (bg != null || (borderBrush != null && borderThick != default(Thickness)) || tag == "A")
                    {
                        element = new Border
                        {
                            Width = EnsureValid(node.Bounds.Width),
                            Height = EnsureValid(node.Bounds.Height),
                            Background = bg,
                            BorderBrush = borderBrush,
                            BorderThickness = borderThick,
                            CornerRadius = node.Style?.BorderRadius ?? new CornerRadius(0)
                        };
                    }
                }
            }
            else if (node is RenderText textNode)
            {
                var style = node.Style ?? node.Parent?.Style;
                var tb = new TextBlock
                {
                    Text = textNode.Text,
                    FontSize = style?.FontSize ?? 16,
                    Foreground = style?.Foreground ?? new SolidColorBrush(Colors.Black),
                    FontFamily = style?.FontFamily ?? new FontFamily("Segoe UI"),
                    FontWeight = style?.FontWeight ?? FontWeight.Normal,
                    TextWrapping = TextWrapping.Wrap
                };

                // Handle Text Decorations
                // Text decorations support via reflection is done in DomBasicRenderer; skip here for minimal renderer
                
                if (node.Bounds.Width > 0) tb.Width = node.Bounds.Width;
                element = tb;
            }

            // Handle Links
            RenderObject current = node;
            string href = null;

            while (current != null)
            {
                if (current.Node != null && 
                    current.Node.Tag != null && 
                    current.Node.Tag.Equals("A", StringComparison.OrdinalIgnoreCase))
                {
                    if (current.Node.Attr != null && current.Node.Attr.ContainsKey("href"))
                    {
                        href = current.Node.Attr["href"];
                        break; // Found the nearest anchor
                    }
                }
                current = current.Parent;
            }

            if (href != null && element != null)
            {
                var uri = ResolveUri(_baseUri, href);
                if (uri != null)
                {
                    // Make sure it's hit-testable
                    if (element is Border b && b.Background == null)
                    {
                        b.Background = new SolidColorBrush(Colors.Transparent);
                    }

                    // Attach handler
                    element.Tapped += (s, e) =>
                    {
                        e.Handled = true;
                        _onNavigate?.Invoke(uri);
                    };
                    // Pointer cursor is platform-specific; leave as-is for now
                }
            }

            return element;
        }

        private static Uri ResolveUri(Uri baseUri, string href)
        {
            if (string.IsNullOrWhiteSpace(href)) return null;
            href = href.Trim();

            if (href.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase))
            {
                try { return new Uri(href); } catch { return null; }
            }

            if (href.StartsWith("//"))
            {
                try { return new Uri((baseUri?.Scheme ?? "https") + ":" + href); } catch { return null; }
            }

            Uri abs;
            if (Uri.TryCreate(href, UriKind.Absolute, out abs)) return abs;
            
            if (baseUri != null)
            {
                try { return new Uri(baseUri, href); } catch { return null; }
            }
            
            try { return new Uri("ms-appx:///" + href.TrimStart('/')); } catch { return null; }
        }

        private async void LoadDataUriAsync(Image img, string dataUri)
        {
            try
            {
                var commaIndex = dataUri.IndexOf(',');
                if (commaIndex > 5)
                {
                    var base64 = dataUri.Substring(commaIndex + 1);
                    var bytes = Convert.FromBase64String(base64);
                    using var stream = new MemoryStream(bytes);
                    // TODO: Convert stream into a BitmapImage via SetSourceAsync using a RandomAccessStream.
                    // For now, hide the image placeholder for data URIs to avoid complexity.
                    img.IsVisible = false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IMG DATA FAIL] {ex.Message}");
                img.IsVisible = false;
            }
        }

        private async void LoadImageFromUrlAsync(Image img, Uri uri)
        {
            if (uri == null) return;
            try
            {
                // Use HttpClient to fetch the image data
                var data = await _httpClient.GetByteArrayAsync(uri);
                
                // Create Bitmap on a background thread if possible, but Bitmap constructor might need UI thread or be thread safe?
                // Avalonia Bitmap constructor reads from stream.
                using (var stream = new MemoryStream(data))
                {
                    var bitmap = new Bitmap(stream);
                    
                    // Update the Image control on the UI thread
                    Dispatcher.UIThread.Post(() => 
                    {
                        img.Source = bitmap;
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IMG LOAD FAIL] {uri}: {ex.Message}");
                // Optionally set a broken image placeholder or hide
                // img.IsVisible = false; 
            }
        }
    }
}
