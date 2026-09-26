using FenBrowser.Core.Logging;

namespace FenBrowser.Tests.Logging;

// ADR-0006: media logging has its own category and subsystem.
public class MediaLogCategoryTests
{
    [Fact]
    public void Media_UsesItsOwnBit()
    {
        Assert.Equal(1 << 30, (int)LogCategory.Media);

        foreach (LogCategory other in Enum.GetValues<LogCategory>())
        {
            if (other is LogCategory.Media or LogCategory.All)
                continue;
            Assert.Equal(LogCategory.None, other & LogCategory.Media);
        }
    }

    [Fact]
    public void Media_IsIncludedInAll()
    {
        Assert.Equal(LogCategory.Media, LogCategory.All & LogCategory.Media);
    }

    [Fact]
    public void Media_RoundTripsThroughSubsystem()
    {
        Assert.Equal(LogSubsystem.Media, EngineLogCompatibility.FromLegacyCategory(LogCategory.Media));
        Assert.Equal(LogCategory.Media, EngineLogCompatibility.ToLegacyCategory(LogSubsystem.Media));
    }
}
