using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Layout;

namespace FenBrowser.FenEngine.Rendering
{
    // Inline video host that feels like a browser <video> element.
    // Minimal controls: tap to play/pause; optional overlay button.
    internal sealed class VideoHost : Grid
    {
        // Avalonia doesn't have a built-in MediaElement in the core package.
        // Using a Border as a placeholder for now to allow compilation.
        private readonly Border _mediaPlaceholder;
        private readonly Grid _overlay;
        private readonly Border _playButton;
        private bool _controls;
        private bool _started;

        public VideoHost()
        {
            Background = new SolidColorBrush(Colors.Black);

            _mediaPlaceholder = new Border
            {
                Background = new SolidColorBrush(Colors.DarkGray),
                Child = new TextBlock { Text = "Video", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Colors.White) }
            };

            _overlay = new Grid
            {
                Background = new SolidColorBrush(Color.Parse("#1E000000")),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            _playButton = new Border
            {
                Width = 48,
                Height = 48,
                CornerRadius = new CornerRadius(24),
                Background = new SolidColorBrush(Color.Parse("#C8000000")),
                BorderBrush = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "▶", Foreground = new SolidColorBrush(Colors.White), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };

            _overlay.Children.Add(_playButton);

            Children.Add(_mediaPlaceholder);
            Children.Add(_overlay);

            Tapped += OnTapped;
        }

        public void SetControls(bool showControls)
        {
            _controls = showControls;
            _overlay.IsVisible = showControls;
        }

        public void SetPoster(IImage poster)
        {
            // Optional: in future overlay an Image before playback
        }

        public void SetSource(Uri uri)
        {
            // Placeholder: just log or store uri
        }

        private void OnTapped(object sender, TappedEventArgs e)
        {
            try
            {
                if (!_started)
                {
                    _started = true;
                    _overlay.IsVisible = false;
                    // _media.Play();
                    return;
                }
                // Toggle play/pause logic would go here
            }
            catch { }
        }
    }
}

