import { ZulipWebError } from '../api/errors';
import type { WebSession } from '../api/types';
import { ZulipApiClient } from '../api/ZulipApiClient';
import type { RealmMediaKind } from '../api/realmMedia';
import { channelTopic } from '../domain/conversation';
import type { ConversationKey, OutboxFailure, RegisterSnapshot, UploadedFile } from '../domain/types';
import { messagesForConversation } from '../state/webClientReducer';
import { WebClientStore } from '../state/WebClientStore';

const HISTORY_PAGE_SIZE = 50;
const OUTBOX_WAIT_MS = 500;
const OUTBOX_EXPIRY_MS = 10_000;

interface WebClientSessionOptions {
    apiClient?: ZulipApiClient;
    store?: WebClientStore;
    now?: () => number;
    createLocalId?: () => string;
    random?: () => number;
    onReauthenticationRequired?: () => void;
}

let nextProcessLocalId = 0;

export class WebClientSession {
    public readonly store: WebClientStore;
    private readonly apiClient: ZulipApiClient;
    private readonly session: WebSession;
    private readonly now: () => number;
    private readonly createLocalId: () => string;
    private readonly random: () => number;
    private readonly onReauthenticationRequired: () => void;
    private lifecycleEpoch = 0;
    private lifecycleController?: AbortController;
    private selectionController?: AbortController;
    private selectionEpoch = 0;
    private queueId?: string;
    private lastEventId = 0;
    private longPollTimeoutMs = 90_000;
    private readonly outboxTimers = new Map<string, readonly number[]>();
    private readonly sendControllers = new Map<string, AbortController>();
    private reauthenticationHandled = false;
    private sendLane: Promise<void> = Promise.resolve();

    public constructor(session: WebSession, options: WebClientSessionOptions = {}) {
        this.session = session;
        this.apiClient = options.apiClient ?? new ZulipApiClient();
        this.store = options.store ?? new WebClientStore();
        this.now = options.now ?? (() => Date.now());
        this.createLocalId = options.createLocalId ?? (() => String(++nextProcessLocalId));
        this.random = options.random ?? Math.random;
        this.onReauthenticationRequired = options.onReauthenticationRequired ?? (() => undefined);
    }

    public async start(): Promise<void> {
        const epoch = ++this.lifecycleEpoch;
        this.lifecycleController?.abort();
        this.lifecycleController = new AbortController();
        this.reauthenticationHandled = false;
        this.clearAllOutboxTimers();
        this.store.dispatch({ type: 'reset' });
        this.store.dispatch({ type: 'connectionChanged', status: 'bootstrapping' });
        try {
            if (!(await this.validateCurrentUserWithRetry(epoch))) {
                return;
            }
            const registered = await this.registerWithRetry(epoch);
            if (!registered || epoch !== this.lifecycleEpoch) {
                return;
            }
            const initialConversation = chooseInitialConversation(this.store.getSnapshot());
            if (initialConversation) {
                void this.selectConversation(initialConversation);
            }
            void this.loadAllTopics(epoch);
            void this.runEventLoop(epoch);
        } catch (error) {
            if (isAbort(error)) {
                return;
            }
            await this.handleBootstrapError(error);
        }
    }

    public async stop(cleanQueue = true): Promise<void> {
        const stoppedEpoch = ++this.lifecycleEpoch;
        this.lifecycleController?.abort();
        this.lifecycleController = undefined;
        this.selectionController?.abort();
        this.selectionController = undefined;
        for (const controller of this.sendControllers.values()) {
            controller.abort(new DOMException('Session stopped', 'AbortError'));
        }
        this.clearAllOutboxTimers();
        const queueId = this.queueId;
        this.queueId = undefined;
        this.lastEventId = 0;
        await this.sendLane.catch(() => undefined);
        if (cleanQueue && queueId) {
            const cleanup = new AbortController();
            const timeout = window.setTimeout(() => cleanup.abort(), 5_000);
            try {
                await this.apiClient.deleteQueue(this.session, queueId, cleanup.signal);
            } catch {
                // Queue cleanup is best effort; credentials are cleared by the caller regardless.
            } finally {
                window.clearTimeout(timeout);
            }
        }
        if (this.lifecycleEpoch === stoppedEpoch) {
            this.store.dispatch({ type: 'reset' });
        }
    }

