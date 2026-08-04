using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RelayCove.Server.Tests.Packaging;

public sealed partial class ReleaseTemplateTests
{
    private const long MinimumMaximumFileBytes = 1L * 1024 * 1024;
    private const long AbsoluteMaximumFileBytes = 100L * 1024 * 1024;
    private const long MultipartOverheadBytes = 64L * 1024;

    [Fact]
    public void DeploymentMaterials_WhenInspected_HaveFrozenNames()
    {
        var requiredPaths = new[]
        {
            PackagingTestPaths.GetRepositoryPath("installer", "linux", "relaycove.service"),
            PackagingTestPaths.GetRepositoryPath("installer", "linux", "nginx.conf"),
            PackagingTestPaths.GetRepositoryPath("installer", "linux", "appsettings.Production.example.json"),
            PackagingTestPaths.GetRepositoryPath("installer", "linux", "relaycove.env.example"),
            PackagingTestPaths.GetRepositoryPath("docs", "deployment.md"),
        };

        foreach (var path in requiredPaths)
        {
            Assert.True(File.Exists(path), $"Required release material is missing: {path}");
        }
    }

    [Fact]
    public void SystemdUnit_WhenInspected_UsesRestrictedProductionBoundary()
    {
        var unit = ReadRepositoryText("installer", "linux", "relaycove.service");

        AssertDirective(unit, "User", "relaycove");
        AssertDirective(unit, "Group", "relaycove");
        AssertDirective(unit, "WorkingDirectory", "/opt/relaycove/current/app");
        AssertDirective(unit, "EnvironmentFile", "/etc/relaycove/relaycove.env");
        AssertDirective(unit, "StateDirectory", "relaycove");
        AssertDirective(unit, "UMask", "0077");
        AssertDirective(unit, "ReadOnlyPaths", "/var/lib/relaycove/updates");
        Assert.Contains("ExecStart=/opt/relaycove/current/app/RelayCove.Server", unit, StringComparison.Ordinal);
        Assert.Contains("ASPNETCORE_URLS=http://127.0.0.1:", unit, StringComparison.Ordinal);
        Assert.True(
            unit.Contains("Restart=on-failure", StringComparison.Ordinal) ||
            unit.Contains("Restart=always", StringComparison.Ordinal),
            "The service must declare an automatic restart policy.");
        Assert.Matches(BoundedRestartSecondsRegex(), unit);
        Assert.Matches(BoundedStopSecondsRegex(), unit);
        Assert.DoesNotContain("User=root", unit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0.0.0.0", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void NginxTemplate_WhenInspected_PreservesTlsWebSocketsAndUploadBoundary()
    {
        var nginx = ReadRepositoryText("installer", "linux", "nginx.conf");

        Assert.Matches(HttpsListenRegex(), nginx);
        Assert.True(
            LoopbackProxyRegex().IsMatch(nginx) ||
            (LoopbackUpstreamRegex().IsMatch(nginx) && NamedUpstreamProxyRegex().IsMatch(nginx)),
            "Nginx must proxy only to a loopback Kestrel endpoint.");
        Assert.Contains("proxy_http_version 1.1", nginx, StringComparison.Ordinal);
        Assert.Matches(UpgradeHeaderRegex(), nginx);
        Assert.Matches(ConnectionHeaderRegex(), nginx);
        Assert.DoesNotContain("proxy_pass http://0.0.0.0", nginx, StringComparison.OrdinalIgnoreCase);

        var hubLocation = ExtractNginxLocation(nginx, "/hubs/chat");
        Assert.Matches(@"(?m)^\s*access_log\s+off\s*;\s*$", hubLocation);

        var bodySizeMatch = BodySizeRegex().Match(nginx);
        Assert.True(bodySizeMatch.Success, "nginx.conf must set client_max_body_size explicitly.");

        var configuredBytes = ParseNginxSize(
            bodySizeMatch.Groups["size"].Value,
            bodySizeMatch.Groups["unit"].Value);
        Assert.True(
            configuredBytes >= AbsoluteMaximumFileBytes + MultipartOverheadBytes,
            $"client_max_body_size is {configuredBytes} bytes; it must allow at least " +
            $"{AbsoluteMaximumFileBytes + MultipartOverheadBytes} bytes.");
    }

    [Fact]
    public void ProductionJson_WhenParsed_UsesRealKeysWithoutSecrets()
    {
        var path = PackagingTestPaths.GetRepositoryPath(
            "installer",
            "linux",
            "appsettings.Production.example.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var connectionString = GetRequiredProperty(root, "ConnectionStrings", "Default").GetString();
        var uploadsPath = GetRequiredProperty(root, "Storage", "UploadsPath").GetString();
        var maximumFileBytes = GetRequiredProperty(root, "Uploads", "MaximumFileBytes").GetInt64();
        var updateManifestPath = GetRequiredProperty(root, "Update", "ManifestPath").GetString();
        var bootstrapEnabled = GetRequiredProperty(root, "BootstrapAdmin", "Enabled").GetBoolean();

        Assert.Equal(
            "Data Source=/var/lib/relaycove/relaycove.db;Foreign Keys=True;Default Timeout=5",
            connectionString);
        Assert.StartsWith("/var/lib/relaycove/", uploadsPath, StringComparison.Ordinal);
        Assert.InRange(maximumFileBytes, MinimumMaximumFileBytes, AbsoluteMaximumFileBytes);
        Assert.Equal("/var/lib/relaycove/updates/manifest.json", updateManifestPath);
        Assert.False(bootstrapEnabled);
        Assert.False(TryGetProperty(root, out _, "Authentication", "SigningKey"));
        Assert.False(TryGetProperty(root, out _, "BootstrapAdmin", "Password"));
        Assert.False(root.TryGetProperty("RelayCove", out _));
    }

    [Fact]
    public void EnvironmentExample_WhenInspected_DeclaresSecretsAsEmptyPlaceholders()
    {
        var environment = ReadRepositoryText("installer", "linux", "relaycove.env.example");
        var assignments = ParseEnvironmentAssignments(environment);

        Assert.True(assignments.TryGetValue("Authentication__SigningKey", out var signingKey));
        Assert.True(string.IsNullOrWhiteSpace(signingKey) || IsDocumentedPlaceholder(signingKey));
        Assert.False(assignments.ContainsKey("BootstrapAdmin__Enabled"),
            "Bootstrap must not be active in the distributed environment example.");

        foreach (var credentialName in new[]
                 {
                     "BootstrapAdmin__UserName",
                     "BootstrapAdmin__DisplayName",
                     "BootstrapAdmin__Password",
                 })
        {
            if (assignments.TryGetValue(credentialName, out var credentialValue))
            {
                Assert.True(
                    string.IsNullOrWhiteSpace(credentialValue) || IsDocumentedPlaceholder(credentialValue),
                    $"{credentialName} must not contain a committed credential.");
            }
        }

        Assert.DoesNotContain("BEGIN PRIVATE KEY", environment, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Data Source=relaycove.db", environment, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeploymentGuide_WhenInspected_MigratesExplicitReleaseBeforeAtomicSwitch()
    {
        var guide = ReadRepositoryText("docs", "deployment.md");
        var configuredMigration = guide.IndexOf(
            "ConnectionStrings__Default='Data Source=/var/lib/relaycove/relaycove.db;Foreign Keys=True;Default Timeout=5'",
            StringComparison.Ordinal);
        var explicitMigration = configuredMigration < 0
            ? -1
            : guide.IndexOf(
                "\"$release_root/migrate/RelayCove.Migrations\"",
                configuredMigration,
                StringComparison.Ordinal);
        var atomicSwitch = guide.IndexOf(
            "mv -Tf /opt/relaycove/current.next /opt/relaycove/current",
            StringComparison.Ordinal);
        var serviceStart = guide.IndexOf(
            "systemctl start relaycove.service",
            StringComparison.Ordinal);

        Assert.True(explicitMigration >= 0, "The guide must invoke the new release's explicit migration bundle.");
        Assert.True(
            configuredMigration >= 0 && configuredMigration < explicitMigration,
            "The migration host must receive its required default connection string before the bundle starts.");
        Assert.True(atomicSwitch > explicitMigration, "The active link must change only after migration succeeds.");
        Assert.True(serviceStart > atomicSwitch, "The service must start only after the atomic active-link switch.");
        Assert.DoesNotContain("/opt/relaycove/current/migrate/", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void DeploymentGuide_WhenInspected_PublishesCompleteBackupBeforeRecoverableRestore()
    {
        var guide = ReadRepositoryText("docs", "deployment.md");
        Assert.Contains(
            "install -d -o root -g relaycove -m 0750 /var/lib/relaycove/updates",
            guide,
            StringComparison.Ordinal);
        var backupIntegrity = guide.IndexOf("BACKUP.SHA256", StringComparison.Ordinal);
        var backupComplete = guide.IndexOf("BACKUP.COMPLETE", backupIntegrity, StringComparison.Ordinal);
        var backupPublish = guide.IndexOf("mv -T \"$backup_staging\" \"$backup_root\"", StringComparison.Ordinal);
        var restoreValidation = guide.LastIndexOf("sha256sum -c BACKUP.SHA256", StringComparison.Ordinal);
        var restoreQuarantine = guide.IndexOf("sudo mv \"/var/lib/relaycove/$state_item\"", StringComparison.Ordinal);
        var quarantineLoopEnd = guide.IndexOf("\ndone", restoreQuarantine, StringComparison.Ordinal);
        var restoreCopy = guide.IndexOf("sudo cp -a \"$backup_root/$state_item\"", StringComparison.Ordinal);

        Assert.True(backupIntegrity >= 0 && backupComplete > backupIntegrity);
        Assert.True(backupPublish > backupComplete, "An incomplete backup must not receive its final path.");
        Assert.True(restoreValidation > backupPublish);
        Assert.True(restoreQuarantine > restoreValidation, "Restore must validate before moving current state.");
        Assert.True(restoreCopy > quarantineLoopEnd, "All current state must be quarantined before any backup item is copied.");
        Assert.DoesNotContain("sudo rm -f /var/lib/relaycove/relaycove.db", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void DeploymentGuide_WhenInspected_PublishesVerifiedArtifactBeforeManifest()
    {
        var guide = ReadRepositoryText("docs", "deployment.md");
        var artifactVerification = guide.IndexOf("actual_artifact_sha256", StringComparison.Ordinal);
        var artifactPublish = guide.IndexOf("mv -Tf \"$update_root/.$artifact_name.next\"", StringComparison.Ordinal);
        var manifestPublish = guide.IndexOf("mv -Tf \"$update_root/.manifest.json.next\"", StringComparison.Ordinal);

        Assert.True(artifactVerification >= 0);
        Assert.True(artifactPublish > artifactVerification);
        Assert.True(manifestPublish > artifactPublish, "The manifest must be published after its exact artifact.");
    }

    private static string ReadRepositoryText(params string[] segments) =>
        File.ReadAllText(PackagingTestPaths.GetRepositoryPath(segments));

    private static string ExtractNginxLocation(string content, string path)
    {
        var marker = $"location {path}";
        var markerIndex = content.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"Missing Nginx location: {path}");
        var openingBrace = content.IndexOf('{', markerIndex);
        var closingBrace = content.IndexOf('}', openingBrace + 1);
        Assert.True(openingBrace >= 0 && closingBrace > openingBrace, $"Invalid Nginx location: {path}");
        return content[(openingBrace + 1)..closingBrace];
    }

    private static void AssertDirective(string content, string name, string expectedValue)
    {
        Assert.Matches(
            $"(?m)^\\s*{Regex.Escape(name)}\\s*=\\s*{Regex.Escape(expectedValue)}\\s*$",
            content);
    }

    private static long ParseNginxSize(string sizeText, string unitText)
    {
        var size = long.Parse(sizeText, CultureInfo.InvariantCulture);
        var multiplier = unitText.ToLowerInvariant() switch
        {
            "" => 1L,
            "k" => 1024L,
            "m" => 1024L * 1024,
            "g" => 1024L * 1024 * 1024,
            _ => throw new InvalidDataException($"Unsupported nginx size unit '{unitText}'."),
        };

        return checked(size * multiplier);
    }

    private static JsonElement GetRequiredProperty(JsonElement root, params string[] path)
    {
        Assert.True(TryGetProperty(root, out var value, path), $"Missing JSON key: {string.Join(':', path)}");
        return value;
    }

    private static bool TryGetProperty(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var segment in path)
        {
            if (value.ValueKind is not JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyDictionary<string, string> ParseEnvironmentAssignments(string content)
    {
        return content.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.Ordinal);
    }

    private static bool IsDocumentedPlaceholder(string value) =>
        value.Contains("CHANGE", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("REPLACE", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("GENERATE", StringComparison.OrdinalIgnoreCase) ||
        value.Contains('<') ||
        value.Contains("${", StringComparison.Ordinal);

    [GeneratedRegex(@"(?m)^\s*RestartSec\s*=\s*(?:[1-9]|[1-5][0-9])s?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BoundedRestartSecondsRegex();

    [GeneratedRegex(@"(?m)^\s*TimeoutStopSec\s*=\s*(?:[1-9]|[1-9][0-9]|1[0-7][0-9]|180)s?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BoundedStopSecondsRegex();

    [GeneratedRegex(@"(?m)^\s*listen\s+443(?:\s+[^;]*)?\bssl\b[^;]*;", RegexOptions.IgnoreCase)]
    private static partial Regex HttpsListenRegex();

    [GeneratedRegex(@"(?m)^\s*proxy_pass\s+http://127\.0\.0\.1:\d+\s*;", RegexOptions.IgnoreCase)]
    private static partial Regex LoopbackProxyRegex();

    [GeneratedRegex(@"(?m)^\s*server\s+127\.0\.0\.1:\d+\s*;", RegexOptions.IgnoreCase)]
    private static partial Regex LoopbackUpstreamRegex();

    [GeneratedRegex(@"(?m)^\s*proxy_pass\s+http://relaycove_server\s*;", RegexOptions.IgnoreCase)]
    private static partial Regex NamedUpstreamProxyRegex();

    [GeneratedRegex(@"(?m)^\s*proxy_set_header\s+Upgrade\s+\$http_upgrade\s*;", RegexOptions.IgnoreCase)]
    private static partial Regex UpgradeHeaderRegex();

    [GeneratedRegex(@"(?m)^\s*proxy_set_header\s+Connection\s+(?:""upgrade""|\$[A-Za-z0-9_]*connection_upgrade)\s*;", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionHeaderRegex();

    [GeneratedRegex(@"(?m)^\s*client_max_body_size\s+(?<size>\d+)\s*(?<unit>[kmg]?)\s*;", RegexOptions.IgnoreCase)]
    private static partial Regex BodySizeRegex();
}
