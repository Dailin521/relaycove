namespace RelayCove.Core;

public static class DomainReducer
{
    public static ClientState Apply(ClientState state, DomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent.EventId is { } eventId && state.LastEventId is { } seen && eventId <= seen)
        {
            return state;
        }

        return ApplyCore(state, domainEvent, advanceCursor: true);
    }

    public static ClientState Apply(ClientState state, IEnumerable<DomainEvent> events)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(events);
        var ordered = events.ToArray();
        var current = state;
        var index = 0;
        while (index < ordered.Length)
        {
            var eventId = ordered[index].EventId;
            if (eventId is null)
            {
                current = ApplyCore(current, ordered[index], advanceCursor: false);
                index++;
                continue;
            }

            var end = index + 1;
            while (end < ordered.Length && ordered[end].EventId == eventId) end++;
            if (current.LastEventId is null || eventId > current.LastEventId)
            {
                for (var item = index; item < end; item++)
                {
                    current = ApplyCore(current, ordered[item], advanceCursor: false);
                }

                current = current with { LastEventId = eventId };
            }

            index = end;
        }

        return current;
    }

    private static ClientState ApplyCore(ClientState state, DomainEvent domainEvent, bool advanceCursor)
    {
        var messages = new Dictionary<long, ChatMessage>(state.Messages);
        var subscriptions = new Dictionary<long, Subscription>(state.Subscriptions);
        var users = new Dictionary<long, UserProfile>(state.Users);
        var topics = new Dictionary<string, TopicSummary>(state.Topics);
        var summaries = new Dictionary<string, ConversationSummary>(state.ConversationSummaries);
        var outbox = new Dictionary<string, OutboxEntry>(state.Outbox, StringComparer.Ordinal);
        var messageMutations = new Dictionary<long, MessageMutationState>(state.MessageMutations);
        var unread = state.Unread;
        var connection = state.Connection;

        switch (domainEvent)
        {
            case MessageUpsertEvent upsert:
                unread = AdjustForReplacement(unread, messages, upsert.Message, upsert.Source);
                messages[upsert.Message.Id] = upsert.Message;
                UpdateConversationSummary(summaries, upsert.Message);
                UpdateTopicFromMessage(topics, upsert.Message);
                if (upsert.LocalId is { } localId) outbox.Remove(localId);
                break;
            case MessagesUpdatedEvent updated:
                foreach (var message in updated.Messages)
                {
                    unread = AdjustForReplacement(unread, messages, message, updated.Source);
                    messages[message.Id] = message;
                    UpdateConversationSummary(summaries, message);
                    UpdateTopicFromMessage(topics, message);
                }
                break;
            case MessageContentChangedEvent changed when messages.TryGetValue(changed.MessageId, out var existing):
                messages[changed.MessageId] = existing with { Content = changed.Content };
                UpdateConversationSummary(summaries, messages[changed.MessageId]);
                messageMutations.Remove(changed.MessageId);
                break;
            case MessageReactionChangedEvent changed when messages.TryGetValue(changed.MessageId, out var reactionMessage):
                var reactions = reactionMessage.Reactions.ToList();
                var reactionIndex = reactions.FindIndex(item =>
                    item.UserId == changed.Reaction.UserId &&
                    string.Equals(item.Identity.CanonicalKey, changed.Reaction.Identity.CanonicalKey, StringComparison.Ordinal));
                if (changed.Add && reactionIndex < 0)
                {
                    reactions.Add(changed.Reaction);
                }
                else if (!changed.Add && reactionIndex >= 0)
                {
                    reactions.RemoveAt(reactionIndex);
                }
                messages[changed.MessageId] = reactionMessage with { Reactions = reactions.ToArray() };
                messageMutations.Remove(changed.MessageId);
                break;
            case SendConfirmedEvent sent:
                messages[sent.Message.Id] = sent.Message;
                UpdateConversationSummary(summaries, sent.Message);
                UpdateTopicFromMessage(topics, sent.Message);
                outbox.Remove(sent.LocalId);
                break;
            case OutboxQueuedEvent queued:
                outbox[queued.Entry.LocalId] = queued.Entry;
                break;
            case OutboxFailedEvent failed when outbox.TryGetValue(failed.LocalId, out var failedEntry):
                outbox[failed.LocalId] = OutboxTimingPolicy.MarkFailed(failedEntry, failed.Failure);
                break;
            case SubscriptionChangedEvent subscription when subscription.IsRemoved:
                subscriptions.Remove(subscription.Subscription.ChannelId);
                unread = unread.RemoveChannel(subscription.Subscription.ChannelId);
                RemoveChannel(subscription.Subscription.ChannelId, messages, topics);
                RemoveChannelSummaries(subscription.Subscription.ChannelId, summaries);
                break;
            case SubscriptionRemovedEvent removed:
                subscriptions.Remove(removed.ChannelId);
                unread = unread.RemoveChannel(removed.ChannelId);
                RemoveChannel(removed.ChannelId, messages, topics);
                RemoveChannelSummaries(removed.ChannelId, summaries);
                break;
            case SubscriptionChangedEvent subscription:
                subscriptions[subscription.Subscription.ChannelId] = subscription.Subscription;
                break;
            case SubscriptionPatchedEvent subscriptionPatch when subscriptions.TryGetValue(subscriptionPatch.ChannelId, out var subscriptionExisting):
                subscriptions[subscriptionPatch.ChannelId] = subscriptionExisting with
                {
                    Name = subscriptionPatch.Name ?? subscriptionExisting.Name,
                    IsActive = subscriptionPatch.IsActive ?? subscriptionExisting.IsActive,
                    IsPrivate = subscriptionPatch.ClearEligibility
                        ? null
                        : subscriptionPatch.IsPrivate ?? subscriptionExisting.IsPrivate,
                    IsWebPublic = subscriptionPatch.ClearEligibility
                        ? null
                        : subscriptionPatch.IsWebPublic ?? subscriptionExisting.IsWebPublic,
                    TopicsPolicy = subscriptionPatch.ClearEligibility
                        ? null
                        : subscriptionPatch.TopicsPolicy ?? subscriptionExisting.TopicsPolicy
                };
                break;
            case SubscriptionPreferenceChangedEvent preference when subscriptions.TryGetValue(preference.ChannelId, out var preferenceExisting):
                subscriptions[preference.ChannelId] = preference.Preference == SubscriptionPreference.Muted
                    ? preferenceExisting with { IsMuted = preference.Value }
                    : preferenceExisting with { IsPinned = preference.Value };
                break;
            case UserUpsertEvent user:
                users[user.User.UserId] = user.User;
                break;
            case UserPatchedEvent userPatch when users.TryGetValue(userPatch.UserId, out var userExisting):
                users[userPatch.UserId] = userExisting with
                {
                    FullName = userPatch.FullName ?? userExisting.FullName,
                    Email = userPatch.Email ?? userExisting.Email,
                    IsActive = userPatch.IsActive ?? userExisting.IsActive
                };
                break;
            case UserPresenceChangedEvent presence when state.Presence.IsAvailable:
                var presences = new Dictionary<long, UserPresence>(state.Presence.Users)
                {
                    [presence.Presence.UserId] = presence.Presence
                };
                state = state with { Presence = new PresenceState(state.Presence.IsAvailable, presences) };
                break;
            case UserStatusChangedEvent userStatus when state.UserStatuses.IsAvailable:
                var userStatuses = new Dictionary<long, UserStatusContent>(state.UserStatuses.Users);
                if (userStatus.Status is null || userStatus.Status.IsEmpty)
                    userStatuses.Remove(userStatus.UserId);
                else
                    userStatuses[userStatus.UserId] = userStatus.Status;
                state = state with { UserStatuses = new UserStatusState(true, userStatuses) };
                break;
            case TopicUpsertEvent topic:
                topics[TopicKey(topic.Topic.ChannelId, topic.Topic.Topic)] = topic.Topic;
                break;
            case MessageDeletedEvent deleted:
                var deletedTopics = deleted.MessageIds
                    .Select(id => messages.GetValueOrDefault(id)?.Conversation)
                    .OfType<ChannelTopic>()
                    .DistinctBy(topic => topic.CanonicalKey)
                    .ToArray();
                foreach (var id in deleted.MessageIds)
                {
                    if (messages.TryGetValue(id, out var deletedMessage) && !deletedMessage.IsRead)
                    {
                        unread = unread.Adjust(deletedMessage.Conversation.CanonicalKey, -1);
                    }
                    messages.Remove(id);
                    messageMutations.Remove(id);
                }
                RefreshConversationSummaries(summaries, messages, deleted.MessageIds);
                foreach (var topic in deletedTopics) RecomputeTopicSummary(topics, messages, topic);
                break;
            case MessageMovedEvent moved:
                var sourceTopics = moved.MessageIds
                    .Select(id => messages.GetValueOrDefault(id)?.Conversation)
                    .OfType<ChannelTopic>()
                    .DistinctBy(topic => topic.CanonicalKey)
                    .ToArray();
                foreach (var id in moved.MessageIds)
                {
                    if (!messages.TryGetValue(id, out var movedMessage)) continue;
                    if (!movedMessage.IsRead && movedMessage.Conversation != moved.Destination)
                    {
                        unread = unread.Adjust(movedMessage.Conversation.CanonicalKey, -1)
                            .Adjust(moved.Destination.CanonicalKey, 1);
                    }
                    messages[id] = movedMessage with { Conversation = moved.Destination };
                }
                RefreshConversationSummaries(summaries, messages, moved.MessageIds);
                RefreshConversationSummary(summaries, messages, moved.Destination);
                foreach (var topic in sourceTopics) RecomputeTopicSummary(topics, messages, topic);
                if (moved.Destination is ChannelTopic destination) RecomputeTopicSummary(topics, messages, destination);
                break;
            case MessageFlagsChangedEvent flags when string.Equals(flags.Flag, "read", StringComparison.OrdinalIgnoreCase):
                var read = flags.Operation == MessageFlagOperation.Add;
                if (flags.AllMessages && read)
                {
                    foreach (var pair in messages.ToArray())
                    {
                        messages[pair.Key] = pair.Value with { IsRead = true };
                        UpdateConversationSummary(summaries, messages[pair.Key]);
                    }
                    unread = new UnreadState();
                    break;
                }

                var ids = flags.AllMessages ? messages.Keys.ToArray() : flags.MessageIds;
                foreach (var id in ids)
                {
                    if (!messages.TryGetValue(id, out var flagged) || flagged.IsRead == read) continue;
                    unread = unread.Adjust(flagged.Conversation.CanonicalKey, read ? -1 : 1);
                    messages[id] = flagged with { IsRead = read };
                    UpdateConversationSummary(summaries, messages[id]);
                }
                break;
            case MessageFlagsChangedEvent flags when string.Equals(flags.Flag, "starred", StringComparison.OrdinalIgnoreCase):
                var starred = flags.Operation == MessageFlagOperation.Add;
                var starredIds = flags.AllMessages ? messages.Keys.ToArray() : flags.MessageIds;
                foreach (var id in starredIds)
                {
                    if (messages.TryGetValue(id, out var flagged))
                    {
                        messages[id] = flagged with { IsStarred = starred };
                        UpdateConversationSummary(summaries, messages[id]);
                        messageMutations.Remove(id);
                    }
                }
                break;
            case ConnectionChangedEvent changed:
                connection = changed.Connection;
                break;
            case ServerRestartedEvent:
                connection = new ConnectionState(ConnectionStatus.Reconnecting, "server_restart");
                break;
        }

        var lastEventId = advanceCursor && domainEvent.EventId is { } incoming
            ? incoming
            : state.LastEventId;
        return new ClientState(
            messages,
            subscriptions,
            users,
            topics,
            summaries,
            outbox,
            unread,
            connection,
            lastEventId,
            messageMutations,
            state.Presence,
            state.UserStatuses);
    }

    private static UnreadState AdjustForReplacement(
        UnreadState unread,
        IReadOnlyDictionary<long, ChatMessage> messages,
        ChatMessage replacement,
        DomainEventSource source)
    {
        if (messages.TryGetValue(replacement.Id, out var existing))
        {
            if (!existing.IsRead)
            {
                unread = unread.Adjust(existing.Conversation.CanonicalKey, -1);
            }
            if (!replacement.IsRead)
            {
                unread = unread.Adjust(replacement.Conversation.CanonicalKey, 1);
            }
        }
        else if (source == DomainEventSource.Realtime && !replacement.IsRead)
        {
            unread = unread.Adjust(replacement.Conversation.CanonicalKey, 1);
        }

        return unread;
    }

    private static void RemoveChannel(
        long channelId,
        IDictionary<long, ChatMessage> messages,
        IDictionary<string, TopicSummary> topics)
    {
        foreach (var id in messages
                     .Where(pair => pair.Value.Conversation is ChannelTopic channel && channel.ChannelId == channelId)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            messages.Remove(id);
        }

        foreach (var key in topics
                     .Where(pair => pair.Value.ChannelId == channelId)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            topics.Remove(key);
        }
    }

    private static void UpdateConversationSummary(
        IDictionary<string, ConversationSummary> summaries,
        ChatMessage message)
    {
        var key = message.Conversation.CanonicalKey;
        if (!summaries.TryGetValue(key, out var existing) || existing.LatestMessage.Id <= message.Id)
        {
            summaries[key] = new ConversationSummary(message.Conversation, message);
        }
    }

    private static void UpdateTopicFromMessage(IDictionary<string, TopicSummary> topics, ChatMessage message) =>
        UpdateTopicFromConversation(topics, message.Conversation, message.Id);

    private static void UpdateTopicFromConversation(
        IDictionary<string, TopicSummary> topics,
        ConversationKey conversation,
        long messageId)
    {
        if (conversation is not ChannelTopic channel) return;
        var key = TopicKey(channel.ChannelId, channel.Topic);
        if (!topics.TryGetValue(key, out var existing) || existing.MaxMessageId is null || existing.MaxMessageId < messageId)
        {
            topics[key] = new TopicSummary(channel.ChannelId, channel.Topic, messageId);
        }
    }

    private static void RecomputeTopicSummary(
        IDictionary<string, TopicSummary> topics,
        IReadOnlyDictionary<long, ChatMessage> messages,
        ChannelTopic topic)
    {
        var maximumId = messages.Values
            .Where(message => message.Conversation == topic)
            .Select(message => (long?)message.Id)
            .Max();
        var key = TopicKey(topic.ChannelId, topic.Topic);
        if (maximumId is null) topics.Remove(key);
        else topics[key] = new TopicSummary(topic.ChannelId, topic.Topic, maximumId);
    }

    private static void RefreshConversationSummaries(
        IDictionary<string, ConversationSummary> summaries,
        IReadOnlyDictionary<long, ChatMessage> messages,
        IEnumerable<long> messageIds)
    {
        var affected = messageIds
            .Select(id => summaries.Values.FirstOrDefault(summary => summary.LatestMessage.Id == id)?.Conversation)
            .Where(static conversation => conversation is not null)
            .Cast<ConversationKey>()
            .DistinctBy(conversation => conversation.CanonicalKey)
            .ToArray();
        foreach (var conversation in affected) RefreshConversationSummary(summaries, messages, conversation);
    }

    private static void RefreshConversationSummary(
        IDictionary<string, ConversationSummary> summaries,
        IReadOnlyDictionary<long, ChatMessage> messages,
        ConversationKey conversation)
    {
        var latest = messages.Values
            .Where(message => message.Conversation == conversation)
            .OrderByDescending(message => message.Id)
            .FirstOrDefault();
        if (latest is null) summaries.Remove(conversation.CanonicalKey);
        else summaries[conversation.CanonicalKey] = new ConversationSummary(conversation, latest);
    }

    private static void RemoveChannelSummaries(long channelId, IDictionary<string, ConversationSummary> summaries)
    {
        foreach (var key in summaries
                     .Where(pair => pair.Value.Conversation is ChannelTopic channel && channel.ChannelId == channelId)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            summaries.Remove(key);
        }
    }

    private static string TopicKey(long channelId, string topic) =>
        new ChannelTopic(channelId, topic).CanonicalKey;
}
