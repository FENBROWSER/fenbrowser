using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FenBrowser.Host.Widgets;

/// <summary>
/// Cross-platform clipboard helper using native APIs.
/// Windows: Uses Win32 clipboard APIs.
/// </summary>
public static class ClipboardHelper
{
    #region Windows Native Methods
    
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
    
    /// <summary>
    /// Get text from clipboard.
    /// </summary>
    public static string GetText()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // For non-Windows, return empty (could implement later)
            return string.Empty;
        }
        
        string result = string.Empty;
        
        // Try multiple times in case clipboard is locked
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
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
                                result = Marshal.PtrToStringUni(pData) ?? string.Empty;
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
                break;
            }
            Thread.Sleep(10);
        }
        
        return result;
    }
    
    /// <summary>
    /// Set text to clipboard.
    /// </summary>
    public static bool SetText(string text)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // For non-Windows, return false (could implement later)
            return false;
        }
        
        if (string.IsNullOrEmpty(text))
            return false;
        
        // Try multiple times in case clipboard is locked
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
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
            Thread.Sleep(10);
        }
        
        return false;
    }
}
