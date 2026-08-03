using RelayCove.Server.Data.Entities;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Services;

internal static class MessageDtoFactory
{
    public static MessageDto Create(Message message, string senderDisplayName) =>
        new(
            message.Id,
            message.ClientMessageId,
            message.ConversationId,
            message.SenderId,
            senderDisplayName,
            message.Type,
            message.Content,
            message.ReplyToMessageId,
            Array.Empty<AttachmentDto>(),
            message.Mentions
                .Select(mention => mention.MentionedUserId)
                .Order()
                .ToArray(),
            new DateTimeOffset(message.CreatedAt));
}
