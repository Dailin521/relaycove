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

    it('keeps unsafe active links literal and presents non-previewable uploads as files', () => {
        const content = [
            '![x](https://evil.test/user_uploads/x.png)',
            '![x](javascript:alert(1))',
            '[vector.svg](/user_uploads/a/vector.svg)',
            '<img src=x onerror=alert(1)>',
        ].join('\n');
        expect(presentMessageContent(content, 'https://chat.example.test')).toEqual({
            body: [
                '![x](https://evil.test/user_uploads/x.png)',
                '![x](javascript:alert(1))',
                '',
                '<img src=x onerror=alert(1)>',
            ].join('\n'),
            attachments: [{
                kind: 'file',
                name: 'vector.svg',
                sourceUrl: 'https://chat.example.test/user_uploads/a/vector.svg',
            }],
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

    it('presents an official-style quote separately from the reply body', () => {
        const content = [
            '@_**Grace Hopper|9** [said](https://chat.example.test/#narrow/near/42):',
            '```quote',
            '正文',
            '![设计图](/user_uploads/1/design.png)',
            '[需求文档](/user_uploads/1/spec.pdf)',
            '```',
            '',
            '收到，我来处理。',
        ].join('\n');

        expect(presentMessageContent(content, 'https://chat.example.test')).toEqual({
            body: '收到，我来处理。',
            attachments: [],
            quote: {
                sender: 'Grace Hopper',
                body: '正文\n![设计图](/user_uploads/1/design.png)\n[需求文档](/user_uploads/1/spec.pdf)',
                permalink: 'https://chat.example.test/#narrow/near/42',
            },
        });
    });
});
