using System.Text;
using FenBrowser.FenEngine.Svg;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Svg.Fuzz;

// Property: path data and every value grammar reject or accept without
// throwing, and whatever they accept is finite. A non-finite coordinate or
// matrix that escapes a parser reaches Skia as geometry, which is how hangs and
// huge allocations start.
public sealed class ValueParserFuzz
{
    private static readonly string[] PathCommands =
        { "M", "m", "L", "l", "H", "h", "V", "v", "C", "c", "S", "s", "Q", "q", "T", "t", "A", "a", "Z", "z" };

    private static readonly string[] Numbers =
        { "0", "-0", "1", "-1", ".5", "-.5", "1e38", "-1e38", "3.4e38", "1e-45", "1e39", "NaN", "Infinity",
          "1.2.3", "--1", "+1", "1e", "0x10", "٣", "1,", ",", " ", "\t", "\n" };

    [Theory]
    [InlineData(201)]
    [InlineData(202)]
    [InlineData(203)]
    [InlineData(204)]
    public void PathDataStaysFinite(int seed)
    {
        var mutator = new SvgMutator(seed);
        for (int iteration = 0; iteration < FuzzSettings.Iterations; iteration++)
        {
            FuzzCase.Run("PathParserFuzz", seed, iteration, RandomPath(mutator.Random), input =>
            {
                var report = new SvgParseReport();
                SvgPathParser.TryBuildPath(input.AsSpan(), out SKPath path, report);
                using (path)
                {
                    SKRect bounds = path.Bounds;
                    FuzzCase.Check(
                        SvgValues.IsFinite(bounds.Left) && SvgValues.IsFinite(bounds.Top) &&
                        SvgValues.IsFinite(bounds.Right) && SvgValues.IsFinite(bounds.Bottom),
                        "accepted path geometry is finite");
                }
            });
        }
    }

    [Theory]
    [InlineData(211)]
    [InlineData(212)]
    [InlineData(213)]
    [InlineData(214)]
    public void ValueGrammarsStayFinite(int seed)
    {
        var mutator = new SvgMutator(seed);
        for (int iteration = 0; iteration < FuzzSettings.Iterations; iteration++)
        {
            string input = mutator.Random.Next(3) switch
            {
                0 => mutator.PickValue(),
                1 => mutator.Mutate(mutator.PickValue()),
                _ => mutator.RandomText(64)
            };

            FuzzCase.Run("ValueParserFuzz", seed, iteration, input, value =>
            {
                ReadOnlySpan<char> span = value.AsSpan();
                if (SvgValues.TryParseNumber(span, out float number))
                    FuzzCase.Check(SvgValues.IsFinite(number), "accepted numbers are finite");
                if (SvgValues.TryParseLength(span, out float length, out _))
                    FuzzCase.Check(SvgValues.IsFinite(length), "accepted lengths are finite");
                SvgValues.TryParseColor(span, out _);
                SvgValues.TryParsePaint(span, out _, out _, out _, out _);
                SvgValues.TryParseLocalReference(value, out _);
                if (SvgValues.TryParseTransformList(span, out SKMatrix matrix))
                    FuzzCase.Check(SvgValues.IsFinite(matrix), "accepted transforms are finite");
            });
        }
    }

    private static string RandomPath(Random random)
    {
        var builder = new StringBuilder();
        int segments = random.Next(64);
        for (int i = 0; i < segments; i++)
        {
            builder.Append(PathCommands[random.Next(PathCommands.Length)]);
            int arguments = random.Next(8);
            for (int j = 0; j < arguments; j++)
            {
                builder.Append(Numbers[random.Next(Numbers.Length)]);
                builder.Append(random.Next(3) == 0 ? "," : " ");
            }
        }
        return builder.ToString();
    }
}
