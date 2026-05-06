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
    /// Navigation API implementation per WICG spec.
    /// https://wicg.github.io/navigation-api/
    /// </summary>
    public static class NavigationAPI
    {
        private sealed class NavigationState
        {
            public NavigationState(IExecutionContext context, FenObject navigationObject)
            {
                Context = context;
                NavigationObject = navigationObject;
            }

            public IExecutionContext Context { get; }
            public FenObject NavigationObject { get; }
            public List<FenObject> Entries { get; } = new();
            public int CurrentIndex { get; set; } = -1;
            public FenObject CurrentEntry { get; set; }

            public FenValue OnCurrentEntryChange { get; set; } = FenValue.Null;
            public FenValue OnNavigate { get; set; } = FenValue.Null;
            public FenValue OnNavigateSuccess { get; set; } = FenValue.Null;
            public FenValue OnNavigateError { get; set; } = FenValue.Null;
            public FenValue OnPopState { get; set; } = FenValue.Null;
        }

        public static FenObject CreateNavigation(IExecutionContext context)
        {
            var navigation = new FenObject();
            var state = new NavigationState(context, navigation);

            InitializeNavigation(state);

            navigation.Set("currentEntry", state.CurrentEntry == null ? FenValue.Null : FenValue.FromObject(state.CurrentEntry));
            navigation.Set("transition", FenValue.Null);
            navigation.Set("oncurrententrychange", FenValue.Null);
            navigation.Set("onnavigate", FenValue.Null);
            navigation.Set("onnavigatesuccess", FenValue.Null);
            navigation.Set("onnavigateerror", FenValue.Null);
            navigation.Set("onpopstate", FenValue.Null);

            navigation.Set("entries", FenValue.FromFunction(new FenFunction("entries", (args, thisVal) =>
                FenValue.FromObject(CreateEntriesArray(state)))));

            navigation.Set("getCurrentEntry", FenValue.FromFunction(new FenFunction("getCurrentEntry", (args, thisVal) =>
                state.CurrentEntry == null ? FenValue.Null : FenValue.FromObject(state.CurrentEntry))));

            navigation.Set("updateCurrentEntry", FenValue.FromFunction(new FenFunction("updateCurrentEntry", (args, thisVal) =>
                UpdateCurrentEntry(args, state))));

            navigation.Set("navigate", FenValue.FromFunction(new FenFunction("navigate", (args, thisVal) => Navigate(args, state))));
            navigation.Set("reload", FenValue.FromFunction(new FenFunction("reload", (args, thisVal) => Reload(state))));
            navigation.Set("back", FenValue.FromFunction(new FenFunction("back", (args, thisVal) => TraverseHistory(state, -1))));
            navigation.Set("forward", FenValue.FromFunction(new FenFunction("forward", (args, thisVal) => TraverseHistory(state, 1))));
            navigation.Set("traverseTo", FenValue.FromFunction(new FenFunction("traverseTo", (args, thisVal) => TraverseTo(args, state))));
            navigation.Set("traverseByType", FenValue.FromFunction(new FenFunction("traverseByType", (args, thisVal) => TraverseByType(state))));

            SetupEventTargetMethods(navigation, state);
            return navigation;
        }

        private static void InitializeNavigation(NavigationState state)
        {
            state.Entries.Clear();
            var currentUrl = state.Context?.CurrentUrl;
            var urlString = string.IsNullOrWhiteSpace(currentUrl) ? "about:blank" : currentUrl;
            var entry = CreateHistoryEntry("key-1", "id-1", urlString, FenValue.Undefined, 0);

            state.Entries.Add(entry);
            SetCurrentEntry(state, 0);
        }

        private static FenObject CreateHistoryEntry(string key, string id, string url, FenValue state, long index)
        {
            var entry = new FenObject();
            entry.Set("key", FenValue.FromString(key));
            entry.Set("id", FenValue.FromString(id));
            entry.Set("url", FenValue.FromString(url ?? "about:blank"));
            entry.Set("state", state.IsNull || state.IsUndefined ? FenValue.Undefined : state);
            entry.Set("index", FenValue.FromNumber(index));
            entry.Set("sameDocument", FenValue.FromBoolean(true));
            entry.Set("on dispose", FenValue.Null);
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

        private static FenValue UpdateCurrentEntry(FenValue[] args, NavigationState state)
        {
            if (state.CurrentEntry == null)
                return FenValue.Undefined;

            if (args.Length > 0 && args[0].IsObject)
            {
                var options = args[0].AsObject();
                var nextState = options?.Get("state") ?? FenValue.Undefined;
                state.CurrentEntry.Set("state", nextState.IsUndefined || nextState.IsNull ? FenValue.Undefined : nextState);
            }

            return FenValue.Undefined;
        }

        private static FenValue Navigate(FenValue[] args, NavigationState state)
        {
            var context = state.Context;
            if (args.Length < 1)
                return RejectPromise("TypeError: URL required", context);

            var url = args[0].ToString();
            if (string.IsNullOrWhiteSpace(url))
                return RejectPromise("TypeError: URL required", context);

            var entryState = FenValue.Undefined;
            if (args.Length > 1 && args[1].IsObject)
            {
                var options = args[1].AsObject();
                var candidateState = options?.Get("state") ?? FenValue.Undefined;
                if (!candidateState.IsUndefined && !candidateState.IsNull)
                    entryState = candidateState;
            }

            EngineLogCompat.Warn($"[NavigationAPI] navigate() to {url}", LogCategory.JavaScript);

            var executor = new FenFunction("executor", (execArgs, execThis) =>
            {
                var resolve = execArgs.Length > 0 ? execArgs[0].AsFunction() : null;
                var reject = execArgs.Length > 1 ? execArgs[1].AsFunction() : null;

                ScheduleMicrotask(context, () =>
                {
                    try
                    {
                        var newEntry = AppendEntry(state, url, entryState);
                        resolve?.Invoke(new[] { FenValue.FromObject(newEntry) }, context);
                    }
                    catch (Exception ex)
                    {
                        reject?.Invoke(new[] { FenValue.FromObject(CreateDOMException("OperationError", ex.Message)) }, context);
                    }
                });

                return FenValue.Undefined;
            });

            return FenValue.FromObject(new JsPromise(FenValue.FromFunction(executor), context));
        }

        private static FenValue Reload(NavigationState state)
        {
            return FenValue.FromObject(ResolvedThenable.Resolved(
                state.CurrentEntry == null ? FenValue.Undefined : FenValue.FromObject(state.CurrentEntry),
                state.Context));
        }

        private static FenValue TraverseHistory(NavigationState state, int delta)
        {
            var newIndex = state.CurrentIndex + delta;
            if (newIndex < 0 || newIndex >= state.Entries.Count)
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined, state.Context));

            var executor = new FenFunction("executor", (execArgs, execThis) =>
            {
                var resolve = execArgs.Length > 0 ? execArgs[0].AsFunction() : null;

                ScheduleMicrotask(state.Context, () =>
                {
                    SetCurrentEntry(state, newIndex);
                    resolve?.Invoke(Array.Empty<FenValue>(), state.Context);
                });

                return FenValue.Undefined;
            });

            return FenValue.FromObject(new JsPromise(FenValue.FromFunction(executor), state.Context));
        }

        private static FenValue TraverseTo(FenValue[] args, NavigationState state)
        {
            if (args.Length < 1)
                return RejectPromise("TypeError: Key required", state.Context);

            var key = args[0].ToString();
            if (string.IsNullOrWhiteSpace(key))
                return RejectPromise("TypeError: Key required", state.Context);

            var targetIndex = -1;
            for (int i = 0; i < state.Entries.Count; i++)
            {
                if (string.Equals(state.Entries[i].Get("key").ToString(), key, StringComparison.Ordinal))
                {
                    targetIndex = i;
                    break;
                }
            }

            if (targetIndex < 0)
                return RejectPromise($"InvalidAccessError: No entry with key '{key}'", state.Context);

            var executor = new FenFunction("executor", (execArgs, execThis) =>
            {
                var resolve = execArgs.Length > 0 ? execArgs[0].AsFunction() : null;

                ScheduleMicrotask(state.Context, () =>
                {
                    SetCurrentEntry(state, targetIndex);
                    resolve?.Invoke(Array.Empty<FenValue>(), state.Context);
                });

                return FenValue.Undefined;
            });

            return FenValue.FromObject(new JsPromise(FenValue.FromFunction(executor), state.Context));
        }

        private static FenValue TraverseByType(NavigationState state)
        {
            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined, state.Context));
        }

        private static FenObject AppendEntry(NavigationState state, string url, FenValue entryState)
        {
            if (state.CurrentIndex >= 0 && state.CurrentIndex < state.Entries.Count - 1)
                state.Entries.RemoveRange(state.CurrentIndex + 1, state.Entries.Count - state.CurrentIndex - 1);

            var entryNumber = state.Entries.Count + 1;
            var newEntry = CreateHistoryEntry(
                key: $"key-{entryNumber}",
                id: $"id-{entryNumber}",
                url: url,
                state: entryState,
                index: state.Entries.Count);

            state.Entries.Add(newEntry);
            SetCurrentEntry(state, state.Entries.Count - 1);
            return newEntry;
        }

        private static void SetCurrentEntry(NavigationState state, int index)
        {
            state.CurrentIndex = index;
            state.CurrentEntry = (index >= 0 && index < state.Entries.Count) ? state.Entries[index] : null;

            if (state.CurrentEntry != null)
            {
                state.NavigationObject.Set("currentEntry", FenValue.FromObject(state.CurrentEntry));
            }
            else
            {
                state.NavigationObject.Set("currentEntry", FenValue.Null);
            }
        }

        private static void SetupEventTargetMethods(FenObject navigation, NavigationState state)
        {
            navigation.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (args, thisVal) =>
            {
                if (args.Length < 2)
                    return FenValue.Undefined;

                var type = args[0].ToString();
                var listener = args[1];

                switch (type)
                {
                    case "currententrychange":
                        state.OnCurrentEntryChange = listener;
                        navigation.Set("oncurrententrychange", listener);
                        break;
                    case "navigate":
                        state.OnNavigate = listener;
                        navigation.Set("onnavigate", listener);
                        break;
                    case "navigatesuccess":
                        state.OnNavigateSuccess = listener;
                        navigation.Set("onnavigatesuccess", listener);
                        break;
                    case "navigateerror":
                        state.OnNavigateError = listener;
                        navigation.Set("onnavigateerror", listener);
                        break;
                    case "popstate":
                        state.OnPopState = listener;
                        navigation.Set("onpopstate", listener);
                        break;
                }

                return FenValue.Undefined;
            })));

            navigation.Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", (args, thisVal) =>
            {
                if (args.Length < 2)
                    return FenValue.Undefined;

                var type = args[0].ToString();

                switch (type)
                {
                    case "currententrychange":
                        state.OnCurrentEntryChange = FenValue.Null;
                        navigation.Set("oncurrententrychange", FenValue.Null);
                        break;
                    case "navigate":
                        state.OnNavigate = FenValue.Null;
                        navigation.Set("onnavigate", FenValue.Null);
                        break;
                    case "navigatesuccess":
                        state.OnNavigateSuccess = FenValue.Null;
                        navigation.Set("onnavigatesuccess", FenValue.Null);
                        break;
                    case "navigateerror":
                        state.OnNavigateError = FenValue.Null;
                        navigation.Set("onnavigateerror", FenValue.Null);
                        break;
                    case "popstate":
                        state.OnPopState = FenValue.Null;
                        navigation.Set("onpopstate", FenValue.Null);
                        break;
                }

                return FenValue.Undefined;
            })));
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

        private static FenValue RejectPromise(string reason, IExecutionContext context)
        {
            return FenValue.FromObject(ResolvedThenable.Rejected(reason, context));
        }

        private static FenObject CreateDOMException(string name, string message)
        {
            var err = new FenObject();
            err.Set("name", FenValue.FromString(name));
            err.Set("message", FenValue.FromString(message ?? string.Empty));
            err.InternalClass = "DOMException";
            return err;
        }
    }
}