    public async selectConversation(conversation: ConversationKey): Promise<void> {
        const lifecycleEpoch = this.lifecycleEpoch;
        const selectionEpoch = ++this.selectionEpoch;
        this.selectionController?.abort();
        const controller = new AbortController();
        this.selectionController = controller;
        this.store.dispatch({ type: 'conversationSelected', conversation });
        this.store.dispatch({ type: 'historyLoading', conversation });
        try {
            const history = await this.apiClient.getHistory(
                this.session,
                conversation,
                'newest',
                true,
                HISTORY_PAGE_SIZE,
                controller.signal,
            );
            if (!this.isCurrentSelection(lifecycleEpoch, selectionEpoch, conversation)) {
                return;
            }
            this.store.dispatch({ type: 'historyLoaded', conversation, history, prepend: false });
            await this.markVisibleRead(conversation, controller.signal, lifecycleEpoch, selectionEpoch);
        } catch (error) {
            if (isAbort(error)) {
                return;
            }
            if (await this.handleCommandError(error)) {
                return;
            }
            if (this.isCurrentSelection(lifecycleEpoch, selectionEpoch, conversation)) {
                this.store.dispatch({ type: 'historyFailed', conversation, error: safeOperationMessage(error) });
            }
        }
    }

    public async loadRealmImage(sourceUrl: string, kind: RealmMediaKind, signal?: AbortSignal): Promise<Blob> {
        try {
            return await this.apiClient.getRealmImage(this.session, sourceUrl, kind, signal);
        } catch (error) {
            if (error instanceof ZulipWebError && error.code === 'unauthorized') {
                await this.requireReauthentication();
            }
            throw error;
        }
    }

    public async uploadImage(file: File, signal?: AbortSignal): Promise<UploadedFile> {
        try {
            return await this.apiClient.uploadFile(this.session, file, signal);
        } catch (error) {
            await this.handleCommandError(error);
            throw error;
        }
    }

    public async loadOlder(conversation: ConversationKey): Promise<void> {
        const state = this.store.getSnapshot();
        const page = state.pages[conversation.canonicalKey];
        if (page?.loading || page?.foundOldest) {
            return;
        }
        const oldestId = messagesForConversation(state, conversation)[0]?.id;
        if (oldestId === undefined) {
            await this.selectConversation(conversation);
            return;
        }
        const lifecycleEpoch = this.lifecycleEpoch;
        const selectionEpoch = this.selectionEpoch;
        const controller = this.selectionController ?? new AbortController();
        this.store.dispatch({ type: 'historyLoading', conversation });
        try {
            const history = await this.apiClient.getHistory(
                this.session,
                conversation,
                oldestId,
                false,
                HISTORY_PAGE_SIZE,
                controller.signal,
            );
            if (!this.isCurrentSelection(lifecycleEpoch, selectionEpoch, conversation)) {
                return;
            }
            this.store.dispatch({ type: 'historyLoaded', conversation, history, prepend: true });
        } catch (error) {
            if (isAbort(error)) {
                return;
            }
            if (!(await this.handleCommandError(error)) && this.isCurrentSelection(lifecycleEpoch, selectionEpoch, conversation)) {
                this.store.dispatch({ type: 'historyFailed', conversation, error: safeOperationMessage(error) });
            }
        }
    }

    public send(conversation: ConversationKey, content: string): Promise<void> {
        const task = this.sendLane.then(
            () => this.performSend(conversation, content),
            () => this.performSend(conversation, content),
        );
        this.sendLane = task.catch(() => undefined);
        return task;
    }

