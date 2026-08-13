import type { AttachmentDraft, ChatMessage, ConversationDetail } from '../models/ui';
import { ChatHeader } from './ChatHeader';
import { Composer } from './Composer';
import { MessageList } from './MessageList';

interface ChatPanelProps {
    conversation?: ConversationDetail;
    detailsOpen: boolean;
    composerStatusText: string;
    sendError?: string;
    sendEnabled: boolean;
    sending: boolean;
    draft: string;
    composerHeight: number;
    onBack(): void;
    onToggleDetails(): void;
    onDraftChange(value: string): void;
    onComposerHeightChange(height: number): void;
    onSend(): void;
    onLoadOlder(): void;
    onRecoverPending(localId: string): void;
    onReply(message: ChatMessage): void;
    onToggleReaction?(
        messageId: string,
        reaction: { emojiName: string; emojiCode: string; reactionType: string },
        active: boolean,
    ): Promise<void>;
    onEditMessage?(messageId: string, content: string): Promise<void>;
    onDeleteMessage?(messageId: string): Promise<void>;
    onToggleStar?(messageId: string, starred: boolean): Promise<void>;
    maxMessageLength?: number;
    composerFocusRequest: number;
    attachmentDrafts: readonly AttachmentDraft[];
    maxAttachmentUploadBytes: number;
    attachmentUploadEnabled: boolean;
    onAttachmentsSelected(files: readonly File[]): void;
    onAttachmentRemoved(attachmentId: string): void;
    onAttachmentError(message: string): void;
}

export function ChatPanel(props: ChatPanelProps) {
    return (
        <main className="chat-panel">
            <ChatHeader
                conversation={props.conversation}
                detailsOpen={props.detailsOpen}
                onBack={props.onBack}
                onToggleDetails={props.onToggleDetails}
            />
            <MessageList
                conversation={props.conversation}
                onLoadOlder={props.onLoadOlder}
                onRecoverPending={props.onRecoverPending}
                onReply={props.onReply}
                onToggleReaction={props.onToggleReaction}
                onEditMessage={props.onEditMessage}
                onDeleteMessage={props.onDeleteMessage}
                onToggleStar={props.onToggleStar}
                maxMessageLength={props.maxMessageLength}
            />
            <Composer
                conversationTitle={props.conversation?.title}
                statusText={props.composerStatusText}
                errorText={props.sendError}
                sendEnabled={props.sendEnabled}
                sending={props.sending}
                value={props.draft}
                height={props.composerHeight}
                onChange={props.onDraftChange}
                onHeightChange={props.onComposerHeightChange}
                onSend={props.onSend}
                focusRequest={props.composerFocusRequest}
                attachmentDrafts={props.attachmentDrafts}
                maxAttachmentUploadBytes={props.maxAttachmentUploadBytes}
                attachmentUploadEnabled={props.attachmentUploadEnabled}
                onAttachmentsSelected={props.onAttachmentsSelected}
                onAttachmentRemoved={props.onAttachmentRemoved}
                onAttachmentError={props.onAttachmentError}
            />
        </main>
    );
}
