using System.ComponentModel;
using System.Runtime.InteropServices;
namespace TclTvWake;
// A short-lived invisible top-level window in the console session receives
// display/device broadcasts. A 3-second fallback also covers missed broadcasts.
internal static class DisplayNotification
{
    internal static void Wait()
    {
        var className = "TclDisplayWatch" + Environment.ProcessId;
        WindowProc callback = Procedure;
        var instance = GetModuleHandle(null);
        var cls = new WindowClass { Procedure = callback, Instance = instance, ClassName = className };
        if (RegisterClass(ref cls) == 0) throw new Win32Exception();
        IntPtr window = IntPtr.Zero;
        try
        {
            window = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (window == IntPtr.Zero) throw new Win32Exception();
            if (SetTimer(window, 1, 3000, IntPtr.Zero) == 0) throw new Win32Exception();
            int result;
            while ((result = GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0) DispatchMessage(ref message);
            if (result < 0) throw new Win32Exception();
        }
        finally { if (window != IntPtr.Zero) DestroyWindow(window); UnregisterClass(className, instance); GC.KeepAlive(callback); }
    }
    private static IntPtr Procedure(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam)
    {
        // WM_DISPLAYCHANGE / WM_DEVICECHANGE: let the configuration settle.
        if (message is 0x7e or 0x219) SetTimer(window, 2, 400, IntPtr.Zero);
        if (message == 0x113) { PostQuitMessage(0); return IntPtr.Zero; }
        return DefWindowProc(window, message, wParam, lParam);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WindowProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Style; public WindowProc Procedure; public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName; public string ClassName;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    { public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint milliseconds, IntPtr callback);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
}
internal sealed class DisplayTransition
{
    private bool? previous;
    internal void Reset() => previous = null;
    internal bool? Observe(bool active)
    {
        var change = previous.HasValue && previous.Value != active;
        previous = active;
        return change ? active : null;
    }
}