    private async performSend(conversation: ConversationKey, content: string): Promise<void> {
        const state = this.store.getSnapshot();
        const queueId = this.queueId;
        if (state.connection !== 'connected' || !queueId) {
            throw new Error('当前未连接，消息没有发送。');
        }
        const normalizedContent = content.replace(/\r\n/gu, '\n');
        if (!normalizedContent.trim()) {
            throw new Error('消息正文不能为空。');
        }
        if (state.maxMessageLength !== undefined && normalizedContent.length > state.maxMessageLength) {
            throw new Error(`消息不能超过 ${state.maxMessageLength} 个字符。`);
        }
        if (conversation.kind === 'channel') {
            if (!state.subscriptions[conversation.channelId]?.isActive) {
                throw new Error('当前已不再订阅该频道，消息没有发送。');
            }
            if (state.maxTopicLength !== undefined && conversation.topic.length > state.maxTopicLength) {
                throw new Error(`话题不能超过 ${state.maxTopicLength} 个字符。`);
            }
        }

        const localId = this.createLocalId();
        const lifecycleEpoch = this.lifecycleEpoch;
        const linkedSend = linkedAbortController(this.lifecycleController?.signal);
        this.sendControllers.set(localId, linkedSend.controller);
        this.store.dispatch({
            type: 'outboxQueued',
            entry: {
                localId,
                conversation,
                content: normalizedContent,
                createdAt: this.now(),
                status: 'hidden',
            },
        });
        this.startOutboxTimers(localId, linkedSend.controller);
        try {
            const result = await this.apiClient.sendMessage(
                this.session,
                queueId,
                localId,
                conversation,
                normalizedContent,
                linkedSend.controller.signal,
            );
            if (lifecycleEpoch !== this.lifecycleEpoch || !this.store.getSnapshot().outbox[localId]) {
                return;
            }
            const current = this.store.getSnapshot().outbox[localId];
            this.store.dispatch({
                type: 'outboxStatus',
                localId,
                status: current.status,
                messageId: result.messageId,
            });
            void this.reconcileSentMessage(conversation, localId, result.messageId, lifecycleEpoch);
        } catch (error) {
            const currentEntry = this.store.getSnapshot().outbox[localId];
            if (isAbort(error) && currentEntry?.status === 'waitExpired') {
                throw new Error('发送结果尚未确认；不会自动重试。正文已保留，再次发送可能重复。');
            }
            if (isAbort(error) && lifecycleEpoch !== this.lifecycleEpoch) {
                return;
            }
            if (!this.store.getSnapshot().outbox[localId]) {
                // A matching realtime echo is authoritative even if the HTTP response was lost.
                this.clearOutboxTimers(localId);
                return;
            }
            const failure = mapSendFailure(error);
            const resultUnknown = failure === 'networkResultUnknown';
            if (!resultUnknown) {
                this.store.dispatch({ type: 'outboxStatus', localId, status: 'failed', failure });
                this.clearOutboxTimers(localId);
            }
            if (error instanceof ZulipWebError && error.code === 'unauthorized') {
                await this.requireReauthentication();
            } else if (error instanceof ZulipWebError && (error.code === 'network' || error.code === 'request_timed_out')) {
                this.store.dispatch({ type: 'connectionChanged', status: 'offline', reason: 'send_result_unknown' });
            } else if (error instanceof ZulipWebError && error.code === 'rate_limited') {
                this.store.dispatch({ type: 'connectionChanged', status: 'rateLimited' });
            }
            throw new Error(sendFailureMessage(failure));
        } finally {
            linkedSend.dispose();
            this.sendControllers.delete(localId);
        }
    }

    public recoverOutbox(localId: string): { conversation: ConversationKey; content: string } | undefined {
        const entry = this.store.getSnapshot().outbox[localId];
        if (!entry || (entry.status !== 'failed' && entry.status !== 'waitExpired')) {
            return undefined;
        }
        this.clearOutboxTimers(localId);
        this.store.dispatch({ type: 'outboxRemoved', localId });
        return { conversation: entry.conversation, content: entry.content };
    }

    private async registerWithRetry(epoch: number): Promise<boolean> {
        let backoffMs = 1_000;
        while (epoch === this.lifecycleEpoch && !this.lifecycleController?.signal.aborted) {
            try {
                const snapshot = await this.apiClient.register(this.session, this.lifecycleController?.signal);
                if (epoch !== this.lifecycleEpoch) {
                    return false;
                }
                this.applyRegister(snapshot);
                return true;
            } catch (error) {
                if (isAbort(error)) {
                    return false;
                }
                if (error instanceof ZulipWebError && error.code === 'unauthorized') {
                    await this.requireReauthentication();
                    return false;
                }
                if (error instanceof ZulipWebError && error.code === 'rate_limited') {
                    this.store.dispatch({ type: 'connectionChanged', status: 'rateLimited' });
                    await delay(error.retryAfterMs ?? jitteredBackoff(backoffMs, this.random), this.lifecycleController?.signal);
                    backoffMs = Math.min(30_000, backoffMs * 2);
                    continue;
                }
                if (error instanceof ZulipWebError && error.code === 'network') {
                    this.store.dispatch({ type: 'connectionChanged', status: 'offline' });
                    await delay(jitteredBackoff(backoffMs, this.random), this.lifecycleController?.signal);
                    backoffMs = Math.min(30_000, backoffMs * 2);
                    continue;
                }
                this.store.dispatch({ type: 'connectionChanged', status: 'faulted', reason: 'register_failed' });
                return false;
            }
        }
        return false;
    }

