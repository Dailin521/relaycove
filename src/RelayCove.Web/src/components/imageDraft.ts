const allowedImageTypes = new Set(['image/avif', 'image/gif', 'image/jpeg', 'image/png', 'image/webp']);

export function validateImageFile(file: File, maxBytes: number): string | undefined {
    if (!allowedImageTypes.has(file.type)) {
        return '请选择 PNG、JPEG、WebP、GIF 或 AVIF 图片。';
    }
    if (file.size <= 0 || file.size > maxBytes) {
        return `图片不能超过 ${formatBytes(maxBytes)}。`;
    }
    return undefined;
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
