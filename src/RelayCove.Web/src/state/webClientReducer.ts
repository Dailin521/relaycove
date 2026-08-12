import { channelTopic } from '../domain/conversation';
import type {
    ChatMessage,
    ConnectionStatus,
    ConversationKey,
    EventPatch,
    EventPatchGroup,
    HistoryResult,
    OutboxEntry,
    OutboxFailure,
    RegisterSnapshot,
    TopicSummary,
    UserProfile,
    WebClientState,
} from '../domain/types';

export type WebClientAction =
    | { type: 'connectionChanged'; status: ConnectionStatus; reason?: string }
    | { type: 'registerApplied'; snapshot: RegisterSnapshot; currentUser: UserProfile }
    | { type: 'topicsLoaded'; topics: readonly TopicSummary[] }
    | { type: 'conversationSelected'; conversation: ConversationKey }
    | { type: 'historyLoading'; conversation: ConversationKey }
    | { type: 'historyLoaded'; conversation: ConversationKey; history: HistoryResult; prepend: boolean }
    | { type: 'historyFailed'; conversation: ConversationKey; error: string }
    | { type: 'eventsApplied'; groups: readonly EventPatchGroup[] }
    | { type: 'readConfirmed'; conversation: ConversationKey; messageIds: readonly number[] }
    | { type: 'outboxQueued'; entry: OutboxEntry }
    | { type: 'outboxStatus'; localId: string; status: OutboxEntry['status']; failure?: OutboxFailure; messageId?: number }
    | { type: 'outboxRemoved'; localId: string }
    | { type: 'reset' };

export const initialWebClientState: WebClientState = {
    connection: 'signedOut',
    subscriptions: {},
    users: {},
    topics: {},
    messages: {},
    recentDirectMessages: [],
    unread: { counts: {}, isTruncated: false },
    outbox: {},
    pages: {},
};

export function webClientReducer(state: WebClientState, action: WebClientAction): WebClientState {
    switch (action.type) {
        case 'connectionChanged':
            return { ...state, connection: action.status, connectionReason: action.reason };
        case 'registerApplied': {
            const subscriptions = Object.fromEntries(action.snapshot.subscriptions.map((item) => [item.channelId, item]));
            const users = Object.fromEntries(action.snapshot.users.map((item) => [item.userId, item]));
            users[action.currentUser.userId] = action.currentUser;
            const authorizedChannelIds = new Set(action.snapshot.subscriptions
                .filter((subscription) => subscription.isActive)
                .map((subscription) => subscription.channelId));
            const messages = Object.fromEntries(Object.entries(state.messages).filter(([, message]) => (
                message.conversation.kind === 'dm' || authorizedChannelIds.has(message.conversation.channelId)
            )));
            const topics = Object.fromEntries(Object.entries(state.topics).filter(([, topic]) => (
                authorizedChannelIds.has(topic.channelId)
            )));
            const stateWithSnapshot: WebClientState = {
                ...state,
                connection: 'connected',
                connectionReason: undefined,
                currentUser: action.currentUser,
                subscriptions,
                users,
                messages,
                topics,
                recentDirectMessages: action.snapshot.recentDirectMessages,
                unread: action.snapshot.unread,
                maxMessageLength: action.snapshot.maxMessageLength,
                maxTopicLength: action.snapshot.maxTopicLength,
                maxFileUploadSizeMiB: action.snapshot.maxFileUploadSizeMiB,
                bootstrapError: undefined,
            };
            return sanitizeSelection(stateWithSnapshot);
        }
        case 'topicsLoaded': {
            const topics = { ...state.topics };
            for (const topic of action.topics) {
                if (state.subscriptions[topic.channelId]?.isActive) {
                    topics[channelTopic(topic.channelId, topic.topic).canonicalKey] = topic;
                }
            }
            return { ...state, topics };
        }
        case 'conversationSelected':
            return action.conversation.kind === 'dm'
                ? {
                    ...state,
                    selectedConversation: action.conversation,
                    recentDirectMessages: mergeDirect(state.recentDirectMessages, action.conversation),
                }
                : {
                    ...state,
                    selectedConversation: action.conversation,
                    topics: {
                        ...state.topics,
                        [action.conversation.canonicalKey]: {
                            channelId: action.conversation.channelId,
                            topic: action.conversation.topic,
                        },
                    },
                };
        case 'historyLoading':
            return {
                ...state,
                pages: {
                    ...state.pages,
                    [action.conversation.canonicalKey]: {
                        ...(state.pages[action.conversation.canonicalKey] ?? { foundOldest: false, foundNewest: false }),
                        loading: true,
                        error: undefined,
                    },
                },
            };
        case 'historyLoaded': {
            const messages = { ...state.messages };
            for (const message of action.history.messages) {
                messages[message.id] = message;
            }
            return {
                ...state,
                messages,
                pages: {
                    ...state.pages,
                    [action.conversation.canonicalKey]: {
                        loading: false,
                        foundOldest: action.history.foundOldest,
                        foundNewest: action.history.foundNewest,
                    },
                },
            };
        }
        case 'historyFailed':
            return {
                ...state,
                pages: {
                    ...state.pages,
                    [action.conversation.canonicalKey]: {
                        ...(state.pages[action.conversation.canonicalKey] ?? { foundOldest: false, foundNewest: false }),
                        loading: false,
                        error: action.error,
                    },
                },
            };
        case 'eventsApplied': {
            let current = state;
            for (const group of action.groups) {
                for (const patch of group.patches) {
                    current = applyPatch(current, patch);
                }
            }
            return sanitizeSelection(current);
        }
        case 'readConfirmed': {
            const messages = { ...state.messages };
            let unread = state.unread;
            for (const messageId of action.messageIds) {
                const message = messages[messageId];
                if (message && !message.isRead) {
                    messages[messageId] = { ...message, isRead: true };
                    unread = adjustUnread(unread, message.conversation.canonicalKey, -1);
                }
            }
            return { ...state, messages, unread };
        }
        case 'outboxQueued':
            return { ...state, outbox: { ...state.outbox, [action.entry.localId]: action.entry } };
        case 'outboxStatus': {
            const existing = state.outbox[action.localId];
            if (!existing) {
                return state;
            }
            return {
                ...state,
                outbox: {
                    ...state.outbox,
                    [action.localId]: {
                        ...existing,
                        status: action.status,
                        failure: action.failure,
                        messageId: action.messageId ?? existing.messageId,
                    },
                },
            };
        }
        case 'outboxRemoved': {
            const outbox = { ...state.outbox };
            delete outbox[action.localId];
            return { ...state, outbox };
        }
        case 'reset':
            return initialWebClientState;
    }
}

