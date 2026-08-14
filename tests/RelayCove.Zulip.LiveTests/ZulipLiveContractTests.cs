using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RelayCove.Core;
using RelayCove.Zulip.Client;

namespace RelayCove.Zulip.LiveTests;

public sealed class ZulipLiveContractTests
{
    private const string WriteConfirmation = "I_UNDERSTAND_THIS_WRITES_TO_ZULIP";

    [Fact]
    public async Task DedicatedAccounts_WhenExplicitlyAuthorized_ExchangeChannelAndDirectMessages()
    {
        var configuration = LiveConfiguration.Load();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var gateway = new ZulipGateway();

        var probe = await gateway.ProbeRealmAsync(configuration.Realm, timeout.Token);
        Assert.True(probe.IsCompatible);

        var registerA = await gateway.RegisterAsync(new RegisterRequest(configuration.UserA), timeout.Token);
        var registerB = await gateway.RegisterAsync(new RegisterRequest(configuration.UserB), timeout.Token);
        AssertApprovedChannel(registerA, configuration.ChannelId, configuration.ChannelName);
        AssertApprovedChannel(registerB, configuration.ChannelId, configuration.ChannelName);

        var topic = $"run-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}";
        var channel = new ChannelTopic(configuration.ChannelId, topic);
        var queueA = registerA.QueueId;
        var queueB = registerB.QueueId;
        try
        {
            var originalChannelContent = $"RelayCove live channel {topic}";
            var channelResult = await gateway.SendAsync(
                new SendRequest(configuration.UserA, queueA, "1", channel, originalChannelContent),
                timeout.Token);
            var afterChannel = await WaitForMessageAsync(
                gateway,
                configuration.UserB,
                queueB,
                registerB.LastEventId,
                registerB.EventQueueLongPollTimeout,
                channelResult.MessageId,
                timeout.Token);
            var lastEventB = afterChannel.LastEventId;

            Assert.Equal(channel, afterChannel.Message.Conversation);
            var history = await gateway.GetHistoryAsync(
                new HistoryRequest(configuration.UserB, channel, channelResult.MessageId, true, 1),
                timeout.Token);
            Assert.Equal(channelResult.MessageId, Assert.Single(history.Messages).Id);
            await gateway.MarkReadAsync(
                new MarkReadRequest(configuration.UserB, channel, channelResult.MessageId, 1),
                timeout.Token);

            var thumbsUp = new EmojiReactionIdentity("+1", "1f44d", "unicode_emoji");
            await gateway.SetReactionAsync(
                new SetReactionRequest(configuration.UserB, channelResult.MessageId, thumbsUp, true),
                timeout.Token);
            var reactionAdded = await WaitForEventAsync<MessageReactionChangedEvent>(
                gateway,
                configuration.UserB,
                queueB,
                lastEventB,
                registerB.EventQueueLongPollTimeout,
                item => item.MessageId == channelResult.MessageId &&
                    item.Reaction.UserId == configuration.UserB.UserId &&
                    item.Reaction.Identity == thumbsUp && item.Add,
                timeout.Token);
            lastEventB = reactionAdded.LastEventId;

            var editedChannelContent = $"RelayCove live edited channel {topic}";
            var previousContentSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(originalChannelContent))).ToLowerInvariant();
            await gateway.EditMessageAsync(
                new EditMessageRequest(
                    configuration.UserA,
                    channelResult.MessageId,
                    editedChannelContent,
                    previousContentSha256),
                timeout.Token);
            var contentChanged = await WaitForEventAsync<MessageContentChangedEvent>(
                gateway,
                configuration.UserB,
                queueB,
                lastEventB,
                registerB.EventQueueLongPollTimeout,
                item => item.MessageId == channelResult.MessageId && item.Content == editedChannelContent,
                timeout.Token);
            lastEventB = contentChanged.LastEventId;

