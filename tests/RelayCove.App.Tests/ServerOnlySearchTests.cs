using RelayCove.Core;

namespace RelayCove.App.Tests;

public sealed partial class ShellViewModelTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("needle", false)]
    [InlineData("needle", true)]
    public async Task ConversationSearch_WhenServerHasNoResultsOrFails_DoesNotShowCachedMatches(string query, bool fail)
    {
        var conversation = new DirectMessage([20]);
        var session = new FakeSession
        {
            Selected = conversation,
            StateValue = new ClientState(messages: new Dictionary<long, ChatMessage>
            {
                [10] = new(10, conversation, 20, "needle cached", DateTimeOffset.UnixEpoch)
            }),
            SearchMessagesAction = (_, _, _, _) => fail
                ? Task.FromException<MessageQueryPage>(new GatewayException(GatewayErrorKind.Offline, GatewayErrorCode.RequestTimedOut))
                : Task.FromResult(new MessageQueryPage([], true, true, true))
        };
        using var shell = CreateViewModel(session);
        shell.OpenConversationSearchCommand.Execute(null);
        shell.SearchQuery = query;
        Assert.Empty(shell.SearchResults);
        await shell.SearchNowCommand.ExecuteAsync(null);
        Assert.Single(session.SearchRequests);
        Assert.Empty(shell.SearchResults);
        session.Publish();
        Assert.Empty(shell.SearchResults);
        Assert.Equal(fail, shell.HasSearchError);
        Assert.Equal(fail ? "搜索未完成，请重试。" : "没有匹配结果。", shell.SearchEmptyText);
    }

    [Fact]
    public async Task ConversationSearch_WhenResubmitted_ClearsPreviousResultsAndShowsOnlyServerPage()
    {
        var conversation = new DirectMessage([20]);
        var session = new FakeSession
        {
            Selected = conversation,
            StateValue = new ClientState(messages: new Dictionary<long, ChatMessage>
            {
                [10] = new(10, conversation, 20, "needle cached", DateTimeOffset.UnixEpoch)
            }),
            SearchMessagesAction = (_, _, _, _) => Task.FromResult(new MessageQueryPage(
                [new(20, conversation, 20, "needle server", DateTimeOffset.UnixEpoch)], false, true, true))
        };
        using var shell = CreateViewModel(session);
        shell.OpenConversationSearchCommand.Execute(null);
        shell.SearchQuery = "needle";
        await shell.SearchNowCommand.ExecuteAsync(null);
        Assert.Equal(20, Assert.Single(shell.SearchResults).MessageId);
        var pending = new TaskCompletionSource<MessageQueryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SearchMessagesAction = (_, _, _, _) => pending.Task;
        var search = shell.SearchNowCommand.ExecuteAsync(null);
        Assert.True(shell.IsSearchBusy);
        Assert.Empty(shell.SearchResults);
        Assert.False(shell.HasMoreSearchResults);
        pending.SetException(new GatewayException(GatewayErrorKind.Offline, GatewayErrorCode.RequestTimedOut));
        await search;
        Assert.True(shell.HasSearchError);
        Assert.Empty(shell.SearchResults);
    }
}
