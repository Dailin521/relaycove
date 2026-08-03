namespace RelayCove.Server.Services;

public sealed class NewUserValidator(
    UserNameNormalizer userNameNormalizer,
    PasswordPolicy passwordPolicy)
{
    public const int MaximumDisplayNameLength = 100;

    public IReadOnlyDictionary<string, string[]> Validate(
        string? userName,
        string? displayName,
        string? password)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!userNameNormalizer.TryNormalize(userName, out _))
        {
            errors["userName"] =
            [
                $"The user name must contain {UserNameNormalizer.MinimumLength}-{UserNameNormalizer.MaximumLength} ASCII letters, digits, dots, underscores, or hyphens.",
            ];
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            errors["displayName"] = ["The field is required."];
        }
        else if (displayName.Length > MaximumDisplayNameLength)
        {
            errors["displayName"] = [$"The field cannot exceed {MaximumDisplayNameLength} characters."];
        }

        var passwordErrors = passwordPolicy.Validate(password, userName, displayName);
        if (passwordErrors.Length > 0)
        {
            errors["password"] = passwordErrors;
        }

        return errors;
    }
}
