import type { ChatMessage, ConversationDetail, ImageDraft } from '../models/ui';
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
    composerFocusRequest: number;
    imageDraft?: ImageDraft;
    maxImageUploadBytes: number;
    imageUploadEnabled: boolean;
    onImageSelected(file: File): void;
    onImageRemoved(): void;
    onImageError(message: string): void;
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
                imageDraft={props.imageDraft}
                maxImageUploadBytes={props.maxImageUploadBytes}
                imageUploadEnabled={props.imageUploadEnabled}
                onImageSelected={props.onImageSelected}
                onImageRemoved={props.onImageRemoved}
                onImageError={props.onImageError}
            />
        </main>
    );
}
