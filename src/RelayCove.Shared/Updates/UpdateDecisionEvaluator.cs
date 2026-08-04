namespace RelayCove.Shared.Updates;

public static class UpdateDecisionEvaluator
{
    public static UpdateDecisionKind Evaluate(string currentVersion, UpdateManifestDto manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!SemanticVersion.TryParse(currentVersion, out var current))
        {
            throw new ArgumentException("Current version is invalid.", nameof(currentVersion));
        }

        if (!UpdateManifestValidator.TryValidate(manifest, out var error))
        {
            throw new ArgumentException(error, nameof(manifest));
        }

        var target = SemanticVersion.Parse(manifest.Version);
        var minimumSupported = SemanticVersion.Parse(manifest.MinimumSupportedVersion);
        if (current.CompareTo(minimumSupported) < 0)
        {
            return UpdateDecisionKind.Unsupported;
        }

        if (target.CompareTo(current) <= 0)
        {
            return UpdateDecisionKind.None;
        }

        return manifest.Mandatory ? UpdateDecisionKind.Mandatory : UpdateDecisionKind.Optional;
    }
}