    private async validateCurrentUserWithRetry(epoch: number): Promise<boolean> {
        let backoffMs = 1_000;
        while (epoch === this.lifecycleEpoch && !this.lifecycleController?.signal.aborted) {
            try {
                const currentUser = await this.apiClient.getCurrentUser(this.session, this.lifecycleController?.signal);
                if (currentUser.userId !== this.session.userId) {
                    await this.requireReauthentication();
                    return false;
                }
                return true;
            } catch (error) {
                if (isAbort(error)) {
                    return false;
                }
                if (error instanceof ZulipWebError && error.code === 'unauthorized') {
                    await this.requireReauthentication();
                    return false;
                }
                if (error instanceof ZulipWebError && error.code === 'rate_limited') {
                    this.store.dispatch({ type: 'connectionChanged', status: 'rateLimited' });
                    await delay(error.retryAfterMs ?? jitteredBackoff(backoffMs, this.random), this.lifecycleController?.signal);
                    backoffMs = Math.min(30_000, backoffMs * 2);
                    continue;
                }
                if (error instanceof ZulipWebError && error.code === 'network') {
                    this.store.dispatch({ type: 'connectionChanged', status: 'offline' });
                    await delay(jitteredBackoff(backoffMs, this.random), this.lifecycleController?.signal);
                    backoffMs = Math.min(30_000, backoffMs * 2);
                    continue;
                }
                this.store.dispatch({ type: 'connectionChanged', status: 'faulted', reason: 'identity_validation_failed' });
                return false;
            }
        }
        return false;
    }

    private applyRegister(snapshot: RegisterSnapshot): void {
        this.queueId = snapshot.queueId;
        this.lastEventId = snapshot.lastEventId;
        this.longPollTimeoutMs = snapshot.longPollTimeoutMs;
        this.store.dispatch({
            type: 'registerApplied',
            snapshot,
            currentUser: {
                userId: this.session.userId,
                fullName: this.session.fullName,
                email: this.session.email,
                isActive: true,
            },
        });
    }

    private async loadAllTopics(epoch: number): Promise<void> {
        const subscriptions = Object.values(this.store.getSnapshot().subscriptions).filter((item) => item.isActive);
        let nextIndex = 0;
        const workers = Array.from({ length: Math.min(4, subscriptions.length) }, async () => {
            while (nextIndex < subscriptions.length && epoch === this.lifecycleEpoch) {
                const subscription = subscriptions[nextIndex++];
                try {
                    const topics = await this.apiClient.getTopics(
                        this.session,
                        subscription.channelId,
                        this.lifecycleController?.signal,
                    );
                    if (epoch === this.lifecycleEpoch) {
                        this.store.dispatch({ type: 'topicsLoaded', topics });
                    }
                } catch (error) {
                    if (error instanceof ZulipWebError && error.code === 'unauthorized') {
                        await this.requireReauthentication();
                    }
                    // A channel may be revoked while the snapshot is being loaded.
                }
            }
        });
        await Promise.allSettled(workers);
        if (epoch !== this.lifecycleEpoch || this.store.getSnapshot().selectedConversation) {
            return;
        }
        const initial = chooseInitialConversation(this.store.getSnapshot());
        if (initial) {
            void this.selectConversation(initial);
        }
    }

