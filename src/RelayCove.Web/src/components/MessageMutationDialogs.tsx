import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import type { ChatMessage } from '../models/ui';

interface MessageEditDialogProps {
    message: ChatMessage;
    maxLength?: number;
    onSave(content: string): Promise<void>;
    onClose(): void;
}

export function MessageEditDialog({ message, maxLength, onSave, onClose }: MessageEditDialogProps) {
    const [value, setValue] = useState(message.rawContent ?? message.body);
    const [saving, setSaving] = useState(false);
    const [error, setError] = useState<string>();
    const textareaRef = useRef<HTMLTextAreaElement>(null);

    useEffect(() => {
        textareaRef.current?.focus();
        textareaRef.current?.setSelectionRange(value.length, value.length);
    }, []); // The dialog captures one explicit edit snapshot.

    useEffect(() => {
        function handleKeyDown(event: KeyboardEvent) {
            if (event.key === 'Escape' && !saving) {
                event.preventDefault();
                onClose();
            }
        }
        window.addEventListener('keydown', handleKeyDown);
        return () => window.removeEventListener('keydown', handleKeyDown);
    }, [onClose, saving]);

    async function save() {
        if (saving || !value.trim()) {
            setError('消息正文不能为空。');
            return;
        }
        if (maxLength !== undefined && value.length > maxLength) {
            setError(`消息不能超过 ${maxLength} 个字符。`);
            return;
        }
        setSaving(true);
        setError(undefined);
        try {
            await onSave(value);
            onClose();
        } catch (caught) {
            setError(caught instanceof Error ? caught.message : '消息没有更新。');
            setSaving(false);
        }
    }

    const theme = document.querySelector('.relaycove-app')?.getAttribute('data-theme') ?? 'light';
    return createPortal(
        <div
            className="message-dialog-backdrop"
            data-theme={theme}
            onPointerDown={(event) => {
                if (event.target === event.currentTarget && !saving) {
                    onClose();
                }
            }}
        >
            <section className="message-dialog" role="dialog" aria-modal="true" aria-labelledby="message-edit-title">
                <header>
                    <h2 id="message-edit-title">编辑消息</h2>
                    <p>修改会直接同步到 Zulip。</p>
                </header>
                <textarea
                    ref={textareaRef}
                    aria-label="编辑消息正文"
                    value={value}
                    maxLength={maxLength}
                    disabled={saving}
                    onChange={(event) => setValue(event.target.value)}
                />
                {error && <p className="message-dialog-error" role="alert">{error}</p>}
                <footer>
                    <button type="button" disabled={saving} onClick={onClose}>取消</button>
                    <button type="button" className="primary" disabled={saving || !value.trim()} onClick={() => void save()}>
                        {saving ? '正在保存…' : '保存修改'}
                    </button>
                </footer>
            </section>
        </div>,
        document.body,
    );
}

interface MessageDeleteDialogProps {
    message: ChatMessage;
    onConfirm(): Promise<void>;
    onClose(): void;
}

export function MessageDeleteDialog({ message, onConfirm, onClose }: MessageDeleteDialogProps) {
    const [deleting, setDeleting] = useState(false);
    const [error, setError] = useState<string>();
    const cancelRef = useRef<HTMLButtonElement>(null);

    useEffect(() => {
        cancelRef.current?.focus();
    }, []);

    useEffect(() => {
        function handleKeyDown(event: KeyboardEvent) {
            if (event.key === 'Escape' && !deleting) {
                event.preventDefault();
                onClose();
            }
        }
        window.addEventListener('keydown', handleKeyDown);
        return () => window.removeEventListener('keydown', handleKeyDown);
    }, [deleting, onClose]);

    async function remove() {
        setDeleting(true);
        setError(undefined);
        try {
            await onConfirm();
            onClose();
        } catch (caught) {
            setError(caught instanceof Error ? caught.message : '消息没有删除。');
            setDeleting(false);
        }
    }

    const preview = (message.rawContent ?? message.body).replace(/\s+/gu, ' ').trim().slice(0, 80);
    const theme = document.querySelector('.relaycove-app')?.getAttribute('data-theme') ?? 'light';
    return createPortal(
        <div
            className="message-dialog-backdrop"
            data-theme={theme}
            onPointerDown={(event) => {
                if (event.target === event.currentTarget && !deleting) {
                    onClose();
                }
            }}
        >
            <section className="message-dialog" role="alertdialog" aria-modal="true" aria-labelledby="message-delete-title">
                <header>
                    <h2 id="message-delete-title">永久删除这条消息？</h2>
                    <p>消息将从 Zulip 删除，无法撤销。</p>
                </header>
                <blockquote>{preview || `消息 ${message.id}`}</blockquote>
                {error && <p className="message-dialog-error" role="alert">{error}</p>}
                <footer>
                    <button ref={cancelRef} type="button" disabled={deleting} onClick={onClose}>取消</button>
                    <button type="button" className="danger" disabled={deleting} onClick={() => void remove()}>
                        {deleting ? '正在删除…' : '确认删除'}
                    </button>
                </footer>
            </section>
        </div>,
        document.body,
    );
}
