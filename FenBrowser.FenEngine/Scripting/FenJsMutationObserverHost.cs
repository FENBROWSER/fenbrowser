// WHATWG DOM Living Standard: MutationObserver JS bridge
// Bridges FenJS MutationObserver constructor to FenBrowser.Core.Dom.V2.MutationObserver
// https://dom.spec.whatwg.org/#interface-mutationobserver

using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// Host-object wrapper that exposes a C# MutationObserver to FenJS scripts.
/// Each instance holds a JS callback and a backing C# MutationObserver whose
/// Action-based callback converts MutationRecords to JS objects and invokes
/// the stored JS function.
/// </summary>
internal sealed class FenJsMutationObserverHost
{
    private readonly MutationObserver _observer;
    private readonly FenJsBrowserScriptEngine _owner;
    private bool _disposed;

    /// <summary>JS callback function (the argument to new MutationObserver(cb)).</summary>
    public JsValue Callback { get; }

    /// <summary>Backing C# MutationObserver.</summary>
    public MutationObserver Observer => _observer;

    public FenJsMutationObserverHost(JsValue callback, FenJsBrowserScriptEngine owner)
    {
        Callback = callback;
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _observer = new MutationObserver(OnMutations);
    }

    private void OnMutations(IReadOnlyList<MutationRecord> records, MutationObserver observer)
    {
        if (_disposed)
        {
            return;
        }

        // Delegate to the owner so it can acquire the interpreter lock and
        // convert records on the correct thread.
        _owner.InvokeMutationObserverCallback(this, records);
    }

    public void Observe(Node target, MutationObserverInit options)
    {
        ThrowIfDisposed();
        _observer.Observe(target, options);
    }

    public void Disconnect()
    {
        ThrowIfDisposed();
        _observer.Disconnect();
    }

    public IReadOnlyList<MutationRecord> TakeRecords()
    {
        ThrowIfDisposed();
        return _observer.TakeRecords();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _observer.Disconnect();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FenJsMutationObserverHost));
        }
    }
}
