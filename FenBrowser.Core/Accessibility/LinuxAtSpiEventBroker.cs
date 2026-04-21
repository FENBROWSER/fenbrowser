using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Accessibility
{
    internal sealed class LinuxAtSpiEventBroker : IDisposable
    {
        private const int MaxQueuedEvents = 256;
        private const string ObjectPath = "/org/fenbrowser/Accessibility";

        private readonly string _busAddress;
        private readonly ConcurrentQueue<AtSpiSignal> _queue = new ConcurrentQueue<AtSpiSignal>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _worker;
        private int _queuedCount;
        private bool _disposed;

        private LinuxAtSpiEventBroker(string busAddress)
        {
            _busAddress = busAddress;
            _worker = Task.Run(ProcessLoopAsync);
        }

        public static bool TryCreate(out LinuxAtSpiEventBroker broker, out string failureReason)
        {
            broker = null;
            failureReason = null;

            var busAddress = Environment.GetEnvironmentVariable("AT_SPI_BUS_ADDRESS");
            if (string.IsNullOrWhiteSpace(busAddress))
            {
                failureReason = "AT_SPI_BUS_ADDRESS is unset.";
                return false;
            }

            broker = new LinuxAtSpiEventBroker(busAddress);
            return true;
        }

        public bool TryPost(AtSpiSignal signal, out string failureReason)
        {
            failureReason = null;
            if (_disposed)
            {
                failureReason = "broker disposed";
                return false;
            }

            if (signal == null)
            {
                failureReason = "signal missing";
                return false;
            }

            var queued = Interlocked.Increment(ref _queuedCount);
            if (queued > MaxQueuedEvents)
            {
                Interlocked.Decrement(ref _queuedCount);
                failureReason = "queue full";
                return false;
            }

            _queue.Enqueue(signal);
            _signal.Release();
            return true;
        }

        private async Task ProcessLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!_queue.TryDequeue(out var signal))
                {
                    continue;
                }

                Interlocked.Decrement(ref _queuedCount);
                try
                {
                    Emit(signal);
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Warn(
                        $"[AT-SPI] Failed to emit {signal.InterfaceName}.{signal.Member}: {ex.Message}",
                        LogCategory.Accessibility);
                }
            }
        }

        private void Emit(AtSpiSignal signal)
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "dbus-send",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            process.StartInfo.Environment["DBUS_SESSION_BUS_ADDRESS"] = _busAddress;
            process.StartInfo.ArgumentList.Add("--type=signal");
            process.StartInfo.ArgumentList.Add(ObjectPath);
            process.StartInfo.ArgumentList.Add($"{signal.InterfaceName}.{signal.Member}");
            process.StartInfo.ArgumentList.Add($"string:{signal.Detail1 ?? string.Empty}");
            process.StartInfo.ArgumentList.Add($"int32:{signal.Detail2}");
            process.StartInfo.ArgumentList.Add($"int32:{signal.Detail3}");
            process.StartInfo.ArgumentList.Add($"variant:string:{signal.Payload ?? string.Empty}");
            process.StartInfo.ArgumentList.Add("array:dict:string:variant:");

            if (!process.Start())
            {
                throw new InvalidOperationException("dbus-send failed to start.");
            }

            process.WaitForExit(1000);
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("dbus-send timed out.");
            }

            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd();
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"dbus-send exited with code {process.ExitCode}." : error.Trim());
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts.Cancel();
            _signal.Release();
            try { _worker.Wait(TimeSpan.FromSeconds(1)); } catch { }
            _signal.Dispose();
            _cts.Dispose();
        }
    }

    internal sealed class AtSpiSignal
    {
        public string InterfaceName { get; init; }

        public string Member { get; init; }

        public string Detail1 { get; init; }

        public int Detail2 { get; init; }

        public int Detail3 { get; init; }

        public string Payload { get; init; }
    }
}
