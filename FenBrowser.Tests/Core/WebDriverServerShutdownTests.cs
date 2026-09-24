using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FenBrowser.WebDriver;
using FenBrowser.WebDriver.Commands;

namespace FenBrowser.Tests.Core;

/// <summary>
/// A command stuck inside the browser must not keep the WebDriver server from
/// stopping: shutdown waits a bounded grace period for running commands.
/// </summary>
public sealed class WebDriverServerShutdownTests
{
    [Fact]
    public async Task StopAsync_DoesNotWaitForeverOnAStuckCommand()
    {
        var port = ReservePort();
        await using var server = new WebDriverServer(port);
        server.SetDriver(HangingTitleDriver.Create());
        server.Start();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var content = new StringContent("{\"capabilities\":{}}", Encoding.UTF8, "application/json");
        using var created = await client.PostAsync($"http://127.0.0.1:{port}/session", content);
        var createdBody = await created.Content.ReadAsStringAsync();
        Assert.True(created.IsSuccessStatusCode, createdBody);
        using var session = JsonDocument.Parse(createdBody);
        var sessionId = session.RootElement.GetProperty("value").GetProperty("sessionId").GetString();

        // Never answered: the driver's GetTitleAsync never completes.
        var title = client.GetAsync($"http://127.0.0.1:{port}/session/{sessionId}/title");
        await Task.Delay(500);
        Assert.False(title.IsCompleted, title.IsCompleted ? await title.Result.Content.ReadAsStringAsync() : "");

        var stopwatch = Stopwatch.StartNew();
        var stop = server.StopAsync();
        var finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(30))) == stop;

        Assert.True(finished, "StopAsync did not complete");
        Assert.True(stopwatch.Elapsed < WebDriverServer.ShutdownGracePeriod + TimeSpan.FromSeconds(10));
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>A driver whose every call succeeds with a default value, except the title, which never answers.</summary>
    public class HangingTitleDriver : DispatchProxy
    {
        public static IBrowserDriver Create() => Create<IBrowserDriver, HangingTitleDriver>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            var returnType = targetMethod.ReturnType;
            if (name == "GetTitleAsync")
            {
                return new TaskCompletionSource<string>().Task;
            }

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType)
                    .Invoke(null, new[] { DefaultFor(name, resultType) });
            }

            return returnType == typeof(void) ? null : DefaultFor(name, returnType);
        }

        // Just enough of a browser for a session: one window, handle "1", no alert.
        private static object? DefaultFor(string method, Type type)
        {
            var isHandle = method.Contains("Handle", StringComparison.Ordinal);
            if (type == typeof(string)) return isHandle ? "1" : null;
            if (type == typeof(bool)) return !method.Contains("Alert", StringComparison.Ordinal);
            if (type.IsAssignableFrom(typeof(string[]))) return isHandle ? new[] { "1" } : Array.Empty<string>();
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
