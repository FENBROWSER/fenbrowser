using System;
using System.Runtime.InteropServices;
using System.Threading;
using FenBrowser.Host.Platform;

namespace FenBrowser.Host.Platform.Windows;

/// <summary>
/// Windows implementation of IClipboard using Win32 APIs.
/// </summary>
internal sealed class WindowsClipboard : IClipboard
{
    #region Win32 Native Methods

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    #endregion

    /// <inheritdoc />
    public string GetText()
    {
        string result = string.Empty;

        if (TryOpenClipboardWithRetry(() => OpenClipboard(IntPtr.Zero)))
        {
            try
            {
                IntPtr hData = GetClipboardData(CF_UNICODETEXT);
                if (hData != IntPtr.Zero)
                {
                    IntPtr pData = GlobalLock(hData);
                    if (pData != IntPtr.Zero)
                    {
                        try
                        {
                            return Marshal.PtrToStringUni(pData) ?? string.Empty;
                        }
                        finally
                        {
                            GlobalUnlock(hData);
                        }
                    }
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        return string.Empty;
    }

    /// <inheritdoc />
    public bool SetText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        if (!TryOpenClipboardWithRetry(() => OpenClipboard(IntPtr.Zero)))
            return false;

        try
        {
            EmptyClipboard();

            // Allocate global memory for the string
            int bytes = (text.Length + 1) * sizeof(char);
            IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);

            if (hGlobal == IntPtr.Zero)
                return false;

            IntPtr pGlobal = GlobalLock(hGlobal);
            if (pGlobal == IntPtr.Zero)
                return false;

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, pGlobal, text.Length);
                // Null terminate
                Marshal.WriteInt16(pGlobal + text.Length * sizeof(char), 0);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            SetClipboardData(CF_UNICODETEXT, hGlobal);
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <inheritdoc />
    public bool HasText
    {
        get
        {
            if (!TryOpenClipboardWithRetry(() => OpenClipboard(IntPtr.Zero)))
                return false;

            try
            {
                IntPtr hData = GetClipboardData(CF_UNICODETEXT);
                return hData != IntPtr.Zero;
            }
            finally
            {
                CloseClipboard();
            }
        }
    }

    internal static bool TryOpenClipboardWithRetry(Func<bool> openClipboard, int maxAttempts = 10, Action<int>? retryDelay = null)
    {
        if (openClipboard == null)
            throw new ArgumentNullException(nameof(openClipboard));

        if (maxAttempts <= 0)
            return false;

        retryDelay ??= ApplyClipboardRetryBackoff;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (openClipboard())
                return true;

            if (attempt + 1 < maxAttempts)
                retryDelay(attempt + 1);
        }

        return false;
    }

    private static void ApplyClipboardRetryBackoff(int attempt)
    {
        var spinner = new SpinWait();
        var spins = Math.Min(8, attempt + 1);
        for (int i = 0; i < spins; i++)
        {
            spinner.SpinOnce();
        }
    }
}