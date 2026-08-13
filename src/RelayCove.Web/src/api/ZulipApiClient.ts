import { createBasicAuthorization } from './basicAuth';
import { conversationNarrow } from '../domain/conversation';
import type {
    ConversationKey,
    EmojiReactionIdentity,
    EventBatch,
    HistoryResult,
    RegisterSnapshot,
    SendResult,
    TopicSummary,
    UploadedFile,
    UserProfile,
} from '../domain/types';
import { ZulipWebError } from './errors';
import { apiUrl, normalizeRealm } from './realm';
import { resolveRealmMediaUrl, type RealmMediaKind } from './realmMedia';
import { mapEvents, mapHistory, mapRegister, mapTopics, mapUser } from './zulipMapping';
import type { ApiKeyCredential, FetchTransport, ServerSettings, WebSession } from './types';

interface RawServerSettings {
    zulip_version?: unknown;
    zulip_feature_level?: unknown;
    is_incompatible?: unknown;
    email_auth_enabled?: unknown;
    authentication_methods?: {
        email?: unknown;
    };
}

interface RawApiKeyCredential {
    api_key?: unknown;
    email?: unknown;
    user_id?: unknown;
}

const requestDefaults: Pick<RequestInit, 'cache' | 'credentials' | 'redirect' | 'referrerPolicy'> = {
    cache: 'no-store',
    credentials: 'omit',
    redirect: 'error',
    referrerPolicy: 'no-referrer',
};

const MAX_PREVIEW_IMAGE_BYTES = 25 * 1024 * 1024;
const MAX_ATTACHMENT_DOWNLOAD_BYTES = 100 * 1024 * 1024;
const previewImageTypes = new Set(['image/avif', 'image/gif', 'image/jpeg', 'image/png', 'image/webp']);

const DEFAULT_EVENT_TYPES = [
    'message',
    'reaction',
    'subscription',
    'realm_user',
    'stream',
    'update_message',
    'delete_message',
    'update_message_flags',
    'realm',
    'heartbeat',
    'restart',
] as const;

const INITIAL_FETCH_EVENT_TYPES = [
    'subscription',
    'realm_user',
    'realm',
    'recent_private_conversations',
] as const;

const CLIENT_CAPABILITIES = {
    notification_settings_null: true,
    bulk_message_deletion: true,
    user_avatar_url_field_optional: true,
    user_list_incomplete: true,
    empty_topic_name: true,
    archived_channels: true,
} as const;

export class ZulipApiClient {
    private readonly transport: FetchTransport;

    public constructor(transport?: FetchTransport) {
        this.transport = transport ?? ((input, init) => globalThis.fetch(input, init));
    }

    public async getServerSettings(realm: string, signal?: AbortSignal): Promise<ServerSettings> {
        const response = await this.send(
            apiUrl(realm, 'server_settings'),
            { ...requestDefaults, method: 'GET', signal },
            'realm_unavailable',
        );
        const body = await this.readJson<RawServerSettings>(response);

        if (
            typeof body.zulip_version !== 'string'
            || typeof body.zulip_feature_level !== 'number'
        ) {
            throw new ZulipWebError('invalid_response', response.status);
        }

        const emailAuthenticationEnabled = typeof body.email_auth_enabled === 'boolean'
            ? body.email_auth_enabled
            : body.authentication_methods?.email === true;

        return {
            zulipVersion: body.zulip_version,
            zulipFeatureLevel: body.zulip_feature_level,
            isIncompatible: body.is_incompatible === true,
            emailAuthenticationEnabled,
        };
    }

