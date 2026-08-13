using System;
using System.Text;
using System.Threading;
using Wasmtime;

namespace FenBrowser.Wasm;

/// <summary>
/// A compiled WASM module ready for instantiation.
/// </summary>
public sealed class WasmModule : IDisposable
{
    private readonly WasmEngine _engine;
    private readonly Module _module;
    private int _disposed;

    internal WasmModule(WasmEngine engine, Module module, string name)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _module = module ?? throw new ArgumentNullException(nameof(module));
        Name = name;
    }

    public string Name { get; }

    /// <summary>
    /// Instantiates the module with the provided imports.
    /// </summary>
    public WasmInstance Instantiate(string? instanceName = null, Action<Linker, Store>? configureImports = null)
    {
        ThrowIfDisposed();

        // Reserve admission before allocating the Store or running host import
        // configuration. This makes MaxInstances a real concurrent limit instead of
        // an advisory pre-check followed by a later atomic increment.
        _engine.TrackInstance();
        Store? store = null;
        try
        {
            store = _engine.CreateStore();
            var linker = new Linker(_engine.InnerEngine);

            // WASI is not exposed by default - the host decides what the module can access.
            configureImports?.Invoke(linker, store);

            var instance = linker.Instantiate(store, _module);
            return new WasmInstance(_engine, store, instance, instanceName ?? Name);
        }
        catch
        {
            _engine.UntrackInstance();
            store?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Gets the module exports (for inspection before instantiation).
    /// </summary>
    public IReadOnlyList<Export> Exports
    {
        get
        {
            ThrowIfDisposed();
            return _module.Exports;
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WasmModule));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _module.Dispose();
    }
}

/// <summary>
/// A live WASM instance with host-defined limits. Wasmtime Store/Instance export
/// objects are intentionally not exposed: all guest interaction stays behind this
/// wrapper so disposal checks, serialization, and accounting cannot be bypassed.
/// </summary>
public sealed class WasmInstance : IDisposable
{
    private readonly WasmEngine _engine;
    private readonly Store _store;
    private readonly Instance _instance;
    private readonly object _callLock = new();
    private int _disposed;

    internal WasmInstance(WasmEngine engine, Store store, Instance instance, string name)
    {
        _engine = engine;
        _store = store;
        _instance = instance;
        Name = name;
    }

    public string Name { get; }

    /// <summary>
    /// Invokes an exported function.
    /// </summary>
    public WasmExecutionResult Invoke(string functionName, params object[] args)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            throw new ArgumentException("Function name cannot be empty.", nameof(functionName));

        lock (_callLock)
        {
            ThrowIfDisposed();

            ulong fuelBefore = (ulong)Math.Max(0, _store.Fuel);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var function = _instance.GetFunction(functionName);
                if (function == null)
                {
                    sw.Stop();
                    return WasmExecutionResult.Fail(
                        $"Function '{functionName}' not found in module '{Name}'.",
                        fuelBefore - (ulong)Math.Max(0, _store.Fuel),
                        sw.Elapsed);
                }

                var boxes = ConvertToValueBoxes(args);
                var result = function.Invoke(boxes);

                sw.Stop();

                if (_store.Fuel <= 0 && fuelBefore > 0)
                {
                    return WasmExecutionResult.Fail(
                        $"WASM execution exceeded fuel limit in '{Name}.{functionName}'.",
                        fuelBefore - (ulong)Math.Max(0, _store.Fuel),
                        sw.Elapsed);
                }

                return WasmExecutionResult.Ok(result, fuelBefore - (ulong)Math.Max(0, _store.Fuel), sw.Elapsed);
            }
            catch (WasmtimeException ex)
            {
                sw.Stop();
                return WasmExecutionResult.Fail(
                    $"WASM error in '{Name}.{functionName}': {ex.Message}",
                    fuelBefore - (ulong)Math.Max(0, _store.Fuel),
                    sw.Elapsed);
            }
            catch (Exception)
            {
                sw.Stop();
                return WasmExecutionResult.Fail(
                    $"Host invocation failed in '{Name}.{functionName}'.",
                    fuelBefore - (ulong)Math.Max(0, _store.Fuel),
                    sw.Elapsed);
            }
        }
    }

    /// <summary>
    /// Reads bytes from exported linear memory.
    /// </summary>
    public byte[]? ReadMemory(string memoryName, int offset, int length)
    {
        lock (_callLock)
        {
            ThrowIfDisposed();
            var memory = FindMemory(memoryName);
            if (memory == null) return null;

            long memLength = memory.GetLength();
            if (offset < 0 || length < 0 || (long)offset + length > memLength)
                return null;

            var result = new byte[length];
            memory.GetSpan(offset, length).CopyTo(result);
            return result;
        }
    }

    /// <summary>
    /// Writes bytes into exported linear memory.
    /// </summary>
    public bool WriteMemory(string memoryName, int offset, ReadOnlyMemory<byte> data)
    {
        lock (_callLock)
        {
            ThrowIfDisposed();
            var memory = FindMemory(memoryName);
            if (memory == null) return false;

            long memLength = memory.GetLength();
            if (offset < 0 || (long)offset + data.Length > memLength)
                return false;

            data.Span.CopyTo(memory.GetSpan(offset, data.Length));
            return true;
        }
    }

    /// <summary>
    /// Reads a UTF-8 string from exported linear memory.
    /// </summary>
    public string? ReadMemoryString(string memoryName, int offset, int length)
    {
        lock (_callLock)
        {
            ThrowIfDisposed();
            var memory = FindMemory(memoryName);
            if (memory == null) return null;

            long memLength = memory.GetLength();
            if (offset < 0 || length < 0 || (long)offset + length > memLength)
                return null;

            return memory.ReadString(offset, length, Encoding.UTF8);
        }
    }

    /// <summary>
    /// Gets the remaining fuel for this instance's store.
    /// </summary>
    public long RemainingFuel
    {
        get
        {
            lock (_callLock)
            {
                ThrowIfDisposed();
                return _store.Fuel > long.MaxValue ? long.MaxValue : (long)_store.Fuel;
            }
        }
    }

    private Memory? FindMemory(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return _instance.GetMemory(name);
    }

    private static ValueBox[] ConvertToValueBoxes(object[] args)
    {
        if (args == null || args.Length == 0)
            return Array.Empty<ValueBox>();

        var result = new ValueBox[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            result[i] = args[i] switch
            {
                int v => v,
                long v => v,
                uint v => (long)v,
                ulong v => unchecked((long)v),
                float v => v,
                double v => v,
                bool v => v ? 1 : 0,
                _ => throw new ArgumentException(
                    $"Unsupported WASM argument type: {args[i]?.GetType().Name ?? "null"} at index {i}")
            };
        }

        return result;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WasmInstance));
    }

    public void Dispose()
    {
        lock (_callLock)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                _store.Dispose();
            }
            finally
            {
                _engine.UntrackInstance();
            }
        }
    }
}
