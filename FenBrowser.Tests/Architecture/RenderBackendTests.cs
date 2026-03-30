using Xunit;
using SkiaSharp;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Backends;
using FenBrowser.FenEngine.Typography;
using System.Linq;

namespace FenBrowser.Tests.Architecture
{
    /// <summary>
    /// Compliance tests for Rule 4: Backend Abstraction.
    /// Uses HeadlessRenderBackend to verify rendering without GPU.
    /// </summary>
    public class RenderBackendTests
    {
        [Fact]
        public void HeadlessBackend_DrawRect_LogsCommand()
        {
            // Arrange
            var backend = new HeadlessRenderBackend();
            var rect = new SKRect(0, 0, 100, 50);
            var color = SKColors.Red;
            
            // Act
            backend.DrawRect(rect, color);
            
            // Assert
            Assert.Single(backend.CommandLog);
            Assert.Equal("DrawRect", backend.CommandLog[0].Name);
            Assert.Contains("100", backend.CommandLog[0].Args);
        }
        
        [Fact]
        public void HeadlessBackend_DrawText_LogsTextContent()
        {
            // Arrange
            var backend = new HeadlessRenderBackend();
            var origin = new SKPoint(10, 20);
            var text = "Hello FenBrowser";
            
            // Act
            backend.DrawText(text, origin, SKColors.Black, 16f, null);
            
            // Assert
            Assert.Single(backend.CommandLog);
            Assert.Equal("DrawText", backend.CommandLog[0].Name);
            Assert.Contains("Hello FenBrowser", backend.CommandLog[0].Args);
        }
        
        [Fact]
        public void HeadlessBackend_PushPopClip_MaintainsBalance()
        {
            // Arrange
            var backend = new HeadlessRenderBackend();
            var clipRect = new SKRect(0, 0, 200, 200);
            
            // Act
            backend.PushClip(clipRect);
            backend.DrawRect(new SKRect(10, 10, 50, 50), SKColors.Blue);
            backend.PopClip();
            
            // Assert
            Assert.Equal(3, backend.CommandLog.Count);
            Assert.Equal("PushClip", backend.CommandLog[0].Name);
            Assert.Equal("DrawRect", backend.CommandLog[1].Name);
            Assert.Equal("PopClip", backend.CommandLog[2].Name);
        }
        
        [Fact]
        public void HeadlessBackend_SaveRestore_ProperlySandboxed()
        {
            // Arrange
            var backend = new HeadlessRenderBackend();
            
            // Act
            backend.Save();
            backend.DrawRect(new SKRect(0, 0, 100, 100), SKColors.Green);
            backend.Restore();
            
            // Assert
            Assert.Equal(3, backend.CommandLog.Count);
            Assert.Equal("Save", backend.CommandLog[0].Name);
            Assert.Equal("Restore", backend.CommandLog[2].Name);
        }
        
        [Fact]
        public void HeadlessBackend_Clear_LogsBackgroundColor()
        {
            // Arrange
            var backend = new HeadlessRenderBackend();
            
            // Act
            backend.Clear(SKColors.White);
            
            // Assert
            Assert.Single(backend.CommandLog);
            Assert.Contains(SKColors.White.ToString(), backend.CommandLog[0].Args);
        }
        
        [Fact]
        public void HeadlessBackend_LoggingCanBeDisabled()
        {
            // Arrange
            var backend = new HeadlessRenderBackend();
            backend.LogCommands = false;
            
            // Act
            backend.DrawRect(new SKRect(0, 0, 50, 50), SKColors.Red);
            backend.DrawText("test", new SKPoint(0, 0), SKColors.Black, 12f, null);
            
            // Assert
            Assert.Empty(backend.CommandLog);
        }

        [Fact]
        public void HeadlessBackend_ExposesFilterAndCustomPaintThroughInterface()
        {
            var backend = new HeadlessRenderBackend();

            backend.PushFilter(SKImageFilter.CreateBlur(2, 2));
            backend.ApplyBackdropFilter(new SKRect(0, 0, 100, 100), SKImageFilter.CreateBlur(1, 1));
            backend.ExecuteCustomPaint((_, _) => { }, new SKRect(0, 0, 20, 20));
            backend.PopFilter();

            Assert.Contains(backend.CommandLog, command => command.Name == "PushFilter");
            Assert.Contains(backend.CommandLog, command => command.Name == "ApplyBackdropFilter");
            Assert.Contains(backend.CommandLog, command => command.Name == "ExecuteCustomPaint");
            Assert.Contains(backend.CommandLog, command => command.Name == "PopFilter");
        }

        [Fact]
        public void HeadlessBackend_RestoreToSaveDepth_ResetsDepth()
        {
            var backend = new HeadlessRenderBackend();

            backend.Save();
            backend.PushLayer(0.5f);
            Assert.True(backend.SaveDepth >= 2);

            backend.RestoreToSaveDepth(0);

            Assert.Equal(0, backend.SaveDepth);
            Assert.Contains(backend.CommandLog, command => command.Name == "RestoreToSaveDepth");
        }
    }
}
