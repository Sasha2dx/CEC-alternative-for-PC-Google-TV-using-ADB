using System.Diagnostics;

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
            if (args.Length == 2 && args[0] == "--watch-change") { DisplayNotification.Wait(); File.WriteAllText(args[1], JsonSerializer.Serialize(Displays.Read(), Json)); return 0; }
            if (args.Contains("--list-displays")) { Console.WriteLine(JsonSerializer.Serialize(Displays.Read(), Json)); return 0; }
            var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")), Json)!;
            config.Validate();
            if (args.Contains("--home-if-hdmi1")) { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); return await new Controller(config).HomeIfHdmi1(deadline.Token) ? 0 : 1; }
            if (args.Contains("--home-sleep-budget")) { using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500)); return await new Controller(config).HomeIfHdmi1(deadline.Token) ? 0 : 1; }
            if (args.Contains("--once")) return await new Controller(config).Wake(CancellationToken.None) ? 0 : 1;
            if (args.Contains("--service")) { new TvService(config).Run(); return 0; }
            Console.WriteLine("Options: --list-displays | --once | --home-if-hdmi1 | --service"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
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
    internal async Task<bool> Active(CancellationToken ct, bool watchChanges = false)
    {
        var monitors = Process.GetCurrentProcess().SessionId == 0 ? await ConsoleProbe.Read(ct, watchChanges) : Displays.Read();
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
    internal async Task<bool> HomeIfHdmi1(CancellationToken ct, bool afterDeactivation = false)
    {
        try
        {
            await gate.WaitAsync(ct);
            try
            {
                if (await Active(ct) == afterDeactivation)
                { Log(afterDeactivation ? "Home skipped: TV became active again; no ADB commands sent."
                    : "Home skipped: TV is not an active Windows display; no ADB commands sent."); return false; }
                await Adb(ct, "connect", config.Endpoint);
                var power = await Adb(ct, "-s", config.Endpoint, "shell", "dumpsys", "power");
                if (!power.Contains("mWakefulness=Awake") || !power.Contains("Display Power: state=ON"))
                { Log("Home skipped: TV is not awake / ON."); return false; }
                var dump = await Adb(ct, "-s", config.Endpoint, "shell", "dumpsys", "tv_input");
                var input = TvInputState.CurrentInput(dump);
                const string prefix = "content://android.media.tv/passthrough/";
                if (!config.HdmiUri.StartsWith(prefix, StringComparison.Ordinal)) throw new Exception("Invalid HDMI URI.");
                var expected = Uri.UnescapeDataString(config.HdmiUri[prefix.Length..]);
                if (input != expected) { Log("Home skipped: current input is " + (input ?? "unknown / no main session") + "."); return false; }
                if (await Active(ct) == afterDeactivation)
                { Log("Home skipped: TV display state changed before Home."); return false; }
                await Adb(ct, "-s", config.Endpoint, "shell", "input", "keyevent", "KEYCODE_HOME");
                Log("HDMI1 main session confirmed; Home command delivered."); return true;
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { Log("Home skipped/interrupted: deadline or cancellation."); return false; }
        catch (Exception ex) { Log("Home failed: " + ex.Message); return false; }
    }
    private async Task<string> Adb(CancellationToken ct, params string[] args)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Path.GetFullPath(config.AdbPath, AppContext.BaseDirectory)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["ADB_VENDOR_KEYS"] = Path.Combine(AppContext.BaseDirectory, "keys");
        start.ArgumentList.Add("-P"); start.ArgumentList.Add(config.AdbServerPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new Exception("Could not start ADB");
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(config.CommandTimeoutSeconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); ct.ThrowIfCancellationRequested(); throw new System.TimeoutException("ADB command timed out"); }
        var output = await stdout + "\n" + await stderr;
        if (process.ExitCode != 0) throw new Exception("ADB: " + output.Trim()); return output;
    }
}
