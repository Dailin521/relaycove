using System.Buffers;
using System.Text;

namespace RelayCove.Server.Services;

public sealed class PasswordPolicy
{
    public const int MinimumLength = 15;
    public const int MaximumLength = 128;

    private static readonly HashSet<string> BlockedPasswords = new(StringComparer.Ordinal)
    {
        "aaaaaaaaaaaaaaa",
        "administrator123",
        "changemechangeme",
        "correcthorsebatterystaple",
        "iloveyouiloveyou",
        "letmeinletmeinletmein",
        "passwordpassword",
        "passwordpassword1",
        "qwertyuiopqwerty",
        "relaycoverelaycove",
        "welcomewelcomewelcome",
    };

    private static readonly string[] CommonSuffixes = ["123", "1234", "2026", "password"];

    public string[] Validate(string? password, string? userName, string? displayName)
    {
        if (password is null)
        {
            return ["The field is required."];
        }

        var errors = new List<string>();
        var scalarCount = 0;
        var containsControlCharacter = false;
        var containsInvalidUnicode = false;
        var remaining = password.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var charsConsumed);
            if (status is not OperationStatus.Done)
            {
                containsInvalidUnicode = true;
                break;
            }

            scalarCount++;
            containsControlCharacter |= Rune.IsControl(rune);
            remaining = remaining[charsConsumed..];
        }

        if (scalarCount is < MinimumLength or > MaximumLength)
        {
            errors.Add($"The password must contain between {MinimumLength} and {MaximumLength} Unicode characters.");
        }

        if (containsControlCharacter)
        {
            errors.Add("The password cannot contain control characters.");
        }

        if (containsInvalidUnicode)
        {
            errors.Add("The password must contain well-formed Unicode text.");
        }

        var canonicalPassword = Canonicalize(password);
        if (BlockedPasswords.Contains(canonicalPassword) ||
            IsContextPassword(canonicalPassword, Canonicalize(userName)) ||
            IsContextPassword(canonicalPassword, Canonicalize(displayName)) ||
            IsContextPassword(canonicalPassword, "relaycove"))
        {
            errors.Add("The password is too common or too closely related to the account.");
        }

        return [.. errors];
    }

    private static bool IsContextPassword(string password, string context)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(context))
        {
            return false;
        }

        if (password == context || password == context + context)
        {
            return true;
        }

        if (password.Length % context.Length == 0)
        {
            var repetitions = password.Length / context.Length;
            if (repetitions >= 2 && string.Concat(Enumerable.Repeat(context, repetitions)) == password)
            {
                return true;
            }
        }

        return CommonSuffixes.Any(suffix => password == context + suffix);
    }

    private static string Canonicalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                builder.Append(Rune.ToLowerInvariant(rune));
            }
        }

        return builder.ToString();
    }
}
