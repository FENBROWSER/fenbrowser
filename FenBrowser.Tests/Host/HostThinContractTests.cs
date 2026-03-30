using System.Linq;
using FenBrowser.FenEngine.Interaction;
using FenBrowser.Host.Context;
using FenBrowser.Host.ProcessIsolation;
using Xunit;

namespace FenBrowser.Tests.Host
{
    public class HostThinContractTests
    {
        [Fact]
        public void RendererInputEvent_NormalizesPayloads_AndTracksMeaningfulState()
        {
            var pointer = new RendererInputEvent
            {
                Type = RendererInputEventType.MouseUp,
                X = float.NaN,
                Y = float.PositiveInfinity,
                Button = -7,
                EmitClick = true
            };

            Assert.Equal(0f, pointer.X);
            Assert.Equal(0f, pointer.Y);
            Assert.Equal(0, pointer.Button);
            Assert.True(pointer.IsPointerEvent);
            Assert.True(pointer.ShouldEmitClick);

            var key = new RendererInputEvent
            {
                Type = RendererInputEventType.KeyDown,
                Key = "  Enter  ",
                Text = "   ",
                DeltaX = float.NegativeInfinity
            };

            Assert.True(key.IsKeyboardEvent);
            Assert.True(key.IsMeaningful);
            Assert.Equal("Enter", key.Key);
            Assert.Equal(string.Empty, key.Text);
            Assert.Equal(0f, key.DeltaX);

            var emptyText = new RendererInputEvent
            {
                Type = RendererInputEventType.TextInput,
                Text = "\r\n\t"
            };

            Assert.False(emptyText.IsMeaningful);
        }

        [Fact]
        public void ContextMenuItem_NormalizesAndProtectsInvocationState()
        {
            int invoked = 0;
            var item = ContextMenuItem.Create("  Copy  ", () => invoked++, " Ctrl+C ");

            Assert.Equal("Copy", item.Label);
            Assert.Equal("Ctrl+C", item.Shortcut);
            Assert.True(item.CanInvoke);
            Assert.True(item.Invoke());
            Assert.Equal(1, invoked);

            var disabled = ContextMenuItem.Create("Paste", null, enabled: true);
            Assert.False(disabled.CanInvoke);
            Assert.False(disabled.Invoke());

            var separator = ContextMenuItem.Separator();
            Assert.True(separator.IsSeparator);
            Assert.False(separator.IsEnabled);
            Assert.False(separator.CanInvoke);
            Assert.False(separator.Invoke());
            Assert.Equal(string.Empty, separator.Label);
        }

        [Fact]
        public void ContextMenuBuilder_DisablesUnavailableCommands()
        {
            var items = ContextMenuBuilder.Build(
                HitTestResult.None,
                "https://fenbrowser.dev/",
                hasSelection: false,
                canPaste: false,
                onNavigate: _ => { },
                onCopy: null,
                onPaste: null,
                onSelectAll: null,
                onReload: null,
                onBack: null,
                onForward: null,
                onOpenInNewTab: null,
                onCopyLink: null,
                onViewPageSource: null,
                onInspectElement: null);

            Assert.False(items.First(item => item.Label == "Back").IsEnabled);
            Assert.False(items.First(item => item.Label == "Forward").IsEnabled);
            Assert.False(items.First(item => item.Label == "Reload").IsEnabled);
            Assert.False(items.First(item => item.Label == "Select All").IsEnabled);
            Assert.False(items.First(item => item.Label == "View Page Source").IsEnabled);
            Assert.False(items.First(item => item.Label == "Inspect").IsEnabled);
        }
    }
}
