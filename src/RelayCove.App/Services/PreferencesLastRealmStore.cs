namespace RelayCove.App.Services;

public sealed class PreferencesLastRealmStore : ILastRealmStore
{
    public const string DefaultRealm = "https://hklight.2000521.xyz";
    private const string PreferenceKey = "relaycove.last-realm";

    public string Get()
    {
        var value = Preferences.Default.Get(PreferenceKey, DefaultRealm);
        return string.IsNullOrWhiteSpace(value) ? DefaultRealm : value;
    }

    public void Set(string realm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        Preferences.Default.Set(PreferenceKey, realm.Trim());
    }
}
