using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using RelayCove.Server.Data.Entities;

namespace RelayCove.Server.Services;

public sealed class PasswordService
{
    private const string DummyPassword = "RelayCove-Dummy-Password-Only-For-Timing";
    private readonly IPasswordHasher<User> passwordHasher;
    private readonly User dummyUser = User.CreatePasswordHashSubject();
    private readonly string dummyPasswordHash;

    public PasswordService(IPasswordHasher<User> passwordHasher)
    {
        this.passwordHasher = passwordHasher;
        dummyPasswordHash = passwordHasher.HashPassword(dummyUser, DummyPassword);
    }

    public string HashPassword(User user, string password)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrEmpty(password);

        return passwordHasher.HashPassword(user, password);
    }

    public PasswordVerificationOutcome VerifyPassword(User user, string? passwordHash, string? password)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrEmpty(passwordHash) || password is null)
        {
            return PasswordVerificationOutcome.Failed;
        }

        try
        {
            return passwordHasher.VerifyHashedPassword(user, passwordHash, password) switch
            {
                PasswordVerificationResult.Success => PasswordVerificationOutcome.Success,
                PasswordVerificationResult.SuccessRehashNeeded => PasswordVerificationOutcome.SuccessRehashNeeded,
                _ => PasswordVerificationOutcome.Failed,
            };
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or CryptographicException)
        {
            return PasswordVerificationOutcome.Failed;
        }
    }

    public void VerifyDummyPassword(string? password)
    {
        _ = passwordHasher.VerifyHashedPassword(dummyUser, dummyPasswordHash, password ?? string.Empty);
    }
}
