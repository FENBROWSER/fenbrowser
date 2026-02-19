using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Host.Tabs;

namespace FenBrowser.Host.ProcessIsolation
{
    /// <summary>
    /// Brokered process model: host keeps UI/process brokering and spawns a per-tab renderer child process.
    /// Child process wiring is intentionally minimal but establishes a concrete OS process boundary.
    /// </summary>
    public sealed class BrokeredProcessIsolationCoordinator : IProcessIsolationCoordinator
    {
        private readonly ConcurrentDictionary<int, RendererChildSession> _sessions = new();
        private readonly int _parentPid = Environment.ProcessId;

        public string Mode => "brokered";
        public bool UsesOutOfProcessRenderer => true;

        public void Initialize()
        {
            FenLogger.Info("[ProcessIsolation] Mode=brokered (per-tab renderer child process enabled)", LogCategory.General);
        }

        public void OnTabCreated(BrowserTab tab)
        {
            if (tab == null)
                return;

            var pipeName = $"fen_renderer_{_parentPid}_{tab.Id}_{Guid.NewGuid():N}";
            var token = CreateAuthToken();
            var session = new RendererChildSession(tab.Id, pipeName, token);
            var process = StartRendererChild(tab.Id, pipeName, token);
            if (process == null)
            {
                session.Dispose();
                return;
            }

            session.AttachProcess(process);
            _sessions[tab.Id] = session;
            FenLogger.Info($"[ProcessIsolation] Renderer child started for tab {tab.Id} (pid={process.Id}, pipe={pipeName})", LogCategory.General);
        }

        public void OnTabActivated(BrowserTab tab)
        {
            if (tab == null)
                return;

            if (_sessions.TryGetValue(tab.Id, out var session))
            {
                var process = session.ChildProcess;
                if (process != null && !process.HasExited)
                {
                    FenLogger.Debug($"[ProcessIsolation] Activated tab {tab.Id} backed by renderer pid={process.Id}", LogCategory.General);
                }
                session.SendTabActivated();
            }
        }

        public void OnNavigationRequested(BrowserTab tab, string url, bool isUserInput)
        {
            if (tab == null || string.IsNullOrWhiteSpace(url))
                return;

            if (_sessions.TryGetValue(tab.Id, out var session))
            {
                session.SendNavigate(url, isUserInput);
            }
        }

        public void OnInputEvent(BrowserTab tab, RendererInputEvent inputEvent)
        {
            if (tab == null || inputEvent == null)
                return;

            if (_sessions.TryGetValue(tab.Id, out var session))
            {
                session.SendInput(inputEvent);
            }
        }

        public void OnFrameRequested(BrowserTab tab, float viewportWidth, float viewportHeight)
        {
            if (tab == null)
                return;

            if (_sessions.TryGetValue(tab.Id, out var session))
            {
                session.SendFrameRequest(viewportWidth, viewportHeight);
            }
        }

        public void OnTabClosed(BrowserTab tab)
        {
            if (tab == null)
                return;

            if (_sessions.TryRemove(tab.Id, out var session))
            {
                session.SendTabClosed();
                session.SendShutdown();
                StopSession(session, $"tab {tab.Id} closed");
            }
        }

        public void Shutdown()
        {
            foreach (var kvp in _sessions)
            {
                kvp.Value.SendShutdown();
                StopSession(kvp.Value, "host shutdown");
            }

            _sessions.Clear();
        }

        private Process StartRendererChild(int tabId, string pipeName, string authToken)
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    exePath = Process.GetCurrentProcess().MainModule?.FileName;
                }

                if (string.IsNullOrWhiteSpace(exePath))
                {
                    FenLogger.Warn("[ProcessIsolation] Could not resolve host executable path for brokered child launch.", LogCategory.General);
                    return null;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"--renderer-child --tab-id={tabId}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                startInfo.Environment["FEN_RENDERER_CHILD"] = "1";
                startInfo.Environment["FEN_RENDERER_TAB_ID"] = tabId.ToString();
                startInfo.Environment["FEN_RENDERER_PARENT_PID"] = _parentPid.ToString();
                startInfo.Environment["FEN_RENDERER_PIPE_NAME"] = pipeName;
                startInfo.Environment["FEN_RENDERER_AUTH_TOKEN"] = authToken;

                return Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[ProcessIsolation] Failed to start renderer child for tab {tabId}: {ex.Message}", LogCategory.General);
                return null;
            }
        }

        private static void StopSession(RendererChildSession session, string reason)
        {
            if (session == null)
                return;

            var process = session.ChildProcess;
            if (process == null)
            {
                session.Dispose();
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1000);
                }
            }
            catch (Exception ex)
            {
                FenLogger.Warn($"[ProcessIsolation] Failed to stop renderer child ({reason}): {ex.Message}", LogCategory.General);
            }
            finally
            {
                try { process.Dispose(); } catch { }
                session.Dispose();
            }
        }

        private static string CreateAuthToken()
        {
            Span<byte> bytes = stackalloc byte[32];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
