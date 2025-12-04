using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// A StackPanel that properly constrains child widths to available space.
    /// Unlike regular StackPanel (Vertical), this panel passes the available width
    /// to children during measure, allowing TextBlocks to wrap properly.
    /// </summary>
    internal sealed class WidthConstrainingStackPanel : Panel
    {
        public static readonly StyledProperty<Orientation> OrientationProperty =
            AvaloniaProperty.Register<WidthConstrainingStackPanel, Orientation>(nameof(Orientation), Orientation.Vertical);

        public static readonly StyledProperty<double> SpacingProperty =
            AvaloniaProperty.Register<WidthConstrainingStackPanel, double>(nameof(Spacing), 0.0);

        public Orientation Orientation
        {
            get => GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        public double Spacing
        {
            get => GetValue(SpacingProperty);
            set => SetValue(SpacingProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double totalWidth = 0;
            double totalHeight = 0;
            double maxWidth = 0;
            double maxHeight = 0;
            
            bool isVertical = Orientation == Orientation.Vertical;
            double spacing = Spacing;
            
            // Get constrained width - if infinite, try to get window width
            double constrainedWidth = availableSize.Width;
            if (double.IsInfinity(constrainedWidth))
            {
                try 
                { 
                    if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop 
                        && desktop.MainWindow != null) 
                    {
                        constrainedWidth = desktop.MainWindow.Bounds.Width - 50;
                        if (constrainedWidth < 200) constrainedWidth = double.PositiveInfinity;
                    }
                } 
                catch { }
            }
            
            for (int i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                
                if (isVertical)
                {
                    // For vertical layout, constrain width, give infinite height
                    child.Measure(new Size(constrainedWidth, double.PositiveInfinity));
                    var desired = child.DesiredSize;
                    
                    maxWidth = Math.Max(maxWidth, desired.Width);
                    totalHeight += desired.Height;
                    if (i < Children.Count - 1) totalHeight += spacing;
                }
                else
                {
                    // For horizontal layout, constrain height, give infinite width
                    child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                    var desired = child.DesiredSize;
                    
                    maxHeight = Math.Max(maxHeight, desired.Height);
                    totalWidth += desired.Width;
                    if (i < Children.Count - 1) totalWidth += spacing;
                }
            }
            
            if (isVertical)
            {
                return new Size(
                    double.IsInfinity(constrainedWidth) ? maxWidth : Math.Min(maxWidth, constrainedWidth),
                    totalHeight);
            }
            else
            {
                return new Size(totalWidth, maxHeight);
            }
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            bool isVertical = Orientation == Orientation.Vertical;
            double spacing = Spacing;
            double offset = 0;
            
            for (int i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                var desired = child.DesiredSize;
                
                if (isVertical)
                {
                    // Arrange child at full width (or desired width, whichever is smaller)
                    double childWidth = Math.Min(finalSize.Width, desired.Width);
                    // Stretch to fill available width if child wants to stretch
                    if (child is Control ctrl && ctrl.HorizontalAlignment == HorizontalAlignment.Stretch)
                    {
                        childWidth = finalSize.Width;
                    }
                    
                    child.Arrange(new Rect(0, offset, childWidth, desired.Height));
                    offset += desired.Height + spacing;
                }
                else
                {
                    double childHeight = Math.Min(finalSize.Height, desired.Height);
                    if (child is Control ctrl && ctrl.VerticalAlignment == VerticalAlignment.Stretch)
                    {
                        childHeight = finalSize.Height;
                    }
                    
                    child.Arrange(new Rect(offset, 0, desired.Width, childHeight));
                    offset += desired.Width + spacing;
                }
            }
            
            return finalSize;
        }
    }
}
