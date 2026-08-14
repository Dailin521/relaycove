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
        var outbox = new Dictionary<string, OutboxEntry>(state.Outbox, StringComparer.Ordinal);
        var messageMutations = new Dictionary<long, MessageMutationState>(state.MessageMutations);
        var unread = state.Unread;
        var connection = state.Connection;

        switch (domainEvent)
        {
            case MessageUpsertEvent upsert:
                unread = AdjustForReplacement(unread, messages, upsert.Message, upsert.Source);
                messages[upsert.Message.Id] = upsert.Message;
                if (upsert.LocalId is { } localId) outbox.Remove(localId);
                break;
            case MessagesUpdatedEvent updated:
                foreach (var message in updated.Messages)
                {
                    unread = AdjustForReplacement(unread, messages, message, updated.Source);
                    messages[message.Id] = message;
                }
                break;
            case MessageContentChangedEvent changed when messages.TryGetValue(changed.MessageId, out var existing):
                messages[changed.MessageId] = existing with { Content = changed.Content };
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
                break;
            case SubscriptionRemovedEvent removed:
                subscriptions.Remove(removed.ChannelId);
                unread = unread.RemoveChannel(removed.ChannelId);
                RemoveChannel(removed.ChannelId, messages, topics);
                break;
            case SubscriptionChangedEvent subscription:
                subscriptions[subscription.Subscription.ChannelId] = subscription.Subscription;
                break;
            case SubscriptionPatchedEvent subscriptionPatch when subscriptions.TryGetValue(subscriptionPatch.ChannelId, out var subscriptionExisting):
                subscriptions[subscriptionPatch.ChannelId] = subscriptionExisting with
                {
                    Name = subscriptionPatch.Name ?? subscriptionExisting.Name,
                    IsActive = subscriptionPatch.IsActive ?? subscriptionExisting.IsActive
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
            case TopicUpsertEvent topic:
                topics[TopicKey(topic.Topic.ChannelId, topic.Topic.Topic)] = topic.Topic;
                break;
            case MessageDeletedEvent deleted:
                foreach (var id in deleted.MessageIds)
                {
                    if (messages.TryGetValue(id, out var deletedMessage) && !deletedMessage.IsRead)
                    {
                        unread = unread.Adjust(deletedMessage.Conversation.CanonicalKey, -1);
                    }
                    messages.Remove(id);
                    messageMutations.Remove(id);
                }
                break;
            case MessageMovedEvent moved:
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
                break;
            case MessageFlagsChangedEvent flags when string.Equals(flags.Flag, "read", StringComparison.OrdinalIgnoreCase):
                var read = flags.Operation == MessageFlagOperation.Add;
                if (flags.AllMessages && read)
                {
                    foreach (var pair in messages.ToArray()) messages[pair.Key] = pair.Value with { IsRead = true };
                    unread = new UnreadState();
                    break;
                }

                var ids = flags.AllMessages ? messages.Keys.ToArray() : flags.MessageIds;
                foreach (var id in ids)
                {
                    if (!messages.TryGetValue(id, out var flagged) || flagged.IsRead == read) continue;
                    unread = unread.Adjust(flagged.Conversation.CanonicalKey, read ? -1 : 1);
                    messages[id] = flagged with { IsRead = read };
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
        return new ClientState(messages, subscriptions, users, topics, outbox, unread, connection, lastEventId, messageMutations);
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

    private static string TopicKey(long channelId, string topic) =>
        new ChannelTopic(channelId, topic).CanonicalKey;
}
