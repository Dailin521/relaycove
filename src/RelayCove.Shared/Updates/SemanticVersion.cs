using System.Diagnostics.CodeAnalysis;

namespace RelayCove.Shared.Updates;

public sealed class SemanticVersion : IComparable<SemanticVersion>, IComparable
{
    private readonly string[] prereleaseIdentifiers;

    private SemanticVersion(string major, string minor, string patch, string[] prereleaseIdentifiers)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        this.prereleaseIdentifiers = prereleaseIdentifiers;
    }

    public string Major { get; }

    public string Minor { get; }

    public string Patch { get; }

    public bool IsPrerelease => prereleaseIdentifiers.Length > 0;

    public static SemanticVersion Parse(string value)
    {
        if (!TryParse(value, out var version))
        {
            throw new ArgumentException("Version must be major.minor.patch optionally followed by a SemVer prerelease identifier.", nameof(value));
        }

        return version;
    }

    public static bool TryParse(string? value, [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(value) || value.Length > UpdateConstants.MaximumVersionLength)
        {
            return false;
        }

        var separator = value.IndexOf('-');
        var core = separator < 0 ? value : value[..separator];
        var prerelease = separator < 0 ? null : value[(separator + 1)..];
        var coreParts = core.Split('.');
        if (coreParts.Length != 3 || !TryParseNumericIdentifier(coreParts[0]) ||
            !TryParseNumericIdentifier(coreParts[1]) || !TryParseNumericIdentifier(coreParts[2]))
        {
            return false;
        }

        var identifiers = Array.Empty<string>();
        if (prerelease is not null)
        {
            if (prerelease.Length == 0)
            {
                return false;
            }

            identifiers = prerelease.Split('.');
            foreach (var identifier in identifiers)
            {
                if (!IsValidPrereleaseIdentifier(identifier))
                {
                    return false;
                }
            }
        }

        version = new SemanticVersion(coreParts[0], coreParts[1], coreParts[2], identifiers);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = CompareNumericIdentifier(Major, other.Major);
        if (result != 0)
        {
            return result;
        }

        result = CompareNumericIdentifier(Minor, other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = CompareNumericIdentifier(Patch, other.Patch);
        if (result != 0 || (!IsPrerelease && !other.IsPrerelease))
        {
            return result;
        }

        if (!IsPrerelease)
        {
            return 1;
        }

        if (!other.IsPrerelease)
        {
            return -1;
        }

        var commonLength = Math.Min(prereleaseIdentifiers.Length, other.prereleaseIdentifiers.Length);
        for (var index = 0; index < commonLength; index++)
        {
            result = ComparePrereleaseIdentifier(prereleaseIdentifiers[index], other.prereleaseIdentifiers[index]);
            if (result != 0)
            {
                return result;
            }
        }

        return prereleaseIdentifiers.Length.CompareTo(other.prereleaseIdentifiers.Length);
    }

    public int CompareTo(object? obj)
    {
        return obj switch
        {
            null => 1,
            SemanticVersion version => CompareTo(version),
            _ => throw new ArgumentException("Object must be a SemanticVersion.", nameof(obj)),
        };
    }

    public override string ToString()
    {
        var core = $"{Major}.{Minor}.{Patch}";
        return IsPrerelease ? $"{core}-{string.Join('.', prereleaseIdentifiers)}" : core;
    }

    private static bool TryParseNumericIdentifier(string value)
    {
        if (value.Length == 0 || (value.Length > 1 && value[0] == '0'))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidPrereleaseIdentifier(string value)
    {
        if (value.Length == 0 || (value.Length > 1 && value[0] == '0' && IsNumeric(value)))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!(character is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static int ComparePrereleaseIdentifier(string left, string right)
    {
        var leftNumeric = IsNumeric(left);
        var rightNumeric = IsNumeric(right);
        if (leftNumeric && rightNumeric)
        {
            var lengthComparison = left.Length.CompareTo(right.Length);
            return lengthComparison != 0 ? lengthComparison : string.CompareOrdinal(left, right);
        }

        if (leftNumeric)
        {
            return -1;
        }

        if (rightNumeric)
        {
            return 1;
        }

        return string.CompareOrdinal(left, right);
    }

    private static int CompareNumericIdentifier(string left, string right)
    {
        var lengthComparison = left.Length.CompareTo(right.Length);
        return lengthComparison != 0 ? lengthComparison : string.CompareOrdinal(left, right);
    }

    private static bool IsNumeric(string value)
    {
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return value.Length > 0;
    }
}