    public async fetchApiKey(
        realm: string,
        email: string,
        password: string,
        signal?: AbortSignal,
    ): Promise<ApiKeyCredential> {
        const normalizedRealm = normalizeRealm(realm);
        const body = new URLSearchParams({
            username: email.trim(),
            password,
        });
        const response = await this.send(
            apiUrl(normalizedRealm, 'fetch_api_key'),
            {
                ...requestDefaults,
                method: 'POST',
                headers: {
                    'Content-Type': 'application/x-www-form-urlencoded;charset=UTF-8',
                },
                body,
                signal,
            },
            'authentication_failed',
        );
        const credential = await this.readJson<RawApiKeyCredential>(response);

        if (
            typeof credential.api_key !== 'string'
            || credential.api_key.length === 0
            || typeof credential.email !== 'string'
            || credential.email.length === 0
            || (credential.user_id !== undefined && typeof credential.user_id !== 'number')
        ) {
            throw new ZulipWebError('invalid_response', response.status);
        }

        return {
            realm: normalizedRealm,
            email: credential.email,
            apiKey: credential.api_key,
            userId: typeof credential.user_id === 'number' ? credential.user_id : undefined,
        };
    }

    public createAuthenticatedRequest(
        credential: ApiKeyCredential,
        endpoint: string,
        init: RequestInit = {},
    ): Request {
        const headers = new Headers(init.headers);
        headers.set('Authorization', createBasicAuthorization(credential.email, credential.apiKey));
        const separator = endpoint.indexOf('?');
        const path = separator >= 0 ? endpoint.slice(0, separator) : endpoint;
        const query = separator >= 0 ? endpoint.slice(separator + 1) : '';
        const url = new URL(apiUrl(credential.realm, path));
        if (query) {
            url.search = query;
        }
        return new Request(url, {
            ...init,
            ...requestDefaults,
            headers,
        });
    }

    public async getCurrentUser(
        credential: ApiKeyCredential,
        signal?: AbortSignal,
    ): Promise<UserProfile> {
        const response = await this.sendAuthenticated(credential, 'users/me', { method: 'GET', signal });
        const user = mapUser(await this.readJson<unknown>(response));
        if (!user) {
            throw new ZulipWebError('invalid_response', response.status);
        }
        return user;
    }

    public async register(session: WebSession, signal?: AbortSignal): Promise<RegisterSnapshot> {
        const body = formBody({
            apply_markdown: 'false',
            client_gravatar: 'false',
            include_subscribers: 'false',
            idle_queue_timeout: '3600',
            event_types: JSON.stringify(DEFAULT_EVENT_TYPES),
            fetch_event_types: JSON.stringify(INITIAL_FETCH_EVENT_TYPES),
            client_capabilities: JSON.stringify(CLIENT_CAPABILITIES),
        });
        const response = await this.sendAuthenticated(session, 'register', {
            method: 'POST',
            headers: formHeaders(),
            body,
            signal,
        });
        return mapRegister(await this.readJson<unknown>(response), session.userId);
    }

    public async getEvents(
        session: WebSession,
        queueId: string,
        lastEventId: number,
        signal?: AbortSignal,
    ): Promise<EventBatch> {
        const endpoint = withQuery('events', {
            queue_id: queueId,
            last_event_id: String(lastEventId),
            dont_block: 'false',
        });
        const response = await this.sendAuthenticated(session, endpoint, { method: 'GET', signal });
        return mapEvents(await this.readJson<unknown>(response), session.userId, lastEventId);
    }

    public async getHistory(
        session: WebSession,
        conversation: ConversationKey,
        anchor: number | 'newest' = 'newest',
        includeAnchor = true,
        limit = 50,
        signal?: AbortSignal,
    ): Promise<HistoryResult> {
        const safeLimit = Math.max(1, Math.min(50, Math.floor(limit)));
        const endpoint = withQuery('messages', {
            narrow: JSON.stringify(conversationNarrow(conversation, session.userId)),
            anchor: String(anchor),
            include_anchor: includeAnchor ? 'true' : 'false',
            num_before: String(includeAnchor ? safeLimit - 1 : safeLimit),
            num_after: '0',
            apply_markdown: 'false',
            client_gravatar: 'false',
            allow_empty_topic_name: 'true',
        });
        const response = await this.sendAuthenticated(session, endpoint, { method: 'GET', signal });
        return mapHistory(await this.readJson<unknown>(response), session.userId);
    }

