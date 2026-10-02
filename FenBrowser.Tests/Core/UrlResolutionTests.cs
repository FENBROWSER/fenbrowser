using System;
using FenBrowser.Core.Network;
using Xunit;

namespace FenBrowser.Tests.Core
{
    /// <summary>
    /// WHATWG URL 4.4: input without a scheme of its own resolves against the base. System.Uri
    /// alone read "//host/path" as a UNC file path, so a protocol-relative stylesheet or
    /// beacon on YouTube was requested as file://host/path.
    /// </summary>
    public class UrlResolutionTests
    {
        private static readonly Uri Base = new Uri("https://www.youtube.com/watch?v=jNQXAC9IVRw");

        [Theory]
        [InlineData("//fonts.googleapis.com/css2?family=Roboto", "https://fonts.googleapis.com/css2?family=Roboto")]
        [InlineData("  //www.youtube.com/api/stats/qoe?cpn=x\n", "https://www.youtube.com/api/stats/qoe?cpn=x")]
        [InlineData("\\\\fonts.googleapis.com/css", "https://fonts.googleapis.com/css")]
        [InlineData("/\\fonts.googleapis.com/css", "https://fonts.googleapis.com/css")]
        [InlineData("/api/stats", "https://www.youtube.com/api/stats")]
        [InlineData("embed/x?y=1", "https://www.youtube.com/embed/x?y=1")]
        [InlineData("http://example.test/a", "http://example.test/a")]
        [InlineData("file:///C:/x.txt", "file:///C:/x.txt")]
        public void ResolvesAgainstTheBase(string input, string expected)
        {
            Assert.True(UrlResolution.TryResolve(input, Base, out var resolved));
            Assert.Equal(expected, resolved.AbsoluteUri);
        }

        [Theory]
        [InlineData("//fonts.googleapis.com/css")]
        [InlineData("\\\\host\\share")]
        [InlineData("/path/only")]
        [InlineData("")]
        public void SchemeLessInput_IsNotAbsolute(string input)
        {
            Assert.False(UrlResolution.TryParseAbsolute(input, out _));
        }

        [Fact]
        public void SchemeRelativeInput_WithoutABase_DoesNotResolve()
        {
            Assert.False(UrlResolution.TryResolve("//fonts.googleapis.com/css", null, out _));
        }
    }
}
