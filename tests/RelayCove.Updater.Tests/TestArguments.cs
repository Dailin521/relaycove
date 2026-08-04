namespace RelayCove.Updater.Tests;

internal static class TestArguments
{
    internal static string[] Create(string? replacementKey = null, string? replacementValue = null)
    {
        var values = new List<string>
        {
            "apply", "--archive", Path.Combine(Path.GetTempPath(), "release.zip"),
            "--expected-sha256", new string('a', 64), "--expected-size", "1",
            "--expected-version", "1.0.1-rc.1", "--current-version", "1.0.0",
            "--target", Path.Combine(Path.GetTempPath(), "relaycove-target"),
            "--wait-pid", "12", "--wait-start-time-utc-ticks", "638000000000000000",
            "--bootstrap-token", "1234567890abcdef1234567890abcdef",
        };
        if (replacementKey is not null)
        {
            var index = values.IndexOf(replacementKey);
            values[index + 1] = replacementValue!;
        }

        return values.ToArray();
    }
}