            await gateway.SetMessageStarredAsync(
                new SetMessageStarredRequest(configuration.UserB, channelResult.MessageId, true),
                timeout.Token);
            var messageStarred = await WaitForEventAsync<MessageFlagsChangedEvent>(
                gateway,
                configuration.UserB,
                queueB,
                lastEventB,
                registerB.EventQueueLongPollTimeout,
                item => !item.AllMessages && item.MessageIds.Contains(channelResult.MessageId) &&
                    item.Operation == MessageFlagOperation.Add &&
                    string.Equals(item.Flag, "starred", StringComparison.OrdinalIgnoreCase),
                timeout.Token);
            lastEventB = messageStarred.LastEventId;

            history = await gateway.GetHistoryAsync(
                new HistoryRequest(configuration.UserB, channel, channelResult.MessageId, true, 1),
                timeout.Token);
            var mutatedMessage = Assert.Single(history.Messages);
            Assert.Equal(editedChannelContent, mutatedMessage.Content);
            Assert.True(mutatedMessage.IsStarred);
            Assert.Contains(mutatedMessage.Reactions, item =>
                item.Identity == thumbsUp && item.UserId == configuration.UserB.UserId);

            var attachmentBytes = Encoding.UTF8.GetBytes($"RelayCove live attachment {topic}\n");
            using var attachmentStream = new MemoryStream(attachmentBytes, writable: false);
            var uploaded = await gateway.UploadAttachmentAsync(
                new UploadAttachmentRequest(
                    configuration.UserA,
                    new AttachmentUpload(
                        "relaycove-live.txt",
                        "text/plain",
                        attachmentBytes.LongLength,
                        attachmentStream)),
                timeout.Token);
            var attachmentContent = $"RelayCove attachment [relaycove-live.txt]({uploaded.Url})";
            var attachmentResult = await gateway.SendAsync(
                new SendRequest(configuration.UserA, queueA, "2", channel, attachmentContent),
                timeout.Token);
            var afterAttachment = await WaitForMessageAsync(
                gateway,
                configuration.UserB,
                queueB,
                lastEventB,
                registerB.EventQueueLongPollTimeout,
                attachmentResult.MessageId,
                timeout.Token);
            lastEventB = afterAttachment.LastEventId;
            Assert.Contains(uploaded.Url, afterAttachment.Message.Content, StringComparison.Ordinal);

            var downloaded = await gateway.GetRealmMediaAsync(
                new GetRealmMediaRequest(
                    configuration.UserB,
                    new RealmMediaRequest(uploaded.Url, RealmMediaKind.File, 1024 * 1024)),
                timeout.Token);
            Assert.Equal(attachmentBytes, downloaded.Content);

            var deleteProbe = await gateway.SendAsync(
                new SendRequest(configuration.UserA, queueA, "3", channel, $"RelayCove delete probe {topic}"),
                timeout.Token);
            var afterDeleteProbe = await WaitForMessageAsync(
                gateway,
                configuration.UserB,
                queueB,
                lastEventB,
                registerB.EventQueueLongPollTimeout,
                deleteProbe.MessageId,
                timeout.Token);
            lastEventB = afterDeleteProbe.LastEventId;
            await gateway.DeleteMessageAsync(
                new DeleteMessageRequest(configuration.UserA, deleteProbe.MessageId),
                timeout.Token);
            var messageDeleted = await WaitForEventAsync<MessageDeletedEvent>(
                gateway,
                configuration.UserB,
                queueB,
                lastEventB,
                registerB.EventQueueLongPollTimeout,
                item => item.MessageIds.Contains(deleteProbe.MessageId),
                timeout.Token);
            lastEventB = messageDeleted.LastEventId;

            await gateway.SetReactionAsync(
                new SetReactionRequest(configuration.UserB, channelResult.MessageId, thumbsUp, false),
                timeout.Token);
            var reactionRemoved = await WaitForEventAsync<MessageReactionChangedEvent>(
                gateway,
                configuration.UserB,
                queueB,
                lastEventB,
                registerB.EventQueueLongPollTimeout,
                item => item.MessageId == channelResult.MessageId &&
                    item.Reaction.UserId == configuration.UserB.UserId &&
                    item.Reaction.Identity == thumbsUp && !item.Add,
                timeout.Token);
            lastEventB = reactionRemoved.LastEventId;

