import { Copy, MoreHorizontal, Pencil, Reply, SmilePlus, Star } from 'lucide-react';
import { type KeyboardEvent, type MouseEvent, useCallback, useEffect, useState } from 'react';
import type { ChatMessage, ConversationDetail, MessageReaction } from '../models/ui';
import { Avatar } from './Avatar';
import { copyText, MessageContextMenu } from './MessageContextMenu';
import { ImageViewer, MessageImage, type OpenImage } from './MessageImage';
import { MessageFile } from './MessageFile';
import { EmojiPicker, type EmojiChoice } from './EmojiPicker';
import { MessageDeleteDialog, MessageEditDialog } from './MessageMutationDialogs';

interface OpenMenu {
    message: ChatMessage;
    x: number;
    y: number;
    trigger: HTMLElement;
}

interface MessageOverlay {
    message: ChatMessage;
    trigger: HTMLElement;
}

function Message({
    message,
    onOpenMenu,
    onOpenImage,
    onReply,
    onAnnounce,
    menuOpen,
    onOpenReaction,
    onToggleReaction,
    onToggleStar,
    onEdit,
}: {
    message: ChatMessage;
    onOpenMenu(menu: OpenMenu): void;
    onOpenImage(image: OpenImage): void;
    onReply(message: ChatMessage): void;
    onAnnounce(message: string): void;
    menuOpen: boolean;
    onOpenReaction?(message: ChatMessage, trigger: HTMLElement): void;
    onToggleReaction?(message: ChatMessage, reaction: MessageReaction, active: boolean): Promise<void>;
    onToggleStar?(message: ChatMessage): Promise<void>;
    onEdit?(message: ChatMessage, trigger: HTMLElement): void;
}) {
    const mutationPending = message.mutation !== undefined && message.mutation.phase !== 'failed';
    function openContextMenu(event: MouseEvent<HTMLElement>) {
        event.preventDefault();
        onOpenMenu({ message, x: event.clientX, y: event.clientY, trigger: event.currentTarget });
    }

    function handleKeyDown(event: KeyboardEvent<HTMLElement>) {
        if (event.key !== 'ContextMenu' && !(event.key === 'F10' && event.shiftKey)) {
            return;
        }
        event.preventDefault();
        const bounds = event.currentTarget.getBoundingClientRect();
        onOpenMenu({
            message,
            x: bounds.left + Math.min(bounds.width, 220),
            y: bounds.top + 34,
            trigger: event.currentTarget,
        });
    }

    async function copyMessage() {
        try {
            await copyText(message.rawContent ?? message.body);
            onAnnounce('已复制消息正文。');
        } catch {
            onAnnounce('复制失败，请使用“更多”菜单重试。');
        }
    }

    return (
        <article
            className={`${message.own ? 'message-row is-own' : 'message-row'}${message.mutation ? ` has-mutation is-${message.mutation.phase}` : ''}`}
            tabIndex={0}
            data-message-id={message.id}
            aria-busy={mutationPending}
            onContextMenu={openContextMenu}
            onKeyDown={handleKeyDown}
        >
            <Avatar
                label={message.sender.name}
                initials={message.sender.initials}
                tone={message.sender.tone}
                avatarUrl={message.sender.avatarUrl}
                isBot={message.sender.isBot}
            />
            <div className="message-stack">
                <div className="message-sender">
                    <strong>{message.own ? '你' : message.sender.name}</strong>
                    <time>{message.sentAt}</time>
                    {message.isStarred && <Star className="message-starred-mark" aria-label="已收藏" fill="currentColor" />}
                    <span className={menuOpen ? 'message-actions is-open' : 'message-actions'} aria-label="消息快捷操作">
                        {!message.own && onOpenReaction && (
                            <button
                                type="button"
                                disabled={mutationPending}
                                aria-label={`给 ${message.sender.name} 的消息添加反应`}
                                title="添加表情反应"
                                onClick={(event) => onOpenReaction(message, event.currentTarget)}
                            >
                                <SmilePlus aria-hidden="true" />
                            </button>
                        )}
                        {message.own && onEdit && (
                            <button
                                type="button"
                                disabled={mutationPending}
                                aria-label={`编辑 ${message.sender.name} 的消息`}
                                title="编辑消息"
                                onClick={(event) => onEdit(message, event.currentTarget)}
                            >
                                <Pencil aria-hidden="true" />
                            </button>
                        )}
                        {onToggleStar && (
                            <button
                                type="button"
                                disabled={mutationPending}
                                aria-label={message.isStarred ? '取消收藏消息' : '收藏消息'}
                                aria-pressed={message.isStarred === true}
                                title={message.isStarred ? '取消收藏' : '收藏'}
                                onClick={() => void onToggleStar(message)}
                            >
                                <Star aria-hidden="true" fill={message.isStarred ? 'currentColor' : 'none'} />
                            </button>
                        )}
                        <button
                            type="button"
                            aria-label={`引用 ${message.sender.name} 的消息`}
                            title="引用回复"
                            onClick={() => onReply(message)}
                        >
                            <Reply aria-hidden="true" />
                        </button>
                        <button
                            type="button"
                            aria-label={`复制 ${message.sender.name} 的消息`}
                            title="复制正文"
                            onClick={() => void copyMessage()}
                        >
                            <Copy aria-hidden="true" />
                        </button>
                        <button
                            className="message-more-button"
                            type="button"
                            aria-label={`更多消息操作：${message.sender.name} ${message.sentAt}`}
                            aria-haspopup="menu"
                            aria-expanded={menuOpen}
                            title="更多操作"
                            onClick={(event) => {
                                const bounds = event.currentTarget.getBoundingClientRect();
                                onOpenMenu({
                                    message,
                                    x: message.own ? bounds.left : bounds.right,
                                    y: bounds.bottom + 4,
                                    trigger: event.currentTarget,
                                });
                            }}
                        >
                            <MoreHorizontal aria-hidden="true" />
                        </button>
                    </span>
                </div>
                <div className="message-bubble">
                    {message.quote && (
                        <blockquote className="message-quote">
                            <header>
                                <Reply aria-hidden="true" />
                                <strong>{message.quote.sender}</strong>
                                <span>写道</span>
                            </header>
                            <p>{message.quote.body}</p>
                        </blockquote>
                    )}
                    {message.body && <p>{message.body}</p>}
                    {message.attachments && message.attachments.length > 0 && (
                        <div className="message-attachments">
                            {message.attachments.map((attachment) => attachment.kind === 'image' ? (
                                <MessageImage
                                    key={attachment.sourceUrl}
                                    attachment={attachment}
                                    onOpen={onOpenImage}
                                />
                            ) : (
                                <MessageFile key={attachment.sourceUrl} attachment={attachment} />
                            ))}
                        </div>
                    )}
                </div>
                {message.reactions && message.reactions.length > 0 && (
                    <div className="message-reactions" aria-label="消息反应">
                        {message.reactions.map((reaction) => (
                            <button
                                key={`${reaction.reactionType}:${reaction.emojiCode}`}
                                type="button"
                                disabled={mutationPending || !onToggleReaction}
                                aria-pressed={reaction.reactedByCurrentUser}
                                aria-label={`${reaction.emojiName}，${reaction.count} 人${reaction.reactedByCurrentUser ? '，你已添加' : ''}`}
                                onClick={() => void onToggleReaction?.(message, reaction, !reaction.reactedByCurrentUser)}
                            >
                                <span>{reaction.emoji}</span><strong>{reaction.count}</strong>
                            </button>
                        ))}
                    </div>
                )}
                {message.mutation && (
                    <p className={`message-mutation-status is-${message.mutation.phase}`} role={message.mutation.error ? 'alert' : 'status'}>
                        {message.mutation.error ?? mutationStatus(message.mutation.kind)}
                    </p>
                )}
            </div>
        </article>
    );
}

