import { ImagePlus, Send, Smile, X } from 'lucide-react';
import { KeyboardEvent, PointerEvent, useEffect, useRef } from 'react';
import type { ImageDraft } from '../models/ui';
import { formatBytes, validateImageFile } from './imageDraft';

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
    imageDraft?: ImageDraft;
    maxImageUploadBytes: number;
    imageUploadEnabled: boolean;
    onImageSelected(file: File): void;
    onImageRemoved(): void;
    onImageError(message: string): void;
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
    imageDraft,
    maxImageUploadBytes,
    imageUploadEnabled,
    onImageSelected,
    onImageRemoved,
    onImageError,
}: ComposerProps) {
    const dragState = useRef<{ startY: number; startHeight: number } | null>(null);
    const textareaRef = useRef<HTMLTextAreaElement>(null);
    const fileInputRef = useRef<HTMLInputElement>(null);
    const hasContent = Boolean(value.trim() || imageDraft);

    useEffect(() => {
        if (focusRequest !== undefined && focusRequest > 0) {
            textareaRef.current?.focus();
            const end = textareaRef.current?.value.length ?? 0;
            textareaRef.current?.setSelectionRange(end, end);
        }
    }, [focusRequest]);

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

    return (
        <section
            className={imageDraft ? 'composer has-image' : 'composer'}
            style={{ '--composer-height': `${height}px` } as React.CSSProperties}
            aria-label="消息输入区"
        >
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
            {imageDraft && (
                <div className="composer-image-draft">
                    <img src={imageDraft.previewUrl} alt="待发送图片预览" />
                    <span>
                        <strong>{imageDraft.file.name}</strong>
                        <small>
                            {formatBytes(imageDraft.file.size)}
                            {imageDraft.uploaded ? ' · 已上传，可复用' : ' · 等待发送'}
                        </small>
                    </span>
                    <button type="button" aria-label="移除待发送图片" disabled={sending} onClick={onImageRemoved}>
                        <X aria-hidden="true" />
                    </button>
                </div>
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
                <button type="button" aria-label="表情能力尚未启用" aria-disabled="true" title="后续能力">
                    <Smile aria-hidden="true" />
                </button>
                <button
                    type="button"
                    aria-label="选择待发送图片"
                    disabled={!conversationTitle || !imageUploadEnabled || sending}
                    title={imageUploadEnabled ? `PNG、JPEG、WebP、GIF 或 AVIF，最大 ${formatBytes(maxImageUploadBytes)}` : '图片上传尚不可用'}
                    onClick={() => fileInputRef.current?.click()}
                >
                    <ImagePlus aria-hidden="true" />
                </button>
                <input
                    ref={fileInputRef}
                    className="composer-file-input"
                    type="file"
                    accept="image/png,image/jpeg,image/webp,image/gif,image/avif"
                    tabIndex={-1}
                    disabled={!conversationTitle || !imageUploadEnabled || sending}
                    onChange={(event) => {
                        const file = event.target.files?.[0];
                        event.target.value = '';
                        if (!file) {
                            return;
                        }
                        const validationError = validateImageFile(file, maxImageUploadBytes);
                        if (validationError) {
                            onImageError(validationError);
                            return;
                        }
                        onImageSelected(file);
                    }}
                />
                <span className={errorText ? 'composer-status is-error' : 'composer-status'} title={errorText}>
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
        </section>
    );
}