            await gateway.SetMessageStarredAsync(
                new SetMessageStarredRequest(configuration.UserB, channelResult.MessageId, false),
                timeout.Token);
            var messageUnstarred = await WaitForEventAsync<MessageFlagsChangedEvent>(
                gateway,
                configuration.UserB,
                queueB,
                lastEventB,
                registerB.EventQueueLongPollTimeout,
                item => !item.AllMessages && item.MessageIds.Contains(channelResult.MessageId) &&
                    item.Operation == MessageFlagOperation.Remove &&
                    string.Equals(item.Flag, "starred", StringComparison.OrdinalIgnoreCase),
                timeout.Token);
            lastEventB = messageUnstarred.LastEventId;

            Assert.True(configuration.AllowedUserIds.SetEquals(
                [configuration.UserA.UserId, configuration.UserB.UserId]));
            var direct = new DirectMessage([configuration.UserB.UserId]);
            var directResult = await gateway.SendAsync(
                new SendRequest(configuration.UserA, queueA, "4", direct, $"RelayCove live direct {topic}"),
                timeout.Token);
            var afterDirect = await WaitForMessageAsync(
                gateway,
                configuration.UserB,
                queueB,
                lastEventB,
                registerB.EventQueueLongPollTimeout,
                directResult.MessageId,
                timeout.Token);
            Assert.Equal(new DirectMessage([configuration.UserA.UserId]), afterDirect.Message.Conversation);

            await gateway.DeleteQueueAsync(new DeleteQueueRequest(configuration.UserB, queueB), timeout.Token);
            var expired = await Assert.ThrowsAsync<GatewayException>(() => gateway.GetEventsAsync(
                new GetEventsRequest(configuration.UserB, queueB, afterDirect.LastEventId, TimeSpan.FromSeconds(30)),
                timeout.Token));
            Assert.Equal(GatewayErrorKind.QueueExpired, expired.Kind);

