using RelayCove.Server.Data.Entities;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Services;

internal static class MessageDtoFactory
{
    public static MessageDto Create(Message message, string senderDisplayName) =>
        Create(message, senderDisplayName, message.Attachments);

    public static MessageDto Create(
        Message message,
        string senderDisplayName,
        IEnumerable<Attachment> attachments) =>
        new(
            message.Id,
            message.ClientMessageId,
            message.ConversationId,
            message.SenderId,
            senderDisplayName,
            message.Type,
            message.Content,
            message.ReplyToMessageId,
            attachments
                .OrderBy(attachment => attachment.Id)
                .Select(AttachmentDtoFactory.Create)
                .ToArray(),
            message.Mentions
                .Select(mention => mention.MentionedUserId)
                .Order()
                .ToArray(),
            new DateTimeOffset(message.CreatedAt));
}
