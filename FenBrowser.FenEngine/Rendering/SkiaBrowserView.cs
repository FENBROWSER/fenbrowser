using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using System;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Rendering
{
    public class SkiaBrowserView : Grid // Changed to Grid for automatic backdrop sizing
    {
        private SkiaBackdrop _backdrop;
        private Canvas _overlayCanvas; // Dedicated canvas for overlays
        private SkiaDomRenderer _renderer;
        private LiteElement _root;
        private Dictionary<LiteElement, CssComputed> _styles;

        public SkiaBrowserView() : this(new SkiaDomRenderer()) 
        { 
        }

        public SkiaBrowserView(SkiaDomRenderer renderer)
        {
            _renderer = renderer;
            
            // Set a transparent background so the Grid receives hit tests
            this.Background = Avalonia.Media.Brushes.Transparent;
            
            // Create the backdrop that performs the actual Skia drawing
            _backdrop = new SkiaBackdrop(_renderer, (size, overlays) => {
                 Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
                    // 1. Update Height (of the View)
                    if (this.Height != size.Height)
                    {
                        this.Height = size.Height;
                        this.InvalidateMeasure();
                    }
                    
                    // 2. Sync Overlays
                    SyncOverlays(overlays);
                 });
            });
            // Make backdrop NOT hit-testable so clicks go to parent Grid (SkiaBrowserView)
            _backdrop.IsHitTestVisible = false;
            Children.Add(_backdrop);
            
            // Canvas for INPUT/TEXTAREA/SELECT overlays
            // Keep IsHitTestVisible = true (default) so children (TextBox, Button, etc.) receive clicks
            // Canvas background is null/transparent by default, so clicks on empty areas pass through to parent
            _overlayCanvas = new Canvas();
            Children.Add(_overlayCanvas);

            ImageLoader.RequestRepaint = () =>
            {
                 Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(_backdrop.InvalidateVisual);
            };
        }

        private SKSize? _layoutViewport;
        public SKSize? LayoutViewport 
        { 
            get => _layoutViewport;
            set
            {
                if (_layoutViewport != value)
                {
                    _layoutViewport = value;
                    // Trigger repaint when layout constraints change
                    if (_backdrop != null) _backdrop.InvalidateVisual();
                }
            }
        }

        public void Render(LiteElement root, Dictionary<LiteElement, CssComputed> styles)
        {
            _root = root;
            _styles = styles;
            _backdrop.SetContent(root, styles, BaseUrl, LayoutViewport);
        }

        public string BaseUrl { get; set; } // Property is fine, but need to update backdrop if set? 
        // Logic: usually Render() is called with fresh state. SkiaBackdrop.SetContent takes BaseUrl.
        // We pass BaseUrl from property in Render()? No, Render() signature doesn't take BaseUrl.
        // SkiaBrowserView.BaseUrl is used. 
        // Wait, look at previous Render() method. It didn't take BaseUrl arg but used property?
        // Ah, DrawOperation used property 'BaseUrl'.
        // So here in Render(), we pass 'BaseUrl'.
        
        /// <summary>
        /// Page zoom level (1.0 = 100%)
        /// </summary>
        public float ZoomLevel { get; set; } = 1.0f;
        
        // Event to bubble up link clicks to the container
        public event EventHandler<string> LinkInternalClicked;

        protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            
            var point = e.GetPosition(this);
            var element = _renderer.HitTest((float)point.X, (float)point.Y);
            
            // Traverse up to find <a> tag if we hit text or image inside it
            if (element != null)
            {
                var curr = element;
                bool stateChanged = false;
                
                // 1. Check for Link (traverse up)
                var linkNode = curr;
                while (linkNode != null)
                {
                    if (CheckLink(linkNode, true)) return;
                    linkNode = _renderer.GetParent(linkNode);
                }

                // 2. Check for Checkbox/Radio (Direct hit)
                if (curr.Tag?.ToUpperInvariant() == "INPUT")
                {
                    string type = curr.Attr.ContainsKey("type") ? curr.Attr["type"].ToLowerInvariant() : "";
                    if (type == "checkbox")
                    {
                        if (curr.Attr.ContainsKey("checked")) curr.Attr.Remove("checked");
                        else curr.Attr["checked"] = "checked";
                        stateChanged = true;
                    }
                    else if (type == "radio")
                    {
                        curr.Attr["checked"] = "checked";
                        stateChanged = true;
                    }
                    else if (type == "submit" || type == "image" || type == "button")
                    {
                        // Potential form submit
                        if (CheckSubmit(curr)) return;
                    }
                }
                else if (curr.Tag?.ToUpperInvariant() == "BUTTON")
                {
                    if (CheckSubmit(curr)) return;
                }

                // 3. Check for Summary (Toggle Details)
                var summaryNode = curr;
                while (summaryNode != null)
                {
                    if (summaryNode.Tag?.ToUpperInvariant() == "SUMMARY")
                    {
                        var details = _renderer.GetParent(summaryNode);
                        if (details != null && details.Tag?.ToUpperInvariant() == "DETAILS")
                        {
                            if (details.Attr.ContainsKey("open")) details.Attr.Remove("open");
                            else details.Attr["open"] = "open";
                            stateChanged = true;
                        }
                        break;
                    }
                    summaryNode = _renderer.GetParent(summaryNode);
                }

                if (stateChanged)
                {
                     Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(_backdrop.InvalidateVisual);
                }
            }
        }

        public event EventHandler<LiteElement> FormSubmitted;
        public event EventHandler<LiteElement> InputChanged;
        public event EventHandler<LiteElement> ElementClicked;

        private bool CheckSubmit(LiteElement element)
        {
            // Traverse up to find parent FORM
            var p = element;
            while (p != null)
            {
                if (p.Tag?.ToUpperInvariant() == "FORM")
                {
                    FormSubmitted?.Invoke(this, p);
                    return true;
                }
                 p = _renderer.GetParent(p);
            }
            return false;
        }
        
        // CheckLink - returns true if element is a link, optionally invokes navigation
        private bool CheckLink(LiteElement element, bool invoke = false)
        {
            if (element.Tag?.ToUpperInvariant() == "A" && element.Attr.ContainsKey("href"))
            {
                 if (invoke)
                 {
                     string href = element.Attr["href"];
                     LinkInternalClicked?.Invoke(this, href);
                 }
                 return true;
            }
            return false;
        }

        protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var point = e.GetPosition(this);
            var element = _renderer.HitTest((float)point.X, (float)point.Y);
            
            bool isLink = false;
            string cssCursor = null;
            
            if (element != null)
            {
                 var curr = element;
                 while(curr != null)
                 {
                     // Check for CSS cursor property
                     var style = _renderer.GetStyle(curr);
                     if (!string.IsNullOrEmpty(style?.Cursor))
                     {
                         cssCursor = style.Cursor.ToLowerInvariant();
                     }
                     
                     if (CheckLink(curr, false))  // Don't invoke, just check
                     {
                         isLink = true;
                         break;
                     }
                     curr = _renderer.GetParent(curr);
                 }
            }
            
            // Apply cursor from CSS cursor property or default based on link
            var cursorType = Avalonia.Input.StandardCursorType.Arrow;
            
            if (!string.IsNullOrEmpty(cssCursor))
            {
                cursorType = cssCursor switch
                {
                    "pointer" => Avalonia.Input.StandardCursorType.Hand,
                    "hand" => Avalonia.Input.StandardCursorType.Hand,
                    "text" => Avalonia.Input.StandardCursorType.Ibeam,
                    "crosshair" => Avalonia.Input.StandardCursorType.Cross,
                    "move" => Avalonia.Input.StandardCursorType.SizeAll,
                    "not-allowed" => Avalonia.Input.StandardCursorType.No,
                    "no-drop" => Avalonia.Input.StandardCursorType.No,
                    "wait" => Avalonia.Input.StandardCursorType.Wait,
                    "progress" => Avalonia.Input.StandardCursorType.AppStarting,
                    "help" => Avalonia.Input.StandardCursorType.Help,
                    "n-resize" => Avalonia.Input.StandardCursorType.TopSide,
                    "s-resize" => Avalonia.Input.StandardCursorType.BottomSide,
                    "e-resize" => Avalonia.Input.StandardCursorType.RightSide,
                    "w-resize" => Avalonia.Input.StandardCursorType.LeftSide,
                    "ne-resize" => Avalonia.Input.StandardCursorType.TopRightCorner,
                    "nw-resize" => Avalonia.Input.StandardCursorType.TopLeftCorner,
                    "se-resize" => Avalonia.Input.StandardCursorType.BottomRightCorner,
                    "sw-resize" => Avalonia.Input.StandardCursorType.BottomLeftCorner,
                    "ew-resize" => Avalonia.Input.StandardCursorType.SizeWestEast,
                    "ns-resize" => Avalonia.Input.StandardCursorType.SizeNorthSouth,
                    "col-resize" => Avalonia.Input.StandardCursorType.SizeWestEast,
                    "row-resize" => Avalonia.Input.StandardCursorType.SizeNorthSouth,
                    "grab" => Avalonia.Input.StandardCursorType.Hand,
                    "grabbing" => Avalonia.Input.StandardCursorType.Hand,
                    "none" => Avalonia.Input.StandardCursorType.None,
                    _ => isLink ? Avalonia.Input.StandardCursorType.Hand : Avalonia.Input.StandardCursorType.Arrow
                };
            }
            else if (isLink)
            {
                cursorType = Avalonia.Input.StandardCursorType.Hand;
            }
            
            this.Cursor = new Avalonia.Input.Cursor(cursorType);
        }

        private void SyncOverlays(List<InputOverlayData> overlays)
        {
            if (overlays == null) return;
            
            FenLogger.Debug($"[SyncOverlays] Count: {overlays.Count}", LogCategory.Layout);
            foreach (var ov in overlays)
            {
                FenLogger.Debug($"[SyncOverlays] Type={ov.Type}, Bounds={ov.Bounds}, Text={ov.InitialText}", LogCategory.Layout);
            }
            
             var touched = new HashSet<Control>();
             
             foreach (var overlay in overlays)
             {
                 Control match = null;
                 foreach(var child in _overlayCanvas.Children)
                 {
                     if (child is Control c && c.Tag == overlay.Node)
                     {
                         match = c;
                         break;
                     }
                 }
                 
                 if (match == null)
                 {
                     // Create new
                     if (overlay.Type == "button" || overlay.Type == "submit" || overlay.Type == "reset")
                     {
                         var btn = new Button { Content = overlay.InitialText, Tag = overlay.Node };
                         // Apply Google-style button appearance
                         btn.Background = new SolidColorBrush(Color.FromRgb(0xf8, 0xf9, 0xfa));
                         btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0xf8, 0xf9, 0xfa));
                         btn.Foreground = Brushes.Black;
                         btn.FontSize = 14;
                         btn.Padding = new Thickness(16, 8, 16, 8);
                         btn.CornerRadius = new CornerRadius(4);
                         btn.BorderThickness = new Thickness(1);
                         // Hover effect handled by Avalonia's default button styles
                        
                        btn.Click += (s, e) =>
                        {
                            ElementClicked?.Invoke(this, overlay.Node);
                            if (overlay.Type == "submit")
                            {
                                CheckSubmit(overlay.Node);
                            }
                        };
                        match = btn;
                     }
                     else if (overlay.Type == "select")
                     {
                         var cb = new ComboBox { Tag = overlay.Node };
                         cb.ItemsSource = overlay.Options;
                         cb.SelectedIndex = overlay.SelectedIndex;
                         
                         cb.SelectionChanged += (s, e) => 
                         {
                             if (overlay.Node.Attr != null && cb.SelectedItem != null)
                             {
                                  // Update DOM
                                  string val = cb.SelectedItem.ToString();
                                  overlay.Node.Attr["value"] = val; 
                                  // Find option and set selected? 
                                  // This requires more complex DOM manip handling. 
                                  // For simple form submission, value on select is enough.
                                  // But if script reads selectedIndex, we need to update children.
                                  // MVP: Update value attribute on Select node.
                             }
                         };
                         match = cb;
                     }
                     else
                     {
                         var tb = new TextBox { Text = overlay.InitialText, Tag = overlay.Node };
                         if (overlay.Type == "password") tb.PasswordChar = '•';
                         if (overlay.Type == "textarea") { tb.AcceptsReturn = true; tb.TextWrapping = TextWrapping.Wrap; }
                         
                         // Set placeholder text (watermark)
                         if (!string.IsNullOrEmpty(overlay.Placeholder))
                         {
                             tb.Watermark = overlay.Placeholder;
                         }
                         
                         // Apply CSS Styles to Avalonia Control
                         if (overlay.BackgroundColor.HasValue)
                         {
                             var c = overlay.BackgroundColor.Value;
                             // If completely transparent, keep transparent. If partly transparent, use it.
                             // Avalonia TextBox default is usually white/transparent.
                             // Google input: #fff (or #202124 dark mode).
                             tb.Background = new SolidColorBrush(Color.FromUInt32((uint)c));
                         }
                         else
                         {
                             tb.Background = Brushes.Transparent;
                         }

                         if (overlay.TextColor.HasValue)
                         {
                             var c = overlay.TextColor.Value;
                             tb.Foreground = new SolidColorBrush(Color.FromUInt32((uint)c));
                         }
                         
                         if (!string.IsNullOrEmpty(overlay.FontFamily))
                         {
                             tb.FontFamily = new FontFamily(overlay.FontFamily);
                         }
                         
                         if (overlay.FontSize > 0)
                         {
                             tb.FontSize = overlay.FontSize;
                         }
                         
                         // Borders
                         tb.BorderThickness = overlay.BorderThickness;
                         if (overlay.BorderColor.HasValue)
                         {
                             var c = overlay.BorderColor.Value;
                             tb.BorderBrush = new SolidColorBrush(Color.FromUInt32((uint)c));
                         }
                         else
                         {
                             tb.BorderBrush = Brushes.Transparent;
                         }
                         
                         tb.CornerRadius = overlay.BorderRadius;
                         
                         // Text Align
                         if (!string.IsNullOrEmpty(overlay.TextAlign))
                         {
                            if (overlay.TextAlign == "center") tb.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
                            else if (overlay.TextAlign == "right") tb.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Right;
                            else tb.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left;
                         }
                         else
                         {
                            tb.VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;
                         }

                         tb.Padding = new Thickness(4, 0, 4, 0); // Add small padding for text cursor breathing room
                         
                        tb.TextChanged += (s, e) => 
                        {
                            if (overlay.Node.Attr != null)
                            {
                                overlay.Node.Attr["value"] = tb.Text;
                                InputChanged?.Invoke(this, overlay.Node);
                            }
                        };
                         
                         match = tb;
                     }
                     _overlayCanvas.Children.Add(match);
                 }
                 
                 touched.Add(match);
                 
                 // Update Visuals
                 match.Width = overlay.Bounds.Width;
                 match.Height = overlay.Bounds.Height;
                 Canvas.SetLeft(match, overlay.Bounds.Left);
                 Canvas.SetTop(match, overlay.Bounds.Top);
                 
                 // Update State explicitly if changed externally?
                 // For ComboBox, if options changed?
                 if (match is ComboBox cbMatch && overlay.Type == "select")
                 {
                     // If options count diff, reset? 
                     // This is heavy. Assume static options for now or check count.
                     if (cbMatch.ItemCount != overlay.Options.Count)
                     {
                         cbMatch.ItemsSource = overlay.Options;
                         cbMatch.SelectedIndex = overlay.SelectedIndex;
                     }
                 }
                 
                 match.IsVisible = true;
             }
             
             // Remove unused
             for (int i = _overlayCanvas.Children.Count - 1; i >= 0; i--)
             {
                 var c = _overlayCanvas.Children[i] as Control;
                 if (c != null && !touched.Contains(c))
                 {
                     _overlayCanvas.Children.RemoveAt(i);
                 }
             }
        }

        // Inner class to handle drawing
        private class SkiaBackdrop : Control
        {
            private SkiaDomRenderer _renderer;
            private LiteElement _root;
            private Dictionary<LiteElement, CssComputed> _styles;
            private string _baseUrl;
            private SKSize? _layoutViewport;
            private Action<SKSize, List<InputOverlayData>> _callback;

            public SkiaBackdrop(SkiaDomRenderer renderer, Action<SKSize, List<InputOverlayData>> callback)
            {
                _renderer = renderer;
                _callback = callback;
            }

            public void SetContent(LiteElement root, Dictionary<LiteElement, CssComputed> styles, string baseUrl, SKSize? layoutViewport)
            {
                _root = root;
                _styles = styles;
                _baseUrl = baseUrl;
                _layoutViewport = layoutViewport;
                InvalidateVisual();
            }

            public override void Render(DrawingContext context)
            {
                if (_root == null)
                {
                    context.DrawRectangle(Brushes.White, null, new Rect(0,0, Bounds.Width, Bounds.Height));
                    return;
                }
                
                context.Custom(new SkiaDrawOperation(new Rect(0, 0, Bounds.Width, Bounds.Height), _root, _renderer, _styles, _baseUrl, _callback, _layoutViewport));
            }
        }

        private class SkiaDrawOperation : ICustomDrawOperation
        {
            // ... (keep logic, just ensure Constructor matches)
            private readonly LiteElement _root;
            private readonly SkiaDomRenderer _renderer;
            private readonly Dictionary<LiteElement, CssComputed> _styles;
            private readonly string _baseUrl;
            private readonly Action<SKSize, List<InputOverlayData>> _onLayout;
            private readonly SKSize? _layoutViewport;

            public Rect Bounds { get; }

            public SkiaDrawOperation(Rect bounds, LiteElement root, SkiaDomRenderer renderer, Dictionary<LiteElement, CssComputed> styles, string baseUrl, Action<SKSize, List<InputOverlayData>> onLayout, SKSize? layoutViewport)
            {
                Bounds = bounds;
                _root = root;
                _renderer = renderer;
                _styles = styles;
                _baseUrl = baseUrl;
                _onLayout = onLayout;
                _layoutViewport = layoutViewport;
            }

            public void Dispose() { }
            public bool HitTest(Point p) => false;
            public bool Equals(ICustomDrawOperation other) => false;

            public void Render(ImmediateDrawingContext context)
            {
                if (!context.TryGetFeature<ISkiaSharpApiLeaseFeature>(out var leaseFeature)) return;
                using var lease = leaseFeature.Lease();
                var canvas = lease.SkCanvas;
                canvas.Save();
                try
                {
                    var viewport = new SKRect((float)Bounds.X, (float)Bounds.Y, (float)Bounds.Right, (float)Bounds.Bottom);
                    _renderer.Render(_root, canvas, _styles, viewport, _baseUrl, _onLayout, _layoutViewport);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SkiaDraw] Render Error: {ex}");
                     if (true) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[SkiaDraw] CRASH: {ex}\r\n"); } catch {} }
                }
                finally { canvas.Restore(); }
            }
        }
    }
}