    public async getTopics(
        session: WebSession,
        channelId: number,
        signal?: AbortSignal,
    ): Promise<readonly TopicSummary[]> {
        if (!Number.isSafeInteger(channelId) || channelId <= 0) {
            throw new TypeError('Invalid channel identity.');
        }
        const endpoint = withQuery(`users/me/${channelId}/topics`, {
            allow_empty_topic_name: 'true',
        });
        const response = await this.sendAuthenticated(session, endpoint, { method: 'GET', signal });
        return mapTopics(await this.readJson<unknown>(response), channelId);
    }

    public async getRealmImage(
        session: WebSession,
        sourceUrl: string,
        kind: RealmMediaKind,
        signal?: AbortSignal,
        maxFileBytes = MAX_ATTACHMENT_DOWNLOAD_BYTES,
    ): Promise<Blob> {
        const approvedUrl = resolveRealmMediaUrl(session.realm, sourceUrl, kind);
        if (!approvedUrl) {
            throw new ZulipWebError('invalid_response');
        }
        const fetchUrl = kind === 'upload' || kind === 'file'
            ? await this.getTemporaryUploadUrl(session, approvedUrl, signal)
            : approvedUrl;
        const headers = new Headers({
            Accept: kind === 'file' ? 'application/octet-stream,*/*;q=0.8' : 'image/avif,image/webp,image/png,image/jpeg,image/gif',
        });
        if (kind === 'avatar') {
            headers.set('Authorization', createBasicAuthorization(session.email, session.apiKey));
        }
        const request = new Request(fetchUrl, {
            ...requestDefaults,
            method: 'GET',
            headers,
            signal,
        });

        let response: Response;
        try {
            response = await this.transport(request);
        } catch (error) {
            if (error instanceof DOMException && error.name === 'AbortError') {
                throw error;
            }
            throw new ZulipWebError('network');
        }
        if (response.status === 401) {
            throw new ZulipWebError('unauthorized', response.status);
        }
        if (!response.ok) {
            throw new ZulipWebError('protocol', response.status);
        }

        const contentType = response.headers.get('Content-Type')?.split(';', 1)[0]?.trim().toLocaleLowerCase();
        if (kind === 'file') {
            return readBlobWithLimit(
                response,
                'application/octet-stream',
                Math.max(1, Math.min(MAX_ATTACHMENT_DOWNLOAD_BYTES, maxFileBytes)),
            );
        }
        if (!contentType || !previewImageTypes.has(contentType)) {
            throw new ZulipWebError('invalid_response', response.status);
        }
        return readBlobWithLimit(response, contentType, MAX_PREVIEW_IMAGE_BYTES);
    }

    private async getTemporaryUploadUrl(
        session: WebSession,
        approvedUploadUrl: string,
        signal?: AbortSignal,
    ): Promise<string> {
        const upload = new URL(approvedUploadUrl);
        if (!upload.pathname.startsWith('/user_uploads/') || upload.pathname.startsWith('/user_uploads/temporary/')) {
            throw new ZulipWebError('invalid_response');
        }
        const apiDownloadUrl = new URL(`/api/v1${upload.pathname}`, session.realm);
        const headers = new Headers({ Accept: 'application/json' });
        headers.set('Authorization', createBasicAuthorization(session.email, session.apiKey));
        const request = new Request(apiDownloadUrl, {
            ...requestDefaults,
            method: 'GET',
            headers,
            signal,
        });

        let response: Response;
        try {
            response = await this.transport(request);
        } catch (error) {
            if (error instanceof DOMException && error.name === 'AbortError') {
                throw error;
            }
            throw new ZulipWebError('network');
        }
        if (response.status === 401) {
            throw new ZulipWebError('unauthorized', response.status);
        }
        if (!response.ok) {
            throw new ZulipWebError('protocol', response.status);
        }
        const body = await this.readJson<{ url?: unknown }>(response);
        const temporaryUrl = typeof body.url === 'string'
            ? resolveRealmMediaUrl(session.realm, body.url, 'upload')
            : undefined;
        if (!temporaryUrl || !new URL(temporaryUrl).pathname.startsWith('/user_uploads/temporary/')) {
            throw new ZulipWebError('invalid_response', response.status);
        }
        return temporaryUrl;
    }

