import { describe, expect, it } from 'vitest';
import { presentMessageContent } from './messagePresentation';

describe('presentMessageContent', () => {
    it('extracts same-Realm Zulip uploads while preserving surrounding raw text', () => {
        expect(presentMessageContent(
            '设计稿\n![screen.png](/user_uploads/a/b/screen.png)\n请确认',
            'https://chat.example.test',
        )).toEqual({
            body: '设计稿\n\n请确认',
            attachments: [{
                kind: 'image',
                name: 'screen.png',
                sourceUrl: 'https://chat.example.test/user_uploads/a/b/screen.png',
            }],
        });
    });

    it('recognizes Zulip file-link syntax for image uploads', () => {
        const result = presentMessageContent(
            '[team photo](/user_uploads/a/b/team-photo.webp)',
            'https://chat.example.test',
        );
        expect(result.body).toBe('');
        expect(result.attachments[0]?.name).toBe('team photo');
    });

    it('keeps unsafe, active, and unrelated links as literal raw Markdown', () => {
        const content = [
            '![x](https://evil.test/user_uploads/x.png)',
            '![x](javascript:alert(1))',
            '[vector.svg](/user_uploads/a/vector.svg)',
            '<img src=x onerror=alert(1)>',
        ].join('\n');
        expect(presentMessageContent(content, 'https://chat.example.test')).toEqual({
            body: content,
            attachments: [],
        });
    });

    it('limits one message to four image previews and preserves overflow links as raw Markdown', () => {
        const content = Array.from({ length: 12 }, (_, index) => (
            `![image-${index}.png](/user_uploads/a/image-${index}.png)`
        )).join('\n');

        const result = presentMessageContent(content, 'https://chat.example.test');

        expect(result.attachments).toHaveLength(4);
        expect(result.body).not.toContain('image-3.png');
        expect(result.body).toContain('![image-4.png](/user_uploads/a/image-4.png)');
        expect(result.body).toContain('![image-11.png](/user_uploads/a/image-11.png)');
    });
});