            var rebuilt = await gateway.RegisterAsync(new RegisterRequest(configuration.UserB), timeout.Token);
            queueB = rebuilt.QueueId;
            AssertApprovedChannel(rebuilt, configuration.ChannelId, configuration.ChannelName);
        }
        finally
        {
            await DeleteQueueBestEffortAsync(gateway, configuration.UserA, queueA);
            await DeleteQueueBestEffortAsync(gateway, configuration.UserB, queueB);
        }
    }

    [Fact]
    public async Task DedicatedAccount_WhenExplicitlyAuthorized_DrivesClientSessionMutations()
    {
        var configuration = LiveConfiguration.Load();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var gateway = new ZulipGateway();
        var store = new EphemeralAccountStore();
        var vault = new EphemeralCredentialVault();
        await using var session = new ClientSession(gateway, store, vault);

        await session.LoginAsync(
            configuration.Realm.AbsoluteUri,
            configuration.UserA.Email,
            configuration.UserAPassword,
            timeout.Token);
        Assert.Equal(ConnectionStatus.Connected, session.State.Connection.Status);
        Assert.Equal(configuration.UserA.UserId, session.CurrentUserId);

        var topic = $"session-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}";
        var channel = new ChannelTopic(configuration.ChannelId, topic);
        await session.SelectConversationAsync(channel, timeout.Token);

        var originalContent = $"RelayCove ClientSession live {topic}";
        await session.SendAsync(originalContent, timeout.Token);
        await WaitUntilAsync(
            () => session.State.Messages.Values.Any(item =>
                item.Conversation == channel && item.SenderId == configuration.UserA.UserId &&
                item.Content == originalContent),
            timeout.Token);
        var sent = Assert.Single(session.State.Messages.Values, item =>
            item.Conversation == channel && item.SenderId == configuration.UserA.UserId &&
            item.Content == originalContent);

        var thumbsUp = new EmojiReactionIdentity("+1", "1f44d", "unicode_emoji");
        await session.SetReactionAsync(sent.Id, thumbsUp, true, timeout.Token);
        Assert.Contains(session.State.Messages[sent.Id].Reactions, item =>
            item.Identity == thumbsUp && item.UserId == configuration.UserA.UserId);

        var editedContent = $"RelayCove ClientSession edited {topic}";
        await session.EditMessageAsync(sent.Id, editedContent, timeout.Token);
        Assert.Equal(editedContent, session.State.Messages[sent.Id].Content);

        await session.SetMessageStarredAsync(sent.Id, true, timeout.Token);
        Assert.True(session.State.Messages[sent.Id].IsStarred);

        var attachmentBytes = Encoding.UTF8.GetBytes($"RelayCove ClientSession attachment {topic}\n");
        using var attachmentStream = new MemoryStream(attachmentBytes, writable: false);
        var uploaded = await session.UploadAttachmentAsync(
            new AttachmentUpload(
                "relaycove-session-live.txt",
                "text/plain",
                attachmentBytes.LongLength,
                attachmentStream),
            timeout.Token);
        var downloaded = await session.GetRealmMediaAsync(
            new RealmMediaRequest(uploaded.Url, RealmMediaKind.File, 1024 * 1024),
            timeout.Token);
        Assert.Equal(attachmentBytes, downloaded.Content);

        var attachmentContent = $"RelayCove ClientSession attachment [relaycove-session-live.txt]({uploaded.Url})";
        await session.SendAsync(attachmentContent, timeout.Token);
        await WaitUntilAsync(
            () => session.State.Messages.Values.Any(item =>
                item.Conversation == channel && item.Content == attachmentContent),
            timeout.Token);

        await session.SetReactionAsync(sent.Id, thumbsUp, false, timeout.Token);
        Assert.DoesNotContain(session.State.Messages[sent.Id].Reactions, item =>
            item.Identity == thumbsUp && item.UserId == configuration.UserA.UserId);
        await session.SetMessageStarredAsync(sent.Id, false, timeout.Token);
        Assert.False(session.State.Messages[sent.Id].IsStarred);
        await session.DeleteMessageAsync(sent.Id, timeout.Token);
        Assert.DoesNotContain(sent.Id, session.State.Messages.Keys);

        var unsubscribeProbe = new ChannelTopic(configuration.UnsubscribeChannelId, "unsubscribe-probe");
        Assert.Equal(
            configuration.UnsubscribeChannelName,
            session.State.Subscriptions[configuration.UnsubscribeChannelId].Name,
            ignoreCase: true);
        await session.SelectConversationAsync(unsubscribeProbe, timeout.Token);
        await session.UnsubscribeChannelAsync(configuration.UnsubscribeChannelId, timeout.Token);
        Assert.DoesNotContain(configuration.UnsubscribeChannelId, session.State.Subscriptions.Keys);
        Assert.Null(session.SelectedConversation);

        await session.LogoutAsync(timeout.Token);
        Assert.Equal(ConnectionStatus.SignedOut, session.State.Connection.Status);
        Assert.Null(vault.Credential);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    private static async Task<ObservedEvent<TEvent>> WaitForEventAsync<TEvent>(
        IZulipGateway gateway,
        CredentialEnvelope credentials,
        string queueId,
        long lastEventId,
        TimeSpan longPollTimeout,
        Func<TEvent, bool> predicate,
        CancellationToken cancellationToken)
        where TEvent : DomainEvent
    {
        while (true)
        {
            var batch = await gateway.GetEventsAsync(
                new GetEventsRequest(credentials, queueId, lastEventId, longPollTimeout + TimeSpan.FromSeconds(10)),
                cancellationToken);
            lastEventId = batch.LastEventId;
            var match = batch.Events.OfType<TEvent>().FirstOrDefault(predicate);
            if (match is not null)
            {
                return new ObservedEvent<TEvent>(match, lastEventId);
            }
        }
    }

    private static async Task<ObservedMessage> WaitForMessageAsync(
        IZulipGateway gateway,
        CredentialEnvelope credentials,
        string queueId,
        long lastEventId,
        TimeSpan longPollTimeout,
        long messageId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var batch = await gateway.GetEventsAsync(
                new GetEventsRequest(credentials, queueId, lastEventId, longPollTimeout + TimeSpan.FromSeconds(10)),
                cancellationToken);
            lastEventId = batch.LastEventId;
            var match = batch.Events
                .OfType<MessageUpsertEvent>()
                .FirstOrDefault(item => item.Message.Id == messageId);
            if (match is not null)
            {
                return new ObservedMessage(match.Message, lastEventId);
            }
        }
    }

    private static void AssertApprovedChannel(RegisterResult register, long channelId, string channelName)
    {
        var channel = Assert.Single(register.Subscriptions, item => item.ChannelId == channelId);
        Assert.Equal(channelName, channel.Name, ignoreCase: true);
        Assert.True(channel.IsActive);
    }

    private static async Task DeleteQueueBestEffortAsync(
        IZulipGateway gateway,
        CredentialEnvelope credentials,
        string queueId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await gateway.DeleteQueueAsync(new DeleteQueueRequest(credentials, queueId), timeout.Token);
        }
        catch (GatewayException)
        {
            // Queue cleanup must not hide the primary Live assertion result.
        }
        catch (OperationCanceledException)
        {
            // Queue cleanup is best effort after the test's own queue-expiry coverage.
        }
    }

    private sealed record ObservedMessage(ChatMessage Message, long LastEventId);

    private sealed record ObservedEvent<TEvent>(TEvent Event, long LastEventId)
        where TEvent : DomainEvent;

    private sealed class EphemeralCredentialVault : ICredentialVault
    {
        public CredentialEnvelope? Credential { get; private set; }

        public Task<CredentialEnvelope?> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Credential);

        public Task SetAsync(CredentialEnvelope credentials, CancellationToken cancellationToken = default)
        {
            Credential = credentials;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(CancellationToken cancellationToken = default)
        {
            Credential = null;
            return Task.CompletedTask;
        }
    }

    private sealed class EphemeralAccountStore : IAccountStore
    {
        private readonly object _gate = new();
        private StoredAccount? _account;
        private ClientState _state = ClientState.Empty;
        private bool _isUnlocked = true;

        public Task<IReadOnlyList<StoredAccount>> ListAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<StoredAccount>>(_account is null ? [] : [_account]);
            }
        }

        public Task InitializeAsync(StoredAccount account, CancellationToken cancellationToken = default)
        {
            lock (_gate) _account = account;
            return Task.CompletedTask;
        }

        public Task MigrateAsync(AccountId accountId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<AccountSnapshot?> LoadAsync(AccountId accountId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_account is null
                    ? null
                    : new AccountSnapshot(_account, _isUnlocked, _isUnlocked ? _state : ClientState.Empty));
            }
        }

        public Task<IReadOnlyList<ChatMessage>> QueryMessagesAsync(
            AccountId accountId,
            ConversationKey conversation,
            long? beforeMessageId,
            int limit,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var messages = _state.Messages.Values
                    .Where(item => item.Conversation == conversation &&
                        (beforeMessageId is null || item.Id < beforeMessageId.Value))
                    .OrderByDescending(item => item.Id)
                    .Take(limit)
                    .OrderBy(item => item.Id)
                    .ToArray();
                return Task.FromResult<IReadOnlyList<ChatMessage>>(messages);
            }
        }

        public Task ReplaceRegisterSnapshotAsync(
            AccountId accountId,
            RegisterResult snapshot,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _state = new ClientState(
                    subscriptions: snapshot.Subscriptions.ToDictionary(item => item.ChannelId),
                    users: snapshot.Users.ToDictionary(item => item.UserId),
                    unread: snapshot.Unread);
                _state = DomainReducer.Apply(_state, snapshot.Events);
            }
            return Task.CompletedTask;
        }

        public Task ApplyBatchAsync(
            AccountId accountId,
            IReadOnlyCollection<DomainEvent> events,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _state = DomainReducer.Apply(_state, events);
            return Task.CompletedTask;
        }

        public Task PurgeSubscriptionAsync(
            AccountId accountId,
            long channelId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> IsCacheUnlockedAsync(
            AccountId accountId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) return Task.FromResult(_isUnlocked);
        }

        public Task SetCacheUnlockedAsync(
            AccountId accountId,
            bool isUnlocked,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _isUnlocked = isUnlocked;
            return Task.CompletedTask;
        }

        public Task ClearAsync(AccountId accountId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _account = null;
                _state = ClientState.Empty;
            }
            return Task.CompletedTask;
        }
    }

    private sealed record LiveConfiguration(
        RealmEndpoint Realm,
        CredentialEnvelope UserA,
        CredentialEnvelope UserB,
        long ChannelId,
        string ChannelName,
        long UnsubscribeChannelId,
        string UnsubscribeChannelName,
        HashSet<long> AllowedUserIds,
        string UserAPassword)
    {
        public static LiveConfiguration Load()
        {
            if (!string.Equals(Require("RELAYCOVE_LIVE_WRITE_CONFIRM"), WriteConfirmation, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Live write confirmation is invalid.");
            }
            if (!string.Equals(Require("RELAYCOVE_LIVE_CHANNEL_APPROVED"), "true", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The isolated private Live channel is not approved.");
            }
            if (!string.Equals(Require("RELAYCOVE_LIVE_UNSUBSCRIBE_CHANNEL_APPROVED"), "true", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The isolated private unsubscribe channel is not approved.");
            }

            var realm = RealmEndpoint.Parse(Require("RELAYCOVE_LIVE_REALM"));
            var userAId = RequirePositiveInt64("RELAYCOVE_LIVE_USER_A_ID");
            var userBId = RequirePositiveInt64("RELAYCOVE_LIVE_USER_B_ID");
            if (userAId == userBId)
            {
                throw new InvalidOperationException("Live accounts must be distinct.");
            }

            var allowed = Require("RELAYCOVE_LIVE_ALLOWED_USER_IDS")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture))
                .ToHashSet();
            if (!allowed.SetEquals([userAId, userBId]))
            {
                throw new InvalidOperationException("The Live recipient allowlist must contain exactly the two test user IDs.");
            }

            var channelId = RequirePositiveInt64("RELAYCOVE_LIVE_CHANNEL_ID");
            var unsubscribeChannelId = RequirePositiveInt64("RELAYCOVE_LIVE_UNSUBSCRIBE_CHANNEL_ID");
            if (channelId == unsubscribeChannelId)
            {
                throw new InvalidOperationException("The Live message and unsubscribe channels must be distinct.");
            }

            return new LiveConfiguration(
                realm,
                new CredentialEnvelope(
                    realm,
                    Require("RELAYCOVE_LIVE_USER_A_EMAIL"),
                    userAId,
                    Require("RELAYCOVE_LIVE_USER_A_API_KEY")),
                new CredentialEnvelope(
                    realm,
                    Require("RELAYCOVE_LIVE_USER_B_EMAIL"),
                    userBId,
                    Require("RELAYCOVE_LIVE_USER_B_API_KEY")),
                channelId,
                Require("RELAYCOVE_LIVE_CHANNEL_NAME"),
                unsubscribeChannelId,
                Require("RELAYCOVE_LIVE_UNSUBSCRIBE_CHANNEL_NAME"),
                allowed,
                Require("RELAYCOVE_LIVE_USER_A_PASSWORD"));
        }

        public override string ToString() =>
            "LiveConfiguration { Realm = [redacted], Users = [redacted], Channels = [redacted], Password = [redacted] }";

        private static string Require(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"Missing required Live setting {name}.");

        private static long RequirePositiveInt64(string name) =>
            long.TryParse(Require(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
                ? value
                : throw new InvalidOperationException($"Live setting {name} must be a positive integer.");
    }
}
