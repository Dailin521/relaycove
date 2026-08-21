using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Core;

namespace RelayCove.Zulip.Client;

/// <summary>Thin, stateless adapter for the Zulip REST and event-queue APIs.</summary>
public sealed class ZulipGateway : IZulipGateway, IDisposable
{
    private const string ApiRoot = "api/v1/";
    private static readonly string[] DefaultEventTypes =
    [
        "message", "subscription", "realm_user", "stream", "update_message",
        "delete_message", "update_message_flags", "reaction", "realm", "heartbeat", "restart"
    ];
    private static readonly string[] InitialFetchEventTypes =
    [
        "subscription", "realm_user", "realm", "realm_user_groups", "recent_private_conversations"
    ];
    private static readonly IReadOnlyDictionary<string, bool> ClientCapabilities =
        new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["notification_settings_null"] = true,
            ["bulk_message_deletion"] = true,
            ["user_avatar_url_field_optional"] = true,
            ["user_list_incomplete"] = true,
            ["empty_topic_name"] = true,
            ["archived_channels"] = true
        };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>Creates a gateway backed by a test handler. Production callers use the redirect-safe constructor.</summary>
    internal ZulipGateway(HttpMessageHandler handler, TimeProvider? timeProvider = null, ILogger? logger = null)
        : this(new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }, false, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(handler);
    }

    /// <summary>Creates a gateway with a redirect-disabled HTTP handler.</summary>
    public ZulipGateway(TimeProvider? timeProvider = null, ILogger? logger = null)
        : this(CreateRedirectDisabledClient(), true, timeProvider, logger)
    {
    }

    /// <summary>Creates a gateway around a test HTTP client. The caller owns the client.</summary>
    internal ZulipGateway(HttpClient httpClient, TimeProvider? timeProvider = null, ILogger? logger = null)
        : this(httpClient, false, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
    }

    private ZulipGateway(HttpClient httpClient, bool ownsClient, TimeProvider? timeProvider, ILogger? logger)
    {
        _httpClient = httpClient;
        _ownsClient = ownsClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<RealmProbeResult> ProbeRealmAsync(RealmEndpoint realm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(realm);
        using var response = await SendAsync(realm, HttpMethod.Get, "server_settings", null, null, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var featureLevel = GetInt64(root, "zulip_feature_level") ?? 0;
        var version = GetString(root, "zulip_version") ?? "unknown";
        var emailEnabled = GetBoolean(root, "email_auth_enabled") ??
            GetBooleanProperty(root, "authentication_methods", "email") ?? false;
        var isIncompatible = GetBoolean(root, "is_incompatible") ?? false;
        return new RealmProbeResult(
            realm,
            version,
            checked((int)Math.Min(featureLevel, int.MaxValue)),
            isIncompatible,
            emailEnabled);
    }

    public async Task<AuthenticationResult> AuthenticateAsync(AuthenticationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = request.Email,
            ["password"] = request.Password
        };

        long userId;
        string apiKey;
        string email;
        HttpResponseMessage authenticationResponse;
        try
        {
            authenticationResponse = await SendAsync(request.Realm, HttpMethod.Post, "fetch_api_key", fields, null, cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayException exception) when (exception.Kind is GatewayErrorKind.ReauthRequired or GatewayErrorKind.RequestFailed)
        {
            throw new GatewayException(
                GatewayErrorKind.AuthenticationFailed,
                GatewayErrorCode.AuthenticationFailed,
                exception.StatusCode);
        }

        using (var response = authenticationResponse)
        using (var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false))
        {
            userId = RequireInt64(document.RootElement, "user_id");
            apiKey = RequireString(document.RootElement, "api_key");
            email = RequireString(document.RootElement, "email");
        }

        var credentials = new CredentialEnvelope(request.Realm, email, userId, apiKey);
        using var userResponse = await SendAsync(credentials.Realm, HttpMethod.Get, "users/me", null, credentials, cancellationToken).ConfigureAwait(false);
        using var userDocument = await ReadDocumentAsync(userResponse, cancellationToken).ConfigureAwait(false);
        var user = ToUserOrNull(userDocument.RootElement);
        if (user is null || user.UserId != userId)
        {
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
        }
        return new AuthenticationResult(credentials, user);
    }

    public async Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var eventTypes = request.EventTypes is { Count: > 0 }
            ? request.EventTypes
            : DefaultEventTypes;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["apply_markdown"] = "false",
            ["client_gravatar"] = "false",
            ["include_subscribers"] = "false",
            ["idle_queue_timeout"] = "3600",
            ["event_types"] = JsonSerializer.Serialize(eventTypes, JsonOptions),
            ["fetch_event_types"] = JsonSerializer.Serialize(InitialFetchEventTypes, JsonOptions),
            ["client_capabilities"] = JsonSerializer.Serialize(ClientCapabilities, JsonOptions)
        };

        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Post, "register", fields, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        // This is the client-side GET /events HTTP timeout. The separate
        // idle_queue_timeout_secs field controls server-side queue collection.
        var queueTimeout = RequirePositiveInt32(root, "event_queue_longpoll_timeout_seconds");
        var maxMessageLength = RequirePositiveInt32(root, "max_message_length");
        var maxTopicLength = RequirePositiveInt32(root, "max_topic_length");
        var maxFileUploadSizeMiB = GetInt32(root, "max_file_upload_size_mib");
        var subscriptions = GetArray(root, "subscriptions").Select(ToSubscription).Where(static item => item is not null).Cast<Subscription>().ToArray();
        var users = GetArray(root, "realm_users").Select(ToUserOrNull).Where(static item => item is not null).Cast<UserProfile>().ToArray();
        return new RegisterResult(
            RequireString(root, "queue_id"),
            RequireInt64(root, "last_event_id"),
            TimeSpan.FromSeconds(queueTimeout),
            maxMessageLength,
            maxTopicLength,
            subscriptions,
            users,
            ToRecentDirectMessages(root),
            ToUnread(root, request.Credentials.UserId),
            [],
            maxFileUploadSizeMiB is > 0 ? maxFileUploadSizeMiB : null,
            PositiveOrNull(GetInt32(root, "max_stream_name_length", "max_channel_name_length")),
            PositiveOrNull(GetInt32(root, "max_stream_description_length", "max_channel_description_length")),
            PositiveOrNull(GetInt32(root, "max_channel_folder_name_length")),
            PositiveOrNull(GetInt32(root, "max_channel_folder_description_length")),
            ToUserTopicVisibilities(root),
            IsOrganizationAdministrator(root, request.Credentials.UserId),
            CanCreatePrivateChannel(root, request.Credentials.UserId));
    }

    public async Task<EventBatch> GetEventsAsync(GetEventsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["queue_id"] = request.QueueId,
            ["last_event_id"] = request.LastEventId.ToString(CultureInfo.InvariantCulture),
            ["dont_block"] = "false"
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(request.Credentials.Realm, HttpMethod.Get, "events", query, request.Credentials, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GatewayException(GatewayErrorKind.Offline, GatewayErrorCode.RequestTimedOut, innerException: exception);
        }
        using (response)
        {
            using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var events = GetArray(root, "events")
                .SelectMany(item => ToEvents(item, DomainEventSource.Realtime, request.Credentials.UserId))
                .ToArray();
            var lastId = events.Select(static item => item.EventId).Where(static id => id.HasValue).Select(static id => id!.Value).DefaultIfEmpty(request.LastEventId).Max();
            return new EventBatch(events, lastId);
        }
    }

    public async Task<HistoryResult> GetHistoryAsync(HistoryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["narrow"] = SerializeNarrow(request.Conversation, request.Credentials.UserId),
            ["anchor"] = request.AnchorMessageId?.ToString(CultureInfo.InvariantCulture) ?? "newest",
            ["include_anchor"] = request.IncludeAnchor ? "true" : "false",
            ["num_before"] = (request.IncludeAnchor ? request.Limit - 1 : request.Limit)
                .ToString(CultureInfo.InvariantCulture),
            ["num_after"] = "0",
            ["apply_markdown"] = "false",
            ["client_gravatar"] = "false",
            ["allow_empty_topic_name"] = "true"
        };
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Get, "messages", query, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        return new HistoryResult(
            GetArray(root, "messages").Select(item => ToMessage(item, request.Credentials.UserId)).Where(static item => item is not null).Cast<ChatMessage>().ToArray(),
            GetBoolean(root, "found_oldest") ?? false,
            GetBoolean(root, "found_newest") ?? false,
            GetBoolean(root, "found_anchor") ?? request.AnchorMessageId is null);
    }

    public async Task<HistoryResult> GetMessagesAroundAsync(
        MessageAroundRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MessageId <= 0) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.BeforeCount is < 0 or > 50 || request.AfterCount is < 0 or > 50)
            throw new ArgumentOutOfRangeException(nameof(request));
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["narrow"] = SerializeNarrow(request.Conversation, request.Credentials.UserId),
            ["anchor"] = request.MessageId.ToString(CultureInfo.InvariantCulture),
            ["include_anchor"] = "true",
            ["num_before"] = request.BeforeCount.ToString(CultureInfo.InvariantCulture),
            ["num_after"] = request.AfterCount.ToString(CultureInfo.InvariantCulture),
            ["apply_markdown"] = "false",
            ["client_gravatar"] = "false",
            ["allow_empty_topic_name"] = "true"
        };
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Get, "messages", query, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        return new HistoryResult(
            GetArray(root, "messages").Select(item => ToMessage(item, request.Credentials.UserId)).Where(static item => item is not null).Cast<ChatMessage>().ToArray(),
            GetBoolean(root, "found_oldest") ?? false,
            GetBoolean(root, "found_newest") ?? false,
            GetBoolean(root, "found_anchor") ?? false);
    }

    private async Task<MessageQueryPage> GetMessagesPageAsync(
        CredentialEnvelope credentials,
        string narrow,
        long? beforeMessageId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["narrow"] = narrow,
            ["anchor"] = beforeMessageId?.ToString(CultureInfo.InvariantCulture) ?? "newest",
            ["include_anchor"] = "false",
            ["num_before"] = limit.ToString(CultureInfo.InvariantCulture),
            ["num_after"] = "0",
            ["apply_markdown"] = "false",
            ["client_gravatar"] = "false",
            ["allow_empty_topic_name"] = "true"
        };
        using var response = await SendAsync(credentials.Realm, HttpMethod.Get, "messages", query, credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        return new MessageQueryPage(
            GetArray(root, "messages").Select(item => ToMessage(item, credentials.UserId)).Where(static item => item is not null).Cast<ChatMessage>().ToArray(),
            GetBoolean(root, "found_oldest") ?? false,
            GetBoolean(root, "found_newest") ?? false,
            GetBoolean(root, "found_anchor") ?? beforeMessageId is null);
    }

    public Task<MessageQueryPage> SearchMessagesAsync(
        MessageSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query)) throw new ArgumentException("A search query is required.", nameof(request));
        return GetMessagesPageAsync(
            request.Credentials,
            SerializeTerms([NarrowTerm("search", request.Query)]),
            request.BeforeMessageId,
            request.Limit,
            cancellationToken);
    }

    public Task<MessageQueryPage> LoadSavedMessagesAsync(
        SavedMessagesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GetMessagesPageAsync(
            request.Credentials,
            SerializeTerms([NarrowTerm("is", "starred")]),
            request.BeforeMessageId,
            request.Limit,
            cancellationToken);
    }

    public async Task<TopicsResult> GetTopicsAsync(TopicsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ChannelId <= 0) throw new ArgumentOutOfRangeException(nameof(request));
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["allow_empty_topic_name"] = "true"
        };
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Get, $"users/me/{request.ChannelId.ToString(CultureInfo.InvariantCulture)}/topics", query, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        var topics = GetArray(document.RootElement, "topics")
            .Select(item => new TopicSummary(request.ChannelId, RequireString(item, "name"), GetInt64(item, "max_id")))
            .ToArray();
        return new TopicsResult(topics);
    }

    public async Task SetTopicVisibilityPolicyAsync(SetTopicVisibilityPolicyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stream_id"] = request.Topic.ChannelId.ToString(CultureInfo.InvariantCulture),
            ["topic"] = request.Topic.Topic,
            ["visibility_policy"] = ((int)request.Policy).ToString(CultureInfo.InvariantCulture)
        };
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Post, "user_topics", fields, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureNoUnsupportedParameters(document.RootElement);
    }

    public async Task<TopicReadResult> MarkTopicReadAsync(MarkTopicReadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["op"] = "add",
            ["flag"] = "read",
            ["narrow"] = SerializeUnreadNarrow(request.Topic, request.Credentials.UserId),
            ["anchor"] = request.AnchorMessageId?.ToString(CultureInfo.InvariantCulture) ?? "oldest",
            ["include_anchor"] = "false",
            ["num_before"] = "0",
            ["num_after"] = "1000"
        };
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Post, "messages/flags/narrow", fields, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        return new TopicReadResult(GetInt64(document.RootElement, "last_processed_id"), GetBoolean(document.RootElement, "found_newest") ?? false);
    }

    public async Task<TopicAnchorResult> ResolveTopicAnchorAsync(ResolveTopicAnchorRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["narrow"] = SerializeNarrow(request.Topic, request.Credentials.UserId),
            ["anchor"] = "oldest",
            ["include_anchor"] = "false",
            ["num_before"] = "0",
            ["num_after"] = "1"
        };
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Get, "messages", query, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        return new TopicAnchorResult(GetArray(document.RootElement, "messages").Select(item => GetInt64(item, "id")).FirstOrDefault(id => id is > 0));
    }

    public async Task MoveTopicAsync(MoveTopicRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["topic"] = request.Destination.Topic,
            ["propagate_mode"] = "change_all",
            ["send_notification_to_old_thread"] = "false",
            ["send_notification_to_new_thread"] = "true"
        };
        if (request.Source.ChannelId != request.Destination.ChannelId)
            fields["stream_id"] = request.Destination.ChannelId.ToString(CultureInfo.InvariantCulture);
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Patch, $"messages/{request.AnchorMessageId.ToString(CultureInfo.InvariantCulture)}", fields, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureNoUnsupportedParameters(document.RootElement);
    }

    public async Task<TopicDeleteResult> DeleteTopicAsync(DeleteTopicRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["topic_name"] = request.Topic.Topic };
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Post, $"streams/{request.Topic.ChannelId.ToString(CultureInfo.InvariantCulture)}/delete_topic", fields, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureNoUnsupportedParameters(document.RootElement);
        return new TopicDeleteResult(GetBoolean(document.RootElement, "complete") ?? false);
    }

    public async Task<SendResult> SendAsync(SendRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["content"] = request.Content,
            ["queue_id"] = request.QueueId,
            ["local_id"] = request.LocalId
        };
        AddSendConversation(fields, request.Conversation, request.Credentials.UserId);
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Post, "messages", fields, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        return new SendResult(request.LocalId, RequireInt64(document.RootElement, "id"));
    }

    public async Task SetReactionAsync(SetReactionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MessageId <= 0) throw new ArgumentOutOfRangeException(nameof(request));
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["emoji_name"] = request.Reaction.EmojiName,
            ["emoji_code"] = request.Reaction.EmojiCode,
            ["reaction_type"] = request.Reaction.ReactionType
        };
        try
        {
            using var response = await SendAsync(
                request.Credentials.Realm,
                request.Add ? HttpMethod.Post : HttpMethod.Delete,
                $"messages/{request.MessageId.ToString(CultureInfo.InvariantCulture)}/reactions",
                fields,
                request.Credentials,
                cancellationToken).ConfigureAwait(false);
            using var ignored = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayException exception) when (
            request.Add && exception.Code == GatewayErrorCode.ReactionAlreadyExists ||
            !request.Add && exception.Code == GatewayErrorCode.ReactionDoesNotExist)
        {
            // The requested target state is already authoritative.
        }
    }

    public async Task EditMessageAsync(EditMessageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["content"] = request.Content,
            ["prev_content_sha256"] = request.PreviousContentSha256
        };
        using var response = await SendAsync(
            request.Credentials.Realm,
            HttpMethod.Patch,
            $"messages/{request.MessageId.ToString(CultureInfo.InvariantCulture)}",
            fields,
            request.Credentials,
            cancellationToken).ConfigureAwait(false);
        using var ignored = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteMessageAsync(DeleteMessageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await SendAsync(
            request.Credentials.Realm,
            HttpMethod.Delete,
            $"messages/{request.MessageId.ToString(CultureInfo.InvariantCulture)}",
            null,
            request.Credentials,
            cancellationToken).ConfigureAwait(false);
        using var ignored = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetMessageStarredAsync(SetMessageStarredRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MessageId <= 0) throw new ArgumentOutOfRangeException(nameof(request));
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["messages"] = JsonSerializer.Serialize(new[] { request.MessageId }, JsonOptions),
            ["op"] = request.IsStarred ? "add" : "remove",
            ["flag"] = "starred"
        };
        using var response = await SendAsync(
            request.Credentials.Realm,
            HttpMethod.Post,
            "messages/flags",
            fields,
            request.Credentials,
            cancellationToken).ConfigureAwait(false);
        using var ignored = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UploadedAttachment> UploadAttachmentAsync(
        UploadAttachmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fileName = SanitizeUploadFileName(request.Upload.FileName);
        using var multipart = new MultipartFormDataContent();
        var streamContent = new StreamContent(request.Upload.Content);
        if (request.Upload.ContentType is { } contentType &&
            MediaTypeHeaderValue.TryParse(contentType, out var parsedContentType))
        {
            streamContent.Headers.ContentType = parsedContentType;
        }
        streamContent.Headers.ContentLength = request.Upload.Length;
        multipart.Add(streamContent, "filename", fileName);
        using var response = await SendMultipartAsync(
            request.Credentials.Realm,
            "user_uploads",
            multipart,
            request.Credentials,
            cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        var rawUrl = GetString(document.RootElement, "url", "uri");
        if (!TryResolveRealmUploadUrl(request.Credentials.Realm, rawUrl, out var url))
        {
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
        }
        var returnedName = SanitizeUploadFileName(GetString(document.RootElement, "filename") ?? fileName);
        return new UploadedAttachment(returnedName, url.AbsoluteUri);
    }

    public async Task<RealmMediaResult> GetRealmMediaAsync(
        GetRealmMediaRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hardLimit = request.Media.Kind == RealmMediaKind.File
            ? 100L * 1024 * 1024
            : 25L * 1024 * 1024;
        var maximumBytes = Math.Min(request.Media.MaximumBytes, hardLimit);
        if (maximumBytes <= 0 ||
            !TryResolveRealmMediaUrl(
                request.Credentials.Realm,
                request.Media.SourceUrl,
                request.Media.Kind,
                temporary: false,
                out var approved))
        {
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
        }

        var fetchUrl = approved;
        CredentialEnvelope? fetchCredentials = request.Media.Kind == RealmMediaKind.Avatar
            ? request.Credentials
            : null;
        if (request.Media.Kind is RealmMediaKind.Image or RealmMediaKind.File)
        {
            var relativePath = approved.AbsolutePath.TrimStart('/');
            using var temporaryResponse = await SendAsync(
                request.Credentials.Realm,
                HttpMethod.Get,
                relativePath,
                null,
                request.Credentials,
                cancellationToken).ConfigureAwait(false);
            using var temporaryDocument = await ReadDocumentAsync(temporaryResponse, cancellationToken).ConfigureAwait(false);
            if (!TryResolveRealmMediaUrl(
                    request.Credentials.Realm,
                    GetString(temporaryDocument.RootElement, "url"),
                    request.Media.Kind,
                    temporary: true,
                    out fetchUrl))
            {
                throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
            }
        }

        using var response = await SendAbsoluteMediaAsync(
            fetchUrl,
            fetchCredentials,
            request.Media.Kind,
            cancellationToken).ConfigureAwait(false);
        var contentType = response.Content.Headers.ContentType?.MediaType?.Trim().ToLowerInvariant();
        if (request.Media.Kind != RealmMediaKind.File && !IsPreviewImageContentType(contentType))
        {
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse, (int)response.StatusCode);
        }
        var bytes = await ReadBytesWithLimitAsync(response.Content, maximumBytes, cancellationToken).ConfigureAwait(false);
        return new RealmMediaResult(bytes, contentType ?? "application/octet-stream");
    }

    public async Task<UnsubscribeChannelResult> UnsubscribeChannelAsync(
        UnsubscribeChannelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ChannelName);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["subscriptions"] = JsonSerializer.Serialize(new[] { request.ChannelName }, JsonOptions)
        };
        using var response = await SendAsync(
            request.Credentials.Realm,
            HttpMethod.Delete,
            "users/me/subscriptions",
            fields,
            request.Credentials,
            cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        return new UnsubscribeChannelResult(
            GetStringArray(document.RootElement, "removed"),
            GetStringArray(document.RootElement, "not_removed"));
    }

    public async Task<IReadOnlyList<ChannelSummary>> GetAvailableChannelsAsync(AvailableChannelsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Get, "streams", null, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        return GetArray(document.RootElement, "streams").Select(ToChannelSummary).Where(static item => item is not null).Cast<ChannelSummary>().ToArray();
    }

    public async Task<SubscribeChannelResult> SubscribeToChannelAsync(SubscribeChannelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["subscriptions"] = JsonSerializer.Serialize(new[] { new { name = request.Channel.Name } }, JsonOptions)
        };
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Post, "users/me/subscriptions", fields, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("subscribed", out _) && !root.TryGetProperty("already_subscribed", out _))
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
        return new SubscribeChannelResult(
            GetSubscriptionResponseNames(root, "subscribed", request.Credentials),
            GetSubscriptionResponseNames(root, "already_subscribed", request.Credentials),
            GetSubscriptionResponseNames(root, "unauthorized", request.Credentials));
    }

    public async Task SetSubscriptionPreferenceAsync(SetSubscriptionPreferenceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var property = request.Preference == SubscriptionPreference.Muted ? "is_muted" : "pin_to_top";
        var fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["property"] = property, ["value"] = request.Value ? "true" : "false" };
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Patch, $"users/me/subscriptions/{request.ChannelId.ToString(CultureInfo.InvariantCulture)}", fields, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var ignored = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChannelSettingsSnapshot> GetChannelSettingsSnapshotAsync(ChannelSettingsSnapshotRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var streamsQuery = new Dictionary<string, string>(StringComparer.Ordinal) { ["include_all"] = "true", ["exclude_archived"] = "false" };
        var groupsQuery = new Dictionary<string, string>(StringComparer.Ordinal) { ["include_deactivated_groups"] = "true" };
        var foldersQuery = new Dictionary<string, string>(StringComparer.Ordinal) { ["include_archived"] = "true" };
        using var meResponse = await SendAsync(request.Credentials.Realm, HttpMethod.Get, "users/me", null, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var meDocument = await ReadDocumentAsync(meResponse, cancellationToken).ConfigureAwait(false);
        var me = meDocument.RootElement;
        var currentUserId = GetInt64(me, "user_id", "id");
        var role = GetInt64(me, "role");
        if (currentUserId is not > 0 || currentUserId != request.Credentials.UserId || role is not (100 or 200 or 300 or 400 or 600))
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
        var isAdmin = role is 100 or 200;
        var isGuest = role == 600;
        if (GetBoolean(me, "is_admin") is { } declaredAdmin && declaredAdmin != isAdmin ||
            GetBoolean(me, "is_guest") is { } declaredGuest && declaredGuest != isGuest)
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
        using var streamsResponse = await SendAsync(request.Credentials.Realm, HttpMethod.Get, "streams", streamsQuery, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var streamsDocument = await ReadDocumentAsync(streamsResponse, cancellationToken).ConfigureAwait(false);
        var subscriptionsQuery = new Dictionary<string, string>(StringComparer.Ordinal) { ["include_subscribers"] = "false" };
        using var subscriptionsResponse = await SendAsync(request.Credentials.Realm, HttpMethod.Get, "users/me/subscriptions", subscriptionsQuery, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var subscriptionsDocument = await ReadDocumentAsync(subscriptionsResponse, cancellationToken).ConfigureAwait(false);
        var subscribedIds = GetRequiredSubscriptionChannelIds(subscriptionsDocument.RootElement);
        var streamsRoot = streamsDocument.RootElement;
        EnsureNotErrorResponse(streamsRoot);
        if (streamsRoot.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(streamsRoot, "streams", out var streams) ||
            streams.ValueKind != JsonValueKind.Array)
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
        var channels = streams.EnumerateArray()
            .Select(ToChannelSummary)
            .Where(static item => item is not null)
            .Cast<ChannelSummary>()
            .ToArray();
        var channelsById = new Dictionary<long, ChannelSummary>();
        foreach (var channel in channels)
        {
            if (!channelsById.TryAdd(channel.ChannelId, channel))
                throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
        }
        if (subscribedIds.Any(id => !channelsById.ContainsKey(id)))
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
        using var foldersResponse = await SendAsync(request.Credentials.Realm, HttpMethod.Get, "channel_folders", foldersQuery, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var foldersDocument = await ReadDocumentAsync(foldersResponse, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ChannelUserGroup> groups = [];
        if (!isGuest)
        {
            using var groupsResponse = await SendAsync(request.Credentials.Realm, HttpMethod.Get, "user_groups", groupsQuery, request.Credentials, cancellationToken).ConfigureAwait(false);
            using var groupsDocument = await ReadDocumentAsync(groupsResponse, cancellationToken).ConfigureAwait(false);
            groups = GetArray(groupsDocument.RootElement, "user_groups")
                .Select(ToChannelUserGroup)
                .Where(static item => item is not null)
                .Cast<ChannelUserGroup>()
                .ToArray();
        }
        return new ChannelSettingsSnapshot(
            channels.Select(channel => channel with { IsSubscribed = subscribedIds.Contains(channel.ChannelId) }).ToArray(),
            GetArray(foldersDocument.RootElement, "channel_folders").Select(ToChannelFolder).Where(static item => item is not null).Cast<ChannelFolder>().OrderBy(static item => item.Order).ToArray(),
            groups,
            currentUserId.Value,
            isAdmin,
            isGuest,
            request.Limits);
    }

    public async Task<ChannelDetails> GetChannelDetailsAsync(ChannelDetailsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ChannelId <= 0) throw new ArgumentOutOfRangeException(nameof(request));
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Get, $"streams/{request.ChannelId.ToString(CultureInfo.InvariantCulture)}", null, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        return ToChannelDetails(document.RootElement) ?? ToChannelDetails(GetObject(document.RootElement, "stream")) ?? throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
    }

    public async Task UpdateChannelAsync(UpdateChannelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ChannelId <= 0) throw new ArgumentOutOfRangeException(nameof(request));
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(request.Name)) fields["new_name"] = request.Name.Trim();
        if (request.Description is not null) fields["description"] = request.Description;
        if (request.ClearFolder) fields["folder_id"] = "null";
        else if (request.FolderId is > 0) fields["folder_id"] = request.FolderId.Value.ToString(CultureInfo.InvariantCulture);
        if (fields.Count == 0) return;
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Patch, $"streams/{request.ChannelId.ToString(CultureInfo.InvariantCulture)}", fields, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        if (GetArray(document.RootElement, "ignored_parameters_unsupported").Length > 0)
        {
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
        }
    }

    public async Task<ChannelFolder> CreateChannelFolderAsync(CreateChannelFolderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = request.Name.Trim(),
            ["description"] = request.Description ?? string.Empty
        };
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Post, "channel_folders/create", fields, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        return ToChannelFolder(document.RootElement) ??
            ToChannelFolder(GetObject(document.RootElement, "channel_folder")) ??
            (GetInt64(document.RootElement, "channel_folder_id") is { } folderId && folderId > 0
                ? new ChannelFolder(folderId, request.Name.Trim(), request.Description)
                : throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse));
    }

    public async Task<string> GetChannelEmailAddressAsync(ChannelEmailAddressRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ChannelId <= 0) throw new ArgumentOutOfRangeException(nameof(request));
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Get, $"streams/{request.ChannelId.ToString(CultureInfo.InvariantCulture)}/email_address", null, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        return RequireString(document.RootElement, "email_address");
    }

    public async Task ArchiveChannelAsync(ArchiveChannelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ChannelId <= 0) throw new ArgumentOutOfRangeException(nameof(request));
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Delete, $"streams/{request.ChannelId.ToString(CultureInfo.InvariantCulture)}", null, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var ignored = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> CreateChannelAsync(CreateChannelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Options.Name);
        var options = request.Options;
        if (options.IsPrivate && options.IsWebPublic) throw new ArgumentException("Private channels cannot be web-public.", nameof(request));
        if (!options.IsPrivate && !options.HistoryPublicToSubscribers) throw new ArgumentException("Public channels must expose history to subscribers.", nameof(request));
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = options.Name.Trim(),
            ["description"] = options.Description ?? string.Empty,
            ["subscribers"] = JsonSerializer.Serialize(new[] { request.Credentials.UserId }, JsonOptions),
            ["invite_only"] = options.IsPrivate ? "true" : "false",
            ["is_web_public"] = options.IsWebPublic ? "true" : "false",
            ["history_public_to_subscribers"] = options.HistoryPublicToSubscribers ? "true" : "false",
            ["is_default_stream"] = options.IsDefaultStream ? "true" : "false"
        };
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Post, "channels/create", fields, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(GetString(document.RootElement, "result"), "success", StringComparison.OrdinalIgnoreCase))
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
        EnsureNoUnsupportedParameters(document.RootElement);
        return GetInt64(document.RootElement, "id") is { } channelId && channelId > 0
            ? channelId
            : throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
    }

    public async Task<long> CreatePrivateGroupAsync(PrivateGroupCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Options);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Options.Name);
        var otherMemberIds = request.Options.OtherMemberIds
            .Where(id => id > 0 && id != request.Credentials.UserId)
            .Distinct()
            .OrderBy(static id => id)
            .ToArray();
        if (otherMemberIds.Length < 2 || otherMemberIds.Length != request.Options.OtherMemberIds.Count)
            throw new ArgumentException("A private group requires at least two distinct other members.", nameof(request));

        var subscribers = new[] { request.Credentials.UserId }.Concat(otherMemberIds).ToArray();
        var ownerGroup = PrivateGroupPolicy.OwnerGroup(request.Credentials.UserId);
        var serializedOwnerGroup = JsonSerializer.Serialize(ToGroupValue(ownerGroup), JsonOptions);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = request.Options.Name.Trim(),
            ["description"] = string.Empty,
            ["subscribers"] = JsonSerializer.Serialize(subscribers, JsonOptions),
            ["announce"] = "false",
            ["invite_only"] = "true",
            ["is_web_public"] = "false",
            ["history_public_to_subscribers"] = "true",
            ["is_default_stream"] = "false",
            ["topics_policy"] = JsonSerializer.Serialize("empty_topic_only", JsonOptions),
            ["can_administer_channel_group"] = serializedOwnerGroup,
            ["can_add_subscribers_group"] = serializedOwnerGroup,
            ["can_remove_subscribers_group"] = serializedOwnerGroup
        };
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Post, "channels/create", fields, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(GetString(document.RootElement, "result"), "success", StringComparison.OrdinalIgnoreCase))
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
        EnsureNoUnsupportedParameters(document.RootElement);
        return GetInt64(document.RootElement, "id") is { } channelId && channelId > 0
            ? channelId
            : throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
    }

    public async Task<ChannelPersonalSettings> GetChannelPersonalSettingsAsync(ChannelMembersRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = new Dictionary<string, string>(StringComparer.Ordinal) { ["include_subscribers"] = "false" };
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Get, "users/me/subscriptions", query, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        var subscription = GetArray(document.RootElement, "subscriptions").FirstOrDefault(item => GetInt64(item, "stream_id", "channel_id") == request.ChannelId);
        if (subscription.ValueKind != JsonValueKind.Object) throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
        return new ChannelPersonalSettings(
            request.ChannelId,
            GetString(subscription, "color"),
            GetBoolean(subscription, "is_muted") ?? false,
            GetBoolean(subscription, "pin_to_top") ?? false,
            GetBoolean(subscription, "desktop_notifications"),
            GetBoolean(subscription, "audible_notifications"),
            GetBoolean(subscription, "push_notifications"),
            GetBoolean(subscription, "email_notifications"),
            GetBoolean(subscription, "wildcard_mentions_notify"));
    }

    public async Task SetChannelPersonalSettingAsync(SetChannelPersonalSettingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (property, value) = ToPersonalSetting(request.Change);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["property"] = property, ["value"] = value };
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Patch, $"users/me/subscriptions/{request.ChannelId.ToString(CultureInfo.InvariantCulture)}", fields, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureNoUnsupportedParameters(document.RootElement);
    }

    public async Task<IReadOnlyList<long>> GetChannelMemberIdsAsync(ChannelMembersRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Get, $"streams/{request.ChannelId.ToString(CultureInfo.InvariantCulture)}/members", null, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureNotErrorResponse(document.RootElement);
        if (!TryGetProperty(document.RootElement, "subscribers", out var subscribers) || subscribers.ValueKind != JsonValueKind.Array)
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
        var ids = new List<long>();
        foreach (var item in subscribers.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var id) || id <= 0)
                throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
            ids.Add(id);
        }
        return ids.Distinct().ToArray();
    }

    public async Task<IReadOnlyList<UserProfile>> GetRealmUsersAsync(RealmUsersRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Get, "users", null, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureNotErrorResponse(document.RootElement);
        EnsureNoUnsupportedParameters(document.RootElement);
        if (!TryGetProperty(document.RootElement, "members", out var members) || members.ValueKind != JsonValueKind.Array)
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);

        var users = new List<UserProfile>();
        var ids = new HashSet<long>();
        foreach (var item in members.EnumerateArray())
        {
            var user = ToRequiredRealmUser(item);
            if (!ids.Add(user.UserId)) throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
            users.Add(user);
        }
        return users;
    }

    public async Task ModifyChannelMembersAsync(ModifyChannelMembersRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ChannelName);
        if (request.PrincipalIds.Count == 0 || request.PrincipalIds.Any(static id => id <= 0)) throw new ArgumentException("At least one valid principal is required.", nameof(request));
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["subscriptions"] = request.Add
                ? JsonSerializer.Serialize(new[] { new { name = request.ChannelName } }, JsonOptions)
                : JsonSerializer.Serialize(new[] { request.ChannelName }, JsonOptions),
            ["principals"] = JsonSerializer.Serialize(request.PrincipalIds, JsonOptions)
        };
        if (request.Add)
        {
            fields["authorization_errors_fatal"] = "true";
            fields["send_new_subscription_messages"] = request.SendNewSubscriptionMessages ? "true" : "false";
        }
        using var response = await SendAsync(request.Credentials.Realm, request.Add ? HttpMethod.Post : HttpMethod.Delete, "users/me/subscriptions", fields, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureNoUnsupportedParameters(document.RootElement);
        if (HasMemberOperationFailure(document.RootElement)) throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
    }

    public async Task UpdateChannelAdvancedSettingsAsync(UpdateChannelAdvancedRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fields = ToAdvancedFields(request.Change);
        if (fields.Count == 0) return;
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Patch, $"streams/{request.ChannelId.ToString(CultureInfo.InvariantCulture)}", fields, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureNoUnsupportedParameters(document.RootElement);
    }

    public async Task MarkReadAsync(MarkReadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["op"] = "add",
            ["flag"] = "read",
            ["narrow"] = SerializeUnreadNarrow(request.Conversation, request.Credentials.UserId),
            ["num_before"] = (request.Limit - 1).ToString(CultureInfo.InvariantCulture),
            ["num_after"] = "0"
        };
        fields["anchor"] = request.AnchorMessageId?.ToString(CultureInfo.InvariantCulture) ?? "newest";
        using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Post, "messages/flags/narrow", fields, request.Credentials, cancellationToken).ConfigureAwait(false);
        using var ignored = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteQueueAsync(DeleteQueueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["queue_id"] = request.QueueId };
        try
        {
            using var response = await SendAsync(request.Credentials.Realm, HttpMethod.Delete, "events", fields, request.Credentials, cancellationToken).ConfigureAwait(false);
            using var ignored = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayException exception) when (exception.Kind == GatewayErrorKind.QueueExpired)
        {
            // An already-expired queue is equivalent to successful cleanup.
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }

    /// <summary>Builds the default handler with redirects explicitly disabled.</summary>
    public static HttpClientHandler CreateDefaultHandler() => new() { AllowAutoRedirect = false };

    private static HttpClient CreateRedirectDisabledClient() => new(CreateDefaultHandler()) { Timeout = Timeout.InfiniteTimeSpan };

    private async Task<HttpResponseMessage> SendAsync(RealmEndpoint realm, HttpMethod method, string relativePath, IReadOnlyDictionary<string, string>? parameters, CredentialEnvelope? credentials, CancellationToken cancellationToken)
    {
        var operation = GetSafeOperationName(relativePath);
        var started = _timeProvider.GetTimestamp();
        var uri = BuildUri(realm, relativePath, method == HttpMethod.Get ? parameters : null);
        using var request = new HttpRequestMessage(method, uri);
        if (credentials is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.Email}:{credentials.ApiKey}")));
        }
        if (parameters is not null && method != HttpMethod.Get) request.Content = new FormUrlEncodedContent(parameters);

        try
        {
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Zulip operation {Operation} completed with status {StatusCode} in {ElapsedMilliseconds} ms.",
                operation,
                (int)response.StatusCode,
                _timeProvider.GetElapsedTime(started).TotalMilliseconds);
            if (response.IsSuccessStatusCode) return response;
            var error = await ToGatewayExceptionAsync(response, cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw error;
        }
        catch (GatewayException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException exception)
        {
            _logger.LogWarning(
                "Zulip operation {Operation} failed with {ErrorCode} in {ElapsedMilliseconds} ms.",
                operation,
                GatewayErrorCode.RequestTimedOut,
                _timeProvider.GetElapsedTime(started).TotalMilliseconds);
            throw new GatewayException(GatewayErrorKind.Offline, GatewayErrorCode.RequestTimedOut, innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                "Zulip operation {Operation} failed with {ErrorCode} in {ElapsedMilliseconds} ms.",
                operation,
                GatewayErrorCode.NetworkError,
                _timeProvider.GetElapsedTime(started).TotalMilliseconds);
            throw new GatewayException(GatewayErrorKind.Offline, GatewayErrorCode.NetworkError, innerException: exception);
        }
    }

    private async Task<HttpResponseMessage> SendMultipartAsync(
        RealmEndpoint realm,
        string relativePath,
        MultipartFormDataContent content,
        CredentialEnvelope credentials,
        CancellationToken cancellationToken)
    {
        var operation = GetSafeOperationName(relativePath);
        var started = _timeProvider.GetTimestamp();
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(realm, relativePath, null))
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.Email}:{credentials.ApiKey}")));
        try
        {
            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Zulip operation {Operation} completed with status {StatusCode} in {ElapsedMilliseconds} ms.",
                operation,
                (int)response.StatusCode,
                _timeProvider.GetElapsedTime(started).TotalMilliseconds);
            if (response.IsSuccessStatusCode) return response;
            var error = await ToGatewayExceptionAsync(response, cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw error;
        }
        catch (GatewayException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new GatewayException(
                GatewayErrorKind.Offline,
                GatewayErrorCode.NetworkError,
                innerException: exception);
        }
    }

    private async Task<HttpResponseMessage> SendAbsoluteMediaAsync(
        Uri uri,
        CredentialEnvelope? credentials,
        RealmMediaKind kind,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            kind == RealmMediaKind.File ? "application/octet-stream" : "image/avif"));
        if (kind != RealmMediaKind.File)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/gif"));
        }
        if (credentials is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.Email}:{credentials.ApiKey}")));
        }
        try
        {
            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;
            var error = await ToGatewayExceptionAsync(response, cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw error;
        }
        catch (GatewayException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new GatewayException(
                GatewayErrorKind.Offline,
                GatewayErrorCode.NetworkError,
                innerException: exception);
        }
    }

    private static async Task<byte[]> ReadBytesWithLimitAsync(
        HttpContent content,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } declared && declared > maximumBytes)
        {
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
        }
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream((int)Math.Min(maximumBytes, 1024 * 1024));
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > maximumBytes)
            {
                throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return destination.ToArray();
    }

    private async Task<GatewayException> ToGatewayExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            return new GatewayException(
                GatewayErrorKind.IncompatibleRealm,
                GatewayErrorCode.RedirectNotAllowed,
                (int)response.StatusCode);
        }

        string? code = null;
        TimeSpan? retryAfter = GetRetryAfter(response.Headers.RetryAfter);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            code = GetString(document.RootElement, "code");
            if (GetDecimal(document.RootElement, "retry-after") is { } seconds && seconds >= 0)
            {
                retryAfter = TimeSpan.FromSeconds((double)seconds);
            }
        }
        catch (JsonException) { }

        if (string.Equals(code, "BAD_EVENT_QUEUE_ID", StringComparison.OrdinalIgnoreCase))
            return new GatewayException(GatewayErrorKind.QueueExpired, GatewayErrorCode.BadEventQueueId, (int)response.StatusCode);
        if (string.Equals(code, "REACTION_ALREADY_EXISTS", StringComparison.OrdinalIgnoreCase))
            return new GatewayException(GatewayErrorKind.RequestFailed, GatewayErrorCode.ReactionAlreadyExists, (int)response.StatusCode);
        if (string.Equals(code, "REACTION_DOES_NOT_EXIST", StringComparison.OrdinalIgnoreCase))
            return new GatewayException(GatewayErrorKind.RequestFailed, GatewayErrorCode.ReactionDoesNotExist, (int)response.StatusCode);
        if (string.Equals(code, "EXPECTATION_MISMATCH", StringComparison.OrdinalIgnoreCase))
            return new GatewayException(GatewayErrorKind.RequestFailed, GatewayErrorCode.ExpectationMismatch, (int)response.StatusCode);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return new GatewayException(GatewayErrorKind.ReauthRequired, GatewayErrorCode.Unauthorized, (int)response.StatusCode);
        if ((int)response.StatusCode == 429)
            return new GatewayException(GatewayErrorKind.RateLimited, GatewayErrorCode.RateLimited, 429, retryAfter);
        return response.StatusCode >= HttpStatusCode.InternalServerError
            ? new GatewayException(GatewayErrorKind.Server, GatewayErrorCode.ServerError, (int)response.StatusCode)
            : new GatewayException(GatewayErrorKind.RequestFailed, GatewayErrorCode.RequestFailed, (int)response.StatusCode);
    }

    private TimeSpan? GetRetryAfter(RetryConditionHeaderValue? retryAfter) => retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - _timeProvider.GetUtcNow() : null);

    private static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse, (int)response.StatusCode, innerException: exception);
        }
    }

    private static Uri BuildUri(RealmEndpoint realm, string relativePath, IReadOnlyDictionary<string, string>? query)
    {
        var builder = new UriBuilder(new Uri(realm.Uri, ApiRoot + relativePath));
        if (query is { Count: > 0 }) builder.Query = string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return builder.Uri;
    }

    private static string GetSafeOperationName(string relativePath) => relativePath switch
    {
        "server_settings" => "probe_realm",
        "fetch_api_key" => "authenticate",
        "users/me" => "get_current_user",
        "register" => "register_queue",
        "events" => "event_queue",
        "messages" => "messages",
        "messages/flags/narrow" => "mark_read",
        "messages/flags" => "set_message_flag",
        "user_uploads" => "upload_attachment",
        "streams" => "channels",
        "user_groups" => "channel_permission_groups",
        "channel_folders" => "channel_folders",
        "channel_folders/create" => "create_channel_folder",
        _ when relativePath.StartsWith("user_uploads/", StringComparison.Ordinal) => "resolve_realm_media",
        _ when relativePath.StartsWith("messages/", StringComparison.Ordinal) &&
               relativePath.EndsWith("/reactions", StringComparison.Ordinal) => "set_reaction",
        _ when relativePath.StartsWith("messages/", StringComparison.Ordinal) => "mutate_message",
        _ when relativePath.StartsWith("streams/", StringComparison.Ordinal) &&
               relativePath.EndsWith("/email_address", StringComparison.Ordinal) => "get_channel_email_address",
        _ when relativePath.StartsWith("streams/", StringComparison.Ordinal) => "channel_settings",
        _ when relativePath.StartsWith("users/me/", StringComparison.Ordinal) &&
               relativePath.EndsWith("/topics", StringComparison.Ordinal) => "topics",
        _ => "unknown"
    };

    private static string SanitizeUploadFileName(string fileName)
    {
        var leaf = Path.GetFileName(fileName)
            .Select(character => character < 0x20 || character == 0x7f ? '_' : character)
            .ToArray();
        var sanitized = new string(leaf).Trim();
        if (sanitized.Length > 256) sanitized = sanitized[..256];
        return sanitized.Length == 0 ? "file" : sanitized;
    }

    private static bool TryResolveRealmUploadUrl(RealmEndpoint realm, string? rawUrl, out Uri url)
    {
        url = null!;
        if (string.IsNullOrWhiteSpace(rawUrl) || !Uri.TryCreate(realm.Uri, rawUrl, out var resolved)) return false;
        if (!string.Equals(resolved.Scheme, realm.Uri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(resolved.Host, realm.Uri.Host, StringComparison.OrdinalIgnoreCase) ||
            resolved.Port != realm.Uri.Port ||
            !resolved.AbsolutePath.StartsWith("/user_uploads/", StringComparison.Ordinal) ||
            resolved.AbsolutePath.StartsWith("/user_uploads/temporary/", StringComparison.Ordinal))
        {
            return false;
        }
        url = resolved;
        return true;
    }

    private static bool TryResolveRealmMediaUrl(
        RealmEndpoint realm,
        string? rawUrl,
        RealmMediaKind kind,
        bool temporary,
        out Uri url)
    {
        url = null!;
        if (string.IsNullOrWhiteSpace(rawUrl) || rawUrl.Length > 4096 || rawUrl.StartsWith("//", StringComparison.Ordinal) ||
            !Uri.TryCreate(realm.Uri, rawUrl, out var resolved) ||
            !string.Equals(resolved.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(resolved.Scheme, realm.Uri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(resolved.Host, realm.Uri.Host, StringComparison.OrdinalIgnoreCase) ||
            resolved.Port != realm.Uri.Port ||
            !string.IsNullOrEmpty(resolved.UserInfo) ||
            !string.IsNullOrEmpty(resolved.Fragment))
        {
            return false;
        }
        string decodedPath;
        try
        {
            decodedPath = Uri.UnescapeDataString(resolved.AbsolutePath);
        }
        catch (UriFormatException)
        {
            return false;
        }
        if (decodedPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal)) return false;
        var allowed = temporary
            ? decodedPath.StartsWith("/user_uploads/temporary/", StringComparison.Ordinal)
            : kind switch
            {
                RealmMediaKind.Avatar => decodedPath.StartsWith("/avatar/", StringComparison.Ordinal) ||
                    decodedPath.StartsWith("/user_avatars/", StringComparison.Ordinal) ||
                    decodedPath.StartsWith("/static/generated/avatars/", StringComparison.Ordinal),
                _ => decodedPath.StartsWith("/user_uploads/", StringComparison.Ordinal) &&
                    !decodedPath.StartsWith("/user_uploads/temporary/", StringComparison.Ordinal)
            };
        if (!allowed) return false;
        url = resolved;
        return true;
    }

    private static bool IsPreviewImageContentType(string? contentType) => contentType is
        "image/avif" or "image/gif" or "image/jpeg" or "image/png" or "image/webp";

    private static void AddSendConversation(
        IDictionary<string, string> fields,
        ConversationKey conversation,
        long currentUserId)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        switch (conversation)
        {
            case ChannelTopic channel:
                fields["type"] = "channel";
                fields["to"] = channel.ChannelId.ToString(CultureInfo.InvariantCulture);
                fields["topic"] = channel.Topic;
                break;
            case DirectMessage direct:
                fields["type"] = "direct";
                var recipients = direct.OtherUserIds.Count == 0
                    ? new[] { currentUserId }
                    : direct.OtherUserIds;
                fields["to"] = JsonSerializer.Serialize(recipients, JsonOptions);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(conversation), "Unsupported conversation type.");
        }
    }

    private static string SerializeNarrow(ConversationKey conversation, long currentUserId)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        IReadOnlyList<IReadOnlyDictionary<string, object>> narrow = conversation switch
        {
            ChannelTopic channel =>
            [
                NarrowTerm("channel", channel.ChannelId),
                NarrowTerm("topic", channel.Topic)
            ],
            DirectMessage direct when direct.OtherUserIds.Count == 0 =>
            [
                NarrowTerm("dm", new[] { currentUserId })
            ],
            DirectMessage direct =>
            [
                NarrowTerm("dm", direct.OtherUserIds)
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(conversation), "Unsupported conversation type.")
        };
        return SerializeTerms(narrow);
    }

    private static string SerializeTerms(IReadOnlyList<IReadOnlyDictionary<string, object>> terms) =>
        JsonSerializer.Serialize(terms, JsonOptions);

    private static string SerializeUnreadNarrow(ConversationKey conversation, long currentUserId)
    {
        var conversationTerms = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(
            SerializeNarrow(conversation, currentUserId),
            JsonOptions) ?? [];
        conversationTerms.Add(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["operator"] = JsonSerializer.SerializeToElement("is", JsonOptions),
            ["operand"] = JsonSerializer.SerializeToElement("unread", JsonOptions)
        });
        return JsonSerializer.Serialize(conversationTerms, JsonOptions);
    }

    private static IReadOnlyDictionary<string, object> NarrowTerm(string op, object operand) =>
        new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["operator"] = op,
            ["operand"] = operand
        };

    private static IReadOnlyList<DomainEvent> ToEvents(
        JsonElement value,
        DomainEventSource source,
        long currentUserId)
    {
        var eventId = GetInt64(value, "id");
        var kind = GetString(value, "type") ?? "unknown";
        try
        {
            return kind switch
            {
                "heartbeat" => [new HeartbeatEvent(eventId, source)],
                "message" => ToMessageEvents(value, source, currentUserId, eventId),
                "delete_message" => ToDeleteMessageEvents(value, source, eventId),
                "update_message" => ToUpdateMessageEvents(value, source, eventId),
                "reaction" => ToReactionEvents(value, source, eventId),
                "update_message_flags" =>
                [
                    new MessageFlagsChangedEvent(
                        GetInt64Array(value, "messages"),
                        GetBoolean(value, "all") ?? false,
                        string.Equals(GetString(value, "op", "operation"), "add", StringComparison.OrdinalIgnoreCase)
                            ? MessageFlagOperation.Add
                            : MessageFlagOperation.Remove,
                        GetString(value, "flag") ?? string.Empty,
                        eventId,
                        source)
                ],
                "realm_user" => ToRealmUserEvents(value, source, eventId),
                "subscription" => ToSubscriptionEvents(value, source, eventId),
                "stream" => ToStreamEvents(value, source, eventId),
                "restart" =>
                [
                    new ServerRestartedEvent(
                        checked((int)(GetInt64(value, "zulip_feature_level") ?? 0)),
                        eventId,
                        source)
                ],
                _ => [new UnknownDomainEvent(kind, eventId, source)]
            };
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return [new UnknownDomainEvent(kind, eventId, source)];
        }
    }

    private static IReadOnlyList<DomainEvent> ToMessageEvents(
        JsonElement value,
        DomainEventSource source,
        long currentUserId,
        long? eventId)
    {
        if (!TryGetProperty(value, "message", out var message) ||
            ToMessage(message, currentUserId, value) is not { } chat)
        {
            return [new UnknownDomainEvent("message", eventId, source)];
        }

        var events = new List<DomainEvent>
        {
            new MessageUpsertEvent(
                chat,
                eventId,
                source,
                GetString(value, "local_message_id", "local_id"))
        };
        if (chat.Conversation is ChannelTopic channel)
        {
            events.Add(new TopicUpsertEvent(new TopicSummary(channel.ChannelId, channel.Topic, chat.Id), eventId, source));
        }
        return events;
    }

    private static IReadOnlyList<DomainEvent> ToDeleteMessageEvents(
        JsonElement value,
        DomainEventSource source,
        long? eventId)
    {
        var ids = GetInt64Array(value, "message_ids");
        if (ids.Length == 0 && GetInt64(value, "message_id") is { } id) ids = [id];
        return ids.Length == 0
            ? [new UnknownDomainEvent("delete_message", eventId, source)]
            : [new MessageDeletedEvent(ids, eventId, source)];
    }

    private static IReadOnlyList<DomainEvent> ToUpdateMessageEvents(
        JsonElement value,
        DomainEventSource source,
        long? eventId)
    {
        var events = new List<DomainEvent>();
        var messageId = GetInt64(value, "message_id");
        var renderingOnly = GetBoolean(value, "rendering_only") ?? false;
        if (!renderingOnly && messageId is { } contentMessageId && GetString(value, "content") is { } content)
        {
            events.Add(new MessageContentChangedEvent(contentMessageId, content, eventId, source));
        }

        if (messageId is { } flaggedMessageId && TryGetProperty(value, "flags", out var flagsElement) &&
            flagsElement.ValueKind == JsonValueKind.Array)
        {
            var flags = GetStringArray(value, "flags");
            var isRead = flags.Contains("read", StringComparer.OrdinalIgnoreCase);
            events.Add(new MessageFlagsChangedEvent(
                [flaggedMessageId],
                false,
                isRead ? MessageFlagOperation.Add : MessageFlagOperation.Remove,
                "read",
                eventId,
                source));
            if (flags.Contains("starred", StringComparer.OrdinalIgnoreCase))
            {
                events.Add(new MessageFlagsChangedEvent(
                    [flaggedMessageId],
                    false,
                    MessageFlagOperation.Add,
                    "starred",
                    eventId,
                    source));
            }
        }

        var hasTopicMove = TryGetProperty(value, "subject", out _);
        var hasChannelMove = GetInt64(value, "new_stream_id") is not null;
        if (hasTopicMove || hasChannelMove)
        {
            var ids = GetInt64Array(value, "message_ids");
            if (ids.Length == 0 && messageId is { } singleId) ids = [singleId];
            var channelId = GetInt64(value, "new_stream_id", "stream_id");
            var topic = GetString(value, "subject", "orig_subject");
            if (ids.Length > 0 && channelId is > 0 && topic is not null)
            {
                events.Add(new MessageMovedEvent(ids, new ChannelTopic(channelId.Value, topic), eventId, source));
                events.Add(new TopicUpsertEvent(new TopicSummary(channelId.Value, topic, ids.Max()), eventId, source));
            }
        }

        return events.Count == 0
            ? [new UnknownDomainEvent("update_message", eventId, source)]
            : events;
    }

    private static IReadOnlyList<DomainEvent> ToReactionEvents(
        JsonElement value,
        DomainEventSource source,
        long? eventId)
    {
        var messageId = GetInt64(value, "message_id");
        var userId = GetInt64(value, "user_id");
        var emojiName = GetString(value, "emoji_name");
        var emojiCode = GetString(value, "emoji_code");
        var reactionType = GetString(value, "reaction_type");
        if (messageId is not > 0 || userId is not > 0 ||
            string.IsNullOrWhiteSpace(emojiName) || string.IsNullOrWhiteSpace(emojiCode) ||
            string.IsNullOrWhiteSpace(reactionType))
        {
            return [new UnknownDomainEvent("reaction", eventId, source)];
        }

        var reaction = new EmojiReaction(
            new EmojiReactionIdentity(emojiName, emojiCode, reactionType),
            userId.Value,
            GetString(value, "user_full_name"));
        return
        [
            new MessageReactionChangedEvent(
                messageId.Value,
                reaction,
                string.Equals(GetString(value, "op"), "add", StringComparison.OrdinalIgnoreCase),
                eventId,
                source)
        ];
    }

    private static IReadOnlyList<DomainEvent> ToRealmUserEvents(
        JsonElement value,
        DomainEventSource source,
        long? eventId)
    {
        var person = GetObject(value, "person");
        var op = GetString(value, "op");
        if (string.Equals(op, "add", StringComparison.OrdinalIgnoreCase) && ToUserOrNull(person) is { } added)
        {
            return [new UserUpsertEvent(added, eventId, source)];
        }

        var userId = GetInt64(person, "user_id", "id");
        if (userId is null) return [new UnknownDomainEvent("realm_user", eventId, source)];
        return
        [
            new UserPatchedEvent(
                userId.Value,
                GetString(person, "full_name"),
                GetString(person, "new_email", "email"),
                GetBoolean(person, "is_active"),
                eventId,
                source)
        ];
    }

    private static IReadOnlyList<DomainEvent> ToSubscriptionEvents(
        JsonElement value,
        DomainEventSource source,
        long? eventId)
    {
        var op = GetString(value, "op");
        if (string.Equals(op, "add", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(op, "remove", StringComparison.OrdinalIgnoreCase))
        {
            var removed = string.Equals(op, "remove", StringComparison.OrdinalIgnoreCase);
            var events = GetArray(value, "subscriptions")
                .Select(ToSubscription)
                .Where(static item => item is not null)
                .Select(item => (DomainEvent)new SubscriptionChangedEvent(item!, removed, eventId, source))
                .ToArray();
            return events.Length == 0
                ? [new UnknownDomainEvent("subscription", eventId, source)]
                : events;
        }

        if (string.Equals(op, "update", StringComparison.OrdinalIgnoreCase))
        {
            var property = GetString(value, "property");
            var channelId = GetInt64(value, "stream_id");
            var preference = string.Equals(property, "is_muted", StringComparison.OrdinalIgnoreCase)
                ? SubscriptionPreference.Muted
                : string.Equals(property, "pin_to_top", StringComparison.OrdinalIgnoreCase)
                    ? SubscriptionPreference.Pinned
                    : (SubscriptionPreference?)null;
            if (channelId is { } id && preference is { } known && GetBoolean(value, "value") is { } preferenceValue)
            {
                return [new SubscriptionPreferenceChangedEvent(id, known, preferenceValue, eventId, source)];
            }

            return [new IgnoredDomainEvent("subscription", "subscription_property_outside_mvp", eventId, source)];
        }

        return [new UnknownDomainEvent("subscription", eventId, source)];
    }

    private static IReadOnlyList<DomainEvent> ToStreamEvents(
        JsonElement value,
        DomainEventSource source,
        long? eventId)
    {
        var op = GetString(value, "op");
        if (string.Equals(op, "update", StringComparison.OrdinalIgnoreCase) &&
            GetInt64(value, "stream_id") is { } channelId)
        {
            var property = GetString(value, "property");
            if (string.Equals(property, "name", StringComparison.OrdinalIgnoreCase))
            {
                return [new SubscriptionPatchedEvent(channelId, GetString(value, "value", "name"), null, eventId, source)];
            }
            if (string.Equals(property, "is_archived", StringComparison.OrdinalIgnoreCase) && GetBoolean(value, "value") is { } archived)
            {
                return [new SubscriptionPatchedEvent(channelId, null, !archived, eventId, source)];
            }
            if (string.Equals(property, "invite_only", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(property, "is_private", StringComparison.OrdinalIgnoreCase))
            {
                return GetBoolean(value, "value") is { } isPrivate
                    ? [new SubscriptionPatchedEvent(channelId, null, null, eventId, source, IsPrivate: isPrivate)]
                    : [new SubscriptionPatchedEvent(channelId, null, null, eventId, source, ClearEligibility: true)];
            }
            if (string.Equals(property, "is_web_public", StringComparison.OrdinalIgnoreCase))
            {
                return GetBoolean(value, "value") is { } isWebPublic
                    ? [new SubscriptionPatchedEvent(channelId, null, null, eventId, source, IsWebPublic: isWebPublic)]
                    : [new SubscriptionPatchedEvent(channelId, null, null, eventId, source, ClearEligibility: true)];
            }
            if (string.Equals(property, "topics_policy", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseTopicsPolicy(GetString(value, "value")) is { } topicsPolicy
                    ? [new SubscriptionPatchedEvent(channelId, null, null, eventId, source, TopicsPolicy: topicsPolicy)]
                    : [new SubscriptionPatchedEvent(channelId, null, null, eventId, source, ClearEligibility: true)];
            }
        }

        if (string.Equals(op, "delete", StringComparison.OrdinalIgnoreCase))
        {
            var ids = GetInt64Array(value, "stream_ids");
            if (ids.Length == 0)
            {
                ids = GetArray(value, "streams")
                    .Select(item => GetInt64(item, "stream_id", "id"))
                    .Where(static id => id is > 0)
                    .Select(static id => id!.Value)
                    .ToArray();
            }
            if (ids.Length > 0)
            {
                return ids.Select(id => (DomainEvent)new SubscriptionRemovedEvent(id, eventId, source)).ToArray();
            }
        }

        return [new UnknownDomainEvent("stream", eventId, source)];
    }

    private static ChatMessage? ToMessage(
        JsonElement value,
        long currentUserId,
        JsonElement? eventEnvelope = null)
    {
        var id = GetInt64(value, "id");
        var senderId = GetInt64(value, "sender_id");
        if (id is null || senderId is null) return null;
        ConversationKey conversation;
        var messageType = GetString(value, "type");
        if (string.Equals(messageType, "stream", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(messageType, "channel", StringComparison.OrdinalIgnoreCase))
        {
            var channelId = GetInt64(value, "stream_id");
            var topic = GetString(value, "subject", "topic");
            if (channelId is null || topic is null) return null;
            conversation = new ChannelTopic(channelId.Value, topic);
        }
        else if (string.Equals(messageType, "private", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(messageType, "direct", StringComparison.OrdinalIgnoreCase))
        {
            var recipients = GetArray(value, "display_recipient")
                .Select(item => GetInt64(item, "id", "user_id"))
                .Where(static item => item is not null)
                .Select(static item => item!.Value)
                .Where(idValue => idValue != currentUserId)
                .ToArray();
            conversation = new DirectMessage(recipients);
        }
        else
        {
            return null;
        }

        var timestamp = GetInt64(value, "timestamp") is { } seconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.UnixEpoch;
        var flags = GetStringArray(value, "flags");
        if (flags.Length == 0 && eventEnvelope is { } envelope) flags = GetStringArray(envelope, "flags");
        var isRead = flags.Contains("read", StringComparer.OrdinalIgnoreCase);
        var isStarred = flags.Contains("starred", StringComparer.OrdinalIgnoreCase);
        var reactions = GetArray(value, "reactions")
            .Select(ToReactionOrNull)
            .Where(static reaction => reaction is not null)
            .Cast<EmojiReaction>()
            .ToArray();
        return new ChatMessage(
            id.Value,
            conversation,
            senderId.Value,
            GetString(value, "content") ?? string.Empty,
            timestamp,
            isRead,
            GetString(value, "sender_full_name"),
            GetString(value, "avatar_url", "sender_avatar_url"),
            isStarred,
            reactions);
    }

    private static Subscription? ToSubscription(JsonElement value)
    {
        var id = GetInt64(value, "stream_id", "channel_id");
        var name = GetString(value, "name", "stream_name");
        return id is > 0 && !string.IsNullOrWhiteSpace(name)
            ? new Subscription(
                id.Value,
                name,
                !(GetBoolean(value, "is_archived") ?? false),
                GetBoolean(value, "is_muted") ?? false,
                GetBoolean(value, "pin_to_top") ?? false,
                GetString(value, "color"),
                GetBoolean(value, "invite_only"),
                TryParseTopicsPolicy(GetString(value, "topics_policy")),
                GetBoolean(value, "is_web_public"))
            : null;
    }

    private static ChannelSummary? ToChannelSummary(JsonElement value)
    {
        var id = GetInt64(value, "stream_id", "channel_id");
        var name = GetString(value, "name");
        return id is > 0 && !string.IsNullOrWhiteSpace(name)
            ? new ChannelSummary(id.Value, name, GetString(value, "description"), GetBoolean(value, "is_archived") ?? false, GetInt32(value, "subscriber_count"), GetBoolean(value, "invite_only") ?? false, false, GetString(value, "color"), GetInt32(value, "stream_weekly_traffic"), GetInt64(value, "folder_id"))
            : null;
    }

    private static ChannelDetails? ToChannelDetails(JsonElement value)
    {
        var id = GetInt64(value, "stream_id", "channel_id");
        var name = GetString(value, "name");
        if (id is not > 0 || string.IsNullOrWhiteSpace(name)) return null;
        var created = GetInt64(value, "date_created") is { } seconds && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : (DateTimeOffset?)null;
        return new ChannelDetails(
            id.Value,
            name,
            GetString(value, "description"),
            GetBoolean(value, "is_archived") ?? false,
            GetBoolean(value, "invite_only") ?? false,
            GetBoolean(value, "is_web_public") ?? false,
            GetInt32(value, "subscriber_count"),
            GetInt32(value, "stream_weekly_traffic"),
            GetInt64(value, "folder_id"),
            GetInt64(value, "creator_id"),
            created,
            ToChannelGroupSetting(value, "can_administer_channel_group"),
            ToChannelGroupSetting(value, "can_add_subscribers_group"),
            ToChannelGroupSetting(value, "can_send_message_group"),
            ToChannelGroupSetting(value, "can_subscribe_group"),
            ToChannelGroupSetting(value, "can_create_topic_group"),
            GetBoolean(value, "history_public_to_subscribers") ?? false,
            GetBoolean(value, "is_default_stream") ?? false,
            ToTopicsPolicy(value),
            GetInt32(value, "message_retention_days"),
            ToChannelGroupSetting(value, "can_remove_subscribers_group"),
            ToChannelGroupSetting(value, "can_move_messages_within_channel_group"),
            ToChannelGroupSetting(value, "can_move_messages_out_of_channel_group"),
            ToChannelGroupSetting(value, "can_resolve_topics_group"),
            ToChannelGroupSetting(value, "can_delete_any_message_group"),
            ToChannelGroupSetting(value, "can_delete_own_message_group"));
    }

    private static ChannelFolder? ToChannelFolder(JsonElement value)
    {
        var id = GetInt64(value, "id", "folder_id");
        var name = GetString(value, "name");
        return id is > 0 && !string.IsNullOrWhiteSpace(name)
            ? new ChannelFolder(
                id.Value,
                name,
                GetString(value, "description"),
                GetBoolean(value, "is_archived") ?? false,
                GetInt32(value, "order") ?? 0)
            : null;
    }

    private static ChannelUserGroup? ToChannelUserGroup(JsonElement value)
    {
        var id = GetInt64(value, "id");
        var name = GetString(value, "name");
        return id is > 0 && !string.IsNullOrWhiteSpace(name)
            ? new ChannelUserGroup(id.Value, name, GetBoolean(value, "deactivated", "is_deactivated") ?? false, GetInt64Array(value, "members"), GetInt64Array(value, "direct_subgroup_ids"))
            : null;
    }

    private static ChannelGroupSetting? ToChannelGroupSetting(JsonElement value, string property)
    {
        if (!TryGetProperty(value, property, out var setting)) return null;
        if (setting.ValueKind == JsonValueKind.Number && setting.TryGetInt64(out var groupId) && groupId > 0)
            return new NamedChannelGroupSetting(groupId);
        if (setting.ValueKind != JsonValueKind.Object) return null;
        var members = GetInt64Array(setting, "direct_members");
        var subgroups = GetInt64Array(setting, "direct_subgroups", "direct_subgroup_ids");
        return new AnonymousChannelGroupSetting(members, subgroups);
    }

    private static IReadOnlyList<string> GetSubscriptionResponseNames(JsonElement root, string property, CredentialEnvelope credentials)
    {
        if (!root.TryGetProperty(property, out var value)) return [];
        if (value.ValueKind == JsonValueKind.Array) return GetStringArray(root, property);
        if (value.ValueKind != JsonValueKind.Object) return [];
        var userId = credentials.UserId.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetProperty(userId, out var names) && names.ValueKind == JsonValueKind.Array)
            return names.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String).Select(static item => item.GetString()!).ToArray();
        if (value.TryGetProperty(credentials.Email, out names) && names.ValueKind == JsonValueKind.Array)
            return names.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String).Select(static item => item.GetString()!).ToArray();
        return [];
    }

    private static UserProfile? ToUserOrNull(JsonElement value)
    {
        var id = GetInt64(value, "user_id", "id");
        var name = GetString(value, "full_name");
        return id is > 0 && !string.IsNullOrWhiteSpace(name)
            ? new UserProfile(
                id.Value,
                name,
                GetString(value, "email"),
                GetBoolean(value, "is_active") ?? true,
                GetString(value, "avatar_url"),
                GetInt32(value, "avatar_version"),
                GetBoolean(value, "is_bot") ?? false)
            : null;
    }

    private static UserProfile ToRequiredRealmUser(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            GetInt64(value, "user_id") is not { } id || id <= 0 ||
            GetString(value, "full_name") is not { } fullName || string.IsNullOrWhiteSpace(fullName) ||
            GetString(value, "email") is not { } email || string.IsNullOrWhiteSpace(email) ||
            GetBoolean(value, "is_active") is not { } isActive ||
            GetBoolean(value, "is_bot") is not { } isBot)
        {
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
        }

        var avatarVersion = GetInt32(value, "avatar_version");
        if (avatarVersion is < 0) throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
        return new UserProfile(id, fullName, email, isActive, GetString(value, "avatar_url"), avatarVersion, isBot);
    }

    private static IReadOnlyList<UserTopicVisibility> ToUserTopicVisibilities(JsonElement root)
    {
        return GetArray(root, "user_topics")
            .Select(item =>
            {
                var channelId = GetInt64(item, "stream_id", "channel_id");
                var topic = GetString(item, "topic_name", "topic");
                var policy = GetInt32(item, "visibility_policy");
                return channelId is > 0 && topic is not null && policy is >= 0 and <= 3
                    ? new UserTopicVisibility(channelId.Value, topic, (TopicVisibilityPolicy)policy.Value)
                    : null;
            })
            .Where(static item => item is not null)
            .Cast<UserTopicVisibility>()
            .ToArray();
    }

    private static bool IsOrganizationAdministrator(JsonElement root, long currentUserId)
    {
        var current = GetArray(root, "realm_users")
            .FirstOrDefault(item => GetInt64(item, "user_id", "id") == currentUserId);
        if (current.ValueKind != JsonValueKind.Object) return false;
        var role = GetInt64(current, "role");
        var declaredAdmin = GetBoolean(root, "is_admin");
        return role is 100 or 200 && declaredAdmin is not false;
    }

    private static bool CanCreatePrivateChannel(JsonElement root, long currentUserId)
    {
        if (!TryGetStrictGroupSetting(root, "realm_can_create_private_channel_group", out var setting) || setting is null)
            return false;

        var rawGroups = GetArray(root, "realm_user_groups");
        var groups = rawGroups
            .Select(ToStrictChannelUserGroup)
            .Where(static group => group is not null)
            .Cast<ChannelUserGroup>()
            .ToArray();
        if (groups.Length != rawGroups.Length || groups.Select(static group => group.GroupId).Distinct().Count() != groups.Length)
            return false;
        return ChannelPermissionEvaluator.IsMember(currentUserId, setting, groups);
    }

    private static ChannelUserGroup? ToStrictChannelUserGroup(JsonElement value)
    {
        var id = GetInt64(value, "id");
        var name = GetString(value, "name");
        if (value.ValueKind != JsonValueKind.Object || id is not > 0 || string.IsNullOrWhiteSpace(name) ||
            !TryGetStrictInt64Array(value, "members", out var members) ||
            !TryGetStrictInt64Array(value, "direct_subgroup_ids", out var directSubgroupIds))
        {
            return null;
        }

        var isDeactivated = false;
        if (TryGetProperty(value, "deactivated", out var deactivated))
        {
            if (deactivated.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            isDeactivated = deactivated.GetBoolean();
        }
        else if (TryGetProperty(value, "is_deactivated", out deactivated))
        {
            if (deactivated.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            isDeactivated = deactivated.GetBoolean();
        }

        return new ChannelUserGroup(id.Value, name, isDeactivated, members, directSubgroupIds);
    }

    private static bool TryGetStrictGroupSetting(JsonElement root, string property, out ChannelGroupSetting? setting)
    {
        setting = null;
        if (!TryGetProperty(root, property, out var value)) return false;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var groupId) && groupId > 0)
        {
            setting = new NamedChannelGroupSetting(groupId);
            return true;
        }
        if (value.ValueKind != JsonValueKind.Object ||
            !TryGetStrictInt64Array(value, "direct_members", out var members) ||
            !TryGetStrictInt64Array(value, "direct_subgroups", out var subgroups))
        {
            return false;
        }

        setting = new AnonymousChannelGroupSetting(members, subgroups);
        return true;
    }

    private static bool TryGetStrictInt64Array(JsonElement value, string property, out long[] values)
    {
        values = [];
        if (!TryGetProperty(value, property, out var array) || array.ValueKind != JsonValueKind.Array) return false;
        var parsed = new List<long>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var id) || id <= 0) return false;
            parsed.Add(id);
        }
        if (parsed.Distinct().Count() != parsed.Count) return false;
        values = parsed.ToArray();
        return true;
    }

    private static void EnsureNoUnsupportedParameters(JsonElement root)
    {
        if (GetStringArray(root, "ignored_parameters_unsupported").Length > 0)
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
    }

    private static void EnsureNotErrorResponse(JsonElement root)
    {
        if (string.Equals(GetString(root, "result"), "error", StringComparison.OrdinalIgnoreCase))
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
    }

    private static HashSet<long> GetRequiredSubscriptionChannelIds(JsonElement root)
    {
        EnsureNotErrorResponse(root);
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(root, "subscriptions", out var subscriptions) ||
            subscriptions.ValueKind != JsonValueKind.Array)
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);

        var channelIds = new HashSet<long>();
        foreach (var subscription in subscriptions.EnumerateArray())
        {
            var channelId = GetInt64(subscription, "stream_id");
            if (subscription.ValueKind != JsonValueKind.Object || channelId is not > 0 || !channelIds.Add(channelId.Value))
                throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
        }

        return channelIds;
    }

    private static (string Property, string Value) ToPersonalSetting(ChannelPersonalSettingChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return change.Setting switch
        {
            ChannelPersonalSetting.Color when !string.IsNullOrWhiteSpace(change.ColorValue) => ("color", change.ColorValue),
            ChannelPersonalSetting.Muted when change.BooleanValue is { } value => ("is_muted", value ? "true" : "false"),
            ChannelPersonalSetting.Pinned when change.BooleanValue is { } value => ("pin_to_top", value ? "true" : "false"),
            ChannelPersonalSetting.DesktopNotifications when change.BooleanValue is { } value => ("desktop_notifications", value ? "true" : "false"),
            ChannelPersonalSetting.AudibleNotifications when change.BooleanValue is { } value => ("audible_notifications", value ? "true" : "false"),
            ChannelPersonalSetting.PushNotifications when change.BooleanValue is { } value => ("push_notifications", value ? "true" : "false"),
            ChannelPersonalSetting.EmailNotifications when change.BooleanValue is { } value => ("email_notifications", value ? "true" : "false"),
            ChannelPersonalSetting.WildcardMentionsNotify when change.BooleanValue is { } value => ("wildcard_mentions_notify", value ? "true" : "false"),
            _ => throw new ArgumentException("The personal setting value is invalid.", nameof(change))
        };
    }

    private static bool HasMemberOperationFailure(JsonElement root)
    {
        return GetArray(root, "unauthorized", "not_removed", "not_added", "not_subscribed", "failed", "errors").Length > 0 ||
            GetObject(root, "unauthorized").ValueKind == JsonValueKind.Object;
    }

    private static Dictionary<string, string> ToAdvancedFields(ChannelAdvancedSettingsChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        AddBoolean(fields, "is_archived", change.IsArchived);
        AddBoolean(fields, "is_private", change.IsPrivate);
        AddBoolean(fields, "is_web_public", change.IsWebPublic);
        AddBoolean(fields, "history_public_to_subscribers", change.HistoryPublicToSubscribers);
        AddBoolean(fields, "is_default_stream", change.IsDefaultStream);
        if (change.IsArchived is true) throw new ArgumentException("Archiving uses the dedicated archive operation.", nameof(change));
        if (change.TopicsPolicy is { } topicsPolicy) fields["topics_policy"] = ToTopicsPolicy(topicsPolicy);
        if (change.RetentionPolicy is { } retention) fields["message_retention_days"] = ToRetentionValue(retention);
        var groupSettings = new List<ChannelGroupSettingUpdate>();
        if (change.GroupSetting is { } groupName)
        {
            if (change.NewGroup is null || change.OldGroup is null) throw new ArgumentException("Both old and new group settings are required.", nameof(change));
            groupSettings.Add(new ChannelGroupSettingUpdate(groupName, change.NewGroup, change.OldGroup));
        }
        if (change.GroupSettings is { Count: > 0 }) groupSettings.AddRange(change.GroupSettings);
        foreach (var groupSetting in groupSettings)
        {
            ArgumentNullException.ThrowIfNull(groupSetting.NewGroup);
            ArgumentNullException.ThrowIfNull(groupSetting.OldGroup);
            var property = ToGroupSettingProperty(groupSetting.Name);
            if (fields.ContainsKey(property)) throw new ArgumentException("A group setting can only be changed once per request.", nameof(change));
            fields[property] = JsonSerializer.Serialize(
                new { @new = ToGroupValue(groupSetting.NewGroup), old = ToGroupValue(groupSetting.OldGroup) },
                JsonOptions);
        }
        return fields;
    }

    private static void AddBoolean(IDictionary<string, string> fields, string property, bool? value)
    {
        if (value is { } actual) fields[property] = actual ? "true" : "false";
    }

    private static string ToTopicsPolicy(ChannelTopicsPolicy policy) => policy switch
    {
        ChannelTopicsPolicy.Inherit => "inherit",
        ChannelTopicsPolicy.AllowEmptyTopic => "allow_empty_topic",
        ChannelTopicsPolicy.DisableEmptyTopic => "disable_empty_topic",
        ChannelTopicsPolicy.EmptyTopicOnly => "empty_topic_only",
        _ => throw new ArgumentOutOfRangeException(nameof(policy))
    };

    private static ChannelTopicsPolicy? ToTopicsPolicy(JsonElement value)
    {
        var policy = GetString(value, "topics_policy");
        return policy switch
        {
            null => null,
            "inherit" => ChannelTopicsPolicy.Inherit,
            "allow_empty_topic" => ChannelTopicsPolicy.AllowEmptyTopic,
            "disable_empty_topic" => ChannelTopicsPolicy.DisableEmptyTopic,
            "empty_topic_only" => ChannelTopicsPolicy.EmptyTopicOnly,
            _ => throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse)
        };
    }

    private static ChannelTopicsPolicy? TryParseTopicsPolicy(string? policy) => policy switch
    {
        "inherit" => ChannelTopicsPolicy.Inherit,
        "allow_empty_topic" => ChannelTopicsPolicy.AllowEmptyTopic,
        "disable_empty_topic" => ChannelTopicsPolicy.DisableEmptyTopic,
        "empty_topic_only" => ChannelTopicsPolicy.EmptyTopicOnly,
        _ => null
    };

    private static string ToRetentionValue(ChannelRetentionPolicy retention) => retention.Kind switch
    {
        ChannelRetentionKind.RealmDefault when retention.Days is null => "realm_default",
        ChannelRetentionKind.Unlimited when retention.Days is null => "unlimited",
        ChannelRetentionKind.Days when retention.Days is > 0 => retention.Days.Value.ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentException("The retention policy is invalid.", nameof(retention))
    };

    private static string ToGroupSettingProperty(ChannelGroupSettingName setting) => setting switch
    {
        ChannelGroupSettingName.CanSubscribe => "can_subscribe_group",
        ChannelGroupSettingName.CanAddSubscribers => "can_add_subscribers_group",
        ChannelGroupSettingName.CanRemoveSubscribers => "can_remove_subscribers_group",
        ChannelGroupSettingName.CanAdministerChannel => "can_administer_channel_group",
        ChannelGroupSettingName.CanSendMessage => "can_send_message_group",
        ChannelGroupSettingName.CanCreateTopic => "can_create_topic_group",
        ChannelGroupSettingName.CanMoveMessagesWithinChannel => "can_move_messages_within_channel_group",
        ChannelGroupSettingName.CanMoveMessagesOutOfChannel => "can_move_messages_out_of_channel_group",
        ChannelGroupSettingName.CanResolveTopics => "can_resolve_topics_group",
        ChannelGroupSettingName.CanDeleteAnyMessage => "can_delete_any_message_group",
        ChannelGroupSettingName.CanDeleteOwnMessage => "can_delete_own_message_group",
        _ => throw new ArgumentOutOfRangeException(nameof(setting))
    };

    private static object ToGroupValue(ChannelGroupSetting group) => group switch
    {
        NamedChannelGroupSetting named => named.GroupId,
        AnonymousChannelGroupSetting anonymous => new { direct_members = anonymous.DirectMembers, direct_subgroups = anonymous.DirectSubgroups },
        _ => throw new ArgumentOutOfRangeException(nameof(group))
    };

    private static EmojiReaction? ToReactionOrNull(JsonElement value)
    {
        var userId = GetInt64(value, "user_id");
        var emojiName = GetString(value, "emoji_name");
        var emojiCode = GetString(value, "emoji_code");
        var reactionType = GetString(value, "reaction_type");
        if (userId is not > 0 || string.IsNullOrWhiteSpace(emojiName) ||
            string.IsNullOrWhiteSpace(emojiCode) || string.IsNullOrWhiteSpace(reactionType))
        {
            return null;
        }

        return new EmojiReaction(
            new EmojiReactionIdentity(emojiName, emojiCode, reactionType),
            userId.Value,
            GetString(value, "user_full_name"));
    }

    private static IReadOnlyList<ConversationKey> ToRecentDirectMessages(JsonElement root)
    {
        var result = new List<ConversationKey>();
        foreach (var item in GetArray(root, "recent_private_conversations"))
        {
            var ids = GetArray(item, "user_ids")
                .Select(static value => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var id) ? (long?)id : null)
                .Where(static id => id is > 0)
                .Select(static id => id!.Value)
                .ToArray();
            result.Add(new DirectMessage(ids));
        }
        return result;
    }

    private static UnreadState ToUnread(JsonElement root, long currentUserId)
    {
        if (!TryGetProperty(root, "unread_msgs", out var unread)) return new UnreadState();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var stream in GetArray(unread, "streams"))
        {
            var channelId = GetInt64(stream, "stream_id");
            var topic = GetString(stream, "topic");
            if (channelId is > 0 && topic is not null)
            {
                counts[new ChannelTopic(channelId.Value, topic).CanonicalKey] =
                    GetArray(stream, "unread_message_ids").Length;
            }
        }

        foreach (var direct in GetArray(unread, "pms"))
        {
            var otherUserId = GetInt64(direct, "other_user_id", "sender_id");
            if (otherUserId is not > 0) continue;
            var otherIds = otherUserId == currentUserId ? Array.Empty<long>() : [otherUserId.Value];
            counts[new DirectMessage(otherIds).CanonicalKey] =
                GetArray(direct, "unread_message_ids").Length;
        }

        foreach (var group in GetArray(unread, "huddles"))
        {
            var ids = (GetString(group, "user_ids_string") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0)
                .Where(id => id > 0 && id != currentUserId)
                .ToArray();
            counts[new DirectMessage(ids).CanonicalKey] =
                GetArray(group, "unread_message_ids").Length;
        }

        var reportedTotal = GetInt64(unread, "count") is { } count
            ? checked((int)count)
            : (int?)null;
        return new UnreadState(counts, reportedTotal, GetBoolean(unread, "old_unreads_missing") ?? false);
    }

    private static JsonElement GetObject(JsonElement value, string name) => TryGetProperty(value, name, out var item) ? item : default;
    private static JsonElement[] GetArray(JsonElement value, params string[] names) => names.Select(name => TryGetProperty(value, name, out var item) && item.ValueKind == JsonValueKind.Array ? item.EnumerateArray().ToArray() : null).FirstOrDefault(static item => item is not null) ?? [];
    private static long[] GetInt64Array(JsonElement value, params string[] names) => GetArray(value, names).Select(static item => item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number) ? (long?)number : null).Where(static item => item is not null).Select(static item => item!.Value).ToArray();
    private static int RequirePositiveInt32(JsonElement value, string name)
    {
        var result = GetInt64(value, name);
        if (result is null || result <= 0 || result > int.MaxValue)
        {
            throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
        }
        return checked((int)result.Value);
    }
    private static int? PositiveOrNull(int? value) => value is > 0 ? value : null;
    private static long RequireInt64(JsonElement value, string name) => GetInt64(value, name) ?? throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
    private static string RequireString(JsonElement value, string name) => GetString(value, name) ?? throw new GatewayException(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse);
    private static long? GetInt64(JsonElement value, params string[] names) => names.Select(name => TryGetProperty(value, name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number) ? (long?)number : null).FirstOrDefault(static item => item is not null);

    private static int? GetInt32(JsonElement value, params string[] names) => names
        .Select(name => TryGetProperty(value, name, out var item) &&
                        item.ValueKind == JsonValueKind.Number &&
                        item.TryGetInt32(out var number)
            ? (int?)number
            : null)
        .FirstOrDefault(static item => item is not null);
    private static decimal? GetDecimal(JsonElement value, params string[] names) => names.Select(name => TryGetProperty(value, name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetDecimal(out var number) ? (decimal?)number : null).FirstOrDefault(static item => item is not null);
    private static string? GetString(JsonElement value, params string[] names) => names.Select(name => TryGetProperty(value, name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null).FirstOrDefault(static item => item is not null);
    private static string[] GetStringArray(JsonElement value, params string[] names) => GetArray(value, names).Where(static item => item.ValueKind == JsonValueKind.String).Select(static item => item.GetString()).Where(static item => item is not null).Cast<string>().ToArray();
    private static bool? GetBoolean(JsonElement value, params string[] names) => names.Select(name => TryGetProperty(value, name, out var item) && (item.ValueKind is JsonValueKind.True or JsonValueKind.False) ? (bool?)item.GetBoolean() : null).FirstOrDefault(static item => item is not null);
    private static bool? GetBooleanProperty(JsonElement value, string objectName, string propertyName) => TryGetProperty(value, objectName, out var obj) ? GetBoolean(obj, propertyName) : null;
    private static bool TryGetProperty(JsonElement value, string name, out JsonElement property)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in value.EnumerateObject()) if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) { property = item.Value; return true; }
        }
        property = default;
        return false;
    }
}
