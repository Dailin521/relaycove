using System.Globalization;
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
        AssertApprovedChannel(registerA, configuration.ChannelId);
        AssertApprovedChannel(registerB, configuration.ChannelId);

        var topic = $"run-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}";
        var channel = new ChannelTopic(configuration.ChannelId, topic);
        var queueA = registerA.QueueId;
        var queueB = registerB.QueueId;
        try
        {
            var channelResult = await gateway.SendAsync(
                new SendRequest(configuration.UserA, queueA, "1", channel, $"RelayCove live channel {topic}"),
                timeout.Token);
            var afterChannel = await WaitForMessageAsync(
                gateway,
                configuration.UserB,
                queueB,
                registerB.LastEventId,
                registerB.EventQueueLongPollTimeout,
                channelResult.MessageId,
                timeout.Token);

            Assert.Equal(channel, afterChannel.Message.Conversation);
            var history = await gateway.GetHistoryAsync(
                new HistoryRequest(configuration.UserB, channel, channelResult.MessageId, true, 1),
                timeout.Token);
            Assert.Equal(channelResult.MessageId, Assert.Single(history.Messages).Id);
            await gateway.MarkReadAsync(
                new MarkReadRequest(configuration.UserB, channel, channelResult.MessageId, 1),
                timeout.Token);

            Assert.True(configuration.AllowedUserIds.SetEquals(
                [configuration.UserA.UserId, configuration.UserB.UserId]));
            var direct = new DirectMessage([configuration.UserB.UserId]);
            var directResult = await gateway.SendAsync(
                new SendRequest(configuration.UserA, queueA, "2", direct, $"RelayCove live direct {topic}"),
                timeout.Token);
            var afterDirect = await WaitForMessageAsync(
                gateway,
                configuration.UserB,
                queueB,
                afterChannel.LastEventId,
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
            AssertApprovedChannel(rebuilt, configuration.ChannelId);
        }
        finally
        {
            await DeleteQueueBestEffortAsync(gateway, configuration.UserA, queueA);
            await DeleteQueueBestEffortAsync(gateway, configuration.UserB, queueB);
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

    private static void AssertApprovedChannel(RegisterResult register, long channelId)
    {
        var channel = Assert.Single(register.Subscriptions, item => item.ChannelId == channelId);
        Assert.Equal("relaycove-client-e2e", channel.Name, ignoreCase: true);
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

    private sealed record LiveConfiguration(
        RealmEndpoint Realm,
        CredentialEnvelope UserA,
        CredentialEnvelope UserB,
        long ChannelId,
        HashSet<long> AllowedUserIds)
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
                RequirePositiveInt64("RELAYCOVE_LIVE_CHANNEL_ID"),
                allowed);
        }

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
