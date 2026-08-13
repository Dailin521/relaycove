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
             "realm_users":[{"user_id":7,"full_name":"Ada","email":"ada@example.test"}],
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
        Assert.Equal("[\"subscription\",\"realm_user\",\"realm\",\"recent_private_conversations\"]", form["fetch_event_types"]);
        Assert.Contains("\"bulk_message_deletion\":true", form["client_capabilities"], StringComparison.Ordinal);
        Assert.Contains("\"archived_channels\":true", form["client_capabilities"], StringComparison.Ordinal);
        Assert.Equal("queue-1", result.QueueId);
        Assert.Equal(TimeSpan.FromSeconds(90), result.EventQueueLongPollTimeout);
        Assert.Equal(42, Assert.Single(result.Subscriptions).ChannelId);
        Assert.Single(result.Users);
        Assert.Equal(2, result.RecentDirectMessages.Count);
        Assert.Contains(result.RecentDirectMessages, item => item is DirectMessage direct && direct.OtherUserIds.Count == 0);
        Assert.Equal(3, result.Unread.Total);
        Assert.Equal(25, result.MaxFileUploadSizeMiB);
        Assert.Empty(result.Events);
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

        var message = Assert.IsType<MessageUpsertEvent>(Assert.Single(batch.Events));
        Assert.Equal("77", message.LocalId);
        Assert.True(message.Message.IsRead);
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
    public async Task Event_SubscriptionUpdate_IsExplicitlyIgnoredWhenPropertyIsOutsideMvp()
    {
        using var handler = new RecordingHandler(Json("""{"events":[{"id":22,"type":"subscription","op":"update","stream_id":4,"property":"is_muted","value":true}]}"""));
        using var gateway = new ZulipGateway(handler);

        var batch = await gateway.GetEventsAsync(
            new GetEventsRequest(Credentials, "queue-1", 21, TimeSpan.FromSeconds(30)));

        var ignored = Assert.IsType<IgnoredDomainEvent>(Assert.Single(batch.Events));
        Assert.Equal("subscription_property_outside_mvp", ignored.ReasonCode);
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

        var result = await gateway.UploadAttachmentAsync(new UploadAttachmentRequest(
            Credentials,
            new AttachmentUpload("design.png", "image/png", 3, stream)));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/api/v1/user_uploads", request.Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("name=filename", request.Body, StringComparison.Ordinal);
        Assert.Contains("filename=design.png", request.Body, StringComparison.Ordinal);
        Assert.Equal("https://chat.example.test/user_uploads/7/ab/design.png", result.Url);
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

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
