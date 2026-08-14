using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Wasmtime;

namespace FenBrowser.Wasm;

/// <summary>
/// A sandboxed WebAssembly engine wrapping Wasmtime.
/// Enforces resource limits, fuel consumption, and execution timeouts.
/// </summary>
public sealed class WasmEngine : IDisposable
{
    private const int EpochTickMilliseconds = 100;

    private readonly Engine _engine;
    private readonly WasmResourceLimits _limits;
    private readonly Timer _epochTimer;
    private readonly object _lifecycleLock = new();
    private int _activeInstanceCount;
    private int _disposed;

    public WasmEngine(WasmResourceLimits? limits = null)
    {
        _limits = limits ?? new WasmResourceLimits();
        _limits.Normalize();

        var config = new Config();
        config.WithFuelConsumption(true);
        config.WithEpochInterruption(true);
        config.WithWasmThreads(true);
        config.WithReferenceTypes(true);
        config.WithTailCalls(true);
        config.WithSIMD(true);
        config.WithBulkMemory(true);
        config.WithMultiValue(true);
        config.WithMultiMemory(true);
        config.WithMemory64(false);
        config.WithStaticMemoryMaximumSize(_limits.MaxMemoryBytes);

        _engine = new Engine(config);

        // Epoch interruption only works when the embedder advances the engine epoch.
        // Keep the cadence aligned with the per-call deadline calculation.
        _epochTimer = new Timer(
            static state => ((WasmEngine)state!).AdvanceEpoch(),
            this,
            EpochTickMilliseconds,
            EpochTickMilliseconds);
    }

    public WasmResourceLimits Limits => _limits;
    public int ActiveInstanceCount => Volatile.Read(ref _activeInstanceCount);

    internal Engine InnerEngine => _engine;

    /// <summary>
    /// Compiles a WASM module from raw bytes.
    /// </summary>
    public WasmModule Compile(ReadOnlyMemory<byte> wasmBytes, string? name = null)
    {
        ThrowIfDisposed();
        if (wasmBytes.IsEmpty)
            throw new ArgumentException("WASM bytes cannot be empty.", nameof(wasmBytes));
        if (wasmBytes.Length > _limits.MaxModuleBytes)
        {
            throw new InvalidDataException(
                $"WASM module exceeds the configured compilation limit ({wasmBytes.Length} > {_limits.MaxModuleBytes} bytes).");
        }

        var module = Module.FromBytes(_engine, name ?? "module", wasmBytes.ToArray());
        return new WasmModule(this, module, name ?? "module");
    }

    /// <summary>
    /// Compiles a WASM module from a file.
    /// </summary>
    public WasmModule CompileFromFile(string path)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty.", nameof(path));

        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("WASM module file was not found.", path);
        if (fileInfo.Length <= 0)
            throw new InvalidDataException("WASM module file is empty.");
        if (fileInfo.Length > _limits.MaxModuleBytes)
        {
            throw new InvalidDataException(
                $"WASM module exceeds the configured compilation limit ({fileInfo.Length} > {_limits.MaxModuleBytes} bytes).");
        }

        var module = Module.FromFile(_engine, path);
        return new WasmModule(this, module, Path.GetFileName(path));
    }

    internal Store CreateStore()
    {
        ThrowIfDisposed();

        // Instance admission is performed atomically by TrackInstance(). Do not make
        // a separate advisory count check here; callers reserve an instance before
        // creating the Store so concurrent instantiation cannot race the limit.
        var store = new Store(_engine);

        // Enforce resource limits at the store level.
        store.SetLimits(
            memorySize: (long)_limits.MaxMemoryBytes,
            tableElements: _limits.MaxTableElements,
            instances: _limits.MaxInstances,
            tables: _limits.MaxTables,
            memories: _limits.MaxMemories);

        // Set initial fuel budget.
        store.Fuel = _limits.MaxFuelPerInstance;

        ResetExecutionDeadline(store);
        return store;
    }

    /// <summary>
    /// Refreshes the Wasmtime epoch deadline for one guest invocation. Epoch
    /// deadlines are relative to the engine's current epoch, so installing the
    /// deadline only when the Store is created would make later calls inherit a
    /// stale/expired timeout.
    /// </summary>
    internal void ResetExecutionDeadline(Store store)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(store);
        store.SetEpochDeadline(ComputeExecutionDeadlineTicks());
    }

    private ulong ComputeExecutionDeadlineTicks()
    {
        // Epoch deadlines are relative tick counts. Clamp to at least one tick so
        // sub-cadence execution budgets don't become an already-expired deadline.
        return (ulong)Math.Max(
            1d,
            Math.Ceiling(_limits.MaxExecutionTime.TotalMilliseconds / EpochTickMilliseconds));
    }

    private void AdvanceEpoch()
    {
        lock (_lifecycleLock)
        {
            if (_disposed != 0)
                return;

            _engine.IncrementEpoch();
        }
    }

    internal void TrackInstance()
    {
        ThrowIfDisposed();

        var count = Interlocked.Increment(ref _activeInstanceCount);
        if (count > _limits.MaxInstances)
        {
            Interlocked.Decrement(ref _activeInstanceCount);
            throw new InvalidOperationException(
                $"WASM instance limit reached ({_limits.MaxInstances}).");
        }
    }

    internal void UntrackInstance()
    {
        var count = Interlocked.Decrement(ref _activeInstanceCount);
        if (count < 0)
        {
            Interlocked.Exchange(ref _activeInstanceCount, 0);
            throw new InvalidOperationException("WASM instance accounting underflow.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WasmEngine));
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed != 0)
                return;

            _disposed = 1;
            _epochTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _epochTimer.Dispose();
            _engine.Dispose();
        }
    }
}

/// <summary>
/// Exception thrown when WASM execution fails or exceeds limits.
/// </summary>
public sealed class WasmExecutionException : Exception
{
    public WasmExecutionException(string operation, string message, long fuelConsumed, TimeSpan duration)
        : base($"WASM {operation} failed: {message} (fuel={fuelConsumed}, duration={duration.TotalMilliseconds:F0}ms)")
    {
        Operation = operation;
        FuelConsumed = fuelConsumed;
        Duration = duration;
    }

    public string Operation { get; }
    public long FuelConsumed { get; }
    public TimeSpan Duration { get; }
}
