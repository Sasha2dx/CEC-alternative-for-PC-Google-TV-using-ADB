using System.Text.RegularExpressions;
namespace TclTvWake;
internal static class TvInputState
{
    // Only the current user's main session counts, never the available-input list.
    internal static string? CurrentInput(string dump)
    {
        var current = Regex.Match(dump, @"Current user: (\d+)");
        if (!current.Success) return null;
        var user = Regex.Matches(dump, @"(?ms)^UserState \((\d+)\):(.*?)(?=^UserState \(|^TvInputHardwareManager|\z)")
            .Cast<Match>().Where(m => m.Groups[1].Value == current.Groups[1].Value).ToArray();
        if (user.Length != 1) return null;
        var main = Regex.Matches(user[0].Value, @"(?m)^\s*mainSessionToken:\s*(\S+)");
        if (main.Count != 1 || main[0].Groups[1].Value == "null") return null;
        var section = Regex.Match(user[0].Value, @"(?ms)^\s*sessionStateMap:[^\r\n]*\r?\n(.*?)(?=^\s*mCallbacks:)");
        if (!section.Success) return null;
        var matches = new List<string>();
        foreach (var block in Regex.Split(section.Groups[1].Value, @"(?m)^\s*\S+: [^\r\n]*\$SessionState@[^\r\n]*\r?\n").Skip(1))
        {
            var tokens = Regex.Matches(block, @"(?m)^\s*sessionToken:\s*(\S+)");
            var inputs = Regex.Matches(block, @"(?m)^\s*inputId:\s*(\S+)");
            if (tokens.Count != 1 || inputs.Count != 1) return null;
            if (tokens[0].Groups[1].Value == main[0].Groups[1].Value) matches.Add(inputs[0].Groups[1].Value);
        }
        return matches.Count == 1 ? matches[0] : null;
    }
}
