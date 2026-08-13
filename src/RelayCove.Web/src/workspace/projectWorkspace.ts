import type { WebSession } from '../api/types';
import { resolveRealmMediaUrl } from '../api/realmMedia';
import { directMessage } from '../domain/conversation';
import type { ConversationKey, DirectMessageConversation, WebClientState } from '../domain/types';
import type {
    ConversationDetail,
    ConversationSummary,
    PersonSummary,
    ShellPresentation,
    WorkspaceViewState,
} from '../models/ui';
import { messagesForConversation } from '../state/webClientReducer';
import { presentMessageContent } from './messagePresentation';

const tones: readonly PersonSummary['tone'][] = ['blue', 'green', 'orange', 'violet', 'slate'];

export interface ProjectedWebClient {
    workspace: WorkspaceViewState;
    presentation: ShellPresentation;
}

export function projectWebClient(session: WebSession, state: WebClientState): ProjectedWebClient {
    const channelConversations = projectChannels(state, session.realm);
    const directConversations = projectDirects(state, session.realm, session.userId);
    const all = [...channelConversations, ...directConversations];
    const conversations = Object.fromEntries(all.map((conversation) => [conversation.id, conversation]));
    const currentUser = state.currentUser ?? {
        userId: session.userId,
        fullName: session.fullName,
        email: session.email,
        isActive: true,
    };
    return {
        workspace: {
            workspaceName: new URL(session.realm).host,
            currentUser: person(
                currentUser.userId,
                currentUser.fullName,
                session.realm,
                currentUser.avatarUrl,
                currentUser.isBot,
                currentUser.avatarVersion,
            ),
            channels: channelConversations,
            directs: directConversations,
            contacts: Object.values(state.users)
                .filter((user) => user.isActive && user.userId !== session.userId)
                .sort((left, right) => left.fullName.localeCompare(right.fullName, 'zh-CN'))
                .map((user) => person(user.userId, user.fullName, session.realm, user.avatarUrl, user.isBot, user.avatarVersion)),
            subscribedChannels: Object.values(state.subscriptions)
                .filter((subscription) => subscription.isActive)
                .sort((left, right) => left.name.localeCompare(right.name, 'zh-CN'))
                .map((subscription) => ({ channelId: subscription.channelId, name: subscription.name })),
            conversations,
            selectedConversationId: state.selectedConversation?.canonicalKey,
            totalUnread: state.unread.reportedTotal,
        },
        presentation: {
            conversationSearchEnabled: true,
            emptySearchText: '没有匹配的会话',
            composerStatusText: composerStatus(state),
            connectionNotice: connectionNotice(state),
            sendEnabled: state.connection === 'connected' && !Object.values(state.outbox).some((entry) => (
                entry.conversation.canonicalKey === state.selectedConversation?.canonicalKey
                && (entry.status === 'hidden' || entry.status === 'waiting')
            )),
            conversationSearchTitle: '仅筛选当前已加载的会话列表',
            maxAttachmentUploadBytes: (state.maxFileUploadSizeMiB ?? 10) * 1024 * 1024,
            maxMessageLength: state.maxMessageLength,
        },
    };
}

function projectChannels(state: WebClientState, realm: string): ConversationDetail[] {
    const topics = new Map(Object.values(state.topics)
        .filter((topic) => state.subscriptions[topic.channelId]?.isActive)
        .map((topic) => {
            const conversation: ConversationKey = {
                kind: 'channel',
                channelId: topic.channelId,
                topic: topic.topic,
                canonicalKey: topicKey(topic.channelId, topic.topic),
            };
            return [conversation.canonicalKey, conversation] as const;
        }));
    for (const message of Object.values(state.messages)) {
        if (message.conversation.kind === 'channel' && state.subscriptions[message.conversation.channelId]?.isActive) {
            topics.set(message.conversation.canonicalKey, message.conversation);
        }
    }
    return [...topics.values()]
        .map((conversation) => projectConversation(state, conversation, realm))
        .sort(compareConversations);
}

function projectDirects(state: WebClientState, realm: string, currentUserId: number): ConversationDetail[] {
    const directs = new Map<string, DirectMessageConversation>();
    const self = directMessage([currentUserId], currentUserId);
    directs.set(self.canonicalKey, self);
    for (const direct of state.recentDirectMessages) {
        directs.set(direct.canonicalKey, direct);
    }
    for (const message of Object.values(state.messages)) {
        if (message.conversation.kind === 'dm') {
            directs.set(message.conversation.canonicalKey, message.conversation);
        }
    }
    return [...directs.values()].map((conversation) => projectConversation(state, conversation, realm)).sort(compareConversations);
}

