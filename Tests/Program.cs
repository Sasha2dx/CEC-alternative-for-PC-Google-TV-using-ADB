using TclTvWake;
const string hdmi1 = "com.tcl.tvinput/.passthroughinput.TvPassThroughService/HW1413744128";
const string hdmi2 = "com.tcl.tvinput/.passthroughinput.TvPassThroughService/HW1413744384";
string Session(string token, string input) => $"    {token}: com.android.server.tv.TvInputManagerService$SessionState@123\n      inputId: {input}\n      sessionToken: {token}\n";
string User(int id, string main, string sessions) => $"UserState ({id}):\n  inputMap:\n    {hdmi1}: available\n  sessionStateMap: ITvInputSession -> SessionState\n{sessions}  mCallbacks:\n  mainSessionToken: {main}\n";
string Dump(string main, string sessions) => "User Ids (Current user: 0):\n" + User(0, main, sessions) + "TvInputHardwareManager Info:\n";
var cases = new (string Name, string Dump, string? Expected)[] {
    ("HDMI1 main", Dump("token1", Session("token1", hdmi1)), hdmi1),
    ("HDMI2 main", Dump("token2", Session("token2", hdmi2)), hdmi2),
    ("Home / null", Dump("null", ""), null),
    ("HDMI1 secondary only", Dump("token2", Session("token1", hdmi1) + Session("token2", hdmi2)), hdmi2),
    ("Missing main mapping", Dump("absent", Session("token1", hdmi1)), null),
    ("Duplicate main mapping", Dump("token1", Session("token1", hdmi1) + Session("token1", hdmi2)), null),
    ("Truncated", Dump("token1", Session("token1", hdmi1)).Split("mCallbacks:")[0], null),
    ("Other user HDMI1", "User Ids (Current user: 0):\n" + User(1,"token1",Session("token1",hdmi1)) + User(0,"null", "") + "TvInputHardwareManager Info:\n", null),
    ("CRLF", Dump("token1", Session("token1", hdmi1)).Replace("\n", "\r\n"), hdmi1),
    ("Invalid output", "error: device offline", null)
};
foreach (var test in cases) {
    var actual = TvInputState.CurrentInput(test.Dump);
    if (actual != test.Expected) throw new Exception($"{test.Name}: expected {test.Expected ?? "null"}, got {actual ?? "null"}");
    Console.WriteLine("PASS " + test.Name);
}
var transitions = new DisplayTransition();
void Check(bool? actual, bool? expected, string name) { if (actual != expected) throw new Exception(name); Console.WriteLine("PASS " + name); }
Check(transitions.Observe(false), null, "Initial inactive is baseline, not Home");
Check(transitions.Observe(false), null, "Repeated inactive ignored");
Check(transitions.Observe(true), true, "Activation detected");
Check(transitions.Observe(true), null, "Repeated active ignored");
Check(transitions.Observe(false), false, "Deactivation detected");
Check(transitions.Observe(true), true, "Rapid reversal detected");
transitions.Reset();
Check(transitions.Observe(false), null, "Sleep/resume reset suppresses stale transition");
var missingHardware = new Config();
try { missingHardware.Validate(); throw new Exception("Missing hardware settings were accepted"); }
catch (InvalidOperationException) { Console.WriteLine("PASS Hardware configuration is required"); }
var configured = new Config { Endpoint = "192.0.2.10:5555", MonitorId = @"MONITOR\TEST123", HdmiUri = "content://android.media.tv/passthrough/example%2Finput", AdbServerPort = 5040 };
configured.Validate();
Console.WriteLine("PASS Custom hardware and server port accepted");
configured.AdbServerPort = 0;
try { configured.Validate(); throw new Exception("Invalid server port was accepted"); }
catch (InvalidOperationException) { Console.WriteLine("PASS Invalid ADB server port rejected"); }
