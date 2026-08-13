import { describe, expect, it } from 'vitest';
import { channelTopic, directMessage } from '../domain/conversation';
import type { ChatMessage, RegisterSnapshot, UserProfile } from '../domain/types';
import { initialWebClientState, webClientReducer } from './webClientReducer';

const currentUser: UserProfile = {
    userId: 7,
    fullName: 'Ada Lovelace',
    email: 'ada@example.test',
    isActive: true,
};

const snapshot: RegisterSnapshot = {
    queueId: 'queue-test',
    lastEventId: 1,
    longPollTimeoutMs: 90_000,
    maxMessageLength: 10_000,
    maxTopicLength: 60,
    subscriptions: [{ channelId: 11, name: 'engineering', isActive: true }],
    users: [currentUser, { userId: 9, fullName: 'Grace Hopper', isActive: true }],
    recentDirectMessages: [directMessage([9])],
    unread: { counts: { [channelTopic(11, 'Web').canonicalKey]: 1 }, reportedTotal: 1, isTruncated: false },
};

function message(id: number, isRead: boolean): ChatMessage {
    return {
        id,
        conversation: channelTopic(11, 'Web'),
        senderId: 9,
        senderDisplayName: 'Grace Hopper',
        content: `message ${id}`,
        timestamp: 1_786_000_000,
        isRead,
        isStarred: false,
        reactions: [],
    };
}

