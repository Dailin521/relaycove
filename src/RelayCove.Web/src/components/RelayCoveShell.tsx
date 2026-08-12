import { CSSProperties, useEffect, useMemo, useRef, useState } from 'react';
import type {
    NavigationSection,
    ChatMessage,
    ImageDraft,
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
    onUploadImage?(file: File, signal: AbortSignal): Promise<{ url: string; filename: string }>;
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
    onUploadImage,
}: RelayCoveShellProps) {
    const [initialPreferences] = useState(readWebPreferences);
    const [theme, setTheme] = useState<Theme>(initialPreferences.theme);
    const [activeSection, setActiveSection] = useState<NavigationSection>('messages');
    const [selectedId, setSelectedId] = useState(workspace.selectedConversationId);
    const [detailsOpen, setDetailsOpen] = useState(initialPreferences.detailsDefault);
    const [detailsDefault, setDetailsDefault] = useState(initialPreferences.detailsDefault);
    const [mobileChatOpen, setMobileChatOpen] = useState(Boolean(workspace.selectedConversationId));
    const [listWidth, setListWidth] = useState(initialPreferences.listWidth);
    const [fontSize, setFontSize] = useState(initialPreferences.fontSize);
    const [composerHeight, setComposerHeight] = useState(112);
    const [drafts, setDrafts] = useState<Record<string, string>>({});
    const [sendError, setSendError] = useState<string>();
    const [sending, setSending] = useState(false);
    const [composerFocusRequest, setComposerFocusRequest] = useState(0);
    const [imageDrafts, setImageDrafts] = useState<Record<string, ImageDraft>>({});
    const imageDraftsRef = useRef(imageDrafts);
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
        writeWebPreferences({ theme, fontSize, listWidth, detailsDefault });
    }, [detailsDefault, fontSize, listWidth, theme]);

    useEffect(() => {
        imageDraftsRef.current = imageDrafts;
    }, [imageDrafts]);

    useEffect(() => () => {
        operationEpochRef.current += 1;
        uploadControllerRef.current?.abort();
        for (const draft of Object.values(imageDraftsRef.current)) {
            URL.revokeObjectURL(draft.previewUrl);
        }
        imageDraftsRef.current = {};
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
        const imageSnapshot = imageDrafts[selectedId];
        if (!snapshot.trim() && !imageSnapshot) {
            return;
        }
        setSending(true);
        setSendError(undefined);
        const operationEpoch = operationEpochRef.current;
        let phase: 'upload' | 'send' = imageSnapshot?.uploaded ? 'send' : 'upload';
        try {
            let uploaded = imageSnapshot?.uploaded;
            if (imageSnapshot && !uploaded) {
                if (!onUploadImage) {
                    throw new Error('当前客户端尚未启用图片上传。');
                }
                const controller = new AbortController();
                uploadControllerRef.current = controller;
                uploaded = await onUploadImage(imageSnapshot.file, controller.signal);
                if (controller.signal.aborted || operationEpoch !== operationEpochRef.current) {
                    return;
                }
                setImageDrafts((current) => current[selectedId]?.file === imageSnapshot.file
                    ? { ...current, [selectedId]: { ...current[selectedId], uploaded } }
                    : current);
            }
            if (operationEpoch !== operationEpochRef.current) {
                return;
            }
            phase = 'send';
            const content = uploaded
                ? [snapshot.trimEnd(), `[${uploaded.filename}](${uploaded.url})`].filter(Boolean).join('\n')
                : snapshot;
            await onSendMessage(selectedId, content);
            if (operationEpoch !== operationEpochRef.current) {
                return;
            }
            setDrafts((current) => current[selectedId] === snapshot
                ? { ...current, [selectedId]: '' }
                : current);
            if (imageSnapshot) {
                setImageDrafts((current) => {
                    if (current[selectedId]?.file !== imageSnapshot.file) {
                        return current;
                    }
                    URL.revokeObjectURL(current[selectedId].previewUrl);
                    const next = { ...current };
                    delete next[selectedId];
                    return next;
                });
            }
        } catch (error) {
            if (operationEpoch !== operationEpochRef.current) {
                return;
            }
            setSendError(phase === 'upload'
                ? '图片上传结果未确认；不会自动重试。请检查连接后再明确发送。'
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
        const imageDraftsToRelease = imageDraftsRef.current;
        imageDraftsRef.current = {};
        for (const draft of Object.values(imageDraftsToRelease)) {
            URL.revokeObjectURL(draft.previewUrl);
        }
        setImageDrafts({});
        setSending(false);
        onLogout();
    }

    function selectImage(file: File) {
        if (!selectedId) {
            return;
        }
        const previewUrl = URL.createObjectURL(file);
        setImageDrafts((current) => {
            const existing = current[selectedId];
            if (existing) {
                URL.revokeObjectURL(existing.previewUrl);
            }
            return { ...current, [selectedId]: { file, previewUrl } };
        });
        setSendError(undefined);
        setComposerHeight((current) => Math.max(current, 180));
    }

    function removeImage() {
        if (!selectedId) {
            return;
        }
        setImageDrafts((current) => {
            const existing = current[selectedId];
            if (!existing) {
                return current;
            }
            URL.revokeObjectURL(existing.previewUrl);
            const next = { ...current };
            delete next[selectedId];
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
        const fallback = message.attachments?.length
            ? `[图片：${message.attachments.map((attachment) => attachment.name).join('、')}]`
            : '';
        const quotedContent = (message.body || fallback || '消息').split(/\r?\n/gu)
            .map((line) => `> ${line}`)
            .join('\n');
        const quote = `> ${message.sender.name}：\n${quotedContent}\n\n`;
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
        <RealmMediaProvider loader={loadRealmImage}>
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
                        />
                        <ChatPanel
                            conversation={activeSection === 'messages' ? selectedConversation : undefined}
                            detailsOpen={detailsOpen}
                            composerStatusText={presentation.composerStatusText}
                            sendError={sendError}
                            sendEnabled={presentation.sendEnabled === true && Boolean(onSendMessage)}
                            sending={sending}
                            draft={selectedId ? drafts[selectedId] ?? '' : ''}
                            imageDraft={selectedId ? imageDrafts[selectedId] : undefined}
                            maxImageUploadBytes={presentation.maxImageUploadBytes ?? 10 * 1024 * 1024}
                            imageUploadEnabled={Boolean(onUploadImage)}
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
                            onImageSelected={selectImage}
                            onImageRemoved={removeImage}
                            onImageError={setSendError}
                            onLoadOlder={() => selectedId && onLoadOlder?.(selectedId)}
                            onRecoverPending={recoverPending}
                            onReply={replyToMessage}
                            composerFocusRequest={composerFocusRequest}
                        />
                        {detailsOpen && (
                            <DetailsPane conversation={selectedConversation} onClose={() => setDetailsOpen(false)} />
                        )}
                    </>
                )}
            </div>
        </div>
        </RealmMediaProvider>
    );
}
