namespace RelayCove.Core;

public sealed record UserPatchedEvent(
    long UserId,
    string? FullName,
    string? Email,
    bool? IsActive,
    long? EventId = null,
    DomainEventSource Source = DomainEventSource.Realtime,
    bool HasAvatar = false,
    string? AvatarUrl = null,
    int? AvatarVersion = null,
    UserAvatarSource AvatarSource = UserAvatarSource.Unknown) : DomainEvent(EventId, Source);
