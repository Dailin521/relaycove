import { describe, expect, it } from 'vitest';
import { validateImageFile } from './imageDraft';

describe('validateImageFile', () => {
    it('accepts safe image formats within both product and Realm limits', () => {
        expect(validateImageFile(new File([new Uint8Array(512)], 'photo.png', { type: 'image/png' }), 1024))
            .toBeUndefined();
    });

    it('rejects active image formats, empty files, and oversized files before upload', () => {
        expect(validateImageFile(new File(['<svg/>'], 'active.svg', { type: 'image/svg+xml' }), 1024))
            .toContain('PNG');
        expect(validateImageFile(new File([], 'empty.png', { type: 'image/png' }), 1024))
            .toContain('1.0 KB');
        expect(validateImageFile(new File([new Uint8Array(1025)], 'large.webp', { type: 'image/webp' }), 1024))
            .toContain('1.0 KB');
    });
});
