import { describe, expect, it } from 'vitest';
import { isPreviewableImageName, isPublicRealmAvatarUrl, resolveRealmMediaUrl } from './realmMedia';

describe('resolveRealmMediaUrl', () => {
    it('accepts only same-Realm approved media paths', () => {
        expect(resolveRealmMediaUrl('https://chat.example.test', '/user_avatars/2/avatar.png?x=1', 'avatar'))
            .toBe('https://chat.example.test/user_avatars/2/avatar.png?x=1');
        expect(resolveRealmMediaUrl('https://chat.example.test', '/user_uploads/a/b/photo.webp', 'upload'))
            .toBe('https://chat.example.test/user_uploads/a/b/photo.webp');
        expect(resolveRealmMediaUrl('https://chat.example.test', 'https://evil.test/user_uploads/a.png', 'upload'))
            .toBeUndefined();
        expect(resolveRealmMediaUrl('https://chat.example.test', '//evil.test/user_uploads/a.png', 'upload'))
            .toBeUndefined();
        expect(resolveRealmMediaUrl('https://chat.example.test', 'javascript:alert(1)', 'upload'))
            .toBeUndefined();
        expect(resolveRealmMediaUrl('https://chat.example.test', '/api/v1/users/me', 'avatar'))
            .toBeUndefined();
        expect(resolveRealmMediaUrl('https://chat.example.test', '/user_uploads/%2e%2e/api/v1/users/me', 'upload'))
            .toBeUndefined();
    });
});

describe('isPublicRealmAvatarUrl', () => {
    it('uses direct loading only for HTTPS Zulip public avatar paths', () => {
        const realm = 'https://chat.example.test';
        expect(isPublicRealmAvatarUrl('https://chat.example.test/user_avatars/2/avatar.png', realm)).toBe(true);
        expect(isPublicRealmAvatarUrl('https://chat.example.test/static/generated/avatars/2.png', realm)).toBe(true);
        expect(isPublicRealmAvatarUrl('https://chat.example.test/avatar/2', realm)).toBe(false);
        expect(isPublicRealmAvatarUrl('https://evil.example/user_avatars/2/avatar.png', realm)).toBe(false);
        expect(isPublicRealmAvatarUrl('http://chat.example.test/user_avatars/2/avatar.png', realm)).toBe(false);
        expect(isPublicRealmAvatarUrl('not-a-url', realm)).toBe(false);
    });
});

describe('isPreviewableImageName', () => {
    it('allows browser-safe image formats and rejects active or unrelated files', () => {
        expect(isPreviewableImageName('design.PNG')).toBe(true);
        expect(isPreviewableImageName('photo.avif')).toBe(true);
        expect(isPreviewableImageName('vector.svg')).toBe(false);
        expect(isPreviewableImageName('notes.txt')).toBe(false);
    });
});
