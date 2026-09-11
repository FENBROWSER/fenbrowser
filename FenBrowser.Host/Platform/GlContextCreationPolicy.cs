using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using Silk.NET.GLFW;

namespace FenBrowser.Host.Platform;

/// <summary>
/// Decides how GLFW creates the GL ES context on Windows: through the vendor
/// WGL driver, or through the ANGLE EGL shipped beside the executable.
/// </summary>
/// <remarks>
/// GLFW's default on Windows is WGL, which only works where a vendor OpenGL
/// driver exists. GitHub-hosted Windows runners have none ("WGL: The driver
/// does not appear to support OpenGL"), so every headless WebDriver launch
/// there died before it could listen, and the WPT lanes ran zero tests.
/// ANGLE renders GL ES on Direct3D 11 and falls back to WARP without a GPU,
/// so it is the right default for headless runs; the interactive window keeps
/// the native driver unless FEN_GL_CONTEXT_API says otherwise.
///
/// Silk.NET's windowing layer never resets GLFW's window hints, so a hint set
/// here before <c>Window.Create</c> is what the window is created with.
/// </remarks>
internal static class GlContextCreationPolicy
{
    private const string EnvironmentVariable = "FEN_GL_CONTEXT_API";

    public static void ApplyGlfwHints(bool headless)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var requested = (Environment.GetEnvironmentVariable(EnvironmentVariable) ?? string.Empty).Trim().ToLowerInvariant();
        var useEgl = requested switch
        {
            "egl" or "angle" => true,
            "native" or "wgl" => false,
            _ => headless
        };

        if (useEgl && !TryPreloadAngle())
        {
            EngineLogBridge.Warn(
                "[GlContext] ANGLE libEGL/libGLESv2 could not be loaded; falling back to the native context API.",
                LogCategory.General);
            useEgl = false;
        }

        try
        {
            var glfw = GlfwProvider.GLFW.Value;
            glfw.WindowHint(
                WindowHintContextApi.ContextCreationApi,
                useEgl ? ContextApi.EglContextApi : ContextApi.NativeContextApi);
            EngineLogBridge.Info(
                $"[GlContext] GLFW context creation API: {(useEgl ? "EGL (ANGLE)" : "native (WGL)")} " +
                $"(headless={headless}, {EnvironmentVariable}='{requested}')",
                LogCategory.General);
        }
        catch (Exception ex)
        {
            EngineLogBridge.Warn($"[GlContext] Could not set GLFW context creation API: {ex.Message}", LogCategory.General);
        }
    }

    // The ANGLE natives ship under runtimes/win-x64/native, which the .NET host
    // resolves through deps.json but GLFW's own LoadLibraryA("libEGL.dll") does
    // not. LoadLibrary hands back a module that is already in the process by
    // name, so loading both through NativeLibrary first is all GLFW needs.
    //
    // The copy beside the executable is tried first, by full path: name-based
    // probing follows deps.json to the NuGet package's runtimes/win-x64 copy,
    // and every published Silk.NET.OpenGLES.ANGLE.Native ships 32-bit DLLs
    // there, so that probe fails with a bad image format on x64. A deployment
    // that drops a real x64 ANGLE next to the binaries gets it used.
    private static bool TryPreloadAngle()
    {
        var probe = Assembly.GetEntryAssembly() ?? typeof(GlContextCreationPolicy).Assembly;
        foreach (var library in new[] { "libGLESv2", "libEGL" })
        {
            try
            {
                var beside = Path.Combine(AppContext.BaseDirectory, library + ".dll");
                if (File.Exists(beside))
                {
                    try
                    {
                        NativeLibrary.Load(beside);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        EngineLogBridge.Warn($"[GlContext] {beside} is present but did not load: {ex.Message}", LogCategory.General);
                    }
                }

                if (!NativeLibrary.TryLoad(library, probe, null, out _))
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                EngineLogBridge.Warn($"[GlContext] Preloading {library} failed: {ex.Message}", LogCategory.General);
                return false;
            }
        }

        return true;
    }
}
