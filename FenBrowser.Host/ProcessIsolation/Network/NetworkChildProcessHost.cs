using System;
using System.Diagnostics;
using System.Security.Cryptography;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Platform;
using FenBrowser.Core.Security.Sandbox;

namespace FenBrowser.Host.ProcessIsolation.Network
{
    /// <summary>
    /// Broker-side launcher for the dedicated Network target process.
    /// Enforces sandbox and ready-handshake contracts before exposing the session.
    /// </summary>
    public sealed class NetworkChildProcessHost : IDisposable
    {
        private readonly int _parentPid = Environment.ProcessId;
        private readonly TimeSpan _readyTimeout;
        private NetworkProcessSession _session;
        private ISandbox _sandbox;
        private Process _childProcess;

        public NetworkChildProcessHost()
        {
            _readyTimeout = TimeSpan.FromMilliseconds(ParseIntEnv("FEN_NETWORK_READY_TIMEOUT_MS", 5000));
        }

        public NetworkProcessSession Session => _session;

        public bool TryStart()
        {
            if (_session != null)
            {
                return true;
            }

            var allowUnsandboxedFallback = string.Equals(
                Environment.GetEnvironmentVariable("FEN_NETWORK_ALLOW_UNSANDBOXED"),
                "1",
                StringComparison.OrdinalIgnoreCase);
            using var launchScope = EngineLogBridge.BeginScope(
                component: "NetworkProcessLauncher",
                data: new System.Collections.Generic.Dictionary<string, object>
                {
                    ["allowUnsandboxedFallback"] = allowUnsandboxedFallback
                });

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                exePath = Process.GetCurrentProcess().MainModule?.FileName;
            }

            if (string.IsNullOrWhiteSpace(exePath))
            {
                EngineLogBridge.Error("[NetworkProcess] Could not resolve host executable path for network child launch.", LogCategory.General);
                return false;
            }

            var pipeName = $"fen_network_{_parentPid}_{Guid.NewGuid():N}";
            var authToken = CreateAuthToken();
            var session = new NetworkProcessSession(pipeName, authToken);

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--network-child",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                startInfo.Environment["FEN_NETWORK_CHILD"] = "1";
                startInfo.Environment["FEN_NETWORK_PARENT_PID"] = _parentPid.ToString();
                startInfo.Environment["FEN_NETWORK_PIPE_NAME"] = pipeName;
                startInfo.Environment["FEN_NETWORK_AUTH_TOKEN"] = authToken;
                startInfo.Environment["FEN_NETWORK_SANDBOX_PROFILE"] = "network_process";
                startInfo.Environment["FEN_NETWORK_CAPABILITIES"] = "network,cookies,cache,dns";

                var sandboxFactory = PlatformLayerFactory.GetInstance().CreateSandboxFactory();
                if (!SandboxLaunchPolicy.TryAcquire(
                    "network child",
                    sandboxFactory,
                    OsSandboxProfile.NetworkProcess,
                    allowUnsandboxedFallback,
                    "FEN_NETWORK_ALLOW_UNSANDBOXED",
                    out var sandbox))
                {
                    session.Dispose();
                    return false;
                }
                
                sandbox?.ApplyToProcessStartInfo(startInfo);

                Process child;
                if (sandbox.RequiresCustomSpawn)
                {
                    child = sandbox.SpawnProcess(startInfo);
                }
                else
                {
                    child = Process.Start(startInfo);
                    if (child != null)
                    {
                        sandbox.AttachToProcess(child);
                    }
                }

                if (child == null)
                {
                    sandbox?.Dispose();
                    session.Dispose();
                    EngineLogBridge.Error("[NetworkProcess] Failed to spawn network child process.", LogCategory.ProcessIsolation);
                    return false;
                }

                session.Start(child);
                var ready = session.WaitForReadyAsync(_readyTimeout).GetAwaiter().GetResult();
                if (!ready)
                {
                    EngineLogBridge.Error(
                        $"[NetworkProcess] Network child failed startup contract (pid={child.Id}, readyTimeoutMs={(int)_readyTimeout.TotalMilliseconds}).",
                        LogCategory.ProcessIsolation);
                    try { child.Kill(entireProcessTree: true); } catch { }
                    sandbox?.Dispose();
                    session.Dispose();
                    return false;
                }

                _childProcess = child;
                _sandbox = sandbox;
                _session = session;

                EngineLogBridge.Info(
                    $"[NetworkProcess] Network child started (pid={child.Id}, pipe={pipeName}, sandbox={sandbox?.ProfileName ?? "unsandboxed"}).",
                    LogCategory.ProcessIsolation);
                return true;
            }
            catch (Exception ex)
            {
                session.Dispose();
                EngineLogBridge.Error($"[NetworkProcess] Failed to start network child: {ex.Message}", LogCategory.ProcessIsolation);
                return false;
            }
        }

        public void Dispose()
        {
            try { _session?.Dispose(); } catch { }
            _session = null;

            try
            {
                if (_childProcess != null && !_childProcess.HasExited)
                {
                    _childProcess.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            try { _childProcess?.Dispose(); } catch { }
            _childProcess = null;

            try { _sandbox?.Dispose(); } catch { }
            _sandbox = null;
        }

        private static int ParseIntEnv(string key, int fallback)
        {
            var raw = Environment.GetEnvironmentVariable(key);
            return int.TryParse(raw, out var parsed) ? parsed : fallback;
        }

        private static string CreateAuthToken()
        {
            Span<byte> bytes = stackalloc byte[32];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}