    private async runEventLoop(epoch: number): Promise<void> {
        let backoffMs = 1_000;
        while (epoch === this.lifecycleEpoch && !this.lifecycleController?.signal.aborted) {
            if (!this.queueId) {
                this.store.dispatch({ type: 'connectionChanged', status: 'reconnecting' });
                if (!(await this.registerWithRetry(epoch))) {
                    return;
                }
                void this.loadAllTopics(epoch);
            }
            const queueId = this.queueId;
            if (!queueId) {
                continue;
            }
            const timedSignal = timeoutSignal(this.lifecycleController!.signal, this.longPollTimeoutMs + 5_000);
            try {
                const batch = await this.apiClient.getEvents(
                    this.session,
                    queueId,
                    this.lastEventId,
                    timedSignal.signal,
                );
                timedSignal.dispose();
                if (epoch !== this.lifecycleEpoch) {
                    return;
                }
                const groups = batch.groups.filter((group) => group.eventId === undefined || group.eventId > this.lastEventId);
                this.lastEventId = Math.max(this.lastEventId, batch.lastEventId);
                this.store.dispatch({ type: 'eventsApplied', groups });
                const addedChannelIds = groups.flatMap((group) => group.patches)
                    .filter((patch): patch is Extract<typeof patch, { type: 'subscriptionUpsert' }> => (
                        patch.type === 'subscriptionUpsert' && patch.subscription.isActive
                    ))
                    .map((patch) => patch.subscription.channelId);
                for (const channelId of new Set(addedChannelIds)) {
                    void this.loadTopics(channelId, epoch);
                }
                const restarted = groups.some((group) => group.patches.some((patch) => patch.type === 'restart'));
                if (restarted) {
                    this.queueId = undefined;
                    try {
                        const settings = await this.apiClient.getServerSettings(
                            this.session.realm,
                            this.lifecycleController?.signal,
                        );
                        if (settings.isIncompatible || settings.zulipFeatureLevel < 500) {
                            this.store.dispatch({ type: 'connectionChanged', status: 'faulted', reason: 'incompatible_after_restart' });
                            return;
                        }
                    } catch (error) {
                        if (error instanceof ZulipWebError && error.code !== 'realm_unavailable') {
                            this.store.dispatch({ type: 'connectionChanged', status: 'faulted', reason: 'probe_failed_after_restart' });
                            return;
                        }
                    }
                    await delay(jitteredBackoff(1_000, this.random), this.lifecycleController?.signal);
                } else {
                    this.store.dispatch({ type: 'connectionChanged', status: 'connected' });
                }
                backoffMs = 1_000;
            } catch (error) {
                timedSignal.dispose();
                if (isAbort(error) && (epoch !== this.lifecycleEpoch || this.lifecycleController?.signal.aborted)) {
                    return;
                }
                if (isAbort(error)) {
                    this.store.dispatch({ type: 'connectionChanged', status: 'offline', reason: 'long_poll_timeout' });
                    await delay(jitteredBackoff(backoffMs, this.random), this.lifecycleController?.signal);
                    backoffMs = Math.min(30_000, backoffMs * 2);
                    continue;
                }
                if (error instanceof ZulipWebError && error.code === 'unauthorized') {
                    await this.requireReauthentication();
                    return;
                }
                if (error instanceof ZulipWebError && error.code === 'queue_expired') {
                    this.queueId = undefined;
                    this.store.dispatch({ type: 'connectionChanged', status: 'reconnecting', reason: 'queue_expired' });
                    continue;
                }
                if (error instanceof ZulipWebError && error.code === 'rate_limited') {
                    this.store.dispatch({ type: 'connectionChanged', status: 'rateLimited' });
                    await delay(error.retryAfterMs ?? jitteredBackoff(backoffMs, this.random), this.lifecycleController?.signal);
                    backoffMs = Math.min(30_000, backoffMs * 2);
                    continue;
                }
                if (error instanceof ZulipWebError && error.code === 'network') {
                    this.store.dispatch({ type: 'connectionChanged', status: 'offline' });
                    await delay(jitteredBackoff(backoffMs, this.random), this.lifecycleController?.signal);
                    backoffMs = Math.min(30_000, backoffMs * 2);
                    continue;
                }
                this.store.dispatch({ type: 'connectionChanged', status: 'faulted', reason: 'event_loop_failed' });
                return;
            }
        }
    }