function projectConversation(state: WebClientState, conversation: ConversationKey, realm: string): ConversationDetail {
    const messages = messagesForConversation(state, conversation);
    const latest = messages.at(-1);
    const page = state.pages[conversation.canonicalKey];
    const title = conversation.kind === 'channel'
        ? conversation.topic || '(无话题)'
        : directTitle(state, conversation);
    const channelName = conversation.kind === 'channel'
        ? state.subscriptions[conversation.channelId]?.name ?? `频道 ${conversation.channelId}`
        : undefined;
    const participantIds = conversation.kind === 'dm'
        ? conversation.otherUserIds.length > 0 ? conversation.otherUserIds : state.currentUser ? [state.currentUser.userId] : []
        : [];
    const avatarText = conversation.kind === 'channel'
        ? '#'
        : initials(participantIds.map((id) => state.users[id]?.fullName).filter((name): name is string => Boolean(name)).join('、') || title);
    const kind = conversation.kind === 'channel'
        ? 'channel'
        : conversation.otherUserIds.length === 0 ? 'self'
            : conversation.otherUserIds.length === 1 ? 'direct' : 'group';
    const avatarOwner = kind === 'direct'
        ? state.users[participantIds[0]!]
        : kind === 'self' ? state.currentUser : undefined;
    const pendingMessages = Object.values(state.outbox)
        .filter((entry) => entry.conversation.canonicalKey === conversation.canonicalKey)
        .sort((left, right) => left.createdAt - right.createdAt)
        .map((entry) => ({
            localId: entry.localId,
            body: entry.content,
            status: entry.status,
            statusText: outboxStatus(entry.status, entry.failure),
            recoverable: entry.status === 'failed' || entry.status === 'waitExpired',
        }));
    return {
        id: conversation.canonicalKey,
        kind,
        title,
        subtitle: latest
            ? `${latest.senderDisplayName ?? state.users[latest.senderId]?.fullName ?? '成员'}：${preview(latest.content)}`
            : conversation.kind === 'channel' ? `# ${channelName}` : '尚未加载消息',
        time: latest ? formatRelativeTime(latest.timestamp) : '',
        unread: state.unread.counts[conversation.canonicalKey] ?? 0,
        channelName,
        channelId: conversation.kind === 'channel' ? conversation.channelId : undefined,
        topic: conversation.kind === 'channel' ? conversation.topic : undefined,
        avatar: avatarText,
        tone: toneFor(conversation.canonicalKey),
        avatarUrl: (avatarOwner || participantIds.length === 1)
            ? resolveRealmMediaUrl(
                realm,
                avatarOwner?.avatarUrl ?? avatarFallback(avatarOwner?.userId ?? participantIds[0]!, avatarOwner?.avatarVersion),
                'avatar',
            )
            : undefined,
        isBot: avatarOwner?.isBot,
        messages: messages.map((message) => {
            const sender = state.users[message.senderId];
            const content = presentMessageContent(message.content, realm);
            return {
                id: String(message.id),
                sender: person(
                    message.senderId,
                    message.senderDisplayName ?? sender?.fullName ?? `用户 ${message.senderId}`,
                    realm,
                    sender?.avatarUrl ?? message.senderAvatarUrl,
                    sender?.isBot,
                    sender?.avatarVersion,
                ),
                sentAt: formatMessageTime(message.timestamp),
                body: content.body,
                rawContent: message.content,
                attachments: content.attachments,
                quote: content.quote,
                permalink: `${realm}/#narrow/near/${message.id}`,
                own: message.senderId === state.currentUser?.userId,
                isStarred: message.isStarred,
                reactions: message.reactions.map((reaction) => ({
                    emoji: reactionEmoji(reaction.emojiName, reaction.emojiCode, reaction.reactionType),
                    emojiName: reaction.emojiName,
                    emojiCode: reaction.emojiCode,
                    reactionType: reaction.reactionType,
                    count: reaction.userIds.length,
                    reactedByCurrentUser: state.currentUser !== undefined
                        && reaction.userIds.includes(state.currentUser.userId),
                })),
                mutation: state.messageMutations[message.id] ? {
                    kind: state.messageMutations[message.id]!.kind,
                    phase: state.messageMutations[message.id]!.phase,
                    error: state.messageMutations[message.id]!.error,
                } : undefined,
            };
        }),
        pendingMessages,
        loading: page?.loading,
        foundOldest: page?.foundOldest,
        loadError: page?.error,
    };
}

