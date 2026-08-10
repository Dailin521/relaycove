using System.Security.Cryptography;
using System.Text;

namespace RelayCove.Core;

public readonly record struct AccountId
{
    private AccountId(string value) => Value = value;

    public string Value { get; }

    public static AccountId Create(RealmEndpoint realm, long userId)
    {
        ArgumentNullException.ThrowIfNull(realm);
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        var input = Encoding.UTF8.GetBytes($"{realm.AbsoluteUri}\n{userId}");
        var hash = SHA256.HashData(input);
        return new AccountId(Convert.ToHexStringLower(hash));
    }

    public override string ToString() => Value;
}
