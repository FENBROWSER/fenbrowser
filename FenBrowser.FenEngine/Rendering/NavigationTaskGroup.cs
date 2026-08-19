using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Rendering;

internal sealed class NavigationTaskGroup : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<int, Task> _tasks = new();
    private int _nextTaskId;
    private int _disposed;

    public CancellationToken Token => _lifetime.Token;

    public Task Run(Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var id = Interlocked.Increment(ref _nextTaskId);
        Task task;
        try
        {
            task = operation(Token);
        }
        catch (Exception ex)
        {
            task = Task.FromException(ex);
        }

        _tasks[id] = task;
        _ = task.ContinueWith(
            completed => Complete(id, completed),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    public async Task CancelAndDrainAsync()
    {
        if (!_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
        }

        var tasks = _tasks.Values.ToArray();
        if (tasks.Length == 0) return;

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void Complete(int id, Task task)
    {
        _tasks.TryRemove(id, out _);
        if (task.IsFaulted && task.Exception != null)
        {
            var error = task.Exception.GetBaseException();
            EngineLogCompat.Warn(
                $"[NavigationTaskGroup] Background operation failed: {error.Message}",
                LogCategory.Rendering);
        }
    }
}
