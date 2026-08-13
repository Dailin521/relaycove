import type { AttachmentDraft } from '../models/ui';

const previewableImageTypes = new Set([
    'image/avif',
    'image/gif',
    'image/jpeg',
    'image/png',
    'image/webp',
]);

export const MAX_ATTACHMENT_COUNT = 10;
const MAX_TOTAL_ATTACHMENT_BYTES = 100 * 1024 * 1024;

export function attachmentKind(file: File): AttachmentDraft['kind'] {
    return previewableImageTypes.has(file.type.toLocaleLowerCase()) ? 'image' : 'file';
}

export function validateAttachmentSelection(
    existing: readonly AttachmentDraft[],
    selected: readonly File[],
    maxFileBytes: number,
): string | undefined {
    if (selected.length === 0) {
        return undefined;
    }
    if (existing.length + selected.length > MAX_ATTACHMENT_COUNT) {
        return `每条消息最多添加 ${MAX_ATTACHMENT_COUNT} 个附件。`;
    }

    for (const file of selected) {
        if (!file.name.trim() || file.name.length > 256 || /[\u0000-\u001f\u007f]/u.test(file.name)) {
            return '附件文件名为空、过长或包含不可用字符。';
        }
        if (file.size <= 0) {
            return `附件“${file.name}”为空文件，不能上传。`;
        }
        if (file.size > maxFileBytes) {
            return `附件“${file.name}”不能超过 ${formatBytes(maxFileBytes)}。`;
        }
    }

    const totalBytes = [...existing.map((draft) => draft.file), ...selected]
        .reduce((total, file) => total + file.size, 0);
    const totalLimit = Math.min(MAX_TOTAL_ATTACHMENT_BYTES, Math.max(maxFileBytes, maxFileBytes * 4));
    if (totalBytes > totalLimit) {
        return `本条消息的附件总大小不能超过 ${formatBytes(totalLimit)}。`;
    }
    return undefined;
}

export function uploadedFileMarkdown(uploaded: { filename: string; url: string }): string {
    return `[${escapeMarkdownLinkLabel(uploaded.filename)}](${uploaded.url})`;
}

export function formatBytes(bytes: number): string {
    if (bytes < 1024) {
        return `${bytes} B`;
    }
    if (bytes < 1024 * 1024) {
        return `${(bytes / 1024).toFixed(1)} KB`;
    }
    return `${(bytes / (1024 * 1024)).toFixed(bytes % (1024 * 1024) === 0 ? 0 : 1)} MB`;
}

function escapeMarkdownLinkLabel(value: string): string {
    const normalized = value.replace(/[\u0000-\u001f\u007f]/gu, '_').trim().slice(0, 256) || 'file';
    return normalized.replace(/[\\[\]()]/gu, '\\$&');
}
