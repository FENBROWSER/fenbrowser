using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Host.Platform;

namespace FenBrowser.Host.Platform.Linux;

/// <summary>
/// Linux clipboard implementation using xclip/xsel.
/// </summary>
internal sealed class LinuxClipboard : IClipboard
{
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(2);
    private const int MaxClipboardChars = 16 * 1024 * 1024;

    public string GetText()
    {
        return TryReadClipboard("xclip", "-selection clipboard -o")
            ?? TryReadClipboard("xsel", "-b")
            ?? string.Empty;
    }

    public bool SetText(string text)
    {
        // An empty string is a valid clipboard value and is how callers clear text.
        text ??= string.Empty;

        return TryWriteClipboard("xclip", "-selection clipboard", text)
            || TryWriteClipboard("xsel", "-b -i", text);
    }

    public bool HasText
    {
        get
        {
            var text = GetText();
            return !string.IsNullOrEmpty(text);
        }
    }

    private static string TryReadClipboard(string fileName, string arguments)
    {
        try
        {
            return ReadClipboardAsync(fileName, arguments).GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> ReadClipboardAsync(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (process == null)
            return null;

        using var timeout = new CancellationTokenSource(HelperTimeout);
        var builder = new StringBuilder();
        var buffer = new char[4096];

        try
        {
            while (true)
            {
                int read = await process.StandardOutput
                    .ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                if (builder.Length > MaxClipboardChars - read)
                {
                    KillProcess(process);
                    return null;
                }

                builder.Append(buffer, 0, read);
            }

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? builder.ToString() : null;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            KillProcess(process);
            return null;
        }
    }

    private static bool TryWriteClipboard(string fileName, string arguments, string text)
    {
        try
        {
            return WriteClipboardAsync(fileName, arguments, text).GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WriteClipboardAsync(string fileName, string arguments, string text)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (process == null)
            return false;

        using var timeout = new CancellationTokenSource(HelperTimeout);
        try
        {
            await process.StandardInput.WriteAsync(text.AsMemory(), timeout.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            KillProcess(process);
            return false;
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (process != null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
