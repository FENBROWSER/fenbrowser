using System.Text.RegularExpressions;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Svg.Fuzz;

// Property: whatever custom property names and values the document cascade
// hands the inline SVG bridge, the root declarations it emits are a flat list
// of "name: value;" entries. No value can end its declaration early, open a
// block, or smuggle in a declaration of its own, and the output stays bounded.
public sealed partial class InlineContextFuzz
{
    [Theory]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(403)]
    [InlineData(404)]
    public void RootDeclarationsCannotBeInjected(int seed)
    {
        var mutator = new SvgMutator(seed);
        for (int iteration = 0; iteration < FuzzSettings.Iterations; iteration++)
        {
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            var references = new System.Text.StringBuilder();
            int count = mutator.Random.Next(12);
            for (int i = 0; i < count; i++)
            {
                string name = "--" + (mutator.Random.Next(4) == 0 ? mutator.RandomText(6) : "p" + i);
                properties[name] = mutator.Random.Next(3) switch
                {
                    0 => mutator.PickValue(),
                    1 => "var(--p" + mutator.Random.Next(12) + ")",
                    _ => mutator.RandomText(48)
                };
                references.Append("var(").Append(name).Append(") ");
            }

            string input = references.ToString();
            FuzzCase.Run(nameof(InlineContextFuzz), seed, iteration, input, text =>
            {
                string output = InlineSvgContext.BuildRootDeclarations(
                    text, new SKColor((uint)mutator.Random.Next()), properties, out int withheld);
                FuzzCase.Check(withheld >= 0, "the withheld count is non-negative");
                FuzzCase.Check(output.Length <= InlineSvgContext.MaxDeclarationChars + 64,
                    "the declaration block is bounded");

                string[] declarations = output.Split(';', StringSplitOptions.RemoveEmptyEntries |
                                                          StringSplitOptions.TrimEntries);
                FuzzCase.Check(declarations.Length >= 1 && declarations[0].StartsWith("color: ", StringComparison.Ordinal),
                    "the color declaration comes first");
                foreach (string declaration in declarations.Skip(1))
                {
                    Match match = Declaration().Match(declaration);
                    FuzzCase.Check(match.Success, "every entry is a custom property declaration");
                    FuzzCase.Check(properties.ContainsKey(match.Groups["name"].Value),
                        "only known custom properties are emitted");
                    FuzzCase.Check(InlineSvgContext.IsSafeCustomPropertyValue(match.Groups["value"].Value),
                        "every emitted value passes validation");
                }
            });
        }
    }

    [GeneratedRegex(@"^(?<name>--[^\s:;{}]+): (?<value>[^;{}]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Declaration();
}
