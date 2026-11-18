using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace OneBox.App;

internal static class NativeClipboard
{
    private const uint UnicodeText = 13;
    private const uint MoveableMemory = 0x0002;
    private static HwndSource? _owner;

    // 只在专用剪贴板线程调用，提前提交Unicode文本，避免依赖OLE延迟渲染。
    public static void SetText(string text)
    {
        _owner ??= new HwndSource(new HwndSourceParameters("OneBox Clipboard") { ParentWindow = new IntPtr(-3) });
        var characterList = (text + '\0').ToCharArray();
        var memory = GlobalAlloc(MoveableMemory, (nuint)characterList.Length * sizeof(char));
        if (memory == IntPtr.Zero) throw ClipboardError(nameof(GlobalAlloc));
        try
        {
            var destination = GlobalLock(memory);
            if (destination == IntPtr.Zero) throw ClipboardError(nameof(GlobalLock));
            try { Marshal.Copy(characterList, 0, destination, characterList.Length); }
            finally { GlobalUnlock(memory); }

            for (var attempt = 0; !OpenClipboard(_owner.Handle); attempt++)
            {
                if (attempt >= 15) throw ClipboardError(nameof(OpenClipboard));
                Thread.Sleep(25);
            }
            try
            {
                if (!EmptyClipboard()) throw ClipboardError(nameof(EmptyClipboard));
                if (SetClipboardData(UnicodeText, memory) == IntPtr.Zero) throw ClipboardError(nameof(SetClipboardData));
                memory = IntPtr.Zero;
            }
            finally { CloseClipboard(); }
        }
        finally
        {
            if (memory != IntPtr.Zero) GlobalFree(memory);
        }
    }

    private static Win32Exception ClipboardError(string operation) => new(Marshal.GetLastPInvokeError(), operation);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
}