function reactionEmoji(name: string, code: string, type: string): string {
    if (type !== 'unicode_emoji') {
        return `:${name}:`;
    }
    const points = code.split(/[-_]/u).map((part) => Number.parseInt(part, 16));
    if (points.length === 0 || points.some((point) => !Number.isSafeInteger(point) || point <= 0 || point > 0x10ffff)) {
        return `:${name}:`;
    }
    try {
        return String.fromCodePoint(...points);
    } catch {
        return `:${name}:`;
    }
}

function directTitle(state: WebClientState, conversation: DirectMessageConversation): string {
    if (conversation.otherUserIds.length === 0) {
        return `${state.currentUser?.fullName ?? '自己'}（自己）`;
    }
    const names = conversation.otherUserIds.map((userId) => state.users[userId]?.fullName ?? `用户 ${userId}`);
    return names.join('、');
}

function compareConversations(left: ConversationSummary, right: ConversationSummary): number {
    const leftUnread = left.unread > 0 ? 1 : 0;
    const rightUnread = right.unread > 0 ? 1 : 0;
    return rightUnread - leftUnread || left.title.localeCompare(right.title, 'zh-CN');
}

function person(
    id: number,
    name: string,
    realm: string,
    avatarUrl?: string,
    isBot?: boolean,
    avatarVersion?: number,
): PersonSummary {
    return {
        id: String(id),
        name,
        initials: initials(name),
        tone: toneFor(String(id)),
        avatarUrl: resolveRealmMediaUrl(realm, avatarUrl ?? avatarFallback(id, avatarVersion), 'avatar'),
        isBot,
    };
}

function avatarFallback(userId: number, version?: number): string {
    return version === undefined ? `/avatar/${userId}` : `/avatar/${userId}?v=${version}`;
}

function initials(name: string): string {
    const parts = name.trim().split(/\s+/u).filter(Boolean);
    if (parts.length >= 2) {
        return `${parts[0][0] ?? ''}${parts.at(-1)?.[0] ?? ''}`.toLocaleUpperCase();
    }
    return [...(parts[0] ?? 'R')].slice(0, 2).join('').toLocaleUpperCase();
}

function toneFor(value: string): PersonSummary['tone'] {
    let hash = 0;
    for (const character of value) {
        hash = ((hash * 31) + character.codePointAt(0)!) >>> 0;
    }
    return tones[hash % tones.length];
}

function preview(content: string): string {
    return content.replace(/\s+/gu, ' ').trim().slice(0, 72) || '空消息';
}

function formatMessageTime(seconds: number): string {
    return new Intl.DateTimeFormat('zh-CN', { hour: '2-digit', minute: '2-digit', hour12: false })
        .format(new Date(seconds * 1_000));
}

function formatRelativeTime(seconds: number): string {
    const timestamp = seconds * 1_000;
    const difference = Date.now() - timestamp;
    if (difference >= 0 && difference < 60_000) {
        return '刚刚';
    }
    const date = new Date(timestamp);
    const today = new Date();
    if (date.toDateString() === today.toDateString()) {
        return formatMessageTime(seconds);
    }
    return new Intl.DateTimeFormat('zh-CN', { month: 'numeric', day: 'numeric' }).format(date);
}

function topicKey(channelId: number, topic: string): string {
    const bytes = new TextEncoder().encode(topic);
    let binary = '';
    for (const byte of bytes) {
        binary += String.fromCharCode(byte);
    }
    const encoded = btoa(binary).replace(/=+$/u, '').replace(/\+/gu, '-').replace(/\//gu, '_');
    return `channel:${channelId}:${encoded}`;
}

function connectionNotice(state: WebClientState): string | undefined {
    switch (state.connection) {
        case 'bootstrapping': return '正在连接 Zulip…';
        case 'offline': return '当前离线；已有消息仍可查看，发送已暂停。';
        case 'reconnecting': return '正在重建 Zulip 事件连接…';
        case 'rateLimited': return 'Realm 暂时限制请求，RelayCove 会在允许后继续同步。';
        case 'reauthRequired': return '登录已失效，请重新登录。';
        case 'faulted': return '同步已停止，请刷新后重试。';
        default: return undefined;
    }
}

function composerStatus(state: WebClientState): string {
    if (state.connection !== 'connected') {
        return '连接恢复后才能发送';
    }
    return 'Ctrl + Enter 发送 · Enter 换行';
}

function outboxStatus(status: string, failure?: string): string {
    if (status === 'hidden') return '正在发送';
    if (status === 'waiting') return '等待服务器确认';
    if (status === 'waitExpired') return '尚未确认；再次发送可能重复';
    if (failure === 'networkResultUnknown') return '发送结果未知；再次发送可能重复';
    if (failure === 'rateLimited') return 'Realm 限制了发送';
    if (failure === 'reauthRequired') return '登录已失效';
    return '发送失败';
}
