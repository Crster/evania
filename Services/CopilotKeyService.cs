using System;
using System.Runtime.InteropServices;

namespace Evania.Services;

/// <summary>
/// Receives Microsoft Copilot hardware key presses while the app is running, without going through
/// URI activation.
///
/// The system finds providers through the manifest declaration, but a running app only gets the key
/// states delivered as a window message after it publishes a message id on the window's property
/// store (the fast path). Registering also requires subclassing the window procedure, because the
/// message arrives at the HWND directly and WinUI's own procedure would drop it. The subclass
/// delegate is kept alive statically for the same reason <see cref="GlobalHotkeyService"/> keeps
/// its window procedure alive: unmanaged code holds a raw pointer, and the collector does not know
/// that.
/// </summary>
public static class CopilotKeyService
{
    /// <summary>Raised on the UI thread when the Copilot key is tapped once.</summary>
    public static event Action? Tapped;

    /// <summary>Raised on the UI thread when the Copilot key is held down (300 ms or more).</summary>
    public static event Action? HoldStarted;

    /// <summary>Raised on the UI thread when a held Copilot key is released.</summary>
    public static event Action? HoldStopped;

    /// <summary>Must match the MessageWParam attributes declared in Package.appxmanifest.</summary>
    private const nuint WParamSingleTap = 0;
    private const nuint WParamHoldStart = 1;
    private const nuint WParamHoldStop = 2;

    // The system posts this id back to the window; it has to sit in the WM_APP range so it cannot
    // collide with a system-defined message.
    private const uint WM_COPILOT = 0x8000 + 0x0001;

    // The window must expose its message id under this property key for the shell to find it.
    private static readonly Guid CopilotKeyMessagePropertyId = new("38652BCA-4329-4E74-86F9-39CF29345EEA");
    private const uint CopilotKeyMessagePropertyPid = 2;

    private const ushort VT_UINT = 23;

    private static NativeMethods.SUBCLASSPROC? subclassProc;

    public static void Attach(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            int hr = NativeMethods.SHGetPropertyStoreForWindow(hWnd, typeof(NativeMethods.IPropertyStore).GUID, out object store);

            if (hr != 0 || store is not NativeMethods.IPropertyStore propertyStore)
            {
                return;
            }

            NativeMethods.PROPERTYKEY key = new()
            {
                fmtid = CopilotKeyMessagePropertyId,
                pid = CopilotKeyMessagePropertyPid,
            };

            NativeMethods.PROPVARIANT value = default;
            value.vt = VT_UINT;
            value.uintVal = WM_COPILOT;

            propertyStore.SetValue(in key, in value);
            propertyStore.Commit();

            subclassProc = HandleMessage;
            NativeMethods.SetWindowSubclass(hWnd, subclassProc, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception)
        {
            // URI activation still works when the fast path cannot be set up; the key just takes the
            // slower route. That is a degradation, not a reason to fail the launch.
        }
    }

    private static IntPtr HandleMessage(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_COPILOT)
        {
            switch ((nuint)wParam)
            {
                case WParamSingleTap:
                    Tapped?.Invoke();
                    break;
                case WParamHoldStart:
                    HoldStarted?.Invoke();
                    break;
                case WParamHoldStop:
                    HoldStopped?.Invoke();
                    break;
            }

            return IntPtr.Zero;
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private static class NativeMethods
    {
        public delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

        [DllImport("shell32.dll")]
        public static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [DllImport("comctl32.dll")]
        public static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll")]
        public static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        // Explicit layout matches the unmanaged PROPVARIANT: the value union starts at offset 8.
        [StructLayout(LayoutKind.Explicit)]
        public struct PROPVARIANT
        {
            [FieldOffset(0)]
            public ushort vt;
            [FieldOffset(8)]
            public uint uintVal;
        }

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPropertyStore
        {
            uint GetCount();
            int GetAt(uint iProp, out PROPERTYKEY pkey);
            int GetValue(in PROPERTYKEY key, out PROPVARIANT pv);
            int SetValue(in PROPERTYKEY key, in PROPVARIANT pv);
            int Commit();
        }
    }
}
