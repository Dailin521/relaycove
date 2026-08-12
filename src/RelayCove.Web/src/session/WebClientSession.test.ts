import { waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ZulipApiClient } from '../api/ZulipApiClient';
import type { WebSession } from '../api/types';
import { directMessage } from '../domain/conversation';
import { jitteredBackoff, WebClientSession } from './WebClientSession';

const session: WebSession = {
    realm: 'https://chat.example.test',
    email: 'ada@example.test',
    apiKey: 'api-key-secret',
    userId: 7,
    fullName: 'Ada Lovelace',
    remember: true,
};

function json(value: unknown): Response {
    return new Response(JSON.stringify(value), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
    });
}

describe('WebClientSession', () => {
    it('never retries an ambiguous non-idempotent send and keeps a recoverable outbox entry', async () => {
        let sendAttempts = 0;
        const transport = vi.fn(async (input: RequestInfo | URL): Promise<Response> => {
            const request = input as Request;
            const url = new URL(request.url);
            const path = url.pathname.replace('/api/v1/', '');
            if (path === 'users/me') {
                return json({ user_id: 7, full_name: 'Ada Lovelace', email: 'ada@example.test' });
            }
            if (path === 'register') {
                return json({
                    queue_id: 'queue-test',
                    last_event_id: 1,
                    event_queue_longpoll_timeout_seconds: 60,
                    max_message_length: 10_000,
                    max_topic_length: 60,
                    subscriptions: [],
                    realm_users: [
                        { user_id: 7, full_name: 'Ada Lovelace' },
                        { user_id: 9, full_name: 'Grace Hopper' },
                    ],
                    recent_private_conversations: [],
                    unread_msgs: { count: 0, streams: [], pms: [], huddles: [] },
                });
            }
            if (path === 'events' && request.method === 'GET') {
                return await new Promise<Response>((_resolve, reject) => {
                    request.signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true });
                });
            }
            if (path === 'events' && request.method === 'DELETE') {
                return json({ result: 'success', msg: '' });
            }
            if (path === 'messages' && request.method === 'GET') {
                return json({ messages: [], found_oldest: true, found_newest: true });
            }
            if (path === 'messages' && request.method === 'POST') {
                sendAttempts += 1;
                throw new TypeError('simulated network loss');
            }
            throw new Error(`Unexpected fake endpoint ${request.method} ${path}`);
        });
        const client = new WebClientSession(session, {
            apiClient: new ZulipApiClient(transport),
            createLocalId: () => 'local-test',
        });

        await client.start();
        await waitFor(() => expect(client.store.getSnapshot().connection).toBe('connected'));
        vi.useFakeTimers();
        await expect(client.send(directMessage([9]), 'send once')).rejects.toThrow('不会自动重试');
        expect(sendAttempts).toBe(1);
        expect(client.store.getSnapshot().outbox['local-test']).toMatchObject({ content: 'send once', status: 'hidden' });
        await vi.advanceTimersByTimeAsync(500);
        expect(client.store.getSnapshot().outbox['local-test']?.status).toBe('waiting');
        await vi.advanceTimersByTimeAsync(9_500);
        expect(client.store.getSnapshot().outbox['local-test']?.status).toBe('waitExpired');
        expect(client.recoverOutbox('local-test')).toMatchObject({ content: 'send once' });
        expect(client.store.getSnapshot().outbox['local-test']).toBeUndefined();
        vi.useRealTimers();
        await client.stop(true);
        expect(sendAttempts).toBe(1);
    });

    it('clears the session through the reauthentication callback on an authenticated 401', async () => {
        const onReauthenticationRequired = vi.fn();
        const transport = vi.fn(async () => new Response('{}', {
            status: 401,
            headers: { 'Content-Type': 'application/json' },
        }));
        const client = new WebClientSession(session, {
            apiClient: new ZulipApiClient(transport),
            onReauthenticationRequired,
        });

        await client.start();

        expect(onReauthenticationRequired).toHaveBeenCalledOnce();
        expect(client.store.getSnapshot().connection).toBe('reauthRequired');
        expect(transport).toHaveBeenCalledOnce();
    });

    it('re-registers after BAD_EVENT_QUEUE_ID and never reuses the expired queue', async () => {
        let registerCount = 0;
        const eventQueues: string[] = [];
        const transport = vi.fn(async (input: RequestInfo | URL): Promise<Response> => {
            const request = input as Request;
            const url = new URL(request.url);
            const path = url.pathname.replace('/api/v1/', '');
            if (path === 'users/me') {
                return json({ user_id: 7, full_name: 'Ada Lovelace', email: 'ada@example.test' });
            }
            if (path === 'register') {
                registerCount += 1;
                return json({
                    queue_id: `queue-${registerCount}`,
                    last_event_id: registerCount,
                    event_queue_longpoll_timeout_seconds: 60,
                    max_message_length: 10_000,
                    max_topic_length: 60,
                    subscriptions: [],
                    realm_users: [{ user_id: 7, full_name: 'Ada Lovelace' }],
                    recent_private_conversations: [],
                    unread_msgs: { count: 0, streams: [], pms: [], huddles: [] },
                });
            }
            if (path === 'events' && request.method === 'GET') {
                const queue = url.searchParams.get('queue_id')!;
                eventQueues.push(queue);
                if (queue === 'queue-1') {
                    return new Response(JSON.stringify({ code: 'BAD_EVENT_QUEUE_ID' }), {
                        status: 400,
                        headers: { 'Content-Type': 'application/json' },
                    });
                }
                return await new Promise<Response>((_resolve, reject) => {
                    request.signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true });
                });
            }
            if (path === 'events' && request.method === 'DELETE') {
                return json({ result: 'success', msg: '' });
            }
            throw new Error(`Unexpected fake endpoint ${request.method} ${path}`);
        });
        const client = new WebClientSession(session, { apiClient: new ZulipApiClient(transport) });

        await client.start();
        await waitFor(() => expect(registerCount).toBe(2));
        await waitFor(() => expect(client.store.getSnapshot().connection).toBe('connected'));

        expect(eventQueues.slice(0, 2)).toEqual(['queue-1', 'queue-2']);
        expect(eventQueues.filter((queue) => queue === 'queue-1')).toHaveLength(1);
        await client.stop(true);
    });

    it.each([
        { randomValue: 0, expectedDelayMs: 800 },
        { randomValue: 1, expectedDelayMs: 1_000 },
    ])('jitters restart recovery by $expectedDelayMs ms and replaces the old queue', async ({ randomValue, expectedDelayMs }) => {
        vi.useFakeTimers();
        let registerCount = 0;
        const eventQueues: string[] = [];
        let observeProbe!: () => void;
        const probeObserved = new Promise<void>((resolve) => { observeProbe = resolve; });
        const transport = vi.fn(async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
            const request = input instanceof Request ? input : new Request(input, init);
            const url = new URL(request.url);
            const path = url.pathname.replace('/api/v1/', '');
            if (path === 'users/me') {
                return json({ user_id: 7, full_name: 'Ada Lovelace', email: 'ada@example.test' });
            }
            if (path === 'register') {
                registerCount += 1;
                return json({
                    queue_id: `queue-${registerCount}`,
                    last_event_id: registerCount,
                    event_queue_longpoll_timeout_seconds: 60,
                    max_message_length: 10_000,
                    max_topic_length: 60,
                    subscriptions: [],
                    realm_users: [{ user_id: 7, full_name: 'Ada Lovelace' }],
                    recent_private_conversations: [],
                    unread_msgs: { count: 0, streams: [], pms: [], huddles: [] },
                });
            }
            if (path === 'server_settings') {
                observeProbe();
                return json({
                    zulip_version: '12.1',
                    zulip_feature_level: 500,
                    is_incompatible: false,
                    email_auth_enabled: true,
                });
            }
            if (path === 'events' && request.method === 'GET') {
                const queue = url.searchParams.get('queue_id')!;
                eventQueues.push(queue);
                if (queue === 'queue-1') {
                    return json({ events: [{ id: 2, type: 'restart' }] });
                }
                return await abortablePending(request.signal);
            }
            throw new Error(`Unexpected fake endpoint ${request.method} ${path}`);
        });
        const client = new WebClientSession(session, {
            apiClient: new ZulipApiClient(transport),
            random: () => randomValue,
        });

        try {
            await client.start();
            await probeObserved;
            await vi.advanceTimersByTimeAsync(expectedDelayMs - 1);
            expect(registerCount).toBe(1);
            await vi.advanceTimersByTimeAsync(1);
            expect(registerCount).toBe(2);
            expect(eventQueues.slice(0, 2)).toEqual(['queue-1', 'queue-2']);
            expect(eventQueues.filter((queue) => queue === 'queue-1')).toHaveLength(1);
        } finally {
            await client.stop(false);
            vi.useRealTimers();
        }
    });

    it('does not let an earlier asynchronous stop reset a newly started lifecycle', async () => {
        let registerCount = 0;
        let sendAttempts = 0;
        const transport = vi.fn(async (input: RequestInfo | URL): Promise<Response> => {
            const request = input as Request;
            const url = new URL(request.url);
            const path = url.pathname.replace('/api/v1/', '');
            if (path === 'users/me') {
                return json({ user_id: 7, full_name: 'Ada Lovelace', email: 'ada@example.test' });
            }
            if (path === 'register') {
                registerCount += 1;
                return json({
                    queue_id: `queue-${registerCount}`,
                    last_event_id: registerCount,
                    event_queue_longpoll_timeout_seconds: 60,
                    max_message_length: 10_000,
                    max_topic_length: 60,
                    subscriptions: [],
                    realm_users: [{ user_id: 7, full_name: 'Ada Lovelace' }],
                    recent_private_conversations: [],
                    unread_msgs: { count: 0, streams: [], pms: [], huddles: [] },
                });
            }
            if (path === 'events' && request.method === 'GET') {
                return await abortablePending(request.signal);
            }
            if (path === 'messages' && request.method === 'POST') {
                sendAttempts += 1;
                return await abortablePending(request.signal);
            }
            throw new Error(`Unexpected fake endpoint ${request.method} ${path}`);
        });
        const client = new WebClientSession(session, { apiClient: new ZulipApiClient(transport) });

        await client.start();
        const send = client.send(directMessage([9]), 'old lifecycle');
        await waitFor(() => expect(sendAttempts).toBe(1));
        expect(Object.keys(client.store.getSnapshot().outbox)).toHaveLength(1);
        const stopping = client.stop(false);
        const restarting = client.start();
        await Promise.all([send, stopping, restarting]);

        expect(client.store.getSnapshot().connection).toBe('connected');
        expect(registerCount).toBe(2);
        expect(sendAttempts).toBe(1);
        expect(client.store.getSnapshot().outbox).toEqual({});
        await client.stop(false);
    });

    it('keeps unread authoritative when the server rejects mark-read', async () => {
        let markReadAttempts = 0;
        const transport = vi.fn(async (input: RequestInfo | URL): Promise<Response> => {
            const request = input as Request;
            const url = new URL(request.url);
            const path = url.pathname.replace('/api/v1/', '');
            if (path === 'users/me') {
                return json({ user_id: 7, full_name: 'Ada Lovelace', email: 'ada@example.test' });
            }
            if (path === 'register') {
                return json({
                    queue_id: 'queue-test',
                    last_event_id: 1,
                    event_queue_longpoll_timeout_seconds: 60,
                    max_message_length: 10_000,
                    max_topic_length: 60,
                    subscriptions: [],
                    realm_users: [
                        { user_id: 7, full_name: 'Ada Lovelace' },
                        { user_id: 9, full_name: 'Grace Hopper' },
                    ],
                    recent_private_conversations: [{ user_ids: [7, 9] }],
                    unread_msgs: {
                        count: 1,
                        streams: [],
                        pms: [{ other_user_id: 9, unread_message_ids: [101] }],
                        huddles: [],
                    },
                });
            }
            if (path === 'messages' && request.method === 'GET') {
                return json({
                    found_oldest: true,
                    found_newest: true,
                    messages: [{
                        id: 101,
                        type: 'private',
                        display_recipient: [{ id: 7 }, { id: 9 }],
                        sender_id: 9,
                        sender_full_name: 'Grace Hopper',
                        content: 'unread',
                        timestamp: 1,
                        flags: [],
                    }],
                });
            }
            if (path === 'messages/flags/narrow') {
                markReadAttempts += 1;
                return new Response('{}', { status: 500, headers: { 'Content-Type': 'application/json' } });
            }
            if (path === 'events' && request.method === 'GET') {
                return await new Promise<Response>((_resolve, reject) => {
                    request.signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true });
                });
            }
            if (path === 'events' && request.method === 'DELETE') {
                return json({ result: 'success', msg: '' });
            }
            throw new Error(`Unexpected fake endpoint ${request.method} ${path}`);
        });
        const client = new WebClientSession(session, { apiClient: new ZulipApiClient(transport) });

        await client.start();
        await waitFor(() => expect(markReadAttempts).toBe(1));

        expect(client.store.getSnapshot().unread.counts['dm:9']).toBe(1);
        expect(client.store.getSnapshot().messages[101]?.isRead).toBe(false);
        await client.stop(true);
    });

    it('settles a hanging POST at WaitExpired without retrying and keeps explicit recovery', async () => {
        let sendAttempts = 0;
        const transport = vi.fn(async (input: RequestInfo | URL): Promise<Response> => {
            const request = input as Request;
            const url = new URL(request.url);
            const path = url.pathname.replace('/api/v1/', '');
            if (path === 'users/me') {
                return json({ user_id: 7, full_name: 'Ada Lovelace', email: 'ada@example.test' });
            }
            if (path === 'register') {
                return json({
                    queue_id: 'queue-test', last_event_id: 1,
                    event_queue_longpoll_timeout_seconds: 60,
                    max_message_length: 10_000, max_topic_length: 60,
                    subscriptions: [],
                    realm_users: [{ user_id: 7, full_name: 'Ada Lovelace' }, { user_id: 9, full_name: 'Grace Hopper' }],
                    recent_private_conversations: [],
                    unread_msgs: { count: 0, streams: [], pms: [], huddles: [] },
                });
            }
            if (path === 'events' && request.method === 'GET') {
                return await abortablePending(request.signal);
            }
            if (path === 'events' && request.method === 'DELETE') {
                return json({ result: 'success', msg: '' });
            }
            if (path === 'messages' && request.method === 'POST') {
                sendAttempts += 1;
                return await abortablePending(request.signal);
            }
            throw new Error(`Unexpected fake endpoint ${request.method} ${path}`);
        });
        const client = new WebClientSession(session, {
            apiClient: new ZulipApiClient(transport),
            createLocalId: () => 'local-expired',
        });
        await client.start();
        vi.useFakeTimers();
        try {
            const send = client.send(directMessage([9]), 'may already exist');
            await vi.advanceTimersByTimeAsync(10_000);

            await expect(send).rejects.toThrow('不会自动重试');
            expect(sendAttempts).toBe(1);
            expect(client.store.getSnapshot().outbox['local-expired']).toMatchObject({
                status: 'waitExpired',
                content: 'may already exist',
            });
            expect(client.recoverOutbox('local-expired')).toMatchObject({ content: 'may already exist' });
            expect(sendAttempts).toBe(1);
        } finally {
            vi.useRealTimers();
            await client.stop(true);
        }
    });

    it('waits for an aborted in-flight send to settle before deleting the queue', async () => {
        let releaseAbort!: () => void;
        const abortRelease = new Promise<void>((resolve) => { releaseAbort = resolve; });
        const order: string[] = [];
        const transport = vi.fn(async (input: RequestInfo | URL): Promise<Response> => {
            const request = input as Request;
            const url = new URL(request.url);
            const path = url.pathname.replace('/api/v1/', '');
            if (path === 'users/me') {
                return json({ user_id: 7, full_name: 'Ada Lovelace', email: 'ada@example.test' });
            }
            if (path === 'register') {
                return json({
                    queue_id: 'queue-test', last_event_id: 1,
                    event_queue_longpoll_timeout_seconds: 60,
                    max_message_length: 10_000, max_topic_length: 60,
                    subscriptions: [],
                    realm_users: [{ user_id: 7, full_name: 'Ada Lovelace' }, { user_id: 9, full_name: 'Grace Hopper' }],
                    recent_private_conversations: [],
                    unread_msgs: { count: 0, streams: [], pms: [], huddles: [] },
                });
            }
            if (path === 'events' && request.method === 'GET') {
                return await abortablePending(request.signal);
            }
            if (path === 'messages' && request.method === 'POST') {
                return await new Promise<Response>((_resolve, reject) => {
                    request.signal.addEventListener('abort', () => {
                        order.push('send-aborted');
                        void abortRelease.then(() => {
                            order.push('send-settled');
                            reject(new DOMException('Aborted', 'AbortError'));
                        });
                    }, { once: true });
                });
            }
            if (path === 'events' && request.method === 'DELETE') {
                order.push('queue-deleted');
                return json({ result: 'success', msg: '' });
            }
            throw new Error(`Unexpected fake endpoint ${request.method} ${path}`);
        });
        const client = new WebClientSession(session, { apiClient: new ZulipApiClient(transport) });
        await client.start();
        const send = client.send(directMessage([9]), 'in flight');
        await waitFor(() => expect(transport.mock.calls.some(([input]) => (
            input instanceof Request && new URL(input.url).pathname.endsWith('/messages') && input.method === 'POST'
        ))).toBe(true));

        const stop = client.stop(true);
        await waitFor(() => expect(order).toContain('send-aborted'));
        expect(order).not.toContain('queue-deleted');
        releaseAbort();
        await send;
        await stop;

        expect(order).toEqual(['send-aborted', 'send-settled', 'queue-deleted']);
    });

    it('uses process-increasing numeric local IDs for default sends', async () => {
        const localIds: string[] = [];
        const transport = vi.fn(async (input: RequestInfo | URL): Promise<Response> => {
            const request = input as Request;
            const url = new URL(request.url);
            const path = url.pathname.replace('/api/v1/', '');
            if (path === 'users/me') return json({ user_id: 7, full_name: 'Ada Lovelace', email: 'ada@example.test' });
            if (path === 'register') return json({
                queue_id: 'queue-test', last_event_id: 1,
                event_queue_longpoll_timeout_seconds: 60,
                max_message_length: 10_000, max_topic_length: 60,
                subscriptions: [],
                realm_users: [{ user_id: 7, full_name: 'Ada Lovelace' }, { user_id: 9, full_name: 'Grace Hopper' }],
                recent_private_conversations: [],
                unread_msgs: { count: 0, streams: [], pms: [], huddles: [] },
            });
            if (path === 'events' && request.method === 'GET') return await abortablePending(request.signal);
            if (path === 'events' && request.method === 'DELETE') return json({ result: 'success', msg: '' });
            if (path === 'messages' && request.method === 'POST') {
                const form = new URLSearchParams(await request.text());
                localIds.push(form.get('local_id')!);
                return json({ id: 500 + localIds.length });
            }
            if (path === 'messages' && request.method === 'GET') return json({ messages: [], found_oldest: true, found_newest: true });
            throw new Error(`Unexpected fake endpoint ${request.method} ${path}`);
        });
        const client = new WebClientSession(session, { apiClient: new ZulipApiClient(transport) });
        await client.start();

        await client.send(directMessage([9]), 'first');
        await client.send(directMessage([9]), 'second');

        expect(localIds).toHaveLength(2);
        expect(localIds.every((value) => /^\d+$/u.test(value))).toBe(true);
        expect(Number(localIds[1])).toBe(Number(localIds[0]) + 1);
        await client.stop(true);
    });

    it('bounds exponential retry jitter and preserves the cap', () => {
        expect(jitteredBackoff(1_000, () => 0)).toBe(800);
        expect(jitteredBackoff(1_000, () => 1)).toBe(1_000);
        expect(jitteredBackoff(60_000, () => 0.5)).toBe(27_000);
        expect(jitteredBackoff(60_000, () => 1)).toBe(30_000);
    });
});

async function abortablePending(signal: AbortSignal): Promise<Response> {
    return await new Promise<Response>((_resolve, reject) => {
        signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true });
    });
}
