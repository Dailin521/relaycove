export type ConnectionStatus =
    | 'signedOut'
    | 'bootstrapping'
    | 'connected'
    | 'offline'
    | 'reconnecting'
    | 'rateLimited'
    | 'reauthRequired'
    | 'faulted';

export interface ChannelTopicConversation {
    kind: 'channel';
    channelId: number;
    topic: string;
    canonicalKey: string;
}

export interface DirectMessageConversation {
    kind: 'dm';
    otherUserIds: readonly number[];
    canonicalKey: string;
}

export type ConversationKey = ChannelTopicConversation | DirectMessageConversation;

export interface UserProfile {
    userId: number;
    fullName: string;
    email?: string;
    isActive: boolean;
    avatarUrl?: string;
    avatarVersion?: number;
    isBot?: boolean;
}

export interface Subscription {
    channelId: number;
    name: string;
    isActive: boolean;
}

export interface TopicSummary {
    channelId: number;
    topic: string;
    maxMessageId?: number;
}

export interface EmojiReactionIdentity {
    emojiName: string;
    emojiCode: string;
    reactionType: string;
}

export interface EmojiReaction extends EmojiReactionIdentity {
    userIds: readonly number[];
}

export interface ChatMessage {
    id: number;
    conversation: ConversationKey;
    senderId: number;
    senderDisplayName?: string;
    senderAvatarUrl?: string;
    content: string;
    timestamp: number;
    isRead: boolean;
    isStarred: boolean;
    reactions: readonly EmojiReaction[];
}

export type MessageMutationKind = 'reaction' | 'edit' | 'delete' | 'star';
export type MessageMutationPhase = 'submitting' | 'uncertain' | 'failed';

interface MessageMutationBase {
    operationId: string;
    messageId: number;
    phase: MessageMutationPhase;
    error?: string;
}

export type MessageMutation =
    | (MessageMutationBase & {
        kind: 'reaction';
        reaction: EmojiReactionIdentity;
        active: boolean;
    })
    | (MessageMutationBase & {
        kind: 'edit';
        content: string;
    })
    | (MessageMutationBase & { kind: 'delete' })
    | (MessageMutationBase & {
        kind: 'star';
        starred: boolean;
    });

export interface UnreadState {
    counts: Readonly<Record<string, number>>;
    reportedTotal?: number;
    isTruncated: boolean;
}

export type OutboxStatus = 'hidden' | 'waiting' | 'waitExpired' | 'failed';

export type OutboxFailure =
    | 'rejected'
    | 'reauthRequired'
    | 'rateLimited'
    | 'networkResultUnknown'
    | 'protocol';

export interface OutboxEntry {
    localId: string;
    conversation: ConversationKey;
    content: string;
    createdAt: number;
    status: OutboxStatus;
    failure?: OutboxFailure;
    messageId?: number;
}

export interface RegisterSnapshot {
    queueId: string;
    lastEventId: number;
    longPollTimeoutMs: number;
    maxMessageLength: number;
    maxTopicLength: number;
    maxFileUploadSizeMiB?: number;
    subscriptions: readonly Subscription[];
    users: readonly UserProfile[];
    recentDirectMessages: readonly DirectMessageConversation[];
    unread: UnreadState;
}

export interface HistoryResult {
    messages: readonly ChatMessage[];
    foundOldest: boolean;
    foundNewest: boolean;
}

export interface ConversationPageState {
    loading: boolean;
    foundOldest: boolean;
    foundNewest: boolean;
    error?: string;
}

export interface WebClientState {
    connection: ConnectionStatus;
    connectionReason?: string;
    currentUser?: UserProfile;
    subscriptions: Readonly<Record<number, Subscription>>;
    users: Readonly<Record<number, UserProfile>>;
    topics: Readonly<Record<string, TopicSummary>>;
    messages: Readonly<Record<number, ChatMessage>>;
    recentDirectMessages: readonly DirectMessageConversation[];
    unread: UnreadState;
    outbox: Readonly<Record<string, OutboxEntry>>;
    messageMutations: Readonly<Record<number, MessageMutation>>;
    pages: Readonly<Record<string, ConversationPageState>>;
    selectedConversation?: ConversationKey;
    maxMessageLength?: number;
    maxTopicLength?: number;
    maxFileUploadSizeMiB?: number;
    bootstrapError?: string;
}

export interface EventPatchGroup {
    eventId?: number;
    patches: readonly EventPatch[];
}

export type EventPatch =
    | { type: 'messageUpsert'; message: ChatMessage; localId?: string }
    | { type: 'messageContent'; messageId: number; content: string }
    | { type: 'messageDeleted'; messageIds: readonly number[] }
    | { type: 'messageMoved'; messageIds: readonly number[]; destination: ChannelTopicConversation }
    | { type: 'messageFlags'; messageIds: readonly number[]; all: boolean; read: boolean }
    | { type: 'messageStarred'; messageIds: readonly number[]; all: boolean; starred: boolean }
    | {
        type: 'reactionChanged';
        messageId: number;
        operation: 'add' | 'remove';
        userId: number;
        reaction: EmojiReactionIdentity;
    }
    | { type: 'subscriptionUpsert'; subscription: Subscription }
    | { type: 'subscriptionRemoved'; channelId: number }
    | { type: 'subscriptionPatched'; channelId: number; name?: string; isActive?: boolean }
    | { type: 'userUpsert'; user: UserProfile }
    | {
        type: 'userPatched';
        userId: number;
        fullName?: string;
        email?: string;
        isActive?: boolean;
        avatarUrl?: string | null;
        avatarVersion?: number;
        isBot?: boolean;
    }
    | { type: 'restart' }
    | { type: 'ignored' };

export interface EventBatch {
    groups: readonly EventPatchGroup[];
    lastEventId: number;
}

export interface SendResult {
    localId: string;
    messageId: number;
}

export interface UploadedFile {
    url: string;
    filename: string;
}
