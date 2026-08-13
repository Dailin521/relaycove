import type { ChatMessage } from '../models/ui';

export interface ParsedMessageQuote {
    sender: string;
    body: string;
    permalink?: string;
    remainder: string;
}

const officialQuoteHeader = /^(?:@_\*\*((?:\\.|[^*\r\n])*)\|[^*\r\n]+\*\*|\*\*((?:\\.|[^*\r\n])*)\*\*)\s+\[said\]\(([^)\r\n]+)\)(?:\s+in\s+[^\r\n]+)?:[ \t]*\r?\n(`{3,})quote[ \t]*\r?\n/u;

export function buildMessageQuote(message: ChatMessage): string {
    const rawContent = message.rawContent?.trim() || fallbackRawContent(message) || '消息';
    const fence = '`'.repeat(Math.max(3, longestBacktickRun(rawContent) + 1));
    const senderName = escapeMentionName(message.sender.name);
    const sender = message.permalink
        ? `@_**${senderName}|${message.sender.id}** [said](${message.permalink}):`
        : `**${senderName}** [said](#):`;

    return `${sender}\n${fence}quote\n${rawContent}\n${fence}\n\n`;
}

export function parseLeadingMessageQuote(content: string): ParsedMessageQuote | undefined {
    const official = officialQuoteHeader.exec(content);
    if (official) {
        const fence = official[4]!;
        const quoteStart = official[0].length;
        const closingMarker = `\n${fence}`;
        const quoteEnd = content.indexOf(closingMarker, quoteStart);
        if (quoteEnd >= quoteStart) {
            const afterFence = quoteEnd + closingMarker.length;
            return {
                sender: unescapeMentionName((official[1] ?? official[2] ?? '成员').trim()),
                body: content.slice(quoteStart, quoteEnd).replace(/\r$/u, '').trim(),
                permalink: official[3] === '#' ? undefined : official[3],
                remainder: content.slice(afterFence).replace(/^\r?\n(?:\r?\n)?/u, ''),
            };
        }
    }

    return parseLegacyQuote(content);
}

function fallbackRawContent(message: ChatMessage): string {
    const parts = [message.body.trim()];
    for (const attachment of message.attachments ?? []) {
        parts.push(`[${attachment.name}](${attachment.sourceUrl})`);
    }
    return parts.filter(Boolean).join('\n');
}

function longestBacktickRun(value: string): number {
    return Math.max(0, ...[...value.matchAll(/`+/gu)].map((match) => match[0].length));
}

function escapeMentionName(value: string): string {
    return value.replace(/([\\*|])/gu, '\\$1');
}

function unescapeMentionName(value: string): string {
    return value.replace(/\\([\\*|])/gu, '$1');
}

function parseLegacyQuote(content: string): ParsedMessageQuote | undefined {
    const lines = content.split(/\r?\n/gu);
    const sender = /^>\s*([^：\r\n]+)：\s*$/u.exec(lines[0] ?? '')?.[1]?.trim();
    if (!sender) {
        return undefined;
    }

    const quoted: string[] = [];
    let index = 1;
    while (index < lines.length && /^>/u.test(lines[index] ?? '')) {
        quoted.push((lines[index] ?? '').replace(/^> ?/u, ''));
        index += 1;
    }
    if (quoted.length === 0) {
        return undefined;
    }
    while (lines[index] === '') {
        index += 1;
    }
    return {
        sender,
        body: quoted.join('\n').trim(),
        remainder: lines.slice(index).join('\n'),
    };
}
