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
            // SW §4.5.3: Set this worker as the controller for all in-scope clients.
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
            {
                return false;
            }

            if (!Uri.TryCreate(_originRoot, raw, out resolved))
            {
                return false;
            }

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

        private FenObject CreatePromise(Func<Task<FenValue>> valueFactory)
        {
            return WorkerPromise.FromTask(valueFactory, _context, nameof(ServiceWorkerClients));
        }
    }
}
