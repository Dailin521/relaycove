import { isPreviewableImageName, resolveRealmMediaUrl } from '../api/realmMedia';
import type { MessageAttachment } from '../models/ui';
import { parseLeadingMessageQuote, type ParsedMessageQuote } from './messageQuote';

export interface MessagePresentation {
    body: string;
    attachments: MessageAttachment[];
    quote?: Omit<ParsedMessageQuote, 'remainder'>;
}

const markdownLink = /!?\[([^\]\r\n]{1,256})\]\(([^)\r\n]{1,4096})\)/gu;
export const MAX_MESSAGE_IMAGE_PREVIEWS = 4;
export const MAX_MESSAGE_ATTACHMENTS = 10;

export function presentMessageContent(content: string, realm: string): MessagePresentation {
    const parsedQuote = parseLeadingMessageQuote(content);
    const attachments: MessageAttachment[] = [];
    const seen = new Set<string>();
    let imageCount = 0;
    const body = (parsedQuote?.remainder ?? content).replace(markdownLink, (match, rawName: string, rawUrl: string) => {
        const sourceUrl = resolveRealmMediaUrl(realm, rawUrl.replace(/^<|>$/gu, ''), 'upload');
        if (!sourceUrl) {
            return match;
        }

        const name = normalizeAttachmentName(rawName);
        const pathName = decodeURIComponent(new URL(sourceUrl).pathname.split('/').at(-1) ?? '');
        const kind = isPreviewableImageName(name) || isPreviewableImageName(pathName) ? 'image' : 'file';
        if (!seen.has(sourceUrl) && attachments.length >= MAX_MESSAGE_ATTACHMENTS) {
            return match;
        }
        if (!seen.has(sourceUrl) && kind === 'image' && imageCount >= MAX_MESSAGE_IMAGE_PREVIEWS) {
            return match;
        }
        if (!seen.has(sourceUrl)) {
            seen.add(sourceUrl);
            attachments.push({ kind, name, sourceUrl });
            if (kind === 'image') {
                imageCount += 1;
            }
        }
        return '';
    }).replace(/[ \t]+\n/gu, '\n').replace(/\n{3,}/gu, '\n\n').trim();

    return parsedQuote
        ? {
            body,
            attachments,
            quote: {
                sender: parsedQuote.sender,
                body: parsedQuote.body,
                permalink: parsedQuote.permalink,
            },
        }
        : { body, attachments };
}

function normalizeAttachmentName(value: string): string {
    const normalized = value.replace(/\\([\\\[\]()])/gu, '$1').trim();
    return normalized.slice(0, 256) || '附件';
}
