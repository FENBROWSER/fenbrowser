using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// Implements the CSS View Transitions API Level 1 (W3C).
    /// https://www.w3.org/TR/css-view-transitions-1/
    ///
    /// Exposes <c>document.startViewTransition(callback?)</c> and the full
    /// ViewTransition object lifecycle with state machine and promise plumbing.
    /// </summary>
    public static class ViewTransitionAPI
    {
        /// <summary>
        /// ViewTransition lifecycle states per spec §4.2.
        /// </summary>
        internal enum TransitionState
        {
            PendingCapture,
            Capturing,
            PendingAnimation,
            Animating,
            Done
        }

        /// <summary>
        /// Internal data model capturing the pre- and post-transition geometry
        /// of one named participating element (§4.3).
        /// </summary>
        internal sealed class ViewTransitionElement
        {
            public string Name { get; set; }
            public double OldWidth { get; set; }
            public double OldHeight { get; set; }
            public double OldX { get; set; }
            public double OldY { get; set; }
            public double NewWidth { get; set; }
            public double NewHeight { get; set; }
            public double NewX { get; set; }
            public double NewY { get; set; }
        }

        /// <summary>
        /// Internal mutable state of a single live ViewTransition (§4.2).
        /// Wrapped in a sealed object so the ViewTransition FenObject can
        /// hold a reference and update promises as the lifecycle advances.
        /// </summary>
        private sealed class ViewTransitionLifecycle
        {
            public TransitionState State;
            public IExecutionContext Context;

            public List<ViewTransitionElement> CapturedElements;

            /// <summary>Promise that resolves when the transition fully completes.</summary>
            public FenObject FinishedPromise;
            public bool FinishedSettled;
            public FenValue FinishedResolve;
            public FenValue FinishedReject;

            /// <summary>Promise that resolves when pseudo-elements are ready.</summary>
            public FenObject ReadyPromise;
            public bool ReadySettled;
            public FenValue ReadyResolve;
            public FenValue ReadyReject;

            /// <summary>Promise that resolves when the update callback has completed.</summary>
            public FenObject UpdateCallbackDonePromise;
            public bool UpdateCallbackDoneSettled;
            public FenValue UpdateCallbackDoneResult;

            public bool Skipped;
        }

        /// <summary>
        /// Creates the <c>startViewTransition</c> method to be attached on every
        /// document's object.
        /// </summary>
        /// <param name="context">Active execution context.</param>
        /// <returns>A callable <see cref="FenValue"/> wrapping <c>startViewTransition</c>.</returns>
        public static FenValue CreateStartViewTransitionMethod(IExecutionContext context)
        {
            var method = new FenFunction("startViewTransition", (args, thisVal) =>
            {
                FenValue updateCallback = FenValue.Undefined;
                if (args.Length > 0 && (args[0].IsFunction || args[0].IsObject))
                {
                    updateCallback = args[0];
                }

                return FenValue.FromObject(CreateViewTransitionObject(context, updateCallback));
            });

            return FenValue.FromFunction(method);
        }

        /// <summary>
        /// Creates a ViewTransition FenObject and initiates the full transition
        /// lifecycle per the W3C spec.
        /// </summary>
        /// <param name="context">Active execution context.</param>
        /// <param name="updateCallback">
        /// Optional async/sync JavaScript function that performs DOM mutations.
        /// <see cref="FenValue.Undefined"/> means transition current state only.
        /// </param>
        /// <returns>A ViewTransition FenObject with <c>finished</c>, <c>ready</c>,
        /// <c>updateCallbackDone</c>, and <c>skipTransition()</c>.</returns>
        public static FenObject CreateViewTransitionObject(IExecutionContext context, FenValue updateCallback)
        {
            var lifecycle = new ViewTransitionLifecycle
            {
                State = TransitionState.PendingCapture,
                Context = context,
                CapturedElements = new List<ViewTransitionElement>(),
                Skipped = false
            };

            var vt = new FenObject();
            lifecycle.FinishedPromise = CreateUnsolvedPromise(lifecycle, context, out var finishedResolve, out var finishedReject, "__vt__");
            lifecycle.FinishedResolve = finishedResolve;
            lifecycle.FinishedReject = finishedReject;

            lifecycle.ReadyPromise = CreateUnsolvedPromise(lifecycle, context, out var readyResolve, out var readyReject, "__ready__");
            lifecycle.ReadyResolve = readyResolve;
            lifecycle.ReadyReject = readyReject;

            lifecycle.UpdateCallbackDonePromise = CreateUnsolvedPromise(lifecycle, context, out var ucdResolve, out var ucdReject, "__ucd__");
            lifecycle.UpdateCallbackDoneResult = ucdResolve;

            vt.Set("finished", FenValue.FromObject(lifecycle.FinishedPromise));
            vt.Set("ready", FenValue.FromObject(lifecycle.ReadyPromise));
            vt.Set("updateCallbackDone", FenValue.FromObject(lifecycle.UpdateCallbackDonePromise));

            vt.Set("skipTransition", FenValue.FromFunction(new FenFunction("skipTransition", (skipArgs, skipThis) =>
            {
                SkipTransition(lifecycle);
                return FenValue.Undefined;
            })));

            vt.Set("__lifecycle__", FenValue.FromObject(StoreLifecycleAsFenObject(lifecycle)));

            StartTransitionLifecycle(lifecycle, updateCallback);

            return vt;
        }

        /// <summary>
        /// Statically attaches <c>startViewTransition</c> to an existing document FenObject.
        /// Call this during <c>SetupWindowEvents</c> / <c>SetupPermissions</c> or any
        /// document-object bootstrapping path.
        /// </summary>
        /// <param name="documentObj">The target document FenObject.</param>
        /// <param name="context">Active execution context.</param>
        public static void AttachToDocument(FenObject documentObj, IExecutionContext context)
        {
            if (documentObj == null || context == null)
            {
                return;
            }

            documentObj.Set("startViewTransition", CreateStartViewTransitionMethod(context));
        }

        // ---------------------------------------------------------------
        // Lifecycle engine (§4.2)
        // ---------------------------------------------------------------

        private static void StartTransitionLifecycle(ViewTransitionLifecycle lifecycle, FenValue updateCallback)
        {
            TransitionTo(lifecycle, TransitionState.PendingCapture);

            lifecycle.Context.ScheduleCallback(() =>
            {
                if (lifecycle.Skipped)
                {
                    return;
                }

                // Phase A: Capture old state
                TransitionTo(lifecycle, TransitionState.Capturing);
                SnapshotOldState(lifecycle);

                TransitionTo(lifecycle, TransitionState.PendingAnimation);

                // Phase B: Invoke the update callback (if any)
                if (updateCallback.IsFunction)
                {
                    InvokeUpdateCallback(lifecycle, updateCallback);
                }
                else
                {
                    // No callback — transition current snapshot immediately
                    SettleUpdateCallbackDone(lifecycle);
                }
            }, 0);
        }

        private static void SnapshotOldState(ViewTransitionLifecycle lifecycle)
        {
            lifecycle.CapturedElements.Clear();

            var context = lifecycle.Context;
            if (context == null)
            {
                return;
            }

            try
            {
                var doc = ResolveDocument(context);
                if (doc == null)
                {
                    return;
                }

                var docElement = doc.Get("documentElement", context);
                if (!docElement.IsObject)
                {
                    return;
                }

                var root = docElement.AsObject();
                CollectTransitionElements(root, context, lifecycle.CapturedElements, isOld: true);
            }
            catch
            {
                // Snapshot-best-effort; silently ignore failures.
            }
        }

        private static void InvokeUpdateCallback(ViewTransitionLifecycle lifecycle, FenValue updateCallback)
        {
            var context = lifecycle.Context;
            try
            {
                var result = updateCallback.AsFunction().Invoke(Array.Empty<FenValue>(), context);

                // If the callback returned a thenable, wait for it.
                if (result.IsObject)
                {
                    var resultObj = result.AsObject();
                    var then = resultObj.Get("then", context);
                    if (then.IsFunction)
                    {
                        then.AsFunction().Invoke(new FenValue[]
                        {
                            FenValue.FromFunction(new FenFunction("_onResolve", (resolveArgs, thisVal) =>
                            {
                                OnCallbackDone(lifecycle);
                                return FenValue.Undefined;
                            })),
                            FenValue.FromFunction(new FenFunction("_onReject", (rejectArgs, thisVal) =>
                            {
                                lifecycle.Context.ScheduleCallback(() => OnCallbackDone(lifecycle), 0);
                                return FenValue.Undefined;
                            }))
                        }, context);

                        return;
                    }
                }

                // Synchronous callback — settle immediately.
                OnCallbackDone(lifecycle);
            }
            catch
            {
                // Callback threw — still complete the transition.
                lifecycle.Context.ScheduleCallback(() => OnCallbackDone(lifecycle), 0);
            }
        }

        private static void OnCallbackDone(ViewTransitionLifecycle lifecycle)
        {
            if (lifecycle.Skipped)
            {
                return;
            }

            SettleUpdateCallbackDone(lifecycle);
        }

        private static void SettleUpdateCallbackDone(ViewTransitionLifecycle lifecycle)
        {
            if (lifecycle.UpdateCallbackDoneSettled)
            {
                return;
            }

            lifecycle.UpdateCallbackDoneSettled = true;
            InvokeResolve(lifecycle.UpdateCallbackDoneResult, FenValue.Undefined, lifecycle.Context);

            // Ready promise resolves now that pseudo-elements can be created.
            OnReady(lifecycle);
        }

        private static void OnReady(ViewTransitionLifecycle lifecycle)
        {
            if (lifecycle.Skipped)
            {
                return;
            }

            // Phase C: Create pseudo-element wrappers
            var pseudoRoot = BuildPseudoElementTree(lifecycle);

            if (!lifecycle.ReadySettled)
            {
                lifecycle.ReadySettled = true;
                InvokeResolve(lifecycle.ReadyResolve, FenValue.FromObject(pseudoRoot), lifecycle.Context);
            }

            // Phase D: Start animation
            TransitionTo(lifecycle, TransitionState.Animating);
            lifecycle.Context.ScheduleCallback(() => CompleteTransition(lifecycle), 300);
        }

        private static void CompleteTransition(ViewTransitionLifecycle lifecycle)
        {
            if (lifecycle.Skipped)
            {
                return;
            }

            TransitionTo(lifecycle, TransitionState.Done);
            SettleFinished(lifecycle);
        }

        private static void SettleFinished(ViewTransitionLifecycle lifecycle)
        {
            if (lifecycle.FinishedSettled)
            {
                return;
            }

            lifecycle.FinishedSettled = true;
            InvokeResolve(lifecycle.FinishedResolve, FenValue.Undefined, lifecycle.Context);
        }

        // ---------------------------------------------------------------
        // skipTransition() (§4.5)
        // ---------------------------------------------------------------

        private static void SkipTransition(ViewTransitionLifecycle lifecycle)
        {
            if (lifecycle.Skipped)
            {
                return;
            }

            lifecycle.Skipped = true;

            var wasReady = lifecycle.ReadySettled;
            if (!lifecycle.ReadySettled)
            {
                lifecycle.ReadySettled = true;
                InvokeResolve(lifecycle.ReadyResolve, FenValue.Undefined, lifecycle.Context);
            }

            if (!lifecycle.UpdateCallbackDoneSettled)
            {
                lifecycle.UpdateCallbackDoneSettled = true;
                InvokeResolve(lifecycle.UpdateCallbackDoneResult, FenValue.Undefined, lifecycle.Context);
            }

            TransitionTo(lifecycle, TransitionState.Done);
            SettleFinished(lifecycle);
        }

        // ---------------------------------------------------------------
        // State transition helper
        // ---------------------------------------------------------------

        private static void TransitionTo(ViewTransitionLifecycle lifecycle, TransitionState next)
        {
            lifecycle.State = next;
        }

        // ---------------------------------------------------------------
        // Pseudo-element tree builders (§4.7)
        // ---------------------------------------------------------------

        private static FenObject BuildPseudoElementTree(ViewTransitionLifecycle lifecycle)
        {
            var root = new FenObject();
            root.Set("__pseudo__", FenValue.FromString("::view-transition"));
            root.Set("__fixed__", FenValue.FromBoolean(true));

            var groups = new FenObject();

            // Add ::view-transition-group(root) for the root cross-fade
            var rootGroup = BuildNamedElementGroup("root", null, lifecycle);
            groups.Set("root", FenValue.FromObject(rootGroup));

            // Add groups for each captured named element
            foreach (var elem in lifecycle.CapturedElements)
            {
                var group = BuildNamedElementGroup(elem.Name, elem, lifecycle);
                groups.Set(elem.Name, FenValue.FromObject(group));
            }

            root.Set("__groups__", FenValue.FromObject(groups));

            return root;
        }

        private static FenObject BuildNamedElementGroup(string name, ViewTransitionElement geometry, ViewTransitionLifecycle lifecycle)
        {
            var group = new FenObject();
            group.Set("__pseudo__", FenValue.FromString("::view-transition-group"));
            group.Set("__name__", FenValue.FromString(name));

            var imagePair = new FenObject();
            imagePair.Set("__pseudo__", FenValue.FromString("::view-transition-image-pair"));

            var oldImage = BuildTransitionImage("::view-transition-old", geometry, isOld: true);
            var newImage = BuildTransitionImage("::view-transition-new", geometry, isOld: false);

            imagePair.Set("old", FenValue.FromObject(oldImage));
            imagePair.Set("new", FenValue.FromObject(newImage));

            group.Set("imagePair", FenValue.FromObject(imagePair));

            return group;
        }

        private static FenObject BuildTransitionImage(string pseudoTag, ViewTransitionElement geometry, bool isOld)
        {
            var img = new FenObject();
            img.Set("__pseudo__", FenValue.FromString(pseudoTag));

            if (geometry != null)
            {
                var w = isOld ? geometry.OldWidth : geometry.NewWidth;
                var h = isOld ? geometry.OldHeight : geometry.NewHeight;
                var x = isOld ? geometry.OldX : geometry.NewX;
                var y = isOld ? geometry.OldY : geometry.NewY;

                img.Set("__width__", FenValue.FromNumber(w));
                img.Set("__height__", FenValue.FromNumber(h));

                // Natural movement: morph from old→new position
                double tx = isOld ? 0 : geometry.NewX - geometry.OldX;
                double ty = isOld ? 0 : geometry.NewY - geometry.OldY;
                img.Set("__transformX__", FenValue.FromNumber(tx));
                img.Set("__transformY__", FenValue.FromNumber(ty));
            }

            img.Set("__blendMode__", FenValue.FromString("plus-lighter"));

            // Default cross-fade opacity for root
            if (string.Equals(pseudoTag, "::view-transition-root", StringComparison.Ordinal))
            {
                img.Set("__opacity__", FenValue.FromNumber(isOld ? 1.0 : 0.0));
            }
            else
            {
                img.Set("__opacity__", FenValue.FromNumber(isOld ? 1.0 : 1.0));
            }

            return img;
        }

        // ---------------------------------------------------------------
        // DOM traversal helpers
        // ---------------------------------------------------------------

        private static IObject ResolveDocument(IExecutionContext context)
        {
            if (context?.Environment == null)
            {
                return null;
            }

            if (context.Environment.TryGetLocal("document", out var doc) && doc.IsObject)
            {
                return doc.AsObject();
            }

            return null;
        }

        private static void CollectTransitionElements(IObject node, IExecutionContext context, List<ViewTransitionElement> elements, bool isOld)
        {
            if (node == null || context == null)
            {
                return;
            }

            try
            {
                var style = node.Get("style", context);
                string transitionName = null;

                if (style.IsObject)
                {
                    var styleObj = style.AsObject();
                    var nameVal = styleObj.Get("viewTransitionName", context);
                    if (!nameVal.IsUndefined && !nameVal.IsNull)
                    {
                        var raw = nameVal.ToString();
                        if (!string.IsNullOrEmpty(raw) && !string.Equals(raw, "none", StringComparison.OrdinalIgnoreCase))
                        {
                            transitionName = raw;
                        }
                    }
                }

                if (transitionName != null)
                {
                    var elem = new ViewTransitionElement { Name = transitionName };
                    FillElementGeometry(node, context, elem, isOld);
                    elements.Add(elem);
                }

                // Recurse into children
                var children = node.Get("children", context);
                if (children.IsObject)
                {
                    var childrenObj = children.AsObject();
                    var lengthVal = childrenObj.Get("length", context);
                    if (lengthVal.IsNumber)
                    {
                        int count = (int)lengthVal.ToNumber();
                        for (int i = 0; i < count; i++)
                        {
                            var child = childrenObj.Get(i.ToString(), context);
                            if (child.IsObject)
                            {
                                CollectTransitionElements(child.AsObject(), context, elements, isOld);
                            }
                        }
                    }
                }

                var childNodes = node.Get("childNodes", context);
                if (childNodes.IsObject)
                {
                    var cnObj = childNodes.AsObject();
                    var lenVal = cnObj.Get("length", context);
                    if (lenVal.IsNumber)
                    {
                        int count = (int)lenVal.ToNumber();
                        for (int i = 0; i < count; i++)
                        {
                            var child = cnObj.Get(i.ToString(), context);
                            if (child.IsObject)
                            {
                                CollectTransitionElements(child.AsObject(), context, elements, isOld);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Best-effort traversal; ignore failures.
            }
        }

        private static void FillElementGeometry(IObject node, IExecutionContext context, ViewTransitionElement elem, bool isOld)
        {
            try
            {
                var rect = node.Get("getBoundingClientRect", context);
                if (rect.IsFunction)
                {
                    var rectObj = rect.AsFunction().Invoke(Array.Empty<FenValue>(), context);
                    if (rectObj.IsObject)
                    {
                        var r = rectObj.AsObject();
                        var x = r.Get("x", context).ToNumber();
                        var y = r.Get("y", context).ToNumber();
                        var w = r.Get("width", context).ToNumber();
                        var h = r.Get("height", context).ToNumber();

                        if (isOld)
                        {
                            elem.OldX = x;
                            elem.OldY = y;
                            elem.OldWidth = w;
                            elem.OldHeight = h;
                        }
                        else
                        {
                            elem.NewX = x;
                            elem.NewY = y;
                            elem.NewWidth = w;
                            elem.NewHeight = h;
                        }
                    }
                }
            }
            catch
            {
                // Best-effort geometry capture.
            }
        }

        // ---------------------------------------------------------------
        // Promise infrastructure
        // ---------------------------------------------------------------

        private static FenObject CreateUnsolvedPromise(ViewTransitionLifecycle lifecycle, IExecutionContext context, out FenValue resolve, out FenValue reject, string tag)
        {
            FenValue capturedResolve = FenValue.Undefined;
            FenValue capturedReject = FenValue.Undefined;

            var executor = new FenFunction("_vt_executor", (execArgs, execThis) =>
            {
                capturedResolve = execArgs.Length > 0 ? execArgs[0] : FenValue.Undefined;
                capturedReject = execArgs.Length > 1 ? execArgs[1] : FenValue.Undefined;
                return FenValue.Undefined;
            });

            var promise = new JsPromise(FenValue.FromFunction(executor), context);

            resolve = capturedResolve;
            reject = capturedReject;

            return promise;
        }

        private static void InvokeResolve(FenValue resolve, FenValue value, IExecutionContext context)
        {
            if (resolve.IsFunction)
            {
                try
                {
                    resolve.AsFunction().Invoke(new FenValue[] { value }, context);
                }
                catch
                {
                    // Silently ignore post-settle errors.
                }
            }
        }

        // ---------------------------------------------------------------
        // Lifecycle FenObject backing store
        // ---------------------------------------------------------------

        private static FenObject StoreLifecycleAsFenObject(ViewTransitionLifecycle lifecycle)
        {
            var obj = new FenObject();
            obj.Set("__state__", FenValue.FromString(lifecycle.State.ToString()));
            obj.Set("__skipped__", FenValue.FromBoolean(lifecycle.Skipped));
            obj.NativeObject = lifecycle;
            return obj;
        }
    }
}
