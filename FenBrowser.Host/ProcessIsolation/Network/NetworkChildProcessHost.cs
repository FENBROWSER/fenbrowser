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
        private const int MinReadyTimeoutMs = 100;
        private const int MaxReadyTimeoutMs = 60_000;

        private readonly int _parentPid = Environment.ProcessId;
        private readonly TimeSpan _readyTimeout;
        private NetworkProcessSession _session;
        private ISandbox _sandbox;
        private Process _childProcess;

        public NetworkChildProcessHost()
        {
            var timeoutMs = Math.Clamp(
                ParseIntEnv("FEN_NETWORK_READY_TIMEOUT_MS", 5000),
                MinReadyTimeoutMs,
                MaxReadyTimeoutMs);
            _readyTimeout = TimeSpan.FromMilliseconds(timeoutMs);
        }

        public NetworkProcessSession Session => _session;

        public bool TryStart()
        {
            if (_session != null)
            {
                return true;
            }

            var allowUnsandboxedFallback = ProcessIsolationEnvPolicy.IsUnsandboxedFallbackEnabled("FEN_NETWORK_ALLOW_UNSANDBOXED");
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
            Process spawnedChild = null;
            ISandbox acquiredSandbox = null;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                startInfo.ArgumentList.Add("--network-child");

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
                    out acquiredSandbox))
                {
                    session.Dispose();
                    return false;
                }

                acquiredSandbox?.ApplyToProcessStartInfo(startInfo);

                if (acquiredSandbox != null && acquiredSandbox.RequiresCustomSpawn)
                {
                    try
                    {
                        spawnedChild = acquiredSandbox.SpawnProcess(startInfo);
                    }
                    catch (Exception ex)
                    {
                        EngineLogBridge.Warn(
                            $"[NetworkProcess] Sandbox.SpawnProcess failed: {ex.Message}" +
                            (allowUnsandboxedFallback ? " (retrying with job-only fallback)" : string.Empty),
                            LogCategory.ProcessIsolation);

                        if (!allowUnsandboxedFallback)
                        {
                            throw;
                        }

                        spawnedChild = Process.Start(startInfo);
                        if (spawnedChild != null)
                        {
                            try
                            {
                                acquiredSandbox.AttachToProcess(spawnedChild);
                            }
                            catch (Exception attachEx)
                            {
                                EngineLogBridge.Warn(
                                    $"[NetworkProcess] Job-only sandbox fallback attach failed for pid={spawnedChild.Id}: {attachEx.Message}",
                                    LogCategory.ProcessIsolation);
                            }
                        }
                    }
                }
                else
                {
                    spawnedChild = Process.Start(startInfo);
                    if (spawnedChild != null && acquiredSandbox != null)
                    {
                        acquiredSandbox.AttachToProcess(spawnedChild);
                    }
                }

                if (spawnedChild == null)
                {
                    TryDispose(acquiredSandbox, "startup-sandbox");
                    acquiredSandbox = null;
                    session.Dispose();
                    EngineLogBridge.Error("[NetworkProcess] Failed to spawn network child process.", LogCategory.ProcessIsolation);
                    return false;
                }

                session.Start(spawnedChild);
                var ready = session.WaitForReadyAsync(_readyTimeout).GetAwaiter().GetResult();
                if (!ready)
                {
                    EngineLogBridge.Error(
                        $"[NetworkProcess] Network child failed startup contract (pid={spawnedChild.Id}, readyTimeoutMs={(int)_readyTimeout.TotalMilliseconds}).",
                        LogCategory.ProcessIsolation);
                    TryKillProcess(spawnedChild, "startup-contract-failed");
                    TryDispose(spawnedChild, "startup-child-process");
                    spawnedChild = null;
                    TryDispose(acquiredSandbox, "startup-sandbox");
                    acquiredSandbox = null;
                    session.Dispose();
                    return false;
                }

                _childProcess = spawnedChild;
                _sandbox = acquiredSandbox;
                _session = session;

                EngineLogBridge.Info(
                    $"[NetworkProcess] Network child started (pid={spawnedChild.Id}, pipe={pipeName}, sandbox={acquiredSandbox?.ProfileName ?? "unsandboxed"}).",
                    LogCategory.ProcessIsolation);
                return true;
            }
            catch (Exception ex)
            {
                TryKillProcess(spawnedChild, "startup-exception");
                TryDispose(spawnedChild, "startup-child-process");
                TryDispose(acquiredSandbox, "startup-sandbox");
                session.Dispose();
                EngineLogBridge.Error($"[NetworkProcess] Failed to start network child: {ex.Message}", LogCategory.ProcessIsolation);
                return false;
            }
        }

        public void Dispose()
        {
            TryDispose(_session, "session");
            _session = null;

            TryKillProcess(_childProcess, "host-dispose");

            TryDispose(_childProcess, "child-process");
            _childProcess = null;

            TryDispose(_sandbox, "sandbox");
            _sandbox = null;
        }

        private static void TryKillProcess(Process process, string reason)
        {
            if (process == null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                EngineLogBridge.Debug($"[NetworkProcess] Failed to kill child ({reason}): {ex.Message}", LogCategory.ProcessIsolation);
            }
        }

        private static void TryDispose(IDisposable disposable, string resourceName)
        {
            if (disposable == null)
            {
                return;
            }

            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                EngineLogBridge.Debug($"[NetworkProcess] Dispose failed for {resourceName}: {ex.Message}", LogCategory.ProcessIsolation);
            }
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
