import { describe, expect, it } from 'vitest';
import {
    attachmentKind,
    MAX_ATTACHMENT_COUNT,
    uploadedFileMarkdown,
    validateAttachmentSelection,
} from './attachmentDraft';

describe('attachment drafts', () => {
    it('accepts ordinary files and only previews safe raster image types', () => {
        const archive = new File(['zip'], 'notes.zip', { type: 'application/zip' });
        const image = new File(['png'], 'photo.png', { type: 'image/png' });
        const svg = new File(['<svg/>'], 'active.svg', { type: 'image/svg+xml' });

        expect(validateAttachmentSelection([], [archive, image, svg], 1024)).toBeUndefined();
        expect(attachmentKind(image)).toBe('image');
        expect(attachmentKind(svg)).toBe('file');
        expect(attachmentKind(archive)).toBe('file');
    });

    it('rejects empty, oversized, excessive, and unsafe selections', () => {
        expect(validateAttachmentSelection([], [new File([], 'empty.txt')], 1024)).toContain('为空文件');
        expect(validateAttachmentSelection([], [new File([new Uint8Array(1025)], 'large.bin')], 1024)).toContain('不能超过');
        expect(validateAttachmentSelection([], [new File(['x'], 'bad\nname.txt')], 1024)).toContain('文件名');
        expect(validateAttachmentSelection([], Array.from(
            { length: MAX_ATTACHMENT_COUNT + 1 },
            (_, index) => new File(['x'], `${index}.txt`),
        ), 1024)).toContain('最多');
    });

    it('escapes server filenames before composing Markdown', () => {
        expect(uploadedFileMarkdown({
            filename: String.raw`report)[demo]\final.txt`,
            url: 'https://chat.example.test/user_uploads/1/report.txt',
        })).toBe(String.raw`[report\)\[demo\]\\final.txt](https://chat.example.test/user_uploads/1/report.txt)`);
    });
});
