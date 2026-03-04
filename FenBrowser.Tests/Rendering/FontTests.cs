using System;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    [Collection("Engine Tests")]
    public class FontTests
    {
        [Fact]
        public void ParseAndRegister_ParsesDescriptorCorrectly()
        {
            FontRegistry.Clear();
            string css = @"
                font-family: 'MyFont';
                src: url('http://example.com/font.woff2');
                font-weight: bold;
                font-style: italic;
            ";

            FontRegistry.ParseAndRegister(css);

            Assert.True(FontRegistry.IsRegistered("MyFont"));
            
            // We can't easily peek into internal _fontFaces, but we can try to resolve it.
            // Since it's not loaded, TryResolve should return null (as per current implementation) 
            // but the fact IsRegistered returns true means it was parsed.
        }

        [Fact]
        public void ParseAndRegister_LocalFont_ResolvesImmediately()
        {
            FontRegistry.Clear();
            // Assuming "Arial" or "Segoe UI" exists on the test runner machine.
            // If not, this test might be flaky on Linux/Mac if Skia can't find them.
            // We'll use a generic one if possible, or skip if platform specific.
            
            string css = @"
                font-family: 'TestLocal';
                src: local('Arial'), local('Segoe UI'), local('DejaVu Sans');
            ";

            FontRegistry.ParseAndRegister(css);

            // Wait for async load (it's fired in background)
            // But RegisterFontFace trigger is fire-and-forget.
            // We can wait a bit or use LoadPendingFontsAsync if we exposed it.
            
            var task = FontRegistry.LoadPendingFontsAsync();
            task.Wait(2000);

            Assert.True(FontRegistry.IsRegistered("TestLocal"));
            
            // Attempt resolve
            var typeface = FontRegistry.TryResolve("TestLocal");
            // This assertion relies on the machine having one of the fonts. 
            // If it fails, we might need a more robust check or partial assertion.
            if (SKTypeface.FromFamilyName("Arial") != null) 
            {
                 Assert.NotNull(typeface);
                 // SkiaTypeface.FamilyName returns actual font name, not alias
                 Assert.Contains(typeface.FamilyName, new[] { "Arial", "Segoe UI", "DejaVu Sans" }); 
                 // SkiaTypeface.FamilyName usually returns the actual font family (e.g. "Arial"), not the alias.
                 // So we check if it's NOT null.
            }
        }

        [Fact]
        public async Task LoadFontFaceAsync_BadUrl_DoesNotCrash()
        {
            FontRegistry.Clear();
            string css = @"
                font-family: 'BadFont';
                src: url('http://invalid-domain-xyz-123.com/font.woff');
            ";

            FontRegistry.ParseAndRegister(css);
            
            // Should not throw
            await FontRegistry.LoadPendingFontsAsync();

            Assert.True(FontRegistry.IsRegistered("BadFont")); // Registered descriptor
            Assert.Null(FontRegistry.TryResolve("BadFont"));   // But not loaded
        }
    }
}
