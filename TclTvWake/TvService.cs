using System.ComponentModel;
using System.Runtime.InteropServices;
namespace TclTvWake;
// Native SCM hosting is needed because ServiceBase does not expose PRESHUTDOWN.
internal sealed class TvService
{
    private const string Name = "TclTvWake";
    private readonly Controller controller;
    private readonly object sync = new();
    private readonly ManualResetEventSlim ended = new();
    private CancellationTokenSource wakeCancellation = new();
    private Task wake = Task.CompletedTask;
    private readonly CancellationTokenSource monitorCancellation = new();
    private readonly DisplayTransition displayTransition = new();
    private Task monitor = Task.CompletedTask;
    private long displayEpoch;
    private bool stopping;
    private bool suspended;
    private long lastResume;
    private IntPtr statusHandle;
    private Status status;
    private readonly MainCallback mainCallback;
    private readonly ControlCallback controlCallback;
    internal TvService(Config config) { controller = new(config); mainCallback = Main; controlCallback = Control; }
    internal void Run()
    {
        var table = new[] { new Entry { Name = Name, Main = mainCallback }, new Entry() };
        if (!StartServiceCtrlDispatcher(table)) throw new Win32Exception();
        GC.KeepAlive(mainCallback); GC.KeepAlive(controlCallback);
    }
    private void Report(uint state, uint hint = 0, uint error = 0)
    {
        lock (sync)
        {
            status = new Status { Type = 0x10, State = state, Accepted = state == 4 ? 1u | 4u | 64u | 256u : 0,
                ExitCode = error, WaitHint = hint, Checkpoint = state is 2 or 3 ? status.Checkpoint + 1 : 0 };
            if (!SetServiceStatus(statusHandle, ref status)) Controller.Log("SetServiceStatus: " + Marshal.GetLastWin32Error());
        }
    }
    private void Main(uint count, IntPtr args)
    {
        try
        {
            statusHandle = RegisterServiceCtrlHandlerEx(Name, controlCallback, IntPtr.Zero);
            if (statusHandle == IntPtr.Zero) { Controller.Log("Register handler failed: " + Marshal.GetLastWin32Error()); return; }
            Report(2, 5000); Report(4); TriggerWake("Service startup");
            monitor = Task.Run(MonitorDisplays);
            ended.Wait();
        }
        catch (Exception ex) { Controller.Log(ex.ToString()); if (statusHandle != IntPtr.Zero) Report(1, error: 1064); }
    }
    private uint Control(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        try
        {
            switch (control)
            {
                case 4: lock (sync) { SetServiceStatus(statusHandle, ref status); } break;
                case 1: BeginStop(false); break;
                case 5: case 15: BeginStop(true); break;
                case 13:
                    if (eventType == 4) Suspend();
                    else if (eventType is 7 or 18 or 6) TriggerWake("Resume: " + eventType);
                    break;
            }
        }
        catch (Exception ex) { Controller.Log("Control handler: " + ex); }
        return 0;
    }
    private void TriggerWake(string reason)
    {
        lock (sync)
        {
            if (stopping) return;
            var now = Environment.TickCount64;
            if (!suspended && lastResume != 0 && now - lastResume < 5000) return;
            suspended = false; lastResume = now;
            displayEpoch++; displayTransition.Reset();
            CancelWake();
            var previous = wake;
            var cancellation = new CancellationTokenSource(); wakeCancellation = cancellation;
            Controller.Log(reason);
            wake = Task.Run(async () =>
            {
                try { await previous; await controller.Wake(cancellation.Token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Controller.Log(ex.ToString()); }
                finally { cancellation.Dispose(); }
            });
        }
    }
    private void Suspend()
    {
        lock (sync)
        {
            if (stopping || suspended) return;
            suspended = true;
            displayEpoch++; displayTransition.Reset();
            CancelWake();
        }
        Controller.Log("Suspend: checking HDMI1 before sleep (1500 ms budget).");
        // Windows may suspend networking at any point; do not queue work that
        // could execute Home after resume. Cancel everything before returning.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        controller.HomeIfHdmi1(deadline.Token).GetAwaiter().GetResult();
        deadline.Cancel();
    }
    private void CancelWake()
    {
        try { wakeCancellation.Cancel(); } catch (ObjectDisposedException) { }
    }
    private async Task MonitorDisplays()
    {
        var initialized = false;
        while (!monitorCancellation.IsCancellationRequested)
        {
            try
            {
                long epoch;
                lock (sync) { epoch = displayEpoch; }
                var active = await controller.Active(monitorCancellation.Token, watchChanges: initialized);
                initialized = true;
                lock (sync)
                {
                    if (stopping) return;
                    if (suspended || epoch != displayEpoch) continue;
                    var changed = displayTransition.Observe(active);
                    if (changed.HasValue) DisplayChanged(changed.Value);
                }
            }
            catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Controller.Log("Display observation failed; preserving previous state: " + ex.Message);
                try { await Task.Delay(3000, monitorCancellation.Token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
    private void DisplayChanged(bool active)
    {
        // Called under sync: serialize actions and cancel stale requests when
        // the user reverses a display change, sleeps, or shuts down.
        CancelWake();
        var previous = wake;
        var cancellation = new CancellationTokenSource(); wakeCancellation = cancellation;
        Controller.Log(active ? "Display transition: inactive -> active; requesting HDMI1."
            : "Display transition: active -> inactive; checking HDMI1 before Home.");
        wake = Task.Run(async () =>
        {
            try
            {
                await previous;
                cancellation.Token.ThrowIfCancellationRequested();
                if (active) await controller.Wake(cancellation.Token);
                else
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    await controller.HomeIfHdmi1(deadline.Token, afterDeactivation: true);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Controller.Log("Display action: " + ex); }
            finally { cancellation.Dispose(); }
        });
    }
    private void BeginStop(bool home)
    {
        lock (sync)
        {
            if (stopping) return;
            stopping = true; CancelWake();
            monitorCancellation.Cancel();
            Report(3, 15000);
        }
        _ = Task.Run(async () =>
        {
            try
            {
                if (home)
                {
                    Controller.Log("Preshutdown/shutdown: checking HDMI1 (10 s budget).");
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await controller.HomeIfHdmi1(deadline.Token);
                }
                await wake.WaitAsync(TimeSpan.FromSeconds(3));
                await monitor.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex) { Controller.Log("Stopping: " + ex.Message); }
            finally { Report(1); ended.Set(); }
        });
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void MainCallback(uint count, IntPtr args);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint ControlCallback(uint control, uint eventType, IntPtr eventData, IntPtr context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Entry
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        public MainCallback? Main;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Status { public uint Type, State, Accepted, ExitCode, SpecificExitCode, Checkpoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartServiceCtrlDispatcher([In] Entry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr RegisterServiceCtrlHandlerEx(string name, ControlCallback handler, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetServiceStatus(IntPtr handle, ref Status status);
}