function applyPatch(state: WebClientState, patch: EventPatch): WebClientState {
    switch (patch.type) {
        case 'messageUpsert': {
            let unread = state.unread;
            const existing = state.messages[patch.message.id];
            if (existing && !existing.isRead) {
                unread = adjustUnread(unread, existing.conversation.canonicalKey, -1);
            }
            if (!patch.message.isRead) {
                unread = adjustUnread(unread, patch.message.conversation.canonicalKey, 1);
            }
            const outbox = { ...state.outbox };
            if (patch.localId) {
                delete outbox[patch.localId];
            }
            const recentDirectMessages = patch.message.conversation.kind === 'dm'
                ? mergeDirect(state.recentDirectMessages, patch.message.conversation)
                : state.recentDirectMessages;
            return {
                ...state,
                unread,
                outbox,
                recentDirectMessages,
                messages: { ...state.messages, [patch.message.id]: patch.message },
            };
        }
        case 'messageContent': {
            const message = state.messages[patch.messageId];
            return message ? {
                ...state,
                messages: { ...state.messages, [patch.messageId]: { ...message, content: patch.content } },
            } : state;
        }
        case 'messageDeleted': {
            const messages = { ...state.messages };
            let unread = state.unread;
            for (const messageId of patch.messageIds) {
                const message = messages[messageId];
                if (message && !message.isRead) {
                    unread = adjustUnread(unread, message.conversation.canonicalKey, -1);
                }
                delete messages[messageId];
            }
            return { ...state, messages, unread };
        }
        case 'messageMoved': {
            const messages = { ...state.messages };
            let unread = state.unread;
            for (const messageId of patch.messageIds) {
                const message = messages[messageId];
                if (!message) {
                    continue;
                }
                if (!message.isRead && message.conversation.canonicalKey !== patch.destination.canonicalKey) {
                    unread = adjustUnread(unread, message.conversation.canonicalKey, -1);
                    unread = adjustUnread(unread, patch.destination.canonicalKey, 1);
                }
                messages[messageId] = { ...message, conversation: patch.destination };
            }
            return { ...state, messages, unread };
        }
        case 'messageFlags': {
            const messages = { ...state.messages };
            let unread = state.unread;
            if (patch.all && patch.read) {
                for (const [messageId, message] of Object.entries(messages)) {
                    if (!message.isRead) {
                        messages[Number(messageId)] = { ...message, isRead: true };
                    }
                }
                return {
                    ...state,
                    messages,
                    unread: { counts: {}, reportedTotal: 0, isTruncated: false },
                };
            }
            const messageIds = patch.all ? Object.keys(messages).map(Number) : patch.messageIds;
            for (const messageId of messageIds) {
                const message = messages[messageId];
                if (!message || message.isRead === patch.read) {
                    continue;
                }
                messages[messageId] = { ...message, isRead: patch.read };
                unread = adjustUnread(unread, message.conversation.canonicalKey, patch.read ? -1 : 1);
            }
            return { ...state, messages, unread };
        }
        case 'subscriptionUpsert':
            return { ...state, subscriptions: { ...state.subscriptions, [patch.subscription.channelId]: patch.subscription } };
        case 'subscriptionPatched': {
            const subscription = state.subscriptions[patch.channelId];
            return subscription ? {
                ...state,
                subscriptions: {
                    ...state.subscriptions,
                    [patch.channelId]: {
                        ...subscription,
                        name: patch.name ?? subscription.name,
                        isActive: patch.isActive ?? subscription.isActive,
                    },
                },
            } : state;
        }
        case 'subscriptionRemoved':
            return removeChannel(state, patch.channelId);
        case 'userUpsert':
            return { ...state, users: { ...state.users, [patch.user.userId]: patch.user } };
        case 'userPatched': {
            const user = state.users[patch.userId];
            return user ? {
                ...state,
                users: {
                    ...state.users,
                    [patch.userId]: {
                        ...user,
                        fullName: patch.fullName ?? user.fullName,
                        email: patch.email ?? user.email,
                        isActive: patch.isActive ?? user.isActive,
                        avatarUrl: patch.avatarUrl === null ? undefined : patch.avatarUrl ?? user.avatarUrl,
                        avatarVersion: patch.avatarVersion ?? user.avatarVersion,
                        isBot: patch.isBot ?? user.isBot,
                    },
                },
            } : state;
        }
        case 'restart':
            return { ...state, connection: 'reconnecting', connectionReason: 'server_restart' };
        case 'ignored':
            return state;
    }
}