    private async markVisibleRead(
        conversation: ConversationKey,
        signal: AbortSignal,
        lifecycleEpoch: number,
        selectionEpoch: number,
    ): Promise<void> {
        const unreadMessages = messagesForConversation(this.store.getSnapshot(), conversation)
            .filter((message) => !message.isRead)
            .slice(-HISTORY_PAGE_SIZE);
        if (unreadMessages.length === 0) {
            return;
        }
        const anchor = Math.max(...unreadMessages.map((message) => message.id));
        try {
            await this.apiClient.markConversationRead(
                this.session,
                conversation,
                anchor,
                unreadMessages.length,
                signal,
            );
            if (this.isCurrentSelection(lifecycleEpoch, selectionEpoch, conversation)) {
                this.store.dispatch({
                    type: 'readConfirmed',
                    conversation,
                    messageIds: unreadMessages.map((message) => message.id),
                });
            }
        } catch (error) {
            if (!isAbort(error)) {
                await this.handleCommandError(error);
            }
        }
    }

    private async loadTopics(channelId: number, epoch: number): Promise<void> {
        try {
            const topics = await this.apiClient.getTopics(
                this.session,
                channelId,
                this.lifecycleController?.signal,
            );
            if (epoch === this.lifecycleEpoch) {
                this.store.dispatch({ type: 'topicsLoaded', topics });
            }
        } catch (error) {
            if (error instanceof ZulipWebError && error.code === 'unauthorized') {
                await this.requireReauthentication();
            }
        }
    }

    private async reconcileSentMessage(
        conversation: ConversationKey,
        localId: string,
        messageId: number,
        lifecycleEpoch: number,
    ): Promise<void> {
        try {
            const history = await this.apiClient.getHistory(
                this.session,
                conversation,
                messageId,
                true,
                1,
                this.lifecycleController?.signal,
            );
            const message = history.messages.find((candidate) => candidate.id === messageId);
            if (!message || lifecycleEpoch !== this.lifecycleEpoch || !this.store.getSnapshot().outbox[localId]) {
                return;
            }
            this.store.dispatch({
                type: 'eventsApplied',
                groups: [{ patches: [{ type: 'messageUpsert', message, localId }] }],
            });
            this.clearOutboxTimers(localId);
        } catch {
            // The event queue remains authoritative; reconciliation is one read-only attempt.
        }
    }

    private startOutboxTimers(localId: string, sendController: AbortController): void {
        const waitingTimer = window.setTimeout(() => {
            const entry = this.store.getSnapshot().outbox[localId];
            if (entry?.status === 'hidden') {
                this.store.dispatch({ type: 'outboxStatus', localId, status: 'waiting' });
            }
        }, OUTBOX_WAIT_MS);
        const expiryTimer = window.setTimeout(() => {
            const entry = this.store.getSnapshot().outbox[localId];
            if (entry && (entry.status === 'hidden' || entry.status === 'waiting')) {
                this.store.dispatch({ type: 'outboxStatus', localId, status: 'waitExpired' });
                sendController.abort(new DOMException('Send deadline elapsed', 'AbortError'));
            }
            this.clearOutboxTimers(localId);
        }, OUTBOX_EXPIRY_MS);
        this.outboxTimers.set(localId, [waitingTimer, expiryTimer]);
    }

    private clearOutboxTimers(localId: string): void {
        for (const timer of this.outboxTimers.get(localId) ?? []) {
            window.clearTimeout(timer);
        }
        this.outboxTimers.delete(localId);
    }

    private clearAllOutboxTimers(): void {
        for (const localId of this.outboxTimers.keys()) {
            this.clearOutboxTimers(localId);
        }
    }

    private isCurrentSelection(lifecycleEpoch: number, selectionEpoch: number, conversation: ConversationKey): boolean {
        return lifecycleEpoch === this.lifecycleEpoch
            && selectionEpoch === this.selectionEpoch
            && this.store.getSnapshot().selectedConversation?.canonicalKey === conversation.canonicalKey;
    }

    private async handleBootstrapError(error: unknown): Promise<void> {
        if (error instanceof ZulipWebError && error.code === 'unauthorized') {
            await this.requireReauthentication();
            return;
        }
        this.store.dispatch({
            type: 'connectionChanged',
            status: error instanceof ZulipWebError && error.code === 'network' ? 'offline' : 'faulted',
            reason: 'bootstrap_failed',
        });
    }

