using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RelayCove.Core;
using RelayCove.Zulip.Client;

namespace RelayCove.Zulip.Client.Tests;

public sealed class ZulipGatewayTests
{
    private static readonly RealmEndpoint Realm = RealmEndpoint.Parse("https://chat.example.test");
    private static readonly CredentialEnvelope Credentials = new(Realm, "ada@example.test", 7, "api-key-secret");

    [Fact]
    public async Task Probe_uses_server_settings_without_credentials_and_ignores_unknown_fields()
    {
        using var handler = new RecordingHandler(Json("""{"zulip_version":"12.1","zulip_feature_level":500,"is_incompatible":false,"email_auth_enabled":true,"future":"ignored"}"""));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.ProbeRealmAsync(Realm);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://chat.example.test/api/v1/server_settings", request.Uri!.AbsoluteUri);
        Assert.Null(request.Authorization);
        Assert.True(result.IsCompatible);
        Assert.Equal("12.1", result.ServerVersion);
    }

    [Fact]
    public void Default_handler_disables_automatic_redirects()
    {
        using var handler = ZulipGateway.CreateDefaultHandler();
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task Authenticate_sends_password_only_to_fetch_api_key_and_uses_basic_afterward()
    {
        using var handler = new RecordingHandler(
            Json("""{"api_key":"api-key-secret","email":"ada@example.test","user_id":7}"""),
            Json("""{"user_id":7,"full_name":"Ada Lovelace","email":"ada@example.test","extra":true}"""));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.AuthenticateAsync(new AuthenticationRequest(Realm, "ada@example.test", "password-secret"));

        Assert.Equal(7, result.User.UserId);
        Assert.Equal("Ada Lovelace", result.User.FullName);
        Assert.Equal("/api/v1/fetch_api_key", handler.Requests[0].Uri!.AbsolutePath);
        Assert.Contains("password=password-secret", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Null(handler.Requests[0].Authorization);
        Assert.Equal("/api/v1/users/me", handler.Requests[1].Uri!.AbsolutePath);
        Assert.Equal("Basic", handler.Requests[1].Authorization!.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("ada@example.test:api-key-secret")), handler.Requests[1].Authorization!.Parameter);
        Assert.DoesNotContain("password-secret", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_serializes_event_types_as_json_and_projects_snapshot()
    {
        using var handler = new RecordingHandler(Json("""
            {"queue_id":"queue-1","last_event_id":9,"event_queue_longpoll_timeout_seconds":90,"idle_queue_timeout_secs":3600,"max_message_length":10000,"max_topic_length":60,"max_file_upload_size_mib":25,
             "subscriptions":[{"stream_id":42,"name":"general","future":1}],
             "realm_users":[{"user_id":7,"full_name":"Ada","email":"ada@example.test","role":200}],
             "user_settings":{"presence_enabled":true},
             "realm_presence_disabled":false,
             "presences":{"7":{"active_timestamp":100,"idle_timestamp":101}},
             "user_status":{"7":{"status_text":"会议中","emoji_name":"calendar","emoji_code":"1f4c5","reaction_type":"unicode_emoji"}},
             "is_admin":true,
             "user_topics":[{"stream_id":42,"topic_name":"follow me","visibility_policy":3}],
             "recent_private_conversations":[{"user_ids":[9,10]},{"user_ids":[]}],
             "unread_msgs":{"count":3,"streams":[{"stream_id":42,"topic":"hello","unread_message_ids":[1,2]}],"pms":[{"other_user_id":9,"unread_message_ids":[3]}],"huddles":[],"old_unreads_missing":false},
             "unknown":{}}
            """));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.RegisterAsync(new RegisterRequest(Credentials, ["message", "heartbeat"]));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/v1/register", request.Uri!.AbsolutePath);
        Assert.Equal("Basic", request.Authorization!.Scheme);
        var form = ParseForm(request.Body);
        Assert.Equal("[\"message\",\"heartbeat\"]", form["event_types"]);
        Assert.Equal("false", form["apply_markdown"]);
        Assert.Equal("false", form["include_subscribers"]);
        Assert.Equal("3600", form["idle_queue_timeout"]);
        Assert.Equal("[\"subscription\",\"realm_user\",\"realm\",\"realm_user_groups\",\"recent_private_conversations\",\"presence\",\"user_settings\",\"user_status\"]", form["fetch_event_types"]);
        Assert.Equal("true", form["slim_presence"]);
        Assert.Contains("\"bulk_message_deletion\":true", form["client_capabilities"], StringComparison.Ordinal);
        Assert.Contains("\"archived_channels\":true", form["client_capabilities"], StringComparison.Ordinal);
        Assert.Contains("\"user_settings_object\":true", form["client_capabilities"], StringComparison.Ordinal);
        Assert.Contains("\"simplified_presence_events\":true", form["client_capabilities"], StringComparison.Ordinal);
        Assert.Equal("queue-1", result.QueueId);
        Assert.Equal(TimeSpan.FromSeconds(90), result.EventQueueLongPollTimeout);
        Assert.Equal(42, Assert.Single(result.Subscriptions).ChannelId);
        Assert.Single(result.Users);
        Assert.Equal(2, result.RecentDirectMessages.Count);
        Assert.Contains(result.RecentDirectMessages, item => item is DirectMessage direct && direct.OtherUserIds.Count == 0);
        Assert.Equal(3, result.Unread.Total);
        Assert.Equal(25, result.MaxFileUploadSizeMiB);
        Assert.True(result.IsOrganizationAdministrator);
        Assert.False(result.CanCreatePrivateChannel);
        Assert.True(result.IsPresenceAvailable);
        Assert.True(result.IsOwnPresenceEnabled);
        Assert.Equal(7, Assert.Single(result.Presences!).UserId);
        Assert.True(result.IsUserStatusAvailable);
        var userStatus = Assert.Single(result.UserStatuses!);
        Assert.Equal(7, userStatus.UserId);
        Assert.Equal("会议中", userStatus.Content.StatusText);
        Assert.Equal("1f4c5", userStatus.Content.Emoji!.EmojiCode);
        Assert.Equal(TopicVisibilityPolicy.Followed, Assert.Single(result.UserTopics!).Policy);
        Assert.Empty(result.Events);
    }

    [Theory]
    [InlineData("{\"direct_members\":[7],\"direct_subgroups\":[]}", "[]", true)]
    [InlineData("1", "[{\"id\":1,\"name\":\"creators\",\"members\":[],\"direct_subgroup_ids\":[2]},{\"id\":2,\"name\":\"nested\",\"members\":[7],\"direct_subgroup_ids\":[]}]", true)]
    [InlineData("1", "[{\"id\":1,\"name\":\"creators\",\"members\":[8],\"direct_subgroup_ids\":[]}]", false)]
    [InlineData("1", "[{\"id\":1,\"name\":\"creators\",\"members\":[7,\"bad\"],\"direct_subgroup_ids\":[]}]", false)]
    public async Task Register_WhenPrivateChannelCreatorGroupIsProvided_EvaluatesStrictMembership(
        string permission,
        string groups,
        bool expected)
    {
        using var handler = new RecordingHandler(Json(RegisterPayload(permission, groups)));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.RegisterAsync(new RegisterRequest(Credentials));

        Assert.Equal(expected, result.CanCreatePrivateChannel);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Register_WhenEventTypesAreDefault_IncludesReaction()
    {
        using var handler = new RecordingHandler(Json("""
            {"queue_id":"queue-1","last_event_id":9,"event_queue_longpoll_timeout_seconds":90,
             "max_message_length":10000,"max_topic_length":60,"subscriptions":[],"realm_users":[]}
            """));
        using var gateway = new ZulipGateway(handler);

        await gateway.RegisterAsync(new RegisterRequest(Credentials));

        var form = ParseForm(Assert.Single(handler.Requests).Body);
        Assert.Contains("\"reaction\"", form["event_types"], StringComparison.Ordinal);
        Assert.Contains("\"presence\"", form["event_types"], StringComparison.Ordinal);
        Assert.Contains("\"user_settings\"", form["event_types"], StringComparison.Ordinal);
        Assert.Contains("\"user_status\"", form["event_types"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"presence_enabled\":true}", true)]
    [InlineData("{\"presence_enabled\":false}", false)]
    [InlineData("{}", null)]
    public async Task Register_WhenUserSettingsContainPresenceEnabled_ProjectsOwnSetting(
        string userSettings,
        bool? expected)
    {
        using var handler = new RecordingHandler(Json($$"""
            {"queue_id":"queue-1","last_event_id":9,"event_queue_longpoll_timeout_seconds":90,
             "max_message_length":10000,"max_topic_length":60,"subscriptions":[],
             "realm_users":[{"user_id":7,"full_name":"Ada","email":"ada@example.test"}],
             "realm_presence_disabled":false,"presences":{},"user_settings":{{userSettings}}}
            """));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.RegisterAsync(new RegisterRequest(Credentials));

        Assert.Equal(expected, result.IsOwnPresenceEnabled);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(",\"realm_presence_disabled\":false,\"presences\":[]", false)]
    [InlineData(",\"realm_presence_disabled\":false,\"presences\":{}", true)]
    [InlineData(",\"realm_presence_disabled\":true,\"presences\":{}", false)]
    [InlineData(",\"realm_presence_disabled\":false,\"presences\":{\"not-a-user\":{}}", false)]
    [InlineData(",\"realm_presence_disabled\":false,\"presences\":{\"7\":[]}", false)]
    [InlineData(",\"realm_presence_disabled\":false,\"presences\":{\"7\":{\"active_timestamp\":100}}", false)]
    [InlineData(",\"realm_presence_disabled\":false,\"presences\":{\"7\":{\"active_timestamp\":\"100\",\"idle_timestamp\":101}}", false)]
    public async Task Register_WhenPresenceSnapshotIsUnavailable_FailsClosed(
        string presenceFields,
        bool expectedAvailable)
    {
        using var handler = new RecordingHandler(Json($$"""
            {"queue_id":"queue-1","last_event_id":9,"event_queue_longpoll_timeout_seconds":90,
             "max_message_length":10000,"max_topic_length":60,"subscriptions":[],"realm_users":[]{{presenceFields}}}
            """));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.RegisterAsync(new RegisterRequest(Credentials));

        Assert.Equal(expectedAvailable, result.IsPresenceAvailable);
        Assert.Empty(result.Presences!);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(",\"user_status\":{}", true)]
    [InlineData(",\"user_status\":[]", false)]
    [InlineData(",\"user_status\":{\"bad-id\":{}}", false)]
    [InlineData(",\"user_status\":{\"7\":{\"status_text\":7}}", false)]
    public async Task Register_WhenUserStatusSnapshotIsUnavailable_FailsClosed(
        string userStatusField,
        bool expectedAvailable)
    {
        using var handler = new RecordingHandler(Json($$"""
            {"queue_id":"queue-1","last_event_id":9,"event_queue_longpoll_timeout_seconds":90,
             "max_message_length":10000,"max_topic_length":60,"subscriptions":[],"realm_users":[]{{userStatusField}}}
            """));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.RegisterAsync(new RegisterRequest(Credentials));

        Assert.Equal(expectedAvailable, result.IsUserStatusAvailable);
        Assert.Empty(result.UserStatuses!);
    }

    [Fact]
    public async Task RealmPresence_UsesReadOnlyEndpointAndMapsLegacyAggregatedStatus()
    {
        using var handler = new RecordingHandler(Json("""
            {"server_timestamp":1000.25,"presences":{
              "active@example.test":{"aggregated":{"status":"active","timestamp":995}},
              "idle@example.test":{"website":{"status":"idle","timestamp":990}},
              "offline@example.test":{"aggregated":{"status":"offline","timestamp":100}}
            }}
            """));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.GetRealmPresenceAsync(new GetRealmPresenceRequest(Credentials));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v1/realm/presence", request.Uri!.AbsolutePath);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_000_250), result.ServerTimestamp);
        Assert.Collection(
            result.Presences.OrderBy(item => item.UserEmail, StringComparer.Ordinal),
            active =>
            {
                Assert.Equal("active@example.test", active.UserEmail);
                Assert.Equal(active.ActiveTimestamp, active.IdleTimestamp);
            },
            idle =>
            {
                Assert.Equal("idle@example.test", idle.UserEmail);
                Assert.Null(idle.ActiveTimestamp);
                Assert.NotNull(idle.IdleTimestamp);
            },
            offline =>
            {
                Assert.Equal("offline@example.test", offline.UserEmail);
                Assert.Null(offline.ActiveTimestamp);
                Assert.Null(offline.IdleTimestamp);
            });
    }

    [Theory]
    [InlineData(UserPresenceStatus.Active, "active")]
    [InlineData(UserPresenceStatus.Idle, "idle")]
    public async Task UpdateOwnPresence_UsesPingOnlyOfficialStatus(
        UserPresenceStatus status,
        string expectedStatus)
    {
        using var handler = new RecordingHandler(Json("""{"result":"success","msg":""}"""));
        using var gateway = new ZulipGateway(handler);

        await gateway.UpdateOwnPresenceAsync(new UpdateOwnPresenceRequest(Credentials, status));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/users/me/presence", request.Uri!.AbsolutePath);
        var form = ParseForm(request.Body);
        Assert.Equal(expectedStatus, form["status"]);
        Assert.Equal("true", form["ping_only"]);
        Assert.Equal("false", form["new_user_input"]);
    }

    [Fact]
    public async Task UpdateOwnPresence_WhenOfflineIsRequested_FailsBeforeNetwork()
    {
        using var handler = new RecordingHandler(Json("""{"result":"success","msg":""}"""));
        using var gateway = new ZulipGateway(handler);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gateway.UpdateOwnPresenceAsync(
            new UpdateOwnPresenceRequest(Credentials, UserPresenceStatus.Offline)));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public async Task SetPresenceEnabled_UsesOfficialInvisibleSetting(bool enabled, string expected)
    {
        using var handler = new RecordingHandler(Json("""{"result":"success","msg":""}"""));
        using var gateway = new ZulipGateway(handler);

        await gateway.SetPresenceEnabledAsync(new SetPresenceEnabledRequest(Credentials, enabled));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("/api/v1/settings", request.Uri!.AbsolutePath);
        Assert.Equal(expected, ParseForm(request.Body)["presence_enabled"]);
    }

    [Fact]
    public async Task UpdateOwnUserStatus_AlwaysSendsCompleteOfficialStatusTuple()
    {
        using var handler = new RecordingHandler(Json("""{"result":"success","msg":""}"""));
        using var gateway = new ZulipGateway(handler);
        var status = new UserStatusContent(
            "会议中",
            new EmojiReactionIdentity("calendar", "1f4c5", "unicode_emoji"));

        await gateway.UpdateOwnUserStatusAsync(new UpdateOwnUserStatusRequest(Credentials, status));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/users/me/status", request.Uri!.AbsolutePath);
        var form = ParseForm(request.Body);
        Assert.Equal("会议中", form["status_text"]);
        Assert.Equal("calendar", form["emoji_name"]);
        Assert.Equal("1f4c5", form["emoji_code"]);
        Assert.Equal("unicode_emoji", form["reaction_type"]);
    }

    [Fact]
    public async Task UpdateOwnUserStatus_WhenCleared_SendsFourEmptyFields()
    {
        using var handler = new RecordingHandler(Json("""{"result":"success","msg":""}"""));
        using var gateway = new ZulipGateway(handler);

        await gateway.UpdateOwnUserStatusAsync(
            new UpdateOwnUserStatusRequest(Credentials, new UserStatusContent()));

        var form = ParseForm(Assert.Single(handler.Requests).Body);
        Assert.Equal(string.Empty, form["status_text"]);
        Assert.Equal(string.Empty, form["emoji_name"]);
        Assert.Equal(string.Empty, form["emoji_code"]);
        Assert.Equal(string.Empty, form["reaction_type"]);
    }

    [Theory]
    [InlineData("{\"result\":\"error\",\"msg\":\"rejected\"}")]
    [InlineData("{\"msg\":\"missing result\"}")]
    [InlineData("{\"result\":\"success\",\"ignored_parameters_unsupported\":[\"emoji_code\"]}")]
    public async Task UpdateOwnUserStatus_WhenTwoHundredResponseCannotConfirmCompleteWrite_FailsProtocolClosed(string body)
    {
        using var handler = new RecordingHandler(Json(body));
        using var gateway = new ZulipGateway(handler);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.UpdateOwnUserStatusAsync(
            new UpdateOwnUserStatusRequest(Credentials, new UserStatusContent("会议中"))));

        Assert.Equal(GatewayErrorKind.Protocol, error.Kind);
        Assert.Equal(GatewayErrorCode.InvalidResponse, error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Event_PresenceModernFormat_MapsEveryUserInOneEventGroup()
    {
        using var handler = new RecordingHandler(Json("""
            {"events":[{"id":14,"type":"presence","presences":{
              "7":{"active_timestamp":100,"idle_timestamp":101},
              "9":{"active_timestamp":0,"idle_timestamp":102}
            }}]}
            """));
        using var gateway = new ZulipGateway(handler);

        var batch = await gateway.GetEventsAsync(
            new GetEventsRequest(Credentials, "queue-1", 13, TimeSpan.FromSeconds(30)));

        Assert.Equal(2, batch.Events.Count);
        var events = batch.Events.Cast<UserPresenceChangedEvent>().OrderBy(item => item.Presence.UserId).ToArray();
        Assert.All(events, item => Assert.Equal(14, item.EventId));
        Assert.NotNull(events[0].Presence.ActiveTimestamp);
        Assert.Null(events[1].Presence.ActiveTimestamp);
        Assert.NotNull(events[1].Presence.IdleTimestamp);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("\"bad\"", null)]
    public async Task Event_UserSettingsPresenceEnabled_MapsOfficialUpdate(
        string value,
        bool? expected)
    {
        using var handler = new RecordingHandler(Json($$"""
            {"events":[{"id":14,"type":"user_settings","op":"update",
              "property":"presence_enabled","value":{{value}}}]}
            """));
        using var gateway = new ZulipGateway(handler);

        var batch = await gateway.GetEventsAsync(
            new GetEventsRequest(Credentials, "queue-1", 13, TimeSpan.FromSeconds(30)));

        var changed = Assert.IsType<OwnPresenceEnabledChangedEvent>(Assert.Single(batch.Events));
        Assert.Equal(expected, changed.IsEnabled);
        Assert.Equal(14, changed.EventId);
    }

    [Fact]
    public async Task Event_UserStatus_MapsPresetAndClear()
    {
        using var handler = new RecordingHandler(Json("""
            {"events":[
              {"id":14,"type":"user_status","user_id":7,"status_text":"远程办公","emoji_name":"house","emoji_code":"1f3e0","reaction_type":"unicode_emoji"},
              {"id":15,"type":"user_status","user_id":9,"status_text":"","emoji_name":"","emoji_code":"","reaction_type":""}
            ]}
            """));
        using var gateway = new ZulipGateway(handler);

        var batch = await gateway.GetEventsAsync(
            new GetEventsRequest(Credentials, "queue-1", 13, TimeSpan.FromSeconds(30)));

        Assert.Collection(
            batch.Events,
            item =>
            {
                var changed = Assert.IsType<UserStatusChangedEvent>(item);
                Assert.Equal(7, changed.UserId);
                Assert.Equal("远程办公", changed.Status!.StatusText);
                Assert.Equal("house", changed.Status.Emoji!.EmojiName);
            },
            item =>
            {
                var changed = Assert.IsType<UserStatusChangedEvent>(item);
                Assert.Equal(9, changed.UserId);
                Assert.Null(changed.Status);
            });
    }

    [Fact]
    public async Task Event_UserStatus_WhenEmojiTupleIsPartial_FailsClosedWithoutClearingStatus()
    {
        using var handler = new RecordingHandler(Json("""
            {"events":[{"id":14,"type":"user_status","user_id":7,
              "status_text":"","emoji_name":"calendar","emoji_code":"","reaction_type":"unicode_emoji"}]}
            """));
        using var gateway = new ZulipGateway(handler);

        var batch = await gateway.GetEventsAsync(
            new GetEventsRequest(Credentials, "queue-1", 13, TimeSpan.FromSeconds(30)));

        var unknown = Assert.IsType<UnknownDomainEvent>(Assert.Single(batch.Events));
        Assert.Equal("user_status", unknown.Kind);
    }

    [Theory]
    [InlineData("channel", "[{\"operator\":\"channel\",\"operand\":42},{\"operator\":\"topic\",\"operand\":\"a topic\"}]")]
    [InlineData("dm", "[{\"operator\":\"dm\",\"operand\":[9,10]}]")]
    [InlineData("self", "[{\"operator\":\"dm\",\"operand\":[7]}]")]
    public async Task History_uses_a_json_encoded_conversation_narrow(string kind, string expectedNarrow)
    {
        ConversationKey conversation = kind switch
        {
            "channel" => new ChannelTopic(42, "a topic"),
            "dm" => new DirectMessage([10, 9]),
            _ => new DirectMessage([])
        };
        using var handler = new RecordingHandler(Json("""{"messages":[{"id":44,"type":"stream","stream_id":42,"subject":"a topic","sender_id":9,"sender_full_name":"Grace","content":"**raw** markdown","timestamp":100,"flags":["read"],"future":1}],"found_oldest":true,"found_newest":false}"""));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.GetHistoryAsync(new HistoryRequest(Credentials, conversation, 44, true, 10));

        var query = ParseQuery(Assert.Single(handler.Requests).Uri!);
        Assert.Equal(expectedNarrow, query["narrow"]);
        Assert.Equal("false", query["apply_markdown"]);
        Assert.Equal("true", query["allow_empty_topic_name"]);
        var message = Assert.Single(result.Messages);
        Assert.Equal("**raw** markdown", message.Content);
        Assert.True(message.IsRead);
        Assert.True(result.FoundOldest);
    }

    [Fact]
    public async Task SearchMessages_UsesSearchNarrowRawMarkdownAndDoesNotExposeMatchHtml()
    {
        using var handler = new RecordingHandler(Json("""
            {"messages":[{"id":44,"type":"stream","stream_id":42,"subject":"topic","sender_id":9,"content":"**raw**","match_content":"<span>raw</span>","match_subject":"<span>topic</span>","timestamp":100,"flags":[]}],"found_oldest":false,"found_newest":true,"found_anchor":true}
            """));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.SearchMessagesAsync(new MessageSearchRequest(Credentials, "raw words", 90, 50));

        var query = ParseQuery(Assert.Single(handler.Requests).Uri!);
        Assert.Equal("[{\"operator\":\"search\",\"operand\":\"raw words\"}]", query["narrow"]);
        Assert.Equal("90", query["anchor"]);
        Assert.Equal("false", query["include_anchor"]);
        Assert.Equal("50", query["num_before"]);
        Assert.Equal("false", query["apply_markdown"]);
        Assert.Equal("**raw**", Assert.Single(result.Messages).Content);
        Assert.True(result.FoundAnchor);
    }

    [Theory]
    [InlineData(MessageSearchFilter.Files, "attachment")]
    [InlineData(MessageSearchFilter.Images, "image")]
    [InlineData(MessageSearchFilter.Videos, "image")]
    [InlineData(MessageSearchFilter.Links, "link")]
    public async Task SearchMessages_WhenContentFilterIsSelected_UsesOfficialHasNarrow(
        MessageSearchFilter filter,
        string operand)
    {
        using var handler = new RecordingHandler(Json("""{"messages":[],"found_oldest":true,"found_newest":true,"found_anchor":true}"""));
        using var gateway = new ZulipGateway(handler);

        await gateway.SearchMessagesAsync(new MessageSearchRequest(Credentials, string.Empty, null, 50, filter));

        var query = ParseQuery(Assert.Single(handler.Requests).Uri!);
        Assert.Equal($"[{{\"operator\":\"has\",\"operand\":\"{operand}\"}}]", query["narrow"]);
    }

    [Fact]
    public async Task LoadSavedMessages_UsesStarredNarrowAndSupportsPaging()
    {
        using var handler = new RecordingHandler(Json("""{"messages":[],"found_oldest":true,"found_newest":false,"found_anchor":false}"""));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.LoadSavedMessagesAsync(new SavedMessagesRequest(Credentials, 77, 25));

        var query = ParseQuery(Assert.Single(handler.Requests).Uri!);
        Assert.Equal("[{\"operator\":\"is\",\"operand\":\"starred\"}]", query["narrow"]);
        Assert.Equal("77", query["anchor"]);
        Assert.Equal("25", query["num_before"]);
        Assert.Equal("false", query["apply_markdown"]);
        Assert.False(result.FoundAnchor);
    }

    [Fact]
    public async Task Mark_read_uses_a_json_encoded_narrow()
    {
        using var handler = new RecordingHandler(Json("""{"result":"success"}"""));
        using var gateway = new ZulipGateway(handler);

        await gateway.MarkReadAsync(new MarkReadRequest(Credentials, new DirectMessage([]), 99, 50));

        var form = ParseForm(Assert.Single(handler.Requests).Body);
        Assert.Equal("[{\"operator\":\"dm\",\"operand\":[7]},{\"operator\":\"is\",\"operand\":\"unread\"}]", form["narrow"]);
        Assert.Equal("99", form["anchor"]);
        Assert.Equal("read", form["flag"]);
        Assert.Equal("add", form["op"]);
        Assert.Equal("49", form["num_before"]);
    }

    [Fact]
    public async Task Send_preserves_raw_markdown_and_sends_local_echo_identifiers()
    {
        using var handler = new RecordingHandler(Json("""{"id":123,"future":"ignored"}"""));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.SendAsync(new SendRequest(Credentials, "queue-1", "77", new ChannelTopic(42, "topic"), "**raw** _markdown_"));

        var form = ParseForm(Assert.Single(handler.Requests).Body);
        Assert.Equal("channel", form["type"]);
        Assert.Equal("42", form["to"]);
        Assert.Equal("topic", form["topic"]);
        Assert.Equal("**raw** _markdown_", form["content"]);
        Assert.Equal("queue-1", form["queue_id"]);
        Assert.Equal("77", form["local_id"]);
        Assert.Equal("77", result.LocalId);
        Assert.Equal(123, result.MessageId);
    }

    [Fact]
    public async Task Send_accepts_an_opaque_local_id_as_required_by_the_Zulip_contract()
    {
        using var handler = new RecordingHandler(Json("""{"id":124}"""));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.SendAsync(
            new SendRequest(Credentials, "queue-1", "100.01", new DirectMessage([9]), "raw"));

        Assert.Equal("100.01", ParseForm(Assert.Single(handler.Requests).Body)["local_id"]);
        Assert.Equal("100.01", result.LocalId);
    }

    [Fact]
    public async Task Event_message_maps_local_echo_id()
    {
        using var handler = new RecordingHandler(Json("""{"events":[{"id":12,"type":"message","local_message_id":"77","flags":["read"],"message":{"id":123,"type":"stream","stream_id":42,"subject":"topic","sender_id":7,"content":"raw","timestamp":100}}]}"""));
        using var gateway = new ZulipGateway(handler);

        var batch = await gateway.GetEventsAsync(new GetEventsRequest(Credentials, "queue-1", 11, TimeSpan.FromSeconds(30)));

        var message = Assert.IsType<MessageUpsertEvent>(batch.Events[0]);
        Assert.Equal("77", message.LocalId);
        Assert.True(message.Message.IsRead);
        var topic = Assert.IsType<TopicUpsertEvent>(batch.Events[1]);
        Assert.Equal(new TopicSummary(42, "topic", 123), topic.Topic);
        Assert.Equal(12, batch.LastEventId);
        Assert.DoesNotContain("timeout", ParseQuery(Assert.Single(handler.Requests).Uri!).Keys);
    }

    [Theory]
    [InlineData(401, "{\"code\":\"UNAUTHORIZED\"}", GatewayErrorKind.ReauthRequired, GatewayErrorCode.Unauthorized)]
    [InlineData(429, "{\"code\":\"RATE_LIMIT_HIT\",\"retry-after\":12.5}", GatewayErrorKind.RateLimited, GatewayErrorCode.RateLimited)]
    [InlineData(400, "{\"code\":\"BAD_EVENT_QUEUE_ID\"}", GatewayErrorKind.QueueExpired, GatewayErrorCode.BadEventQueueId)]
    public async Task Failures_are_safe_typed_and_do_not_leak_secrets(int status, string body, GatewayErrorKind expectedKind, GatewayErrorCode expectedCode)
    {
        using var handler = new RecordingHandler(Json(body, (HttpStatusCode)status, retryAfter: status == 429 ? TimeSpan.FromSeconds(12) : null));
        using var gateway = new ZulipGateway(handler, new FixedTimeProvider());

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.GetEventsAsync(new GetEventsRequest(Credentials, "queue-secret", 1, TimeSpan.FromSeconds(30))));

        Assert.Equal(expectedKind, error.Kind);
        Assert.Equal(expectedCode, error.Code);
        Assert.DoesNotContain("api-key-secret", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("queue-secret", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(status == 429 ? TimeSpan.FromSeconds(12.5) : null, error.RetryAfter);
    }

    [Fact]
    public async Task Probe_WhenServerDeclaresIncompatible_ReturnsFailedCapabilityGate()
    {
        using var handler = new RecordingHandler(Json("""{"zulip_version":"12.1","zulip_feature_level":500,"is_incompatible":true,"email_auth_enabled":true}"""));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.ProbeRealmAsync(Realm);

        Assert.False(result.IsCompatible);
        Assert.True(result.IsIncompatible);
    }

    [Fact]
    public async Task Probe_WhenServerRedirects_RejectsWithoutFollowing()
    {
        var redirect = Json("{}", HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("https://other.example.test/api/v1/server_settings");
        using var handler = new RecordingHandler(redirect);
        using var gateway = new ZulipGateway(handler);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.ProbeRealmAsync(Realm));

        Assert.Equal(GatewayErrorKind.IncompatibleRealm, error.Kind);
        Assert.Equal(GatewayErrorCode.RedirectNotAllowed, error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Authenticate_WhenPasswordIsRejected_ReturnsSafeAuthenticationFailure()
    {
        using var handler = new RecordingHandler(Json("""{"result":"error","code":"INVALID_AUTHENTICATION","msg":"password-secret"}""", HttpStatusCode.BadRequest));
        using var gateway = new ZulipGateway(handler);

        var error = await Assert.ThrowsAsync<GatewayException>(() =>
            gateway.AuthenticateAsync(new AuthenticationRequest(Realm, "ada@example.test", "password-secret")));

        Assert.Equal(GatewayErrorKind.AuthenticationFailed, error.Kind);
        Assert.Equal(GatewayErrorCode.AuthenticationFailed, error.Code);
        Assert.DoesNotContain("password-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_WhenOnlyIdleQueueTimeoutIsPresent_FailsProtocolClosed()
    {
        using var handler = new RecordingHandler(Json("""{"queue_id":"q","last_event_id":1,"idle_queue_timeout_secs":3600,"max_message_length":10000,"max_topic_length":60}"""));
        using var gateway = new ZulipGateway(handler);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.RegisterAsync(new RegisterRequest(Credentials)));

        Assert.Equal(GatewayErrorKind.Protocol, error.Kind);
        Assert.Equal(GatewayErrorCode.InvalidResponse, error.Code);
    }

    [Fact]
    public async Task Event_DirectMessageReceived_UsesOtherUsersNotSenderOrCurrentUserIncorrectly()
    {
        using var handler = new RecordingHandler(Json("""
            {"events":[{"id":13,"type":"message","flags":[],"message":{"id":124,"type":"private","sender_id":9,"content":"raw","timestamp":100,
             "display_recipient":[{"id":7,"full_name":"Ada"},{"id":9,"full_name":"Grace"}]}}]}
            """));
        using var gateway = new ZulipGateway(handler);

        var batch = await gateway.GetEventsAsync(new GetEventsRequest(Credentials, "queue-1", 12, TimeSpan.FromSeconds(30)));

        var message = Assert.IsType<MessageUpsertEvent>(Assert.Single(batch.Events));
        var direct = Assert.IsType<DirectMessage>(message.Message.Conversation);
        Assert.Equal([9L], direct.OtherUserIds);
    }

    [Fact]
    public async Task Event_UpdateMessage_MapsContentAndBulkMoveAsOneEventIdGroup()
    {
        using var handler = new RecordingHandler(Json("""
            {"events":[{"id":20,"type":"update_message","message_id":100,"message_ids":[99,100],"content":"new raw","rendering_only":false,
             "stream_id":42,"new_stream_id":43,"orig_subject":"old","subject":"new"}]}
            """));
        using var gateway = new ZulipGateway(handler);

        var batch = await gateway.GetEventsAsync(new GetEventsRequest(Credentials, "queue-1", 19, TimeSpan.FromSeconds(30)));

        Assert.Collection(
            batch.Events,
            item =>
            {
                var content = Assert.IsType<MessageContentChangedEvent>(item);
                Assert.Equal(100, content.MessageId);
                Assert.Equal("new raw", content.Content);
                Assert.Equal(20, content.EventId);
            },
            item =>
            {
                var moved = Assert.IsType<MessageMovedEvent>(item);
                Assert.Equal([99L, 100L], moved.MessageIds);
                Assert.Equal(new ChannelTopic(43, "new"), moved.Destination);
                Assert.Equal(20, moved.EventId);
            },
            item =>
            {
                var topic = Assert.IsType<TopicUpsertEvent>(item);
                Assert.Equal(new TopicSummary(43, "new", 100), topic.Topic);
                Assert.Equal(20, topic.EventId);
            });
    }

    [Theory]
    [InlineData("[\"read\"]", MessageFlagOperation.Add)]
    [InlineData("[]", MessageFlagOperation.Remove)]
    public async Task Event_UpdateMessage_WhenFlagsArePresent_MapsReadState(
        string flags,
        MessageFlagOperation expectedOperation)
    {
        using var handler = new RecordingHandler(Json($$"""{"events":[{"id":23,"type":"update_message","message_id":100,"flags":{{flags}},"rendering_only":true}]}"""));
        using var gateway = new ZulipGateway(handler);

        var batch = await gateway.GetEventsAsync(
            new GetEventsRequest(Credentials, "queue-1", 22, TimeSpan.FromSeconds(30)));

        var changed = Assert.IsType<MessageFlagsChangedEvent>(Assert.Single(batch.Events));
        Assert.Equal([100L], changed.MessageIds);
        Assert.Equal(expectedOperation, changed.Operation);
        Assert.Equal("read", changed.Flag);
    }

    [Fact]
    public async Task Event_SubscriptionRemove_MapsEveryRevokedChannel()
    {
        using var handler = new RecordingHandler(Json("""
            {"events":[{"id":21,"type":"subscription","op":"remove","subscriptions":[{"stream_id":4,"name":"four"},{"stream_id":5,"name":"five"}]}]}
            """));
        using var gateway = new ZulipGateway(handler);

        var batch = await gateway.GetEventsAsync(new GetEventsRequest(Credentials, "queue-1", 20, TimeSpan.FromSeconds(30)));

        Assert.Equal(2, batch.Events.Count);
        Assert.All(batch.Events, item => Assert.True(Assert.IsType<SubscriptionChangedEvent>(item).IsRemoved));
    }

    [Fact]
    public async Task Event_SubscriptionUpdate_MapsMutedPreference()
    {
        using var handler = new RecordingHandler(Json("""{"events":[{"id":22,"type":"subscription","op":"update","stream_id":4,"property":"is_muted","value":true}]}"""));
        using var gateway = new ZulipGateway(handler);

        var batch = await gateway.GetEventsAsync(
            new GetEventsRequest(Credentials, "queue-1", 21, TimeSpan.FromSeconds(30)));

        var changed = Assert.IsType<SubscriptionPreferenceChangedEvent>(Assert.Single(batch.Events));
        Assert.Equal(4, changed.ChannelId);
        Assert.Equal(SubscriptionPreference.Muted, changed.Preference);
        Assert.True(changed.Value);
    }

    [Theory]
    [InlineData("false", false, false)]
    [InlineData("true", true, false)]
    [InlineData("\"invalid\"", null, true)]
    public async Task Event_StreamWebPublicUpdate_MapsBooleanAndFailsClosedOnMalformedValue(
        string value,
        bool? expected,
        bool clearEligibility)
    {
        using var handler = new RecordingHandler(Json($$"""
            {"events":[{"id":23,"type":"stream","op":"update","stream_id":4,"property":"is_web_public","value":{{value}}}]}
            """));
        using var gateway = new ZulipGateway(handler);

        var batch = await gateway.GetEventsAsync(
            new GetEventsRequest(Credentials, "queue-1", 22, TimeSpan.FromSeconds(30)));

        var changed = Assert.IsType<SubscriptionPatchedEvent>(Assert.Single(batch.Events));
        Assert.Equal(4, changed.ChannelId);
        Assert.Equal(expected, changed.IsWebPublic);
        Assert.Equal(clearEligibility, changed.ClearEligibility);
    }

    [Fact]
    public async Task Event_DeleteMessage_MapsBulkMessageIds()
    {
        using var handler = new RecordingHandler(Json("""{"events":[{"id":22,"type":"delete_message","message_ids":[3,8,13]}]}"""));
        using var gateway = new ZulipGateway(handler);

        var batch = await gateway.GetEventsAsync(new GetEventsRequest(Credentials, "queue-1", 21, TimeSpan.FromSeconds(30)));

        Assert.Equal([3L, 8L, 13L], Assert.IsType<MessageDeletedEvent>(Assert.Single(batch.Events)).MessageIds);
    }

    [Theory]
    [InlineData("self", "[7]")]
    [InlineData("group", "[9,10]")]
    public async Task Send_DirectMessage_UsesCanonicalRecipientIds(string kind, string expectedRecipients)
    {
        var conversation = kind == "self" ? new DirectMessage([]) : new DirectMessage([10, 9]);
        using var handler = new RecordingHandler(Json("""{"id":321}"""));
        using var gateway = new ZulipGateway(handler);

        await gateway.SendAsync(new SendRequest(Credentials, "queue-1", "78", conversation, "raw"));

        var form = ParseForm(Assert.Single(handler.Requests).Body);
        Assert.Equal("direct", form["type"]);
        Assert.Equal(expectedRecipients, form["to"]);
    }

    [Fact]
    public async Task Topics_RequestsEmptyTopicCapability()
    {
        using var handler = new RecordingHandler(Json("""{"topics":[{"name":"","max_id":44}]}"""));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.GetTopicsAsync(new TopicsRequest(Credentials, 42));

        Assert.Equal(string.Empty, Assert.Single(result.Topics).Topic);
        Assert.Equal("true", ParseQuery(Assert.Single(handler.Requests).Uri!)["allow_empty_topic_name"]);
    }

    [Fact]
    public async Task DeleteQueue_WhenQueueAlreadyExpired_TreatsCleanupAsSuccessful()
    {
        using var handler = new RecordingHandler(Json("""{"result":"error","code":"BAD_EVENT_QUEUE_ID"}""", HttpStatusCode.BadRequest));
        using var gateway = new ZulipGateway(handler);

        await gateway.DeleteQueueAsync(new DeleteQueueRequest(Credentials, "expired-queue"));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SetReaction_Add_UsesFullIdentityAndMessageEndpoint()
    {
        using var handler = new RecordingHandler(Json("""{"result":"success"}"""));
        using var gateway = new ZulipGateway(handler);
        var identity = new EmojiReactionIdentity("thumbs_up", "1f44d", "unicode_emoji");

        await gateway.SetReactionAsync(new SetReactionRequest(Credentials, 42, identity, true));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/api/v1/messages/42/reactions", request.Uri!.AbsolutePath, StringComparison.Ordinal);
        var form = ParseForm(request.Body);
        Assert.Equal("thumbs_up", form["emoji_name"]);
        Assert.Equal("1f44d", form["emoji_code"]);
        Assert.Equal("unicode_emoji", form["reaction_type"]);
    }

    [Fact]
    public async Task EditMessage_UsesPatchWithPreviousContentHash()
    {
        using var handler = new RecordingHandler(Json("""{"result":"success"}"""));
        using var gateway = new ZulipGateway(handler);

        await gateway.EditMessageAsync(new EditMessageRequest(Credentials, 43, "new raw", "abc123"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.EndsWith("/api/v1/messages/43", request.Uri!.AbsolutePath, StringComparison.Ordinal);
        var form = ParseForm(request.Body);
        Assert.Equal("new raw", form["content"]);
        Assert.Equal("abc123", form["prev_content_sha256"]);
    }

    [Fact]
    public async Task SetMessageStarred_UsesPerAccountFlagsEndpoint()
    {
        using var handler = new RecordingHandler(Json("""{"result":"success"}"""));
        using var gateway = new ZulipGateway(handler);

        await gateway.SetMessageStarredAsync(new SetMessageStarredRequest(Credentials, 44, true));

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/api/v1/messages/flags", request.Uri!.AbsolutePath, StringComparison.Ordinal);
        var form = ParseForm(request.Body);
        Assert.Equal("[44]", form["messages"]);
        Assert.Equal("add", form["op"]);
        Assert.Equal("starred", form["flag"]);
    }

    [Fact]
    public async Task UnsubscribeChannel_UsesExactNameForCurrentUserAndMapsResponse()
    {
        using var handler = new RecordingHandler(Json("""
            {"result":"success","removed":["工程频道"],"not_removed":[]}
            """));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.UnsubscribeChannelAsync(
            new UnsubscribeChannelRequest(Credentials, "工程频道"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.EndsWith("/api/v1/users/me/subscriptions", request.Uri!.AbsolutePath, StringComparison.Ordinal);
        var form = ParseForm(request.Body);
        var subscriptions = JsonSerializer.Deserialize<string[]>(form["subscriptions"]);
        Assert.NotNull(subscriptions);
        Assert.Equal(["工程频道"], subscriptions);
        Assert.Equal(["工程频道"], result.Removed);
        Assert.Empty(result.NotRemoved);
    }

    [Fact]
    public async Task AvailableChannels_UsesRawDescriptionAndSubscriberCount()
    {
        using var handler = new RecordingHandler(Json("""
            {"streams":[{"stream_id":7,"name":"engineering","description":"raw markdown","rendered_description":"<p>unsafe</p>","subscriber_count":12,"is_archived":false}]}
            """));
        using var gateway = new ZulipGateway(handler);

        var channels = await gateway.GetAvailableChannelsAsync(new AvailableChannelsRequest(Credentials));

        var channel = Assert.Single(channels);
        Assert.Equal("raw markdown", channel.Description);
        Assert.Equal(12, channel.SubscriberCount);
        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Requests).Method);
        Assert.EndsWith("/api/v1/streams", handler.Requests[0].Uri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscribeAndPreferences_UseOfficialEndpointsAndForms()
    {
        using var handler = new RecordingHandler(
            Json("""{"result":"success","subscribed":{"10":["engineering"]},"already_subscribed":{"10":[]},"unauthorized":{"10":[]}}"""),
            Json("""{"result":"success"}"""),
            Json("""{"result":"success"}"""));
        using var gateway = new ZulipGateway(handler);
        var channel = new ChannelSummary(8, "engineering", null, false, null);

        await gateway.SubscribeToChannelAsync(new SubscribeChannelRequest(Credentials, channel));
        await gateway.SetSubscriptionPreferenceAsync(new SetSubscriptionPreferenceRequest(Credentials, 8, SubscriptionPreference.Muted, true));
        await gateway.SetSubscriptionPreferenceAsync(new SetSubscriptionPreferenceRequest(Credentials, 8, SubscriptionPreference.Pinned, false));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.EndsWith("/api/v1/users/me/subscriptions", handler.Requests[0].Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("[{\"name\":\"engineering\"}]", ParseForm(handler.Requests[0].Body)["subscriptions"]);
        Assert.All(handler.Requests.Skip(1), request => Assert.Equal(HttpMethod.Patch, request.Method));
        Assert.All(handler.Requests.Skip(1), request => Assert.EndsWith("/api/v1/users/me/subscriptions/8", request.Uri!.AbsolutePath, StringComparison.Ordinal));
        Assert.Equal("is_muted", ParseForm(handler.Requests[1].Body)["property"]);
        Assert.Equal("true", ParseForm(handler.Requests[1].Body)["value"]);
        Assert.Equal("pin_to_top", ParseForm(handler.Requests[2].Body)["property"]);
        Assert.Equal("false", ParseForm(handler.Requests[2].Body)["value"]);
    }

    [Fact]
    public async Task Event_Reaction_MapsFullIdentityAndOperation()
    {
        using var handler = new RecordingHandler(Json("""
            {"events":[{"id":30,"type":"reaction","op":"add","message_id":42,"user_id":9,
             "user_full_name":"Grace","emoji_name":"thumbs_up","emoji_code":"1f44d","reaction_type":"unicode_emoji"}]}
            """));
        using var gateway = new ZulipGateway(handler);

        var batch = await gateway.GetEventsAsync(new GetEventsRequest(Credentials, "queue-1", 29, TimeSpan.FromSeconds(30)));

        var changed = Assert.IsType<MessageReactionChangedEvent>(Assert.Single(batch.Events));
        Assert.True(changed.Add);
        Assert.Equal(42, changed.MessageId);
        Assert.Equal("1f44d", changed.Reaction.Identity.EmojiCode);
        Assert.Equal(9, changed.Reaction.UserId);
    }

    [Fact]
    public async Task UploadAttachment_UsesMultipartFilenameAndAcceptsOnlySameRealmUploadUrl()
    {
        using var handler = new RecordingHandler(Json("""
            {"result":"success","filename":"design.png","url":"/user_uploads/7/ab/design.png"}
            """));
        using var gateway = new ZulipGateway(handler);
        await using var stream = new MemoryStream([1, 2, 3]);
        var reports = new List<RealmMediaTransferProgress>();

        var result = await gateway.UploadAttachmentAsync(new UploadAttachmentRequest(
            Credentials,
            new AttachmentUpload(
                "design.png",
                "image/png",
                3,
                stream,
                new InlineProgress<RealmMediaTransferProgress>(reports.Add))));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/api/v1/user_uploads", request.Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("name=filename", request.Body, StringComparison.Ordinal);
        Assert.Contains("filename=design.png", request.Body, StringComparison.Ordinal);
        Assert.Equal("https://chat.example.test/user_uploads/7/ab/design.png", result.Url);
        Assert.Equal(new RealmMediaTransferProgress(0, 3), reports[0]);
        Assert.Equal(new RealmMediaTransferProgress(3, 3), reports[^1]);
    }

    [Fact]
    public async Task UploadAttachment_WhenServerReturnsCrossRealmUrl_FailsClosed()
    {
        using var handler = new RecordingHandler(Json("""
            {"result":"success","filename":"design.png","url":"https://evil.example/user_uploads/design.png"}
            """));
        using var gateway = new ZulipGateway(handler);
        await using var stream = new MemoryStream([1]);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.UploadAttachmentAsync(
            new UploadAttachmentRequest(Credentials, new AttachmentUpload("design.png", "image/png", 1, stream))));

        Assert.Equal(GatewayErrorKind.Protocol, error.Kind);
    }

    [Fact]
    public async Task GetRealmMedia_Image_ResolvesWithAuthenticationThenFetchesTemporaryUrlWithoutCredentials()
    {
        using var handler = new RecordingHandler(
            Json("""{"result":"success","url":"/user_uploads/temporary/7/preview.png"}"""),
            Binary([1, 2, 3], "image/png"));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.GetRealmMediaAsync(new GetRealmMediaRequest(
            Credentials,
            new RealmMediaRequest(
                "https://chat.example.test/user_uploads/7/ab/preview.png",
                RealmMediaKind.Image,
                1024)));

        Assert.Equal([1, 2, 3], result.Content);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/v1/user_uploads/7/ab/preview.png", handler.Requests[0].Uri!.AbsolutePath);
        Assert.Equal("Basic", handler.Requests[0].Authorization!.Scheme);
        Assert.Equal("/user_uploads/temporary/7/preview.png", handler.Requests[1].Uri!.AbsolutePath);
        Assert.Null(handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task GetRealmMedia_WhenSourceOrTemporaryUrlLeavesRealm_FailsClosed()
    {
        using var sourceHandler = new RecordingHandler();
        using var sourceGateway = new ZulipGateway(sourceHandler);

        var sourceError = await Assert.ThrowsAsync<GatewayException>(() => sourceGateway.GetRealmMediaAsync(
            new GetRealmMediaRequest(
                Credentials,
                new RealmMediaRequest(
                    "https://evil.example/user_uploads/7/file.png",
                    RealmMediaKind.Image,
                    1024))));

        Assert.Equal(GatewayErrorKind.Protocol, sourceError.Kind);
        Assert.Empty(sourceHandler.Requests);

        using var temporaryHandler = new RecordingHandler(
            Json("""{"result":"success","url":"https://evil.example/user_uploads/temporary/file.png"}"""));
        using var temporaryGateway = new ZulipGateway(temporaryHandler);

        var temporaryError = await Assert.ThrowsAsync<GatewayException>(() => temporaryGateway.GetRealmMediaAsync(
            new GetRealmMediaRequest(
                Credentials,
                new RealmMediaRequest(
                    "/user_uploads/7/file.png",
                    RealmMediaKind.Image,
                    1024))));

        Assert.Equal(GatewayErrorKind.Protocol, temporaryError.Kind);
        Assert.Single(temporaryHandler.Requests);
    }

    [Fact]
    public async Task GetRealmMedia_WhenPayloadExceedsRequestedLimit_FailsWithoutReturningPartialContent()
    {
        using var handler = new RecordingHandler(
            Json("""{"result":"success","url":"/user_uploads/temporary/7/file.png"}"""),
            Binary([1, 2, 3, 4], "image/png"));
        using var gateway = new ZulipGateway(handler);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.GetRealmMediaAsync(
            new GetRealmMediaRequest(
                Credentials,
                new RealmMediaRequest("/user_uploads/7/file.png", RealmMediaKind.Image, 3))));

        Assert.Equal(GatewayErrorKind.Protocol, error.Kind);
    }

    [Fact]
    public async Task GetRealmMedia_Avatar_UsesAuthenticatedSameRealmReadAndRequiresImageMime()
    {
        using var handler = new RecordingHandler(Binary([9, 8], "image/webp"));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.GetRealmMediaAsync(new GetRealmMediaRequest(
            Credentials,
            new RealmMediaRequest("/user_avatars/7/avatar.png", RealmMediaKind.Avatar, 1024)));

        Assert.Equal([9, 8], result.Content);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://chat.example.test/user_avatars/7/avatar.png", request.Uri!.AbsoluteUri);
        Assert.Equal("Basic", request.Authorization!.Scheme);
    }

    [Fact]
    public async Task Register_UserTopicsAllowsEmptyNameAndConflictingAdminDeclarationFailsClosed()
    {
        using var handler = new RecordingHandler(Json("""
            {"queue_id":"queue-1","last_event_id":9,"event_queue_longpoll_timeout_seconds":90,"max_message_length":100,"max_topic_length":60,
             "subscriptions":[],"realm_users":[{"user_id":7,"full_name":"Ada","role":300}],"recent_private_conversations":[],"unread_msgs":{"count":0,"streams":[],"pms":[],"huddles":[]},
             "is_admin":true,"user_topics":[{"stream_id":42,"topic_name":"","visibility_policy":1}]}
            """));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.RegisterAsync(new RegisterRequest(Credentials));

        Assert.False(result.IsOrganizationAdministrator);
        var topic = Assert.Single(result.UserTopics!);
        Assert.Equal(string.Empty, topic.Topic);
        Assert.Equal(TopicVisibilityPolicy.Muted, topic.Policy);
    }

    [Fact]
    public async Task Register_CurrentMemberWithoutAdminRole_IsNotOrganizationAdministrator()
    {
        using var handler = new RecordingHandler(Json("""
            {"queue_id":"queue-1","last_event_id":9,"event_queue_longpoll_timeout_seconds":90,"max_message_length":100,"max_topic_length":60,
             "subscriptions":[],"realm_users":[{"user_id":7,"full_name":"Ada","role":300}],"recent_private_conversations":[],"unread_msgs":{"count":0,"streams":[],"pms":[],"huddles":[]}}
            """));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.RegisterAsync(new RegisterRequest(Credentials));

        Assert.False(result.IsOrganizationAdministrator);
    }

    [Fact]
    public async Task ChannelSettingsSnapshot_UsesAuthoritativeQueriesAndParsesGroupsWithoutEmailLogging()
    {
        using var handler = new RecordingHandler(
            Json("""{"user_id":7,"role":100}"""),
            Json("""{"streams":[{"stream_id":42,"name":"general","description":"desc"}]}"""),
            Json("""{"subscriptions":[{"stream_id":42}]}"""),
            Json("""{"channel_folders":[{"id":3,"name":"Work","description":"d"}]}"""),
            Json("""{"user_groups":[{"id":10,"name":"admins","members":[7],"direct_subgroup_ids":[],"deactivated":false}]}"""));
        using var gateway = new ZulipGateway(handler);

        var snapshot = await gateway.GetChannelSettingsSnapshotAsync(new ChannelSettingsSnapshotRequest(Credentials, new ChannelSettingsLimits(60, 1024, 60, 1024)));

        Assert.Equal(5, handler.Requests.Count);
        var query = ParseQuery(handler.Requests[1].Uri!);
        Assert.Equal("true", query["include_all"]);
        Assert.Equal("false", query["exclude_archived"]);
        Assert.Equal("false", ParseQuery(handler.Requests[2].Uri!)["include_subscribers"]);
        Assert.True(Assert.Single(snapshot.Channels).IsSubscribed);
        Assert.Equal(10, Assert.Single(snapshot.UserGroups).GroupId);
        Assert.True(snapshot.IsOrganizationAdministrator);
        Assert.Equal("Work", Assert.Single(snapshot.Folders).Name);
    }

    [Fact]
    public async Task ChannelDetails_ParsesNamedAndAnonymousPermissionGroups()
    {
        using var namedHandler = new RecordingHandler(Json("""{"stream":{"stream_id":42,"name":"general","can_administer_channel_group":10,"can_send_message_group":{"direct_members":[7],"direct_subgroups":[11]},"stream_weekly_traffic":9,"folder_id":3,"creator_id":7,"date_created":100}}"""));
        using var namedGateway = new ZulipGateway(namedHandler);
        var detail = await namedGateway.GetChannelDetailsAsync(new ChannelDetailsRequest(Credentials, 42));
        Assert.Equal(9, detail.WeeklyTraffic);
        Assert.IsType<NamedChannelGroupSetting>(detail.CanAdministerChannelGroup);
        var anonymous = Assert.IsType<AnonymousChannelGroupSetting>(detail.CanSendMessageGroup);
        Assert.Equal([7], anonymous.DirectMembers);

        using var malformedHandler = new RecordingHandler(Json("""{"stream":{"stream_id":42,"name":"general","can_administer_channel_group":"bad"}}"""));
        using var malformedGateway = new ZulipGateway(malformedHandler);
        var malformed = await malformedGateway.GetChannelDetailsAsync(new ChannelDetailsRequest(Credentials, 42));
        Assert.Null(malformed.CanAdministerChannelGroup);
    }

    [Fact]
    public async Task ChannelWrites_UseExactFormsAndDoNotRetryFailures()
    {
        using var handler = new RecordingHandler(Json("""{"result":"success"}"""), Json("""{"channel_folder_id":4}"""), Json("""{"email_address":"private@example.test"}"""), Json("""{"result":"success"}"""));
        using var gateway = new ZulipGateway(handler);
        await gateway.UpdateChannelAsync(new UpdateChannelRequest(Credentials, 42, "new", "description", null, true));
        await gateway.CreateChannelFolderAsync(new CreateChannelFolderRequest(Credentials, "Folder", "desc"));
        var email = await gateway.GetChannelEmailAddressAsync(new ChannelEmailAddressRequest(Credentials, 42));
        await gateway.ArchiveChannelAsync(new ArchiveChannelRequest(Credentials, 42));
        Assert.Equal("private@example.test", email);
        Assert.Equal(HttpMethod.Patch, handler.Requests[0].Method);
        var update = ParseForm(handler.Requests[0].Body);
        Assert.Equal("new", update["new_name"]);
        Assert.Equal("null", update["folder_id"]);
        Assert.Equal("/api/v1/channel_folders/create", handler.Requests[1].Uri!.AbsolutePath);
        Assert.Equal("desc", ParseForm(handler.Requests[1].Body)["description"]);
        Assert.Equal("/api/v1/streams/42/email_address", handler.Requests[2].Uri!.AbsolutePath);
        Assert.Equal(HttpMethod.Delete, handler.Requests[3].Method);

        using var failureHandler = new RecordingHandler(Json("""{"code":"BAD_REQUEST","msg":"no"}""", HttpStatusCode.BadRequest));
        using var failureGateway = new ZulipGateway(failureHandler);
        await Assert.ThrowsAsync<GatewayException>(() => failureGateway.ArchiveChannelAsync(new ArchiveChannelRequest(Credentials, 42)));
        Assert.Single(failureHandler.Requests);
    }

    [Fact]
    public async Task TopicOperations_UseOfficialEndpointsFormsAndSingleWriteAttempts()
    {
        using var handler = new RecordingHandler(
            Json("""{"result":"success"}"""),
            Json("""{"last_processed_id":11,"found_newest":false}"""),
            Json("""{"messages":[{"id":9}]}"""),
            Json("""{"result":"success"}"""),
            Json("""{"complete":false}"""));
        using var gateway = new ZulipGateway(handler);
        var source = new ChannelTopic(42, "private topic");
        await gateway.SetTopicVisibilityPolicyAsync(new SetTopicVisibilityPolicyRequest(Credentials, source, TopicVisibilityPolicy.Followed));
        var read = await gateway.MarkTopicReadAsync(new MarkTopicReadRequest(Credentials, source));
        var anchor = await gateway.ResolveTopicAnchorAsync(new ResolveTopicAnchorRequest(Credentials, source));
        await gateway.MoveTopicAsync(new MoveTopicRequest(Credentials, source, anchor.MessageId!.Value, new ChannelTopic(43, "renamed")));
        var deleted = await gateway.DeleteTopicAsync(new DeleteTopicRequest(Credentials, source));

        Assert.False(deleted.Complete);
        Assert.Equal(11, read.LastProcessedMessageId);
        Assert.Equal(9, anchor.MessageId);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/v1/user_topics", handler.Requests[0].Uri!.AbsolutePath);
        var policy = ParseForm(handler.Requests[0].Body);
        Assert.Equal("42", policy["stream_id"]);
        Assert.Equal("private topic", policy["topic"]);
        Assert.Equal("3", policy["visibility_policy"]);
        var readForm = ParseForm(handler.Requests[1].Body);
        Assert.Equal("oldest", readForm["anchor"]);
        Assert.Equal("false", readForm["include_anchor"]);
        Assert.Equal("1000", readForm["num_after"]);
        Assert.Contains("unread", readForm["narrow"], StringComparison.Ordinal);
        var anchorQuery = ParseQuery(handler.Requests[2].Uri!);
        Assert.Equal("oldest", anchorQuery["anchor"]);
        Assert.Equal("1", anchorQuery["num_after"]);
        Assert.Equal("/api/v1/messages/9", handler.Requests[3].Uri!.AbsolutePath);
        var move = ParseForm(handler.Requests[3].Body);
        Assert.Equal("change_all", move["propagate_mode"]);
        Assert.Equal("43", move["stream_id"]);
        Assert.Equal("false", move["send_notification_to_old_thread"]);
        Assert.Equal("true", move["send_notification_to_new_thread"]);
        Assert.Equal("/api/v1/streams/42/delete_topic", handler.Requests[4].Uri!.AbsolutePath);
        Assert.DoesNotContain("private topic", new SetTopicVisibilityPolicyRequest(Credentials, source, TopicVisibilityPolicy.Muted).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private topic", new DeleteTopicRequest(Credentials, source).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetTopicVisibilityPolicy_WhenServerIgnoresParameter_FailsClosedWithoutRetry()
    {
        using var handler = new RecordingHandler(Json("""{"result":"success","ignored_parameters_unsupported":["visibility_policy"]}"""));
        using var gateway = new ZulipGateway(handler);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.SetTopicVisibilityPolicyAsync(
            new SetTopicVisibilityPolicyRequest(Credentials, new ChannelTopic(42, string.Empty), TopicVisibilityPolicy.Muted)));

        Assert.Equal(GatewayErrorKind.Protocol, error.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ChannelSettingsSnapshot_WhenCurrentUserIsGuest_SkipsUnavailableUserGroups()
    {
        using var handler = new RecordingHandler(
            Json("""{"user_id":7,"role":600,"is_guest":true}"""),
            Json("""{"streams":[{"stream_id":42,"name":"guest-visible"}]}"""),
            Json("""{"subscriptions":[]}"""),
            Json("""{"channel_folders":[]}"""));
        using var gateway = new ZulipGateway(handler);

        var snapshot = await gateway.GetChannelSettingsSnapshotAsync(
            new ChannelSettingsSnapshotRequest(Credentials, new ChannelSettingsLimits(null, null, null, null)));

        Assert.True(snapshot.IsGuest);
        Assert.Empty(snapshot.UserGroups);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Theory]
    [InlineData("{}", "{\"subscriptions\":[]}")]
    [InlineData("{\"streams\":{}}", "{\"subscriptions\":[]}")]
    [InlineData("{\"result\":\"error\",\"streams\":[]}", "{\"subscriptions\":[]}")]
    public async Task ChannelSettingsSnapshot_WhenStreamsAreInvalid_FailsClosed(string streamsJson, string subscriptionsJson)
    {
        using var handler = new RecordingHandler(
            Json("""{"user_id":7,"role":400}"""),
            Json(streamsJson),
            Json(subscriptionsJson));
        using var gateway = new ZulipGateway(handler);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.GetChannelSettingsSnapshotAsync(
            new ChannelSettingsSnapshotRequest(Credentials, new ChannelSettingsLimits(null, null, null, null))));

        Assert.Equal(GatewayErrorKind.Protocol, error.Kind);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"subscriptions\":[{\"stream_id\":0}]}")]
    [InlineData("{\"subscriptions\":[{\"stream_id\":42},{\"stream_id\":42}]}")]
    [InlineData("{\"result\":\"error\",\"subscriptions\":[]}")]
    [InlineData("{\"subscriptions\":[{\"stream_id\":99}]}")]
    public async Task ChannelSettingsSnapshot_WhenSubscriptionsAreInvalid_FailsClosed(string subscriptionsJson)
    {
        using var handler = new RecordingHandler(
            Json("""{"user_id":7,"role":400}"""),
            Json("""{"streams":[{"stream_id":42,"name":"general"}]}"""),
            Json(subscriptionsJson));
        using var gateway = new ZulipGateway(handler);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.GetChannelSettingsSnapshotAsync(
            new ChannelSettingsSnapshotRequest(Credentials, new ChannelSettingsLimits(null, null, null, null))));

        Assert.Equal(GatewayErrorKind.Protocol, error.Kind);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("{\"role\":100}")]
    [InlineData("{\"user_id\":8,\"role\":100}")]
    [InlineData("{\"user_id\":7,\"role\":400,\"is_admin\":true}")]
    [InlineData("{\"user_id\":7,\"role\":600,\"is_guest\":false}")]
    public async Task ChannelSettingsSnapshot_WhenOwnUserIdentityOrRoleIsInconsistent_FailsClosed(string ownUserJson)
    {
        using var handler = new RecordingHandler(Json(ownUserJson));
        using var gateway = new ZulipGateway(handler);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.GetChannelSettingsSnapshotAsync(
            new ChannelSettingsSnapshotRequest(Credentials, new ChannelSettingsLimits(null, null, null, null))));

        Assert.Equal(GatewayErrorKind.Protocol, error.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UpdateChannel_WhenServerIgnoresUnsupportedField_FailsClosedWithoutRetry()
    {
        using var handler = new RecordingHandler(Json("""{"result":"success","ignored_parameters_unsupported":["folder_id"]}"""));
        using var gateway = new ZulipGateway(handler);

        await Assert.ThrowsAsync<GatewayException>(() => gateway.UpdateChannelAsync(
            new UpdateChannelRequest(Credentials, 42, null, null, 3)));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CompleteChannelSettings_UseOfficialFormsAndFailClosedResponses()
    {
        using var handler = new RecordingHandler(
            Json("""{"result":"success","id":44}"""),
            Json("""{"subscriptions":[{"stream_id":42,"color":"#123456","is_muted":true,"pin_to_top":false,"desktop_notifications":null,"audible_notifications":true,"push_notifications":false,"email_notifications":null,"wildcard_mentions_notify":true}]}"""),
            Json("""{"result":"success"}"""),
            Json("""{"subscribers":[7,8]}"""),
            Json("""{"result":"success"}"""),
            Json("""{"result":"success"}"""),
            Json("""{"result":"success"}"""));
        using var gateway = new ZulipGateway(handler);
        await gateway.CreateChannelAsync(new CreateChannelRequest(Credentials, new ChannelCreateOptions("new", "desc", true, false, true, false)));
        var personal = await gateway.GetChannelPersonalSettingsAsync(new ChannelMembersRequest(Credentials, 42));
        await gateway.SetChannelPersonalSettingAsync(new SetChannelPersonalSettingRequest(Credentials, 42, new ChannelPersonalSettingChange(ChannelPersonalSetting.Color, "#abcdef")));
        var members = await gateway.GetChannelMemberIdsAsync(new ChannelMembersRequest(Credentials, 42));
        await gateway.ModifyChannelMembersAsync(new ModifyChannelMembersRequest(Credentials, "new", [8], true, true));
        await gateway.ModifyChannelMembersAsync(new ModifyChannelMembersRequest(Credentials, "new", [8], false, false));
        await gateway.UpdateChannelAdvancedSettingsAsync(new UpdateChannelAdvancedRequest(Credentials, 42, new ChannelAdvancedSettingsChange(IsArchived: false, TopicsPolicy: ChannelTopicsPolicy.AllowEmptyTopic, RetentionPolicy: ChannelRetentionPolicy.ForDays(30), GroupSetting: ChannelGroupSettingName.CanRemoveSubscribers, NewGroup: new NamedChannelGroupSetting(9), OldGroup: new NamedChannelGroupSetting(8))));

        Assert.True(personal.IsMuted);
        Assert.Equal([7, 8], members);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/v1/channels/create", handler.Requests[0].Uri!.AbsolutePath);
        var create = ParseForm(handler.Requests[0].Body);
        Assert.Equal("new", create["name"]);
        Assert.Equal("desc", create["description"]);
        Assert.Equal("true", create["invite_only"]);
        Assert.False(create.ContainsKey("is_private"));
        Assert.Equal("true", create["history_public_to_subscribers"]);
        Assert.Equal("false", create["is_default_stream"]);
        using (var subscribers = JsonDocument.Parse(create["subscribers"]))
            Assert.Equal(Credentials.UserId, Assert.Single(subscribers.RootElement.EnumerateArray()).GetInt64());
        Assert.Equal("false", ParseQuery(handler.Requests[1].Uri!)["include_subscribers"]);
        Assert.Equal("color", ParseForm(handler.Requests[2].Body)["property"]);
        Assert.Equal("/api/v1/streams/42/members", handler.Requests[3].Uri!.AbsolutePath);
        var add = ParseForm(handler.Requests[4].Body);
        Assert.Equal("true", add["authorization_errors_fatal"]);
        Assert.Equal("true", add["send_new_subscription_messages"]);
        Assert.Equal(HttpMethod.Delete, handler.Requests[5].Method);
        var advanced = ParseForm(handler.Requests[6].Body);
        Assert.Equal("false", advanced["is_archived"]);
        Assert.Equal("allow_empty_topic", advanced["topics_policy"]);
        Assert.Contains("\"new\":9", advanced["can_remove_subscribers_group"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"members\":[7]}")]
    [InlineData("{\"subscribers\":[7,\"bad\"]}")]
    [InlineData("{\"result\":\"error\",\"subscribers\":[]}")]
    public async Task GetChannelMembers_WhenSubscribersIsInvalid_FailsClosed(string payload)
    {
        using var handler = new RecordingHandler(Json(payload));
        using var gateway = new ZulipGateway(handler);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.GetChannelMemberIdsAsync(new ChannelMembersRequest(Credentials, 42)));

        Assert.Equal(GatewayErrorKind.Protocol, error.Kind);
    }

    [Fact]
    public async Task UploadAttachment_WhenLargeTransferDisconnects_ResumesFromAuthoritativeTusOffset()
    {
        const long length = 25L * 1024 * 1024;
        using var handler = new ResumableUploadHandler(length, disconnectFirstPatch: true);
        using var gateway = new ZulipGateway(handler);
        await using var stream = new FixedLengthReadStream(length);
        var reports = new List<RealmMediaTransferProgress>();

        var result = await gateway.UploadAttachmentAsync(new UploadAttachmentRequest(
            Credentials,
            new AttachmentUpload(
                "large.bin",
                "application/octet-stream",
                length,
                stream,
                new InlineProgress<RealmMediaTransferProgress>(reports.Add))));

        Assert.Equal("https://chat.example.test/user_uploads/7/ab/large.bin", result.Url);
        Assert.Equal(1, handler.CreationCount);
        Assert.Equal(1, handler.HeadCount);
        Assert.Equal(0, handler.PatchOffsets[0]);
        Assert.Equal(1024 * 1024, handler.PatchOffsets[1]);
        Assert.Equal(new RealmMediaTransferProgress(length, length), reports[^1]);
        Assert.Contains(reports, report => report.BytesTransferred == 2L * 1024 * 1024);
        Assert.Contains(reports, report => report.BytesTransferred == 1024L * 1024);
        Assert.Equal("1.0.0", handler.TusVersion);
        Assert.Contains("filename bGFyZ2UuYmlu", handler.UploadMetadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadAttachment_WhenTusCreationReturnsCrossRealmLocation_FailsClosedWithoutRetry()
    {
        const long length = 25L * 1024 * 1024;
        using var response = new HttpResponseMessage(HttpStatusCode.Created);
        response.Headers.Location = new Uri("https://evil.example/api/v1/tus/7/ab/large.bin");
        using var handler = new RecordingHandler(response);
        using var gateway = new ZulipGateway(handler);
        await using var stream = new FixedLengthReadStream(length);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.UploadAttachmentAsync(
            new UploadAttachmentRequest(
                Credentials,
                new AttachmentUpload("large.bin", "application/octet-stream", length, stream))));

        Assert.Equal(GatewayErrorKind.Protocol, error.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UploadAttachment_WhenTusPatchKeepsDisconnecting_StopsAfterBoundedRecovery()
    {
        const long length = 25L * 1024 * 1024;
        using var handler = new AlwaysDisconnectTusHandler();
        using var gateway = new ZulipGateway(handler);
        await using var stream = new FixedLengthReadStream(length);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.UploadAttachmentAsync(
            new UploadAttachmentRequest(
                Credentials,
                new AttachmentUpload("large.bin", "application/octet-stream", length, stream))));

        Assert.Equal(GatewayErrorKind.Offline, error.Kind);
        Assert.Equal(1, handler.CreationCount);
        Assert.Equal(4, handler.PatchCount);
        Assert.Equal(3, handler.HeadCount);
    }

    [Fact]
    public async Task DownloadRealmMedia_File_StreamsExactBytesAndReportsLengthProgress()
    {
        using var handler = new RecordingHandler(
            Json("""{"result":"success","url":"/user_uploads/temporary/7/guide.pdf"}"""),
            Binary([1, 2, 3, 4], "application/pdf"));
        using var gateway = new ZulipGateway(handler);
        await using var destination = new MemoryStream();
        var reports = new List<RealmMediaTransferProgress>();

        var result = await gateway.DownloadRealmMediaAsync(
            new GetRealmMediaRequest(
                Credentials,
                new RealmMediaRequest("/user_uploads/7/guide.pdf", RealmMediaKind.File, 1024)),
            destination,
            new InlineProgress<RealmMediaTransferProgress>(reports.Add));

        Assert.Equal([1, 2, 3, 4], destination.ToArray());
        Assert.Equal(4, result.Length);
        Assert.Equal("application/pdf", result.ContentType);
        Assert.Equal(new RealmMediaTransferProgress(0, 4), reports[0]);
        Assert.Equal(new RealmMediaTransferProgress(4, 4), reports[^1]);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Basic", handler.Requests[0].Authorization!.Scheme);
        Assert.Null(handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task DownloadRealmMedia_WhenDeclaredLengthExceedsLimit_FailsBeforeWriting()
    {
        using var handler = new RecordingHandler(
            Json("""{"result":"success","url":"/user_uploads/temporary/7/guide.pdf"}"""),
            Binary([1, 2, 3, 4], "application/pdf"));
        using var gateway = new ZulipGateway(handler);
        await using var destination = new MemoryStream();

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.DownloadRealmMediaAsync(
            new GetRealmMediaRequest(
                Credentials,
                new RealmMediaRequest("/user_uploads/7/guide.pdf", RealmMediaKind.File, 3)),
            destination));

        Assert.Equal(GatewayErrorKind.Protocol, error.Kind);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task GetRealmUsers_WhenResponseIsValid_ReturnsStrictUserProfiles()
    {
        using var handler = new RecordingHandler(Json("""{"members":[{"user_id":7,"full_name":"Ada Lovelace","email":"ada@example.test","is_active":true,"is_bot":false,"avatar_url":"https://example.test/a.png","avatar_version":2},{"user_id":8,"full_name":"Build Bot","email":"bot@example.test","is_active":false,"is_bot":true}]}"""));
        using var gateway = new ZulipGateway(handler);

        var users = await gateway.GetRealmUsersAsync(new RealmUsersRequest(Credentials));

        Assert.Equal("/api/v1/users", Assert.Single(handler.Requests).Uri!.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Collection(users,
            user =>
            {
                Assert.Equal(7, user.UserId);
                Assert.Equal("Ada Lovelace", user.FullName);
                Assert.Equal("ada@example.test", user.Email);
                Assert.True(user.IsActive);
                Assert.False(user.IsBot);
                Assert.Equal(2, user.AvatarVersion);
            },
            user =>
            {
                Assert.Equal(8, user.UserId);
                Assert.False(user.IsActive);
                Assert.True(user.IsBot);
            });
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"members\":{}}")]
    [InlineData("{\"result\":\"error\",\"members\":[]}")]
    [InlineData("{\"members\":[{\"user_id\":7,\"full_name\":\"Ada\",\"email\":\"ada@example.test\",\"is_active\":true}]} ")]
    [InlineData("{\"members\":[{\"user_id\":0,\"full_name\":\"Ada\",\"email\":\"ada@example.test\",\"is_active\":true,\"is_bot\":false}]}")]
    [InlineData("{\"members\":[{\"user_id\":7,\"full_name\":\"\",\"email\":\"ada@example.test\",\"is_active\":true,\"is_bot\":false}]}")]
    [InlineData("{\"members\":[{\"user_id\":7,\"full_name\":\"Ada\",\"email\":\"\",\"is_active\":true,\"is_bot\":false}]}")]
    [InlineData("{\"members\":[{\"user_id\":7,\"full_name\":\"Ada\",\"email\":\"ada@example.test\",\"is_active\":\"true\",\"is_bot\":false}]}")]
    [InlineData("{\"members\":[{\"user_id\":7,\"full_name\":\"Ada\",\"email\":\"ada@example.test\",\"is_active\":true,\"is_bot\":false},{\"user_id\":7,\"full_name\":\"Grace\",\"email\":\"grace@example.test\",\"is_active\":true,\"is_bot\":false}]}")]
    public async Task GetRealmUsers_WhenResponseIsMalformed_FailsClosed(string payload)
    {
        using var handler = new RecordingHandler(Json(payload));
        using var gateway = new ZulipGateway(handler);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.GetRealmUsersAsync(new RealmUsersRequest(Credentials)));

        Assert.Equal(GatewayErrorKind.Protocol, error.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetRealmUsers_WhenServerReturnsUnsupportedParameters_FailsClosed()
    {
        using var handler = new RecordingHandler(Json("""{"members":[],"ignored_parameters_unsupported":["unexpected"]}"""));
        using var gateway = new ZulipGateway(handler);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.GetRealmUsersAsync(new RealmUsersRequest(Credentials)));

        Assert.Equal(GatewayErrorKind.Protocol, error.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ModifyChannelMembers_WhenAddingOrRemoving_UsesOfficialSubscriptionShapes()
    {
        using var handler = new RecordingHandler(Json("""{"result":"success"}"""), Json("""{"result":"success"}"""));
        using var gateway = new ZulipGateway(handler);

        await gateway.ModifyChannelMembersAsync(new ModifyChannelMembersRequest(Credentials, "engineering", [7], true, false));
        await gateway.ModifyChannelMembersAsync(new ModifyChannelMembersRequest(Credentials, "engineering", [7], false, false));

        var add = ParseForm(handler.Requests[0].Body);
        using var addSubscriptions = JsonDocument.Parse(add["subscriptions"]);
        var addChannel = Assert.Single(addSubscriptions.RootElement.EnumerateArray());
        Assert.Equal(JsonValueKind.Object, addChannel.ValueKind);
        Assert.Equal("engineering", addChannel.GetProperty("name").GetString());

        var remove = ParseForm(handler.Requests[1].Body);
        using var removeSubscriptions = JsonDocument.Parse(remove["subscriptions"]);
        var removeChannel = Assert.Single(removeSubscriptions.RootElement.EnumerateArray());
        Assert.Equal(JsonValueKind.String, removeChannel.ValueKind);
        Assert.Equal("engineering", removeChannel.GetString());
    }

    [Fact]
    public async Task CreateChannel_WhenPrivateHistoryIsShared_AllowsOfficialCombination()
    {
        using var handler = new RecordingHandler(Json("""{"result":"success","id":50}"""));
        using var gateway = new ZulipGateway(handler);

        var channelId = await gateway.CreateChannelAsync(new CreateChannelRequest(Credentials, new ChannelCreateOptions("private", null, true, false, true, false)));

        Assert.Equal(50, channelId);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateChannel_WhenSuccessResponseHasNoChannelId_FailsClosedWithoutRetry()
    {
        using var handler = new RecordingHandler(Json("""{"result":"success"}"""));
        using var gateway = new ZulipGateway(handler);

        await Assert.ThrowsAsync<GatewayException>(() => gateway.CreateChannelAsync(
            new CreateChannelRequest(Credentials, new ChannelCreateOptions("public", null, false, false, true, false))));

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("{\"result\":\"error\",\"id\":50}")]
    [InlineData("{\"result\":\"unexpected\",\"id\":50}")]
    [InlineData("{\"result\":\"success\",\"id\":50,\"ignored_parameters_unsupported\":[\"invite_only\"]}")]
    public async Task CreateChannel_WhenResponseIsNotStrictlySupported_FailsClosedWithoutRetry(string json)
    {
        using var handler = new RecordingHandler(Json(json));
        using var gateway = new ZulipGateway(handler);

        await Assert.ThrowsAsync<GatewayException>(() => gateway.CreateChannelAsync(
            new CreateChannelRequest(Credentials, new ChannelCreateOptions("public", null, false, false, true, false))));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateChannel_WhenPublicHistoryIsNotShared_FailsBeforeNetwork()
    {
        using var handler = new RecordingHandler(Json("""{"result":"success"}"""));
        using var gateway = new ZulipGateway(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => gateway.CreateChannelAsync(new CreateChannelRequest(Credentials, new ChannelCreateOptions("public", null, false, false, false, false))));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreatePrivateGroup_UsesOneStrictPrivateEmptyTopicRequestWithInitialMembersAndOwnerGroups()
    {
        using var handler = new RecordingHandler(Json("""{"result":"success","id":61}"""));
        using var gateway = new ZulipGateway(handler);

        var channelId = await gateway.CreatePrivateGroupAsync(new PrivateGroupCreateRequest(
            Credentials,
            new PrivateGroupCreateOptions("  产品设计群  ", [9, 8])));

        Assert.Equal(61, channelId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/channels/create", request.Uri!.AbsolutePath);
        var form = ParseForm(request.Body);
        Assert.Equal("产品设计群", form["name"]);
        Assert.Equal(string.Empty, form["description"]);
        Assert.Equal("false", form["announce"]);
        Assert.Equal("true", form["invite_only"]);
        Assert.Equal("false", form["is_web_public"]);
        Assert.Equal("false", form["is_default_stream"]);
        Assert.Equal("true", form["history_public_to_subscribers"]);
        Assert.Equal("\"empty_topic_only\"", form["topics_policy"]);
        using (var topicsPolicy = JsonDocument.Parse(form["topics_policy"]))
            Assert.Equal("empty_topic_only", topicsPolicy.RootElement.GetString());
        using (var subscribers = JsonDocument.Parse(form["subscribers"]))
        {
            Assert.Equal([7L, 8L, 9L], subscribers.RootElement.EnumerateArray().Select(static item => item.GetInt64()));
        }
        foreach (var field in new[]
                 {
                     "can_administer_channel_group",
                     "can_add_subscribers_group",
                     "can_remove_subscribers_group"
                 })
        {
            using var owner = JsonDocument.Parse(form[field]);
            Assert.Equal([7L], owner.RootElement.GetProperty("direct_members").EnumerateArray().Select(static item => item.GetInt64()));
            Assert.Empty(owner.RootElement.GetProperty("direct_subgroups").EnumerateArray());
        }
        Assert.DoesNotContain("is_private", form.Keys);
    }

    [Theory]
    [InlineData("{\"result\":\"success\"}")]
    [InlineData("{\"result\":\"error\",\"id\":61}")]
    [InlineData("{\"result\":\"success\",\"id\":61,\"ignored_parameters_unsupported\":[\"topics_policy\"]}")]
    public async Task CreatePrivateGroup_WhenResponseIsUncertain_FailsClosedWithoutRetry(string payload)
    {
        using var handler = new RecordingHandler(Json(payload));
        using var gateway = new ZulipGateway(handler);

        await Assert.ThrowsAsync<GatewayException>(() => gateway.CreatePrivateGroupAsync(new PrivateGroupCreateRequest(
            Credentials,
            new PrivateGroupCreateOptions("group", [8, 9]))));

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(8, 8)]
    [InlineData(7, 8)]
    public async Task CreatePrivateGroup_WhenInitialMembersAreInvalid_FailsBeforeNetwork(long first, long second)
    {
        using var handler = new RecordingHandler(Json("""{"result":"success","id":61}"""));
        using var gateway = new ZulipGateway(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => gateway.CreatePrivateGroupAsync(new PrivateGroupCreateRequest(
            Credentials,
            new PrivateGroupCreateOptions("group", [first, second]))));

        Assert.Empty(handler.Requests);
    }

    private static string RegisterPayload(string permission, string groups) => $$"""
        {
          "queue_id":"queue-1",
          "last_event_id":9,
          "event_queue_longpoll_timeout_seconds":90,
          "max_message_length":10000,
          "max_topic_length":60,
          "subscriptions":[],
          "realm_users":[{"user_id":7,"full_name":"Ada","email":"ada@example.test"}],
          "realm_can_create_private_channel_group":{{permission}},
          "realm_user_groups":{{groups}}
        }
        """;

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (retryAfter is { } retry) response.Headers.RetryAfter = new RetryConditionHeaderValue(retry);
        return response;
    }

    private static HttpResponseMessage Binary(byte[] content, string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }

    private static Dictionary<string, string> ParseForm(string form) => form.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split('=', 2)).ToDictionary(parts => DecodeForm(parts[0]), parts => DecodeForm(parts[1]), StringComparer.Ordinal);
    private static Dictionary<string, string> ParseQuery(Uri uri) => ParseForm(uri.Query.TrimStart('?'));
    private static string DecodeForm(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<CapturedRequest> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri, request.Headers.Authorization, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return _responses.Dequeue();
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri? Uri, AuthenticationHeaderValue? Authorization, string Body);

    private sealed class ResumableUploadHandler(long totalLength, bool disconnectFirstPatch) : HttpMessageHandler
    {
        private bool _disconnectFirstPatch = disconnectFirstPatch;
        private long _serverOffset;

        public int CreationCount { get; private set; }
        public int HeadCount { get; private set; }
        public List<long> PatchOffsets { get; } = [];
        public string? TusVersion { get; private set; }
        public string? UploadMetadata { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            TusVersion ??= Assert.Single(request.Headers.GetValues("Tus-Resumable"));
            if (request.Method == HttpMethod.Post)
            {
                CreationCount++;
                UploadMetadata = Assert.Single(request.Headers.GetValues("Upload-Metadata"));
                Assert.Equal(totalLength.ToString(CultureInfo.InvariantCulture), Assert.Single(request.Headers.GetValues("Upload-Length")));
                var created = new HttpResponseMessage(HttpStatusCode.Created);
                created.Headers.Location = new Uri("/api/v1/tus/7/ab/large.bin", UriKind.Relative);
                return created;
            }

            if (request.Method == HttpMethod.Head)
            {
                HeadCount++;
                return WithTusOffset(new HttpResponseMessage(HttpStatusCode.OK), _serverOffset);
            }

            Assert.Equal(HttpMethod.Patch, request.Method);
            var patchOffset = long.Parse(
                Assert.Single(request.Headers.GetValues("Upload-Offset")),
                CultureInfo.InvariantCulture);
            PatchOffsets.Add(patchOffset);
            Assert.Equal(_serverOffset, patchOffset);
            if (_disconnectFirstPatch)
            {
                _disconnectFirstPatch = false;
                await ReadExactlyAsync(request.Content!, 2 * 1024 * 1024, cancellationToken);
                _serverOffset = 1024 * 1024;
                throw new HttpRequestException("simulated connection loss");
            }

            await request.Content!.CopyToAsync(Stream.Null, cancellationToken);
            _serverOffset += request.Content.Headers.ContentLength!.Value;
            var isComplete = _serverOffset == totalLength;
            var response = isComplete
                ? Json("""{"url":"/user_uploads/7/ab/large.bin","filename":"large.bin"}""")
                : new HttpResponseMessage(HttpStatusCode.NoContent);
            return WithTusOffset(response, _serverOffset);
        }

        private static HttpResponseMessage WithTusOffset(HttpResponseMessage response, long offset)
        {
            response.Headers.TryAddWithoutValidation("Upload-Offset", offset.ToString(CultureInfo.InvariantCulture));
            return response;
        }

        private static async Task ReadExactlyAsync(
            HttpContent content,
            int byteCount,
            CancellationToken cancellationToken)
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[64 * 1024];
            var remaining = byteCount;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
                Assert.True(read > 0);
                remaining -= read;
            }
        }
    }

    private sealed class FixedLengthReadStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > length) throw new ArgumentOutOfRangeException(nameof(value));
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = (int)Math.Min(buffer.Length, length - _position);
            buffer[..read].Clear();
            _position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            Position = target;
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class AlwaysDisconnectTusHandler : HttpMessageHandler
    {
        public int CreationCount { get; private set; }
        public int PatchCount { get; private set; }
        public int HeadCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                CreationCount++;
                var response = new HttpResponseMessage(HttpStatusCode.Created);
                response.Headers.Location = new Uri("/api/v1/tus/7/ab/large.bin", UriKind.Relative);
                return Task.FromResult(response);
            }
            if (request.Method == HttpMethod.Head)
            {
                HeadCount++;
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation("Upload-Offset", "0");
                return Task.FromResult(response);
            }
            PatchCount++;
            throw new HttpRequestException("simulated persistent connection loss");
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
