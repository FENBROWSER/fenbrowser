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
        private static FenObject _currentEntry;
        private static FenObject _transition;
        private static readonly List<FenObject> _entries = new();
        private static int _currentIndex = -1;

        private static FenValue _onCurrentEntryChange;
        private static FenValue _onNavigate;
        private static FenValue _onNavigateSuccess;
        private static FenValue _onNavigateError;
        private static FenValue _onPopState;

        private static IExecutionContext _context;

        public static FenObject CreateNavigation(IExecutionContext context)
        {
            _context = context;
            var navigation = new FenObject();

            InitializeNavigation(context);

            navigation.Set("currentEntry", FenValue.FromObject(_currentEntry));
            navigation.Set("transition", FenValue.Null);

            navigation.Set("oncurrententrychange", FenValue.Null);
            navigation.Set("onnavigate", FenValue.Null);
            navigation.Set("onnavigatesuccess", FenValue.Null);
            navigation.Set("onnavigateerror", FenValue.Null);
            navigation.Set("onpopstate", FenValue.Null);

            navigation.Set("entries", FenValue.FromFunction(new FenFunction("entries", (args, thisVal) =>
            {
                var arr = new FenObject();
                arr.Set("length", FenValue.FromNumber(_entries.Count));
                for (int i = 0; i < _entries.Count; i++)
                    arr.Set(i.ToString(), FenValue.FromObject(_entries[i]));
                return FenValue.FromObject(arr);
            })));

            navigation.Set("getCurrentEntry", FenValue.FromFunction(new FenFunction("getCurrentEntry", (args, thisVal) =>
                FenValue.FromObject(_currentEntry))));

            navigation.Set("updateCurrentEntry", FenValue.FromFunction(new FenFunction("updateCurrentEntry", (args, thisVal) =>
            {
                return FenValue.Undefined;
            })));

            navigation.Set("navigate", FenValue.FromFunction(new FenFunction("navigate", (args, thisVal) => Navigate(args, thisVal, context))));
            navigation.Set("reload", FenValue.FromFunction(new FenFunction("reload", (args, thisVal) => Reload(args, thisVal, context))));
            navigation.Set("back", FenValue.FromFunction(new FenFunction("back", (args, thisVal) => TraverseHistory(args, thisVal, context, -1))));
            navigation.Set("forward", FenValue.FromFunction(new FenFunction("forward", (args, thisVal) => TraverseHistory(args, thisVal, context, 1))));
            navigation.Set("traverseTo", FenValue.FromFunction(new FenFunction("traverseTo", (args, thisVal) => TraverseTo(args, thisVal, context))));
            navigation.Set("traverseByType", FenValue.FromFunction(new FenFunction("traverseByType", (args, thisVal) => TraverseByType(args, thisVal, context))));

            SetupEventTargetMethods(navigation, context);
            return navigation;
        }

