using System;
using System.Diagnostics;
using System.Security.Cryptography;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Platform;
using FenBrowser.Core.Security.Sandbox;
using FenBrowser.Host.ProcessIsolation.Gpu;
using FenBrowser.Host.ProcessIsolation.Utility;

namespace FenBrowser.Host.ProcessIsolation.Targets
{
    public sealed class TargetChildProcessHost : IDisposable
    {
        private const int MinReadyTimeoutMs = 100;
        private const int MaxReadyTimeoutMs = 60_000;

        private readonly int _parentPid = Environment.ProcessId;
        private readonly TargetProcessKind _targetKind;
        private readonly TargetProcessContract _contract;
        private readonly OsSandboxProfile _sandboxProfile;
        private readonly TimeSpan _readyTimeout;
        private TargetProcessSession _session;
        private ISandbox _sandbox;
        private Process _childProcess;

        public TargetChildProcessHost(TargetProcessKind targetKind)
        {
            _targetKind = targetKind;
            _contract = targetKind switch
            {
                TargetProcessKind.Gpu => GpuProcessIpc.Contract,
                _ => UtilityProcessIpc.Contract
            };
            _sandboxProfile = targetKind == TargetProcessKind.Gpu
                ? OsSandboxProfile.GpuProcess
                : OsSandboxProfile.UtilityProcess;
            var readyTimeoutMs = Math.Clamp(
                ParseIntEnv(_contract.ReadyTimeoutEnvKey, 5000),
                MinReadyTimeoutMs,
                MaxReadyTimeoutMs);
            _readyTimeout = TimeSpan.FromMilliseconds(readyTimeoutMs);
        }

        public TargetProcessSession Session => _session;

        public bool TryStart()
        {
            if (_session != null)
            {
                return true;
            }

            var allowUnsandboxedFallback = ProcessIsolationEnvPolicy.IsUnsandboxedFallbackEnabled(_contract.AllowUnsandboxedEnvKey);
            using var launchScope = EngineLogBridge.BeginScope(
                component: $"{_targetKind}ProcessLauncher",
                data: new System.Collections.Generic.Dictionary<string, object>
                {
                    ["targetKind"] = _targetKind.ToString(),
                    ["allowUnsandboxedFallback"] = allowUnsandboxedFallback
                });

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                exePath = Process.GetCurrentProcess().MainModule?.FileName;
            }

            if (string.IsNullOrWhiteSpace(exePath))
            {
                EngineLogBridge.Error($"[{_targetKind}Process] Could not resolve host executable path for child launch.", LogCategory.General);
                return false;
            }

            var pipeName = IpcPaths.PipeName($"fen_{_targetKind.ToString().ToLowerInvariant()}_{_parentPid}_{Guid.NewGuid():N}");
            var authToken = CreateAuthToken();
            var session = new TargetProcessSession(_targetKind, pipeName, authToken);
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
                startInfo.ArgumentList.Add(_contract.LaunchArgument);

                startInfo.Environment["FEN_TARGET_PARENT_PID"] = _parentPid.ToString();
                startInfo.Environment["FEN_TARGET_PIPE_NAME"] = pipeName;
                startInfo.Environment["FEN_TARGET_AUTH_TOKEN"] = authToken;
                startInfo.Environment["FEN_TARGET_KIND"] = _targetKind.ToString().ToLowerInvariant();
                startInfo.Environment["FEN_TARGET_SANDBOX_PROFILE"] = _contract.ProfileName;
                startInfo.Environment["FEN_TARGET_CAPABILITIES"] = _contract.CapabilitySet;
                startInfo.Environment[_contract.ChildEnvironmentFlag] = "1";

                var sandboxFactory = PlatformLayerFactory.GetInstance().CreateSandboxFactory();
                if (!SandboxLaunchPolicy.TryAcquire(
                    $"{_targetKind} child",
                    sandboxFactory,
                    _sandboxProfile,
                    allowUnsandboxedFallback,
                    _contract.AllowUnsandboxedEnvKey,
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
                            $"[{_targetKind}Process] Sandbox.SpawnProcess failed: {ex.Message}" +
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
                                    $"[{_targetKind}Process] Job-only sandbox fallback attach failed for pid={spawnedChild.Id}: {attachEx.Message}",
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
                    EngineLogBridge.Error($"[{_targetKind}Process] Failed to spawn child process.", LogCategory.ProcessIsolation);
                    return false;
                }

                session.Start(spawnedChild);
                var ready = session.WaitForReadyAsync(_readyTimeout).GetAwaiter().GetResult();
                if (!ready)
                {
                    EngineLogBridge.Error(
                        $"[{_targetKind}Process] Child failed startup contract (pid={spawnedChild.Id}, readyTimeoutMs={(int)_readyTimeout.TotalMilliseconds}).",
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
                    $"[{_targetKind}Process] Child started (pid={spawnedChild.Id}, pipe={pipeName}, sandbox={acquiredSandbox?.ProfileName ?? "unsandboxed"}).",
                    LogCategory.ProcessIsolation);
                return true;
            }
            catch (Exception ex)
            {
                TryKillProcess(spawnedChild, "startup-exception");
                TryDispose(spawnedChild, "startup-child-process");
                TryDispose(acquiredSandbox, "startup-sandbox");
                session.Dispose();
                EngineLogBridge.Error($"[{_targetKind}Process] Failed to start child: {ex.Message}", LogCategory.ProcessIsolation);
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

        private void TryKillProcess(Process process, string reason)
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
                EngineLogBridge.Debug($"[{_targetKind}Process] Failed to kill child ({reason}): {ex.Message}", LogCategory.ProcessIsolation);
            }
        }

        private void TryDispose(IDisposable disposable, string resourceName)
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
                EngineLogBridge.Debug($"[{_targetKind}Process] Dispose failed for {resourceName}: {ex.Message}", LogCategory.ProcessIsolation);
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
