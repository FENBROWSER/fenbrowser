using FenBrowser.Media.Element;
using static FenBrowser.Media.Tests.Element.MediaElementTestKit;

namespace FenBrowser.Media.Tests.Element;

/// <summary>
/// The user agent's background policy (MEDIA_ENGINE_DESIGN section 5): the element tells
/// its resource when nothing is showing its pictures, and a muted video that started
/// itself is paused while it is out of sight.
/// </summary>
public class MediaElementVisibilityTests
{
    [Fact]
    public void TheResourceLearnsWhetherAnythingIsShowingThePictures()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);
        Assert.True(host.Resource!.VideoVisible);

        element.IsVideoVisible = false;
        Assert.False(host.Resource.VideoVisible);

        element.IsVideoVisible = true;
        Assert.True(host.Resource.VideoVisible);
    }

    [Fact]
    public void AMutedAutoplayingVideoIsPausedOutOfSightAndPlaysAgainWhenItComesBack()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData, h =>
        {
            h.HasAutoplayAttribute = true;
            h.HasMutedAttribute = true;
        });
        Assert.False(element.Paused);
        host.TakeEvents();

        element.IsVideoVisible = false;
        host.Run();
        Assert.True(element.Paused);
        Assert.Contains("pause", host.TakeEvents());

        element.IsVideoVisible = true;
        host.Run();
        Assert.False(element.Paused);
        Assert.Contains("play", host.TakeEvents());
    }

    [Fact]
    public void AnAudibleAutoplayingVideoKeepsPlayingOutOfSight()
    {
        var (_, element, _) = Loaded(MediaReadyState.HaveEnoughData, h =>
        {
            h.HasAutoplayAttribute = true;
            h.IsAllowedToPlay = true;
        });
        Assert.False(element.Paused);

        element.IsVideoVisible = false;
        Assert.False(element.Paused);
    }

    [Fact]
    public void AVideoThePageAskedToPlayIsNeverPausedOutOfSight()
    {
        var (_, element, _) = Loaded(MediaReadyState.HaveEnoughData, h => h.HasMutedAttribute = true);
        element.Play();
        Assert.False(element.Paused);

        element.IsVideoVisible = false;
        Assert.False(element.Paused);
    }

    [Fact]
    public void APauseWhileOutOfSightStaysPausedOnTheWayBack()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData, h =>
        {
            h.HasAutoplayAttribute = true;
            h.HasMutedAttribute = true;
        });
        element.IsVideoVisible = false;
        host.Run();
        Assert.True(element.Paused);

        element.Pause();
        host.Run();

        element.IsVideoVisible = true;
        host.Run();
        Assert.True(element.Paused);
    }

    [Fact]
    public void AnAudioElementIsNeverPausedForBeingOutOfSight()
    {
        var (_, element, _) = Loaded(MediaReadyState.HaveEnoughData, h =>
        {
            h.IsVideo = false;
            h.HasAutoplayAttribute = true;
            h.HasMutedAttribute = true;
        });
        Assert.False(element.Paused);

        element.IsVideoVisible = false;
        Assert.False(element.Paused);
    }
}
