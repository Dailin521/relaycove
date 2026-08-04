namespace RelayCove.Shared.Updates;

public static class UpdateManifestValidator
{
    public static bool TryValidate(UpdateManifestDto? manifest, out string error)
    {
        if (manifest is null)
        {
            error = "Update manifest is required.";
            return false;
        }

        if (manifest.SchemaVersion != UpdateConstants.SchemaVersion)
        {
            error = "Update manifest schema version is unsupported.";
            return false;
        }

        if (!string.Equals(manifest.Channel, UpdateConstants.Channel, StringComparison.Ordinal))
        {
            error = "Update manifest channel is unsupported.";
            return false;
        }

        if (!SemanticVersion.TryParse(manifest.Version, out var version) ||
            !SemanticVersion.TryParse(manifest.MinimumSupportedVersion, out var minimumSupportedVersion))
        {
            error = "Update manifest contains an invalid version.";
            return false;
        }

        if (version.CompareTo(minimumSupportedVersion) < 0)
        {
            error = "Update manifest version must not be below the minimum supported version.";
            return false;
        }

        if (manifest.Artifact is null ||
            !string.Equals(manifest.Artifact.Type, UpdateConstants.ArtifactTypePortableZip, StringComparison.Ordinal))
        {
            error = "Update manifest artifact type is unsupported.";
            return false;
        }

        if (!Uri.TryCreate(manifest.Artifact.Url, UriKind.Absolute, out var artifactUri) ||
            !string.Equals(artifactUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(artifactUri.Host) ||
            !string.IsNullOrEmpty(artifactUri.UserInfo) ||
            !string.IsNullOrEmpty(artifactUri.Fragment))
        {
            error = "Update manifest artifact URL must be an absolute HTTPS URL without user info or fragment.";
            return false;
        }

        if (manifest.Artifact.SizeBytes is < 1 or > UpdateConstants.MaximumArtifactBytes)
        {
            error = "Update manifest artifact size is outside the supported range.";
            return false;
        }

        if (manifest.Artifact.Sha256 is null || manifest.Artifact.Sha256.Length != 64 ||
            !manifest.Artifact.Sha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            error = "Update manifest artifact SHA-256 must be lowercase hexadecimal.";
            return false;
        }

        if (manifest.ReleaseNotes is null || manifest.ReleaseNotes.Length > UpdateConstants.MaximumReleaseNotesLength)
        {
            error = "Update manifest release notes exceed the supported length.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
