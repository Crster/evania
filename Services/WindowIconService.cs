using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Evania.Services;

/// <summary>
/// Gives a window the Evania mark in its title bar, taskbar button and Alt+Tab tile.
///
/// WinUI 3 has no icon property on <see cref="Window"/> or AppWindow, so this goes through Win32
/// WM_SETICON. Without it every window shows the generic application glyph that IDI_APPLICATION
/// hands out, no matter how complete the package logos are -- those only drive Start tiles and the
/// splash, which is a different surface.
/// </summary>
public static class WindowIconService
{
    private const int WM_SETICON = 0x0080;
    private static readonly IntPtr IconSmall = new(0);
    private static readonly IntPtr IconBig = new(1);

    private static readonly string IconPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Evania.ico");

    // A window borrows these handles and never owns them, so the icons have to outlive every window
    // they are applied to: disposing one leaves the title bar pointing at a dead HICON.
    private static Icon? _small;
    private static Icon? _big;

    public static void ApplyTo(Window window)
    {
        _small ??= new Icon(IconPath, 16, 16);
        _big ??= new Icon(IconPath, 32, 32);

        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        NativeMethods.SendMessage(hwnd, WM_SETICON, IconSmall, _small.Handle);
        NativeMethods.SendMessage(hwnd, WM_SETICON, IconBig, _big.Handle);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
