namespace RelayCove.Client.Search;

internal sealed record ClientSearchScopeOption(
    ClientSearchScope Scope,
    string Label)
{
    public override string ToString() => Label;
}
