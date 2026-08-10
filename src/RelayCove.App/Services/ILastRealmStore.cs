namespace RelayCove.App.Services;

public interface ILastRealmStore
{
    string Get();
    void Set(string realm);
}
