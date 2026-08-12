export type Theme = 'light' | 'dark';
export type NavigationSection = 'messages' | 'contacts' | 'saved' | 'settings';
export type ConversationKind = 'channel' | 'direct' | 'group' | 'self';

export interface PersonSummary {
    id: string;
    name: string;
    initials: string;
    tone: 'blue' | 'green' | 'orange' | 'violet' | 'slate';
    avatarUrl?: string;
    isBot?: boolean;
}

export interface ConversationSummary {
    id: string;
    kind: ConversationKind;
    title: string;
    subtitle: string;
    time: string;
    unread: number;
    pinned?: boolean;
    online?: boolean;
    channelName?: string;
    topic?: string;
    avatar: string;
    tone: PersonSummary['tone'];
    avatarUrl?: string;
    isBot?: boolean;
}

export interface MessageAttachment {
    kind: 'image';
    name: string;
    sourceUrl: string;
}

export interface ImageDraft {
    file: File;
    previewUrl: string;
    uploaded?: {
        url: string;
        filename: string;
    };
}

export interface ChatMessage {
    id: string;
    sender: PersonSummary;
    sentAt: string;
    body: string;
    rawContent?: string;
    permalink?: string;
    attachments?: MessageAttachment[];
    own?: boolean;
    quote?: {
        sender: string;
        body: string;
    };
    reaction?: string;
}

export interface PendingMessage {
    localId: string;
    body: string;
    status: 'hidden' | 'waiting' | 'waitExpired' | 'failed';
    statusText: string;
    recoverable: boolean;
}

export interface ConversationDetail extends ConversationSummary {
    messages: ChatMessage[];
    pendingMessages?: PendingMessage[];
    loading?: boolean;
    foundOldest?: boolean;
    loadError?: string;
    dateLabel?: string;
    unreadSeparatorAfter?: number;
    unreadSeparatorText?: string;
}

export interface WorkspaceViewState {
    workspaceName: string;
    currentUser: PersonSummary;
    channels: ConversationSummary[];
    directs: ConversationSummary[];
    contacts?: PersonSummary[];
    subscribedChannels?: Array<{ channelId: number; name: string }>;
    conversations: Record<string, ConversationDetail>;
    selectedConversationId?: string;
    totalUnread?: number;
}

export type NewConversationRequest =
    | { kind: 'dm'; userIds: number[] }
    | { kind: 'channel'; channelId: number; topic: string };

export interface SessionSummary {
    realm: string;
    email: string;
}

export interface ShellPresentation {
    conversationSearchEnabled: boolean;
    conversationSearchTitle?: string;
    dataSourceNotice?: string;
    emptySearchText: string;
    composerStatusText: string;
    connectionNotice?: string;
    sendEnabled?: boolean;
    maxImageUploadBytes?: number;
}
