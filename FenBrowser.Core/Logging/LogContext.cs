using System;
using System.Collections.Generic;
using System.Threading;

namespace FenBrowser.Core.Logging
{
    /// <summary>
    /// Async-local logging scope used to correlate browser, engine, and tooling activity.
    /// </summary>
    public static class LogContext
    {
        private static readonly AsyncLocal<LogScopeState> CurrentState = new AsyncLocal<LogScopeState>();

        public static string CurrentCorrelationId => CurrentState.Value?.CorrelationId;

        public static string CurrentComponent
        {
            get
            {
                for (var state = CurrentState.Value; state != null; state = state.Parent)
                {
                    if (!string.IsNullOrWhiteSpace(state.Component))
                    {
                        return state.Component;
                    }
                }

                return null;
            }
        }

        public static IDisposable Push(
            string component = null,
            string correlationId = null,
            IReadOnlyDictionary<string, object> data = null)
        {
            var parent = CurrentState.Value;
            var resolvedCorrelationId = string.IsNullOrWhiteSpace(correlationId)
                ? parent?.CorrelationId ?? Guid.NewGuid().ToString("n")
                : correlationId;
            var state = new LogScopeState(parent, component, resolvedCorrelationId, Clone(data));
            CurrentState.Value = state;
            return new PopWhenDisposed(parent);
        }

        public static Dictionary<string, object> CaptureData()
        {
            if (CurrentState.Value == null)
            {
                return null;
            }

            var merged = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            CaptureInto(CurrentState.Value, merged);
            return merged.Count == 0 ? null : merged;
        }

        private static void CaptureInto(LogScopeState state, Dictionary<string, object> destination)
        {
            if (state == null || destination == null)
            {
                return;
            }

            CaptureInto(state.Parent, destination);

            if (state.Data == null)
            {
                return;
            }

            foreach (var pair in state.Data)
            {
                destination[pair.Key] = pair.Value;
            }
        }

        private static Dictionary<string, object> Clone(IReadOnlyDictionary<string, object> data)
        {
            if (data == null || data.Count == 0)
            {
                return null;
            }

            var clone = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in data)
            {
                clone[pair.Key] = pair.Value;
            }

            return clone;
        }

        private sealed class PopWhenDisposed : IDisposable
        {
            private readonly LogScopeState _parent;
            private bool _disposed;

            public PopWhenDisposed(LogScopeState parent)
            {
                _parent = parent;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                CurrentState.Value = _parent;
            }
        }

        private sealed class LogScopeState
        {
            public LogScopeState(
                LogScopeState parent,
                string component,
                string correlationId,
                Dictionary<string, object> data)
            {
                Parent = parent;
                Component = component;
                CorrelationId = correlationId;
                Data = data;
            }

            public LogScopeState Parent { get; }

            public string Component { get; }

            public string CorrelationId { get; }

            public Dictionary<string, object> Data { get; }
        }
    }
}
