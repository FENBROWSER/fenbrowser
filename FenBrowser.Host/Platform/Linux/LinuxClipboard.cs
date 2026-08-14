using System;
using System.Diagnostics;
using FenBrowser.Host.Platform;

namespace FenBrowser.Host.Platform.Linux;

/// <summary>
/// Linux clipboard implementation using xclip/xsel.
/// </summary>
internal sealed class LinuxClipboard : IClipboard
{
    public string GetText()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "xclip",
                Arguments = "-selection clipboard -o",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode == 0)
                {
                    return output;
                }
            }
        }
        catch { }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "xsel",
                Arguments = "-b",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode == 0)
                {
                    return output;
                }
            }
        }
        catch { }

        return string.Empty;
    }

    public bool SetText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "xclip",
                Arguments = "-selection clipboard",
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process != null)
            {
                process.StandardInput.Write(text);
                process.StandardInput.Close();
                process.WaitForExit();
                return process.ExitCode == 0;
            }
        }
        catch { }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "xsel",
                Arguments = "-b -i",
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process != null)
            {
                process.StandardInput.Write(text);
                process.StandardInput.Close();
                process.WaitForExit();
                return process.ExitCode == 0;
            }
        }
        catch { }

        return false;
    }

    public bool HasText
    {
        get
        {
            var text = GetText();
            return !string.IsNullOrEmpty(text);
        }
    }
}
