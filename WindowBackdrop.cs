using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MarkDNext;

public enum WindowBackdropKind
{
    Flat,
    Mica,
    Acrylic
}

public static class WindowBackdrop
{
    private const int DwmwaSystemBackdropType = 38;

    public static bool TryApply(Window window, WindowBackdropKind kind)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var value = kind switch
        {
            WindowBackdropKind.Mica => 2,
            WindowBackdropKind.Acrylic => 3,
            _ => 1
        };

        return DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref value, Marshal.SizeOf<int>()) == 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
