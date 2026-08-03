using RelayCove.Server.Data.Entities;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Services;

public sealed class MessageRequestValidator
{
    public IReadOnlyDictionary<string, string[]> ValidateSend(SendMessageRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors["clientMessageId"] = ["A client message ID is required."];
            errors["conversationId"] = ["A conversation ID is required."];
            errors["type"] = ["A message type is required."];
            return errors;
        }

        if (request.ClientMessageId == Guid.Empty)
        {
            errors["clientMessageId"] = ["A client message ID is required."];
        }

        if (request.ConversationId == Guid.Empty)
        {
            errors["conversationId"] = ["A conversation ID is required."];
        }

        if (!Enum.IsDefined(request.Type))
        {
            errors["type"] = ["The message type is invalid."];
        }
        else if (request.Type == MessageType.Text)
        {
            try
            {
                Message.ValidateContent(request.Type, request.Content);
            }
            catch (ArgumentException)
            {
                errors["content"] =
                    [$"Text content must contain 1 to {Message.MaximumContentLength} valid characters."];
            }
        }

        if (request.ReplyToMessageId is <= 0)
        {
            errors["replyToMessageId"] = ["Reply message IDs must be positive."];
        }

        if (request.AttachmentIds is null)
        {
            errors["attachmentIds"] = ["Attachment IDs are required."];
        }
        else if (request.AttachmentIds.Count > 0)
        {
            errors["attachmentIds"] = ["Attachments are not supported by this endpoint yet."];
        }

        if (request.MentionUserIds is null)
        {
            errors["mentionUserIds"] = ["Mention user IDs are required."];
        }
        else if (request.MentionUserIds.Count > Message.MaximumMentionCount ||
                 request.MentionUserIds.Any(userId => userId == Guid.Empty) ||
                 request.MentionUserIds.Distinct().Count() != request.MentionUserIds.Count)
        {
            errors["mentionUserIds"] =
                [$"Mention user IDs must be unique, non-empty, and contain at most {Message.MaximumMentionCount} items."];
        }

        return errors;
    }

    public IReadOnlyDictionary<string, string[]> ValidateHistory(long? beforeMessageId, int? limit)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (beforeMessageId is <= 0)
        {
            errors["beforeMessageId"] = ["Before-message IDs must be positive."];
        }

        if (limit is < 1 or > 100)
        {
            errors["limit"] = ["The limit must be between 1 and 100."];
        }

        return errors;
    }
}
