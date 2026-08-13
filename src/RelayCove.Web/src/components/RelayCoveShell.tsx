import { CSSProperties, useEffect, useMemo, useRef, useState } from 'react';
import type {
    AttachmentDraft,
    NavigationSection,
    ChatMessage,
    NewConversationRequest,
    ShellPresentation,
    SessionSummary,
    Theme,
    WorkspaceViewState,
} from '../models/ui';
import { ChatPanel } from './ChatPanel';
import { ConversationPane } from './ConversationPane';
import { DetailsPane } from './DetailsPane';
import { NavigationRail } from './NavigationRail';
import { ProductBar } from './ProductBar';
import { SettingsPage } from './SettingsPage';
import { readWebPreferences, writeWebPreferences } from '../session/WebPreferenceStore';
import { RealmMediaProvider, type RealmImageLoader } from './RealmMedia';
import { buildMessageQuote } from '../workspace/messageQuote';
import {
    attachmentKind,
    uploadedFileMarkdown,
    validateAttachmentSelection,
} from './attachmentDraft';

interface RelayCoveShellProps {
    session: SessionSummary;
    workspace: WorkspaceViewState;
    presentation: ShellPresentation;
    onLogout(): void;
    onSelectConversation?(conversationId: string): void;
    onLoadOlder?(conversationId: string): void;
    onSendMessage?(conversationId: string, content: string): Promise<void>;
    onRecoverPending?(localId: string): { conversationId: string; content: string } | undefined;
    onCreateConversation?(request: NewConversationRequest): void;
    loadRealmImage?: RealmImageLoader;
    allowCrossOriginMediaLoader?: boolean;
    onUploadAttachment?(file: File, signal: AbortSignal): Promise<{ url: string; filename: string }>;
    onToggleReaction?(
        messageId: string,
        reaction: { emojiName: string; emojiCode: string; reactionType: string },
        active: boolean,
    ): Promise<void>;
    onEditMessage?(messageId: string, content: string): Promise<void>;
    onDeleteMessage?(messageId: string): Promise<void>;
    onToggleStar?(messageId: string, starred: boolean): Promise<void>;
    onUnsubscribeChannel?(channelId: number): Promise<void>;
}

interface ShellStyle extends CSSProperties {
    '--conversation-width': string;
    '--base-font-size': string;
}

