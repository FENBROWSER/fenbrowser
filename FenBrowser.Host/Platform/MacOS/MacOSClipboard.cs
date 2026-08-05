using System;
using System.Diagnostics;
using FenBrowser.Host.Platform;

namespace FenBrowser.Host.Platform.MacOS;

/// <summary>
/// macOS clipboard implementation using pbpaste/pbcopy command-line tools.
/// </summary>
internal sealed class MacOSClipboard : IClipboard
{
    public string GetText()
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "pbpaste",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return output.Trim();
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
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "pbcopy",
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