    public async sendMessage(
        session: WebSession,
        queueId: string,
        localId: string,
        conversation: ConversationKey,
        content: string,
        signal?: AbortSignal,
    ): Promise<SendResult> {
        const fields: Record<string, string> = { content, queue_id: queueId, local_id: localId };
        if (conversation.kind === 'channel') {
            fields.type = 'channel';
            fields.to = String(conversation.channelId);
            fields.topic = conversation.topic;
        } else {
            fields.type = 'direct';
            fields.to = JSON.stringify(
                conversation.otherUserIds.length === 0 ? [session.userId] : conversation.otherUserIds,
            );
        }
        const response = await this.sendAuthenticated(session, 'messages', {
            method: 'POST',
            headers: formHeaders(),
            body: formBody(fields),
            signal,
        }, true);
        const body = await this.readJson<Record<string, unknown>>(response);
        const messageId = typeof body.id === 'number' && Number.isSafeInteger(body.id) ? body.id : undefined;
        if (messageId === undefined) {
            throw new ZulipWebError('invalid_response', response.status);
        }
        return { localId, messageId };
    }

    public async uploadFile(
        session: WebSession,
        file: File,
        signal?: AbortSignal,
    ): Promise<UploadedFile> {
        if (file.size <= 0 || file.name.length === 0) {
            throw new TypeError('Invalid upload file.');
        }
        const uploadFilename = sanitizeUploadFilename(file.name);
        const body = new FormData();
        body.append('filename', file, uploadFilename);
        const response = await this.sendAuthenticated(session, 'user_uploads', {
            method: 'POST',
            body,
            signal,
        }, true);
        const result = await this.readJson<Record<string, unknown>>(response);
        const rawUrl = typeof result.url === 'string'
            ? result.url
            : typeof result.uri === 'string' ? result.uri : undefined;
        const url = resolveRealmMediaUrl(session.realm, rawUrl, 'upload');
        if (!url) {
            throw new ZulipWebError('invalid_response', response.status);
        }
        const filename = sanitizeUploadFilename(
            typeof result.filename === 'string' ? result.filename : file.name,
        );
        return { url, filename };
    }

    public async unsubscribeChannel(
        session: WebSession,
        channelName: string,
        signal?: AbortSignal,
    ): Promise<{ removed: string[]; notRemoved: string[] }> {
        if (!channelName.trim() || channelName.length > 200 || /[\u0000-\u001f\u007f]/u.test(channelName)) {
            throw new TypeError('Invalid channel name.');
        }
        const response = await this.sendAuthenticated(session, 'users/me/subscriptions', {
            method: 'DELETE',
            headers: formHeaders(),
            body: formBody({ subscriptions: JSON.stringify([channelName]) }),
            signal,
        }, true);
        const result = await this.readJson<Record<string, unknown>>(response);
        const removed = parseStringArray(result.removed);
        const notRemoved = parseStringArray(result.not_removed);
        if (!removed || !notRemoved) {
            throw new ZulipWebError('invalid_response', response.status);
        }
        return { removed, notRemoved };
    }

    public async setReaction(
        session: WebSession,
        messageId: number,
        reaction: EmojiReactionIdentity,
        active: boolean,
        signal?: AbortSignal,
    ): Promise<void> {
        validateMessageId(messageId);
        validateReaction(reaction);
        try {
            const response = await this.sendAuthenticated(session, `messages/${messageId}/reactions`, {
                method: active ? 'POST' : 'DELETE',
                headers: formHeaders(),
                body: formBody({
                    emoji_name: reaction.emojiName,
                    emoji_code: reaction.emojiCode,
                    reaction_type: reaction.reactionType,
                }),
                signal,
            }, true);
            await this.readJson<unknown>(response);
        } catch (error) {
            if (error instanceof ZulipWebError && (
                (active && error.serverCode === 'REACTION_ALREADY_EXISTS')
                || (!active && error.serverCode === 'REACTION_DOES_NOT_EXIST')
            )) {
                return;
            }
            throw error;
        }
    }

