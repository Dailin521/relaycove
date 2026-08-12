import type {
    ChannelTopicConversation,
    ConversationKey,
    DirectMessageConversation,
} from './types';

function encodeTopic(topic: string): string {
    const bytes = new TextEncoder().encode(topic);
    let binary = '';
    for (const byte of bytes) {
        binary += String.fromCharCode(byte);
    }
    return btoa(binary).replace(/=+$/u, '').replace(/\+/gu, '-').replace(/\//gu, '_');
}

export function channelTopic(channelId: number, topic: string): ChannelTopicConversation {
    if (!Number.isSafeInteger(channelId) || channelId <= 0) {
        throw new TypeError('Invalid channel identity.');
    }
    return {
        kind: 'channel',
        channelId,
        topic,
        canonicalKey: `channel:${channelId}:${encodeTopic(topic)}`,
    };
}

export function directMessage(
    userIds: readonly number[],
    currentUserId?: number,
): DirectMessageConversation {
    if (userIds.some((id) => !Number.isSafeInteger(id) || id <= 0)) {
        throw new TypeError('Invalid direct-message identity.');
    }
    const normalized = [...new Set(userIds)]
        .filter((id) => id !== currentUserId)
        .sort((left, right) => left - right);
    return {
        kind: 'dm',
        otherUserIds: normalized,
        canonicalKey: normalized.length === 0 ? 'dm:self' : `dm:${normalized.join(',')}`,
    };
}

export function sameConversation(left: ConversationKey, right: ConversationKey): boolean {
    return left.canonicalKey === right.canonicalKey;
}

export function conversationNarrow(conversation: ConversationKey, currentUserId: number) {
    if (conversation.kind === 'channel') {
        return [
            { operator: 'channel', operand: conversation.channelId },
            { operator: 'topic', operand: conversation.topic },
        ];
    }
    return [{
        operator: 'dm',
        operand: conversation.otherUserIds.length === 0
            ? [currentUserId]
            : conversation.otherUserIds,
    }];
}
