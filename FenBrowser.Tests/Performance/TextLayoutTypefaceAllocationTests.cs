using System;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Performance
{
    [Collection("Performance diagnostics")]
    public sealed class TextLayoutTypefaceAllocationTests
    {
        private const string RegisteredFamily = "FenBrowser Performance Registered Face";

        [Fact]
        public void ResolveTypeface_RegisteredFamily_HasBoundedManagedAllocations()
        {
            var systemFamily = SKTypeface.Default.FamilyName;
            Assert.False(string.IsNullOrWhiteSpace(systemFamily));
            FontRegistry.Register(RegisteredFamily, systemFamily);
            var expected = TextLayoutHelper.ResolveTypeface(
                RegisteredFamily,
                string.Empty,
                400,
                SKFontStyleSlant.Upright);

            SKTypeface resolved = null;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 10_000; iteration++)
            {
                resolved = TextLayoutHelper.ResolveTypeface(
                    RegisteredFamily,
                    string.Empty,
                    400,
                    SKFontStyleSlant.Upright);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.Same(expected, resolved);
            Assert.InRange(allocated, 1, 2_241_000);
        }

        [Fact]
        public void ResolveTypeface_AdmitsValidSystemTypeface_WhenCacheIsFull()
        {
            var systemFamily = SKTypeface.Default.FamilyName;
            Assert.False(string.IsNullOrWhiteSpace(systemFamily));

            for (var weight = 901; weight < 965; weight++)
            {
                TextLayoutHelper.ResolveTypeface(
                    systemFamily,
                    string.Empty,
                    weight,
                    SKFontStyleSlant.Upright);
            }

            using var expected = SKTypeface.FromFamilyName(
                systemFamily,
                (SKFontStyleWeight)965,
                SKFontStyleWidth.Normal,
                SKFontStyleSlant.Upright);
            Assert.NotNull(expected);

            var resolved = TextLayoutHelper.ResolveTypeface(
                systemFamily,
                string.Empty,
                965,
                SKFontStyleSlant.Upright);

            Assert.NotNull(resolved);
            Assert.NotSame(SKTypeface.Default, resolved);
            Assert.Equal(expected.FamilyName, resolved.FamilyName);
        }
    }
}