    public async editMessage(
        session: WebSession,
        messageId: number,
        content: string,
        previousContentSha256: string,
        signal?: AbortSignal,
    ): Promise<void> {
        validateMessageId(messageId);
        if (!content.trim() || !/^[a-f0-9]{64}$/u.test(previousContentSha256)) {
            throw new TypeError('Invalid message edit.');
        }
        const response = await this.sendAuthenticated(session, `messages/${messageId}`, {
            method: 'PATCH',
            headers: formHeaders(),
            body: formBody({ content, prev_content_sha256: previousContentSha256 }),
            signal,
        }, true);
        await this.readJson<unknown>(response);
    }

    public async deleteMessage(
        session: WebSession,
        messageId: number,
        signal?: AbortSignal,
    ): Promise<void> {
        validateMessageId(messageId);
        const response = await this.sendAuthenticated(session, `messages/${messageId}`, {
            method: 'DELETE',
            signal,
        }, true);
        await this.readJson<unknown>(response);
    }

    public async setMessageStarred(
        session: WebSession,
        messageId: number,
        starred: boolean,
        signal?: AbortSignal,
    ): Promise<void> {
        validateMessageId(messageId);
        const response = await this.sendAuthenticated(session, 'messages/flags', {
            method: 'POST',
            headers: formHeaders(),
            body: formBody({
                messages: JSON.stringify([messageId]),
                op: starred ? 'add' : 'remove',
                flag: 'starred',
            }),
            signal,
        }, true);
        await this.readJson<unknown>(response);
    }

    public async markConversationRead(
        session: WebSession,
        conversation: ConversationKey,
        anchor: number | 'newest',
        limit: number,
        signal?: AbortSignal,
    ): Promise<void> {
        const narrow = [...conversationNarrow(conversation, session.userId), { operator: 'is', operand: 'unread' }];
        const safeLimit = Math.max(1, Math.min(50, Math.floor(limit)));
        const response = await this.sendAuthenticated(session, 'messages/flags/narrow', {
            method: 'POST',
            headers: formHeaders(),
            body: formBody({
                op: 'add',
                flag: 'read',
                narrow: JSON.stringify(narrow),
                anchor: String(anchor),
                num_before: String(safeLimit - 1),
                num_after: '0',
            }),
            signal,
        });
        await this.readJson<unknown>(response);
    }

    public async deleteQueue(
        session: WebSession,
        queueId: string,
        signal?: AbortSignal,
    ): Promise<void> {
        try {
            const response = await this.sendAuthenticated(session, 'events', {
                method: 'DELETE',
                headers: formHeaders(),
                body: formBody({ queue_id: queueId }),
                signal,
            });
            await this.readJson<unknown>(response);
        } catch (error) {
            if (!(error instanceof ZulipWebError) || error.code !== 'queue_expired') {
                throw error;
            }
        }
    }

    private async sendAuthenticated(
        credential: ApiKeyCredential,
        endpoint: string,
        init: RequestInit,
        isNonIdempotent = false,
    ): Promise<Response> {
        const request = this.createAuthenticatedRequest(credential, endpoint, init);
        let response: Response;
        try {
            response = await this.transport(request);
        } catch (error) {
            if (error instanceof DOMException && error.name === 'AbortError') {
                throw error;
            }
            throw new ZulipWebError(isNonIdempotent ? 'request_timed_out' : 'network');
        }
        if (response.ok) {
            return response;
        }
        const status = response.status;
        if (status === 401) {
            throw new ZulipWebError('unauthorized', status);
        }
        if (status === 429) {
            throw new ZulipWebError('rate_limited', status, parseRetryAfter(response.headers.get('Retry-After')));
        }
        const serverCode = await readSafeErrorCode(response);
        if (serverCode === 'BAD_EVENT_QUEUE_ID') {
            throw new ZulipWebError('queue_expired', status);
        }
        if (isNonIdempotent && status >= 400 && status < 500) {
            throw new ZulipWebError('rejected', status, undefined, serverCode);
        }
        if (isNonIdempotent && status >= 500) {
            throw new ZulipWebError('request_timed_out', status);
        }
        throw new ZulipWebError('protocol', status);
    }

