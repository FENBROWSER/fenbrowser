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
            _readyTimeout = TimeSpan.FromMilliseconds(ParseIntEnv(_contract.ReadyTimeoutEnvKey, 5000));
        }

        public TargetProcessSession Session => _session;

        public bool TryStart()
        {
            if (_session != null)
            {
                return true;
            }

            var allowUnsandboxedFallback = string.Equals(
                Environment.GetEnvironmentVariable(_contract.AllowUnsandboxedEnvKey),
                "1",
                StringComparison.OrdinalIgnoreCase);
            using var launchScope = FenLogger.BeginScope(
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
                FenLogger.Error($"[{_targetKind}Process] Could not resolve host executable path for child launch.", LogCategory.General);
                return false;
            }

            var pipeName = $"fen_{_targetKind.ToString().ToLowerInvariant()}_{_parentPid}_{Guid.NewGuid():N}";
            var authToken = CreateAuthToken();
            var session = new TargetProcessSession(_targetKind, pipeName, authToken);

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = _contract.LaunchArgument,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

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
                    FenLogger.Error($"[{_targetKind}Process] Failed to spawn child process.", LogCategory.ProcessIsolation);
                    return false;
                }

                session.Start(child);
                var ready = session.WaitForReadyAsync(_readyTimeout).GetAwaiter().GetResult();
                if (!ready)
                {
                    FenLogger.Error(
                        $"[{_targetKind}Process] Child failed startup contract (pid={child.Id}, readyTimeoutMs={(int)_readyTimeout.TotalMilliseconds}).",
                        LogCategory.ProcessIsolation);
                    try { child.Kill(entireProcessTree: true); } catch { }
                    sandbox?.Dispose();
                    session.Dispose();
                    return false;
                }

                _childProcess = child;
                _sandbox = sandbox;
                _session = session;

                FenLogger.Info(
                    $"[{_targetKind}Process] Child started (pid={child.Id}, pipe={pipeName}, sandbox={sandbox?.ProfileName ?? "unsandboxed"}).",
                    LogCategory.ProcessIsolation);
                return true;
            }
            catch (Exception ex)
            {
                session.Dispose();
                FenLogger.Error($"[{_targetKind}Process] Failed to start child: {ex.Message}", LogCategory.ProcessIsolation);
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
