import { describe, expect, it, vi } from 'vitest';
import { channelTopic, directMessage } from '../domain/conversation';
import type { WebSession } from './types';
import { ZulipApiClient } from './ZulipApiClient';

const session: WebSession = {
    realm: 'https://chat.example.test',
    email: 'ada@example.test',
    apiKey: 'api-key-secret',
    userId: 7,
    fullName: 'Ada Lovelace',
    remember: true,
};

function json(value: unknown, status = 200): Response {
    return new Response(JSON.stringify(value), {
        status,
        headers: { 'Content-Type': 'application/json' },
    });
}

describe('ZulipApiClient formal chat boundary', () => {
    it('loads and validates the authenticated current user', async () => {
        const transport = vi.fn(async (_input: RequestInfo | URL) => json({
            user_id: 7,
            full_name: 'Ada Lovelace',
            email: 'ada@example.test',
            is_active: true,
            avatar_url: '/user_avatars/7/avatar.png',
        }));

        const user = await new ZulipApiClient(transport).getCurrentUser(session);

        expect(user).toEqual({
            userId: 7,
            fullName: 'Ada Lovelace',
            email: 'ada@example.test',
            isActive: true,
            avatarUrl: '/user_avatars/7/avatar.png',
        });
        const request = transport.mock.calls[0][0] as Request;
        expect(request.url).toBe('https://chat.example.test/api/v1/users/me');
        expect(request.headers.get('Authorization')).toBe(`Basic ${btoa('ada@example.test:api-key-secret')}`);
        expect(request.credentials).toBe('omit');
        expect(request.redirect).toBe('error');
    });

    it('registers the fixed event contract and maps subscriptions, DMs, and unread state', async () => {
        const transport = vi.fn(async (_input: RequestInfo | URL) => json({
            queue_id: 'queue-secret-for-test',
            last_event_id: 41,
            event_queue_longpoll_timeout_seconds: 90,
            max_message_length: 10_000,
            max_topic_length: 60,
            subscriptions: [{ stream_id: 11, name: 'engineering', is_archived: false }],
            realm_users: [{ user_id: 7, full_name: 'Ada Lovelace', email: 'ada@example.test' }],
            recent_private_conversations: [{ user_ids: [7] }, { user_ids: [9, 7, 8] }],
            unread_msgs: {
                count: 3,
                old_unreads_missing: false,
                streams: [{ stream_id: 11, topic: 'Web', unread_message_ids: [101, 102] }],
                pms: [{ other_user_id: 9, unread_message_ids: [103] }],
                huddles: [],
            },
        }));

        const result = await new ZulipApiClient(transport).register(session);

        expect(result.queueId).toBe('queue-secret-for-test');
        expect(result.longPollTimeoutMs).toBe(90_000);
        expect(result.subscriptions).toEqual([{ channelId: 11, name: 'engineering', isActive: true }]);
        expect(result.recentDirectMessages.map((item) => item.canonicalKey)).toEqual(['dm:self', 'dm:8,9']);
        expect(result.unread.counts).toEqual({
            [channelTopic(11, 'Web').canonicalKey]: 2,
            'dm:9': 1,
        });
        const request = transport.mock.calls[0][0] as Request;
        expect(request.method).toBe('POST');
        const form = new URLSearchParams(await request.text());
        expect(JSON.parse(form.get('event_types')!)).toContain('message');
        expect(JSON.parse(form.get('fetch_event_types')!)).toContain('recent_private_conversations');
        expect(JSON.parse(form.get('client_capabilities')!)).toMatchObject({
            empty_topic_name: true,
            archived_channels: true,
        });
        expect(form.get('apply_markdown')).toBe('false');
    });

    it('constructs channel, group DM, and self-DM history narrows with 50-message paging', async () => {
        const requests: Request[] = [];
        const transport = vi.fn(async (input: RequestInfo | URL) => {
            requests.push(input as Request);
            return json({ messages: [], found_oldest: true, found_newest: true });
        });
        const client = new ZulipApiClient(transport);

        await client.getHistory(session, channelTopic(11, 'Web'), 'newest', true);
        await client.getHistory(session, directMessage([9, 8]), 100, false);
        await client.getHistory(session, directMessage([]), 'newest', true);

        const channelUrl = new URL(requests[0].url);
        expect(JSON.parse(channelUrl.searchParams.get('narrow')!)).toEqual([
            { operator: 'channel', operand: 11 },
            { operator: 'topic', operand: 'Web' },
        ]);
        expect(channelUrl.searchParams.get('num_before')).toBe('49');
        const groupUrl = new URL(requests[1].url);
        expect(JSON.parse(groupUrl.searchParams.get('narrow')!)).toEqual([
            { operator: 'dm', operand: [8, 9] },
        ]);
        expect(groupUrl.searchParams.get('num_before')).toBe('50');
        const selfUrl = new URL(requests[2].url);
        expect(JSON.parse(selfUrl.searchParams.get('narrow')!)).toEqual([
            { operator: 'dm', operand: [7] },
        ]);
    });

    it('maps event groups and safely ignores a non-read flag event while advancing the cursor', async () => {
        const transport = vi.fn(async (_input: RequestInfo | URL) => json({
            events: [
                {
                    id: 42,
                    type: 'message',
                    local_message_id: 'local-test-id',
                    message: {
                        id: 101,
                        type: 'stream',
                        stream_id: 11,
                        subject: 'Web',
                        sender_id: 9,
                        sender_full_name: 'Grace Hopper',
                        content: '**raw** Markdown',
                        timestamp: 1_786_000_000,
                        flags: [],
                    },
                },
                { id: 44, type: 'update_message_flags', flag: 'starred', op: 'add', messages: [101] },
                {
                    id: 45,
                    type: 'realm_user',
                    op: 'update',
                    person: { user_id: 9, avatar_url: '/user_avatars/9/new.png', avatar_version: 3 },
                },
            ],
        }));

        const result = await new ZulipApiClient(transport).getEvents(session, 'queue-test', 41);

        expect(result.lastEventId).toBe(45);
        expect(result.groups[0].patches[0]).toMatchObject({
            type: 'messageUpsert',
            localId: 'local-test-id',
            message: { content: '**raw** Markdown', isRead: false },
        });
        expect(result.groups[1].patches).toEqual([{ type: 'ignored' }]);
        expect(result.groups[2].patches).toEqual([{
            type: 'userPatched',
            userId: 9,
            fullName: undefined,
            email: undefined,
            isActive: undefined,
            avatarUrl: '/user_avatars/9/new.png',
            avatarVersion: 3,
            isBot: undefined,
        }]);
    });

    it('sends each explicit message once with queue/local identity and never puts the API key in the URL', async () => {
        const transport = vi.fn(async (_input: RequestInfo | URL) => json({ id: 501 }));
        const client = new ZulipApiClient(transport);

        await client.sendMessage(session, 'queue-test', 'local-test', channelTopic(11, 'Web'), 'hello');

        expect(transport).toHaveBeenCalledOnce();
        const request = transport.mock.calls[0][0] as Request;
        expect(request.url).toBe('https://chat.example.test/api/v1/messages');
        expect(request.url).not.toContain('api-key-secret');
        const form = new URLSearchParams(await request.text());
        expect(Object.fromEntries(form)).toMatchObject({
            content: 'hello',
            queue_id: 'queue-test',
            local_id: 'local-test',
            type: 'channel',
            to: '11',
            topic: 'Web',
        });
    });

    it('marks only the current narrow read and deletes the event queue explicitly', async () => {
        const requests: Request[] = [];
        const transport = vi.fn(async (input: RequestInfo | URL) => {
            requests.push(input as Request);
            return json({ result: 'success', msg: '' });
        });
        const client = new ZulipApiClient(transport);

        await client.markConversationRead(session, directMessage([9]), 101, 3);
        await client.deleteQueue(session, 'queue-test');

        const markForm = new URLSearchParams(await requests[0].text());
        expect(JSON.parse(markForm.get('narrow')!)).toEqual([
            { operator: 'dm', operand: [9] },
            { operator: 'is', operand: 'unread' },
        ]);
        expect(markForm.get('num_before')).toBe('2');
        expect(requests[1].method).toBe('DELETE');
        expect(new URLSearchParams(await requests[1].text()).get('queue_id')).toBe('queue-test');
    });

    it('downloads only approved same-Realm images through the authenticated no-redirect boundary', async () => {
        const transport = vi.fn(async (input: RequestInfo | URL) => {
            const request = input as Request;
            if (request.url.includes('/api/v1/user_uploads/')) {
                return json({ url: '/user_uploads/temporary/signed-test-token' });
            }
            return new Response(
                new Uint8Array([137, 80, 78, 71]),
                { status: 200, headers: { 'Content-Type': 'image/png', 'Content-Length': '4' } },
            );
        });
        const client = new ZulipApiClient(transport);

        const blob = await client.getRealmImage(session, '/user_uploads/a/b/design.png', 'upload');

        expect(blob.type).toBe('image/png');
        expect(blob.size).toBe(4);
        const temporaryRequest = transport.mock.calls[0][0] as Request;
        expect(temporaryRequest.url).toBe('https://chat.example.test/api/v1/user_uploads/a/b/design.png');
        expect(temporaryRequest.url).not.toContain('api-key-secret');
        expect(temporaryRequest.headers.get('Authorization')).toBe(`Basic ${btoa('ada@example.test:api-key-secret')}`);
        expect(temporaryRequest.credentials).toBe('omit');
        expect(temporaryRequest.redirect).toBe('error');
        expect(temporaryRequest.referrerPolicy).toBe('no-referrer');
        const imageRequest = transport.mock.calls[1][0] as Request;
        expect(imageRequest.url).toBe('https://chat.example.test/user_uploads/temporary/signed-test-token');
        expect(imageRequest.headers.get('Authorization')).toBeNull();
        expect(imageRequest.redirect).toBe('error');

        await expect(client.getRealmImage(session, 'https://evil.test/user_uploads/design.png', 'upload'))
            .rejects.toMatchObject({ code: 'invalid_response' });
        expect(transport).toHaveBeenCalledTimes(2);
    });

    it('rejects non-image and oversized media responses before exposing them to the UI', async () => {
        const wrongType = new ZulipApiClient(async () => new Response('not an image', {
            status: 200,
            headers: { 'Content-Type': 'text/html' },
        }));
        await expect(wrongType.getRealmImage(session, '/user_avatars/7/avatar.png', 'avatar'))
            .rejects.toMatchObject({ code: 'invalid_response' });

        const oversized = new ZulipApiClient(async () => new Response(new Uint8Array([1]), {
            status: 200,
            headers: { 'Content-Type': 'image/png', 'Content-Length': String(26 * 1024 * 1024) },
        }));
        await expect(oversized.getRealmImage(session, '/user_avatars/7/large.png', 'avatar'))
            .rejects.toMatchObject({ code: 'invalid_response' });
    });

    it('loads the server avatar fallback with Basic auth but never exposes credentials in its URL', async () => {
        const transport = vi.fn(async (_input: RequestInfo | URL) => new Response(new Uint8Array([1, 2, 3]), {
            status: 200,
            headers: { 'Content-Type': 'image/webp' },
        }));

        await new ZulipApiClient(transport).getRealmImage(session, '/avatar/9', 'avatar');

        const request = transport.mock.calls[0][0] as Request;
        expect(request.url).toBe('https://chat.example.test/avatar/9');
        expect(request.url).not.toContain('api-key-secret');
        expect(request.headers.get('Authorization')).toBe(`Basic ${btoa('ada@example.test:api-key-secret')}`);
        expect(request.redirect).toBe('error');
    });

    it('uploads one image as multipart, sanitizes its Markdown filename, and never retries an unknown result', async () => {
        const transport = vi.fn(async (_input: RequestInfo | URL) => json({
            result: 'success',
            msg: '',
            url: '/user_uploads/1/a/design.png',
            filename: '[design].png',
        }));
        const client = new ZulipApiClient(transport);
        const file = new File([new Uint8Array([1, 2, 3])], 'design.png', { type: 'image/png' });

        const uploaded = await client.uploadFile(session, file);

        expect(uploaded).toEqual({
            url: 'https://chat.example.test/user_uploads/1/a/design.png',
            filename: '_design_.png',
        });
        const request = transport.mock.calls[0][0] as Request;
        expect(request.method).toBe('POST');
        expect(request.url).toBe('https://chat.example.test/api/v1/user_uploads');
        expect(request.url).not.toContain('api-key-secret');
        expect(request.headers.get('Authorization')).toBe(`Basic ${btoa('ada@example.test:api-key-secret')}`);
        expect(request.headers.get('Content-Type')).toContain('multipart/form-data; boundary=');
        expect(request.body).not.toBeNull();

        const failingTransport = vi.fn(async (_input: RequestInfo | URL) => {
            throw new TypeError('network detail must not escape');
        });
        await expect(new ZulipApiClient(failingTransport).uploadFile(session, file))
            .rejects.toMatchObject({ code: 'request_timed_out' });
        expect(failingTransport).toHaveBeenCalledOnce();
    });
});
