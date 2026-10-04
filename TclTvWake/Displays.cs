using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace TclTvWake;
internal sealed record MonitorInfo(string Id, string Name, string Source, bool Active);
internal static class Displays
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Device
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? name, uint index, ref Device device, uint flags);
    internal static MonitorInfo[] Read()
    {
        var result = new List<MonitorInfo>();
        for (uint i = 0; ; i++)
        {
            var adapter = new Device { Size = Marshal.SizeOf<Device>() };
            if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;
            for (uint j = 0; ; j++)
            {
                var monitor = new Device { Size = Marshal.SizeOf<Device>() };
                if (!EnumDisplayDevices(adapter.Name, j, ref monitor, 0)) break;
                result.Add(new(monitor.Id, monitor.Description, adapter.Name, (adapter.Flags & 1) != 0 && (monitor.Flags & 1) != 0 && (adapter.Flags & 8) == 0));
            }
        }
        return result.ToArray();
    }
}
// Query displays in the physical console session, including before user sign-in.
// Session 0 cannot reliably inspect the console's desktop display configuration.
internal static class ConsoleProbe
{
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonation, int type, out IntPtr duplicate);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUser(IntPtr token, string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref Startup startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr handle, uint code);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Startup
    {
        public int Size; public string? Reserved; public string? Desktop; public string? Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint Id, ThreadId; }
    internal static async Task<MonitorInfo[]> Read(CancellationToken ct)
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session == uint.MaxValue) throw new Exception("No physical console session yet");
        using var winlogon = Process.GetProcessesByName("winlogon").FirstOrDefault(p => p.SessionId == session) ?? throw new Exception("Console winlogon not ready");
        var parent = OpenProcess(0x1000, false, winlogon.Id);
        if (parent == IntPtr.Zero) throw new Win32Exception();
        IntPtr token;
        try { if (!OpenProcessToken(parent, 0x0002 | 0x0008, out token)) throw new Win32Exception(); }
        finally { CloseHandle(parent); }
        IntPtr duplicate = IntPtr.Zero;
        var path = Path.Combine(Controller.DataDir, "probe-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Directory.CreateDirectory(Controller.DataDir);
            if (!DuplicateTokenEx(token, 0x02000000, IntPtr.Zero, 2, 1, out duplicate)) throw new Win32Exception();
            var executable = Environment.ProcessPath!;
            var startup = new Startup { Size = Marshal.SizeOf<Startup>(), Desktop = @"winsta0\default" };
            if (!CreateProcessAsUser(duplicate, executable, new StringBuilder($"\"{executable}\" --probe \"{path}\""), IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var child)) throw new Win32Exception();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    while (true)
                    {
                        var status = WaitForSingleObject(child.Process, 0);
                        if (status == 0) break;
                        if (status != 258) throw new Win32Exception();
                        await Task.Delay(100, timeout.Token);
                    }
                }
                catch (OperationCanceledException) { TerminateProcess(child.Process, 1); ct.ThrowIfCancellationRequested(); throw new TimeoutException("Console probe timed out"); }
                if (!GetExitCodeProcess(child.Process, out var exitCode)) throw new Win32Exception();
                if (exitCode != 0) throw new Exception("Console display probe failed");
            }
            finally { CloseHandle(child.Process); CloseHandle(child.Thread); }
            return JsonSerializer.Deserialize<MonitorInfo[]>(File.ReadAllText(path), Program.Json) ?? throw new Exception("Invalid probe result");
        }
        finally { if (duplicate != IntPtr.Zero) CloseHandle(duplicate); CloseHandle(token); if (File.Exists(path)) File.Delete(path); }
    }
}
