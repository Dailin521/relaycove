namespace RelayCove.Server.Data.Entities;

public sealed class AppSetting
{
    private AppSetting()
    {
    }

    public AppSetting(string key, string value, DateTime updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        Key = key;
        Value = value;
        UpdatedAt = SqliteValueConverters.NormalizeUtc(updatedAt, nameof(updatedAt));
    }

    public string Key { get; private set; } = string.Empty;

    public string Value { get; private set; } = string.Empty;

    public DateTime UpdatedAt { get; private set; }

    public void SetValue(string value, DateTime updatedAt)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
        UpdatedAt = SqliteValueConverters.NormalizeUtc(updatedAt, nameof(updatedAt));
    }
}
