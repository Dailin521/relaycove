import { MoreHorizontal } from 'lucide-react';
import { type KeyboardEvent, type MouseEvent, useCallback, useEffect, useState } from 'react';
import type { ChatMessage, ConversationDetail } from '../models/ui';
import { Avatar } from './Avatar';
import { MessageContextMenu } from './MessageContextMenu';
import { ImageViewer, MessageImage, type OpenImage } from './MessageImage';

interface OpenMenu {
    message: ChatMessage;
    x: number;
    y: number;
    trigger: HTMLElement;
}

function Message({
    message,
    onOpenMenu,
    onOpenImage,
}: {
    message: ChatMessage;
    onOpenMenu(menu: OpenMenu): void;
    onOpenImage(image: OpenImage): void;
}) {
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

    return (
        <article
            className={message.own ? 'message-row is-own' : 'message-row'}
            tabIndex={0}
            data-message-id={message.id}
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
                    <button
                        className="message-more-button"
                        type="button"
                        aria-label={`更多消息操作：${message.sender.name} ${message.sentAt}`}
                        aria-haspopup="menu"
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
                </div>
                <div className="message-bubble">
                    {message.quote && (
                        <blockquote>
                            <strong>{message.quote.sender}</strong>
                            <span>{message.quote.body}</span>
                        </blockquote>
                    )}
                    {message.body && <p>{message.body}</p>}
                    {message.attachments && message.attachments.length > 0 && (
                        <div className="message-images">
                            {message.attachments.map((attachment) => (
                                <MessageImage
                                    key={attachment.sourceUrl}
                                    attachment={attachment}
                                    onOpen={onOpenImage}
                                />
                            ))}
                        </div>
                    )}
                </div>
                {message.reaction && (
                    <button className="message-reaction" type="button" aria-label={`反应 ${message.reaction}`} aria-pressed="true">
                        {message.reaction}
                    </button>
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
}

export function MessageList({ conversation, onLoadOlder, onRecoverPending, onReply }: MessageListProps) {
    const [openMenu, setOpenMenu] = useState<OpenMenu>();
    const [openImage, setOpenImage] = useState<OpenImage>();
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
    }, [conversation?.id]);

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
                    <Message message={message} onOpenMenu={setOpenMenu} onOpenImage={setOpenImage} />
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
                    onClose={closeMenu}
                    onAnnounce={setAnnouncement}
                />
            )}
            {openImage && <ImageViewer image={openImage} onClose={closeImage} />}
        </div>
    );
}
