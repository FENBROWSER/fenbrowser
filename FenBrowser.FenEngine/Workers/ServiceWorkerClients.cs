using System;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;

namespace FenBrowser.FenEngine.Workers
{
    /// <summary>
    /// Minimal Service Worker Clients API surface.
    /// Provides promise-based helpers for claim(), matchAll(), and openWindow().
    /// </summary>
    public sealed class ServiceWorkerClients : FenObject
    {
        private readonly string _origin;
        private readonly Uri _originRoot;
        private readonly IExecutionContext _context;

        public ServiceWorkerClients(string origin, IExecutionContext context = null)
        {
            _origin = origin;
            _originRoot = ResolveOriginRoot(origin);
            _context = context;

            Set("claim", FenValue.FromFunction(new FenFunction("claim", Claim)));
            Set("matchAll", FenValue.FromFunction(new FenFunction("matchAll", MatchAll)));
            Set("openWindow", FenValue.FromFunction(new FenFunction("openWindow", OpenWindow)));
        }

        private FenValue Claim(FenValue[] args, FenValue thisVal)
        {
            // SW §4.5.3: Set this worker as the controller for all in-scope clients
            ServiceWorkerManager.Instance.ClaimClients(_origin);
            return FenValue.FromObject(CreatePromise(() => Task.FromResult(FenValue.Undefined)));
        }

        private FenValue MatchAll(FenValue[] args, FenValue thisVal)
        {
            var arr = new FenObject();
            arr.Set("length", FenValue.FromNumber(0));
            return FenValue.FromObject(CreatePromise(() => Task.FromResult(FenValue.FromObject(arr))));
        }

        private FenValue OpenWindow(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 1)
            {
                return FenValue.FromObject(CreatePromise(() =>
                    Task.FromException<FenValue>(new InvalidOperationException("openWindow requires a URL"))));
            }

            var raw = args[0].ToString();
            if (!TryResolveSameOrigin(raw, out var target))
            {
                return FenValue.FromObject(CreatePromise(() =>
                    Task.FromException<FenValue>(new UnauthorizedAccessException("openWindow blocked by origin policy"))));
            }

            var client = BuildWindowClient(target);
            return FenValue.FromObject(CreatePromise(() => Task.FromResult(FenValue.FromObject(client))));
        }

        private FenObject BuildWindowClient(Uri target)
        {
            var client = new FenObject();
            var id = $"client-{Guid.NewGuid():N}";
            client.Set("id", FenValue.FromString(id));
            client.Set("url", FenValue.FromString(target.AbsoluteUri));
            client.Set("type", FenValue.FromString("window"));

            client.Set("postMessage", FenValue.FromFunction(new FenFunction("postMessage", (a, t) => FenValue.Undefined)));
            client.Set("focus", FenValue.FromFunction(new FenFunction("focus", (a, t) =>
                FenValue.FromObject(CreatePromise(() => Task.FromResult(FenValue.FromObject(client)))))));

            return client;
        }

        private bool TryResolveSameOrigin(string raw, out Uri resolved)
        {
            resolved = null;
            if (_originRoot == null || string.IsNullOrWhiteSpace(raw))
                return false;

            if (!Uri.TryCreate(_originRoot, raw, out resolved))
                return false;

            if (!(resolved.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                  resolved.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                return false;

            return resolved.Scheme.Equals(_originRoot.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   resolved.Host.Equals(_originRoot.Host, StringComparison.OrdinalIgnoreCase) &&
                   resolved.Port == _originRoot.Port;
        }

        private static Uri ResolveOriginRoot(string origin)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                return null;

            return new UriBuilder(uri)
            {
                Path = "/",
                Query = string.Empty,
                Fragment = string.Empty
            }.Uri;
        }

        private static Task RunDetachedAsync(Func<Task> operation)
        {
            return Task.Factory.StartNew(async () =>
            {
                try
                {
                    await operation().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FenBrowser.Core.FenLogger.Warn($"[ServiceWorkerClients] Detached async operation failed: {ex.Message}",
                        FenBrowser.Core.Logging.LogCategory.ServiceWorker);
                }
            }, System.Threading.CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }

        private FenObject CreatePromise(Func<Task<FenValue>> valueFactory)
        {
            if (_context != null)
            {
                FenValue capturedResolve = FenValue.Undefined;
                FenValue capturedReject = FenValue.Undefined;
                var executor = new FenFunction("executor", (args, thisVal) =>
                {
                    capturedResolve = args.Length > 0 ? args[0] : FenValue.Undefined;
                    capturedReject = args.Length > 1 ? args[1] : FenValue.Undefined;
                    return FenValue.Undefined;
                });
                var jsPromise = new JsPromise(FenValue.FromFunction(executor), _context);
                _ = RunDetachedAsync(async () =>
                {
                    try
                    {
                        var value = await valueFactory().ConfigureAwait(false);
                        if (capturedResolve.IsFunction)
                            capturedResolve.AsFunction().Invoke(new[] { value }, _context);
                    }
                    catch (Exception ex)
                    {
                        if (capturedReject.IsFunction)
                            capturedReject.AsFunction().Invoke(new[] { FenValue.FromString(ex.Message) }, _context);
                    }
                });
                return jsPromise;
            }

            // Fallback: hand-rolled promise
            var promise = new FenObject();
            _ = RunDetachedAsync(async () =>
            {
                try
                {
                    var result = await valueFactory().ConfigureAwait(false);
                    promise.Set("__state", FenValue.FromString("fulfilled"));
                    promise.Set("__result", result);
                    var onFulfilled = promise.Get("onFulfilled");
                    if (onFulfilled.IsFunction)
                        onFulfilled.AsFunction().Invoke(new[] { result }, null);
                }
                catch (Exception ex)
                {
                    var reason = FenValue.FromString(ex.Message);
                    promise.Set("__state", FenValue.FromString("rejected"));
                    promise.Set("__reason", reason);
                    var onRejected = promise.Get("onRejected");
                    if (onRejected.IsFunction)
                        onRejected.AsFunction().Invoke(new[] { reason }, null);
                }
            });

            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, _) =>
            {
                if (args.Length > 0 && args[0].IsFunction) promise.Set("onFulfilled", args[0]);
                if (args.Length > 1 && args[1].IsFunction) promise.Set("onRejected", args[1]);
                var state = promise.Get("__state");
                if (!state.IsUndefined && state.ToString() == "fulfilled" && args.Length > 0 && args[0].IsFunction)
                    args[0].AsFunction().Invoke(new[] { promise.Get("__result") }, null);
                else if (!state.IsUndefined && state.ToString() == "rejected" && args.Length > 1 && args[1].IsFunction)
                    args[1].AsFunction().Invoke(new[] { promise.Get("__reason") }, null);
                return FenValue.FromObject(promise);
            })));

            return promise;
        }
    }
}
