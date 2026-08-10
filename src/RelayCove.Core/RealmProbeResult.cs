namespace RelayCove.Core;

public sealed record RealmProbeResult(
    RealmEndpoint Realm,
    string ServerVersion,
    int FeatureLevel,
    bool IsIncompatible,
    bool EmailAuthenticationEnabled)
{
    public const int MinimumFeatureLevel = 500;

    public bool IsCompatible =>
        !IsIncompatible &&
        FeatureLevel >= MinimumFeatureLevel &&
        EmailAuthenticationEnabled;
}
