import { useCallback, useEffect, useMemo, useState, useSyncExternalStore } from 'react';
import type { WebSession } from './api/types';
import { LoginPage } from './components/LoginPage';
import { RelayCoveShell } from './components/RelayCoveShell';
import type { ConversationKey } from './domain/types';
import { channelTopic, directMessage } from './domain/conversation';
import { WebAuthService } from './session/WebAuthService';
import { WebClientSession } from './session/WebClientSession';
import { projectWebClient } from './workspace/projectWorkspace';

export function App() {
    const authService = useMemo(() => new WebAuthService(), []);
    const [session, setSession] = useState<WebSession | null>(() => authService.restore());

    if (!session) {
        return (
            <LoginPage
                login={(request, signal) => authService.login(request, signal)}
                onAuthenticated={setSession}
            />
        );
    }

    return (
        <AuthenticatedApp
            session={session}
            onCredentialsInvalid={() => {
                authService.logout();
                setSession(null);
            }}
            onLogout={async (client) => {
                await client.stop(true);
                authService.logout();
                setSession(null);
            }}
        />
    );
}

interface AuthenticatedAppProps {
    session: WebSession;
    onCredentialsInvalid(): void;
    onLogout(client: WebClientSession): Promise<void>;
}

function AuthenticatedApp({ session, onCredentialsInvalid, onLogout }: AuthenticatedAppProps) {
    const client = useMemo(() => new WebClientSession(session, {
        onReauthenticationRequired: onCredentialsInvalid,
    }), [onCredentialsInvalid, session]);
    const state = useSyncExternalStore(
        client.store.subscribe,
        client.store.getSnapshot,
        client.store.getSnapshot,
    );
    const projected = useMemo(() => projectWebClient(session, state), [session, state]);
    const loadRealmImage = useCallback((sourceUrl: string, kind: 'avatar' | 'upload', signal: AbortSignal) => (
        client.loadRealmImage(sourceUrl, kind, signal)
    ), [client]);

    useEffect(() => {
        void client.start();
        return () => {
            void client.stop(false);
        };
    }, [client]);

    function resolveConversation(conversationId: string): ConversationKey | undefined {
        if (state.selectedConversation?.canonicalKey === conversationId) {
            return state.selectedConversation;
        }
        const topic = state.topics[conversationId];
        if (topic) {
            return {
                kind: 'channel',
                channelId: topic.channelId,
                topic: topic.topic,
                canonicalKey: conversationId,
            };
        }
        const direct = state.recentDirectMessages.find((item) => item.canonicalKey === conversationId);
        if (direct) {
            return direct;
        }
        return Object.values(state.messages).find((message) => (
            message.conversation.canonicalKey === conversationId
        ))?.conversation;
    }

    return (
        <RelayCoveShell
            session={{ realm: session.realm, email: session.email }}
            workspace={projected.workspace}
            presentation={projected.presentation}
            loadRealmImage={loadRealmImage}
            onUploadImage={(file, signal) => client.uploadImage(file, signal)}
            onSelectConversation={(conversationId) => {
                const conversation = resolveConversation(conversationId);
                if (conversation) {
                    void client.selectConversation(conversation);
                }
            }}
            onLoadOlder={(conversationId) => {
                const conversation = resolveConversation(conversationId);
                if (conversation) {
                    void client.loadOlder(conversation);
                }
            }}
            onSendMessage={async (conversationId, content) => {
                const conversation = resolveConversation(conversationId);
                if (!conversation) {
                    throw new Error('该会话已不可用，消息没有发送。');
                }
                await client.send(conversation, content);
            }}
            onRecoverPending={(localId) => {
                const recovered = client.recoverOutbox(localId);
                return recovered ? {
                    conversationId: recovered.conversation.canonicalKey,
                    content: recovered.content,
                } : undefined;
            }}
            onCreateConversation={(request) => {
                const conversation = request.kind === 'channel'
                    ? channelTopic(request.channelId, request.topic)
                    : directMessage(request.userIds, session.userId);
                void client.selectConversation(conversation);
            }}
            onLogout={() => {
                void onLogout(client);
            }}
        />
    );
}
