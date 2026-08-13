import { Copy, ExternalLink, Hash, Link, Pencil, Reply, SmilePlus, Star, Trash2 } from 'lucide-react';
import { type CSSProperties, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import type { ChatMessage } from '../models/ui';

interface MessageContextMenuProps {
    message: ChatMessage;
    x: number;
    y: number;
    onReply(message: ChatMessage): void;
    onOpenReaction?(message: ChatMessage): void;
    onToggleStar?(message: ChatMessage): void;
    onEdit?(message: ChatMessage): void;
    onDelete?(message: ChatMessage): void;
    onClose(restoreFocus?: boolean): void;
    onAnnounce(message: string): void;
}

export function MessageContextMenu({
    message,
    x,
    y,
    onReply,
    onOpenReaction,
    onToggleStar,
    onEdit,
    onDelete,
    onClose,
    onAnnounce,
}: MessageContextMenuProps) {
    const menuRef = useRef<HTMLDivElement>(null);
    const [position, setPosition] = useState({ left: x, top: y });

    useLayoutEffect(() => {
        const menu = menuRef.current;
        if (!menu) {
            return;
        }
        const bounds = menu.getBoundingClientRect();
        setPosition({
            left: Math.max(8, Math.min(x, window.innerWidth - bounds.width - 8)),
            top: Math.max(8, Math.min(y, window.innerHeight - bounds.height - 8)),
        });
        menu.querySelector<HTMLElement>('[role="menuitem"]:not([disabled])')?.focus();
    }, [x, y]);

    useEffect(() => {
        function handlePointerDown(event: PointerEvent) {
            if (!menuRef.current?.contains(event.target as Node)) {
                onClose();
            }
        }
        function handleKeyDown(event: KeyboardEvent) {
            const items = [...(menuRef.current?.querySelectorAll<HTMLElement>('[role="menuitem"]:not([disabled])') ?? [])];
            const current = items.indexOf(document.activeElement as HTMLElement);
            if (event.key === 'Escape') {
                event.preventDefault();
                onClose();
            } else if (event.key === 'ArrowDown' && items.length > 0) {
                event.preventDefault();
                items[(current + 1 + items.length) % items.length]?.focus();
            } else if (event.key === 'ArrowUp' && items.length > 0) {
                event.preventDefault();
                items[(current - 1 + items.length) % items.length]?.focus();
            } else if (event.key === 'Home' && items.length > 0) {
                event.preventDefault();
                items[0]?.focus();
            } else if (event.key === 'End' && items.length > 0) {
                event.preventDefault();
                items.at(-1)?.focus();
            }
        }
        window.addEventListener('pointerdown', handlePointerDown, true);
        window.addEventListener('keydown', handleKeyDown);
        return () => {
            window.removeEventListener('pointerdown', handlePointerDown, true);
            window.removeEventListener('keydown', handleKeyDown);
        };
    }, [onClose]);

    async function copy(value: string, successMessage: string) {
        try {
            await copyText(value);
            onAnnounce(successMessage);
            onClose();
        } catch {
            onAnnounce('复制失败，请手动选择消息正文。');
        }
    }

    const theme = document.querySelector('.relaycove-app')?.getAttribute('data-theme') ?? 'light';
    const style: CSSProperties = { left: position.left, top: position.top };
    const mutationPending = message.mutation !== undefined && message.mutation.phase !== 'failed';
    return createPortal(
        <div
            ref={menuRef}
            className="message-context-menu"
            data-theme={theme}
            role="menu"
            aria-label={`消息 ${message.id} 操作`}
            style={style}
        >
            {onOpenReaction && (
                <button type="button" role="menuitem" disabled={mutationPending} onClick={() => { onOpenReaction(message); onClose(false); }}>
                    <SmilePlus aria-hidden="true" /><span>添加表情反应</span>
                </button>
            )}
            {onToggleStar && (
                <button type="button" role="menuitem" disabled={mutationPending} onClick={() => { onToggleStar(message); onClose(); }}>
                    <Star aria-hidden="true" fill={message.isStarred ? 'currentColor' : 'none'} />
                    <span>{message.isStarred ? '取消收藏' : '收藏消息'}</span>
                </button>
            )}
            <button type="button" role="menuitem" onClick={() => { onReply(message); onClose(false); }}>
                <Reply aria-hidden="true" /><span>引用回复</span>
            </button>
            {message.own && onEdit && (
                <button type="button" role="menuitem" disabled={mutationPending} onClick={() => { onEdit(message); onClose(false); }}>
                    <Pencil aria-hidden="true" /><span>编辑消息</span>
                </button>
            )}
            {message.own && onDelete && (
                <button className="danger" type="button" role="menuitem" disabled={mutationPending} onClick={() => { onDelete(message); onClose(false); }}>
                    <Trash2 aria-hidden="true" /><span>删除消息</span>
                </button>
            )}
            <button type="button" role="menuitem" onClick={() => void copy(message.rawContent ?? message.body, '已复制消息正文。')}>
                <Copy aria-hidden="true" /><span>复制消息正文</span>
            </button>
            {message.permalink && (
                <button type="button" role="menuitem" onClick={() => void copy(message.permalink!, '已复制消息链接。')}>
                    <Link aria-hidden="true" /><span>复制消息链接</span>
                </button>
            )}
            <button type="button" role="menuitem" onClick={() => void copy(message.id, '已复制消息 ID。')}>
                <Hash aria-hidden="true" /><span>复制消息 ID</span>
            </button>
            {message.permalink && (
                <a href={message.permalink} target="_blank" rel="noopener noreferrer" role="menuitem" onClick={() => onClose()}>
                    <ExternalLink aria-hidden="true" /><span>在 Zulip 中打开</span>
                </a>
            )}
        </div>,
        document.body,
    );
}

export async function copyText(value: string): Promise<void> {
    if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(value);
        return;
    }
    const textarea = document.createElement('textarea');
    textarea.value = value;
    textarea.setAttribute('readonly', '');
    textarea.style.position = 'fixed';
    textarea.style.opacity = '0';
    document.body.append(textarea);
    textarea.select();
    const copied = document.execCommand('copy');
    textarea.remove();
    if (!copied) {
        throw new Error('Copy unavailable.');
    }
}
