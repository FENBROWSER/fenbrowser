using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Media;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Js.Runtime;
using FenBrowser.Media.Element;
using FenBrowser.Media.PictureInPicture;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// The realm side of the Picture-in-Picture API (https://w3c.github.io/picture-in-picture/).
/// The state machine is <see cref="PictureInPictureController"/>; the interfaces, the
/// events and the window objects live in the realm as script, because a page holds on to
/// the window it was handed and expects the same object back.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private PictureInPictureController _pictureInPicture;

    private PictureInPictureController PictureInPicture =>
        _pictureInPicture ??= new PictureInPictureController(MediaEngineServices.Log);

    private void InstallFenJsPictureInPicture()
    {
        Native("__fenPipEnabled", 0, _ => JsValue.FromBoolean(PictureInPicture.Enabled));

        Native("__fenPipElement", 0, _ =>
            PictureInPicture.Element is Element element ? ToHostNodeOrNull(element) : JsValue.Null);

        // §4.1 requestPictureInPicture() steps 1-5, at the moment the page calls: the
        // element's eligibility and the activation, which is spent here whether or not the
        // task that follows succeeds. A second request inside the same gesture therefore
        // has nothing left to spend, which is what the specification asks for.
        Native("__fenPipCheck", 1, args =>
        {
            if (!TryPipCandidate(args, out var element, out var candidate))
                return PipResult(PictureInPictureRefusal.InvalidState);

            var refusal = PictureInPicture.CheckRequest(element, candidate, HasTransientActivation, out bool activationNeeded);
            if (activationNeeded && refusal == PictureInPictureRefusal.None)
                _ = ConsumeTransientActivation();
            return PipResult(refusal);
        });

        // The rest of §4.1, which runs in a task. Whatever was being shown has already been
        // taken away by the prelude, so a page reading pictureInPictureElement from the
        // displaced element's handler sees nothing there.
        Native("__fenPipEnter", 1, args =>
        {
            if (!TryPipCandidate(args, out var element, out var candidate))
                return PipResult(PictureInPictureRefusal.InvalidState);
            var change = PictureInPicture.Enter(element, candidate);
            PublishPictureInPictureState(change);
            return PipResult(change);
        });

        // §4.2 exitPictureInPicture().
        Native("__fenPipExit", 0, _ =>
        {
            var change = PictureInPicture.Exit();
            PublishPictureInPictureState(change);
            return PipResult(change);
        });

        // The element leaves because its author, not the page's user, said so: the
        // disablePictureInPicture attribute became true on the element being shown.
        Native("__fenPipExitElement", 1, args =>
        {
            if (ResolveHostObjectOrNull(args.Count > 0 ? args[0] : JsValue.Undefined) is not Element element)
                return PipResult(PictureInPictureRefusal.InvalidState);
            var change = PictureInPicture.ExitIfShowing(element);
            PublishPictureInPictureState(change);
            return PipResult(change);
        });

        try
        {
            EvaluateWithFenJsRaw(PictureInPicturePrelude);
        }
        catch (Exception ex)
        {
            EngineLogCompat.Warn($"[FenJsBridge] picture-in-picture prelude failed: {ex.Message}", LogCategory.JavaScript);
        }

        void Native(string name, int length, Func<IReadOnlyList<JsValue>, JsValue> body) =>
            _interpreter.RegisterGlobalValue(name, _interpreter.AllocateNativeFunction(name, (_, args) => body(args), length: length));
    }

    /// <summary>
    /// The element a Picture-in-Picture native was called on and what the state machine
    /// needs to know about it. False when it is not a video element at all.
    /// </summary>
    private bool TryPipCandidate(IReadOnlyList<JsValue> args, out Element element, out PictureInPictureCandidate candidate)
    {
        candidate = default;
        element = ResolveHostObjectOrNull(args.Count > 0 ? args[0] : JsValue.Undefined) as Element;
        if (element is null || !IsVideoElement(element))
        {
            element = null;
            return false;
        }

        var controller = GetOrCreateMediaBinding(element).Controller;
        candidate = new PictureInPictureCandidate(
            HasMetadata: controller.ReadyState > MediaReadyState.HaveNothing,
            HasVideoTrack: controller.VideoWidth > 0 && controller.VideoHeight > 0,
            DisablePictureInPicture: element.HasAttribute("disablepictureinpicture"),
            VideoWidth: controller.VideoWidth,
            VideoHeight: controller.VideoHeight);
        return true;
    }

    private JsValue PipResult(PictureInPictureRefusal refusal) => PipResult(new PictureInPictureChange(refusal));

    private JsValue PipResult(in PictureInPictureChange change)
    {
        var result = new Dictionary<string, JsValue>
        {
            ["error"] = JsValue.FromString(change.Refusal switch
            {
                PictureInPictureRefusal.NotSupported => "NotSupportedError",
                PictureInPictureRefusal.InvalidState => "InvalidStateError",
                PictureInPictureRefusal.NotAllowed => "NotAllowedError",
                _ => string.Empty,
            }),
            ["width"] = JsValue.FromInt32(change.Window?.Width ?? 0),
            ["height"] = JsValue.FromInt32(change.Window?.Height ?? 0),
            ["entered"] = change.Entered is Element entered ? ToHostNodeOrNull(entered) : JsValue.Null,
            ["left"] = change.Left is Element left ? ToHostNodeOrNull(left) : JsValue.Null,
            ["element"] = PictureInPicture.Element is Element current ? ToHostNodeOrNull(current) : JsValue.Null,
        };
        return _interpreter.AllocateObject(result);
    }

    /// <summary>
    /// The element being shown in Picture-in-Picture right now, for the CSS engine's
    /// <c>:picture-in-picture</c> pseudo-class and for the host window that presents it.
    /// </summary>
    internal Element PictureInPictureElement => PictureInPicture.Element as Element;

    /// <summary>
    /// Hands the element being shown to the CSS engine, so <c>:picture-in-picture</c>
    /// matches it, and marks both the element that entered and the one that left for a
    /// fresh cascade - their computed style just changed without any attribute changing.
    /// </summary>
    private void PublishPictureInPictureState(in PictureInPictureChange change)
    {
        if (!change.Succeeded)
            return;

        ElementStateManager.Instance.PictureInPictureElement = PictureInPicture.Element as Element;
        Mark(change.Entered as Element, true);
        Mark(change.Left as Element, false);

        void Mark(Element element, bool showing)
        {
            if (element is null)
                return;
            // A video being shown outside the page is still being watched, so removing it
            // from the document must not pause it.
            GetOrCreateMediaBinding(element).Controller.InPictureInPicture = showing;
            element.MarkDirty(InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
        }
    }

    /// <summary>
    /// The Picture-in-Picture window changed size. Fires <c>resize</c> on the window the
    /// page holds, and only when the size really changed.
    /// </summary>
    internal void ReportPictureInPictureResize(int width, int height)
    {
        if (!PictureInPicture.Resize(width, height))
            return;
        CallPipHook("__fenPipFireResize", JsValue.FromInt32(width), JsValue.FromInt32(height));
    }

    /// <summary>
    /// The element being shown went away with its document, or was taken out of one. The
    /// page hears leavepictureinpicture, as it would for any other exit.
    /// </summary>
    internal void ExitPictureInPictureFor(Element element)
    {
        if (element is null || !ReferenceEquals(PictureInPicture.Element, element))
            return;
        CallPipHook("__fenPipExitForced", ToHostNodeOrNull(element));
    }

    private JsValue CallPipHook(string name, params JsValue[] args)
    {
        if (_realmAbandoned || _interpreter is null)
            return JsValue.Undefined;
        var hook = ReadGlobalValueOrUndefined(name);
        if (!_interpreter.CanCallValue(hook))
            return JsValue.Undefined;

        try
        {
            return _interpreter.InvokeFunction(hook, args ?? Array.Empty<JsValue>(), JsValue.Undefined);
        }
        catch (Exception ex)
        {
            EngineLogCompat.Warn($"[FenJsBridge] picture-in-picture hook {name} failed: {ex.Message}", LogCategory.JavaScript);
            return JsValue.Undefined;
        }
    }
}