private static void InitializeNavigation(IExecutionContext context)
{
    _entries.Clear();
    string currentUrl = context.CurrentUrl;
    var urlString = string.IsNullOrEmpty(currentUrl) ? "about:blank" : currentUrl;
    _currentEntry = CreateHistoryEntry(context, "key-1", "id-1", urlString, FenValue.Undefined, 0);
    _entries.Add(_currentEntry);
    _currentIndex = 0;
}

        private static FenObject CreateHistoryEntry(IExecutionContext context, string key, string id, string url, FenValue state, long index)
        {
            var entry = new FenObject();
            entry.Set("key", FenValue.FromString(key));
            entry.Set("id", FenValue.FromString(id));
            entry.Set("url", FenValue.FromString(url));
            entry.Set("state", state.IsNull || state.IsUndefined ? FenValue.Undefined : state);
            entry.Set("index", FenValue.FromNumber(index));
            entry.Set("sameDocument", FenValue.FromBoolean(true));
            entry.Set("on dispose", FenValue.Null);
            return entry;
        }

        private static FenValue Navigate(FenValue[] args, FenValue thisVal, IExecutionContext context)
        {
            if (args.Length < 1)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: URL required", context));
            }

            var url = args[0].ToString();

            EngineLogCompat.Warn($"[NavigationAPI] navigate() to {url}", LogCategory.JavaScript);

            EventLoopCoordinator.Instance.ScheduleMicrotask(() =>
            {
                var entryNum = _entries.Count + 1;
                var newEntry = CreateHistoryEntry(context, $"key-{entryNum}", $"id-{entryNum}", url, FenValue.Undefined, entryNum - 1);
                _entries.Add(newEntry);
                _currentIndex = _entries.Count - 1;
                _currentEntry = newEntry;
            });

            var executor = new FenFunction("executor", (execArgs, execThis) =>
            {
                var resolve = execArgs[0].AsFunction();
                var reject = execArgs[1].AsFunction();

                if (args.Length < 1)
                {
                    reject.Invoke(new[] { FenValue.FromObject(CreateDOMException("TypeError", "URL required")) }, context);
                    return FenValue.Undefined;
                }

                EventLoopCoordinator.Instance.ScheduleMicrotask(() =>
                {
                    var entryNum = _entries.Count + 1;
                    var newEntry = CreateHistoryEntry(context, $"key-{entryNum}", $"id-{entryNum}", url, FenValue.Undefined, entryNum - 1);
                    _entries.Add(newEntry);
                    _currentIndex = _entries.Count - 1;
                    _currentEntry = newEntry;
                    resolve.Invoke(new[] { FenValue.FromObject(newEntry) }, context);
                });

                return FenValue.Undefined;
            });

            return FenValue.FromObject(new JsPromise(FenValue.FromFunction(executor), context));
        }

        private static FenValue Reload(FenValue[] args, FenValue thisVal, IExecutionContext context)
        {
            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(_currentEntry), context));
        }

        private static FenValue TraverseHistory(FenValue[] args, FenValue thisVal, IExecutionContext context, int delta)
        {
            var newIndex = _currentIndex + delta;
            if (newIndex < 0 || newIndex >= _entries.Count)
            {
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined, context));
            }

            var executor = new FenFunction("executor", (execArgs, execThis) =>
            {
                var resolve = execArgs[0].AsFunction();

                EventLoopCoordinator.Instance.ScheduleMicrotask(() =>
                {
                    _currentIndex = newIndex;
                    _currentEntry = _entries[newIndex];
                    resolve.Invoke(Array.Empty<FenValue>(), context);
                });

                return FenValue.Undefined;
            });

            return FenValue.FromObject(new JsPromise(FenValue.FromFunction(executor), context));
        }

        private static FenValue TraverseTo(FenValue[] args, FenValue thisVal, IExecutionContext context)
        {
            if (args.Length < 1)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Key required", context));
            }

            var key = args[0].ToString();
            var targetIndex = -1;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Get("key").ToString() == key)
                {
                    targetIndex = i;
                    break;
                }
            }

            if (targetIndex < 0)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: No entry with key '{key}'", context));
            }

            var executor = new FenFunction("executor", (execArgs, execThis) =>
            {
                var resolve = execArgs[0].AsFunction();

                EventLoopCoordinator.Instance.ScheduleMicrotask(() =>
                {
                    _currentIndex = targetIndex;
                    _currentEntry = _entries[targetIndex];
                    resolve.Invoke(Array.Empty<FenValue>(), context);
                });

                return FenValue.Undefined;
            });

            return FenValue.FromObject(new JsPromise(FenValue.FromFunction(executor), context));
        }

        private static FenValue TraverseByType(FenValue[] args, FenValue thisVal, IExecutionContext context)
        {
            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined, context));
        }

        private static void SetupEventTargetMethods(FenObject navigation, IExecutionContext context)
        {
            navigation.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (args, thisVal) =>
            {
                if (args.Length < 2) return FenValue.Undefined;
                var type = args[0].ToString();
                var listener = args[1];

                switch (type)
                {
                    case "currententrychange": _onCurrentEntryChange = listener; break;
                    case "navigate": _onNavigate = listener; break;
                    case "navigatesuccess": _onNavigateSuccess = listener; break;
                    case "navigateerror": _onNavigateError = listener; break;
                    case "popstate": _onPopState = listener; break;
                }

                return FenValue.Undefined;
            })));

            navigation.Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", (args, thisVal) =>
            {
                if (args.Length < 2) return FenValue.Undefined;
                var type = args[0].ToString();

                switch (type)
                {
                    case "currententrychange": _onCurrentEntryChange = FenValue.Null; break;
                    case "navigate": _onNavigate = FenValue.Null; break;
                    case "navigatesuccess": _onNavigateSuccess = FenValue.Null; break;
                    case "navigateerror": _onNavigateError = FenValue.Null; break;
                    case "popstate": _onPopState = FenValue.Null; break;
                }

                return FenValue.Undefined;
            })));
        }

        private static FenObject CreateDOMException(string name, string message)
        {
            var err = new FenObject();
            err.Set("name", FenValue.FromString(name));
            err.Set("message", FenValue.FromString(message));
            err.InternalClass = "DOMException";
            return err;
        }
    }
}