using System.Linq;
using FenBrowser.Core.WebIDL;
using Xunit;

namespace FenBrowser.Tests.WebIDL
{
    public class WebIdlBindingGeneratorTests
    {
        [Fact]
        public void Generate_IsDeterministicAcrossDefinitionOrder()
        {
            var parser = new WebIdlParser();
            var first = parser.Parse("""
                interface B {};
                interface A {};
                dictionary Z { DOMString value; };
                """);
            var second = parser.Parse("""
                dictionary Z { DOMString value; };
                interface A {};
                interface B {};
                """);

            var generator = new WebIdlBindingGenerator();
            var firstFiles = generator.Generate(first);
            var secondFiles = generator.Generate(second);

            Assert.Equal(
                firstFiles.Select(file => file.FileName).ToArray(),
                secondFiles.Select(file => file.FileName).ToArray());
            Assert.Equal(
                firstFiles.Select(file => file.SourceCode).ToArray(),
                secondFiles.Select(file => file.SourceCode).ToArray());
        }
    }
}
