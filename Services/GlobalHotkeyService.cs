using System;
using System.Runtime.InteropServices;

namespace Evania.Services;

/// <summary>
/// Registers the capture shortcut the Settings page picks.
///
/// <see cref="NativeMethods.RegisterHotKey"/> posts <c>WM_HOTKEY</c> to the window named at
/// registration, so the service owns a message-only window rather than chaining onto the main
/// window's procedure: a hidden window that exists purely to receive this one message cannot fight
/// WinUI for its own window procedure, keeps working while the main window is hidden in the tray,
/// and needs no comctl32 version assumptions. It is created on the UI thread, so subscribers get the
/// callback already marshalled and can start a capture directly.
/// </summary>
public static class GlobalHotkeyService
{
    public static event Action? Invoked;

    /// <summary>Why the last requested shortcut could not be registered, for the Settings page.</summary>
    public static string? LastError { get; private set; }

    /// <summary>The shortcut currently live on the keyboard.</summary>
    public static ScreenshotHotkey Registered { get; private set; } = ScreenshotHotkey.None;

    private const uint WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x5641;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;

    private const uint VK_SNAPSHOT = 0x2C;
    private const uint VK_3 = 0x33;
    private const uint VK_P = 0x50;

    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    private const string ClassName = "EvaniaHotkeyWindow";

    private static IntPtr windowHandle;

    // The window procedure is a managed delegate handed to unmanaged code: with nothing holding a
    // reference, the collector takes it and the next WM_HOTKEY calls into freed memory.
    private static NativeMethods.WndProc? windowProc;

    /// <summary>Creates the message window. Call on the UI thread; safe to call twice.</summary>
    public static void Attach()
    {
        if (windowHandle != IntPtr.Zero)
        {
            return;
        }

        IntPtr module = NativeMethods.GetModuleHandle(null);

        windowProc = HandleMessage;

        WNDCLASSEX windowClass = new()
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(windowProc),
            hInstance = module,
            lpszClassName = ClassName,
        };

        ushort atom = NativeMethods.RegisterClassEx(ref windowClass);

        if (atom == 0 && Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS)
        {
            windowProc = null;
            LastError = "Evania could not start listening for its shortcut key.";

            return;
        }

        windowHandle = NativeMethods.CreateWindowEx(
            0, ClassName, ClassName, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, module, IntPtr.Zero);

        if (windowHandle == IntPtr.Zero)
        {
            windowProc = null;
            LastError = "Evania could not start listening for its shortcut key.";
        }
    }

    public static void Detach()
    {
        UnregisterCore();

        if (windowHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(windowHandle);
            windowHandle = IntPtr.Zero;
        }

        windowProc = null;
    }

    /// <summary>
    /// Moves the keyboard listener to the requested shortcut. Returns null when it is live, or the
    /// message to show - including when another app already owns the keys, which for Print Screen is
    /// the usual outcome of Windows or the Snipping Tool claiming it first.
    /// </summary>
    public static string? Apply(ScreenshotHotkey hotkey)
    {
        if (Registered == hotkey)
        {
            return LastError;
        }

        UnregisterCore();

        if (hotkey == ScreenshotHotkey.None)
        {
            LastError = null;

            return null;
        }

        if (windowHandle == IntPtr.Zero)
        {
            LastError = "Evania is not listening for shortcut keys yet.";

            return LastError;
        }

        (uint modifiers, uint virtualKey) = KeysFor(hotkey);

        if (NativeMethods.RegisterHotKey(windowHandle, HotkeyId, modifiers, virtualKey))
        {
            Registered = hotkey;
            LastError = null;

            return null;
        }

        int error = Marshal.GetLastWin32Error();
        LastError = error == ERROR_HOTKEY_ALREADY_REGISTERED
            ? $"{hotkey.ToDisplayString()} is already taken by another app. Free it there, or pick another shortcut."
            : $"Windows refused {hotkey.ToDisplayString()} (error {error}).";

        return LastError;
    }

    private static (uint Modifiers, uint VirtualKey) KeysFor(ScreenshotHotkey hotkey) => hotkey switch
    {
        ScreenshotHotkey.PrintScreen => (MOD_NOREPEAT, VK_SNAPSHOT),
        ScreenshotHotkey.AltPrintScreen => (MOD_ALT | MOD_NOREPEAT, VK_SNAPSHOT),
        // Digit row, so the number pad does not have to be on.
        ScreenshotHotkey.CtrlShift3 => (MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_3),
        ScreenshotHotkey.AltP => (MOD_ALT | MOD_NOREPEAT, VK_P),
        _ => (0u, 0u),
    };

    private static void UnregisterCore()
    {
        if (Registered != ScreenshotHotkey.None && windowHandle != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(windowHandle, HotkeyId);
        }

        Registered = ScreenshotHotkey.None;
    }

    private static IntPtr HandleMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WM_HOTKEY && (int)wParam == HotkeyId)
        {
            Invoked?.Invoke();

            return IntPtr.Zero;
        }

        // Everything else, including WM_DESTROY, goes to the default procedure. No PostQuitMessage:
        // this window shares the UI thread's message loop with the app itself, so quitting it here
        // would take the whole application down when the shortcut is only being changed.
        return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    private static class NativeMethods
    {
        public delegate IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // CharSet must match CreateWindowEx below: without it the runtime binds RegisterClassExA,
        // which reads the class name as ANSI and takes "EvaniaHotkeyWindow" as just "E", so the wide
        // CreateWindowExW then finds no such class and returns NULL.
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx([In] ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}
