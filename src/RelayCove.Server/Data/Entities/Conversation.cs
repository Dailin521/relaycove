using System.Buffers;
using System.Globalization;
using System.Text;
using RelayCove.Shared.Conversations;

namespace RelayCove.Server.Data.Entities;

public sealed class Conversation
{
    public const int MaximumNameLength = 100;

    private Conversation()
    {
    }

    private Conversation(
        Guid id,
        ConversationType type,
        string name,
        Guid createdByUserId,
        DateTime createdAt,
        string? directParticipantKey)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Conversation IDs cannot be empty.", nameof(id));
        }

        if (createdByUserId == Guid.Empty)
        {
            throw new ArgumentException("Creator user IDs cannot be empty.", nameof(createdByUserId));
        }

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), "Conversation types must be defined.");
        }

        Id = id;
        Type = type;
        Name = name;
        CreatedByUserId = createdByUserId;
        CreatedAt = SqliteValueConverters.NormalizeUtc(createdAt, nameof(createdAt));
        UpdatedAt = CreatedAt;
        DirectParticipantKey = directParticipantKey;
    }

    public Guid Id { get; private set; }

    public ConversationType Type { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public Guid? AvatarAttachmentId { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    public bool IsDeleted { get; private set; }

    public string? DirectParticipantKey { get; private set; }

    public User CreatedByUser { get; private set; } = null!;

    public ICollection<ConversationMember> Members { get; } = new List<ConversationMember>();

    public static Conversation CreateChannel(
        Guid id,
        ConversationType type,
        string name,
        Guid createdByUserId,
        DateTime createdAt)
    {
        if (type is not ConversationType.PublicChannel and not ConversationType.PrivateChannel)
        {
            throw new ArgumentOutOfRangeException(nameof(type), "Channel conversations must be public or private.");
        }

        ValidateChannelName(name);
        return new Conversation(id, type, name, createdByUserId, createdAt, directParticipantKey: null);
    }

    public static Conversation CreateDirect(
        Guid id,
        Guid firstParticipantUserId,
        Guid secondParticipantUserId,
        Guid createdByUserId,
        DateTime createdAt)
    {
        var directParticipantKey = CreateDirectParticipantKey(firstParticipantUserId, secondParticipantUserId);
        if (createdByUserId != firstParticipantUserId && createdByUserId != secondParticipantUserId)
        {
            throw new ArgumentException("The creator must be a direct conversation participant.", nameof(createdByUserId));
        }

        return new Conversation(
            id,
            ConversationType.Direct,
            string.Empty,
            createdByUserId,
            createdAt,
            directParticipantKey);
    }

    public void Rename(string name, DateTime updatedAt)
    {
        if (Type == ConversationType.Direct)
        {
            throw new InvalidOperationException("Direct conversation names are derived from their participants.");
        }

        ValidateChannelName(name);
        var normalizedUpdatedAt = NormalizeUpdatedAt(updatedAt);
        Name = name;
        UpdatedAt = normalizedUpdatedAt;
    }

    public void SetAvatarAttachment(Guid? avatarAttachmentId, DateTime updatedAt)
    {
        if (avatarAttachmentId == Guid.Empty)
        {
            throw new ArgumentException("Avatar attachment IDs cannot be empty.", nameof(avatarAttachmentId));
        }

        var normalizedUpdatedAt = NormalizeUpdatedAt(updatedAt);
        AvatarAttachmentId = avatarAttachmentId;
        UpdatedAt = normalizedUpdatedAt;
    }

    public void MarkDeleted(DateTime updatedAt)
    {
        var normalizedUpdatedAt = NormalizeUpdatedAt(updatedAt);
        IsDeleted = true;
        UpdatedAt = normalizedUpdatedAt;
    }

    public void Restore(DateTime updatedAt)
    {
        var normalizedUpdatedAt = NormalizeUpdatedAt(updatedAt);
        IsDeleted = false;
        UpdatedAt = normalizedUpdatedAt;
    }

    internal static string CreateDirectParticipantKey(Guid firstParticipantUserId, Guid secondParticipantUserId)
    {
        if (firstParticipantUserId == Guid.Empty)
        {
            throw new ArgumentException("Participant user IDs cannot be empty.", nameof(firstParticipantUserId));
        }

        if (secondParticipantUserId == Guid.Empty)
        {
            throw new ArgumentException("Participant user IDs cannot be empty.", nameof(secondParticipantUserId));
        }

        if (firstParticipantUserId == secondParticipantUserId)
        {
            throw new ArgumentException("Direct conversations require two distinct participants.", nameof(secondParticipantUserId));
        }

        var first = firstParticipantUserId.ToString("D").ToLowerInvariant();
        var second = secondParticipantUserId.ToString("D").ToLowerInvariant();
        return string.CompareOrdinal(first, second) < 0 ? $"{first}:{second}" : $"{second}:{first}";
    }

    private static void ValidateChannelName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var scalarCount = 0;
        var hasNonWhitespace = false;
        var remaining = name.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var charsConsumed);
            if (status != OperationStatus.Done)
            {
                throw new ArgumentException("Channel names must contain valid Unicode.", nameof(name));
            }

            scalarCount++;
            if (scalarCount > MaximumNameLength)
            {
                throw new ArgumentOutOfRangeException(nameof(name), $"Channel names cannot exceed {MaximumNameLength} characters.");
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Control)
            {
                throw new ArgumentException("Channel names cannot contain control characters.", nameof(name));
            }

            hasNonWhitespace |= !Rune.IsWhiteSpace(rune);
            remaining = remaining[charsConsumed..];
        }

        if (scalarCount == 0 || !hasNonWhitespace)
        {
            throw new ArgumentException("Channel names cannot be empty or whitespace.", nameof(name));
        }
    }

    private DateTime NormalizeUpdatedAt(DateTime value)
    {
        var normalizedValue = SqliteValueConverters.NormalizeUtc(value, nameof(value));
        if (normalizedValue < UpdatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Conversation updates cannot move backward in time.");
        }

        return normalizedValue;
    }
}
