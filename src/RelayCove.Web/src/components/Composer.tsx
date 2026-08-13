import { FileText, Paperclip, Send, Smile, X } from 'lucide-react';
import {
    type DragEvent,
    type KeyboardEvent,
    type PointerEvent,
    useCallback,
    useEffect,
    useRef,
    useState,
} from 'react';
import type { AttachmentDraft } from '../models/ui';
import { formatBytes, validateAttachmentSelection } from './attachmentDraft';
import { EmojiPicker } from './EmojiPicker';

const MIN_HEIGHT = 72;
const MAX_HEIGHT = 300;
const KEYBOARD_STEP = 16;

interface ComposerProps {
    conversationTitle?: string;
    value: string;
    height: number;
    statusText: string;
    errorText?: string;
    sendEnabled: boolean;
    sending: boolean;
    onChange(value: string): void;
    onHeightChange(height: number): void;
    onSend(): void;
    focusRequest?: number;
    attachmentDrafts: readonly AttachmentDraft[];
    maxAttachmentUploadBytes: number;
    attachmentUploadEnabled: boolean;
    onAttachmentsSelected(files: readonly File[]): void;
    onAttachmentRemoved(attachmentId: string): void;
    onAttachmentError(message: string): void;
}

function clamp(value: number): number {
    return Math.max(MIN_HEIGHT, Math.min(MAX_HEIGHT, Math.round(value)));
}

