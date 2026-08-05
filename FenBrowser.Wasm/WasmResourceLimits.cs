using FenBrowser.Core.Logging;

namespace FenBrowser.Wasm;

/// <summary>
/// Resource limits for the WebAssembly engine.
/// These prevent untrusted WASM modules from exhausting host resources.
/// </summary>
public sealed class WasmResourceLimits
{
    /// <summary>Maximum memory size in bytes (default 512 MiB).</summary>
    public ulong MaxMemoryBytes { get; set; } = 512 * 1024 * 1024;

    /// <summary>Maximum number of WASM instances (default 16 per document).</summary>
    public int MaxInstances { get; set; } = 16;

    /// <summary>Maximum fuel (instructions) per instantiation (default 100M).</summary>
    public ulong MaxFuelPerInstance { get; set; } = 100_000_000;

    /// <summary>Maximum execution wall-clock time per call (default 10s).</summary>
    public TimeSpan MaxExecutionTime { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum tables count.</summary>
    public uint MaxTables { get; set; } = 100;

    /// <summary>Maximum memories count.</summary>
    public uint MaxMemories { get; set; } = 10;

    /// <summary>Maximum table elements.</summary>
    public uint MaxTableElements { get; set; } = 10_000_000;

    /// <summary>Validates and normalizes the limits.</summary>
    public void Normalize()
    {
        if (MaxMemoryBytes < 16 * 1024 * 1024) MaxMemoryBytes = 16 * 1024 * 1024;
        if (MaxInstances < 1) MaxInstances = 1;
        if (MaxInstances > 256) MaxInstances = 256;
        if (MaxFuelPerInstance == 0) MaxFuelPerInstance = 100_000_000;
        if (MaxExecutionTime <= TimeSpan.Zero) MaxExecutionTime = TimeSpan.FromSeconds(10);
    }
}

/// <summary>
/// Result of a WASM execution.
/// </summary>
public sealed class WasmExecutionResult
{
    public bool Success { get; init; }
    public object? Result { get; init; }
    public string? Error { get; init; }
    public ulong FuelConsumed { get; init; }
    public TimeSpan Duration { get; init; }

    public static WasmExecutionResult Ok(object? result, ulong fuel, TimeSpan duration) =>
        new() { Success = true, Result = result, FuelConsumed = fuel, Duration = duration };

    public static WasmExecutionResult Fail(string error, ulong fuel, TimeSpan duration) =>
        new() { Success = false, Error = error, FuelConsumed = fuel, Duration = duration };
}