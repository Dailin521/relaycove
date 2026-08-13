import { channelTopic, directMessage } from '../domain/conversation';
import type {
    ChatMessage,
    EmojiReaction,
    EmojiReactionIdentity,
    EventBatch,
    EventPatch,
    EventPatchGroup,
    HistoryResult,
    RegisterSnapshot,
    Subscription,
    TopicSummary,
    UnreadState,
    UserProfile,
} from '../domain/types';
import { ZulipWebError } from './errors';

type JsonObject = Record<string, unknown>;

function object(value: unknown): JsonObject | undefined {
    return value !== null && typeof value === 'object' && !Array.isArray(value)
        ? value as JsonObject
        : undefined;
}

function array(value: unknown): readonly unknown[] {
    return Array.isArray(value) ? value : [];
}

function string(value: unknown): string | undefined {
    return typeof value === 'string' ? value : undefined;
}

function number(value: unknown): number | undefined {
    return typeof value === 'number' && Number.isSafeInteger(value) ? value : undefined;
}

function boolean(value: unknown): boolean | undefined {
    return typeof value === 'boolean' ? value : undefined;
}

function stringArray(value: unknown): readonly string[] {
    return array(value).filter((item): item is string => typeof item === 'string');
}

function numberArray(value: unknown): readonly number[] {
    return array(value).filter((item): item is number => typeof item === 'number' && Number.isSafeInteger(item));
}

function mapReactionIdentity(value: unknown): EmojiReactionIdentity | undefined {
    const item = object(value);
    const emojiName = string(item?.emoji_name);
    const emojiCode = string(item?.emoji_code);
    const reactionType = string(item?.reaction_type);
    return emojiName && emojiCode && reactionType
        ? { emojiName, emojiCode, reactionType }
        : undefined;
}

function mapReactions(rawValue: unknown): readonly EmojiReaction[] {
    const grouped = new Map<string, { identity: EmojiReactionIdentity; userIds: Set<number> }>();
    for (const reactionValue of array(rawValue)) {
        const item = object(reactionValue);
        const identity = mapReactionIdentity(item);
        const user = object(item?.user);
        const userId = number(item?.user_id ?? user?.id ?? user?.user_id);
        if (!identity || userId === undefined) {
            continue;
        }
        const key = reactionKey(identity);
        const existing = grouped.get(key) ?? { identity, userIds: new Set<number>() };
        existing.userIds.add(userId);
        grouped.set(key, existing);
    }
    return [...grouped.values()].map(({ identity, userIds }) => ({
        ...identity,
        userIds: [...userIds].sort((left, right) => left - right),
    }));
}

function reactionKey(reaction: EmojiReactionIdentity): string {
    return `${reaction.reactionType}:${reaction.emojiCode}`;
}

export function mapUser(value: unknown): UserProfile | undefined {
    const item = object(value);
    const userId = number(item?.user_id ?? item?.id);
    const fullName = string(item?.full_name);
    if (!item || !userId || !fullName) {
        return undefined;
    }
    return {
        userId,
        fullName,
        email: string(item.email ?? item.new_email),
        isActive: boolean(item.is_active) ?? true,
        avatarUrl: string(item.avatar_url),
        avatarVersion: number(item.avatar_version),
        isBot: boolean(item.is_bot),
    };
}

function mapSubscription(value: unknown): Subscription | undefined {
    const item = object(value);
    const channelId = number(item?.stream_id ?? item?.channel_id);
    const name = string(item?.name ?? item?.stream_name);
    if (!item || !channelId || !name?.trim()) {
        return undefined;
    }
    return {
        channelId,
        name,
        isActive: !(boolean(item.is_archived) ?? false),
    };
}