describe('webClientReducer', () => {
    it('applies register as one authoritative snapshot and removes revoked channel data', () => {
        const staleConversation = channelTopic(99, 'revoked');
        const staleMessage = { ...message(99, false), conversation: staleConversation };
        const staleState = {
            ...initialWebClientState,
            messages: { 99: staleMessage },
            topics: { [staleConversation.canonicalKey]: { channelId: 99, topic: 'revoked' } },
            selectedConversation: staleConversation,
        };

        const result = webClientReducer(staleState, { type: 'registerApplied', snapshot, currentUser });

        expect(result.subscriptions[11]?.name).toBe('engineering');
        expect(result.messages[99]).toBeUndefined();
        expect(result.topics[staleConversation.canonicalKey]).toBeUndefined();
        expect(result.selectedConversation).toBeUndefined();
    });

    it('applies realtime unread, read confirmation, deletion, and local echo reconciliation', () => {
        let state = webClientReducer(initialWebClientState, { type: 'registerApplied', snapshot, currentUser });
        state = webClientReducer(state, {
            type: 'outboxQueued',
            entry: {
                localId: 'local-test',
                conversation: channelTopic(11, 'Web'),
                content: 'hello',
                createdAt: 1,
                status: 'hidden',
            },
        });
        state = webClientReducer(state, {
            type: 'eventsApplied',
            groups: [{ eventId: 2, patches: [{ type: 'messageUpsert', message: message(101, false), localId: 'local-test' }] }],
        });

        expect(state.outbox['local-test']).toBeUndefined();
        expect(state.unread.counts[channelTopic(11, 'Web').canonicalKey]).toBe(2);
        expect(state.unread.reportedTotal).toBe(2);

        state = webClientReducer(state, {
            type: 'readConfirmed',
            conversation: channelTopic(11, 'Web'),
            messageIds: [101],
        });
        expect(state.messages[101].isRead).toBe(true);
        expect(state.unread.counts[channelTopic(11, 'Web').canonicalKey]).toBe(1);
        expect(state.unread.reportedTotal).toBe(1);

        state = webClientReducer(state, {
            type: 'eventsApplied',
            groups: [{ eventId: 3, patches: [{ type: 'messageDeleted', messageIds: [101] }] }],
        });
        expect(state.messages[101]).toBeUndefined();
        expect(state.unread.counts[channelTopic(11, 'Web').canonicalKey]).toBe(1);
        expect(state.unread.reportedTotal).toBe(1);
    });

    it('reconciles an own local echo without creating an unread count', () => {
        const zeroUnreadSnapshot = {
            ...snapshot,
            unread: { counts: {}, reportedTotal: 0, isTruncated: false },
        };
        let state = webClientReducer(initialWebClientState, {
            type: 'registerApplied',
            snapshot: zeroUnreadSnapshot,
            currentUser,
        });
        state = webClientReducer(state, {
            type: 'outboxQueued',
            entry: {
                localId: 'local-own-message',
                conversation: channelTopic(11, 'Web'),
                content: 'hello',
                createdAt: 1,
                status: 'hidden',
            },
        });
        state = webClientReducer(state, {
            type: 'eventsApplied',
            groups: [{
                eventId: 2,
                patches: [{
                    type: 'messageUpsert',
                    localId: 'local-own-message',
                    message: { ...message(102, false), senderId: currentUser.userId },
                }],
            }],
        });

        expect(state.outbox['local-own-message']).toBeUndefined();
        expect(state.messages[102]).toMatchObject({ senderId: currentUser.userId, isRead: true });
        expect(state.unread.counts[channelTopic(11, 'Web').canonicalKey]).toBeUndefined();
        expect(state.unread.reportedTotal).toBe(0);
    });

    it('removes navigation, messages, topics, unread, and selection on subscription revoke', () => {
        const conversation = channelTopic(11, 'Web');
        let state = webClientReducer(initialWebClientState, { type: 'registerApplied', snapshot, currentUser });
        state = webClientReducer(state, { type: 'topicsLoaded', topics: [{ channelId: 11, topic: 'Web' }] });
        state = webClientReducer(state, { type: 'conversationSelected', conversation });
        state = webClientReducer(state, {
            type: 'historyLoaded',
            conversation,
            history: { messages: [message(101, false)], foundOldest: true, foundNewest: true },
            prepend: false,
        });
        state = webClientReducer(state, {
            type: 'eventsApplied',
            groups: [{ eventId: 2, patches: [{ type: 'subscriptionRemoved', channelId: 11 }] }],
        });

        expect(state.subscriptions[11]).toBeUndefined();
        expect(state.messages[101]).toBeUndefined();
        expect(state.topics[conversation.canonicalKey]).toBeUndefined();
        expect(state.unread.counts[conversation.canonicalKey]).toBeUndefined();
        expect(state.unread.reportedTotal).toBe(0);
        expect(state.selectedConversation).toBeUndefined();
    });

    it('rejects a late topic response after the channel was revoked', () => {
        let state = webClientReducer(initialWebClientState, { type: 'registerApplied', snapshot, currentUser });
        state = webClientReducer(state, {
            type: 'eventsApplied',
            groups: [{ eventId: 2, patches: [{ type: 'subscriptionRemoved', channelId: 11 }] }],
        });
        state = webClientReducer(state, {
            type: 'topicsLoaded',
            topics: [{ channelId: 11, topic: 'late response', maxMessageId: 501 }],
        });

        expect(state.topics).toEqual({});
        expect(state.subscriptions[11]).toBeUndefined();
    });

    it('clears authoritative and loaded unread state for an all-read flag event', () => {
        let state = webClientReducer(initialWebClientState, { type: 'registerApplied', snapshot, currentUser });
        state = webClientReducer(state, {
            type: 'historyLoaded',
            conversation: channelTopic(11, 'Web'),
            history: { messages: [message(101, false)], foundOldest: true, foundNewest: true },
            prepend: false,
        });
        state = webClientReducer(state, {
            type: 'eventsApplied',
            groups: [{ eventId: 2, patches: [{ type: 'messageFlags', messageIds: [], all: true, read: true }] }],
        });

        expect(state.messages[101].isRead).toBe(true);
        expect(state.unread).toEqual({ counts: {}, reportedTotal: 0, isTruncated: false });
    });

    it('projects reaction, edit, star, and delete confirmations and settles their message mutations', () => {
        const conversation = channelTopic(11, 'Web');
        const reaction = { emojiName: '+1', emojiCode: '1f44d', reactionType: 'unicode_emoji' };
        let state = webClientReducer(initialWebClientState, { type: 'registerApplied', snapshot, currentUser });
        state = webClientReducer(state, {
            type: 'historyLoaded',
            conversation,
            history: { messages: [message(101, true)], foundOldest: true, foundNewest: true },
            prepend: false,
        });
        state = webClientReducer(state, {
            type: 'messageMutationStarted',
            mutation: {
                operationId: 'reaction-1',
                messageId: 101,
                kind: 'reaction',
                phase: 'submitting',
                reaction,
                active: true,
            },
        });
        state = webClientReducer(state, {
            type: 'eventsApplied',
            groups: [{ patches: [{ type: 'reactionChanged', messageId: 101, operation: 'add', userId: 7, reaction }] }],
        });
        expect(state.messages[101].reactions).toEqual([{ ...reaction, userIds: [7] }]);
        expect(state.messageMutations[101]).toBeUndefined();

        state = webClientReducer(state, {
            type: 'messageMutationStarted',
            mutation: { operationId: 'star-1', messageId: 101, kind: 'star', phase: 'submitting', starred: true },
        });
        state = webClientReducer(state, {
            type: 'eventsApplied',
            groups: [{ patches: [{ type: 'messageStarred', messageIds: [101], all: false, starred: true }] }],
        });
        expect(state.messages[101].isStarred).toBe(true);
        expect(state.messageMutations[101]).toBeUndefined();

        state = webClientReducer(state, {
            type: 'messageMutationStarted',
            mutation: { operationId: 'edit-1', messageId: 101, kind: 'edit', phase: 'submitting', content: 'edited' },
        });
        state = webClientReducer(state, {
            type: 'eventsApplied',
            groups: [{ patches: [{ type: 'messageContent', messageId: 101, content: 'edited' }] }],
        });
        expect(state.messages[101].content).toBe('edited');
        expect(state.messageMutations[101]).toBeUndefined();

        state = webClientReducer(state, {
            type: 'messageMutationStarted',
            mutation: { operationId: 'delete-1', messageId: 101, kind: 'delete', phase: 'submitting' },
        });
        state = webClientReducer(state, {
            type: 'eventsApplied',
            groups: [{ patches: [{ type: 'messageDeleted', messageIds: [101] }] }],
        });
        expect(state.messages[101]).toBeUndefined();
        expect(state.messageMutations[101]).toBeUndefined();
    });
});
