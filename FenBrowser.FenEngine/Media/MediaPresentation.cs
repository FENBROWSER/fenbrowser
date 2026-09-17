using System.Runtime.CompilerServices;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.FenEngine.Media
{
    /// <summary>
    /// What layout and paint need to know about a media element (HTML §4.8.9): whether the
    /// poster frame is shown and the intrinsic size of the video, if any.
    /// </summary>
    public sealed record MediaPresentationState(bool ShowPoster, int VideoWidth, int VideoHeight)
    {
        /// <summary>An element no controller has touched: it shows its poster and has no video.</summary>
        public static readonly MediaPresentationState Initial = new(true, 0, 0);
    }

    /// <summary>
    /// The presentation state of every media element, written by the element's controller
    /// host on the script thread and read by layout and paint. Layout never sees the
    /// controller itself; this is the only seam between them.
    /// </summary>
    public static class MediaPresentation
    {
        private static readonly ConditionalWeakTable<Element, MediaPresentationState> s_states = new();

        public static MediaPresentationState Get(Element element)
        {
            if (element != null && s_states.TryGetValue(element, out var state))
                return state;
            return MediaPresentationState.Initial;
        }

        public static void Update(Element element, MediaPresentationState state)
        {
            if (element == null || state == null)
                return;
            s_states.AddOrUpdate(element, state);
        }
    }
}