export function mapMessage(value: unknown, currentUserId: number, envelope?: unknown): ChatMessage | undefined {
    const item = object(value);
    const event = object(envelope);
    const id = number(item?.id);
    const senderId = number(item?.sender_id);
    const messageType = string(item?.type)?.toLocaleLowerCase();
    if (!item || id === undefined || senderId === undefined || !messageType) {
        return undefined;
    }

    let conversation;
    if (messageType === 'stream' || messageType === 'channel') {
        const channelId = number(item.stream_id);
        const topic = string(item.subject ?? item.topic);
        if (!channelId || topic === undefined) {
            return undefined;
        }
        conversation = channelTopic(channelId, topic);
    } else if (messageType === 'private' || messageType === 'direct') {
        const rawRecipients = array(item.display_recipient);
        const recipients = rawRecipients
            .map((recipient) => {
                const person = object(recipient);
                return number(person?.id ?? person?.user_id);
            })
            .filter((userId): userId is number => userId !== undefined);
        if (rawRecipients.length === 0 || recipients.length !== rawRecipients.length) {
            return undefined;
        }
        conversation = directMessage(recipients, currentUserId);
    } else {
        return undefined;
    }

    const itemFlags = stringArray(item.flags);
    const flags = itemFlags.length > 0 ? itemFlags : stringArray(event?.flags);
    return {
        id,
        conversation,
        senderId,
        senderDisplayName: string(item.sender_full_name),
        senderAvatarUrl: string(item.avatar_url ?? item.sender_avatar_url),
        content: string(item.content) ?? '',
        timestamp: number(item.timestamp) ?? 0,
        // Zulip clients with a custom client name are not guaranteed to receive a
        // `read` flag on their own message echo. A message authored by the active
        // user must never create an unread badge in this client.
        isRead: senderId === currentUserId
            || flags.some((flag) => flag.toLocaleLowerCase() === 'read'),
        isStarred: flags.some((flag) => flag.toLocaleLowerCase() === 'starred'),
        reactions: mapReactions(item.reactions),
    };
}

function mapUnread(value: unknown, currentUserId: number): UnreadState {
    const unread = object(value);
    const counts: Record<string, number> = {};
    for (const value of array(unread?.streams)) {
        const stream = object(value);
        const channelId = number(stream?.stream_id);
        const topic = string(stream?.topic);
        if (channelId && topic !== undefined) {
            counts[channelTopic(channelId, topic).canonicalKey] = array(stream?.unread_message_ids).length;
        }
    }
    for (const value of array(unread?.pms)) {
        const direct = object(value);
        const otherUserId = number(direct?.other_user_id ?? direct?.sender_id);
        if (otherUserId) {
            counts[directMessage([otherUserId], currentUserId).canonicalKey] = array(direct?.unread_message_ids).length;
        }
    }
    for (const value of array(unread?.huddles)) {
        const group = object(value);
        const ids = (string(group?.user_ids_string) ?? '')
            .split(',')
            .map((part) => Number(part))
            .filter((id) => Number.isSafeInteger(id) && id > 0);
        counts[directMessage(ids, currentUserId).canonicalKey] = array(group?.unread_message_ids).length;
    }
    return {
        counts,
        reportedTotal: number(unread?.count),
        isTruncated: boolean(unread?.old_unreads_missing) ?? false,
    };
}

export function mapRegister(value: unknown, currentUserId: number): RegisterSnapshot {
    const item = object(value);
    const queueId = string(item?.queue_id);
    const lastEventId = number(item?.last_event_id);
    const timeoutSeconds = number(item?.event_queue_longpoll_timeout_seconds);
    const maxMessageLength = number(item?.max_message_length);
    const maxTopicLength = number(item?.max_topic_length);
    if (!item || !queueId || lastEventId === undefined || !timeoutSeconds || !maxMessageLength || !maxTopicLength) {
        throw new ZulipWebError('invalid_response');
    }
    const recentDirectMessages = array(item.recent_private_conversations).map((value) => {
        const recent = object(value);
        return directMessage(numberArray(recent?.user_ids), currentUserId);
    });
    return {
        queueId,
        lastEventId,
        longPollTimeoutMs: timeoutSeconds * 1_000,
        maxMessageLength,
        maxTopicLength,
        maxFileUploadSizeMiB: number(item.max_file_upload_size_mib),
        subscriptions: array(item.subscriptions)
            .map(mapSubscription)
            .filter((subscription): subscription is Subscription => subscription !== undefined),
        users: array(item.realm_users)
            .map(mapUser)
            .filter((user): user is UserProfile => user !== undefined),
        recentDirectMessages,
        unread: mapUnread(item.unread_msgs, currentUserId),
    };
}

