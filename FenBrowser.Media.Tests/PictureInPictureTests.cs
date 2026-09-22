using FenBrowser.Media.PictureInPicture;

namespace FenBrowser.Media.Tests;

/// <summary>
/// One document's Picture-in-Picture state machine: who is being shown, what their window
/// reads, and which requests are refused and why.
/// </summary>
public class PictureInPictureTests
{
    private static PictureInPictureCandidate Ready(int width = 1280, int height = 720, bool disabled = false) =>
        new(HasMetadata: true, HasVideoTrack: true, DisablePictureInPicture: disabled, VideoWidth: width, VideoHeight: height);

    [Fact]
    public void ARequestWithAUserGestureShowsTheElementAndOpensItsWindow()
    {
        var controller = new PictureInPictureController();
        var video = new object();

        var change = controller.Request(video, Ready(), hasTransientActivation: true);

        Assert.True(change.Succeeded);
        Assert.Same(video, change.Entered);
        Assert.Null(change.Left);
        Assert.Same(video, controller.Element);
        Assert.NotNull(change.Window);
        Assert.True(change.Window.IsOpen);
        Assert.True(change.Window.Width > 0);
        Assert.True(change.Window.Height > 0);

        // The window keeps the video's aspect ratio, which is what the window test checks.
        Assert.Equal(1280d / 720d, (double)change.Window.Width / change.Window.Height, 2);
    }

    [Fact]
    public void ARequestWithoutAUserGestureIsRefused()
    {
        var controller = new PictureInPictureController();
        var change = controller.Request(new object(), Ready(), hasTransientActivation: false);

        Assert.Equal(PictureInPictureRefusal.NotAllowed, change.Refusal);
        Assert.Null(controller.Element);
    }

    [Fact]
    public void AnElementWithNothingToShowIsRefusedWhateverTheGesture()
    {
        var controller = new PictureInPictureController();

        Assert.Equal(
            PictureInPictureRefusal.InvalidState,
            controller.Request(new object(), Ready() with { HasMetadata = false }, hasTransientActivation: true).Refusal);
        Assert.Equal(
            PictureInPictureRefusal.InvalidState,
            controller.Request(new object(), Ready() with { HasVideoTrack = false }, hasTransientActivation: true).Refusal);
        Assert.Equal(
            PictureInPictureRefusal.InvalidState,
            controller.Request(new object(), Ready(disabled: true), hasTransientActivation: true).Refusal);
        Assert.Null(controller.Element);
    }

    [Fact]
    public void ADocumentThatDoesNotOfferPictureInPictureRefusesEveryRequest()
    {
        var controller = new PictureInPictureController { Enabled = false };
        Assert.Equal(
            PictureInPictureRefusal.NotSupported,
            controller.Request(new object(), Ready(), hasTransientActivation: true).Refusal);
    }

    [Fact]
    public void AsecondRequestForTheSameElementChangesNothingAndReturnsTheSameWindow()
    {
        var controller = new PictureInPictureController();
        var video = new object();

        var first = controller.Request(video, Ready(), hasTransientActivation: true);
        var second = controller.Request(video, Ready(), hasTransientActivation: true);

        Assert.True(second.Succeeded);
        Assert.Same(first.Window, second.Window);
        Assert.Equal(first.Window!.Width, second.Window!.Width);
        // Nothing entered and nothing left, so neither event fires a second time.
        Assert.Null(second.Entered);
        Assert.Null(second.Left);
    }

    [Fact]
    public void ShowingASecondElementClosesTheFirstElementsWindow()
    {
        var controller = new PictureInPictureController();
        var first = new object();
        var second = new object();

        var opened = controller.Request(first, Ready(), hasTransientActivation: true);
        // No gesture needed: something is already in Picture-in-Picture in this document.
        var swapped = controller.Request(second, Ready(640, 480), hasTransientActivation: false);

        Assert.True(swapped.Succeeded);
        Assert.Same(second, controller.Element);
        Assert.Same(first, swapped.Left);
        Assert.Same(opened.Window, swapped.LeftWindow);

        Assert.Equal(0, opened.Window!.Width);
        Assert.Equal(0, opened.Window.Height);
        Assert.False(opened.Window.IsOpen);
        Assert.True(swapped.Window!.Width > 0);
    }

    [Fact]
    public void ExitingLeavesNothingShowingAndZeroesTheWindow()
    {
        var controller = new PictureInPictureController();
        var video = new object();
        var entered = controller.Request(video, Ready(), hasTransientActivation: true);

        var exit = controller.Exit();

        Assert.True(exit.Succeeded);
        Assert.Same(video, exit.Left);
        Assert.Same(entered.Window, exit.LeftWindow);
        Assert.Null(controller.Element);
        Assert.Equal(0, entered.Window!.Width);
        Assert.Equal(0, entered.Window.Height);
    }

    [Fact]
    public void ExitingWithNothingShowingIsRefused()
    {
        var controller = new PictureInPictureController();
        Assert.Equal(PictureInPictureRefusal.InvalidState, controller.Exit().Refusal);
    }

    [Fact]
    public void OnlyTheElementBeingShownIsTakenOutOfPictureInPicture()
    {
        var controller = new PictureInPictureController();
        var shown = new object();
        var other = new object();
        controller.Request(shown, Ready(), hasTransientActivation: true);

        Assert.False(controller.ExitIfShowing(other).Succeeded);
        Assert.Same(shown, controller.Element);

        Assert.True(controller.ExitIfShowing(shown).Succeeded);
        Assert.Null(controller.Element);
    }

    [Fact]
    public void AnElementShownAgainAfterLeavingGetsItsOwnWindowBack()
    {
        // The page may still be holding the window it was given, with its listeners on it.
        var controller = new PictureInPictureController();
        var video = new object();

        var first = controller.Request(video, Ready(), hasTransientActivation: true);
        controller.Exit();
        var again = controller.Request(video, Ready(), hasTransientActivation: true);

        Assert.Same(first.Window, again.Window);
        Assert.True(again.Window!.IsOpen);
        Assert.True(again.Window.Width > 0);
    }

    [Fact]
    public void AResizeIsOnlyReportedWhenTheSizeActuallyChanges()
    {
        var controller = new PictureInPictureController();
        var entered = controller.Request(new object(), Ready(), hasTransientActivation: true);

        Assert.False(controller.Resize(entered.Window!.Width, entered.Window.Height));
        Assert.True(controller.Resize(320, 180));
        Assert.Equal(320, entered.Window.Width);

        // A closed window never resizes again.
        controller.Exit();
        Assert.False(controller.Resize(640, 360));
        Assert.Equal(0, entered.Window.Width);
    }

    [Fact]
    public void AVeryWideVideoStillGetsAWindowWithBothEdgesVisible()
    {
        var controller = new PictureInPictureController();
        var entered = controller.Request(new object(), Ready(2560, 240), hasTransientActivation: true);

        Assert.True(entered.Window!.Height >= 160);
        Assert.Equal(2560d / 240d, (double)entered.Window.Width / entered.Window.Height, 1);
    }
}
