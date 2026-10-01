// SecretClipboard.cs: put text on the clipboard with the same markers jt uses, so Windows keeps it
// out of the Win+V history and the cloud clipboard. Win32 directly: the WinForms Clipboard class
// cannot set these custom formats reliably.
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace JtDecrypt;

public static class SecretClipboard
{
    const uint CF_UNICODETEXT = 13;
    const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormatW(string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);

    public static void Set(IntPtr owner, string text)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (OpenClipboard(owner)) break;
            if (attempt == 10) throw new Win32Exception(Marshal.GetLastWin32Error(), "剪贴板正被其它程序占用");
            Thread.Sleep(50);
        }
        try
        {
            if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());
            Put(CF_UNICODETEXT, Utf16z(text));
            // Presence of this format is enough for the clipboard history to skip the item.
            Put(RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing"), [1, 0, 0, 0]);
            Put(RegisterClipboardFormatW("CanIncludeInClipboardHistory"), [0, 0, 0, 0]);
            Put(RegisterClipboardFormatW("CanUploadToCloudClipboard"), [0, 0, 0, 0]);
        }
        finally
        {
            CloseClipboard();
        }
    }

    static byte[] Utf16z(string s)
    {
        byte[] body = System.Text.Encoding.Unicode.GetBytes(s);
        byte[] z = new byte[body.Length + 2];
        Buffer.BlockCopy(body, 0, z, 0, body.Length);
        return z;
    }

    static void Put(uint format, byte[] bytes)
    {
        if (format == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClipboardFormat failed");
        IntPtr handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
        if (handle == IntPtr.Zero) throw new OutOfMemoryException();
        IntPtr p = GlobalLock(handle);
        if (p == IntPtr.Zero) { GlobalFree(handle); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        Marshal.Copy(bytes, 0, p, bytes.Length);
        GlobalUnlock(handle);
        if (SetClipboardData(format, handle) == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            GlobalFree(handle);
            throw new Win32Exception(err, "SetClipboardData failed");
        }
        // On success the system owns the handle.
    }
}
