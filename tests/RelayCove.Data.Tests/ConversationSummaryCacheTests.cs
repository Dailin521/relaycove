using RelayCove.Core;

namespace RelayCove.Data.Tests;

public sealed class ConversationSummaryCacheTests
{
    [Fact]
    public async Task QueryConversationSummariesAsync_WhenMessagesAreEditedMovedAndDeleted_UsesCurrentIndexedRows()
    {
        await using var context = StoreTestContext.Create();
        var account = StoreTestData.Account();
        var source = new ChannelTopic(1, "source");
        var destination = new ChannelTopic(2, "destination");
        await context.Store.InitializeAsync(account);
        await context.Store.ReplaceRegisterSnapshotAsync(account.AccountId, StoreTestData.Register(
            [new Subscription(1, "Source"), new Subscription(2, "Destination")]));

        await context.Store.ApplyBatchAsync(account.AccountId,
        [
            new MessageUpsertEvent(StoreTestData.Message(10, source, content: "old")),
            new MessageUpsertEvent(StoreTestData.Message(20, source, content: "latest")),
            new MessageContentChangedEvent(20, "edited"),
            new MessageMovedEvent([20], destination)
        ]);

        var summaries = await context.Store.QueryConversationSummariesAsync(account.AccountId);
        Assert.Equal(10, summaries.Single(summary => summary.Conversation == source).LatestMessage.Id);
        var moved = summaries.Single(summary => summary.Conversation == destination).LatestMessage;
        Assert.Equal(20, moved.Id);
        Assert.Equal("edited", moved.Content);

        await context.Store.ApplyBatchAsync(account.AccountId, [new MessageDeletedEvent([20])]);

        Assert.DoesNotContain((await context.Store.QueryConversationSummariesAsync(account.AccountId)),
            summary => summary.Conversation == destination);
    }
}
