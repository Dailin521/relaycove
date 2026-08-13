import { describe, expect, it } from 'vitest';
import type { ChatMessage } from '../models/ui';
import { buildMessageQuote, parseLeadingMessageQuote } from './messageQuote';

const message: ChatMessage = {
    id: '42',
    sender: { id: '9', name: 'Grace Hopper', initials: 'GH', tone: 'blue' },
    sentAt: '10:00',
    body: '正文',
    rawContent: '正文\n![设计图](/user_uploads/1/design.png)\n[需求文档](/user_uploads/1/spec.pdf)',
    permalink: 'https://chat.example.test/#narrow/near/42',
};

describe('message quote', () => {
    it('builds an official-style fenced quote from complete raw Markdown', () => {
        const quote = buildMessageQuote(message);

        expect(quote).toContain('@_**Grace Hopper|9** [said](https://chat.example.test/#narrow/near/42):');
        expect(quote).toContain('```quote\n正文\n![设计图](/user_uploads/1/design.png)\n[需求文档](/user_uploads/1/spec.pdf)\n```');
    });

    it('parses a leading quote into a presentation and preserves the reply body', () => {
        const parsed = parseLeadingMessageQuote(`${buildMessageQuote(message)}收到，我来处理。`);

        expect(parsed).toEqual({
            sender: 'Grace Hopper',
            body: message.rawContent,
            permalink: message.permalink,
            remainder: '收到，我来处理。',
        });
    });

    it('raises the quote fence above backticks already present in the message', () => {
        const quote = buildMessageQuote({ ...message, rawContent: '示例：```ts\nconst ok = true;\n```' });

        expect(quote).toContain('````quote');
        expect(quote).toContain('\n````\n\n');
    });
});
