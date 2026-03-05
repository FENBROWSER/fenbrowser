using System;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Workers
{
    /// <summary>
    /// Minimal Service Worker Clients API surface.
    /// Provides promise-based helpers for claim(), matchAll(), and openWindow().
    /// </summary>
    public sealed class ServiceWorkerClients : FenObject
    {
        private readonly Uri _originRoot;

        public ServiceWorkerClients(string origin)
        {
            _originRoot = ResolveOriginRoot(origin);

            Set("claim", FenValue.FromFunction(new FenFunction("claim", Claim)));
            Set("matchAll", FenValue.FromFunction(new FenFunction("matchAll", MatchAll)));
            Set("openWindow", FenValue.FromFunction(new FenFunction("openWindow", OpenWindow)));
        }

        private FenValue Claim(FenValue[] args, FenValue thisVal)
        {
            return FenValue.FromObject(CreatePromise(() => Task.FromResult(FenValue.Undefined)));
        }

        private FenValue MatchAll(FenValue[] args, FenValue thisVal)
        {
            // Current runtime does not track controlled window clients yet.
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

            client.Set("postMessage", FenValue.FromFunction(new FenFunction("postMessage", (args, thisVal) =>
            {
                // Placeholder message channel bridge for future client messaging wiring.
                return FenValue.Undefined;
            })));

            client.Set("focus", FenValue.FromFunction(new FenFunction("focus", (args, thisVal) =>
            {
                return FenValue.FromObject(CreatePromise(() => Task.FromResult(FenValue.FromObject(client))));
            })));

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
            {
                return false;
            }

            return resolved.Scheme.Equals(_originRoot.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   resolved.Host.Equals(_originRoot.Host, StringComparison.OrdinalIgnoreCase) &&
                   resolved.Port == _originRoot.Port;
        }

        private static Uri ResolveOriginRoot(string origin)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            {
                return null;
            }

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
                    System.Diagnostics.Debug.WriteLine($"[ServiceWorker] Detached async operation failed: {ex.Message}");
                }
            }, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }        private static FenObject CreatePromise(Func<Task<FenValue>> valueFactory)
        {
            var promise = new FenObject();
            _ = RunDetachedAsync(async () =>
            {
                try
                {
                    var result = await valueFactory().ConfigureAwait(false);
                    promise.Set("__state", FenValue.FromString("fulfilled"));
                    promise.Set("__result", result);
                    if (promise.Has("onFulfilled"))
                    {
                        var cb = promise.Get("onFulfilled").AsFunction();
                        cb?.Invoke(new[] { result }, null);
                    }
                }
                catch (Exception ex)
                {
                    var reason = FenValue.FromString(ex.Message);
                    promise.Set("__state", FenValue.FromString("rejected"));
                    promise.Set("__reason", reason);
                    if (promise.Has("onRejected"))
                    {
                        var cb = promise.Get("onRejected").AsFunction();
                        cb?.Invoke(new[] { reason }, null);
                    }
                }
            });

            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, _) =>
            {
                if (args.Length > 0) promise.Set("onFulfilled", args[0]);
                if (args.Length > 1) promise.Set("onRejected", args[1]);

                var state = promise.Get("__state").ToString();
                if (string.Equals(state, "fulfilled", StringComparison.OrdinalIgnoreCase) &&
                    args.Length > 0 && args[0].IsFunction)
                {
                    args[0].AsFunction().Invoke(new[] { promise.Get("__result") }, null);
                }
                else if (string.Equals(state, "rejected", StringComparison.OrdinalIgnoreCase) &&
                         args.Length > 1 && args[1].IsFunction)
                {
                    args[1].AsFunction().Invoke(new[] { promise.Get("__reason") }, null);
                }

                return FenValue.FromObject(promise);
            })));

            return promise;
        }
    }
}