interface MessageListProps {
    conversation?: ConversationDetail;
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
}

export function MessageList({
    conversation,
    onLoadOlder,
    onRecoverPending,
    onReply,
    onToggleReaction,
    onEditMessage,
    onDeleteMessage,
    onToggleStar,
    maxMessageLength,
}: MessageListProps) {
    const [openMenu, setOpenMenu] = useState<OpenMenu>();
    const [openImage, setOpenImage] = useState<OpenImage>();
    const [reactionPicker, setReactionPicker] = useState<MessageOverlay>();
    const [editDialog, setEditDialog] = useState<MessageOverlay>();
    const [deleteDialog, setDeleteDialog] = useState<MessageOverlay>();
    const [announcement, setAnnouncement] = useState('');
    const closeMenu = useCallback((restoreFocus = true) => {
        const trigger = openMenu?.trigger;
        setOpenMenu(undefined);
        if (restoreFocus) {
            window.requestAnimationFrame(() => trigger?.focus());
        }
    }, [openMenu]);
    const closeImage = useCallback(() => {
        const source = openImage?.source;
        setOpenImage(undefined);
        window.requestAnimationFrame(() => source?.focus());
    }, [openImage]);

    useEffect(() => {
        setOpenMenu(undefined);
        setOpenImage(undefined);
        setReactionPicker(undefined);
        setEditDialog(undefined);
        setDeleteDialog(undefined);
    }, [conversation?.id]);

    function closeOverlay(
        overlay: MessageOverlay | undefined,
        setter: (value: MessageOverlay | undefined) => void,
        restoreFocus = true,
    ) {
        setter(undefined);
        if (restoreFocus) {
            window.requestAnimationFrame(() => overlay?.trigger.focus());
        }
    }

    async function toggleStar(message: ChatMessage) {
        if (!onToggleStar) {
            return;
        }
        try {
            await onToggleStar(message.id, !message.isStarred);
            setAnnouncement(message.isStarred ? '已取消收藏。' : '已收藏消息。');
        } catch (error) {
            setAnnouncement(error instanceof Error ? error.message : '收藏操作失败。');
        }
    }

    async function toggleReaction(message: ChatMessage, reaction: MessageReaction, active: boolean) {
        if (!onToggleReaction) {
            return;
        }
        try {
            await onToggleReaction(message.id, {
                emojiName: reaction.emojiName,
                emojiCode: reaction.emojiCode,
                reactionType: reaction.reactionType,
            }, active);
            setAnnouncement(active ? '已添加表情反应。' : '已移除表情反应。');
        } catch (error) {
            setAnnouncement(error instanceof Error ? error.message : '表情反应操作失败。');
        }
    }

    async function chooseReaction(choice: EmojiChoice) {
        const overlay = reactionPicker;
        if (!overlay || !onToggleReaction) {
            return;
        }
        const existing = overlay.message.reactions?.find((reaction) => (
            reaction.reactionType === choice.reactionType && reaction.emojiCode === choice.emojiCode
        ));
        closeOverlay(overlay, setReactionPicker, false);
        try {
            await onToggleReaction(overlay.message.id, {
                emojiName: choice.emojiName,
                emojiCode: choice.emojiCode,
                reactionType: choice.reactionType,
            }, !existing?.reactedByCurrentUser);
            setAnnouncement(existing?.reactedByCurrentUser ? '已移除表情反应。' : '已添加表情反应。');
        } catch (error) {
            setAnnouncement(error instanceof Error ? error.message : '表情反应操作失败。');
        } finally {
            window.requestAnimationFrame(() => overlay.trigger.focus());
        }
    }

    if (!conversation) {
        return (
            <div className="message-empty" role="status">
                <span className="empty-mark" aria-hidden="true">R</span>
                <strong>选择一个会话</strong>
                <p>频道话题与私信会从当前 Zulip Realm 加载。</p>
            </div>
        );
    }

    return (
        <div className="message-list" aria-label={`${conversation.title} 的消息`}>
            {conversation.foundOldest === false && (
                <button
                    className="load-older-button"
                    type="button"
                    disabled={conversation.loading}
                    onClick={onLoadOlder}
                >
                    {conversation.loading ? '正在加载…' : '加载更早消息'}
                </button>
            )}
            {conversation.loadError && <p className="message-load-error" role="alert">{conversation.loadError}</p>}
            {conversation.dateLabel && <div className="date-separator"><span>{conversation.dateLabel}</span></div>}
            {conversation.messages.length === 0 && conversation.loading && (
                <div className="message-loading" role="status" aria-label="正在加载聊天记录">
                    <span /><span /><span />
                </div>
            )}
            {conversation.messages.length === 0 && !conversation.loading && (
                <div className="conversation-message-empty" role="status">这个会话还没有可显示的消息。</div>
            )}
            {conversation.messages.map((message, index) => (
                <div key={message.id} className="message-entry">
                    <Message
                        message={message}
                        menuOpen={openMenu?.message.id === message.id}
                        onOpenMenu={setOpenMenu}
                        onOpenImage={setOpenImage}
                        onReply={onReply}
                        onAnnounce={setAnnouncement}
                        onOpenReaction={onToggleReaction ? (item, trigger) => setReactionPicker({ message: item, trigger }) : undefined}
                        onToggleReaction={onToggleReaction ? toggleReaction : undefined}
                        onToggleStar={onToggleStar ? toggleStar : undefined}
                        onEdit={onEditMessage ? (item, trigger) => setEditDialog({ message: item, trigger }) : undefined}
                    />
                    {conversation.unreadSeparatorAfter === index + 1 && (
                        <div className="unread-separator">
                            <span>{conversation.unreadSeparatorText ?? `${conversation.unread} 条未读消息`}</span>
                        </div>
                    )}
                </div>
            ))}
            {conversation.pendingMessages?.map((pending) => (
                <article className="message-row is-own pending-message" key={pending.localId}>
                    <div className="message-stack">
                        <div className="message-sender"><strong>你</strong><time>{pending.statusText}</time></div>
                        <div className="message-bubble"><p>{pending.body}</p></div>
                        {pending.recoverable && (
                            <button type="button" className="recover-message-button" onClick={() => onRecoverPending(pending.localId)}>
                                恢复正文
                            </button>
                        )}
                    </div>
                </article>
            ))}
            <p className="sr-only" aria-live="polite">{announcement}</p>
            {openMenu && (
                <MessageContextMenu
                    message={openMenu.message}
                    x={openMenu.x}
                    y={openMenu.y}
                    onReply={onReply}
                    onOpenReaction={onToggleReaction ? (message) => {
                        const trigger = openMenu.trigger;
                        setReactionPicker({ message, trigger });
                    } : undefined}
                    onToggleStar={onToggleStar ? (message) => void toggleStar(message) : undefined}
                    onEdit={onEditMessage ? (message) => setEditDialog({ message, trigger: openMenu.trigger }) : undefined}
                    onDelete={onDeleteMessage ? (message) => setDeleteDialog({ message, trigger: openMenu.trigger }) : undefined}
                    onClose={closeMenu}
                    onAnnounce={setAnnouncement}
                />
            )}
            {reactionPicker && (
                <EmojiPicker
                    trigger={reactionPicker.trigger}
                    title="添加表情反应"
                    description="选择后同步到 Zulip"
                    ariaLabel="选择消息反应"
                    onSelect={(choice) => void chooseReaction(choice)}
                    onClose={(restoreFocus = true) => closeOverlay(reactionPicker, setReactionPicker, restoreFocus)}
                />
            )}
            {editDialog && onEditMessage && (
                <MessageEditDialog
                    message={editDialog.message}
                    maxLength={maxMessageLength}
                    onSave={(content) => onEditMessage(editDialog.message.id, content)}
                    onClose={() => closeOverlay(editDialog, setEditDialog)}
                />
            )}
            {deleteDialog && onDeleteMessage && (
                <MessageDeleteDialog
                    message={deleteDialog.message}
                    onConfirm={() => onDeleteMessage(deleteDialog.message.id)}
                    onClose={() => closeOverlay(deleteDialog, setDeleteDialog)}
                />
            )}
            {openImage && <ImageViewer image={openImage} onClose={closeImage} />}
        </div>
    );
}

function mutationStatus(kind: NonNullable<ChatMessage['mutation']>['kind']): string {
    switch (kind) {
        case 'reaction': return '正在同步表情反应…';
        case 'edit': return '正在保存修改…';
        case 'delete': return '正在删除消息…';
        case 'star': return '正在同步收藏状态…';
    }
}
