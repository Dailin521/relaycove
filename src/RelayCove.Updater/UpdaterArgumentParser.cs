using System.Globalization;
using System.Security.Cryptography;
using RelayCove.Shared.Updates;

namespace RelayCove.Updater;

internal static class UpdaterArgumentParser
{
    internal const string HelpText = "RelayCove Updater: apply a verified portable package.";

    internal static bool IsHelp(string[] args) => args.Length == 1 && (args[0] == "--help" || args[0] == "-h");

    internal static bool TryParse(string[] args, out UpdaterOptions? options)
    {
        options = null;
        if (args.Length < 2 || !string.Equals(args[0], "apply", StringComparison.Ordinal))
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var bootstrapped = false;
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--bootstrapped", StringComparison.Ordinal))
            {
                if (bootstrapped)
                {
                    return false;
                }

                bootstrapped = true;
                continue;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length || values.ContainsKey(argument))
            {
                return false;
            }

            var value = args[++index];
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            values.Add(argument, value);
        }

        var required = new[]
        {
            "--archive", "--expected-sha256", "--expected-size", "--expected-version", "--current-version",
            "--target", "--wait-pid", "--wait-start-time-utc-ticks",
        };
        if (values.Keys.Except(required.Append("--wait-timeout-seconds"), StringComparer.Ordinal).Any() || required.Any(key => !values.ContainsKey(key)))
        {
            return false;
        }

        if (!long.TryParse(values["--expected-size"], NumberStyles.None, CultureInfo.InvariantCulture, out var expectedSize) || expectedSize <= 0 ||
            !int.TryParse(values["--wait-pid"], NumberStyles.None, CultureInfo.InvariantCulture, out var waitProcessId) || waitProcessId <= 0 ||
            !long.TryParse(values["--wait-start-time-utc-ticks"], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0 ||
            !SemanticVersion.TryParse(values["--expected-version"], out var expectedVersion) ||
            !SemanticVersion.TryParse(values["--current-version"], out var currentVersion) ||
            !IsLowerSha256(values["--expected-sha256"]))
        {
            return false;
        }

        var timeout = 60;
        if (values.TryGetValue("--wait-timeout-seconds", out var suppliedTimeout) &&
            (!int.TryParse(suppliedTimeout, NumberStyles.None, CultureInfo.InvariantCulture, out timeout) || timeout is < 1 or > 300))
        {
            return false;
        }

        if (!Path.IsPathFullyQualified(values["--archive"]) || !Path.IsPathFullyQualified(values["--target"]) || expectedVersion.CompareTo(currentVersion) <= 0)
        {
            return false;
        }

        options = new UpdaterOptions
        {
            ArchivePath = Path.GetFullPath(values["--archive"]),
            ExpectedSha256 = values["--expected-sha256"],
            ExpectedSize = expectedSize,
            ExpectedVersion = expectedVersion,
            CurrentVersion = currentVersion,
            TargetPath = Path.GetFullPath(values["--target"]),
            WaitProcessId = waitProcessId,
            WaitProcessStartTimeUtcTicks = ticks,
            WaitTimeoutSeconds = timeout,
            Bootstrapped = bootstrapped,
        };
        return true;
    }

    private static bool IsLowerSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromHexString(value);
            return bytes.Length == 32 && CryptographicOperations.FixedTimeEquals(bytes, Convert.FromHexString(value.ToLowerInvariant())) && value == value.ToLowerInvariant();
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
