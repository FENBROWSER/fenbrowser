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
    private const int MaxClipboardChars = 16 * 1024 * 1024;

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    #endregion

    /// <inheritdoc />
    public string GetText()
    {
        if (!TryOpenClipboardWithRetry(() => OpenClipboard(IntPtr.Zero)))
            return string.Empty;

        try
        {
            IntPtr hData = GetClipboardData(CF_UNICODETEXT);
            if (hData == IntPtr.Zero)
                return string.Empty;

            ulong byteSize = GlobalSize(hData).ToUInt64();
            if (byteSize < sizeof(char))
                return string.Empty;

            // Do not trust another process to provide a well-formed null-terminated
            // CF_UNICODETEXT block. Bound reads by the actual HGLOBAL size and by a
            // browser admission limit before asking Marshal to construct the string.
            ulong maxBytes = ((ulong)MaxClipboardChars + 1UL) * sizeof(char);
            if (byteSize > maxBytes)
                return string.Empty;

            int charCapacity = checked((int)Math.Min(byteSize / sizeof(char), int.MaxValue));
            if (charCapacity == 0)
                return string.Empty;

            IntPtr pData = GlobalLock(hData);
            if (pData == IntPtr.Zero)
                return string.Empty;

            try
            {
                var value = Marshal.PtrToStringUni(pData, charCapacity) ?? string.Empty;
                int nullIndex = value.IndexOf('\0');
                return nullIndex >= 0 ? value.Substring(0, nullIndex) : value;
            }
            finally
            {
                GlobalUnlock(hData);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <inheritdoc />
    public bool SetText(string text)
    {
        // Empty text is a valid clipboard payload and is how callers clear text.
        text ??= string.Empty;
        if (text.Length > MaxClipboardChars)
            return false;

        if (!TryOpenClipboardWithRetry(() => OpenClipboard(IntPtr.Zero)))
            return false;

        try
        {
            if (!EmptyClipboard())
                return false;

            ulong bytes = checked(((ulong)text.Length + 1UL) * sizeof(char));
            IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (hGlobal == IntPtr.Zero)
                return false;

            bool ownershipTransferred = false;
            try
            {
                IntPtr pGlobal = GlobalLock(hGlobal);
                if (pGlobal == IntPtr.Zero)
                    return false;

                try
                {
                    if (text.Length > 0)
                    {
                        // Avoid ToCharArray() so large clipboard writes do not allocate a
                        // second complete managed copy of the string.
                        unsafe
                        {
                            fixed (char* source = text)
                            {
                                Buffer.MemoryCopy(
                                    source,
                                    pGlobal.ToPointer(),
                                    checked((long)bytes),
                                    (long)text.Length * sizeof(char));
                            }
                        }
                    }

                    Marshal.WriteInt16(IntPtr.Add(pGlobal, checked(text.Length * sizeof(char))), 0);
                }
                finally
                {
                    GlobalUnlock(hGlobal);
                }

                if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                    return false;

                // On success Windows owns the HGLOBAL until the clipboard contents are
                // replaced. The caller must not free it after SetClipboardData succeeds.
                ownershipTransferred = true;
                return true;
            }
            finally
            {
                if (!ownershipTransferred)
                {
                    GlobalFree(hGlobal);
                }
            }
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
                return hData != IntPtr.Zero && GlobalSize(hData).ToUInt64() >= sizeof(char);
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
        // OpenClipboard commonly fails briefly while another application owns the
        // clipboard. SpinWait retries were effectively immediate and exhausted all
        // attempts before the owner could release it. Keep total blocking bounded.
        int delayMs = Math.Min(32, 1 << Math.Min(5, Math.Max(0, attempt - 1)));
        Thread.Sleep(delayMs);
    }
}