export function mapHistory(value: unknown, currentUserId: number): HistoryResult {
    const item = object(value);
    if (!item) {
        throw new ZulipWebError('invalid_response');
    }
    return {
        messages: array(item.messages)
            .map((message) => mapMessage(message, currentUserId))
            .filter((message): message is ChatMessage => message !== undefined),
        foundOldest: boolean(item.found_oldest) ?? false,
        foundNewest: boolean(item.found_newest) ?? false,
    };
}

export function mapTopics(value: unknown, channelId: number): readonly TopicSummary[] {
    const item = object(value);
    if (!item) {
        throw new ZulipWebError('invalid_response');
    }
    return array(item.topics).map((value) => {
        const topic = object(value);
        const name = string(topic?.name);
        if (name === undefined) {
            throw new ZulipWebError('invalid_response');
        }
        return {
            channelId,
            topic: name,
            maxMessageId: number(topic?.max_id),
        };
    });
}

function mapMessageEvent(event: JsonObject, currentUserId: number): readonly EventPatch[] {
    const message = mapMessage(event.message, currentUserId, event);
    return message
        ? [{ type: 'messageUpsert', message, localId: string(event.local_message_id ?? event.local_id) }]
        : [{ type: 'ignored' }];
}

function mapUpdateMessageEvent(event: JsonObject): readonly EventPatch[] {
    const patches: EventPatch[] = [];
    const messageId = number(event.message_id);
    if ((boolean(event.rendering_only) ?? false) === false && messageId !== undefined) {
        const content = string(event.content);
        if (content !== undefined) {
            patches.push({ type: 'messageContent', messageId, content });
        }
    }
    if (messageId !== undefined && Array.isArray(event.flags)) {
        const flags = stringArray(event.flags).map((flag) => flag.toLocaleLowerCase());
        patches.push({
            type: 'messageFlags',
            messageIds: [messageId],
            all: false,
            read: flags.includes('read'),
        });
        patches.push({
            type: 'messageStarred',
            messageIds: [messageId],
            all: false,
            starred: flags.includes('starred'),
        });
    }
    if (event.subject !== undefined || event.new_stream_id !== undefined) {
        const messageIds = numberArray(event.message_ids).length > 0
            ? numberArray(event.message_ids)
            : messageId === undefined ? [] : [messageId];
        const channelId = number(event.new_stream_id ?? event.stream_id);
        const topic = string(event.subject ?? event.orig_subject);
        if (messageIds.length > 0 && channelId && topic !== undefined) {
            patches.push({ type: 'messageMoved', messageIds, destination: channelTopic(channelId, topic) });
        }
    }
    return patches.length > 0 ? patches : [{ type: 'ignored' }];
}

function mapReactionEvent(event: JsonObject): readonly EventPatch[] {
    const messageId = number(event.message_id);
    const userId = number(event.user_id);
    const operation = string(event.op)?.toLocaleLowerCase();
    const reaction = mapReactionIdentity(event);
    return messageId !== undefined
        && userId !== undefined
        && (operation === 'add' || operation === 'remove')
        && reaction
        ? [{ type: 'reactionChanged', messageId, userId, operation, reaction }]
        : [{ type: 'ignored' }];
}

function mapSubscriptionEvent(event: JsonObject): readonly EventPatch[] {
    const operation = string(event.op)?.toLocaleLowerCase();
    if (operation === 'add' || operation === 'remove') {
        const subscriptions = array(event.subscriptions)
            .map(mapSubscription)
            .filter((subscription): subscription is Subscription => subscription !== undefined);
        return subscriptions.map((subscription) => operation === 'remove'
            ? { type: 'subscriptionRemoved', channelId: subscription.channelId }
            : { type: 'subscriptionUpsert', subscription });
    }
    return [{ type: 'ignored' }];
}

