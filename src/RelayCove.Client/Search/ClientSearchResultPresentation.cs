using System.Globalization;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Search;

internal sealed record ClientSearchResultPresentation(
    SearchResultDto Result,
    string ConversationAndSender,
    string Timestamp,
    string Snippet,
    string AttachmentLabel,
    bool HasMatchedAttachment,
    int ResultOrdinal)
{
    public string AutomationName =>
        $"打开搜索结果：{ConversationAndSender}，{Timestamp}，结果 {ResultOrdinal}";

    public static ClientSearchResultPresentation Create(SearchResultDto result, int resultOrdinal = 1)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentOutOfRangeException.ThrowIfLessThan(resultOrdinal, 1);
        var hasMatchedAttachment = !string.IsNullOrEmpty(result.MatchedAttachmentFileName);
        var snippet = string.IsNullOrEmpty(result.Snippet)
            ? hasMatchedAttachment
                ? "正文为空；结果由附件名称匹配。"
                : "正文为空。"
            : result.Snippet;
        return new ClientSearchResultPresentation(
            result,
            $"{result.ConversationName} · {result.SenderName}",
            result.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
            snippet,
            hasMatchedAttachment
                ? $"匹配附件：{result.MatchedAttachmentFileName}"
                : string.Empty,
            hasMatchedAttachment,
            resultOrdinal);
    }

    public override string ToString() =>
        $"{nameof(ClientSearchResultPresentation)} {{ Result = [REDACTED], " +
        "ConversationAndSender = [REDACTED], Timestamp = [REDACTED], " +
        "Snippet = [REDACTED], AttachmentLabel = [REDACTED], " +
        $"HasMatchedAttachment = {HasMatchedAttachment} }}";
}
