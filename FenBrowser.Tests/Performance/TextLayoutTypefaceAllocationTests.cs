using System;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Performance
{
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
    }
}
