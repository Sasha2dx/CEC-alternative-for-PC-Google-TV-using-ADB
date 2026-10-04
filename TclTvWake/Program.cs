using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;
namespace TclTvWake;
internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--probe") { File.WriteAllText(args[1], JsonSerializer.Serialize(Displays.Read(), Json)); return 0; }
            if (args.Contains("--list-displays")) { Console.WriteLine(JsonSerializer.Serialize(Displays.Read(), Json)); return 0; }
            var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")), Json)!;
            if (string.IsNullOrWhiteSpace(config.MonitorId) || config.Attempts < 1 || config.RetrySeconds < 1 || config.WakeDelaySeconds < 0) throw new Exception("Invalid configuration");
            if (args.Contains("--once")) return await new Controller(config).Wake(CancellationToken.None) ? 0 : 1;
            if (args.Contains("--service")) { ServiceBase.Run(new TvService(config)); return 0; }
            Console.WriteLine("Options: --list-displays | --once | --service"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
internal sealed class Config
{
    public string AdbPath { get; set; } = "adb/adb.exe";
    public string Endpoint { get; set; } = "192.168.1.95:5555";
    public string MonitorId { get; set; } = @"MONITOR\XXX9615";
    public string HdmiUri { get; set; } = "content://android.media.tv/passthrough/com.tcl.tvinput%2F.passthroughinput.TvPassThroughService%2FHW1413744128";
    public int Attempts { get; set; } = 12;
    public int RetrySeconds { get; set; } = 5;
    public int WakeDelaySeconds { get; set; } = 3;
}
internal sealed class Controller(Config config)
{
    internal static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TclTvWake");
    private readonly SemaphoreSlim gate = new(1, 1);
    internal static void Log(string message)
    {
        var line = $"{DateTimeOffset.Now:O} {message}";
        Console.WriteLine(line);
        try
        {
            Directory.CreateDirectory(DataDir);
            lock (typeof(Controller))
            {
                var path = Path.Combine(DataDir, "service.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, path + ".1", true);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        } catch { }
    }
    private async Task<bool> Active(CancellationToken ct)
    {
        var monitors = Process.GetCurrentProcess().SessionId == 0 ? await ConsoleProbe.Read(ct) : Displays.Read();
        return monitors.Any(m => m.Active && m.Id.StartsWith(config.MonitorId + "\\", StringComparison.OrdinalIgnoreCase));
    }
    internal async Task<bool> Wake(CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) return false;
        try
        {
            for (int attempt = 1; attempt <= config.Attempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!await Active(ct)) Log($"Attempt {attempt}: TV is not an active Windows display; no ADB commands sent.");
                    else
                    {
                        await Adb(ct, "connect", config.Endpoint);
                        await Adb(ct, "-s", config.Endpoint, "shell", "input", "keyevent", "KEYCODE_WAKEUP");
                        await Task.Delay(TimeSpan.FromSeconds(config.WakeDelaySeconds), ct);
                        var power = await Adb(ct, "-s", config.Endpoint, "shell", "dumpsys", "power");
                        if (!power.Contains("mWakefulness=Awake") || !power.Contains("Display Power: state=ON")) throw new Exception("TV did not confirm Awake / ON.");
                        if (!await Active(ct)) { Log("TV display disabled before HDMI selection; stopping."); return false; }
                        var output = await Adb(ct, "-s", config.Endpoint, "shell", "am", "start", "-a", "android.intent.action.VIEW", "-d", config.HdmiUri);
                        if (output.Contains("Error:", StringComparison.OrdinalIgnoreCase) || output.Contains("Exception", StringComparison.OrdinalIgnoreCase)) throw new Exception(output);
                        Log("TV awake; HDMI1 intent delivered. " + output.Trim()); return true;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { Log($"Attempt {attempt}: {ex.Message}"); }
                if (attempt < config.Attempts) await Task.Delay(TimeSpan.FromSeconds(config.RetrySeconds), ct);
            }
            Log("Startup/resume attempts exhausted."); return false;
        }
        finally { gate.Release(); }
    }
    private async Task<string> Adb(CancellationToken ct, params string[] args)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(config.AdbPath, AppContext.BaseDirectory)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["ADB_VENDOR_KEYS"] = Path.Combine(AppContext.BaseDirectory, "keys");
        start.ArgumentList.Add("-P"); start.ArgumentList.Add("5038");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new Exception("Could not start ADB");
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); ct.ThrowIfCancellationRequested(); throw new System.TimeoutException("ADB command timed out"); }
        var output = await stdout + "\n" + await stderr;
        if (process.ExitCode != 0) throw new Exception("ADB: " + output.Trim()); return output;
    }
}
internal sealed class TvService : ServiceBase
{
    private readonly Controller controller;
    private readonly CancellationTokenSource stopping = new();
    private Task work = Task.CompletedTask;
    private long lastTrigger;
    public TvService(Config config) { ServiceName = "TclTvWake"; CanHandlePowerEvent = true; CanShutdown = true; AutoLog = false; controller = new(config); }
    protected override void OnStart(string[] args) => Trigger("Service startup");
    protected override bool OnPowerEvent(PowerBroadcastStatus status)
    {
        if (status is PowerBroadcastStatus.ResumeAutomatic or PowerBroadcastStatus.ResumeSuspend or PowerBroadcastStatus.ResumeCritical) Trigger("Resume: " + status);
        return true;
    }
    private void Trigger(string reason)
    {
        var now = Environment.TickCount64;
        if (lastTrigger != 0 && now - lastTrigger < 5000) return;
        lastTrigger = now;
        Controller.Log(reason);
        if (!work.IsCompleted) return;
        work = Task.Run(async () => { try { await controller.Wake(stopping.Token); } catch (OperationCanceledException) { } catch (Exception ex) { Controller.Log(ex.ToString()); } });
    }
    protected override void OnStop() { stopping.Cancel(); try { work.Wait(TimeSpan.FromSeconds(20)); } catch (AggregateException) { } }
    protected override void OnShutdown() => OnStop();
}
