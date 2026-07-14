using System;
using System.Text;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Performance
{
    public sealed class CssCommentStrippingAllocationTests
    {
        [Fact]
        public void StripComments_CommentHeavyStylesheet_HasBoundedAllocations()
        {
            var (source, expected) = BuildStylesheet(ruleCount: 256);

            AssertReferenceCompatibility();
            Assert.Equal(expected, CssLoader.StripComments(source));

            var noComments = ".plain{display:block}";
            Assert.Same(noComments, CssLoader.StripComments(noComments));

            CssLoader.StripComments(source);
            string result = null;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 100; iteration++)
            {
                result = CssLoader.StripComments(source);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.Equal(expected, result);
            Assert.InRange(allocated, 1, 1_773_000);
        }

        private static void AssertReferenceCompatibility()
        {
            string[] inputs =
            {
                null!,
                string.Empty,
                "plain",
                "/",
                "/*",
                "/**/",
                "a/* removed */b",
                "/* leading */a/* trailing */",
                "a/**//**/b",
                "a/* unterminated",
                "/* outer /* nested marker */tail"
            };

            foreach (var input in inputs)
            {
                Assert.Equal(StripCommentsReference(input), CssLoader.StripComments(input));
            }
        }

        private static string StripCommentsReference(string css)
        {
            if (string.IsNullOrEmpty(css)) return string.Empty;
            if (!css.Contains("/*")) return css;

            var output = new StringBuilder(css.Length);
            int index = 0;
            while (index < css.Length)
            {
                if (index + 1 < css.Length && css[index] == '/' && css[index + 1] == '*')
                {
                    index += 2;
                    while (index + 1 < css.Length && !(css[index] == '*' && css[index + 1] == '/'))
                    {
                        index++;
                    }

                    index += 2;
                }
                else
                {
                    output.Append(css[index]);
                    index++;
                }
            }

            return output.ToString();
        }

        private static (string Source, string Expected) BuildStylesheet(int ruleCount)
        {
            var source = new StringBuilder(ruleCount * 64);
            var expected = new StringBuilder(ruleCount * 40);

            for (var index = 0; index < ruleCount; index++)
            {
                source.Append("/* rule ").Append(index).Append(" metadata */");
                source.Append(".item-").Append(index).Append("{color:#123456;margin:").Append(index % 10).Append("px}");
                expected.Append(".item-").Append(index).Append("{color:#123456;margin:").Append(index % 10).Append("px}");
            }

            return (source.ToString(), expected.ToString());
        }
    }
}