export function RelayCoveShell({
    session,
    workspace,
    presentation,
    onLogout,
    onSelectConversation,
    onLoadOlder,
    onSendMessage,
    onRecoverPending,
    onCreateConversation,
    loadRealmImage,
    allowCrossOriginMediaLoader,
    onUploadAttachment,
    onToggleReaction,
    onEditMessage,
    onDeleteMessage,
    onToggleStar,
    onUnsubscribeChannel,
}: RelayCoveShellProps) {
    const [initialPreferences] = useState(readWebPreferences);
    const [theme, setTheme] = useState<Theme>(initialPreferences.theme);
    const [activeSection, setActiveSection] = useState<NavigationSection>('messages');
    const [selectedId, setSelectedId] = useState(workspace.selectedConversationId);
    const [detailsOpen, setDetailsOpen] = useState(initialPreferences.detailsDefault);
    const [detailsDefault, setDetailsDefault] = useState(initialPreferences.detailsDefault);
    const [channelsCollapsed, setChannelsCollapsed] = useState(initialPreferences.channelsCollapsed);
    const [directsCollapsed, setDirectsCollapsed] = useState(initialPreferences.directsCollapsed);
    const [mobileChatOpen, setMobileChatOpen] = useState(Boolean(workspace.selectedConversationId));
    const [listWidth, setListWidth] = useState(initialPreferences.listWidth);
    const [fontSize, setFontSize] = useState(initialPreferences.fontSize);
    const [composerHeight, setComposerHeight] = useState(112);
    const [drafts, setDrafts] = useState<Record<string, string>>({});
    const [sendError, setSendError] = useState<string>();
    const [sending, setSending] = useState(false);
    const [composerFocusRequest, setComposerFocusRequest] = useState(0);
    const [attachmentDrafts, setAttachmentDrafts] = useState<Record<string, AttachmentDraft[]>>({});
    const attachmentDraftsRef = useRef(attachmentDrafts);
    const nextAttachmentIdRef = useRef(0);
    const uploadControllerRef = useRef<AbortController | undefined>(undefined);
    const operationEpochRef = useRef(0);
    const selectedConversation = selectedId ? workspace.conversations[selectedId] : undefined;
    const knownUnread = useMemo(
        () => [...workspace.channels, ...workspace.directs]
            .reduce((sum, conversation) => sum + conversation.unread, 0),
        [workspace.channels, workspace.directs],
    );
    const totalUnread = workspace.totalUnread ?? knownUnread;
    const shellStyle: ShellStyle = {
        '--conversation-width': `${listWidth}px`,
        '--base-font-size': `${fontSize}px`,
    };

    useEffect(() => {
        if (workspace.selectedConversationId !== undefined) {
            setSelectedId(workspace.selectedConversationId);
            setMobileChatOpen(true);
        } else if (onSelectConversation) {
            setSelectedId(undefined);
        }
    }, [onSelectConversation, workspace.selectedConversationId]);

    useEffect(() => {
        writeWebPreferences({
            theme,
            fontSize,
            listWidth,
            detailsDefault,
            channelsCollapsed,
            directsCollapsed,
        });
    }, [channelsCollapsed, detailsDefault, directsCollapsed, fontSize, listWidth, theme]);

    useEffect(() => {
        attachmentDraftsRef.current = attachmentDrafts;
    }, [attachmentDrafts]);

    useEffect(() => () => {
        operationEpochRef.current += 1;
        uploadControllerRef.current?.abort();
        for (const drafts of Object.values(attachmentDraftsRef.current)) {
            releaseAttachmentDrafts(drafts);
        }
        attachmentDraftsRef.current = {};
    }, []);

    function changeSection(section: NavigationSection) {
        setActiveSection(section);
        setDetailsOpen(false);
        if (section === 'messages') {
            setMobileChatOpen(false);
        }
    }

    function selectConversation(id: string) {
        setSelectedId(id);
        setActiveSection('messages');
        setMobileChatOpen(true);
        setSendError(undefined);
        onSelectConversation?.(id);
    }

    async function sendMessage() {
        if (!selectedId || !onSendMessage || sending) {
            return;
        }
        const snapshot = drafts[selectedId] ?? '';
        const attachmentSnapshot = [...(attachmentDrafts[selectedId] ?? [])];
        if (!snapshot.trim() && attachmentSnapshot.length === 0) {
            return;
        }
        setSending(true);
        setSendError(undefined);
        const operationEpoch = operationEpochRef.current;
        let phase: 'upload' | 'send' = attachmentSnapshot.every((draft) => draft.uploaded) ? 'send' : 'upload';
        try {
            const uploadedFiles: Array<{ url: string; filename: string }> = [];
            for (const attachment of attachmentSnapshot) {
                let uploaded = attachment.uploaded;
                if (!uploaded) {
                    if (!onUploadAttachment) {
                        throw new Error('当前客户端尚未启用附件上传。');
                    }
                    const controller = new AbortController();
                    uploadControllerRef.current = controller;
                    uploaded = await onUploadAttachment(attachment.file, controller.signal);
                    if (controller.signal.aborted || operationEpoch !== operationEpochRef.current) {
                        return;
                    }
                    const completedUpload = uploaded;
                    setAttachmentDrafts((current) => ({
                        ...current,
                        [selectedId]: (current[selectedId] ?? []).map((draft) => (
                            draft.id === attachment.id ? { ...draft, uploaded: completedUpload } : draft
                        )),
                    }));
                }
                uploadedFiles.push(uploaded);
            }
            if (operationEpoch !== operationEpochRef.current) {
                return;
            }
            phase = 'send';
            const content = [
                snapshot.trimEnd(),
                ...uploadedFiles.map(uploadedFileMarkdown),
            ].filter(Boolean).join('\n');
            await onSendMessage(selectedId, content);
            if (operationEpoch !== operationEpochRef.current) {
                return;
            }
            setDrafts((current) => current[selectedId] === snapshot
                ? { ...current, [selectedId]: '' }
                : current);
            if (attachmentSnapshot.length > 0) {
                const sentIds = new Set(attachmentSnapshot.map((draft) => draft.id));
                releaseAttachmentDrafts(attachmentSnapshot);
                setAttachmentDrafts((current) => {
                    const remaining = (current[selectedId] ?? []).filter((draft) => !sentIds.has(draft.id));
                    const next = { ...current };
                    if (remaining.length === 0) {
                        delete next[selectedId];
                    } else {
                        next[selectedId] = remaining;
                    }
                    return next;
                });
            }
        } catch (error) {
            if (operationEpoch !== operationEpochRef.current) {
                return;
            }
            setSendError(phase === 'upload'
                ? '附件上传结果未确认；不会自动重试。已确认的附件会保留，请检查连接后再明确发送。'
                : error instanceof Error ? error.message : '消息没有发送。');
        } finally {
            if (operationEpoch === operationEpochRef.current) {
                uploadControllerRef.current = undefined;
                setSending(false);
            }
        }
    }

    function logout() {
        operationEpochRef.current += 1;
        uploadControllerRef.current?.abort();
        uploadControllerRef.current = undefined;
        const draftsToRelease = attachmentDraftsRef.current;
        attachmentDraftsRef.current = {};
        for (const drafts of Object.values(draftsToRelease)) {
            releaseAttachmentDrafts(drafts);
        }
        setAttachmentDrafts({});
        setSending(false);
        onLogout();
    }

    function selectAttachments(files: readonly File[]) {
        if (!selectedId) {
            return;
        }
        const existing = attachmentDrafts[selectedId] ?? [];
        const validationError = validateAttachmentSelection(
            existing,
            files,
            presentation.maxAttachmentUploadBytes ?? 10 * 1024 * 1024,
        );
        if (validationError) {
            setSendError(validationError);
            return;
        }
        const additions = files.map((file): AttachmentDraft => {
            const kind = attachmentKind(file);
            return {
                id: `attachment-${++nextAttachmentIdRef.current}`,
                file,
                kind,
                previewUrl: kind === 'image' ? URL.createObjectURL(file) : undefined,
            };
        });
        setAttachmentDrafts((current) => ({
            ...current,
            [selectedId]: [...(current[selectedId] ?? []), ...additions],
        }));
        setSendError(undefined);
        setComposerHeight((current) => Math.max(current, 200));
    }

    function removeAttachment(attachmentId: string) {
        if (!selectedId) {
            return;
        }
        setAttachmentDrafts((current) => {
            const existing = current[selectedId] ?? [];
            const removed = existing.find((draft) => draft.id === attachmentId);
            if (!removed) {
                return current;
            }
            releaseAttachmentDrafts([removed]);
            const remaining = existing.filter((draft) => draft.id !== attachmentId);
            const next = { ...current };
            if (remaining.length === 0) {
                delete next[selectedId];
            } else {
                next[selectedId] = remaining;
            }
            return next;
        });
    }

    function recoverPending(localId: string) {
        const recovered = onRecoverPending?.(localId);
        if (!recovered) {
            return;
        }
        setDrafts((current) => ({ ...current, [recovered.conversationId]: recovered.content }));
        selectConversation(recovered.conversationId);
        setSendError('正文已恢复。再次发送前请确认，上一条消息的结果可能未知。');
    }

    function replyToMessage(message: ChatMessage) {
        if (!selectedId) {
            return;
        }
        const quote = buildMessageQuote(message);
        setDrafts((current) => ({
            ...current,
            [selectedId]: current[selectedId]?.trim()
                ? `${current[selectedId]}\n\n${quote}`
                : quote,
        }));
        setComposerFocusRequest((value) => value + 1);
    }

    function createConversation(request: NewConversationRequest) {
        setActiveSection('messages');
        setDetailsOpen(false);
        setMobileChatOpen(true);
        onCreateConversation?.(request);
    }

    return (
        <RealmMediaProvider
            loader={loadRealmImage}
            realm={session.realm}
            allowCrossOriginLoader={allowCrossOriginMediaLoader}
        >
        <div
            className={`relaycove-app${presentation.connectionNotice ? ' has-connection-banner' : ''}`}
            data-theme={theme}
            style={shellStyle}
        >
            <ProductBar workspaceName={workspace.workspaceName} theme={theme} onThemeChange={setTheme} />
            {presentation.connectionNotice && (
                <div className="connection-banner" role="status">{presentation.connectionNotice}</div>
            )}
            <div className={`shell-grid${detailsOpen ? ' has-details' : ''}${mobileChatOpen ? ' mobile-chat-open' : ''}`}>
                <NavigationRail
                    currentUser={workspace.currentUser}
                    activeSection={activeSection}
                    totalUnread={totalUnread}
                    onSelect={changeSection}
                />
                {activeSection === 'settings' ? (
                    <SettingsPage
                        session={session}
                        theme={theme}
                        fontSize={fontSize}
                        listWidth={listWidth}
                        detailsDefault={detailsDefault}
                        onThemeChange={setTheme}
                        onFontSizeChange={setFontSize}
                        onListWidthChange={setListWidth}
                        onDetailsDefaultChange={(value) => {
                            setDetailsDefault(value);
                            setDetailsOpen(value);
                        }}
                        onLogout={logout}
                    />
                ) : (
                    <>
                        <ConversationPane
                            section={activeSection}
                            channels={workspace.channels}
                            directs={workspace.directs}
                            contacts={workspace.contacts ?? []}
                            currentUser={workspace.currentUser}
                            subscribedChannels={workspace.subscribedChannels ?? []}
                            selectedId={selectedId}
                            searchEnabled={presentation.conversationSearchEnabled}
                            searchTitle={presentation.conversationSearchTitle}
                            dataSourceNotice={presentation.dataSourceNotice}
                            emptySearchText={presentation.emptySearchText}
                            onSelect={selectConversation}
                            onCreateConversation={onCreateConversation ? createConversation : undefined}
                            channelsCollapsed={channelsCollapsed}
                            directsCollapsed={directsCollapsed}
                            onChannelsCollapsedChange={setChannelsCollapsed}
                            onDirectsCollapsedChange={setDirectsCollapsed}
                        />
                        <ChatPanel
                            conversation={activeSection === 'messages' ? selectedConversation : undefined}
                            detailsOpen={detailsOpen}
                            composerStatusText={presentation.composerStatusText}
                            sendError={sendError}
                            sendEnabled={presentation.sendEnabled === true && Boolean(onSendMessage)}
                            sending={sending}
                            draft={selectedId ? drafts[selectedId] ?? '' : ''}
                            attachmentDrafts={selectedId ? attachmentDrafts[selectedId] ?? [] : []}
                            maxAttachmentUploadBytes={presentation.maxAttachmentUploadBytes ?? 10 * 1024 * 1024}
                            attachmentUploadEnabled={Boolean(onUploadAttachment)}
                            composerHeight={composerHeight}
                            onBack={() => setMobileChatOpen(false)}
                            onToggleDetails={() => setDetailsOpen((value) => !value)}
                            onDraftChange={(value) => {
                                if (selectedId) {
                                    setDrafts((current) => ({ ...current, [selectedId]: value }));
                                }
                            }}
                            onComposerHeightChange={setComposerHeight}
                            onSend={() => void sendMessage()}
                            onAttachmentsSelected={selectAttachments}
                            onAttachmentRemoved={removeAttachment}
                            onAttachmentError={setSendError}
                            onLoadOlder={() => selectedId && onLoadOlder?.(selectedId)}
                            onRecoverPending={recoverPending}
                            onReply={replyToMessage}
                            onToggleReaction={onToggleReaction}
                            onEditMessage={onEditMessage}
                            onDeleteMessage={onDeleteMessage}
                            onToggleStar={onToggleStar}
                            maxMessageLength={presentation.maxMessageLength}
                            composerFocusRequest={composerFocusRequest}
                        />
                        {detailsOpen && (
                            <DetailsPane
                                conversation={selectedConversation}
                                onClose={() => setDetailsOpen(false)}
                                onUnsubscribeChannel={onUnsubscribeChannel}
                            />
                        )}
                    </>
                )}
            </div>
        </div>
        </RealmMediaProvider>
    );
}

function releaseAttachmentDrafts(drafts: readonly AttachmentDraft[]): void {
    for (const draft of drafts) {
        if (draft.previewUrl) {
            URL.revokeObjectURL(draft.previewUrl);
        }
    }
}