    private async handleCommandError(error: unknown): Promise<boolean> {
        if (error instanceof ZulipWebError && error.code === 'unauthorized') {
            await this.requireReauthentication();
            return true;
        }
        if (error instanceof ZulipWebError && error.code === 'rate_limited') {
            this.store.dispatch({ type: 'connectionChanged', status: 'rateLimited' });
        } else if (error instanceof ZulipWebError && error.code === 'network') {
            this.store.dispatch({ type: 'connectionChanged', status: 'offline' });
        }
        return false;
    }

    private async requireReauthentication(): Promise<void> {
        if (this.reauthenticationHandled) {
            return;
        }
        this.reauthenticationHandled = true;
        ++this.lifecycleEpoch;
        this.lifecycleController?.abort();
        this.selectionController?.abort();
        this.queueId = undefined;
        this.clearAllOutboxTimers();
        this.store.dispatch({ type: 'connectionChanged', status: 'reauthRequired' });
        this.onReauthenticationRequired();
    }
}

function chooseInitialConversation(state: ReturnType<WebClientStore['getSnapshot']>): ConversationKey | undefined {
    if (state.recentDirectMessages.length > 0) {
        return state.recentDirectMessages[0];
    }
    const topics = Object.values(state.topics).sort((left, right) => (right.maxMessageId ?? 0) - (left.maxMessageId ?? 0));
    if (topics.length > 0) {
        return channelTopic(topics[0].channelId, topics[0].topic);
    }
    return undefined;
}

function mapSendFailure(error: unknown): OutboxFailure {
    if (!(error instanceof ZulipWebError)) {
        return 'protocol';
    }
    switch (error.code) {
        case 'unauthorized':
            return 'reauthRequired';
        case 'rate_limited':
            return 'rateLimited';
        case 'network':
        case 'request_timed_out':
            return 'networkResultUnknown';
        case 'rejected':
            return 'rejected';
        default:
            return 'protocol';
    }
}

function safeOperationMessage(error: unknown): string {
    return error instanceof ZulipWebError ? error.message : '无法完成该操作。';
}

function sendFailureMessage(failure: OutboxFailure): string {
    switch (failure) {
        case 'networkResultUnknown': return '发送结果未知；不会自动重试。正文仍在输入区。';
        case 'reauthRequired': return '登录已失效，消息没有自动重试。';
        case 'rateLimited': return 'Realm 暂时限制发送，消息没有自动重试。';
        case 'rejected': return 'Realm 拒绝了这条消息。';
        default: return '消息没有发送，且不会自动重试。';
    }
}

function isAbort(error: unknown): boolean {
    return error instanceof DOMException && error.name === 'AbortError';
}

async function delay(milliseconds: number, signal?: AbortSignal): Promise<void> {
    await new Promise<void>((resolve, reject) => {
        if (signal?.aborted) {
            reject(new DOMException('Aborted', 'AbortError'));
            return;
        }
        const timer = window.setTimeout(resolve, Math.max(0, milliseconds));
        signal?.addEventListener('abort', () => {
            window.clearTimeout(timer);
            reject(new DOMException('Aborted', 'AbortError'));
        }, { once: true });
    });
}

function timeoutSignal(parent: AbortSignal, milliseconds: number) {
    const controller = new AbortController();
    const abortFromParent = () => controller.abort(parent.reason);
    parent.addEventListener('abort', abortFromParent, { once: true });
    const timer = window.setTimeout(() => controller.abort(new DOMException('Timed out', 'TimeoutError')), milliseconds);
    return {
        signal: controller.signal,
        dispose() {
            window.clearTimeout(timer);
            parent.removeEventListener('abort', abortFromParent);
        },
    };
}

function linkedAbortController(parent?: AbortSignal) {
    const controller = new AbortController();
    if (!parent) {
        return { controller, dispose() {} };
    }
    const abortFromParent = () => controller.abort(parent.reason);
    if (parent.aborted) {
        abortFromParent();
    } else {
        parent.addEventListener('abort', abortFromParent, { once: true });
    }
    return {
        controller,
        dispose() {
            parent.removeEventListener('abort', abortFromParent);
        },
    };
}

export function jitteredBackoff(baseMilliseconds: number, random: () => number): number {
    const capped = Math.max(0, Math.min(30_000, baseMilliseconds));
    const sample = Math.max(0, Math.min(1, random()));
    return Math.floor(capped * (0.8 + (sample * 0.2)));
}
