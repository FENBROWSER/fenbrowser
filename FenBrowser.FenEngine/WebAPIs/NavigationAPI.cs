using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// W3C Navigation API implementation.
    /// https://wicg.github.io/navigation-api/
    ///
    /// Provides window.navigation for intercepting and managing navigations programmatically.
    ///
    /// Architecture:
    ///   NavigationAPI (entry point) -> NavigationState (per-context state machine)
    ///   FenObject wrappers for: NavigationHistoryEntry, NavigationResult, NavigateEvent,
    ///   NavigationDestination, NavigationCurrentEntryChangeEvent, NavigationTransition
    ///
    /// Event dispatch order (spec 6.1):
    ///   1. "navigate" — cancellable; preventDefault() without intercept() cancels navigation
    ///   2a. "navigatesuccess" — on success
    ///   2b. "navigateerror" — on error
    ///   3. "currententrychange" — after entry list or current entry changes
    /// </summary>
    public static class NavigationAPI
    {
        private static long s_keyCounter;

        /// <summary>
        /// Creates the window.navigation object and attaches a navigate handler from the host.
        /// </summary>
        /// <param name="context">Execution context for the window.</param>
        /// <param name="navigateHandler">
        /// Invoked when a non-intercept navigation should proceed.
        /// string = url, object = the NavigationResult FenObject (for resolving committed promise externally).
        /// </param>
        /// <returns>A FenObject representing window.navigation.</returns>
        public static FenObject CreateNavigationObject(IExecutionContext context,
            Action<string, object> navigateHandler)
        {
            var navigation = new FenObject();
            var state = new NavigationState(context, navigation, navigateHandler);

            InitializeNavigationInternal(state);
            BindNavigationProperties(navigation, state);

            return navigation;
        }

        #region NavigationState

        /// <summary>
        /// Mutable per-window state for the Navigation API.
        /// Stored as __internal_navigationState on the navigation FenObject.
        /// </summary>
        private sealed class NavigationState
        {
            public IExecutionContext Context { get; }
            public FenObject NavigationObject { get; }
            public Action<string, object> NavigateHandler { get; }
            public List<FenObject> Entries { get; } = new();
            public int CurrentIndex { get; set; } = -1;
            public FenObject CurrentEntry { get; set; }
            public FenObject Transition { get; set; }
            public int EntryCounter { get; set; }

            // Event listener storage (single listener per type; spec allows multiple but single covers common case)
            public Dictionary<string, FenValue> EventListeners { get; } = new(StringComparer.Ordinal);
            // on-event-handler properties
            public FenValue OnNavigateHandler { get; set; } = FenValue.Null;
            public FenValue OnNavigateSuccessHandler { get; set; } = FenValue.Null;
            public FenValue OnNavigateErrorHandler { get; set; } = FenValue.Null;
            public FenValue OnCurrentEntryChangeHandler { get; set; } = FenValue.Null;

            // Active transition tracking
            public bool InNavigation { get; set; }
            public string LastNavigationKey { get; set; }

            public NavigationState(IExecutionContext context, FenObject navigationObject,
                Action<string, object> navigateHandler)
            {
                Context = context;
                NavigationObject = navigationObject;
                NavigateHandler = navigateHandler;
            }
        }

        #endregion

        #region Initialization

        private static void InitializeNavigationInternal(NavigationState state)
        {
            state.Entries.Clear();
            var currentUrl = state.Context?.CurrentUrl;
            var urlString = string.IsNullOrWhiteSpace(currentUrl) ? "about:blank" : currentUrl;
            var entry = CreateHistoryEntry("key-1", "id-1", urlString, FenValue.Undefined, 0);
            state.EntryCounter = 1;
            state.Entries.Add(entry);
            SetCurrentEntry(state, 0);
        }

        private static void BindNavigationProperties(FenObject navigation, NavigationState state)
        {
            navigation.Set("__internal_navigationState", FenValue.FromObject(new FenObject()));
            navigation.Set("__internal_stateRef", FenValue.FromObject(state.NavigationObject)); // dummy to anchor ref

            // Readonly properties
            navigation.Set("entries",
                FenValue.FromFunction(
                    new FenFunction("entries", (args, thisVal) =>
                        FenValue.FromObject(CreateEntriesArray(state)))));

            navigation.Set("currentEntry",
                FenValue.FromFunction(new FenFunction("get_currentEntry", (args, thisVal) =>
                    state.CurrentEntry == null ? FenValue.Null : FenValue.FromObject(state.CurrentEntry))));

            navigation.Set("canGoBack",
                FenValue.FromFunction(new FenFunction("get_canGoBack", (args, thisVal) =>
                    FenValue.FromBoolean(state.CurrentIndex > 0))));

            navigation.Set("canGoForward",
                FenValue.FromFunction(new FenFunction("get_canGoForward", (args, thisVal) =>
                    FenValue.FromBoolean(state.CurrentIndex >= 0 &&
                                         state.CurrentIndex < state.Entries.Count - 1))));

            navigation.Set("transition",
                FenValue.FromFunction(new FenFunction("get_transition", (args, thisVal) =>
                    state.Transition == null ? FenValue.Null : FenValue.FromObject(state.Transition))));

            // Navigation methods
            navigation.Set("navigate",
                FenValue.FromFunction(
                    new FenFunction("navigate", (args, thisVal) => Navigate(args, state, "push"))));

            navigation.Set("reload",
                FenValue.FromFunction(
                    new FenFunction("reload", (args, thisVal) => Reload(args, state))));

            navigation.Set("traverseTo",
                FenValue.FromFunction(
                    new FenFunction("traverseTo", (args, thisVal) => TraverseTo(args, state))));

            navigation.Set("back",
                FenValue.FromFunction(
                    new FenFunction("back", (args, thisVal) => Back(args, state))));

            navigation.Set("forward",
                FenValue.FromFunction(
                    new FenFunction("forward", (args, thisVal) => Forward(args, state))));

            navigation.Set("updateCurrentEntry",
                FenValue.FromFunction(
                    new FenFunction("updateCurrentEntry", (args, thisVal) =>
                        PerformUpdateCurrentEntry(args, state))));

            // EventTarget methods
            SetupEventTargetMethods(navigation, state);

            // Event handler IDL attributes (on* properties)
            navigation.Set("onnavigate", FenValue.Null);
            navigation.Set("onnavigatesuccess", FenValue.Null);
            navigation.Set("onnavigateerror", FenValue.Null);
            navigation.Set("oncurrententrychange", FenValue.Null);
        }

        #endregion

        #region HistoryEntry management

        private static FenObject CreateHistoryEntry(string key, string id, string url,
            FenValue entryState, long index)
        {
            var entry = new FenObject();
            entry.InternalClass = "NavigationHistoryEntry";

            entry.Set("__key", FenValue.FromString(key));
            entry.Set("__id", FenValue.FromString(id));
            entry.Set("__url", FenValue.FromString(url ?? "about:blank"));
            entry.Set("__state", entryState.IsNull || entryState.IsUndefined ? FenValue.Undefined : entryState);
            entry.Set("__index", FenValue.FromNumber(index));
            entry.Set("__sameDocument", FenValue.FromBoolean(true));

            // Public getters
            entry.Set("url",
                FenValue.FromFunction(new FenFunction("get_url", (args, thisVal) =>
                    FenValue.FromString(entry.Get("__url").ToString()))));

            entry.Set("key",
                FenValue.FromFunction(new FenFunction("get_key", (args, thisVal) =>
                    FenValue.FromString(entry.Get("__key").ToString()))));

            entry.Set("id",
                FenValue.FromFunction(new FenFunction("get_id", (args, thisVal) =>
                    FenValue.FromString(entry.Get("__id").ToString()))));

            entry.Set("index",
                FenValue.FromFunction(new FenFunction("get_index", (args, thisVal) =>
                    entry.Get("__index"))));

            entry.Set("sameDocument",
                FenValue.FromFunction(new FenFunction("get_sameDocument", (args, thisVal) =>
                    entry.Get("__sameDocument"))));

            entry.Set("getState",
                FenValue.FromFunction(new FenFunction("getState", (args, thisVal) =>
                    entry.Get("__state"))));

            return entry;
        }

        private static FenObject CreateEntriesArray(NavigationState state)
        {
            var arr = FenObject.CreateArray();
            for (int i = 0; i < state.Entries.Count; i++)
                arr.Set(i.ToString(), FenValue.FromObject(state.Entries[i]));

            arr.Set("length", FenValue.FromNumber(state.Entries.Count));
            return arr;
        }

        private static void SetCurrentEntry(NavigationState state, int index)
        {
            state.CurrentIndex = index;
            state.CurrentEntry = (index >= 0 && index < state.Entries.Count)
                ? state.Entries[index]
                : null;

            if (state.CurrentEntry != null)
                state.CurrentEntry.Set("__index", FenValue.FromNumber(index));
        }

        private static string GenerateKey()
        {
            s_keyCounter++;
            var guid = Guid.NewGuid().ToString("N");
            return $"nav-{s_keyCounter}-{guid.Substring(0, 8)}";
        }

        private static string GenerateId()
        {
            var guid = Guid.NewGuid().ToString("N");
            return $"id-{guid.Substring(0, 12)}";
        }

        private static FenObject AppendEntry(NavigationState state, string url, FenValue entryState)
        {
            // Truncate forward history (spec 7.3)
            if (state.CurrentIndex >= 0 && state.CurrentIndex < state.Entries.Count - 1)
                state.Entries.RemoveRange(state.CurrentIndex + 1,
                    state.Entries.Count - state.CurrentIndex - 1);

            state.EntryCounter++;
            var key = GenerateKey();
            var id = GenerateId();
            var newEntry = CreateHistoryEntry(
                key: key,
                id: id,
                url: url,
                entryState: entryState,
                index: state.Entries.Count);

            state.Entries.Add(newEntry);
            SetCurrentEntry(state, state.Entries.Count - 1);
            state.LastNavigationKey = key;
            return newEntry;
        }

        #endregion

        #region Navigation methods

        private static FenValue Navigate(FenValue[] args, NavigationState state, string defaultNavigationType)
        {
            var context = state.Context;
            if (args.Length < 1)
                return CreateNavigationResult(null, "TypeError: URL required", state);

            var url = args[0].ToString();
            if (string.IsNullOrWhiteSpace(url))
                return CreateNavigationResult(null, "TypeError: URL required", state);

            var info = FenValue.Undefined;
            var navigationType = defaultNavigationType;
            var entryState = FenValue.Undefined;

            if (args.Length > 1 && args[0].IsObject)
            {
                var options = args[1].AsObject();
                if (options != null)
                {
                    var histVal = options.Get("history");
                    navigationType = histVal.IsString
                        ? histVal.ToString()
                        : defaultNavigationType;

                    var stateVal = options.Get("state");
                    if (!stateVal.IsUndefined && !stateVal.IsNull)
                        entryState = stateVal;

                    info = options.Get("info");
                }
            }

            var prevEntry = state.CurrentEntry;
            var prevKey = prevEntry?.Get("__key").ToString() ?? string.Empty;
            var navType = NormalizeNavigationType(navigationType, state);

            EngineLogCompat.Debug(
                $"[NavigationAPI] navigate() url={url} type={navType}",
                LogCategory.JavaScript);

            // Build the destination
            var destination = BuildDestinationForNavigate(url, navType);

            // Fire the navigate event (step 1)
            var navigateEvent = CreateNavigateEvent(navType, destination, info,
                userInitiated: true, hashChange: false, signal: null, formData: null,
                downloadRequest: null);

            var shouldIntercept = !FireNavigateEvent(state, navigateEvent);

            // If preventDefault() was called and intercept() was not, cancel
            var defaultPrevented = navigateEvent.Get("__defaultPrevented").AsBoolean();
            var interceptCalled = navigateEvent.Get("__interceptCalled").AsBoolean();

            if (defaultPrevented && !interceptCalled)
            {
                EngineLogCompat.Warn(
                    $"[NavigationAPI] navigate cancelled by preventDefault (no intercept) url={url}",
                    LogCategory.JavaScript);
                return CreateNavigationResult(null, "AbortError: Navigation cancelled", state);
            }

            if (interceptCalled)
            {
                // intercept() called — app handles navigation, navigation is committed as same-document
                var interceptedResult = CreateNavigationResultObject(state);

                ScheduleMicrotask(context, () =>
                {
                    try
                    {
                        var newEntry = navType == "push"
                            ? AppendEntry(state, url, entryState)
                            : ReplaceCurrentEntry(state, url, entryState);

                        ResolveCommittedPromise(interceptedResult, newEntry, state);
                        ResolveFinishedPromise(interceptedResult, newEntry, state);

                        FireNavigateSuccessEvent(state,
                            CreateNavigateSuccessEvent(navType));
                        FireCurrentEntryChangeEvent(state, navType, prevEntry);
                    }
                    catch (Exception ex)
                    {
                        RejectResultPromises(interceptedResult,
                            $"OperationError: {ex.Message}", state);
                        FireNavigateErrorEvent(state,
                            CreateNavigateErrorEvent(navType, ex.Message));
                    }
                });

                return FenValue.FromObject(interceptedResult);
            }

            // Normal (non-intercept) navigation — delegate to host navigateHandler
            if (state.NavigateHandler != null)
            {
                var resultObj = CreateNavigationResultObject(state);
                var prevEntryCapture = prevEntry;
                var urlCapture = url;
                var navTypeCapture = navType;
                var entryStateCapture = entryState;

                ScheduleMicrotask(context, () =>
                {
                    try
                    {
                        var newEntry = navTypeCapture == "push"
                            ? AppendEntry(state, urlCapture, entryStateCapture)
                            : ReplaceCurrentEntry(state, urlCapture, entryStateCapture);

                        ResolveCommittedPromise(resultObj, newEntry, state);
                    }
                    catch (Exception ex)
                    {
                        RejectResultPromises(resultObj,
                            $"OperationError: {ex.Message}", state);
                    }
                });

                try
                {
                    state.NavigateHandler(url, resultObj);

                    ScheduleMicrotask(context, () =>
                    {
                        try
                        {
                            if (!IsResultRejected(resultObj))
                            {
                                var committedEntry = GetResultCommittedEntry(resultObj, state);
                                ResolveFinishedPromise(resultObj,
                                    committedEntry ?? state.CurrentEntry, state);
                                FireNavigateSuccessEvent(state,
                                    CreateNavigateSuccessEvent(navTypeCapture));
                                FireCurrentEntryChangeEvent(state, navTypeCapture,
                                    prevEntryCapture);
                            }
                        }
                        catch (Exception ex)
                        {
                            RejectResultPromises(resultObj,
                                $"OperationError: {ex.Message}", state);
                            FireNavigateErrorEvent(state,
                                CreateNavigateErrorEvent(navTypeCapture, ex.Message));
                        }
                    });
                }
                catch (Exception ex)
                {
                    RejectResultPromises(resultObj, $"OperationError: {ex.Message}", state);
                }

                return FenValue.FromObject(resultObj);
            }

            // Fallback: no handler, do same-document push
            var fallbackResult = CreateNavigationResultObject(state);

            ScheduleMicrotask(context, () =>
            {
                try
                {
                    var newEntry = AppendEntry(state, url, entryState);
                    ResolveCommittedPromise(fallbackResult, newEntry, state);
                    ResolveFinishedPromise(fallbackResult, newEntry, state);
                    FireNavigateSuccessEvent(state,
                        CreateNavigateSuccessEvent(navType));
                    FireCurrentEntryChangeEvent(state, navType, prevEntry);
                }
                catch (Exception ex)
                {
                    RejectResultPromises(fallbackResult,
                        $"OperationError: {ex.Message}", state);
                    FireNavigateErrorEvent(state,
                        CreateNavigateErrorEvent(navType, ex.Message));
                }
            });

            return FenValue.FromObject(fallbackResult);
        }

        private static FenValue Reload(FenValue[] args, NavigationState state)
        {
            var context = state.Context;
            var prevEntry = state.CurrentEntry;

            if (prevEntry == null)
                return CreateNavigationResult(null, "InvalidStateError: No current entry", state);

            var info = FenValue.Undefined;
            if (args.Length > 0 && args[0].IsObject)
            {
                var options = args[0].AsObject();
                info = options?.Get("info") ?? FenValue.Undefined;
            }

            var url = prevEntry.Get("__url").ToString();
            var destination = CreateDestination(url, prevEntry);
            var navigateEvent = CreateNavigateEvent("reload", destination, info,
                userInitiated: true, hashChange: false, signal: null, formData: null,
                downloadRequest: null);
            var shouldIntercept = !FireNavigateEvent(state, navigateEvent);

            var defaultPrevented = navigateEvent.Get("__defaultPrevented").AsBoolean();
            var interceptCalled = navigateEvent.Get("__interceptCalled").AsBoolean();

            if (defaultPrevented && !interceptCalled)
                return CreateNavigationResult(null,
                    "AbortError: Navigation cancelled", state);

            var resultObj = CreateNavigationResultObject(state);

            ScheduleMicrotask(context, () =>
            {
                try
                {
                    ResolveCommittedPromise(resultObj, prevEntry, state);
                    ResolveFinishedPromise(resultObj, prevEntry, state);
                    FireNavigateSuccessEvent(state,
                        CreateNavigateSuccessEvent("reload"));
                }
                catch (Exception ex)
                {
                    RejectResultPromises(resultObj,
                        $"OperationError: {ex.Message}", state);
                    FireNavigateErrorEvent(state,
                        CreateNavigateErrorEvent("reload", ex.Message));
                }
            });

            return FenValue.FromObject(resultObj);
        }

        private static FenValue TraverseTo(FenValue[] args, NavigationState state)
        {
            var context = state.Context;
            if (args.Length < 1)
                return CreateNavigationResult(null,
                    "TypeError: Key required", state);

            var key = args[0].ToString();
            if (string.IsNullOrWhiteSpace(key))
                return CreateNavigationResult(null,
                    "TypeError: Key required", state);

            var info = FenValue.Undefined;
            if (args.Length > 1 && args[1].IsObject)
            {
                var options = args[1].AsObject();
                info = options?.Get("info") ?? FenValue.Undefined;
            }

            var targetIndex = -1;
            FenObject targetEntry = null;
            for (int i = 0; i < state.Entries.Count; i++)
            {
                if (string.Equals(state.Entries[i].Get("__key").ToString(), key,
                    StringComparison.Ordinal))
                {
                    targetIndex = i;
                    targetEntry = state.Entries[i];
                    break;
                }
            }

            if (targetIndex < 0)
                return CreateNavigationResult(null,
                    $"InvalidAccessError: No entry with key '{key}'", state);

            return PerformTraversal(state, targetIndex, targetEntry, "traverse", info);
        }

        private static FenValue Back(FenValue[] args, NavigationState state)
        {
            return PerformDirectionalTraversal(args, state, -1, "back");
        }

        private static FenValue Forward(FenValue[] args, NavigationState state)
        {
            return PerformDirectionalTraversal(args, state, 1, "forward");
        }

        private static FenValue PerformDirectionalTraversal(FenValue[] args,
            NavigationState state, int delta, string directionName)
        {
            var newIndex = state.CurrentIndex + delta;
            if (newIndex < 0 || newIndex >= state.Entries.Count || state.CurrentIndex < 0)
                return CreateNavigationResult(null,
                    "InvalidStateError: Cannot go " + directionName, state);

            var info = FenValue.Undefined;
            if (args.Length > 0 && args[0].IsObject)
            {
                var options = args[0].AsObject();
                info = options?.Get("info") ?? FenValue.Undefined;
            }

            var targetEntry = state.Entries[newIndex];
            return PerformTraversal(state, newIndex, targetEntry, "traverse", info);
        }

        private static FenValue PerformTraversal(NavigationState state, int targetIndex,
            FenObject targetEntry, string navigationType, FenValue info)
        {
            var context = state.Context;
            var prevEntry = state.CurrentEntry;

            var destination = CreateDestination(
                targetEntry.Get("__url").ToString(), targetEntry);
            var navigateEvent = CreateNavigateEvent(navigationType, destination, info,
                userInitiated: true, hashChange: false, signal: null, formData: null,
                downloadRequest: null);
            var shouldIntercept = !FireNavigateEvent(state, navigateEvent);

            var defaultPrevented = navigateEvent.Get("__defaultPrevented").AsBoolean();
            var interceptCalled = navigateEvent.Get("__interceptCalled").AsBoolean();

            if (defaultPrevented && !interceptCalled)
                return CreateNavigationResult(null,
                    "AbortError: Navigation cancelled", state);

            // Set up transition (spec 4.5)
            var fromEntry = (prevEntry != null) ? CreateEntrySnapshot(prevEntry) : null;
            var transition = CreateTransition(navigationType, fromEntry);

            // Store transition on navigation object
            state.Transition = transition;

            var resultObj = CreateNavigationResultObject(state);
            var prevEntryCapture = prevEntry;
            var navTypeCapture = navigationType;

            ScheduleMicrotask(context, () =>
            {
                try
                {
                    SetCurrentEntry(state, targetIndex);

                    // Update __index on the entry to reflect new position
                    if (state.CurrentEntry != null)
                        state.CurrentEntry.Set("__index", FenValue.FromNumber(targetIndex));

                    ResolveCommittedPromise(resultObj, state.CurrentEntry, state);
                    ResolveFinishedPromise(resultObj, state.CurrentEntry, state);

                    // Resolve transition.finished
                    if (transition != null)
                    {
                        var transitionFinished = transition.Get("__finishedPromise");
                        if (transitionFinished.IsObject)
                        {
                            var tfObj = transitionFinished.AsObject();
                            var tfResolve = tfObj.Get("__resolve");
                            if (tfResolve.IsFunction)
                                tfResolve.AsFunction()
                                    .Invoke(new[] { FenValue.Undefined }, context);
                        }
                    }

                    state.Transition = null;

                    FireNavigateSuccessEvent(state,
                        CreateNavigateSuccessEvent(navTypeCapture));
                    FireCurrentEntryChangeEvent(state, navTypeCapture,
                        prevEntryCapture);
                }
                catch (Exception ex)
                {
                    if (transition != null)
                    {
                        var transitionFinished = transition.Get("__finishedPromise");
                        if (transitionFinished.IsObject)
                        {
                            var tfObj = transitionFinished.AsObject();
                            var tfReject = tfObj.Get("__reject");
                            if (tfReject.IsFunction)
                                tfReject.AsFunction()
                                    .Invoke(new[]
                                    {
                                        FenValue.FromObject(
                                            CreateDOMException("OperationError",
                                                ex.Message))
                                    }, context);
                        }
                    }

                    state.Transition = null;
                    RejectResultPromises(resultObj,
                        $"OperationError: {ex.Message}", state);
                    FireNavigateErrorEvent(state,
                        CreateNavigateErrorEvent(navTypeCapture, ex.Message));
                }
            });

            return FenValue.FromObject(resultObj);
        }

        private static FenObject ReplaceCurrentEntry(NavigationState state, string url,
            FenValue entryState)
        {
            state.EntryCounter++;
            var key = GenerateKey();
            var id = GenerateId();
            var newEntry = CreateHistoryEntry(
                key: key,
                id: id,
                url: url,
                entryState: entryState,
                index: state.CurrentIndex >= 0 ? state.CurrentIndex : 0);

            if (state.CurrentIndex >= 0 && state.CurrentIndex < state.Entries.Count)
                state.Entries[state.CurrentIndex] = newEntry;
            else
                state.Entries.Add(newEntry);

            state.CurrentEntry = newEntry;
            return newEntry;
        }

        private static FenValue PerformUpdateCurrentEntry(FenValue[] args,
            NavigationState state)
        {
            if (state.CurrentEntry == null || state.CurrentIndex < 0)
                return FenValue.Undefined;

            if (args.Length > 0 && args[0].IsObject)
            {
                var options = args[0].AsObject();
                var nextState = options?.Get("state") ?? FenValue.Undefined;
                state.CurrentEntry.Set("__state",
                    nextState.IsUndefined || nextState.IsNull
                        ? FenValue.Undefined
                        : nextState);
            }

            return FenValue.Undefined;
        }

        #endregion

        #region NavigationResult

        private static FenValue CreateNavigationResult(FenObject committedEntry,
            string rejectionReason, NavigationState state)
        {
            if (!string.IsNullOrEmpty(rejectionReason))
                return CreateRejectedNavigationResult(rejectionReason, state);

            var resultObj = CreateNavigationResultObject(state);
            ResolveCommittedPromise(resultObj, committedEntry, state);
            ResolveFinishedPromise(resultObj, committedEntry, state);
            return FenValue.FromObject(resultObj);
        }

        private static FenValue CreateRejectedNavigationResult(string reason,
            NavigationState state)
        {
            var context = state.Context;
            var resultObj = new FenObject();
            resultObj.InternalClass = "NavigationResult";

            // committed promise
            var committedRejected = RejectThenable(reason, context);
            resultObj.Set("__committedPromise", FenValue.FromObject(committedRejected));
            resultObj.Set("committed", FenValue.FromObject(committedRejected));

            // finished promise (also rejected)
            var finishedRejected = RejectThenable(reason, context);
            resultObj.Set("__finishedPromise", FenValue.FromObject(finishedRejected));
            resultObj.Set("finished", FenValue.FromObject(finishedRejected));

            resultObj.Set("__rejected", FenValue.FromBoolean(true));

            return FenValue.FromObject(resultObj);
        }

        private static FenObject CreateNavigationResultObject(NavigationState state)
        {
            var resultObj = new FenObject();
            resultObj.InternalClass = "NavigationResult";

            // committed promise (pending until resolved)
            var committedPending = CreatePendingThenable(state.Context);
            resultObj.Set("__committedPromise", FenValue.FromObject(committedPending));
            resultObj.Set("committed", FenValue.FromObject(committedPending));

            // finished promise (pending until resolved)
            var finishedPending = CreatePendingThenable(state.Context);
            resultObj.Set("__finishedPromise", FenValue.FromObject(finishedPending));
            resultObj.Set("finished", FenValue.FromObject(finishedPending));

            resultObj.Set("__rejected", FenValue.FromBoolean(false));
            resultObj.Set("__resolvedEntry", FenValue.Null);

            // Attach then/catch for chaining
            AttachResultThenCatch(resultObj, state.Context);

            return resultObj;
        }

        private static void AttachResultThenCatch(FenObject resultObj, IExecutionContext context)
        {
            resultObj.Set("then", FenValue.FromFunction(new FenFunction("then", (args, thisVal) =>
            {
                var committedObj = resultObj.Get("__committedPromise");
                if (committedObj.IsObject)
                {
                    var thenVal = committedObj.AsObject().Get("then");
                    if (thenVal.IsFunction)
                        return thenVal.AsFunction()
                            .Invoke(new[] { args.Length > 0 ? args[0] : FenValue.Undefined,
                                           args.Length > 1 ? args[1] : FenValue.Undefined },
                                context);
                }

                return FenValue.FromObject(new FenObject());
            })));

            resultObj.Set("catch", FenValue.FromFunction(new FenFunction("catch", (args, thisVal) =>
            {
                var committedObj = resultObj.Get("__committedPromise");
                if (committedObj.IsObject)
                {
                    var catchVal = committedObj.AsObject().Get("catch");
                    if (catchVal.IsFunction)
                        return catchVal.AsFunction()
                            .Invoke(new[] { args.Length > 0 ? args[0] : FenValue.Undefined },
                                context);
                }

                return FenValue.FromObject(new FenObject());
            })));
        }

        private static void ResolveCommittedPromise(FenObject resultObj, FenObject entry,
            NavigationState state)
        {
            if (entry == null) return;

            resultObj.Set("__resolvedEntry", FenValue.FromObject(entry));

            var committedObj = resultObj.Get("__committedPromise");
            if (committedObj.IsObject)
            {
                var resolve = committedObj.AsObject().Get("__resolve");
                if (resolve.IsFunction)
                    resolve.AsFunction()
                        .Invoke(new[] { FenValue.FromObject(entry) }, state.Context);
            }
        }

        private static void ResolveFinishedPromise(FenObject resultObj, FenObject entry,
            NavigationState state)
        {
            if (entry == null) return;

            resultObj.Set("__resolvedEntry", FenValue.FromObject(entry));

            var finishedObj = resultObj.Get("__finishedPromise");
            if (finishedObj.IsObject)
            {
                var resolve = finishedObj.AsObject().Get("__resolve");
                if (resolve.IsFunction)
                    resolve.AsFunction()
                        .Invoke(new[] { FenValue.FromObject(entry) }, state.Context);
            }
        }

        private static void RejectResultPromises(FenObject resultObj, string reason,
            NavigationState state)
        {
            resultObj.Set("__rejected", FenValue.FromBoolean(true));

            var committedObj = resultObj.Get("__committedPromise");
            if (committedObj.IsObject)
            {
                var reject = committedObj.AsObject().Get("__reject");
                if (reject.IsFunction)
                    reject.AsFunction().Invoke(
                        new[]
                        {
                            FenValue.FromObject(
                                CreateDOMException("OperationError", reason))
                        },
                        state.Context);
            }

            var finishedObj = resultObj.Get("__finishedPromise");
            if (finishedObj.IsObject)
            {
                var reject = finishedObj.AsObject().Get("__reject");
                if (reject.IsFunction)
                    reject.AsFunction().Invoke(
                        new[]
                        {
                            FenValue.FromObject(
                                CreateDOMException("OperationError", reason))
                        },
                        state.Context);
            }
        }

        private static bool IsResultRejected(FenObject resultObj)
        {
            return resultObj.Get("__rejected").AsBoolean();
        }

        private static FenObject GetResultCommittedEntry(FenObject resultObj,
            NavigationState state)
        {
            var resolvedEntry = resultObj.Get("__resolvedEntry");
            if (resolvedEntry.IsObject)
                return resolvedEntry.AsObject() as FenObject;

            return state.CurrentEntry;
        }

        #endregion

        #region Promise / thenable helpers

        private static FenObject CreatePendingThenable(IExecutionContext context)
        {
            var p = new FenObject();
            p.Set("__state", FenValue.FromString("pending"));

            var resolveFunc = new FenFunction("resolve", (resolveArgs, thisVal) =>
            {
                p.Set("__state", FenValue.FromString("fulfilled"));
                p.Set("__result", resolveArgs.Length > 0 ? resolveArgs[0] : FenValue.Undefined);
                InvokeThenHandlers(p);
                return FenValue.Undefined;
            });

            var rejectFunc = new FenFunction("reject", (rejectArgs, thisVal) =>
            {
                p.Set("__state", FenValue.FromString("rejected"));
                p.Set("__reason", rejectArgs.Length > 0
                    ? rejectArgs[0]
                    : FenValue.FromString("Error"));
                return FenValue.Undefined;
            });

            p.Set("__resolve", FenValue.FromFunction(resolveFunc));
            p.Set("__reject", FenValue.FromFunction(rejectFunc));

            p.Set("__handlers", FenValue.FromObject(new FenObject()));

            p.Set("then", FenValue.FromFunction(new FenFunction("then", (args, thisVal) =>
            {
                var state = p.Get("__state").ToString();

                if (state == "pending")
                {
                    var handlers = p.Get("__handlers").AsObject();
                    var handlerCount = handlers.Get("__count");
                    var idx = handlerCount.IsNumber ? ((int)handlerCount.ToNumber()) : 0;
                    handlers.Set($"__fulfill_{idx}",
                        args.Length > 0 ? args[0] : FenValue.Undefined);
                    handlers.Set($"__reject_{idx}",
                        args.Length > 1 ? args[1] : FenValue.Undefined);
                    handlers.Set("__count", FenValue.FromNumber(idx + 1));
                    return FenValue.FromObject(p);
                }

                if (state == "fulfilled" && args.Length > 0 && args[0].IsFunction)
                {
                    var result = p.Get("__result");
                    SafeInvoke(args[0].AsFunction(),
                        new[] { result }, context, "pendingThenable.onfulfilled");
                }
                else if (state == "rejected" && args.Length > 1 && args[1].IsFunction)
                {
                    var reason = p.Get("__reason");
                    SafeInvoke(args[1].AsFunction(),
                        new[] { reason }, context, "pendingThenable.onrejected");
                }

                return FenValue.FromObject(p);
            })));

            p.Set("catch", FenValue.FromFunction(new FenFunction("catch", (args, thisVal) =>
            {
                var state = p.Get("__state").ToString();
                if (state == "rejected" && args.Length > 0 && args[0].IsFunction)
                {
                    var reason = p.Get("__reason");
                    SafeInvoke(args[0].AsFunction(),
                        new[] { reason }, context, "pendingThenable.catch");
                }

                return FenValue.FromObject(p);
            })));

            return p;
        }

        private static void InvokeThenHandlers(FenObject p)
        {
            var handlers = p.Get("__handlers").AsObject();
            if (handlers == null) return;

            var countVal = handlers.Get("__count");
            var count = countVal.IsNumber ? (int)countVal.ToNumber() : 0;
            var result = p.Get("__result");

            for (int i = 0; i < count; i++)
            {
                var handler = handlers.Get($"__fulfill_{i}");
                if (handler.IsFunction)
                {
                    try
                    {
                        handler.AsFunction().Invoke(new[] { result }, null);
                    }
                    catch { }
                }
            }
        }

        private static FenObject RejectThenable(string reason, IExecutionContext context)
        {
            var r = new FenObject();
            r.Set("__state", FenValue.FromString("rejected"));
            r.Set("__reason", FenValue.FromString(reason ?? "Error"));
            r.Set("then",
                FenValue.FromFunction(new FenFunction("then", (args, thisVal) =>
                {
                    if (args.Length > 1 && args[1].IsFunction)
                    {
                        var reasonVal = r.Get("__reason");
                        SafeInvoke(args[1].AsFunction(),
                            new[] { reasonVal }, context, "rejectionThenable.onrejected");
                    }

                    return FenValue.FromObject(r);
                })));
            r.Set("catch",
                FenValue.FromFunction(new FenFunction("catch", (args, thisVal) =>
                {
                    if (args.Length > 0 && args[0].IsFunction)
                    {
                        var reasonVal = r.Get("__reason");
                        SafeInvoke(args[0].AsFunction(),
                            new[] { reasonVal }, context, "rejectionThenable.catch");
                    }

                    return FenValue.FromObject(r);
                })));

            return r;
        }

        private static void SafeInvoke(FenFunction func, FenValue[] args,
            IExecutionContext context, string operation)
        {
            try
            {
                func.Invoke(args, context);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn(
                    $"[NavigationAPI] {operation} callback failed: {ex.Message}",
                    LogCategory.JavaScript);
            }
        }

        #endregion

        #region Event objects

        /// <summary>
        /// Creates a NavigateEvent FenObject per spec 4.2.
        /// </summary>
        private static FenObject CreateNavigateEvent(string navigationType,
            FenObject destination, FenValue info, bool userInitiated, bool hashChange,
            FenObject signal, FenObject formData, string downloadRequest)
        {
            var evt = new FenObject();
            evt.InternalClass = "NavigateEvent";

            evt.Set("__type", FenValue.FromString("navigate"));
            evt.Set("__navigationType", FenValue.FromString(navigationType));
            evt.Set("__destination", FenValue.FromObject(destination));

            evt.Set("__canIntercept", FenValue.FromBoolean(true));
            evt.Set("__defaultPrevented", FenValue.FromBoolean(false));
            evt.Set("__interceptCalled", FenValue.FromBoolean(false));
            evt.Set("__interceptOptions", FenValue.Null);

            evt.Set("__userInitiated", FenValue.FromBoolean(userInitiated));
            evt.Set("__hashChange", FenValue.FromBoolean(hashChange));
            evt.Set("__formData", formData != null
                ? FenValue.FromObject(formData)
                : FenValue.Null);
            evt.Set("__info", info);
            evt.Set("__downloadRequest", string.IsNullOrEmpty(downloadRequest)
                ? FenValue.Null
                : FenValue.FromString(downloadRequest));

            // CreateAbortSignal equivalent
            var signalObj = signal ?? CreateAbortSignalLike();
            evt.Set("__signal", FenValue.FromObject(signalObj));

            // Public getters
            evt.Set("navigationType",
                FenValue.FromFunction(new FenFunction("get_navigationType", (args, thisVal) =>
                    evt.Get("__navigationType"))));

            evt.Set("destination",
                FenValue.FromFunction(new FenFunction("get_destination", (args, thisVal) =>
                    evt.Get("__destination"))));

            evt.Set("canIntercept",
                FenValue.FromFunction(new FenFunction("get_canIntercept", (args, thisVal) =>
                    evt.Get("__canIntercept"))));

            evt.Set("userInitiated",
                FenValue.FromFunction(new FenFunction("get_userInitiated", (args, thisVal) =>
                    evt.Get("__userInitiated"))));

            evt.Set("hashChange",
                FenValue.FromFunction(new FenFunction("get_hashChange", (args, thisVal) =>
                    evt.Get("__hashChange"))));

            evt.Set("signal",
                FenValue.FromFunction(new FenFunction("get_signal", (args, thisVal) =>
                    evt.Get("__signal"))));

            evt.Set("formData",
                FenValue.FromFunction(new FenFunction("get_formData", (args, thisVal) =>
                    evt.Get("__formData"))));

            evt.Set("info",
                FenValue.FromFunction(new FenFunction("get_info", (args, thisVal) =>
                    evt.Get("__info"))));

            evt.Set("downloadRequest",
                FenValue.FromFunction(new FenFunction("get_downloadRequest", (args, thisVal) =>
                    evt.Get("__downloadRequest"))));

            // Methods
            evt.Set("intercept",
                FenValue.FromFunction(new FenFunction("intercept", (funcArgs, thisVal) =>
                {
                    evt.Set("__interceptCalled", FenValue.FromBoolean(true));
                    if (funcArgs.Length > 0 && funcArgs[0].IsObject)
                    {
                        evt.Set("__interceptOptions",
                            FenValue.FromObject(funcArgs[0].AsObject()));
                    }

                    return FenValue.Undefined;
                })));

            evt.Set("scroll", FenValue.FromFunction(new FenFunction("scroll",
                (funcArgs, thisVal) => FenValue.Undefined)));

            evt.Set("restoreScroll", FenValue.FromFunction(new FenFunction("restoreScroll",
                (funcArgs, thisVal) => FenValue.Undefined)));

            evt.Set("preventDefault",
                FenValue.FromFunction(new FenFunction("preventDefault", (funcArgs, thisVal) =>
                {
                    evt.Set("__defaultPrevented", FenValue.FromBoolean(true));
                    return FenValue.Undefined;
                })));

            // Convenience: expose type matching Event
            evt.Set("type", FenValue.FromString("navigate"));

            return evt;
        }

        /// <summary>
        /// Creates a navigatesuccess event FenObject.
        /// </summary>
        private static FenObject CreateNavigateSuccessEvent(string navigationType)
        {
            var evt = new FenObject();
            evt.InternalClass = "Event";
            evt.Set("type", FenValue.FromString("navigatesuccess"));
            evt.Set("__navigationType", FenValue.FromString(navigationType));
            evt.Set("bubbles", FenValue.FromBoolean(false));
            evt.Set("cancelable", FenValue.FromBoolean(false));
            return evt;
        }

        /// <summary>
        /// Creates a navigateerror event FenObject.
        /// </summary>
        private static FenObject CreateNavigateErrorEvent(string navigationType,
            string errorMessage)
        {
            var evt = new FenObject();
            evt.InternalClass = "Event";
            evt.Set("type", FenValue.FromString("navigateerror"));
            evt.Set("__navigationType", FenValue.FromString(navigationType));
            evt.Set("message", FenValue.FromString(errorMessage ?? "Unknown error"));
            evt.Set("bubbles", FenValue.FromBoolean(false));
            evt.Set("cancelable", FenValue.FromBoolean(false));

            var err = CreateDOMException("OperationError", errorMessage ?? "Unknown error");
            evt.Set("error", FenValue.FromObject(err));

            return evt;
        }

        /// <summary>
        /// Creates a NavigationCurrentEntryChangeEvent FenObject (spec 4.4).
        /// </summary>
        private static FenObject CreateCurrentEntryChangeEvent(string navigationType,
            FenObject fromEntry)
        {
            var evt = new FenObject();
            evt.InternalClass = "NavigationCurrentEntryChangeEvent";
            evt.Set("type", FenValue.FromString("currententrychange"));
            evt.Set("__navigationType", FenValue.FromString(navigationType));

            if (fromEntry != null)
                evt.Set("__from", FenValue.FromObject(fromEntry));
            else
                evt.Set("__from", FenValue.Null);

            evt.Set("bubbles", FenValue.FromBoolean(false));
            evt.Set("cancelable", FenValue.FromBoolean(false));

            // Public getters
            evt.Set("navigationType",
                FenValue.FromFunction(new FenFunction("get_navigationType_cc", (args, thisVal) =>
                    evt.Get("__navigationType"))));

            evt.Set("from",
                FenValue.FromFunction(new FenFunction("get_from", (args, thisVal) =>
                    evt.Get("__from"))));

            return evt;
        }

        /// <summary>
        /// Creates a NavigationDestination FenObject (spec 4.3).
        /// </summary>
        private static FenObject CreateDestination(string url, FenObject entry)
        {
            var dest = new FenObject();
            dest.InternalClass = "NavigationDestination";

            dest.Set("__url", FenValue.FromString(url ?? "about:blank"));
            dest.Set("__key", entry != null
                ? entry.Get("__key")
                : FenValue.Null);
            dest.Set("__id", entry != null
                ? entry.Get("__id")
                : FenValue.Null);
            dest.Set("__index", entry != null
                ? entry.Get("__index")
                : FenValue.FromNumber(-1));
            dest.Set("__sameDocument", FenValue.FromBoolean(true));
            dest.Set("__state", entry != null
                ? entry.Get("__state")
                : FenValue.Undefined);

            // Public getters
            dest.Set("url",
                FenValue.FromFunction(new FenFunction("get_url_dest", (args, thisVal) =>
                    dest.Get("__url"))));

            dest.Set("key",
                FenValue.FromFunction(new FenFunction("get_key_dest", (args, thisVal) =>
                    dest.Get("__key"))));

            dest.Set("id",
                FenValue.FromFunction(new FenFunction("get_id_dest", (args, thisVal) =>
                    dest.Get("__id"))));

            dest.Set("index",
                FenValue.FromFunction(new FenFunction("get_index_dest", (args, thisVal) =>
                    dest.Get("__index"))));

            dest.Set("sameDocument",
                FenValue.FromFunction(new FenFunction("get_sameDocument_dest", (args, thisVal) =>
                    dest.Get("__sameDocument"))));

            dest.Set("getState",
                FenValue.FromFunction(new FenFunction("getState_dest", (args, thisVal) =>
                    dest.Get("__state"))));

            return dest;
        }

        /// <summary>
        /// Builds a destination for a navigate() call (not yet in history).
        /// </summary>
        private static FenObject BuildDestinationForNavigate(string url,
            string navigationType)
        {
            var dest = new FenObject();
            dest.InternalClass = "NavigationDestination";

            dest.Set("__url", FenValue.FromString(url ?? "about:blank"));
            dest.Set("__key", FenValue.Null);
            dest.Set("__id", FenValue.Null);
            dest.Set("__index", FenValue.FromNumber(-1));
            dest.Set("__sameDocument", FenValue.FromBoolean(true));
            dest.Set("__state", FenValue.Undefined);

            dest.Set("url",
                FenValue.FromFunction(new FenFunction("get_url_dest", (args, thisVal) =>
                    dest.Get("__url"))));
            dest.Set("key",
                FenValue.FromFunction(new FenFunction("get_key_dest", (args, thisVal) =>
                    FenValue.Null)));
            dest.Set("id",
                FenValue.FromFunction(new FenFunction("get_id_dest", (args, thisVal) =>
                    FenValue.Null)));
            dest.Set("index",
                FenValue.FromFunction(new FenFunction("get_index_dest", (args, thisVal) =>
                    FenValue.FromNumber(-1))));
            dest.Set("sameDocument",
                FenValue.FromFunction(new FenFunction("get_sameDocument_dest", (args, thisVal) =>
                    FenValue.FromBoolean(true))));
            dest.Set("getState",
                FenValue.FromFunction(new FenFunction("getState_dest", (args, thisVal) =>
                    FenValue.Undefined)));

            return dest;
        }

        /// <summary>
        /// Creates a NavigationTransition FenObject (spec 4.5).
        /// </summary>
        private static FenObject CreateTransition(string navigationType,
            FenObject fromEntry)
        {
            var transition = new FenObject();
            transition.InternalClass = "NavigationTransition";

            transition.Set("__navigationType", FenValue.FromString(navigationType));
            transition.Set("__from", fromEntry != null
                ? FenValue.FromObject(fromEntry)
                : FenValue.Null);

            // finished promise (pending)
            var finishedPending = CreatePendingThenable(null);
            transition.Set("__finishedPromise", FenValue.FromObject(finishedPending));

            transition.Set("navigationType",
                FenValue.FromFunction(new FenFunction("get_navType_trans", (args, thisVal) =>
                    transition.Get("__navigationType"))));

            transition.Set("from",
                FenValue.FromFunction(new FenFunction("get_from_trans", (args, thisVal) =>
                    transition.Get("__from"))));

            transition.Set("finished",
                FenValue.FromFunction(new FenFunction("get_finished_trans", (args, thisVal) =>
                    FenValue.FromObject(
                        transition.Get("__finishedPromise").AsObject() ?? new FenObject()))));

            return transition;
        }

        /// <summary>
        /// Creates a snapshot of a NavigationHistoryEntry for use in "from" fields.
        /// </summary>
        private static FenObject CreateEntrySnapshot(FenObject entry)
        {
            if (entry == null) return null;

            var snapshot = new FenObject();
            snapshot.InternalClass = "NavigationHistoryEntry";

            snapshot.Set("__key", entry.Get("__key"));
            snapshot.Set("__id", entry.Get("__id"));
            snapshot.Set("__url", entry.Get("__url"));
            snapshot.Set("__index", entry.Get("__index"));
            snapshot.Set("__sameDocument", FenValue.FromBoolean(true));
            snapshot.Set("__state", entry.Get("__state"));

            snapshot.Set("url",
                FenValue.FromFunction(new FenFunction("get_url", (args, thisVal) =>
                    snapshot.Get("__url"))));
            snapshot.Set("key",
                FenValue.FromFunction(new FenFunction("get_key", (args, thisVal) =>
                    snapshot.Get("__key"))));
            snapshot.Set("id",
                FenValue.FromFunction(new FenFunction("get_id", (args, thisVal) =>
                    snapshot.Get("__id"))));
            snapshot.Set("index",
                FenValue.FromFunction(new FenFunction("get_index", (args, thisVal) =>
                    snapshot.Get("__index"))));
            snapshot.Set("sameDocument",
                FenValue.FromFunction(new FenFunction("get_sameDocument", (args, thisVal) =>
                    snapshot.Get("__sameDocument"))));
            snapshot.Set("getState",
                FenValue.FromFunction(new FenFunction("getState", (args, thisVal) =>
                    snapshot.Get("__state"))));

            return snapshot;
        }

        /// <summary>
        /// Creates a minimal AbortSignal-like FenObject for the navigate event signal.
        /// </summary>
        private static FenObject CreateAbortSignalLike()
        {
            var signal = new FenObject();
            signal.InternalClass = "AbortSignal";
            signal.Set("aborted", FenValue.FromBoolean(false));
            signal.Set("reason", FenValue.Undefined);
            signal.Set("onabort", FenValue.Null);
            signal.Set("throwIfAborted",
                FenValue.FromFunction(new FenFunction("throwIfAborted", (args, thisVal) =>
                    FenValue.Undefined)));
            signal.Set("addEventListener",
                FenValue.FromFunction(new FenFunction("addEventListener", (args, thisVal) =>
                    FenValue.Undefined)));
            signal.Set("removeEventListener",
                FenValue.FromFunction(new FenFunction("removeEventListener", (args, thisVal) =>
                    FenValue.Undefined)));
            return signal;
        }

        #endregion

        #region Event dispatch

        private static void SetupEventTargetMethods(FenObject navigation,
            NavigationState state)
        {
            navigation.Set("addEventListener",
                FenValue.FromFunction(new FenFunction("addEventListener", (args, thisVal) =>
                {
                    if (args.Length < 2)
                        return FenValue.Undefined;

                    var type = args[0].ToString();
                    var listener = args[1];

                    state.EventListeners[type] = listener;

                    switch (type)
                    {
                        case "navigate":
                            state.OnNavigateHandler = listener;
                            navigation.Set("onnavigate", listener);
                            break;
                        case "navigatesuccess":
                            state.OnNavigateSuccessHandler = listener;
                            navigation.Set("onnavigatesuccess", listener);
                            break;
                        case "navigateerror":
                            state.OnNavigateErrorHandler = listener;
                            navigation.Set("onnavigateerror", listener);
                            break;
                        case "currententrychange":
                            state.OnCurrentEntryChangeHandler = listener;
                            navigation.Set("oncurrententrychange", listener);
                            break;
                    }

                    return FenValue.Undefined;
                })));

            navigation.Set("removeEventListener",
                FenValue.FromFunction(new FenFunction("removeEventListener", (args, thisVal) =>
                {
                    if (args.Length < 2)
                        return FenValue.Undefined;

                    var type = args[0].ToString();

                    if (state.EventListeners.Remove(type))
                    {
                        switch (type)
                        {
                            case "navigate":
                                state.OnNavigateHandler = FenValue.Null;
                                navigation.Set("onnavigate", FenValue.Null);
                                break;
                            case "navigatesuccess":
                                state.OnNavigateSuccessHandler = FenValue.Null;
                                navigation.Set("onnavigatesuccess", FenValue.Null);
                                break;
                            case "navigateerror":
                                state.OnNavigateErrorHandler = FenValue.Null;
                                navigation.Set("onnavigateerror", FenValue.Null);
                                break;
                            case "currententrychange":
                                state.OnCurrentEntryChangeHandler = FenValue.Null;
                                navigation.Set("oncurrententrychange", FenValue.Null);
                                break;
                        }
                    }

                    return FenValue.Undefined;
                })));
        }

        /// <summary>
        /// Fires the "navigate" event. Returns true if default was allowed (navigation proceeds).
        /// </summary>
        private static bool FireNavigateEvent(NavigationState state,
            FenObject navigateEvent)
        {
            var context = state.Context;
            if (context == null) return true;

            var listener = state.EventListeners.TryGetValue("navigate", out var l)
                ? l
                : FenValue.Null;

            var onHandler = state.OnNavigateHandler.IsFunction
                ? state.OnNavigateHandler.AsFunction()
                : null;

            bool anyFired = false;
            var eventValue = FenValue.FromObject(navigateEvent);

            if (listener.IsFunction)
            {
                ScheduleMicrotask(context, () =>
                {
                    try { listener.AsFunction()
                        .Invoke(new[] { eventValue }, context); } catch { }
                });
                anyFired = true;
            }

            if (onHandler != null)
            {
                ScheduleMicrotask(context, () =>
                {
                    try { onHandler.Invoke(new[] { eventValue }, context); } catch { }
                });
                anyFired = true;
            }

            return anyFired;
        }

        /// <summary>
        /// Fires the "navigatesuccess" event.
        /// </summary>
        private static void FireNavigateSuccessEvent(NavigationState state,
            FenObject successEvent)
        {
            var context = state.Context;
            if (context == null) return;

            var listener = state.EventListeners.TryGetValue("navigatesuccess", out var l)
                ? l
                : FenValue.Null;
            var onHandler = state.OnNavigateSuccessHandler.IsFunction
                ? state.OnNavigateSuccessHandler.AsFunction()
                : null;

            var eventValue = FenValue.FromObject(successEvent);

            ScheduleMicrotask(context, () =>
            {
                try
                {
                    if (listener.IsFunction)
                        listener.AsFunction().Invoke(new[] { eventValue }, context);
                    if (onHandler != null)
                        onHandler.Invoke(new[] { eventValue }, context);
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Warn(
                        $"[NavigationAPI] navigatesuccess handler error: {ex.Message}",
                        LogCategory.JavaScript);
                }
            });
        }

        /// <summary>
        /// Fires the "navigateerror" event.
        /// </summary>
        private static void FireNavigateErrorEvent(NavigationState state,
            FenObject errorEvent)
        {
            var context = state.Context;
            if (context == null) return;

            var listener = state.EventListeners.TryGetValue("navigateerror", out var l)
                ? l
                : FenValue.Null;
            var onHandler = state.OnNavigateErrorHandler.IsFunction
                ? state.OnNavigateErrorHandler.AsFunction()
                : null;

            var eventValue = FenValue.FromObject(errorEvent);

            ScheduleMicrotask(context, () =>
            {
                try
                {
                    if (listener.IsFunction)
                        listener.AsFunction().Invoke(new[] { eventValue }, context);
                    if (onHandler != null)
                        onHandler.Invoke(new[] { eventValue }, context);
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Warn(
                        $"[NavigationAPI] navigateerror handler error: {ex.Message}",
                        LogCategory.JavaScript);
                }
            });
        }

        /// <summary>
        /// Fires the "currententrychange" event per spec 6.1 step 3.
        /// </summary>
        private static void FireCurrentEntryChangeEvent(NavigationState state,
            string navigationType, FenObject fromEntry)
        {
            var context = state.Context;
            if (context == null) return;

            var evt = CreateCurrentEntryChangeEvent(navigationType, fromEntry);

            var listener =
                state.EventListeners.TryGetValue("currententrychange", out var l)
                    ? l
                    : FenValue.Null;
            var onHandler = state.OnCurrentEntryChangeHandler.IsFunction
                ? state.OnCurrentEntryChangeHandler.AsFunction()
                : null;

            var eventValue = FenValue.FromObject(evt);

            ScheduleMicrotask(context, () =>
            {
                try
                {
                    if (listener.IsFunction)
                        listener.AsFunction().Invoke(new[] { eventValue }, context);
                    if (onHandler != null)
                        onHandler.Invoke(new[] { eventValue }, context);
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Warn(
                        $"[NavigationAPI] currententrychange handler error: {ex.Message}",
                        LogCategory.JavaScript);
                }
            });
        }

        #endregion

        #region Utilities

        private static string NormalizeNavigationType(string rawType,
            NavigationState state)
        {
            switch (rawType)
            {
                case "push":
                case "replace":
                case "traverse":
                case "reload":
                    return rawType;
            }

            return "push";
        }

        private static void ScheduleMicrotask(IExecutionContext context, Action action)
        {
            if (action == null)
                return;

            if (context?.ScheduleMicrotask != null)
            {
                context.ScheduleMicrotask(action);
                return;
            }

            EventLoopCoordinator.Instance.ScheduleMicrotask(action);
        }

        private static FenObject CreateDOMException(string name, string message)
        {
            var err = new FenObject();
            err.Set("name", FenValue.FromString(name));
            err.Set("message", FenValue.FromString(message ?? string.Empty));
            err.InternalClass = "DOMException";
            return err;
        }

        #endregion
    }
}