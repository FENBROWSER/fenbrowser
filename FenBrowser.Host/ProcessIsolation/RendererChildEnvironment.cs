using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FenBrowser.Host.ProcessIsolation;

internal static class RendererChildEnvironment
{
    private static readonly string[] SafeInheritedKeys =
    {
        "SystemRoot",
        "WINDIR",
        "DOTNET_ROOT",
        "DOTNET_ROOT(x86)",
        // Diagnostics-only toggles; the renderer child writes probes/dumps to the
        // workspace logs dir, so it needs the same envelope as the host process.
        "FEN_DIAGNOSTIC_APPENDS",
        "FEN_DIAGNOSTICS_DIR",
        // The run this browser belongs to. A child that does not inherit it invents
        // its own, and the log files stop being groupable into one run - which is
        // the entire reason the id exists. It carries no capability: it is an opaque
        // eight-character tag used only to label log entries.
        "FEN_RUN_ID",

        // Diagnostic and tuning knobs. Page script, layout and paint all run in
        // the renderer child, so a knob that stops at the host does nothing at
        // all where it is meant to act: FEN_FENJS_PROFILE was set for a whole
        // investigation and profiled an idle process, reporting nothing and
        // reading as "the slow code never ran". These stay an explicit list
        // rather than a FEN_* wildcard so the child keeps the clean-slate
        // environment the sandbox depends on - a new knob belongs here, and the
        // per-child wiring variables (FEN_RENDERER_*, FEN_NETWORK_*,
        // FEN_TARGET_*) must never appear.
        "FEN_LOG_PRESET",
        "FEN_FENJS_PROFILE",
        "FEN_FENJS_PIN_AUDIT",
        "FEN_FENJS_MICROTASK_TRACE",
        "FEN_FENJS_GC_SWEEPLOG",
        "FEN_FENJS_GC_STRESS",
        "FEN_FENJS_GC_ROOT_AUDIT",
        "FEN_FENJS_GC_AUDIT_REMEMBERED",
        "FEN_FENJS_TASK_INSTRUCTION_BUDGET",
        "FEN_FENJS_SCRIPT_TIMEOUT_MS",
        "FEN_FENJS_INPUT_EVENT_TIMEOUT_MS",
        "FEN_JIT_DISABLE",
        "FEN_JIT_NOOSR",
        "FEN_JIT_TRACE",
        "FEN_LAYOUT_DEBUG_LOG",
        "FEN_LAYOUT_DEADLINE_MS",
        "FEN_GRID_TRACE",
        "FEN_NAV_GLOBALS_SNAPSHOT",
        "FEN_COMPAT_INTERVENTIONS",
        // Automation posture has to match the host's or the page sees a
        // different browser in the frame that actually renders it.
        "FEN_AUTOMATION_MODE",
        "FEN_WEBDRIVER",
        "FEN_WEBDRIVER_FRAME_TRACE"
    };

    private static readonly string[] UnixGraphicsEnvironmentKeys =
    {
        "DISPLAY",
        "WAYLAND_DISPLAY",
        "XDG_RUNTIME_DIR",
        "XAUTHORITY"
    };

    public static void ResetToSafeBase(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        var safeValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in SafeInheritedKeys)
        {
            Preserve(startInfo, safeValues, key);
        }

        if (!OperatingSystem.IsWindows())
        {
            foreach (var key in UnixGraphicsEnvironmentKeys)
            {
                Preserve(startInfo, safeValues, key);
            }
        }

        startInfo.Environment.Clear();
        foreach (var pair in safeValues)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
    }

    private static void Preserve(
        ProcessStartInfo startInfo,
        Dictionary<string, string> safeValues,
        string key)
    {
        if (startInfo.Environment.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            safeValues[key] = value;
        }
    }
}