function mapStreamEvent(event: JsonObject): readonly EventPatch[] {
    const operation = string(event.op)?.toLocaleLowerCase();
    if (operation === 'update') {
        const channelId = number(event.stream_id);
        const property = string(event.property)?.toLocaleLowerCase();
        if (channelId && property === 'name') {
            return [{ type: 'subscriptionPatched', channelId, name: string(event.value ?? event.name) }];
        }
        if (channelId && property === 'is_archived' && typeof event.value === 'boolean') {
            return [{ type: 'subscriptionPatched', channelId, isActive: !event.value }];
        }
    }
    if (operation === 'delete') {
        const ids = numberArray(event.stream_ids);
        if (ids.length > 0) {
            return ids.map((channelId) => ({ type: 'subscriptionRemoved', channelId }));
        }
        return array(event.streams).map((value) => {
            const stream = object(value);
            return number(stream?.stream_id ?? stream?.id);
        }).filter((id): id is number => id !== undefined)
            .map((channelId) => ({ type: 'subscriptionRemoved', channelId }));
    }
    return [{ type: 'ignored' }];
}

function mapEvent(value: unknown, currentUserId: number): EventPatchGroup {
    const event = object(value) ?? {};
    const eventId = number(event.id);
    const eventType = string(event.type)?.toLocaleLowerCase();
    let patches: readonly EventPatch[];
    switch (eventType) {
        case 'message':
            patches = mapMessageEvent(event, currentUserId);
            break;
        case 'reaction':
            patches = mapReactionEvent(event);
            break;
        case 'update_message':
            patches = mapUpdateMessageEvent(event);
            break;
        case 'delete_message': {
            const ids = numberArray(event.message_ids).length > 0
                ? numberArray(event.message_ids)
                : number(event.message_id) === undefined ? [] : [number(event.message_id)!];
            patches = ids.length > 0 ? [{ type: 'messageDeleted', messageIds: ids }] : [{ type: 'ignored' }];
            break;
        }
        case 'update_message_flags': {
            const flag = string(event.flag)?.toLocaleLowerCase();
            const active = string(event.op ?? event.operation)?.toLocaleLowerCase() === 'add';
            patches = flag === 'read'
                ? [{
                    type: 'messageFlags',
                    messageIds: numberArray(event.messages),
                    all: boolean(event.all) ?? false,
                    read: active,
                }]
                : flag === 'starred' ? [{
                    type: 'messageStarred',
                    messageIds: numberArray(event.messages),
                    all: boolean(event.all) ?? false,
                    starred: active,
                }]
                : [{ type: 'ignored' }];
            break;
        }
        case 'realm_user': {
            const person = object(event.person);
            const user = mapUser(person);
            const userId = number(person?.user_id ?? person?.id);
            patches = string(event.op)?.toLocaleLowerCase() === 'add' && user
                ? [{ type: 'userUpsert', user }]
                : userId ? [{
                    type: 'userPatched',
                    userId,
                    fullName: string(person?.full_name),
                    email: string(person?.new_email ?? person?.email),
                    isActive: boolean(person?.is_active),
                    avatarUrl: person && Object.hasOwn(person, 'avatar_url')
                        ? string(person.avatar_url) ?? null
                        : undefined,
                    avatarVersion: number(person?.avatar_version),
                    isBot: boolean(person?.is_bot),
                }] : [{ type: 'ignored' }];
            break;
        }
        case 'subscription':
            patches = mapSubscriptionEvent(event);
            break;
        case 'stream':
            patches = mapStreamEvent(event);
            break;
        case 'restart':
            patches = [{ type: 'restart' }];
            break;
        default:
            patches = [{ type: 'ignored' }];
            break;
    }
    return { eventId, patches };
}

export function mapEvents(value: unknown, currentUserId: number, currentCursor: number): EventBatch {
    const item = object(value);
    if (!item) {
        throw new ZulipWebError('invalid_response');
    }
    const groups = array(item.events).map((event) => mapEvent(event, currentUserId));
    const lastEventId = groups.reduce(
        (cursor, group) => group.eventId !== undefined ? Math.max(cursor, group.eventId) : cursor,
        currentCursor,
    );
    return { groups, lastEventId };
}
