using System;
using System.Text;
using Wasmtime;

namespace FenBrowser.Wasm;

/// <summary>
/// A compiled WASM module ready for instantiation.
/// </summary>
public sealed class WasmModule : IDisposable
{
    private readonly WasmEngine _engine;
    private readonly Module _module;

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
        var store = _engine.CreateStore();
        var linker = new Linker(_engine.InnerEngine);

        // WASI is not exposed by default - the host decides what the module can access.
        configureImports?.Invoke(linker, store);

        _engine.TrackInstance();
        try
        {
            var instance = linker.Instantiate(store, _module);
            return new WasmInstance(_engine, store, instance, instanceName ?? Name);
        }
        catch
        {
            _engine.UntrackInstance();
            store.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Gets the module exports (for inspection before instantiation).
    /// </summary>
    public IReadOnlyList<Export> Exports => _module.Exports;

    public void Dispose()
    {
        _module.Dispose();
    }
}

/// <summary>
/// A live WASM instance with host-defined limits.
/// </summary>
public sealed class WasmInstance : IDisposable
{
    private readonly WasmEngine _engine;
    private readonly Store _store;
    private readonly Instance _instance;
    private readonly object _callLock = new();

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
        lock (_callLock)
        {
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

                // Check fuel exhaustion - the engine may have run out of fuel
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
            catch (Exception ex)
            {
                sw.Stop();
                return WasmExecutionResult.Fail(
                    $"Unexpected WASM error in '{Name}.{functionName}': {ex.Message}",
                    fuelBefore - (ulong)Math.Max(0, _store.Fuel),
                    sw.Elapsed);
            }
        }
    }

    /// <summary>
    /// Gets an exported function.
    /// </summary>
    public Function? GetFunction(string name) => _instance.GetFunction(name);

    /// <summary>
    /// Gets an exported global.
    /// </summary>
    public Global? GetGlobal(string name) => _instance.GetGlobal(name);

    /// <summary>
    /// Gets an exported memory.
    /// </summary>
    public Memory? GetMemory(string name) => _instance.GetMemory(name);

    /// <summary>
    /// Gets an exported table.
    /// </summary>
    public Table? GetTable(string name) => _instance.GetTable(name);

    /// <summary>
    /// Reads bytes from exported linear memory.
    /// </summary>
    public byte[]? ReadMemory(string memoryName, int offset, int length)
    {
        var memory = GetMemory(memoryName);
        if (memory == null) return null;

        long memLength = memory.GetLength();
        if (offset < 0 || length < 0 || (long)offset + length > memLength)
            return null;

        var result = new byte[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = memory.ReadByte((long)offset + i);
        }
        return result;
    }

    /// <summary>
    /// Writes bytes into exported linear memory.
    /// </summary>
    public bool WriteMemory(string memoryName, int offset, ReadOnlyMemory<byte> data)
    {
        var memory = GetMemory(memoryName);
        if (memory == null) return false;

        long memLength = memory.GetLength();
        if (offset < 0 || (long)offset + data.Length > memLength)
            return false;

        var span = data.Span;
        for (int i = 0; i < span.Length; i++)
        {
            memory.WriteByte((long)offset + i, span[i]);
        }
        return true;
    }

    /// <summary>
    /// Reads a UTF-8 string from exported linear memory.
    /// </summary>
    public string? ReadMemoryString(string memoryName, int offset, int length)
    {
        var memory = GetMemory(memoryName);
        if (memory == null) return null;

        long memLength = memory.GetLength();
        if (offset < 0 || length < 0 || (long)offset + length > memLength)
            return null;

        return memory.ReadString(offset, length, Encoding.UTF8);
    }

    /// <summary>
    /// Gets the remaining fuel for this instance's store.
    /// </summary>
    public long RemainingFuel => _store.Fuel > long.MaxValue ? long.MaxValue : (long)_store.Fuel;

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
                ulong v => (long)v,
                float v => v,
                double v => v,
                bool v => v ? 1 : 0,
                _ => throw new ArgumentException(
                    $"Unsupported WASM argument type: {args[i]?.GetType().Name ?? "null"} at index {i}")
            };
        }

        return result;
    }

    public void Dispose()
    {
        _engine.UntrackInstance();
        _store.Dispose();
    }
}