using System;
using System.Collections.Concurrent;
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
        // Keep the cadence aligned with CreateStore's deadline calculation.
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
        if (wasmBytes.IsEmpty)
            throw new ArgumentException("WASM bytes cannot be empty.", nameof(wasmBytes));

        var module = Module.FromBytes(_engine, name ?? "module", wasmBytes.ToArray());
        return new WasmModule(this, module, name ?? "module");
    }

    /// <summary>
    /// Compiles a WASM module from a file.
    /// </summary>
    public WasmModule CompileFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty.", nameof(path));

        var module = Module.FromFile(_engine, path);
        return new WasmModule(this, module, System.IO.Path.GetFileName(path));
    }

    internal Store CreateStore()
    {
        if (Volatile.Read(ref _activeInstanceCount) >= _limits.MaxInstances)
            throw new InvalidOperationException(
                $"WASM instance limit reached ({_limits.MaxInstances}). Consider unloading modules.");

        var store = new Store(_engine);

        // Enforce resource limits at the store level
        store.SetLimits(
            memorySize: (long)_limits.MaxMemoryBytes,
            tableElements: _limits.MaxTableElements,
            instances: _limits.MaxInstances,
            tables: _limits.MaxTables,
            memories: _limits.MaxMemories);

        // Set initial fuel budget
        store.Fuel = _limits.MaxFuelPerInstance;

        // Epoch deadlines are relative tick counts. Clamp to at least one tick so
        // sub-cadence execution budgets don't become an already-expired deadline.
        var epochTicks = (ulong)Math.Max(
            1d,
            Math.Ceiling(_limits.MaxExecutionTime.TotalMilliseconds / EpochTickMilliseconds));
        store.SetEpochDeadline(epochTicks);

        return store;
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
        Interlocked.Decrement(ref _activeInstanceCount);
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