using System.Buffers;
using System.Globalization;
using System.Text;
using RelayCove.Server.Data.Entities;
using RelayCove.Shared.Conversations;

namespace RelayCove.Server.Services;

public sealed class ConversationRequestValidator
{
    public IReadOnlyDictionary<string, string[]> ValidateCreate(
        CreateConversationRequest? request,
        Guid actorUserId)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors["type"] = ["A conversation type is required."];
            return errors;
        }

        if (!Enum.IsDefined(request.Type))
        {
            errors["type"] = ["The conversation type is invalid."];
            return errors;
        }

        if (request.Type is ConversationType.PublicChannel or ConversationType.PrivateChannel)
        {
            if (!IsValidChannelName(request.Name))
            {
                errors["name"] = ["A valid channel name of at most 100 characters is required."];
            }

            if (request.ParticipantUserId is not null)
            {
                errors["participantUserId"] = ["Channel conversations do not accept a participant user ID."];
            }

            return errors;
        }

        if (request.Name is not null)
        {
            errors["name"] = ["Direct conversations do not accept a name."];
        }

        if (!request.ParticipantUserId.HasValue || request.ParticipantUserId.Value == Guid.Empty)
        {
            errors["participantUserId"] = ["A participant user ID is required."];
        }
        else if (request.ParticipantUserId == actorUserId)
        {
            errors["participantUserId"] = ["Direct conversations require another participant."];
        }

        return errors;
    }

    public IReadOnlyDictionary<string, string[]> ValidateMember(
        UpsertConversationMemberRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null || request.UserId == Guid.Empty)
        {
            errors["userId"] = ["A user ID is required."];
        }

        if (request is null || !Enum.IsDefined(request.Role))
        {
            errors["role"] = ["The conversation member role is invalid."];
        }

        return errors;
    }

    private static bool IsValidChannelName(string? name)
    {
        if (name is null)
        {
            return false;
        }

        var scalarCount = 0;
        var hasNonWhitespace = false;
        var remaining = name.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var charsConsumed);
            if (status != OperationStatus.Done)
            {
                return false;
            }

            scalarCount++;
            if (scalarCount > Conversation.MaximumNameLength ||
                Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
            {
                return false;
            }

            hasNonWhitespace |= !Rune.IsWhiteSpace(rune);
            remaining = remaining[charsConsumed..];
        }

        return scalarCount > 0 && hasNonWhitespace;
    }
}
