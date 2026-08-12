import { Download, ImageIcon, RotateCcw, X } from 'lucide-react';
import { useEffect, useRef } from 'react';
import { createPortal } from 'react-dom';
import type { MessageAttachment } from '../models/ui';
import { useRealmImage } from './RealmMedia';

export interface OpenImage {
    attachment: MessageAttachment;
    objectUrl: string;
    source: HTMLButtonElement;
}

export function MessageImage({
    attachment,
    onOpen,
}: {
    attachment: MessageAttachment;
    onOpen(image: OpenImage): void;
}) {
    const image = useRealmImage(attachment.sourceUrl, 'upload');
    const buttonRef = useRef<HTMLButtonElement>(null);

    if (image.status === 'error') {
        return (
            <div className="message-image-fallback" role="status">
                <ImageIcon aria-hidden="true" />
                <span><strong>{attachment.name}</strong><small>无法加载安全预览</small></span>
                <button type="button" onClick={image.retry} aria-label={`重试加载 ${attachment.name}`}>
                    <RotateCcw aria-hidden="true" />
                </button>
            </div>
        );
    }

    return (
        <button
            ref={buttonRef}
            type="button"
            className="message-image"
            disabled={!image.objectUrl}
            aria-label={image.objectUrl ? `打开图片 ${attachment.name}` : `正在安全加载图片 ${attachment.name}`}
            onClick={() => {
                if (image.objectUrl && buttonRef.current) {
                    onOpen({ attachment, objectUrl: image.objectUrl, source: buttonRef.current });
                }
            }}
        >
            {image.objectUrl ? (
                <img src={image.objectUrl} alt={attachment.name} decoding="async" loading="lazy" />
            ) : (
                <span className="message-image-loading" aria-hidden="true"><ImageIcon /></span>
            )}
            <span>{attachment.name}</span>
        </button>
    );
}

export function ImageViewer({ image, onClose }: { image: OpenImage; onClose(): void }) {
    const closeRef = useRef<HTMLButtonElement>(null);
    const dialogRef = useRef<HTMLDivElement>(null);

    useEffect(() => {
        closeRef.current?.focus();
        function handleKeyDown(event: KeyboardEvent) {
            if (event.key === 'Escape') {
                event.preventDefault();
                onClose();
                return;
            }
            if (event.key !== 'Tab') {
                return;
            }
            const focusable = dialogRef.current?.querySelectorAll<HTMLElement>('button, a[href]');
            if (!focusable || focusable.length === 0) {
                return;
            }
            const first = focusable[0]!;
            const last = focusable[focusable.length - 1]!;
            if (event.shiftKey && document.activeElement === first) {
                event.preventDefault();
                last.focus();
            } else if (!event.shiftKey && document.activeElement === last) {
                event.preventDefault();
                first.focus();
            }
        }
        window.addEventListener('keydown', handleKeyDown);
        return () => window.removeEventListener('keydown', handleKeyDown);
    }, [onClose]);

    const theme = document.querySelector('.relaycove-app')?.getAttribute('data-theme') ?? 'light';
    return createPortal(
        <div
            className="image-viewer-backdrop"
            data-theme={theme}
            role="presentation"
            onPointerDown={(event) => {
                if (event.target === event.currentTarget) {
                    onClose();
                }
            }}
        >
            <div
                ref={dialogRef}
                className="image-viewer"
                role="dialog"
                aria-modal="true"
                aria-label={`图片预览：${image.attachment.name}`}
            >
                <header>
                    <strong>{image.attachment.name}</strong>
                    <span>
                        <a
                            className="image-viewer-action"
                            href={image.objectUrl}
                            download={image.attachment.name}
                            aria-label={`下载图片 ${image.attachment.name}`}
                        >
                            <Download aria-hidden="true" />
                        </a>
                        <button ref={closeRef} type="button" onClick={onClose} aria-label="关闭图片预览">
                            <X aria-hidden="true" />
                        </button>
                    </span>
                </header>
                <div className="image-viewer-stage">
                    <img src={image.objectUrl} alt={image.attachment.name} />
                </div>
            </div>
        </div>,
        document.body,
    );
}
