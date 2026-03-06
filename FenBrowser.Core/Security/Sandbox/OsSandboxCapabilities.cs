using System;

namespace FenBrowser.Core.Security.Sandbox;

/// <summary>
/// OS-level capability flags that control what a sandboxed process is permitted to do.
/// </summary>
/// <remarks>
/// <para>
/// These capabilities are distinct from the HTML iframe <c>sandbox</c> attribute feature
/// flags found in <see cref="FenBrowser.Core.SandboxFeature"/>. Those govern what a web
/// document may do within the rendering engine; <see cref="OsSandboxCapabilities"/> govern
/// what the <em>OS process</em> hosting the engine is permitted to do at the kernel level.
/// </para>
/// <para>
/// Use bitwise OR to compose capability sets. Pre-composed sets for each named process
/// type are provided as static constants.
/// </para>
/// </remarks>
[Flags]
public enum OsSandboxCapabilities : ulong
{
    /// <summary>No OS-level capabilities. Suitable for the most restricted utility processes.</summary>
    None = 0,

    /// <summary>
    /// Process may initiate outbound TCP/UDP connections.
    /// Required by <c>NetworkProcess</c>; denied for <c>RendererMinimal</c> (renderer
    /// reaches the network only through the broker IPC pipe).
    /// </summary>
    NetworkOutbound = 1UL << 0,

    /// <summary>Process may open listening sockets (bind + accept).</summary>
    NetworkListen = 1UL << 1,

    /// <summary>Process may open and read files in user-writable directories (e.g. profile, downloads).</summary>
    FileReadUser = 1UL << 2,

    /// <summary>Process may read files in system directories (e.g. <c>C:\Windows\System32</c> on Windows).</summary>
    FileReadSystem = 1UL << 3,

    /// <summary>Process may write or create files anywhere on the file system.</summary>
    FileWrite = 1UL << 4,

    /// <summary>
    /// Process may access GPU hardware via DXGI / Direct3D / Vulkan / OpenGL.
    /// Granted only to <c>GpuProcess</c>.
    /// </summary>
    Gpu = 1UL << 5,

    /// <summary>Process may open an audio capture device (microphone).</summary>
    AudioCapture = 1UL << 6,

    /// <summary>Process may open a video capture device (camera).</summary>
    VideoCapture = 1UL << 7,

    /// <summary>
    /// Process may call <c>CreateProcess</c> (Windows) or <c>fork</c>/<c>exec</c> (POSIX)
    /// to spawn child processes.
    /// </summary>
    SpawnChildProcess = 1UL << 8,

    /// <summary>Process may read the system clipboard.</summary>
    ReadClipboard = 1UL << 9,

    /// <summary>Process may write to the system clipboard.</summary>
    WriteClipboard = 1UL << 10,

    /// <summary>
    /// Process may synthesise user input (e.g. <c>SendInput</c> on Windows, <c>XTest</c>
    /// on X11). Denied for all sandboxed process types.
    /// </summary>
    SendUserInput = 1UL << 11,

    // -------------------------------------------------------------------------
    // Pre-composed sets for named process types
    // -------------------------------------------------------------------------

    /// <summary>
    /// Renderer process capability set: no direct OS capabilities.
    /// The renderer communicates with the network and file system exclusively through
    /// the broker IPC pipe; it never touches OS primitives directly.
    /// </summary>
    RendererMinimal = None,

    /// <summary>
    /// Network process capability set: outbound TCP connections only.
    /// The network process brokers all HTTP/HTTPS traffic on behalf of renderers.
    /// </summary>
    NetworkProcess = NetworkOutbound,

    /// <summary>
    /// GPU process capability set: GPU hardware access only.
    /// The GPU process renders composited frames and returns bitmaps through shared memory.
    /// </summary>
    GpuProcess = Gpu,

    /// <summary>
    /// Broker (browser) process capability set: all capabilities enabled.
    /// The broker is the trust root of the process hierarchy and is not sandboxed.
    /// </summary>
    BrokerFull =
        NetworkOutbound | NetworkListen |
        FileReadUser | FileReadSystem | FileWrite |
        Gpu |
        AudioCapture | VideoCapture |
        SpawnChildProcess |
        ReadClipboard | WriteClipboard |
        SendUserInput
}
