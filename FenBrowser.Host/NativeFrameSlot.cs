using System;

namespace FenBrowser.Host;

/// <summary>
/// Owns one native-backed frame and prevents replacement or disposal while a
/// compositor reader is using it.
/// </summary>
internal sealed class NativeFrameSlot<T> : IDisposable where T : class, IDisposable
{
    private readonly object _sync = new();
    private T? _current;
    private bool _disposed;

    public bool TryUse(Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (_sync)
        {
            if (_disposed || _current == null)
            {
                return false;
            }

            action(_current);
            return true;
        }
    }

    public void Publish(T next)
    {
        ArgumentNullException.ThrowIfNull(next);

        lock (_sync)
        {
            if (_disposed)
            {
                next.Dispose();
                return;
            }

            var retiring = _current;
            _current = next;
            retiring?.Dispose();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            var retiring = _current;
            _current = null;
            retiring?.Dispose();
        }
    }

    public bool HasValue
    {
        get
        {
            lock (_sync)
            {
                return !_disposed && _current != null;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var retiring = _current;
            _current = null;
            retiring?.Dispose();
        }
    }
}
