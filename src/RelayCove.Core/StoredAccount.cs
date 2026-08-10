namespace RelayCove.Core;

public sealed record StoredAccount(AccountId AccountId, RealmEndpoint Realm, string Email, long UserId);