export function Composer({
    conversationTitle,
    value,
    height,
    statusText,
    errorText,
    sendEnabled,
    sending,
    onChange,
    onHeightChange,
    onSend,
    focusRequest,
    attachmentDrafts,
    maxAttachmentUploadBytes,
    attachmentUploadEnabled,
    onAttachmentsSelected,
    onAttachmentRemoved,
    onAttachmentError,
}: ComposerProps) {
    const dragState = useRef<{ startY: number; startHeight: number } | null>(null);
    const fileDragDepth = useRef(0);
    const textareaRef = useRef<HTMLTextAreaElement>(null);
    const fileInputRef = useRef<HTMLInputElement>(null);
    const emojiButtonRef = useRef<HTMLButtonElement>(null);
    const [emojiOpen, setEmojiOpen] = useState(false);
    const [fileDragActive, setFileDragActive] = useState(false);
    const hasContent = Boolean(value.trim() || attachmentDrafts.length > 0);
    const canAttach = Boolean(conversationTitle && attachmentUploadEnabled && !sending);

    const closeEmojiPicker = useCallback((restoreFocus = true) => {
        setEmojiOpen(false);
        if (restoreFocus) {
            window.requestAnimationFrame(() => emojiButtonRef.current?.focus());
        }
    }, []);

    useEffect(() => {
        if (focusRequest !== undefined && focusRequest > 0) {
            textareaRef.current?.focus();
            const end = textareaRef.current?.value.length ?? 0;
            textareaRef.current?.setSelectionRange(end, end);
        }
    }, [focusRequest]);

    useEffect(() => {
        function preventFileNavigation(event: globalThis.DragEvent) {
            if (shouldPreventDropNavigation(event.dataTransfer)) {
                event.preventDefault();
            }
        }
        window.addEventListener('dragover', preventFileNavigation);
        window.addEventListener('drop', preventFileNavigation);
        return () => {
            window.removeEventListener('dragover', preventFileNavigation);
            window.removeEventListener('drop', preventFileNavigation);
        };
    }, []);

    useEffect(() => {
        function cancelDragHighlight(event: globalThis.KeyboardEvent) {
            if (event.key === 'Escape') {
                fileDragDepth.current = 0;
                setFileDragActive(false);
            }
        }
        window.addEventListener('keydown', cancelDragHighlight);
        return () => window.removeEventListener('keydown', cancelDragHighlight);
    }, []);

    function handlePointerDown(event: PointerEvent<HTMLDivElement>) {
        dragState.current = { startY: event.clientY, startHeight: height };
        event.currentTarget.setPointerCapture(event.pointerId);
    }

    function handlePointerMove(event: PointerEvent<HTMLDivElement>) {
        if (!dragState.current) {
            return;
        }
        onHeightChange(clamp(dragState.current.startHeight + dragState.current.startY - event.clientY));
    }

    function handlePointerUp(event: PointerEvent<HTMLDivElement>) {
        dragState.current = null;
        if (event.currentTarget.hasPointerCapture(event.pointerId)) {
            event.currentTarget.releasePointerCapture(event.pointerId);
        }
    }

    function handleResizerKeyDown(event: KeyboardEvent<HTMLDivElement>) {
        const heights: Record<string, number> = {
            ArrowUp: height + KEYBOARD_STEP,
            ArrowDown: height - KEYBOARD_STEP,
            Home: MIN_HEIGHT,
            End: MAX_HEIGHT,
        };
        const nextHeight = heights[event.key];
        if (nextHeight === undefined) {
            return;
        }
        event.preventDefault();
        onHeightChange(clamp(nextHeight));
    }

    function insertEmoji(emoji: string) {
        const textarea = textareaRef.current;
        const selectionStart = textarea?.selectionStart ?? value.length;
        const selectionEnd = textarea?.selectionEnd ?? selectionStart;
        const next = `${value.slice(0, selectionStart)}${emoji}${value.slice(selectionEnd)}`;
        onChange(next);
        setEmojiOpen(false);
        window.requestAnimationFrame(() => {
            textarea?.focus();
            const caret = selectionStart + emoji.length;
            textarea?.setSelectionRange(caret, caret);
        });
    }

    function addFiles(files: readonly File[]) {
        const validationError = validateAttachmentSelection(
            attachmentDrafts,
            files,
            maxAttachmentUploadBytes,
        );
        if (validationError) {
            onAttachmentError(validationError);
            return;
        }
        onAttachmentsSelected(files);
    }

    function handleFileDragEnter(event: DragEvent<HTMLElement>) {
        event.preventDefault();
        event.stopPropagation();
        if (!hasDraggedFiles(event.dataTransfer)) {
            return;
        }
        fileDragDepth.current += 1;
        if (canAttach) {
            setFileDragActive(true);
        }
    }

    function handleFileDragOver(event: DragEvent<HTMLElement>) {
        event.preventDefault();
        event.stopPropagation();
        if (!hasDraggedFiles(event.dataTransfer)) {
            event.dataTransfer.dropEffect = 'none';
            return;
        }
        event.dataTransfer.dropEffect = canAttach ? 'copy' : 'none';
    }

    function handleFileDragLeave(event: DragEvent<HTMLElement>) {
        if (!hasDraggedFiles(event.dataTransfer)) {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        fileDragDepth.current = Math.max(0, fileDragDepth.current - 1);
        if (fileDragDepth.current === 0) {
            setFileDragActive(false);
        }
    }

    function handleFileDrop(event: DragEvent<HTMLElement>) {
        event.preventDefault();
        event.stopPropagation();
        if (!hasDraggedFiles(event.dataTransfer)) {
            onAttachmentError('只支持拖入本地文件，不能导入网页链接或 HTML。');
            return;
        }
        fileDragDepth.current = 0;
        setFileDragActive(false);
        if (!canAttach) {
            return;
        }
        addFiles(Array.from(event.dataTransfer.files));
    }

    return (
        <section
            className={`composer${attachmentDrafts.length > 0 ? ' has-attachments' : ''}${fileDragActive ? ' is-file-drag-active' : ''}`}
            style={{ '--composer-height': `${height}px` } as React.CSSProperties}
            aria-label="消息输入区"
            aria-describedby="composer-drop-hint"
            onDragEnter={handleFileDragEnter}
            onDragOver={handleFileDragOver}
            onDragLeave={handleFileDragLeave}
            onDrop={handleFileDrop}
        >
            <span id="composer-drop-hint" className="sr-only">可选择多个附件，也可将文件拖放到消息输入区。</span>
            <div
                className="composer-resizer"
                role="separator"
                aria-label={`输入区高度 ${height} 像素`}
                aria-orientation="horizontal"
                aria-valuemin={MIN_HEIGHT}
                aria-valuemax={MAX_HEIGHT}
                aria-valuenow={height}
                tabIndex={0}
                onPointerDown={handlePointerDown}
                onPointerMove={handlePointerMove}
                onPointerUp={handlePointerUp}
                onPointerCancel={handlePointerUp}
                onKeyDown={handleResizerKeyDown}
            >
                <span />
            </div>
            {attachmentDrafts.length > 0 && (
                <ul className="composer-attachment-drafts" aria-label="待发送附件">
                    {attachmentDrafts.map((draft) => (
                        <li className="composer-attachment-draft" key={draft.id}>
                            {draft.kind === 'image' && draft.previewUrl
                                ? <img src={draft.previewUrl} alt="" />
                                : <span className="composer-file-icon"><FileText aria-hidden="true" /></span>}
                            <span>
                                <strong>{draft.file.name}</strong>
                                <small>
                                    {formatBytes(draft.file.size)}
                                    {draft.uploaded ? ' · 已上传，可复用' : ' · 等待发送'}
                                </small>
                            </span>
                            <button
                                type="button"
                                aria-label={`移除附件 ${draft.file.name}`}
                                disabled={sending}
                                onClick={() => onAttachmentRemoved(draft.id)}
                            >
                                <X aria-hidden="true" />
                            </button>
                        </li>
                    ))}
                </ul>
            )}
            <textarea
                ref={textareaRef}
                aria-label="消息正文"
                placeholder={conversationTitle ? `发送到 ${conversationTitle}` : '选择会话后输入消息'}
                value={value}
                disabled={!conversationTitle}
                onChange={(event) => onChange(event.target.value)}
                onKeyDown={(event) => {
                    if (event.key === 'Enter' && event.ctrlKey) {
                        event.preventDefault();
                        if (sendEnabled && hasContent && !sending) {
                            onSend();
                        }
                    }
                }}
            />
            <div className="composer-toolbar">
                <button
                    ref={emojiButtonRef}
                    type="button"
                    aria-label="插入表情"
                    aria-haspopup="dialog"
                    aria-expanded={emojiOpen}
                    disabled={!conversationTitle}
                    title="插入 Unicode 表情"
                    onClick={() => setEmojiOpen((current) => !current)}
                >
                    <Smile aria-hidden="true" />
                </button>
                <button
                    type="button"
                    aria-label="选择待发送附件"
                    disabled={!canAttach}
                    title={attachmentUploadEnabled
                        ? `可选择任意文件，单个最大 ${formatBytes(maxAttachmentUploadBytes)}`
                        : '附件上传尚不可用'}
                    onClick={() => fileInputRef.current?.click()}
                >
                    <Paperclip aria-hidden="true" />
                </button>
                <input
                    ref={fileInputRef}
                    className="composer-file-input"
                    type="file"
                    multiple
                    tabIndex={-1}
                    disabled={!canAttach}
                    onChange={(event) => {
                        const files = Array.from(event.target.files ?? []);
                        event.target.value = '';
                        addFiles(files);
                    }}
                />
                <span
                    className={errorText ? 'composer-status is-error' : 'composer-status'}
                    title={errorText}
                    aria-live="polite"
                >
                    {errorText ?? statusText}
                </span>
                <button
                    className="send-button"
                    type="button"
                    disabled={!sendEnabled || !hasContent || sending}
                    aria-label={sending ? '正在发送' : '发送消息'}
                    onClick={onSend}
                >
                    <Send aria-hidden="true" />
                    {sending ? '发送中' : '发送'}
                </button>
            </div>
            {fileDragActive && <div className="composer-drop-overlay" aria-hidden="true">松开即可添加附件</div>}
            {emojiOpen && emojiButtonRef.current && (
                <EmojiPicker
                    trigger={emojiButtonRef.current}
                    onSelect={(choice) => insertEmoji(choice.emoji)}
                    onClose={closeEmojiPicker}
                />
            )}
        </section>
    );
}

function hasDraggedFiles(dataTransfer: DataTransfer | null): boolean {
    return Boolean(dataTransfer && Array.from(dataTransfer.types).includes('Files'));
}

function shouldPreventDropNavigation(dataTransfer: DataTransfer | null): boolean {
    if (!dataTransfer) {
        return false;
    }
    const types = Array.from(dataTransfer.types);
    return types.includes('Files') || types.includes('text/uri-list') || types.includes('text/html');
}
