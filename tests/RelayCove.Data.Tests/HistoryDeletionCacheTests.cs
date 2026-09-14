using RelayCove.Core;

namespace RelayCove.Data.Tests;

public sealed class HistoryDeletionCacheTests
{
    [Fact]
    public async Task ApplyBatchAsync_WhenHistoryConfirmsDeletedMessages_PersistsRemovalWithoutReducingAuthoritativeUnread()
    {
        await using var context = StoreTestContext.Create();
        var account = StoreTestData.Account();
        var conversation = new DirectMessage([20]);
        await context.Store.InitializeAsync(account);
        await context.Store.StoreMessagePageAsync(account.AccountId,
            [StoreTestData.Message(1, conversation), StoreTestData.Message(2, conversation)]);
        await context.Store.ReplaceRegisterSnapshotAsync(account.AccountId, StoreTestData.Register([], unread:
            new UnreadState(new Dictionary<string, int> { [conversation.CanonicalKey] = 1 }, 1)));

        await context.Store.ApplyBatchAsync(account.AccountId,
            [new MessageDeletedEvent([2], Source: DomainEventSource.History),
             new MessageUpsertEvent(StoreTestData.Message(1, conversation) with { IsRead = true }, Source: DomainEventSource.History)]);

        var loaded = (await context.Store.LoadAsync(account.AccountId))!;
        var message = Assert.Single(await context.Store.QueryMessagesAsync(account.AccountId, conversation, null, 50));
        Assert.Equal(1, message.Id);
        Assert.Equal(1, loaded.State.Unread.Total);
        Assert.True(message.IsRead);
        Assert.Equal(1, Assert.Single(await context.Store.QueryConversationSummariesAsync(account.AccountId)).LatestMessage.Id);
        await context.Store.ApplyBatchAsync(account.AccountId, [new MessageDeletedEvent([1], Source: DomainEventSource.History)]);
        Assert.Empty(await context.Store.QueryMessagesAsync(account.AccountId, conversation, null, 50));
        Assert.Empty(await context.Store.QueryConversationSummariesAsync(account.AccountId));
    }
}