    private async send(
        url: string,
        init: RequestInit,
        errorCode: 'realm_unavailable' | 'authentication_failed',
    ): Promise<Response> {
        let response: Response;
        try {
            response = await this.transport(url, init);
        } catch (error) {
            if (error instanceof DOMException && error.name === 'AbortError') {
                throw error;
            }
            throw new ZulipWebError(errorCode);
        }

        if (!response.ok) {
            throw new ZulipWebError(errorCode, response.status);
        }

        return response;
    }

    private async readJson<T>(response: Response): Promise<T> {
        try {
            return await response.json() as T;
        } catch {
            throw new ZulipWebError('invalid_response', response.status);
        }
    }
}

function formHeaders(): HeadersInit {
    return { 'Content-Type': 'application/x-www-form-urlencoded;charset=UTF-8' };
}

function formBody(fields: Readonly<Record<string, string>>): URLSearchParams {
    return new URLSearchParams(fields);
}

function withQuery(endpoint: string, fields: Readonly<Record<string, string>>): string {
    return `${endpoint}?${new URLSearchParams(fields).toString()}`;
}

function validateMessageId(messageId: number): void {
    if (!Number.isSafeInteger(messageId) || messageId <= 0) {
        throw new TypeError('Invalid message identity.');
    }
}

function validateReaction(reaction: EmojiReactionIdentity): void {
    if (
        !reaction.emojiName.trim()
        || !reaction.emojiCode.trim()
        || !reaction.reactionType.trim()
        || reaction.emojiName.length > 128
        || reaction.emojiCode.length > 128
        || reaction.reactionType.length > 64
    ) {
        throw new TypeError('Invalid emoji reaction.');
    }
}

async function readSafeErrorCode(response: Response): Promise<string | undefined> {
    try {
        const value = await response.clone().json() as { code?: unknown };
        return typeof value.code === 'string' ? value.code : undefined;
    } catch {
        return undefined;
    }
}

function parseRetryAfter(value: string | null): number | undefined {
    if (!value) {
        return undefined;
    }
    const seconds = Number(value);
    if (Number.isFinite(seconds) && seconds >= 0) {
        return Math.ceil(seconds * 1_000);
    }
    const date = Date.parse(value);
    return Number.isNaN(date) ? undefined : Math.max(0, date - Date.now());
}

function sanitizeUploadFilename(value: string): string {
    const filename = value
        .replace(/[\u0000-\u001f\u007f\\[\]()`]/gu, '_')
        .trim()
        .slice(0, 256);
    return filename || 'file';
}

function parseStringArray(value: unknown): string[] | undefined {
    return Array.isArray(value) && value.every((item) => typeof item === 'string')
        ? value
        : undefined;
}

async function readBlobWithLimit(response: Response, contentType: string, maxBytes: number): Promise<Blob> {
    const declaredLength = Number(response.headers.get('Content-Length'));
    if (Number.isFinite(declaredLength) && declaredLength > maxBytes) {
        throw new ZulipWebError('invalid_response', response.status);
    }
    if (!response.body) {
        const blob = await response.blob();
        if (blob.size > maxBytes) {
            throw new ZulipWebError('invalid_response', response.status);
        }
        return blob.slice(0, blob.size, contentType);
    }

    const reader = response.body.getReader();
    const chunks: ArrayBuffer[] = [];
    let size = 0;
    try {
        while (true) {
            const { done, value } = await reader.read();
            if (done) {
                break;
            }
            size += value.byteLength;
            if (size > maxBytes) {
                await reader.cancel();
                throw new ZulipWebError('invalid_response', response.status);
            }
            const copy = new Uint8Array(value.byteLength);
            copy.set(value);
            chunks.push(copy.buffer);
        }
    } finally {
        reader.releaseLock();
    }
    return new Blob(chunks, { type: contentType });
}
