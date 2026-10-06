namespace TclTvWake;

internal sealed class Config
{
    public string AdbPath { get; set; } = "adb/adb.exe";
    // Hardware-specific values must come from appsettings.json.
    public string Endpoint { get; set; } = "";
    public string MonitorId { get; set; } = "";
    public string HdmiUri { get; set; } = "";
    public int AdbServerPort { get; set; } = 5038;
    public int CommandTimeoutSeconds { get; set; } = 15;
    public int Attempts { get; set; } = 12;
    public int RetrySeconds { get; set; } = 5;
    public int WakeDelaySeconds { get; set; } = 3;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(AdbPath) || string.IsNullOrWhiteSpace(Endpoint)
            || string.IsNullOrWhiteSpace(MonitorId)
            || !HdmiUri.StartsWith("content://android.media.tv/passthrough/", StringComparison.Ordinal)
            || HdmiUri.Length <= "content://android.media.tv/passthrough/".Length
            || AdbServerPort is < 1 or > 65535 || CommandTimeoutSeconds < 1
            || Attempts < 1 || RetrySeconds < 1 || WakeDelaySeconds < 0)
            throw new InvalidOperationException("Invalid appsettings.json: specify Endpoint, MonitorId, HdmiUri and valid ADB/retry settings.");
    }
}
