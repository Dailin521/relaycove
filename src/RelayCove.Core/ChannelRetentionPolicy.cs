namespace RelayCove.Core;

public sealed record ChannelRetentionPolicy(ChannelRetentionKind Kind, int? Days = null)
{
    public static ChannelRetentionPolicy RealmDefault { get; } = new(ChannelRetentionKind.RealmDefault);
    public static ChannelRetentionPolicy Unlimited { get; } = new(ChannelRetentionKind.Unlimited);
    public static ChannelRetentionPolicy ForDays(int days) => days > 0 ? new(ChannelRetentionKind.Days, days) : throw new ArgumentOutOfRangeException(nameof(days));
}