function removeChannel(state: WebClientState, channelId: number): WebClientState {
    const subscriptions = { ...state.subscriptions };
    const messages = { ...state.messages };
    const topics = { ...state.topics };
    const counts = { ...state.unread.counts };
    let removedUnread = 0;
    delete subscriptions[channelId];
    for (const [id, message] of Object.entries(messages)) {
        if (message.conversation.kind === 'channel' && message.conversation.channelId === channelId) {
            delete messages[Number(id)];
        }
    }
    for (const [key, topic] of Object.entries(topics)) {
        if (topic.channelId === channelId) {
            delete topics[key];
        }
    }
    for (const key of Object.keys(counts)) {
        if (key.startsWith(`channel:${channelId}:`)) {
            removedUnread += counts[key] ?? 0;
            delete counts[key];
        }
    }
    return {
        ...state,
        subscriptions,
        messages,
        topics,
        unread: {
            ...state.unread,
            counts,
            reportedTotal: state.unread.reportedTotal === undefined
                ? undefined
                : Math.max(0, state.unread.reportedTotal - removedUnread),
        },
    };
}

function sanitizeSelection(state: WebClientState): WebClientState {
    const selected = state.selectedConversation;
    if (!selected || selected.kind !== 'channel') {
        return state;
    }
    return state.subscriptions[selected.channelId]?.isActive
        ? state
        : { ...state, selectedConversation: undefined };
}

function adjustUnread(unread: WebClientState['unread'], key: string, delta: number): WebClientState['unread'] {
    const counts = { ...unread.counts };
    counts[key] = Math.max(0, (counts[key] ?? 0) + delta);
    if (counts[key] === 0) {
        delete counts[key];
    }
    return {
        ...unread,
        counts,
        reportedTotal: unread.reportedTotal === undefined
            ? undefined
            : Math.max(0, unread.reportedTotal + delta),
    };
}

function mergeDirect(
    directs: WebClientState['recentDirectMessages'],
    direct: Extract<ConversationKey, { kind: 'dm' }>,
) {
    return [direct, ...directs.filter((item) => item.canonicalKey !== direct.canonicalKey)];
}

export function messagesForConversation(state: WebClientState, conversation: ConversationKey): readonly ChatMessage[] {
    return Object.values(state.messages)
        .filter((message) => message.conversation.canonicalKey === conversation.canonicalKey)
        .sort((left, right) => left.id - right.id);
}
