using System;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;

namespace FenBrowser.FenEngine.Workers
{
    /// <summary>
    /// Canonical promise creation helper for worker and service-worker surfaces.
    /// This intentionally always returns a real JsPromise, even when no execution
    /// context is available, so worker APIs do not split into legacy thenables.
    /// </summary>
    internal static class WorkerPromise
    {
        internal sealed class Handle
        {
            internal JsPromise Promise { get; set; }
            internal FenValue Resolve { get; set; } = FenValue.Undefined;
            internal FenValue Reject { get; set; } = FenValue.Undefined;
        }

        internal static Handle CreatePending(
            IExecutionContext context,
            string executorName = "workerPromiseExecutor")
        {
            var handle = new Handle();
            var executor = new FenFunction(executorName, (args, thisVal) =>
            {
                handle.Resolve = args.Length > 0 ? args[0] : FenValue.Undefined;
                handle.Reject = args.Length > 1 ? args[1] : FenValue.Undefined;
                return FenValue.Undefined;
            });

            handle.Promise = new JsPromise(FenValue.FromFunction(executor), context);
            return handle;
        }

        internal static JsPromise FromTask(
            Func<Task<FenValue>> valueFactory,
            IExecutionContext context,
            string ownerName)
        {
            var handle = CreatePending(context, ownerName + ".executor");
            _ = RunDetachedAsync(async () =>
            {
                try
                {
                    var value = await valueFactory().ConfigureAwait(false);
                    if (handle.Resolve.IsFunction)
                    {
                        handle.Resolve.AsFunction().Invoke(new[] { value }, context);
                    }
                }
                catch (Exception ex)
                {
                    if (handle.Reject.IsFunction)
                    {
                        handle.Reject.AsFunction().Invoke(new[] { FenValue.FromString(ex.Message) }, context);
                    }
                }
            }, ownerName);

            return handle.Promise;
        }

        private static Task RunDetachedAsync(Func<Task> operation, string ownerName)
        {
            return Task.Factory.StartNew(async () =>
            {
                try
                {
                    await operation().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FenBrowser.Core.EngineLogCompat.Warn(
                        $"[{ownerName}] Detached async operation failed: {ex.Message}",
                        LogCategory.ServiceWorker);
                }
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }
    }
}
